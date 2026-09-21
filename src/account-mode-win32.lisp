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

(defconstant +tcp-table-owner-pid-all+ 5
  "TCP_TABLE_OWNER_PID_ALL: every connection with its owning pid, which
is the only reason we walk this table at all.")

(defconstant +max-tcp-table-bytes+ (* 256 1024)
  "Refuse to snapshot a table larger than this. The buffer below is
dynamic-extent (stack), and a machine with an absurd number of open
sockets must lose the probe rather than overflow the stack - a
STORAGE-CONDITION is not an ERROR, so the IGNORE-ERRORS around the
caller would not catch it. 256 KB is ~10k connections.")

(defun tcp-table-bytes ()
  "A MIB_TCPTABLE_OWNER_PID snapshot as bytes, or NIL. The table can
grow between the two calls - any process on the machine may open a
socket meanwhile - and then the second call fails; that costs this
probe, not the session, so it just returns NIL."
  (multiple-value-bind (result size)
      (%get-extended-tcp-table fli:*null-pointer* 0 nil +af-inet+
                               +tcp-table-owner-pid-all+ 0)
    ;; RESULT is ERROR_INSUFFICIENT_BUFFER here; SIZE is what we came for.
    (declare (ignore result))
    (when (<= 1 size +max-tcp-table-bytes+)
      (fli:with-dynamic-foreign-objects ()
        (let ((buffer (fli:allocate-dynamic-foreign-object
                       :type '(:unsigned :byte) :nelems size)))
          (multiple-value-bind (result written)
              (%get-extended-tcp-table buffer size nil +af-inet+
                                       +tcp-table-owner-pid-all+ 0)
            (declare (ignore written))
            (when (zerop result)
              (let ((bytes (make-array size :element-type '(unsigned-byte 8))))
                (fli:replace-foreign-array bytes buffer :end2 size)
                bytes))))))))

(defmethod read-account-mode ((reader live-reader))
  "Judge the mode from the game process's TCP peers, log the reading and
keep it in *ACCOUNT-MODE-PROBE* for the capture diagnostics. Called on
attach and once per quest load (ACCOUNT-MODE-FOR-QUEST), never at
poll-loop rate: walking the machine's whole TCP table is not free.
Never signals: a reading must not be able to cost anyone a recording."
  (ignore-errors
    (let* ((pid (live-reader-pid reader))
           (peers (tcp-peers-for-pid (tcp-table-bytes) pid))
           (mode (account-mode-from-peers peers))
           (line (account-mode-probe-line
                  :pid pid
                  :peers peers
                  :mode mode
                  :window-title (reader-window-title reader)
                  :image-path (ignore-errors (process-image-path reader)))))
      (setf *account-mode-probe* line)
      (win32-log "~a" line)
      mode)))
