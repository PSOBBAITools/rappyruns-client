(in-package :ephinea-ta-client)

;;; Pin Share relay: the client stands in for the Pin Share addon's
;;; network half. The addon (addons\Pin Share, a Lua script inside the
;;; game) cannot open sockets, so it talks to a resident relay through two
;;; files in its exchange\ folder, and the relay talks WebSocket to the
;;; relay server:
;;;
;;;   exchange\out.txt  addon -> relay. One command per line,
;;;                     <seq>\t<command>\t<args...>; seq only grows.
;;;   exchange\in.txt   relay -> addon. The whole pin/arrow list plus the
;;;                     connection status, rewritten on every change and at
;;;                     least once a second (the addon treats a stale
;;;                     "time" line as "relay not running").
;;;
;;; The client only ever touches those two files - never the game process -
;;; so this stays inside the read-only-access policy. The server owns the
;;; pin list (numbering, expiry, per-owner limits); the relay is a
;;; translator between the tab-separated file format and the server's JSON
;;; messages, plus reconnect handling.
;;;
;;; This file is the pure half (parsing, translation, rendering), covered
;;; by the SBCL tests. The thread that moves bytes lives in
;;; pinshare-win32.lisp.

(defparameter +pinshare-default-server+
  "wss://pin-share-server-production.up.railway.app")

