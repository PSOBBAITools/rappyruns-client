;;;; Converts client/src/i18n.lisp (*STRINGS*, CL FORMAT control strings)
;;;; into the desktop client's JSON string table, plus a golden file of
;;;; FORMAT outputs the C# formatter must reproduce.
;;;;
;;;; Run from the repo root:
;;;;   sbcl --script desktop/tools/export-i18n.lisp
;;;;
;;;; Template syntax written to strings.json (see RappyRuns.Core/I18n):
;;;;   {n}          argument n (FORMAT ~a / ~d)
;;;;   {n?text}     text only when argument n is non-null (~@[text~])
;;;;   {n#one|many} "one" when argument n is 1, else "many" (~:p, ~:@p)
;;;;   newline      ~%

(defpackage :ephinea-ta-client (:use :cl))
(in-package :ephinea-ta-client)

(load (merge-pathnames "client/src/i18n.lisp" *default-pathname-defaults*))

(defun convert (control)
  "CL FORMAT control string -> template. Handles exactly the directives
the table uses; anything else is an error so a new directive can't slip
through unconverted."
  (let ((out (make-string-output-stream))
        (arg 0)
        (i 0)
        (n (length control)))
    (labels ((peek (k) (and (< (+ i k) n) (char control (+ i k)))))
      (loop while (< i n) do
        (let ((c (char control i)))
          (cond
            ((char/= c #\~)
             (write-char c out) (incf i))
            ((member (peek 1) '(#\a #\A #\d #\D))
             (format out "{~d}" arg) (incf arg) (incf i 2))
            ((eql (peek 1) #\%)
             (write-char #\Newline out) (incf i 2))
            ((and (eql (peek 1) #\:) (member (peek 2) '(#\p #\P)))
             (format out "{~d#|s}" (1- arg)) (incf i 3))
            ((and (eql (peek 1) #\:) (eql (peek 2) #\@) (member (peek 3) '(#\p #\P)))
             (format out "{~d#y|ies}" (1- arg)) (incf i 4))
            ((and (eql (peek 1) #\@) (eql (peek 2) #\[))
             ;; ~@[ tests the next argument without consuming it; the
             ;; body then consumes it. Either way one argument is used.
             (let ((end (search "~]" control :start2 i)))
               (unless end (error "Unclosed ~~@[ in ~s" control))
               (let ((body (convert-body (subseq control (+ i 3) end) arg)))
                 (format out "{~d?~a}" arg body))
               (incf arg)
               (setf i (+ end 2))))
            (t (error "Unsupported directive at ~d in ~s" i control)))))
      (get-output-stream-string out))))

(defun convert-body (body first-arg)
  "Converts a ~@[...~] body whose argument numbering starts at FIRST-ARG."
  (let ((s (convert body)))
    ;; CONVERT numbers from 0; shift every {k...} by FIRST-ARG.
    (with-output-to-string (out)
      (loop with i = 0
            while (< i (length s))
            do (let ((c (char s i)))
                 (if (char= c #\{)
                     (multiple-value-bind (k end) (parse-integer s :start (1+ i) :junk-allowed t)
                       (format out "{~d" (+ k first-arg))
                       (setf i end))
                     (progn (write-char c out) (incf i))))))))

(defun json-string (s out)
  (write-char #\" out)
  (loop for c across s
        for code = (char-code c) do
    (cond ((= code 34) (write-string "\\\"" out))   ; "
          ((= code 92) (write-string "\\\\" out))  ; backslash
          ((= code 10) (write-string "\\n" out))
          ((< code 32) (format out "\\u~4,'0x" code))
          (t (write-char c out))))
  (write-char #\" out))

(defun key-name (key) (string-downcase (symbol-name key)))

(defun sample-args (control variant)
  "Arguments to exercise CONTROL. VARIANT 0: all present, integers where
~d is used; VARIANT 1: 1s (singular plurals) and NIL for ~@[ args."
  (let ((args '()) (i 0) (n (length control)))
    (loop while (< i n) do
      (let ((c (char control i)))
        (cond ((char/= c #\~) (incf i))
              ((member (char control (1+ i)) '(#\a #\A))
               (push (format nil "A~d" (length args)) args) (incf i 2))
              ((member (char control (1+ i)) '(#\d #\D))
               (push (if (= variant 0) (+ 2 (length args)) 1) args) (incf i 2))
              ((and (char= (char control (1+ i)) #\@) (char= (char control (+ i 2)) #\[))
               (push (if (= variant 0) (+ 2 (length args)) nil) args)
               (setf i (+ (search "~]" control :start2 i) 2)))
              (t (incf i 2)))))
    (nreverse args)))

(defun arg-json (a out)
  (cond ((null a) (write-string "null" out))
        ((integerp a) (format out "~d" a))
        (t (json-string a out))))

(let ((strings-path (merge-pathnames "desktop/src/RappyRuns.Core/I18n/strings.json" *default-pathname-defaults*))
      (golden-path (merge-pathnames "desktop/tests/RappyRuns.Tests/golden/i18n-format.json" *default-pathname-defaults*)))
  (ensure-directories-exist strings-path)
  (ensure-directories-exist golden-path)
  (with-open-file (out strings-path :direction :output :if-exists :supersede :external-format :utf-8)
    (write-line "{" out)
    (loop for (key (en ja)) on *strings* by #'cddr
          for first = t then nil
          do (unless first (write-line "," out))
             (write-string "  " out)
             (json-string (key-name key) out)
             (write-string ": { \"en\": " out)
             (json-string (convert en) out)
             (write-string ", \"ja\": " out)
             (json-string (convert ja) out)
             (write-string " }" out))
    (terpri out)
    (write-line "}" out))
  (with-open-file (out golden-path :direction :output :if-exists :supersede :external-format :utf-8)
    (write-line "[" out)
    (let ((first t))
      (loop for (key entry) on *strings* by #'cddr do
        (loop for lang in '(:en :ja)
              for control in entry do
                (loop for variant in '(0 1) do
                  (let ((args (sample-args control variant)))
                    (unless first (write-line "," out))
                    (setf first nil)
                    (write-string "  { \"key\": " out)
                    (json-string (key-name key) out)
                    (format out ", \"lang\": \"~(~a~)\", \"args\": [" lang)
                    (loop for a in args for j from 0
                          do (when (> j 0) (write-string ", " out))
                             (arg-json a out))
                    (write-string "], \"expected\": " out)
                    (json-string (apply #'format nil control args) out)
                    (write-string " }" out))))))
    (terpri out)
    (write-line "]" out))
  (format t "Wrote ~a (~d keys) and ~a~%" strings-path (/ (length *strings*) 2) golden-path))
