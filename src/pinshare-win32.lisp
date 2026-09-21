(in-package :ephinea-ta-client)

;;; LispWorks-only: the Pin Share relay thread (see pinshare.lisp for the
;;; protocol and the pure half). One resident thread supervises; while
;;; Pin Share is on, a passphrase is set and a verified game is attached
;;; it runs a relay session: make sure the addon is in the game's addons
;;; folder, then shuttle between the addon's exchange files and the relay
;;; server until a setting changes or the game goes away.
;;;
;;; The session loop ticks every 50 ms and never blocks on the network:
;;; connecting and receiving happen on a connection thread that reports
;;; through a mailbox, so in.txt keeps its once-a-second heartbeat even
;;; while a connect attempt hangs (the addon calls a 5 s old heartbeat
;;; "relay not running").

(fli:define-foreign-function (%get-file-attributes "GetFileAttributesW")
    ((path (:reference-pass :ef-wc-string)))
  :result-type (:unsigned :long)
  :calling-convention :stdcall
  :module :kernel32)

(fli:define-foreign-function (%move-file-ex "MoveFileExW")
    ((from (:reference-pass :ef-wc-string))
     (to (:reference-pass :ef-wc-string))
     (flags (:unsigned :long)))
  :result-type (:boolean :int)
  :calling-convention :stdcall
  :module :kernel32)

