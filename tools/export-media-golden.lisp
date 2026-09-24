;;;; Golden for RappyRuns.Core.Media: the recording pipeline's pure
;;;; functions (client/src/recording.lisp) - every ffmpeg argv across the
;;;; capture-source decision table (media spec §1.6, byte for byte), the
;;;; remux/trim argv (§1.8), the rect math (§1.4.1), retention (§1.13) and
;;;; the Windows command-line quoting (§1.7).
;;;;
;;;; Run from the repo root (loads the client into SBCL first):
;;;;   sbcl --non-interactive --load ~/quicklisp/setup.lisp \
;;;;     --eval '(setf sb-impl::*default-external-format* :utf-8)' \
;;;;     --eval '(push #p"<ABS REPO>/client/" asdf:*central-registry*)' \
;;;;     --eval '(ql:quickload :ephinea-ta-client :silent t)' \
;;;;     --load desktop/tools/export-media-golden.lisp
;;;; Writes desktop/tests/RappyRuns.Tests/golden/media/recording.json.
;;;; (The UTF-8 default matters: this file holds Japanese test strings, and
;;;; SBCL on Windows otherwise reads it in the ANSI code page.)

(in-package :ephinea-ta-client)

;;; The x264 thread count reads NUMBER_OF_PROCESSORS at call time; pin it
;;; so the golden does not depend on the exporting machine (the C# side
;;; passes the same core count explicitly).
(setf (fdefinition 'logical-processor-count) (lambda () 8))

;;; QUOTE-WINDOWS-ARG / ARGV->COMMAND-LINE live in ffmpeg-win32.lisp,
;;; which is LispWorks-only (FLI). They are pure; this is a verbatim copy
;;; (ffmpeg-win32.lisp:98-124) so the quoting can be pinned from SBCL.
(defun golden-quote-windows-arg (arg)
  (if (and (plusp (length arg))
           (notany (lambda (char) (find char '(#\Space #\Tab #\"))) arg))
      arg
      (with-output-to-string (out)
        (write-char #\" out)
        (let ((backslashes 0))
          (loop :for char :across arg
                :do (case char
                      (#\\ (incf backslashes))
                      (#\"
                       (dotimes (i (1+ (* 2 backslashes)))
                         (write-char #\\ out))
                       (setf backslashes 0)
                       (write-char #\" out))
                      (t
                       (dotimes (i backslashes) (write-char #\\ out))
                       (setf backslashes 0)
                       (write-char char out))))
          (dotimes (i (* 2 backslashes)) (write-char #\\ out)))
        (write-char #\" out))))

(defun golden-argv->command-line (program args)
  (format nil "~{~a~^ ~}" (mapcar #'golden-quote-windows-arg (cons program args))))

;;; A tiny JSON writer (strings, integers, T/NIL, lists as arrays, and
;;; (:obj k v ...) as objects).

(defun json-string (s out)
  (write-char #\" out)
  (loop :for c :across s
        :for code := (char-code c)
        :do (cond ((= code 34) (write-char (code-char 92) out) (write-char #\" out))
                  ((= code 92) (write-char (code-char 92) out) (write-char (code-char 92) out))
                  ((= code 10) (write-char (code-char 92) out) (write-char #\n out))
                  ((= code 9) (write-char (code-char 92) out) (write-char #\t out))
                  ((< code 32) (format out "~cu~4,'0x" (code-char 92) code))
                  (t (write-char c out))))
  (write-char #\" out))

(defun json (x out)
  (cond ((null x) (write-string "null" out))
        ((eq x t) (write-string "true" out))
        ((eq x :false) (write-string "false" out))
        ((stringp x) (json-string x out))
        ((integerp x) (format out "~d" x))
        ((keywordp x) (json-string (string-downcase (symbol-name x)) out))
        ((and (consp x) (eq (first x) :obj))
         (write-char #\{ out)
         (loop :for (k v) :on (rest x) :by #'cddr
               :for first := t :then nil
               :do (unless first (write-string ", " out))
                   (json-string k out)
                   (write-string ": " out)
                   (json v out))
         (write-char #\} out))
        ((listp x)
         (write-char #\[ out)
         (loop :for item :in x
               :for first := t :then nil
               :do (unless first (write-string ", " out))
                   (json item out))
         (write-char #\] out))
        (t (error "cannot encode ~s" x))))

(defun bool (x) (if x t :false))

;;; The decision table: every capture source x encoder x chain x audio x
;;; memory profile START-RECORDING can hand BUILD-FFMPEG-ARGS.

(defparameter *audio-pipe* "\\\\.\\pipe\\ephinea-ta-audio")
(defparameter *video-pipe* "\\\\.\\pipe\\ephinea-ta-video")
(defparameter *window-title* "Ephinea: Phantasy Star Online Blue Burst")
(defparameter *output* "C:\\Users\\u\\Videos\\RappyRuns\\rec-tmp-20260924-213000-0a1b2c3d.mp4")

(defparameter *sources*
  `(("gdigrab" nil nil)
    ("dda-full-adapter0" (:output-idx 0 :adapter 0 :width 1920 :height 1080) nil)
    ("dda-full-legacy" (:output-idx 1 :width 2560 :height 1600) nil)
    ("dda-full-adapter1" (:output-idx 1 :adapter 1 :width 3840 :height 2160) nil)
    ("dda-crop-adapter0" (:output-idx 0 :adapter 0 :width 1920 :height 1080
                          :crop (160 90 1600 900)) nil)
    ("dda-crop-adapter2" (:output-idx 3 :adapter 2 :width 3200 :height 1800
                          :crop (8 31 1280 960)) nil)
    ("wgc-crop" nil (:pipe ,*video-pipe* :width 1296 :height 999 :crop (8 31 1280 960)))
    ("wgc-nocrop" nil (:pipe ,*video-pipe* :width 1280 :height 960))))

(defun monitor-json (m)
  (and m (list :obj
               "outputIdx" (getf m :output-idx)
               "adapter" (getf m :adapter)
               "width" (getf m :width)
               "height" (getf m :height)
               "crop" (getf m :crop))))

(defun wgc-json (w)
  (and w (list :obj
               "pipe" (getf w :pipe)
               "width" (getf w :width)
               "height" (getf w :height)
               "crop" (getf w :crop))))

(defun ffmpeg-cases ()
  (loop :for (name monitor wgc) :in *sources*
        :nconc
        (loop :for encoder :in '(nil "h264_nvenc" "h264_amf" "h264_qsv")
              :nconc
              (loop :for chain :in '(nil t)
                    :nconc
                    (loop :for audio :in (list nil *audio-pipe*)
                          :nconc
                          (loop :for low :in '(nil t)
                                :collect
                                (let ((args (build-ffmpeg-args
                                             :window-title *window-title*
                                             :output-path *output*
                                             :audio-pipe audio
                                             :capture-monitor monitor
                                             :wgc-capture wgc
                                             :video-encoder encoder
                                             :gpu-chain chain
                                             :low-memory low)))
                                  (list :obj
                                        "source" name
                                        "monitor" (monitor-json monitor)
                                        "wgc" (wgc-json wgc)
                                        "encoder" encoder
                                        "gpuChain" (bool chain)
                                        "audioPipe" audio
                                        "lowMemory" (bool low)
                                        "args" args
                                        "stripped" (and audio (strip-audio-args args audio))
                                        "retargeted" (and audio (retarget-audio-args
                                                                 args :sample-format "f32le"
                                                                 :rate 48000 :channels 2))
                                        "commandLine" (golden-argv->command-line
                                                       "C:\\Program Files\\Rappy Runs\\ffmpeg\\ffmpeg.exe"
                                                       args)))))))))

(defun remux-cases ()
  (loop :for duration :in '(nil 0 1 999 1000 2000 7005 702345 701123 3600000 12345678)
        :collect (list :obj
                       "input" *output*
                       "output" "C:\\Users\\u\\Videos\\RappyRuns\\Towards the Future 9'59.123 (2026-07-04 2130).mp4"
                       "durationMs" duration
                       "args" (build-remux-args
                               *output*
                               "C:\\Users\\u\\Videos\\RappyRuns\\Towards the Future 9'59.123 (2026-07-04 2130).mp4"
                               :duration-ms duration))))

(defun mv-list (form-values)
  "(values ...) as a list, NIL when the first value is NIL."
  (and (first form-values) form-values))

(defun golden ()
  (list :obj
        "cores" 8
        "ffmpeg" (ffmpeg-cases)
        "remux" (remux-cases)
        "threads" (loop :for cores :in '(1 2 3 4 5 8 12 16 17 32 64)
                        :collect (list cores (encoder-thread-count cores)))
        "scale" (loop :for (w h) :in '((3200 1800) (2560 1600) (1440 900) (1367 899)
                                       (1920 1080) (1921 1081) (5120 2160) (3840 2160)
                                       (1280 720) (1366 768) (2560 1080) (1080 1920)
                                       (3000 2000) (1000 1081) (1001 1082) (4096 2160)
                                       (1600 1200) (3440 1440) (2880 1800) (2736 1824))
                      :collect (multiple-value-list (record-scale-dimensions w h)))
        "cropRects" (loop :for (client monitor)
                            :in '(((160 90 1760 990) (0 0 1920 1080))
                                  ((2080 90 3680 990) (1920 0 3840 1080))
                                  ((-100 -50 924 718) (0 0 1920 1080))
                                  ((100 100 1123 867) (0 0 1920 1080))
                                  ((0 0 32 32) (0 0 1920 1080))
                                  ((1900 1000 2500 1500) (0 0 1920 1080))
                                  ((1800 900 2500 1500) (0 0 1920 1080))
                                  ((-1920 0 -100 1000) (-1920 0 0 1080))
                                  ((-2000 -30 -1 1079) (-1920 0 0 1080))
                                  ((10 10 75 75) (0 0 1920 1080))
                                  ((10 10 74 74) (0 0 1920 1080))
                                  ((0 0 1920 1080) (0 0 1920 1080)))
                          :collect (list :obj "client" client "monitor" monitor
                                         "crop" (mv-list (multiple-value-list
                                                          (capture-crop-rect client monitor)))))
        "wgcCropRects" (loop :for (client window fw fh)
                               :in '(((108 131 1388 1091) (100 100 1396 1099) 1296 999)
                                     ((108 131 1389 1092) (100 100 1396 1099) 1296 999)
                                     ((100 100 130 130) (100 100 140 140) 40 40)
                                     ((8 31 3202 1831) (0 0 3210 1840) 3202 1832)
                                     ((50 50 1000 800) (60 60 900 700) 840 640)
                                     ((0 0 1920 1080) (0 0 1920 1080) 1920 1080)
                                     ((105 130 1386 1092) (100 100 1396 1099) 1290 990))
                             :collect (list :obj "client" client "window" window
                                            "frameWidth" fw "frameHeight" fh
                                            "crop" (wgc-crop-rect client window fw fh)))
        "covers" (loop :for (window monitor)
                         :in '(((0 0 1920 1080) (0 0 1920 1080))
                               ((1920 0 3840 1080) (1920 0 3840 1080))
                               ((0 0 1920 1032) (0 0 1920 1080))
                               ((100 100 1124 868) (0 0 1920 1080))
                               ((-8 -8 1928 1088) (0 0 1920 1080))
                               ((0 0 1919 1080) (0 0 1920 1080)))
                       :collect (list :obj "window" window "monitor" monitor
                                      "covers" (bool (rect-covers-p window monitor))))
        "quote" (loop :for arg :in (list "" "plain" "with space" "tab	here"
                                         "a\"b" "trail\\" "trail\\\\" "c:\\dir\\x"
                                         "sp ace\\" "q\\\"x" "q\\\\\"x"
                                         "title=Ephinea: Phantasy Star Online Blue Burst"
                                         "scale=-2:trunc(min(1080\\,ih)/2)*2"
                                         "日本語 タイトル")
                      :collect (list arg (golden-quote-windows-arg arg)))
        "sanitize" (loop :for s :in (list "a:b/c\"d" "x\\y*z?w<v>u|t"
                                          (format nil "tab~cnl~c" (code-char 9) (code-char 10))
                                          "Towards the Future 9'59.123 (2026-07-04 2130).mp4"
                                          "ダーク・ファルス")
                         :collect (list s (sanitize-filename s)))
        "duration" (loop :for ms :in '(nil 0 699123 1)
                         :collect (list ms (session-video-duration-ms ms)))
        "probe" (list :obj
                      "encoders" (mapcar (lambda (e) (list e (hw-encoder-probe-args e)))
                                         +hw-encoder-candidates+)
                      "gpuChain" (hw-gpu-chain-probe-args)
                      "gdigrab" (gdigrab-probe-args *window-title*))
        "evict" (let ((files '(("a.mp4" 500 100) ("b.mp4" 500 200) ("c.mp4" 500 300)
                               ("d.mp4" 700 150) ("e.mp4" 10 150))))
                  (loop :for (cap protected uploaded)
                          :in '((nil nil nil) (0 nil nil) (5000 nil nil) (2210 nil nil)
                                (2209 nil nil) (1200 nil nil) (600 nil nil)
                                (100 ("a.mp4") nil) (100 ("a.mp4" "b.mp4") nil)
                                (1200 nil ("c.mp4")) (600 nil ("c.mp4"))
                                (100 ("c.mp4") ("c.mp4" "e.mp4"))
                                (1000 ("d.mp4") ("b.mp4" "zz.mp4")))
                        :collect (list :obj "files" files "cap" cap
                                       "protected" protected "uploaded" uploaded
                                       "evicted" (recordings-to-evict
                                                  files cap :protected protected
                                                  :uploaded uploaded))))))

(with-open-file (out "desktop/tests/RappyRuns.Tests/golden/media/recording.json"
                     :direction :output :if-exists :supersede
                     :if-does-not-exist :create :external-format :utf-8)
  (json (golden) out)
  (terpri out))

(format t "ok~%")
