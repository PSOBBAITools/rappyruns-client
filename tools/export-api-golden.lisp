;;;; Golden for RappyRuns.Core.Api / Update: request paths, response parsing
;;;; and error wording, produced by the real Lisp client functions.
;;;;
;;;; Run from the repo root (loads the client system through Quicklisp):
;;;;   sbcl --non-interactive --load ~/quicklisp/setup.lisp \
;;;;     --eval '(push (truename "client/") asdf:*central-registry*)' \
;;;;     --eval '(ql:quickload :ephinea-ta-client :silent t)' \
;;;;     --load desktop/tools/export-api-golden.lisp
;;;;
;;;; HTTP-REQUEST is swapped for a recorder, so every "fetch" below runs the
;;;; client's own URL building without touching the network.

(in-package :ephinea-ta-client)

(defun obj (&rest kvs)
  (let ((h (make-hash-table :test 'equal)))
    (loop for (k v) on kvs by #'cddr do (setf (gethash k h) v))
    h))

(defvar *captured-url* nil)

(defun captured-path (thunk)
  "Run THUNK with HTTP-REQUEST recording its URL and answering 404."
  (let ((original (fdefinition 'http-request)))
    (setf (fdefinition 'http-request)
          (lambda (method url &rest args)
            (declare (ignore method args))
            (setf *captured-url* url)
            (values 404 "{}")))
    (unwind-protect (progn (funcall thunk) *captured-url*)
      (setf (fdefinition 'http-request) original))))

(defparameter *ghost-cases*
  ;; (slug extra-slugs difficulty party-size pb)
  '(("q" nil nil nil nil)
    ("q" ("a" "b") nil nil nil)
    ("q" nil "Very Hard" nil nil)
    ("q" ("seg-1") "Ultimate" 4 0)
    ("q" nil "Anguish 3" 1 0)
    ("q" nil nil 2 nil)
    ("q" nil nil nil 1)))

(defun ghost-case (c)
  (destructuring-bind (slug extra difficulty party pb) c
    (obj "slug" slug
         "extra" (coerce extra 'vector)
         "difficulty" (or difficulty 'null)
         "party_size" (or party 'null)
         "pb" (or pb 'null)
         "path" (captured-path
                 (lambda ()
                   (fetch-ghost-splits slug :extra-slugs extra :difficulty difficulty
                                            :party-size party :pb pb
                                            :server-url "https://s.example/" :token "t"))))))

(defparameter *pins-cases* '(("q" nil) ("q" ("a")) ("q" ("a" "b"))))

(defun pins-case (c)
  (destructuring-bind (slug extra) c
    (obj "slug" slug "extra" (coerce extra 'vector)
         "path" (captured-path
                 (lambda ()
                   (fetch-pin-set slug :extra-slugs extra
                                       :server-url "https://s.example" :token "t"))))))

(defparameter *encode-cases*
  '("Very Hard" "Aあ" "Ultimate" "Anguish 3" "a-b_c.d~e" "x/y?z=1&w" "100%" "é" "😀"))

(defparameter *reason-payloads*
  ;; JSON bodies of rejected submissions
  '("{\"message\":\"nope\"}"
    "{\"message\":\"Invalid run\",\"errors\":[\"time_ms too small\",\"unknown quest\"]}"
    "{\"errors\":[\"a\"]}"
    "{\"errors\":[]}"
    "{\"message\":\"m\",\"errors\":[]}"
    "{}"
    "{\"message\":\"m\",\"errors\":[1,2]}"))

(defun reason-case (body)
  (obj "body" body
       "reason" (getf (submission-updates :rejected (jzon:parse body)) :reason)))

(defparameter *standing-payloads*
  '("{\"id\":7,\"url\":\"https://x/runs/7\",\"standing\":{\"rank\":2,\"parties\":5,\"previous_best_ms\":605000,\"delta_ms\":-3210}}"
    "{\"id\":7,\"standing\":{\"rank\":1,\"parties\":1}}"
    "{\"id\":7,\"standing\":{\"rank\":1,\"parties\":1,\"previous_best_ms\":5}}"
    "{\"id\":7,\"standing\":{\"rank\":1.0,\"parties\":1}}"
    "{\"id\":7,\"standing\":null}"
    "{\"id\":7}"
    "{\"url\":\"u\"}"))

(defun standing-case (body)
  (let ((u (submission-updates :created (jzon:parse body))))
    (obj "body" body
         "server_id" (or (getf u :server-id) 'null)
         "url" (or (getf u :url) 'null)
         "rank" (or (getf u :standing-rank) 'null)
         "parties" (or (getf u :standing-parties) 'null)
         "prev" (or (getf u :standing-prev-ms) 'null)
         "delta" (or (getf u :standing-delta-ms) 'null))))

(defparameter *error-messages*
  '("WinHttpConnect failed (Windows error 12029)"
    "WinHttpSendRequest failed (Windows error 12030)"
    "WinHttpSendRequest failed (Windows error 12007)"
    "WinHttpReceiveResponse failed (Windows error 12002)"
    "WinHttpSendRequest failed (Windows error 12175)"
    "WinHttpSendRequest failed (Windows error 12157)"
    "WinHttpSendRequest failed (Windows error 5)"
    "Bad URL: nonsense"
    "GET /api/quests -> 500"
    "plain message"))

(defun error-case (message)
  (let ((condition (make-condition 'api-error :message message)))
    (obj "message" message
         "code" (or (windows-error-code message) 'null)
         "server_en" (let ((*language* :en)) (server-status-error-text condition))
         "server_ja" (let ((*language* :ja)) (server-status-error-text condition))
         "token_en" (let ((*language* :en)) (token-status-error-text condition))
         "token_ja" (let ((*language* :ja)) (token-status-error-text condition)))))

(defparameter *release-bodies*
  '("{\"tag_name\":\"v0.6.0\",\"assets\":[{\"name\":\"RappyRunsClient.zip\",\"size\":10,\"browser_download_url\":\"https://x/a.zip\"}]}"
    "{\"tag_name\":\"v0.6.0\",\"assets\":[{\"name\":\"RappyRunsClient.zip\",\"size\":0,\"browser_download_url\":\"https://x/a.zip\"}]}"
    "{\"tag_name\":\"v0.6.0\",\"assets\":[{\"name\":\"RappyRunsClient.zip\",\"size\":10.5,\"browser_download_url\":\"https://x/a.zip\"}]}"
    "{\"tag_name\":\"v0.6.0\",\"assets\":[{\"name\":\"RappyRunsClient.zip\",\"browser_download_url\":\"https://x/a.zip\"}]}"
    "{\"tag_name\":\"v0.6.0\",\"assets\":[{\"name\":\"RappyRunsClient.zip\",\"browser_download_url\":5},{\"name\":\"RappyRunsClient.zip\",\"browser_download_url\":\"https://x/b.zip\"}]}"
    "{\"tag_name\":\"v0.6.0\",\"assets\":[{\"name\":\"rappyrunsclient.zip\",\"browser_download_url\":\"https://x/a.zip\"}]}"
    "{\"tag_name\":6,\"assets\":[{\"name\":\"RappyRunsClient.zip\",\"browser_download_url\":\"https://x/a.zip\"}]}"
    "{\"tag_name\":\"v0.6.0\",\"assets\":{}}"
    "[]"
    "not json"
    ""))

(defun release-case (body)
  (let ((r (parse-release-json body)))
    (obj "body" body
         "tag" (or (getf r :tag) 'null)
         "url" (or (getf r :asset-url) 'null)
         "size" (or (getf r :asset-size) 'null))))

(defparameter *triggers*
  '((:warp-in) (:register 254) (:floor-switch 5 2) (:monster-dead 1234)))

(defun trigger-case (trigger)
  (obj "sexp" (prin1-to-string trigger)
       "json" (jzon:stringify (trigger->json trigger))))

(with-open-file (out "desktop/tests/RappyRuns.Tests/golden/api/api-golden.json"
                     :direction :output :if-exists :supersede
                     :external-format :utf-8)
  (write-string
   (jzon:stringify
    (obj "ghost" (map 'vector #'ghost-case *ghost-cases*)
         "pins" (map 'vector #'pins-case *pins-cases*)
         "encode" (map 'vector (lambda (s) (obj "in" s "out" (url-encode-component s)))
                       *encode-cases*)
         "reasons" (map 'vector #'reason-case *reason-payloads*)
         "standings" (map 'vector #'standing-case *standing-payloads*)
         "errors" (map 'vector #'error-case *error-messages*)
         "releases" (map 'vector #'release-case *release-bodies*)
         "triggers" (map 'vector #'trigger-case *triggers*))
    :pretty t)
   out))

(format t "~&wrote desktop/tests/RappyRuns.Tests/golden/api/api-golden.json~%")