(defstruct pinshare-relay
  (channel "")        ; the party's shared passphrase
  (session "")        ; fresh per relay start; the addon re-sends its name
                      ; and colors when it sees a new one
  (last-seq 0)        ; highest out.txt seq handled
  (name "")           ; the player's name, as told by the addon
  (color "")          ; own pin color RRGGBB, "" = not told yet
  (arrow-color nil)   ; own arrow color; "" = same as pins, NIL = not told yet
  (pins #())          ; the server's last state snapshot, parsed JSON
  (arrows #())
  (members #())
  (status "connecting") ; "connecting" / "connected" / "error" - the addon
                        ; keys its status line off these exact strings
  (status-message "")
  (addon-seen nil)    ; the addon has written a command to THIS session:
                      ; proof the running game loaded the script (a fresh
                      ; install needs Reload in the game's addon menu)
  (dirty t))          ; in.txt needs rewriting

(defun pinshare-clean (value)
  "VALUE as a string with control characters removed: tabs and newlines
would break the line/tab framing of both exchange files."
  (if (stringp value)
      (remove-if (lambda (char)
                   (let ((code (char-code char)))
                     (or (< code 32) (= code 127))))
                 value)
      ""))

;;; --- out.txt ----------------------------------------------------------

(defun split-on (char string)
  (loop :with start := 0
        :for position := (position char string :start start)
        :collect (subseq string start position)
        :while position
        :do (setf start (1+ position))))

(defun parse-pinshare-outbox (text)
  "out.txt TEXT -> list of (seq . fields), oldest first. FIELDS includes
the seq field itself, so the command is (second fields). The addon may
be mid-write: only lines already terminated by a newline count, and
lines without an integer seq and a command are skipped."
  (let ((last-newline (position #\Newline text :from-end t)))
    (when last-newline
      (loop :for line :in (split-on #\Newline (subseq text 0 last-newline))
            :for fields := (split-on #\Tab (string-right-trim '(#\Return) line))
            :for seq := (ignore-errors (parse-integer (first fields)))
            :when (and seq (rest fields))
              :collect (cons seq fields)))))

(defun parse-pinshare-number (string)
  "Decimal STRING (optional sign, fraction and exponent) -> double-float,
or NIL when it is anything else (the addon prints \"nan\" / \"inf\" for a
broken coordinate). Hand-rolled because the Lisp reader must never see
file contents another process wrote."
  (let ((position 0)
        (end (length string)))
    (labels ((peek () (and (< position end) (char string position)))
             (digits ()
               (let ((start position))
                 (loop :while (and (peek) (digit-char-p (peek)))
                       :do (incf position))
                 (subseq string start position)))
             (sign ()
               (case (peek)
                 (#\- (incf position) -1)
                 (#\+ (incf position) 1)
                 (t 1))))
      (let* ((mantissa-sign (sign))
             (whole (digits))
             (fraction (if (eql (peek) #\.)
                           (progn (incf position) (digits))
                           ""))
             (exponent (if (member (peek) '(#\e #\E))
                           (progn
                             (incf position)
                             (let* ((exponent-sign (sign))
                                    (exponent-digits (digits)))
                               (if (string= exponent-digits "")
                                   (return-from parse-pinshare-number nil)
                                   (* exponent-sign
                                      (parse-integer exponent-digits)))))
                           0)))
        (when (and (= position end)
                   (or (string/= whole "") (string/= fraction ""))
                   ;; Far outside any game coordinate; refusing keeps the
                   ;; exact rational below small.
                   (<= (length whole) 30) (<= (length fraction) 30)
                   (<= (abs exponent) 30))
          (coerce (* mantissa-sign
                     (/ (parse-integer (concatenate 'string whole fraction "0"))
                        (expt 10 (1+ (length fraction))))
                     (expt 10 exponent))
                  'double-float))))))

(defun parse-pinshare-integer (string)
  "Integer-valued decimal STRING -> integer, else NIL."
  (let ((number (parse-pinshare-number string)))
    (when number
      (multiple-value-bind (whole remainder) (round number)
        (and (zerop remainder) whole)))))

(defun pinshare-json (&rest plist)
  "PLIST of string key / value pairs -> the JSON text of one message."
  (let ((object (make-hash-table :test 'equal)))
    (loop :for (key value) :on plist :by #'cddr
          :do (setf (gethash key object) value))
    (jzon:stringify object)))

(defun pinshare-command-message (fields)
  "One out.txt command FIELDS (seq command args...) -> the JSON text to
send to the server, or NIL when the command is unknown or malformed.
name / color / arrow_color are not here: they change relay state too
\(PINSHARE-RELAY-CONSUME)."
  (let ((command (second fields))
        (args (cddr fields)))
    (flet ((numbers (strings keys)
             ;; (key value ...) plist, or :bad when any value fails to parse
             (loop :for string :in strings
                   :for key :in keys
                   :for number := (parse-pinshare-number string)
                   :unless number :do (return :bad)
                   :append (list key number)))
           (optional-integer (string key)
             (let ((value (and string (parse-pinshare-integer string))))
               (and value (list key value)))))
      (cond
        ;; add <floor> <x> <y> <z> <ttl> [<label> [<max> [<room>]]]
        ((and (string= command "add") (>= (length args) 5))
         (let ((floor (parse-pinshare-integer (first args)))
               (point (numbers (subseq args 1 4) '("x" "y" "z")))
               (ttl (parse-pinshare-integer (fifth args))))
           (when (and floor ttl (listp point))
             (apply #'pinshare-json
                    "t" "add" "floor" floor "ttl" ttl
                    "label" (or (sixth args) "")
                    (append point
                            (optional-integer (seventh args) "max")
                            (optional-integer (eighth args) "room"))))))
        ;; move <id> <x> <y> <z>
        ((and (string= command "move") (>= (length args) 4))
         (let ((id (parse-pinshare-integer (first args)))
               (point (numbers (subseq args 1 4) '("x" "y" "z"))))
           (when (and id (listp point))
             (apply #'pinshare-json "t" "move" "id" id point))))
        ;; arrow_add <floor> <x1> <y1> <z1> <x2> <y2> <z2> <ttl> <max>
        ;;           [<xm> <ym> <zm> [<room>]]
        ((and (string= command "arrow_add") (>= (length args) 9))
         (let ((floor (parse-pinshare-integer (first args)))
               (ends (numbers (subseq args 1 7)
                              '("x1" "y1" "z1" "x2" "y2" "z2")))
               (ttl (parse-pinshare-integer (eighth args)))
               (max (parse-pinshare-integer (ninth args)))
               (middle (if (>= (length args) 12)
                           (numbers (subseq args 9 12) '("xm" "ym" "zm"))
                           '())))
           (when (and floor ttl max (listp ends) (listp middle))
             (apply #'pinshare-json
                    "t" "arrow_add" "floor" floor "ttl" ttl "max" max
                    (append ends middle
                            (optional-integer (nth 12 args) "room"))))))
        ;; arrow_move <id> <x1> <y1> <z1> <x2> <y2> <z2> [<xm> <ym> <zm>]
        ((and (string= command "arrow_move") (>= (length args) 7))
         (let ((id (parse-pinshare-integer (first args)))
               (ends (numbers (subseq args 1 7)
                              '("x1" "y1" "z1" "x2" "y2" "z2")))
               (middle (if (>= (length args) 10)
                           (numbers (subseq args 7 10) '("xm" "ym" "zm"))
                           '())))
           (when (and id (listp ends) (listp middle))
             (apply #'pinshare-json "t" "arrow_move" "id" id
                    (append ends middle)))))
        ((and (member command '("remove" "arrow_remove") :test #'string=)
              args)
         (let ((id (parse-pinshare-integer (first args))))
           (when id
             (pinshare-json "t" command "id" id))))
        ((member command '("clear_mine" "clear_all") :test #'string=)
         (pinshare-json "t" command))))))

(defun pinshare-hello-messages (relay)
  "The messages that (re)introduce this player to the server: hello,
then the colors - the server keeps colors per name, so a new name needs
them again."
  (append
   (list (pinshare-json "t" "hello"
                        "channel" (pinshare-relay-channel relay)
                        "name" (pinshare-relay-name relay)))
   (when (string/= (pinshare-relay-color relay) "")
     (list (pinshare-json "t" "color" "color" (pinshare-relay-color relay))))
   (when (pinshare-relay-arrow-color relay)
     (list (pinshare-json "t" "arrow_color"
                          "color" (pinshare-relay-arrow-color relay))))))

(defun pinshare-relay-consume (relay lines connected)
  "Handle the out.txt LINES (from PARSE-PINSHARE-OUTBOX) newer than the
relay's last seq; returns the JSON messages to send, oldest first. While
not CONNECTED the identity commands still update the relay (they are
replayed by the next hello) and everything else is dropped on purpose: a
pin placed minutes ago must not pop up after a reconnect."
  (let ((messages '()))
    (loop :for (seq . fields) :in lines
          :when (> seq (pinshare-relay-last-seq relay))
            :do (setf (pinshare-relay-last-seq relay) seq
                      (pinshare-relay-addon-seen relay) t
                      (pinshare-relay-dirty relay) t)
                (let ((command (second fields))
                      (value (and (cddr fields)
                                  (pinshare-clean (third fields)))))
                  (cond
                    ((string= command "name")
                     (when value
                       (setf (pinshare-relay-name relay) value)
                       (when connected
                         (setf messages
                               (append messages
                                       (pinshare-hello-messages relay))))))
                    ((string= command "color")
                     (when value
                       (setf (pinshare-relay-color relay) value)
                       (when connected
                         (setf messages
                               (append messages
                                       (list (pinshare-json
                                              "t" "color" "color" value)))))))
                    ((string= command "arrow_color")
                     (when value
                       (setf (pinshare-relay-arrow-color relay) value)
                       (when connected
                         (setf messages
                               (append messages
                                       (list (pinshare-json
                                              "t" "arrow_color"
                                              "color" value)))))))
                    (connected
                     (let ((message (pinshare-command-message fields)))
                       (when message
                         (setf messages
                               (append messages (list message)))))))))
    messages))

(defun pinshare-relay-skip-backlog (relay lines)
  "Relay start: whatever already sits in out.txt is stale, so only the
identity commands are kept and the seq cursor jumps past everything."
  (pinshare-relay-consume relay lines nil)
  ;; Left over from before this session: says nothing about whether the
  ;; addon is alive now.
  (setf (pinshare-relay-addon-seen relay) nil)
  relay)

;;; --- server messages ----------------------------------------------------

(defun pinshare-relay-note-message (relay text)
  "Apply one server message TEXT. A state snapshot replaces the pin,
arrow and member lists; returns the message string of a server-side
error (for the log), else NIL."
  (let ((message (ignore-errors (jzon:parse text))))
    (when (hash-table-p message)
      (let ((type (gethash "t" message)))
        (cond
          ((equal type "state")
           (flet ((items (key)
                    (let ((value (gethash key message)))
                      (if (vectorp value) value #()))))
             (setf (pinshare-relay-pins relay) (items "pins")
                   ;; absent from a pre-arrow server
                   (pinshare-relay-arrows relay) (items "arrows")
                   (pinshare-relay-members relay) (items "members")
                   (pinshare-relay-dirty relay) t))
           nil)
          ((equal type "error")
           (pinshare-clean (gethash "message" message))))))))

(defun pinshare-relay-clear-state (relay)
  "Forget the server's lists (disconnect): the addon must not keep
drawing pins nobody can confirm any more."
  (setf (pinshare-relay-pins relay) #()
        (pinshare-relay-arrows relay) #()
        (pinshare-relay-members relay) #()
        (pinshare-relay-dirty relay) t))

(defun pinshare-relay-set-status (relay status message)
  "Returns true when the status actually changed."
  (unless (and (string= status (pinshare-relay-status relay))
               (string= message (pinshare-relay-status-message relay)))
    (setf (pinshare-relay-status relay) status
          (pinshare-relay-status-message relay) message
          (pinshare-relay-dirty relay) t)
    t))

;;; --- in.txt ---------------------------------------------------------------

(defun pinshare-format-number (number)
  "NUMBER as text Lua's tonumber reads back: plain decimal, an \"e\"
exponent at most - never Lisp's d0 / f0 float markers."
  (if (integerp number)
      (format nil "~d" number)
      (let ((*read-default-float-format* 'double-float))
        (substitute #\e #\d
                    (string-downcase
                     (prin1-to-string (coerce number 'double-float)))))))

(defun pinshare-field (value)
  "One optional in.txt field: a JSON null or a missing key is empty."
  (cond ((integerp value) (format nil "~d" value))
        ((realp value) (pinshare-format-number value))
        ((stringp value) (pinshare-clean value))
        (t "")))

(defun pinshare-join (fields)
  "FIELDS printed and joined by tabs: one in.txt line, sans newline."
  (with-output-to-string (out)
    (loop :for (field . more) :on fields
          :do (princ field out)
              (when more (write-char #\Tab out)))))

(defun pinshare-item-line (kind item number-keys tail-keys
                           &optional extra-keys)
  "One \"pin\" / \"arrow\" line for the hash table ITEM, or NIL when an
id, floor or coordinate is not a number (the addon would drop the line
anyway). EXTRA-KEYS is the arrow's trailing group - written only when
its first value is present, because the addon reads those columns by
position."
  (let ((id (gethash "id" item))
        (floor (gethash "floor" item))
        (numbers (loop :for key :in number-keys
                       :collect (gethash key item))))
    (when (and (integerp id) (integerp floor) (every #'realp numbers))
      (pinshare-join
       (append
        (list kind id (pinshare-clean (gethash "owner" item)) floor)
        (mapcar #'pinshare-format-number numbers)
        (loop :for key :in tail-keys
              :collect (pinshare-field (gethash key item)))
        (when (and extra-keys
                   (realp (gethash (first extra-keys) item)))
          (loop :for key :in extra-keys
                :collect (pinshare-field (gethash key item)))))))))

(defun render-pinshare-inbox (relay unix-time)
  "The full text of in.txt for RELAY's current state."
  (with-output-to-string (out)
    (flet ((line (&rest fields)
             (write-string (pinshare-join fields) out)
             (terpri out)))
      (line "session" (pinshare-relay-session relay))
      (line "time" unix-time)
      (line "ack" (pinshare-relay-last-seq relay))
      (line "status" (pinshare-relay-status relay)
            (pinshare-clean (pinshare-relay-status-message relay)))
      (line "channel" (pinshare-clean (pinshare-relay-channel relay)))
      (loop :for member :across (pinshare-relay-members relay)
            :when (stringp member)
              :do (line "member" (pinshare-clean member)))
      (loop :for pin :across (pinshare-relay-pins relay)
            :for text := (and (hash-table-p pin)
                              (pinshare-item-line
                               "pin" pin '("x" "y" "z")
                               '("remaining" "label" "no" "ownerNo"
                                 "color" "roomNo" "room")))
            :when text :do (line text))
      (loop :for arrow :across (pinshare-relay-arrows relay)
            :for text := (and (hash-table-p arrow)
                              (pinshare-item-line
                               "arrow" arrow
                               '("x1" "y1" "z1" "x2" "y2" "z2")
                               '("remaining" "color")
                               '("xm" "ym" "zm" "room")))
            :when text :do (line text))
      (line "end"))))

(defun pinshare-inbox-heartbeat (text)
  "The (values session unix-time) another relay stamped into in.txt TEXT,
NILs when absent. Used once at relay start to notice the old PowerShell
relay still running: two relays would fight over both files."
  (let (session time)
    (dolist (line (split-on #\Newline text))
      (let ((fields (split-on #\Tab (string-right-trim '(#\Return) line))))
        (cond ((string= (first fields) "session")
               (setf session (second fields)))
              ((string= (first fields) "time")
               (setf time (and (second fields)
                               (parse-pinshare-integer (second fields))))))))
    (values session time)))

(defun pinshare-foreign-relay-p (text own-sessions unix-time)
  "True when in.txt TEXT carries a fresh heartbeat from a relay that is
not one of OWN-SESSIONS (every session id this process has used: a
passphrase change restarts the relay within the old heartbeat's
freshness window)."
  (multiple-value-bind (session time) (pinshare-inbox-heartbeat text)
    (and session time
         (not (member session own-sessions :test #'string=))
         (<= (abs (- unix-time time)) 3))))

;;; --- status for the GUI ---------------------------------------------------

(defvar *pinshare-status* '(:off)
  "What the relay is doing, as (keyword . args); written by the relay
thread, read by the GUI's status tick. The single variable swap of a
fresh list is atomic enough.")

(defun pinshare-status-text (status)
  "(values text error-p) for the Settings status line."
  (destructuring-bind (kind &rest args) status
    (ecase kind
      (:off (values (tr :pinshare-status-off) nil))
      (:no-channel (values (tr :pinshare-status-no-channel) nil))
      (:waiting-game (values (tr :pinshare-status-waiting-game) nil))
      (:connecting (values (tr :pinshare-status-connecting) nil))
      (:connected (values (tr :pinshare-status-connected
                              (first args) (second args))
                          nil))
      (:connected-no-addon (values (tr :pinshare-status-no-addon) nil))
      (:error (values (tr :pinshare-status-error (first args)) t))
      (:no-addon-plugin (values (tr :pinshare-status-no-plugin) t))
      (:install-failed (values (tr :pinshare-status-install-failed
                                   (first args))
                               t))
      (:broken-link (values (tr :pinshare-status-broken-link (first args))
                            t))
      (:conflict (values (tr :pinshare-status-conflict) t)))))

;;; --- settings / paths -----------------------------------------------------

(defvar *pinshare-game-exe* nil
  "Full path of the attached (signature-verified) PSOBB.exe, or NIL while
no game is attached. Written by the poll loop, read by the relay thread:
the addon's exchange folder is found next to it, and the relay only runs
while a game is there to draw pins.")

(defun pinshare-channel ()
  "The configured passphrase, cleaned the way the server will clean it
\(control characters out, trimmed, 64 characters)."
  (let ((channel (string-trim " " (pinshare-clean
                                   (config-value :pinshare-channel)))))
    (subseq channel 0 (min 64 (length channel)))))

(defun pinshare-server-url ()
  (let ((url (config-value :pinshare-server)))
    (if (and (stringp url) (string/= url ""))
        url
        +pinshare-default-server+)))

(defun pinshare-addon-dir (game-exe-path)
  "addons\\Pin Share\\ next to the game exe at GAME-EXE-PATH."
  (merge-pathnames
   (make-pathname :directory '(:relative "addons" "Pin Share"))
   (uiop:pathname-directory-pathname (pathname game-exe-path))))
