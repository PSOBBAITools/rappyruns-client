(in-package :ephinea-ta-client)

;;; Account mode (sandbox vs normal): detection, and the evidence behind
;;; it.
;;;
;;; Ephinea's Sandbox is an account-level mode: chosen when the account
;;; is created, never changed afterwards, and kept apart from everyone
;;; else - a sandbox player "will be unable to play with other players
;;; outside of Sandbox Mode in this mode". Its characters level and
;;; spawn items freely (/levelup, /item, /srank, /redbox), so a sandbox
;;; time is not comparable with a normal-server one and the site ranks
;;; the two on separate boards (runs.account_mode server-side).
;;;
;;; The detection source is the port band of the game process's TCP
;;; peer, chosen from a normal-vs-sandbox measurement (2026-08-13):
;;;
;;;   normal    35.75.43.206:5279 / :5280, 34.223.124.214:5279
;;;   sandbox   34.223.124.214:14000 / :14001
;;;
;;; The ports are the ship (5278 / 14000) and its blocks (5279+ /
;;; 14001+). The hosts are the ships' machines and BOTH modes share
;;; them, so the address says nothing and none is hardcoded. Guild card
;;; numbers are no use either: both modes draw from one serial pool.
;;;
;;; One game process can change modes - a player logs out and into
;;; another account without restarting - so a reading taken at attach
;;; goes stale. The mode is read again for every quest load
;;; (ACCOUNT-MODE-FOR-QUEST) and stamped on that load's runs.
;;;
;;; The Win32 half (walking the system TCP table) lives in
;;; account-mode-win32.lisp; the decoding and the verdict stay here, off
;;; Windows, so the tests can reach them.

(defvar *account-mode-probe* nil
  "The newest reading as a line (ACCOUNT-MODE-PROBE-LINE), or NIL before
the first attach. It rides along in the capture diagnostics so a reading
reports itself without the player digging a log out of %TEMP% - and it
is what will notice if the sandbox port band ever moves.")

(defconstant +tcp-row-bytes+ 24
  "sizeof(MIB_TCPROW_OWNER_PID): state, local address and port, remote
address and port, owning pid - six DWORDs.")

(defconstant +tcp-state-established+ 5
  "MIB_TCP_STATE_ESTAB. The game holds one established connection to
its ship; listeners and closing sockets are noise here.")

(defconstant +loopback-first-octet+ 127
  "127.0.0.0/8. A peer on the player's own machine is a local proxy or
some tool's service, never a ship, so its port says nothing about the
mode - in either direction.")

(defconstant +sandbox-port-min+ 14000)
(defconstant +sandbox-port-max+ 14999
  "The sandbox ship sits on 14000 and its blocks count up from 14001;
the whole thousand is taken so a second sandbox block is still sandbox.")

(defun tcp-peer-ports-for-pid (bytes pid)
  "Remote ports of PID's established IPv4 connections in a
MIB_TCPTABLE_OWNER_PID snapshot, loopback peers left out. BYTES is the
raw table: a DWORD row count followed by +TCP-ROW-BYTES+ rows. The
address and port fields hold network byte order inside their DWORDs
while the rest of the struct is little-endian like everything else,
hence the byte-wise reads for those two and BYTES-U32 for the others. A
short or truncated table yields NIL rather than an error.

Only the port comes out. The address cannot tell the modes apart (they
share their hosts), and behind a VPN or a proxy it is the player's own
endpoint rather than a ship - and every reading is logged and uploaded
with the capture diagnostics. What the verdict does not need does not
leave this function."
  (when (and bytes (>= (length bytes) 4))
    (loop :with count := (bytes-u32 bytes 0)
          :for i :below count
          :for base := (+ 4 (* i +tcp-row-bytes+))
          :while (<= (+ base +tcp-row-bytes+) (length bytes))
          :when (and (eql pid (bytes-u32 bytes (+ base 20)))
                     (eql +tcp-state-established+ (bytes-u32 bytes base))
                     (/= +loopback-first-octet+ (aref bytes (+ base 12))))
            :collect (+ (* 256 (aref bytes (+ base 16)))
                        (aref bytes (+ base 17))))))

(defun account-mode-from-ports (ports)
  "The account mode PORTS (TCP-PEER-PORTS-FOR-PID) says the session is
in: \"sandbox\" when any sits in the sandbox port band, \"normal\" when
the game has peers and none does, NIL when there is nothing to judge by
(the table could not be read, the game is not connected, or its only
peer is a proxy on this machine). NIL means no verdict, never normal:
the caller omits the field and tries again.

\"normal\" is the weaker of the two verdicts. A game tunnelled through a
remote proxy shows that proxy's port, not the ship's, and reads as
normal whichever account it is. That costs nothing a missing verdict
would not: normal is what the server files a run as without one."
  (cond ((null ports) nil)
        ((some (lambda (port)
                 (<= +sandbox-port-min+ port +sandbox-port-max+))
               ports)
         "sandbox")
        (t "normal")))

(defun account-mode-probe-line (&key pid ports mode)
  "One reading as a log line: the verdict next to the ports it was made
from, so a wrong verdict is diagnosable from the line alone. Kept free
of Win32 so the tests can build one."
  (format nil "account-mode probe: pid ~a ports ~:[none~;~:*~{~d~^ ~}~] mode ~a"
          (or pid "?") ports (or mode "?")))

(defgeneric read-account-mode (reader)
  (:documentation
   "Read the account mode of the game READER is attached to, right now:
\"sandbox\", \"normal\" or NIL (no verdict). Never signals. Only a live
reader has a process to ask; everything else has no verdict.")
  (:method (reader)
    (declare (ignore reader))
    nil))

(defvar *account-mode-reading* nil
  "(QUEST-PTR MODE READ-AT) of the newest per-quest reading, or NIL
while no quest is loaded. See ACCOUNT-MODE-FOR-QUEST.")

(defun forget-account-mode-reading ()
  "No quest is loaded (or the game went away): the next load reads the
mode afresh. This is what makes an account change between quests land
on the right board."
  (setf *account-mode-reading* nil))

(defun account-mode-for-quest (quest-ptr read-mode
                               &optional (now (get-internal-real-time)))
  "The account mode for the quest load QUEST-PTR, calling READ-MODE (a
thunk) only when it has to. The mode cannot change while a quest stays
loaded, so one successful reading serves the whole load - walking the
machine's TCP table at poll rate would buy nothing. A reading that came
back NIL is retried, but at most once per second."
  (destructuring-bind (&optional ptr mode read-at) *account-mode-reading*
    (if (and (eql ptr quest-ptr)
             (or mode
                 (< (- now read-at) internal-time-units-per-second)))
        mode
        (let ((mode (funcall read-mode)))
          (setf *account-mode-reading* (list quest-ptr mode now))
          mode))))
