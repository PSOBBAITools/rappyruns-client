;;;; Downgrade check for RappyRuns.Core.Config (issue #322): the config.sexp
;;;; the C# client writes (tests/RappyRuns.Tests/golden/store/config-csharp.sexp,
;;;; pinned by StoreGoldenTests.TheCSharpWrittenConfigIsTheOneLispVerified)
;;;; must load in the Lisp client's LOAD-CONFIG! with the values C# set.
;;;; Keep EXPECTED in sync with StoreGoldenTests.BuildCSharpConfig.
;;;;
;;;; Run from the repo root:
;;;;   sbcl --non-interactive --load ~/quicklisp/setup.lisp \
;;;;     --eval '(push #p"<repo>/client/" asdf:*central-registry*)' \
;;;;     --eval '(ql:quickload :ephinea-ta-client :silent t)' \
;;;;     --load desktop/tools/verify-store-config.lisp
;;;; Prints PASS/FAIL per key and exits non-zero on any failure.

(in-package :ephinea-ta-client)

(require :sb-posix)

(defparameter *expected*
  (list :server-url "http://localhost:8080"
        :api-token (format nil "eta_tok\"en~cx" (code-char 92))
        :anon-token ""
        :language :ja
        :auto-update nil
        :debug t
        :overlay-corner :custom
        :overlay-position (list 0.25 0.7894558)
        :record-max-total-gb 2.5
        :pinshare-channel "パーティ"
        :record-dir (format nil "D:~cVideos~cRR" (code-char 92) (code-char 92))
        ;; untouched defaults
        :record-audio t :hw-encode t :close-to-tray t :ghost-marker t
        :tracking-only nil :pinshare-server ""
        ;; forced keys: absent from the file, default wins
        :video-upload t :auto-submit t :completion-sound nil))

(let* ((source (merge-pathnames "desktop/tests/RappyRuns.Tests/golden/store/config-csharp.sexp"))
       (appdata (uiop:ensure-directory-pathname
                 (merge-pathnames (format nil "rr-verify-~d/" (get-universal-time))
                                  (uiop:temporary-directory))))
       (target (merge-pathnames "ephinea-ta-client/config.sexp" appdata))
       (failures 0))
  (ensure-directories-exist target)
  (uiop:copy-file source target)
  ;; Point the real CONFIG-DIR at the copy and go through LOAD-CONFIG!.
  (sb-posix:setenv "APPDATA" (string-right-trim "/\\" (namestring appdata)) 1)
  (setf *config* nil)
  (load-config!)
  (format t "config-path: ~a~%" (config-path))
  (loop for (key value) on *expected* by #'cddr
        do (let ((actual (config-value key)))
             (if (equal actual value)
                 (format t "PASS ~s = ~s~%" key actual)
                 (progn (incf failures)
                        (format t "FAIL ~s: expected ~s, got ~s~%" key value actual)))))
  (let ((position (config-value :overlay-position)))
    (unless (every (lambda (x) (typep x 'single-float)) position)
      (incf failures)
      (format t "FAIL :overlay-position is not single floats: ~s~%" position)))
  (uiop:delete-directory-tree appdata :validate t)
  (format t "~d failure~:p~%" failures)
  (uiop:quit (if (zerop failures) 0 1)))
