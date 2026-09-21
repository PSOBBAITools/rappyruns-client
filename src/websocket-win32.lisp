(in-package :ephinea-ta-client)

;;; LispWorks-only: a WebSocket client over the WinHTTP WebSocket API
;;; (Windows 8+). Same reasoning as winhttp.lisp: TLS comes from the OS,
;;; so wss:// works with no OpenSSL DLLs next to the exe. Used by the Pin
;;; Share relay (pinshare.lisp) to talk to the relay server.
;;;
;;; The session runs in WinHTTP's synchronous mode. One thread may block
;;; in WEBSOCKET-RECEIVE while another calls WEBSOCKET-SEND-TEXT - WinHTTP
;;; allows one pending receive plus one pending send per socket - and
;;; WEBSOCKET-CLOSE from any thread cancels a blocked receive (it returns
;;; :CLOSED), which is how the relay stops its reader. Pings are answered
;;; by WinHTTP itself, and its own keep-alive pings (30 s by default)
;;; turn a dead connection into a receive failure, so the receive timeout
;;; is left infinite.
;;;
;;; winhttp.lisp (loaded earlier) registers the :winhttp module and owns
;;; the session/connect/request bindings reused here.

(fli:define-foreign-function (%win-http-set-option "WinHttpSetOption")
    ((handle :pointer)
     (option (:unsigned :long))
     (buffer :pointer)
     (buffer-length (:unsigned :long)))
  :result-type (:boolean :int)
  :calling-convention :stdcall
  :module :winhttp)

(fli:define-foreign-function (%win-http-web-socket-complete-upgrade
                              "WinHttpWebSocketCompleteUpgrade")
    ((request :pointer)
     (context :size-t))
  :result-type :pointer
  :calling-convention :stdcall
  :module :winhttp)

;; The three calls below return a Windows error code (0 = success), not
;; a BOOL - GetLastError is not involved.
(fli:define-foreign-function (%win-http-web-socket-send "WinHttpWebSocketSend")
    ((socket :pointer)
     (buffer-type :int)
     (buffer :pointer)
     (buffer-length (:unsigned :long)))
  :result-type (:unsigned :long)
  :calling-convention :stdcall
  :module :winhttp)

(fli:define-foreign-function (%win-http-web-socket-receive
                              "WinHttpWebSocketReceive")
    ((socket :pointer)
     (buffer :pointer)
     (buffer-length (:unsigned :long))
     (bytes-read (:reference-return (:unsigned :long)))
     (buffer-type (:reference-return :int)))
  :result-type (:unsigned :long)
  :calling-convention :stdcall
  :module :winhttp)

;; Shutdown, not WinHttpWebSocketClose: Close waits for the peer's close
;; frame, which a receive blocked on another thread would swallow.
;; Shutdown only sends ours and returns.
(fli:define-foreign-function (%win-http-web-socket-shutdown
                              "WinHttpWebSocketShutdown")
    ((socket :pointer)
     (status (:unsigned :short))
     (reason :pointer)
     (reason-length (:unsigned :long)))
  :result-type (:unsigned :long)
  :calling-convention :stdcall
  :module :winhttp)

(defconstant +winhttp-option-upgrade-to-web-socket+ 114)
(defconstant +winhttp-web-socket-utf8-message+ 2)
(defconstant +winhttp-web-socket-utf8-fragment+ 3)
(defconstant +winhttp-web-socket-close+ 4)
(defconstant +winhttp-web-socket-status-normal+ 1000)

(defparameter +websocket-receive-chunk+ 65536)

