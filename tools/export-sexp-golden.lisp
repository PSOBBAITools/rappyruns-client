;;;; Golden for RappyRuns.Core.Sexp: how the Lisp client prints data
;;;; (config.lisp write-sexp-file: standard io syntax, *package* KEYWORD).
;;;; Run from the repo root: sbcl --script desktop/tools/export-sexp-golden.lisp

(defun lisp-print (datum)
  (with-standard-io-syntax
    (let ((*print-readably* nil)
          (*package* (find-package :keyword)))
      (prin1-to-string datum))))

(defun json-string (s out)
  (write-char #\" out)
  (loop for c across s
        for code = (char-code c) do
    (cond ((= code 34) (write-char (code-char 92) out) (write-char #\" out))
          ((= code 92) (write-char (code-char 92) out) (write-char (code-char 92) out))
          ((= code 10) (write-char (code-char 92) out) (write-char #\n out))
          (t (write-char c out))))
  (write-char #\" out))

(defparameter *floats*
  '(0.7894558 0.020304569 1.0 0.0 -2.5 1.3 100.0 12345.67 9999999.0 1.0e7 1.5e7
    0.001 0.00099 1.0e-5 3.4028235e38 1.17549435e-38 0.1 -0.125 123456.79))

(defparameter *doubles* '(1.5d0 0.1d0 1.0d7 2.5d-4 12345.6789d0))

(with-open-file (out "desktop/tests/RappyRuns.Tests/golden/sexp-print.json"
                     :direction :output :if-exists :supersede :external-format :utf-8)
  (format out "{~%  \"singles\": [")
  (loop for f in *floats* for i from 0
        do (format out "~:[~;, ~]{ \"bits\": ~d, \"text\": " (> i 0)
                   (ldb (byte 32 0) (sb-kernel:single-float-bits f)))
           (json-string (lisp-print f) out)
           (write-string " }" out))
  (format out "],~%  \"doubles\": [")
  (loop for d in *doubles* for i from 0
        do (format out "~:[~;, ~]{ \"value\": ~a, \"text\": " (> i 0)
                   (substitute #\e #\d (let ((*read-default-float-format* 'double-float))
                                         (prin1-to-string d))))
           (json-string (lisp-print d) out)
           (write-string " }" out))
  (format out "],~%  \"data\": [")
  (let ((data (list (list :server-url "https://x/" :api-token (format nil "a\"b~cc" (code-char 92))
                          :language :ja :debug nil :trigger-log t
                          :overlay-position (list 0.7894558 0.020304569) :record-max-total-gb 20)
                    (list (cons 5 1234) (cons "Resta" 2) (cons :monomate 1))
                    nil
                    (list :quest-name "ダーク・ファルス" :finished-at 3967948800 :neg -42))))
    (loop for d in data for i from 0
          do (format out "~:[~;, ~]" (> i 0))
             (json-string (lisp-print d) out)))
  (format out "]~%}~%"))

(format t "ok~%")
