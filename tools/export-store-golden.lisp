;;;; Golden files for RappyRuns.Core.Config / RappyRuns.Core.Store (issue #322):
;;;; a queue.sexp and a config.sexp written by the Lisp client (the C# client
;;;; must load and re-save them byte-identically), and the outputs of the pure
;;;; store/config/credentials helpers as JSON.
;;;;
;;;; Run from the repo root:
;;;;   sbcl --non-interactive --load ~/quicklisp/setup.lisp \
;;;;     --eval '(push #p"<repo>/client/" asdf:*central-registry*)' \
;;;;     --eval '(ql:quickload :ephinea-ta-client :silent t)' \
;;;;     --load desktop/tools/export-store-golden.lisp

(in-package :ephinea-ta-client)

(defparameter *golden-dir* #p"desktop/tests/RappyRuns.Tests/golden/store/")

(defun golden-path (name) (merge-pathnames name *golden-dir*))

(defun lisp-print (datum)
  "The exact text write-sexp-file produces for DATUM."
  (with-standard-io-syntax
    (let ((*print-readably* nil)
          (*package* (find-package :keyword)))
      (prin1-to-string datum))))

(defun obj (&rest kvs)
  (let ((h (make-hash-table :test 'equal)))
    (loop for (k v) on kvs by #'cddr do (setf (gethash k h) v))
    h))

(defun or-null (x) (if x x 'null))

(defun write-json (name value &key (pretty t))
  (with-open-file (out (golden-path name) :direction :output :if-exists :supersede
                                          :external-format :utf-8)
    (jzon:stringify value :stream out :pretty pretty))
  (format t "wrote ~a~%" name))

(ensure-directories-exist (golden-path "x"))

;;; ------------------------------------------------------------------
;;; queue.sexp written by save-queue!
;;; ------------------------------------------------------------------

(defparameter *telemetry*
  (list :frames (list (list :t 0 :hp 1200 :x 12.5 :z -3.25 :pb 0.7894558)
                      (list :t 1 :hp 1187 :x 13.75 :z -2.0 :pb 1.0e-5))
        :items (list (cons 5 1234) (cons "Resta" 2) (cons :monomate 1))
        :ratio 1.5d0
        :note "a \"quoted\" back\\slash"))

(let ((*queue-path* (golden-path "queue-lisp.sexp"))
      (*runs*
        (list
         ;; queued: keeps its telemetry
         (list :status :queued :quest-slug "ep1-lost-heat-sword"
               :quest-name "ロスト ヒート ソード" :time-ms 599123
               :finished-at 3967948800 :episode 1 :players 4
               :telemetry *telemetry* :video-offset-ms 2345)
         ;; submitted, video pending: telemetry dropped on save
         (list :standing-delta-ms -3210 :standing-prev-ms 605000
               :standing-parties 5 :standing-rank 2
               :server-id 42 :url "https://rappyruns-production.up.railway.app/runs/42"
               :status :submitted :quest-slug "ep2-gal-da-val"
               :quest-name "Gal Da Val's Darkness" :time-ms 1234567
               :finished-at 3967949000 :video-path "C:\\Users\\テスト\\Videos\\RappyRuns\\run.mp4"
               :telemetry *telemetry* :pb t :next-upload-at 3967950000)
         ;; failed: keeps telemetry and the reason
         (list :reason "WinHttpConnect failed (Windows error 12029)"
               :status :failed :quest-slug "ep4-mop-up-1" :quest-name "Mop-up Operation #1"
               :time-ms 61000 :finished-at 3967948000 :aborted nil
               :ghost-delta-ms -1500 :ghost-label "PB 1:02.500"
               :telemetry (list :frames nil))
         ;; finished: not active, never written
         (list :status :submitted :server-id 41 :quest-slug "ep1-x" :time-ms 1
               :video-attached t :video-path "v.mp4"))))
  (save-queue!)
  (format t "wrote queue-lisp.sexp~%"))

;;; ------------------------------------------------------------------
;;; config.sexp written by save-config! (no forced keys: those are
;;; scrubbed on load, which would change the bytes)
;;; ------------------------------------------------------------------

(let ((*config*
        (list :pinshare-allowed t :pinshare-channel "teapot" :pinshare-enabled t
              :ghost-race t :overlay-capturable t
              :overlay-position (list 0.7894558 0.020304569) :overlay-corner :custom
              :ghost-video t :ghost-overlay t :tracking-private nil :tracking-only t
              :auto-publish t :moderator t :record-audio t :language :ja
              :record-dir "D:\\録画\\\"rr\"" :ffmpeg-path "" :trigger-log t
              :record-max-total-gb 7.5 :update-repo "someone/fork"
              :server-url "https://rappyruns-production.up.railway.app"
              :api-token "eta_REDACTED")))
  (write-sexp-file (golden-path "config-lisp.sexp") *config*)
  (format t "wrote config-lisp.sexp~%"))

;;; ------------------------------------------------------------------
;;; Display helpers
;;; ------------------------------------------------------------------

(defparameter *display-entries*
  (list
   '(:status :queued)
   '(:status :submitted :url "https://example.com/runs/42")
   '(:status :submitted :video-path "v.mp4")
   '(:status :submitted :video-path "v.mp4" :upload-given-up t)
   '(:status :submitted :aborted t :video-path "v.mp4")
   '(:status :submitted :unranked t)
   '(:status :submitted :video-attached t)
   '(:status :submitted :video-attached t :held t)
   '(:status :submitted :video-attached t :approved t)
   '(:status :submitted :video-attached t :aborted t)
   '(:status :submitted :video-attached t :video-uploaded t :video-path "v.mp4")
   '(:status :duplicate)
   '(:status :rejected :reason "too fast")
   '(:status :rejected)
   '(:status :failed :reason "POST /api/runs -> 500")
   '(:status :weird)
   '(:server-id 7 :video-path "v.mp4")
   '(:server-id 8 :video-path "v.mp4")
   '(:status :submitted :server-id 7 :video-path "v.mp4" :upload-given-up t)
   '(:status :submitted :quest-name "Lost HEAT SWORD" :time-ms 599123 :standing-rank 2 :standing-parties 5 :standing-delta-ms -3210)
   '(:status :submitted :quest-name "Q" :time-ms 60000 :standing-rank 3 :standing-parties 8 :standing-delta-ms 1500)
   '(:status :submitted :quest-name "Q" :time-ms 60000 :standing-rank 1 :standing-parties 1 :standing-delta-ms -3210)
   '(:status :submitted :quest-name "Q" :time-ms 60000 :standing-rank 1 :standing-parties 1 :standing-delta-ms 16830)
   '(:status :submitted :quest-name "Q" :time-ms 60000 :standing-rank 1 :standing-parties 1)
   '(:status :submitted :quest-name "Q" :time-ms 60000 :standing-rank 1 :standing-parties 5 :standing-delta-ms -3210)
   '(:status :submitted :quest-name "Q" :time-ms 60000 :standing-rank 3 :standing-parties 8)
   '(:status :submitted :quest-name "Q" :time-ms 60000 :standing-rank 3 :standing-parties 3 :standing-delta-ms 1500)
   '(:status :submitted :quest-name "Q" :time-ms 60000 :standing-rank 5 :standing-parties 8 :standing-delta-ms 1500)
   '(:status :submitted :quest-name "Q" :time-ms 3600000 :standing-rank 5 :standing-parties 8 :standing-delta-ms -70000)
   '(:status :submitted :quest-slug "ep1-slug-only" :time-ms 5 :standing-rank 1 :standing-parties 2)
   '(:status :submitted :quest-name "クエスト" :time-ms 754321 :standing-rank 4 :standing-parties 9 :standing-delta-ms 1005)
   '(:status :submitted :quest-name "Q" :time-ms 70000 :ghost-delta-ms -3210)
   '(:status :submitted :quest-name "Q" :time-ms 70000 :ghost-delta-ms 2500)
   '(:status :submitted :quest-name "Q" :time-ms 70000 :ghost-delta-ms -65000)
   '(:status :submitted :quest-name "Q" :time-ms 70000 :ghost-delta-ms 0)
   '(:status :submitted :quest-name "Q" :time-ms 70000 :standing-rank 5 :standing-parties 8 :standing-delta-ms 1500 :ghost-delta-ms -1125)
   '(:status :submitted :quest-name "Q" :time-ms 70000 :standing-rank 1 :standing-parties 3 :ghost-delta-ms -1125)))

(defun toast-json (fn entry)
  (multiple-value-bind (title text) (funcall fn entry)
    (if title (obj "title" title "text" text) 'null)))

(let ((rows '()))
  (dolist (entry *display-entries*)
    (dolist (language '(:en :ja))
      (let ((*language* language)
            (*upload-progress* (list 7 50 200)))
        (let ((labels (make-hash-table :test 'equal)))
          (dolist (token '(t nil))
            (dolist (upload '(t nil))
              (let ((*config* (append (list :video-upload upload
                                            :anon-token (if token "eta_g" ""))
                                      (copy-list *default-config*))))
                (setf (gethash (format nil "~:[notoken~;token~]-~:[noupload~;upload~]" token upload)
                               labels)
                      (run-status-label entry)))))
          (push (obj "entry" (lisp-print entry)
                     "language" (string-downcase (symbol-name language))
                     "status" labels
                     "video" (run-video-label entry)
                     "note" (or-null (entry-note entry))
                     "standingNote" (or-null (run-standing-note entry))
                     "ghostNote" (or-null (ghost-note entry))
                     "standingToast" (toast-json #'standing-toast entry)
                     "ghostToast" (toast-json #'ghost-toast entry))
                rows)))))
  (write-json "display.json" (coerce (nreverse rows) 'vector)))

(let* ((times '(0 1 999 1000 59999 60000 61000 599123 754321 3599999 3600000 3900000 -1 -1001))
       (improvements (append '(0 1 5 45 3210 1500 16830 59999 60000 65000 100000)
                             (loop for ms from 5 below 60000 by 10 collect ms))))
  (write-json "formats.json"
              (obj "runTime" (coerce (mapcar (lambda (ms) (obj "ms" ms "text" (format-run-time ms))) times) 'vector)
                   "splitClock" (coerce (mapcar (lambda (ms) (obj "ms" ms "text" (format-split-clock ms))) times) 'vector)
                   ;; {"ms": "text"} keeps the ~6000 tie cases compact.
                   "improvement" (let ((h (make-hash-table :test 'equal)))
                                   (dolist (ms improvements h)
                                     (setf (gethash (princ-to-string ms) h)
                                           (format-improvement-ms ms)))))
              :pretty nil))

;;; ------------------------------------------------------------------
;;; submission-updates from response bodies
;;; ------------------------------------------------------------------

(let ((cases '((:created "{\"id\":42,\"url\":\"https://x/runs/42\"}")
               (:duplicate "{\"id\":42,\"url\":\"https://x/runs/42\"}")
               (:created "{\"id\":7,\"url\":\"u\",\"standing\":{\"rank\":2,\"parties\":5,\"previous_best_ms\":605000,\"delta_ms\":-3210}}")
               (:created "{\"id\":7,\"standing\":{\"rank\":1,\"parties\":1}}")
               (:created "{\"id\":7,\"standing\":{\"rank\":1.5,\"parties\":1}}")
               (:created "{\"id\":7,\"standing\":{\"rank\":1,\"parties\":1,\"previous_best_ms\":5}}")
               (:created "{\"id\":7,\"standing\":null}")
               (:created "[]")
               (:rejected "{\"message\":\"nope\"}")
               (:rejected "{\"message\":\"Invalid run\",\"errors\":[\"time too fast\",\"bad quest\"]}")
               (:rejected "{\"errors\":[\"a\"]}")
               (:rejected "{\"errors\":[]}")
               (:rejected "{\"message\":\"x\",\"errors\":[]}")
               (:rejected "{}"))))
  (write-json "submission-updates.json"
              (coerce (loop for (outcome body) in cases
                            collect (obj "outcome" (string-downcase (symbol-name outcome))
                                         "payload" body
                                         "updates" (lisp-print (submission-updates outcome (jzon:parse body)))))
                      'vector)))

;;; ------------------------------------------------------------------
;;; parse-credentials
;;; ------------------------------------------------------------------

(let ((cases (list (format nil "username=Teapot~%password=secret123~%")
                   (format nil "~a# comment~a~%  username = Teapot ~a~%password=a=b=c~a~%"
                           (code-char #xFEFF) #\Return #\Return #\Return)
                   (format nil "username=Teapot~%")
                   (format nil "username=Teapot~%password=~%")
                   ""
                   (format nil "USERNAME=Upper~%Password=pw~%")
                   (format nil "username=first~%username=second~%password=p~%")
                   (format nil "~cusername=tabbed~c~%password = spaced pass ~%" #\Tab #\Tab)
                   (format nil "#username=commented~%password=p~%")
                   (format nil "garbage line~%username=u~%password=p")
                   (format nil "username=ユーザー~%password=パス=ワード~%"))))
  (write-json "credentials.json"
              (coerce (loop for text in cases
                            collect (multiple-value-bind (u p) (parse-credentials text)
                                      (obj "text" text "username" (or-null u) "password" (or-null p))))
                      'vector)))