(defconstant +invalid-file-attributes+ #xFFFFFFFF)
(defconstant +file-attribute-reparse-point+ #x400)
(defconstant +movefile-replace-existing+ 1)

(defparameter +pinshare-tick-seconds+ 0.05)
(defparameter +pinshare-max-backoff-seconds+ 30)

(defparameter +pinshare-stable-connection-seconds+ 30
  "A connection that lasted at least this long resets the reconnect
backoff when it drops; a shorter one counts as a failed attempt.")

(defvar *pinshare-process* nil)

(defvar *pinshare-sessions* '()
  "Every relay session id this process has stamped into an in.txt, so
the start-up conflict check never mistakes our own previous session for
a foreign relay.")

(defun pinshare-unix-time ()
  (- (get-universal-time) 2208988800)) ; 1900 -> 1970 epoch

(defun pinshare-seconds ()
  (/ (get-internal-real-time) internal-time-units-per-second))

(defun pinshare-wait (seconds)
  (mp:process-wait-with-timeout "pin share idle" seconds
                                (lambda () *stop-requested*)))

;;; --- files ------------------------------------------------------------------

(defun pinshare-read-text (path)
  "The text of PATH, \"\" when it is missing or momentarily locked by
the addon. Bytes that are not UTF-8 (a name in some other encoding)
fall back to one character per byte rather than losing the commands."
  (let ((octets (ignore-errors
                  (with-open-file (in path :element-type '(unsigned-byte 8))
                    (let ((buffer (make-array (file-length in)
                                              :element-type '(unsigned-byte 8))))
                      (subseq buffer 0 (read-sequence buffer in)))))))
    (cond ((null octets) "")
          ((ignore-errors (utf8-to-string octets)))
          (t (map 'string #'code-char octets)))))

(defun pinshare-write-inbox (relay in-path tmp-path)
  "Rewrite in.txt through a temp file + replace, so the addon never
reads a half-written list. Returns true on success; a failure (the addon
has the file open this instant) leaves the relay dirty for the next
tick."
  (let ((octets (string-to-utf8
                 (render-pinshare-inbox relay (pinshare-unix-time)))))
    (when (ignore-errors
            (with-open-file (out tmp-path :direction :output
                                          :if-exists :supersede
                                          :element-type '(unsigned-byte 8))
              (write-sequence octets out))
            (%move-file-ex (namestring tmp-path) (namestring in-path)
                           +movefile-replace-existing+))
      (setf (pinshare-relay-dirty relay) nil)
      t)))

;;; --- addon install ----------------------------------------------------------

(defun pinshare-bundled-addon-path ()
  "The addon shipped with this client: data/pin-share/init.lua next to
the exe (delivered image) or in the source tree (development), or NIL."
  ;; Through LW:LISP-IMAGE-NAME like updater.lisp, not the argv[0]-based
  ;; EXE-ADJACENT-PATH: a relative argv[0] plus another working directory
  ;; would leave a fresh user with "init.lua is missing".
  (or (ignore-errors
        (probe-file (merge-pathnames
                     "data/pin-share/init.lua"
                     (uiop:pathname-directory-pathname
                      (lw:lisp-image-name)))))
      (ignore-errors
        (probe-file (asdf:system-relative-pathname
                     :ephinea-ta-client "data/pin-share/init.lua")))))

(defun pinshare-reparse-point-p (directory)
  "True when DIRECTORY is a junction / symlink: a developer's
addons\\Pin Share linked to a working copy, which must never be
overwritten with the bundled file."
  (let ((attributes (%get-file-attributes
                     (string-right-trim "\\/" (namestring directory)))))
    (and (/= attributes +invalid-file-attributes+)
         (logtest attributes +file-attribute-reparse-point+))))

(defconstant +error-path-not-found+ 3)

(defun pinshare-dangling-link-p (directory)
  "True when DIRECTORY is a junction / symlink whose target is gone.
PROBE-FILE cannot tell (LispWorks answers for the link itself), so ask
for a name inside it: through a dead link that fails with
ERROR_PATH_NOT_FOUND, where a live folder says ERROR_FILE_NOT_FOUND."
  (and (pinshare-reparse-point-p directory)
       (= (%get-file-attributes
           (namestring (merge-pathnames "pinshare-link-probe" directory)))
          +invalid-file-attributes+)
       (= (%get-last-error) +error-path-not-found+)))

(defun pinshare-file-octets (path)
  (with-open-file (in path :element-type '(unsigned-byte 8))
    (let ((buffer (make-array (file-length in)
                              :element-type '(unsigned-byte 8))))
      (read-sequence buffer in)
      buffer)))

(defun pinshare-ensure-addon (addon-dir)
  "Install or update the addon under ADDON-DIR and make sure its
exchange folder exists. Only init.lua is ever written: options.lua (the
player's key bindings) and anything else in the folder stay untouched.
Returns NIL when the addon is ready, else the status list to show."
  (let ((plugin (merge-pathnames
                 "init.lua"
                 (uiop:pathname-parent-directory-pathname addon-dir)))
        (installed (merge-pathnames "init.lua" addon-dir))
        (bundled (pinshare-bundled-addon-path)))
    (cond
      ;; No Solybum addon plugin: the script would never be loaded.
      ((not (probe-file plugin)) '(:no-addon-plugin))
      ;; A developer link whose target moved away (field-observed: the
      ;; working copy was renamed). Nothing can be created through it,
      ;; and it is the user's link to fix - never ours to delete.
      ((pinshare-dangling-link-p addon-dir)
       (list :broken-link
             (string-right-trim "\\/" (namestring addon-dir))))
      (t
       (handler-case
           (progn
             (when (and bundled
                        (not (pinshare-reparse-point-p addon-dir)))
               (let ((wanted (pinshare-file-octets bundled)))
                 (unless (and (probe-file installed)
                              (equalp wanted (pinshare-file-octets installed)))
                   (ensure-directories-exist installed)
                   (with-open-file (out installed
                                        :direction :output
                                        :if-exists :supersede
                                        :element-type '(unsigned-byte 8))
                     (write-sequence wanted out))
                   (win32-log "pin share: addon installed at ~a" installed))))
             (ensure-directories-exist
              (merge-pathnames "exchange/placeholder" addon-dir))
             (if (probe-file installed)
                 nil
                 '(:install-failed "init.lua is missing")))
         (error (condition)
           (list :install-failed (princ-to-string condition))))))))

;;; --- connection thread ------------------------------------------------------

(defstruct pinshare-link
  "Shared between a relay session and its connection threads so a
session can end while a connect is still in flight without leaking the
socket that connect is about to produce."
  (lock (mp:make-lock :name "pin share link"))
  (socket nil)
  (cancelled nil))

(defun pinshare-link-cancel (link)
  (let (socket)
    (mp:with-lock ((pinshare-link-lock link))
      (setf (pinshare-link-cancelled link) t
            socket (pinshare-link-socket link)
            (pinshare-link-socket link) nil))
    (when socket
      (websocket-close socket))))

(defun pinshare-start-connection (url link mailbox)
  "Connect on a fresh thread and feed MAILBOX: exactly one (:open socket)
or (:failed text), then (:message text) per server message, then one
\(:closed)."
  (mp:process-run-function
   "eta-client-pinshare-connection" '()
   (lambda ()
     (let ((socket (handler-case (websocket-connect url)
                     (error (condition)
                       (mp:mailbox-send
                        mailbox (list :failed (princ-to-string condition)))
                       nil))))
       (when socket
         (if (mp:with-lock ((pinshare-link-lock link))
               (unless (pinshare-link-cancelled link)
                 (setf (pinshare-link-socket link) socket)))
             (progn
               (mp:mailbox-send mailbox (list :open socket))
               ;; :closed goes out however the loop ends: without it the
               ;; session would sit on a dead socket calling itself
               ;; connected, and never reconnect.
               ;; IGNORE-ERRORS rather than UNWIND-PROTECT: an unhandled
               ;; error in a thread does not unwind on its own.
               (ignore-errors
                 (loop :for message := (websocket-receive socket)
                       :until (eq message :closed)
                       :do (mp:mailbox-send mailbox
                                            (list :message message))))
               (mp:mailbox-send mailbox (list :closed)))
             ;; The session ended while we were connecting.
             (websocket-close socket)))))))

;;; --- relay session ----------------------------------------------------------

(defun pinshare-wanted ()
  "(values wanted status): WANTED is the (game-exe channel server-url)
the relay should be serving right now, or NIL with the STATUS list that
says why it is idle."
  (let ((channel (pinshare-channel))
        (exe *pinshare-game-exe*))
    (cond ((not (config-value :pinshare-enabled)) (values nil '(:off)))
          ;; Staged rollout: the server decides who may use the relay.
          ((not *pinshare-allowed-p*) (values nil '(:not-allowed)))
          ((string= channel "") (values nil '(:no-channel)))
          ((null exe) (values nil '(:waiting-game)))
          (t (values (list exe channel (pinshare-server-url)) nil)))))

(defun pinshare-session-current-p (wanted)
  (and (not *stop-requested*)
       (equal wanted (pinshare-wanted))))

(defun pinshare-relay-loop (relay wanted out-path in-path tmp-path)
  (let ((url (third wanted))
        (mailbox (mp:make-mailbox :name "pin share events"))
        (link (make-pinshare-link))
        (socket nil)
        (connecting nil)
        (retry-at 0)
        (backoff 1)
        (opened-at 0)
        (last-write 0))
    (labels ((status (status message gui)
               (when (pinshare-relay-set-status relay status message)
                 (win32-log "pin share: ~a ~a" status message))
               (setf *pinshare-status* gui))
             (connected-status ()
               (status "connected" ""
                       (if (pinshare-relay-addon-seen relay)
                           (list :connected (pinshare-relay-channel relay)
                                 (length (pinshare-relay-members relay)))
                           ;; Connected, yet the game has not loaded the
                           ;; script (fresh install: it needs a Reload).
                           '(:connected-no-addon))))
             (back-off ()
               (setf retry-at (+ (pinshare-seconds) backoff)
                     backoff (min (* 2 backoff)
                                  +pinshare-max-backoff-seconds+)))
             (send (messages)
               (handler-case
                   (dolist (message messages)
                     (websocket-send-text socket message))
                 ;; The reader thread sees the same failure and posts
                 ;; :closed, which does the state change.
                 (error () (websocket-close socket))))
             (handle (event)
               (ecase (first event)
                 (:open
                  (setf socket (second event)
                        connecting nil
                        opened-at (pinshare-seconds))
                  (pinshare-relay-clear-state relay)
                  (connected-status)
                  (send (pinshare-hello-messages relay)))
                 (:failed
                  (setf connecting nil)
                  (back-off)
                  (let ((text (format nil "connect failed: ~a" (second event))))
                    (status "error" text (list :error text))))
                 (:message
                  (let ((server-error (pinshare-relay-note-message
                                       relay (second event))))
                    (cond (server-error
                           ;; in.txt stays "connected" (it is); the
                           ;; Settings line shows the refusal until the
                           ;; next state snapshot, so a rejected command
                           ;; is not a silent no-op.
                           (win32-log "pin share: server says ~a"
                                      server-error)
                           (setf *pinshare-status*
                                 (list :error (format nil "server: ~a"
                                                      server-error))))
                          (socket (connected-status)))))
                 (:closed
                  (when socket
                    (websocket-close socket)
                    (setf socket nil))
                  (mp:with-lock ((pinshare-link-lock link))
                    (setf (pinshare-link-socket link) nil))
                  ;; Only a connection that held earns the quick retry. A
                  ;; server that accepts and drops (crash loop, a proxy
                  ;; that upgrades then resets) backs off like a failed
                  ;; connect - every resident client hitting it once a
                  ;; second would keep it down.
                  (if (>= (- (pinshare-seconds) opened-at)
                          +pinshare-stable-connection-seconds+)
                      (setf backoff 1
                            retry-at (+ (pinshare-seconds) 1))
                      (back-off))
                  (pinshare-relay-clear-state relay)
                  (status "error" "disconnected from the server"
                          '(:error "disconnected from the server"))))))
      (unwind-protect
           (loop :while (pinshare-session-current-p wanted)
                 :do (when (and (null socket) (not connecting)
                                (>= (pinshare-seconds) retry-at))
                       (setf connecting t)
                       (status "connecting" url '(:connecting))
                       (pinshare-start-connection url link mailbox))
                     ;; The timed read is the tick; a server message
                     ;; wakes it early.
                     (loop :for event := (mp:mailbox-read
                                          mailbox "pin share relay"
                                          +pinshare-tick-seconds+)
                             :then (and (not (mp:mailbox-empty-p mailbox))
                                        (mp:mailbox-read mailbox))
                           :while event
                           :do (handle event))
                     (let ((messages (pinshare-relay-consume
                                      relay
                                      (parse-pinshare-outbox
                                       (pinshare-read-text out-path))
                                      (and socket t))))
                       (when (and socket messages)
                         (send messages))
                       ;; The addon's first command to this session flips
                       ;; the "waiting for the addon" line.
                       (when (and socket
                                  (equal *pinshare-status*
                                         '(:connected-no-addon))
                                  (pinshare-relay-addon-seen relay))
                         (connected-status)))
                     (when (or (pinshare-relay-dirty relay)
                               (>= (- (pinshare-seconds) last-write) 1))
                       (when (pinshare-write-inbox relay in-path tmp-path)
                         (setf last-write (pinshare-seconds)))))
        (pinshare-link-cancel link)))))

(defun pinshare-run-session (wanted)
  (let* ((addon-dir (pinshare-addon-dir (first wanted)))
         (problem (pinshare-ensure-addon addon-dir)))
    (if problem
        (progn
          (setf *pinshare-status* problem)
          (pinshare-wait 5))
        (let ((out-path (merge-pathnames "exchange/out.txt" addon-dir))
              (in-path (merge-pathnames "exchange/in.txt" addon-dir))
              (tmp-path (merge-pathnames "exchange/in.txt.tmp" addon-dir))
              (relay (make-pinshare-relay
                      :channel (second wanted)
                      ;; Seeded per call: a delivered image starts from
                      ;; the same *RANDOM-STATE* every launch, and the
                      ;; addon only re-sends its name to a NEW session.
                      :session (format nil "~(~8,'0x~)"
                                       (random (expt 16 8)
                                               (make-random-state t))))))
          ;; The old PowerShell relay still running would fight us over
          ;; both files; stand aside until its heartbeat goes stale.
          (loop :while (and (pinshare-session-current-p wanted)
                            (pinshare-foreign-relay-p
                             (pinshare-read-text in-path)
                             *pinshare-sessions* (pinshare-unix-time)))
                :do (setf *pinshare-status* '(:conflict))
                    (pinshare-wait 2))
          (when (pinshare-session-current-p wanted)
            (push (pinshare-relay-session relay) *pinshare-sessions*)
            (pinshare-relay-skip-backlog
             relay (parse-pinshare-outbox (pinshare-read-text out-path)))
            (pinshare-relay-loop relay wanted out-path in-path tmp-path))))))

(defun pinshare-loop ()
  (loop :until *stop-requested*
        :do (multiple-value-bind (wanted status) (pinshare-wanted)
              (if wanted
                  (handler-case (pinshare-run-session wanted)
                    (error (condition)
                      (win32-log "pin share relay failed: ~a" condition)
                      (setf *pinshare-status*
                            (list :error (princ-to-string condition)))
                      (pinshare-wait 5)))
                  (progn
                    (setf *pinshare-status* status)
                    (pinshare-wait 1))))))

(defparameter +pinshare-permission-interval-seconds+ 1800
  "How often a linked client re-asks /api/me whether it is in the Pin
Share rollout. The client is resident for days, and CHECK-TOKEN only
runs at startup and on Save: without this, widening the rollout - or
pulling the feature - would wait for everyone's next restart.")

(defun pinshare-permission-loop ()
  "Moves the rollout flag and nothing else. No window rebuild from here:
the client usually sits in the tray while the game has the foreground,
and a rebuild could also land on an open dialog. The relay obeys the
flag within a tick; the Settings group catches up at the next moment a
rebuild is safe (launch, Save - APPLY-ACCOUNT-GATES). The moderator role
is CHECK-TOKEN's business and is left alone."
  (loop
    (pinshare-wait +pinshare-permission-interval-seconds+)
    (when *stop-requested* (return))
    (let ((token (normalize-token (config-value :api-token))))
      (when (string/= token "")
        ;; Transport errors keep the cached verdict: an unreachable site
        ;; must not switch a working relay off.
        (ignore-errors
          (multiple-value-bind (outcome user) (fetch-me :token token)
            ;; The account may have changed while the request was out
            ;; (Save with another token): a stale answer must not
            ;; overwrite the verdict CHECK-TOKEN just stored for it.
            (when (string= token (normalize-token (config-value :api-token)))
              (case outcome
                (:ok (set-pinshare-permission (pinshare-feature-p user)))
                (:unauthorized (set-pinshare-permission nil))))))))))

(defun start-pinshare! ()
  "Start the resident relay supervisor (idle until Pin Share is on) and
the rollout-permission refresher."
  (mp:process-run-function "eta-client-pinshare-permission" '()
                           'pinshare-permission-loop)
  (setf *pinshare-process*
        (mp:process-run-function "eta-client-pinshare" '() 'pinshare-loop)))
