;;;; Golden for RappyRuns.Core.PinShare: what the Lisp relay (pinshare.lisp)
;;;; produces for number parsing / printing, out.txt command translation and
;;;; the full in.txt text. The in.txt / out.txt formats are a contract with
;;;; the unchanged Lua addon, so the C# port must match these byte for byte.
;;;;
;;;; Run from the repo root (loads the whole client through ASDF):
;;;;   sbcl --non-interactive --load ~/quicklisp/setup.lisp \
;;;;     --eval '(push #p"<repo>/client/" asdf:*central-registry*)' \
;;;;     --eval '(ql:quickload :ephinea-ta-client :silent t)' \
;;;;     --load desktop/tools/export-pinshare-golden.lisp
;;;;
;;;; Writes desktop/tests/RappyRuns.Tests/golden/pinshare/pinshare.json.
;;;; NOTE: prints doubles the way SBCL does; the shipped client ran on
;;;; LispWorks, whose PRIN1 of a double agrees for the fixed-notation range
;;;; game coordinates live in (1e-3 <= |x| < 1e7).

(in-package :ephinea-ta-client)

(defun golden-table (&rest plist)
  (let ((table (make-hash-table :test 'equal)))
    (loop :for (key value) :on plist :by #'cddr
          :do (setf (gethash key table) value))
    table))

(defparameter *number-inputs*
  '("-123.456" "0.000" "1e3" "1.5" "60" "60.000" "60.5" "+7" "-0.0" "-0"
    ".5" "5." "1E-2" "1e+2" "0.001" "0.0009999" "9999999.999" "10000000"
    "1e7" "12345678.9" "1.5e-5" "1e-30" "1e30" "1e31" "123456789012345678901234567890"
    "1234567890123456789012345678901" "0.1" "0.2" "0.30000000000000004"
    "3.14159265358979323846" "-2.25" "100" "1e0000000000000000005"
    "nan" "inf" "-nan(ind)" "" "-" "." "1.5x" "1e" "#.(quit)" " 1" "1 " "1,5"
    "--1" "+-1" "1e--2" "0x10" "１"))

(defun number-case (input)
  (let ((number (parse-pinshare-number input))
        (integer (parse-pinshare-integer input)))
    (golden-table "in" input
                  "text" (if number (pinshare-format-number number) 'null)
                  "int" (if integer (princ-to-string integer) 'null))))

(defparameter *command-inputs*
  '(("1" "add" "10203" "1.500" "0.000" "-2.250" "60" "集合" "3" "7")
    ("1" "add" "1" "1.000" "2.000" "3.000" "0")
    ("1" "add" "1" "1.000" "2.000" "3.000" "0" "lbl")
    ("1" "add" "1" "1.000" "2.000" "3.000" "0" "" "")
    ("1" "add" "1" "1.000" "2.000" "3.000" "0" "" "x" "5")
    ("1" "add" "1" "1.000" "2.000" "3.000" "0.5")
    ("1" "add" "1.5" "1.000" "2.000" "3.000" "0")
    ("1" "add" "1" "nan" "2.000" "3.000" "0" "" "3")
    ("1" "add" "1" "1" "2" "3")
    ("1" "move" "4" "1.000" "2.000" "3.000")
    ("1" "move" "4" "1.000" "2.000")
    ("1" "move" "x" "1.000" "2.000" "3.000")
    ("1" "arrow_add" "5" "1.000" "2.000" "3.000" "4.000" "5.000" "6.000" "30" "3"
     "2.500" "3.500" "9.000" "12")
    ("1" "arrow_add" "5" "1.000" "2.000" "3.000" "4.000" "5.000" "6.000" "30" "3")
    ("1" "arrow_add" "5" "1.000" "2.000" "3.000" "4.000" "5.000" "6.000" "30" "3"
     "2.500" "3.500")
    ("1" "arrow_add" "5" "1.000" "2.000" "3.000" "4.000" "5.000" "6.000" "30" "3"
     "2.500" "bad" "9.000")
    ("1" "arrow_add" "5" "1.000" "2.000" "3.000" "4.000" "5.000" "6.000" "30" "3"
     "2.500" "3.500" "9.000")
    ("1" "arrow_add" "5" "1.000" "2.000" "3.000" "4.000" "5.000" "6.000" "30")
    ("1" "arrow_move" "8" "1.000" "2.000" "3.000" "4.000" "5.000" "6.000")
    ("1" "arrow_move" "8" "1.000" "2.000" "3.000" "4.000" "5.000" "6.000"
     "7.000" "8.000" "9.000")
    ("1" "arrow_move" "8" "1.000" "2.000" "3.000" "4.000" "5.000" "6.000" "7.000")
    ("1" "remove" "5") ("1" "remove") ("1" "remove" "-2") ("1" "arrow_remove" "4")
    ("1" "remove" "5.5")
    ("1" "clear_mine") ("1" "clear_all") ("1" "clear_all" "extra")
    ("1" "teleport" "1") ("1" "name" "x") ("1" "color" "FF0000")))

(defun command-case (fields)
  (golden-table "fields" (coerce fields 'vector)
                "json" (or (pinshare-command-message fields) 'null)))

(defparameter *render-inputs*
  ;; (channel session last-seq status message state-json set-json)
  (list
   (list "secret" "abcd1234" 10 "connected" ""
         "{\"t\":\"state\",\"pins\":[{\"id\":3,\"owner\":\"Tea\\tpot\",\"floor\":10203,\"x\":1.5,\"y\":0,\"z\":-2.25,\"label\":\"集合\",\"no\":4,\"ownerNo\":2,\"room\":7,\"roomNo\":1,\"color\":\"ff8c00\",\"remaining\":42},{\"id\":4,\"owner\":\"Old\",\"floor\":1,\"x\":1,\"y\":2,\"z\":3,\"label\":\"\",\"no\":5,\"ownerNo\":1,\"room\":null,\"roomNo\":null,\"color\":\"\",\"remaining\":-1},{\"id\":\"bad\",\"floor\":1,\"x\":1,\"y\":2,\"z\":3}],\"arrows\":[{\"id\":9,\"owner\":\"Kettle\",\"floor\":5,\"room\":12,\"x1\":1,\"y1\":2,\"z1\":3,\"x2\":4,\"y2\":5,\"z2\":6,\"xm\":2.5,\"ym\":3.5,\"zm\":9,\"color\":\"66e0ff\",\"remaining\":30}],\"members\":[\"Teapot\",\"Kettle\"]}"
         nil)
   (list "se	cret" "00000000" 0 "connecting" "wss://x"
         "{\"t\":\"state\",\"pins\":[{\"id\":1.0,\"floor\":1,\"x\":1,\"y\":2,\"z\":3},{\"id\":2,\"floor\":1.5,\"x\":1,\"y\":2,\"z\":3},{\"id\":3,\"floor\":1,\"x\":\"1\",\"y\":2,\"z\":3},{\"id\":5,\"owner\":7,\"floor\":1,\"x\":1.0,\"y\":1e2,\"z\":-0.0,\"label\":12.5,\"no\":true,\"ownerNo\":false,\"color\":{},\"roomNo\":[],\"room\":1e-5,\"remaining\":12345678.5},{\"id\":6,\"floor\":2,\"x\":0.001,\"y\":0.0001,\"z\":123456789.25},\"junk\",7],\"arrows\":[{\"id\":10,\"owner\":\"A\",\"floor\":5,\"x1\":1,\"y1\":2,\"z1\":3,\"x2\":4,\"y2\":5,\"z2\":6,\"color\":\"\",\"remaining\":3},{\"id\":11,\"owner\":\"B\",\"floor\":5,\"x1\":1,\"y1\":2,\"z1\":3,\"x2\":4,\"y2\":5,\"z2\":6,\"xm\":1.25,\"remaining\":3},{\"id\":12,\"floor\":5,\"x1\":1,\"y1\":2,\"z1\":3,\"x2\":4,\"y2\":5,\"z2\":6,\"xm\":null,\"ym\":1,\"zm\":2,\"room\":3}],\"members\":[\"A\",5,null,\"B\\u0001C\"]}"
         nil)
   (list "" "cafe0001" 99 "local" ""
         "{\"t\":\"state\"}"
         "{\"id\":7,\"name\":\"TTF route\",\"mine\":1,\"items\":{\"pins\":[{\"area\":10101,\"room\":3,\"x\":1,\"y\":2.5,\"z\":-3,\"label\":\"start\",\"color\":\"ff8c00\"},{\"area\":10101,\"room\":3,\"x\":4,\"y\":0,\"z\":0},{\"area\":10102,\"x\":5,\"y\":0,\"z\":0}],\"arrows\":[{\"area\":10101,\"room\":4,\"x1\":0,\"y1\":0,\"z1\":0,\"x2\":10,\"y2\":0,\"z2\":10,\"xm\":5,\"ym\":0,\"zm\":6}]}}")
   (list "p" "cafe0002" 1 "error" "disconnected	from
server"
         "{\"t\":\"state\",\"pins\":[],\"arrows\":[],\"members\":[]}"
         "{\"name\":\"Edge\\tcases and a long name\",\"items\":{\"pins\":[{\"area\":null,\"floor\":2,\"x\":0,\"y\":0,\"z\":0},{\"area\":false,\"floor\":3,\"x\":0,\"y\":0,\"z\":0},{\"floor\":4,\"x\":0,\"y\":0,\"z\":0,\"id\":99,\"owner\":\"x\",\"remaining\":5,\"locked\":0,\"no\":3,\"ownerNo\":8},\"skip\",{\"area\":5,\"x\":1},{\"area\":6,\"x\":1.5,\"y\":-1.5,\"z\":1e-4,\"label\":3,\"color\":null,\"room\":2.5}],\"arrows\":[{\"area\":1,\"x1\":0,\"y1\":0,\"z1\":0,\"x2\":1,\"y2\":1,\"z2\":1},{\"area\":1,\"x1\":0,\"y1\":0,\"z1\":0,\"x2\":1,\"y2\":1,\"z2\":1,\"xm\":0.5}]}}")
   (list "p" "cafe0003" 1 "connected" "" "{\"t\":\"state\",\"pins\":5}"
         "{\"name\":7,\"items\":{\"pins\":{},\"arrows\":[{\"area\":1,\"x1\":0,\"y1\":0,\"z1\":0,\"x2\":1,\"y2\":1,\"z2\":1,\"xm\":2,\"ym\":2,\"zm\":2,\"room\":1}]}}")
   (list "p" "cafe0004" 1 "connected" "" "{\"t\":\"state\"}"
         "{\"name\":\"no items\"}")))

(defun render-case (input)
  (destructuring-bind (channel session seq status message state set) input
    (let ((relay (make-pinshare-relay :channel channel :session session
                                      :last-seq seq)))
      (pinshare-relay-set-status relay status message)
      (pinshare-relay-note-message relay state)
      (golden-table "channel" channel "session" session "lastSeq" seq
                    "status" status "message" message
                    "state" state "set" (or set 'null)
                    "time" 1700000000
                    "text" (render-pinshare-inbox
                            relay 1700000000
                            (and set (com.inuoe.jzon:parse set)))))))

(let ((path (merge-pathnames
             "desktop/tests/RappyRuns.Tests/golden/pinshare/pinshare.json"
             (uiop:getcwd))))
  (ensure-directories-exist path)
  (with-open-file (out path :direction :output :if-exists :supersede
                            :external-format :utf-8)
    (write-string
     (com.inuoe.jzon:stringify
      (golden-table "numbers" (map 'vector #'number-case *number-inputs*)
                    "commands" (map 'vector #'command-case *command-inputs*)
                    "renders" (map 'vector #'render-case *render-inputs*))
      :pretty t)
     out))
  (format t "~&wrote ~a~%" path))