(defparameter +websocket-max-message+ (* 4 1024 1024)
  "Upper bound on one reassembled message. The relay's state snapshots
are a few KB; anything near this is a broken or hostile peer, and the
connection is dropped rather than buffered without limit.")

(defstruct websocket
  session connection handle
  (lock (mp:make-lock :name "websocket-handles")))

(defun websocket-release (websocket)
  "Close every WinHTTP handle WEBSOCKET still owns, exactly once even
when the reader and the relay thread race here."
  (let (handles)
    (mp:with-lock ((websocket-lock websocket))
      (setf handles (list (websocket-handle websocket)
                          (websocket-connection websocket)
                          (websocket-session websocket))
            (websocket-handle websocket) nil
            (websocket-connection websocket) nil
            (websocket-session websocket) nil))
    (dolist (handle handles)
      (when handle
        (%win-http-close-handle handle)))))

(defun websocket-connect (url)
  "Open URL (ws:// or wss://) and return the connected WEBSOCKET.
Signals WINHTTP-ERROR when the transport or the upgrade fails."
  (multiple-value-bind (secure host port path) (parse-websocket-url url)
    (let ((websocket (make-websocket))
          (request nil))
      (flet ((check (pointer function-name)
               (when (fli:null-pointer-p pointer)
                 (winhttp-fail function-name))
               pointer))
        (handler-bind ((error (lambda (condition)
                                (declare (ignore condition))
                                (when request
                                  (%win-http-close-handle request))
                                (websocket-release websocket))))
          (setf (websocket-session websocket)
                (check (open-winhttp-session) "WinHttpOpen"))
          ;; Resolve/connect/send bounded; receive infinite (0) - an idle
          ;; channel is silent for as long as nobody touches a pin.
          (%win-http-set-timeouts (websocket-session websocket)
                                  10000 10000 10000 0)
          (setf (websocket-connection websocket)
                (check (%win-http-connect (websocket-session websocket)
                                          host port 0)
                       "WinHttpConnect"))
          (setf request
                (check (%win-http-open-request
                        (websocket-connection websocket) "GET" path
                        fli:*null-pointer* fli:*null-pointer*
                        fli:*null-pointer*
                        (if secure +winhttp-flag-secure+ 0))
                       "WinHttpOpenRequest"))
          (unless (%win-http-set-option request
                                        +winhttp-option-upgrade-to-web-socket+
                                        fli:*null-pointer* 0)
            (winhttp-fail "WinHttpSetOption"))
          (send-winhttp-request request "" nil)
          (unless (%win-http-receive-response request fli:*null-pointer*)
            (winhttp-fail "WinHttpReceiveResponse"))
          (let ((status (read-winhttp-status request)))
            (unless (eql status 101)
              (error 'winhttp-error
                     :message (format nil "WebSocket upgrade refused (HTTP ~d)"
                                      status))))
          (setf (websocket-handle websocket)
                (check (%win-http-web-socket-complete-upgrade request 0)
                       "WinHttpWebSocketCompleteUpgrade"))
          ;; The socket handle outlives the request it was upgraded from.
          (%win-http-close-handle request)
          (setf request nil)
          websocket)))))

(defun websocket-send-text (websocket string)
  "Send STRING as one UTF-8 text message. Signals WINHTTP-ERROR when the
socket is closed or the send fails."
  (let ((handle (websocket-handle websocket))
        (octets (string-to-utf8 string)))
    (unless handle
      (error 'winhttp-error :message "WebSocket is closed"))
    (fli:with-dynamic-foreign-objects ()
      (let* ((length (length octets))
             (buffer (fli:allocate-dynamic-foreign-object
                      :type '(:unsigned :byte) :nelems (max 1 length))))
        (dotimes (i length)
          (setf (fli:dereference buffer :index i) (aref octets i)))
        (let ((code (%win-http-web-socket-send
                     handle +winhttp-web-socket-utf8-message+ buffer length)))
          (unless (zerop code)
            (error 'winhttp-error
                   :message (format nil "WinHttpWebSocketSend failed (Windows error ~d)"
                                    code))))))
    string))

(defun websocket-receive (websocket)
  "Block until one whole text message arrives and return it as a string.
Returns :CLOSED when the peer closed the socket, the connection died or
WEBSOCKET-CLOSE cancelled the wait. Binary messages are skipped."
  (let ((handle (websocket-handle websocket)))
    (if (null handle)
        :closed
        (let ((chunk (fli:allocate-foreign-object
                      :type '(:unsigned :byte)
                      :nelems +websocket-receive-chunk+))
              (message (make-array 4096 :element-type '(unsigned-byte 8)
                                        :adjustable t :fill-pointer 0)))
          (unwind-protect
               (loop
                 (multiple-value-bind (code bytes-read buffer-type)
                     (%win-http-web-socket-receive
                      handle chunk +websocket-receive-chunk+ 0 0)
                   (when (or (/= code 0)
                             (= buffer-type +winhttp-web-socket-close+))
                     (return :closed))
                   (when (> (+ (length message) bytes-read)
                            +websocket-max-message+)
                     (return :closed))
                   (dotimes (i bytes-read)
                     (vector-push-extend (fli:dereference chunk :index i)
                                         message))
                   (cond ((= buffer-type +winhttp-web-socket-utf8-message+)
                          (return (utf8-to-string
                                   (coerce message
                                           '(simple-array (unsigned-byte 8)
                                             (*))))))
                         ((= buffer-type +winhttp-web-socket-utf8-fragment+))
                         ;; Binary message or fragment: not ours; drop
                         ;; what was gathered and wait for the next one.
                         (t (setf (fill-pointer message) 0)))))
            (fli:free-foreign-object chunk))))))

(defun websocket-close (websocket)
  "Close WEBSOCKET from any thread; idempotent. A receive blocked on
another thread returns :CLOSED."
  (let ((handle (websocket-handle websocket)))
    (when handle
      ;; Best-effort close frame; closing the handle below is what
      ;; actually cancels a pending receive.
      (ignore-errors
        (%win-http-web-socket-shutdown handle
                                       +winhttp-web-socket-status-normal+
                                       fli:*null-pointer* 0))))
  (websocket-release websocket))
