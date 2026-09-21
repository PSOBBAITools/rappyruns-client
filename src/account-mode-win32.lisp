(in-package :ephinea-ta-client)

;;; The Win32 half of account-mode detection: the process-to-peer
;;; mapping Windows keeps in its TCP table. See account-mode.lisp for
;;; why the peer's port band is what tells sandbox from normal.

(fli:register-module :iphlpapi :real-name "iphlpapi"
                     :connection-style :automatic)

;; GetExtendedTcpTable(pTcpTable, &dwSize, bOrder, ulAf, TableClass,
;; Reserved). dwSize is in/out, so it rides as a :reference: the first
;; call passes a null table and gets back the size it wants, the second
;; fills a buffer of that size.
(fli:define-foreign-function (%get-extended-tcp-table "GetExtendedTcpTable")
    ((table :pointer)
     (size (:reference (:unsigned :long)))
     (order (:boolean :int))
     (address-family (:unsigned :long))
     (table-class (:unsigned :int))
     (reserved (:unsigned :long)))
  :result-type (:unsigned :long)
  :calling-convention :stdcall
  :module :iphlpapi)

(defconstant +af-inet+ 2)

(defconstant +tcp-table-owner-pid-connections+ 4
  "TCP_TABLE_OWNER_PID_CONNECTIONS: every connection with its owning pid,
which is the only reason we walk this table at all. Not the _ALL class:
that one adds every listening socket on the machine, which can only push
a busy machine's table towards +MAX-TCP-TABLE-BYTES+ - and an unreadable
table now costs a sandbox run its board, not just a diagnostic.")

(defconstant +error-insufficient-buffer+ 122)

(defconstant +max-tcp-table-bytes+ (* 256 1024)
  "Refuse to snapshot a table larger than this: a machine with an absurd
number of open sockets loses the reading rather than have a diagnostic
allocate without bound. 256 KB is ~10k connections.")

(defconstant +tcp-table-slack-bytes+ (* 16 +tcp-row-bytes+)
  "Headroom over the size the sizing call asked for. Any process on the
machine may open a socket between that call and the one that fills the
buffer, and an exact-size buffer would then fail on a busy machine.")

(defun tcp-table-bytes ()
  "A MIB_TCPTABLE_OWNER_PID snapshot as bytes, or NIL. The sizing call
passes a null table and gets back the size it wants; the table can still
outgrow the headroom before the filling call, which then answers
ERROR_INSUFFICIENT_BUFFER with the new size - so that is retried, a
couple of times, before the reading is given up.

The buffer is heap-allocated on purpose. It can reach hundreds of KB on
a machine with thousands of sockets, and this runs on the poll thread: a
stack (dynamic-extent) buffer that size could overflow it, and a stack
overflow is a STORAGE-CONDITION, not an ERROR - the IGNORE-ERRORS around
the caller would not catch it and the poll loop would die."
  (let ((size (nth-value 1 (%get-extended-tcp-table
                            fli:*null-pointer* 0 nil +af-inet+
                            +tcp-table-owner-pid-connections+ 0))))
    (loop :repeat 3
          :while (<= 1 size +max-tcp-table-bytes+)
          :do (let* ((capacity (+ size +tcp-table-slack-bytes+))
                     (buffer (fli:allocate-foreign-object
                              :type '(:unsigned :byte) :nelems capacity)))
                (unwind-protect
                     (multiple-value-bind (result wanted)
                         (%get-extended-tcp-table buffer capacity nil +af-inet+
                                                  +tcp-table-owner-pid-connections+ 0)
                       (cond ((zerop result)
                              ;; The whole buffer is copied; the decoder
                              ;; goes by the table's own row count, so
                              ;; the unused tail is never read.
                              (let ((bytes (make-array
                                            capacity
                                            :element-type '(unsigned-byte 8))))
                                (fli:replace-foreign-array bytes buffer
                                                           :end2 capacity)
                                (return bytes)))
                             ((eql result +error-insufficient-buffer+)
                              (setf size wanted))
                             (t (return nil))))
                  (fli:free-foreign-object buffer))))))

(defmethod read-account-mode ((reader live-reader))
  "Judge the mode from the game process's TCP peers, log the reading and
keep it in *ACCOUNT-MODE-PROBE* for the capture diagnostics. Called per
quest load (DETECTOR-READ-ACCOUNT-MODE), never at poll-loop rate:
walking the machine's TCP table is not free. Never signals: a reading
must not be able to cost anyone a recording.

A reading without a verdict is retried, so it is logged only when it
says something new:
the recording log is capped and its tail is what a capture diagnostics
report is cut from, and a game that never yields a verdict must not
push the recording evidence out of it one identical line at a time."
  (ignore-errors
    (let* ((pid (live-reader-pid reader))
           (ports (tcp-peer-ports-for-pid (tcp-table-bytes) pid))
           (mode (account-mode-from-ports ports))
           (line (account-mode-probe-line :pid pid :ports ports :mode mode)))
      (when (or mode (not (equal line *account-mode-probe*)))
        (win32-log "~a" line))
      (setf *account-mode-probe* line)
      mode)))
