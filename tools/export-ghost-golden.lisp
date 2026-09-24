;;;; Goldens for RappyRuns.Core.Ghost: what the Lisp client's ghost race
;;;; math returns over realistic inputs (camera projection and FOV, room
;;;; alignment, track interpolation, overlay placement, gap / clock
;;;; formatting). The C# port must reproduce these exactly.
;;;;
;;;; Run from the repo root, with the client loadable by ASDF:
;;;;   sbcl --non-interactive --load ~/quicklisp/setup.lisp \
;;;;     --eval '(push #p"<repo>/client/" asdf:*central-registry*)' \
;;;;     --eval '(ql:quickload :ephinea-ta-client :silent t)' \
;;;;     --load desktop/tools/export-ghost-golden.lisp
;;;;
;;;; Single floats travel as their IEEE bit patterns ("...Bits" keys) so
;;;; no decimal round trip can blur them; doubles go through jzon's
;;;; shortest round-trip printing.

(defpackage :export-ghost-golden
  (:use :cl))
(in-package :export-ghost-golden)

(defvar *rs* (sb-ext:seed-random-state 20260924))

(defun rnd (n) (random n *rs*))
(defun rnd-float (lo hi) (+ lo (random (- hi lo) *rs*)))

(defun obj (&rest kvs)
  (let ((h (make-hash-table :test 'equal)))
    (loop for (k v) on kvs by #'cddr do (setf (gethash k h) v))
    h))

(defun jnull (x) (if (null x) 'null x))
(defun sbits (f) (ldb (byte 32 0) (sb-kernel:single-float-bits f)))

(defun write-golden (name datum)
  (let ((path (format nil "desktop/tests/RappyRuns.Tests/golden/ghost/~a" name)))
    (ensure-directories-exist path)
    (with-open-file (out path :direction :output :if-exists :supersede
                              :external-format :utf-8)
      (write-string (com.inuoe.jzon:stringify datum :pretty t) out)
      (terpri out))
    (format t "wrote ~a~%" path)))

;;; --- Camera projection -------------------------------------------------

(defun unit-dir (yaw pitch)
  "A single-float unit eye direction, like the game's camera struct."
  (let* ((cp (cos pitch))
         (x (coerce (* cp (sin yaw)) 'single-float))
         (y (coerce (sin pitch) 'single-float))
         (z (coerce (* cp (cos yaw)) 'single-float)))
    (list x y z)))

(defun projection-case (camera width height wx wy wz)
  (multiple-value-bind (sx sy)
      (eta-client::ghost-screen-position camera width height wx wy wz)
    (obj "camera" (if camera
                      (obj "xBits" (sbits (getf camera :x))
                           "yBits" (sbits (getf camera :y))
                           "zBits" (sbits (getf camera :z))
                           "dirXBits" (sbits (getf camera :dir-x))
                           "dirYBits" (sbits (getf camera :dir-y))
                           "dirZBits" (sbits (getf camera :dir-z))
                           "zoom" (jnull (getf camera :zoom)))
                      'null)
         "width" width "height" height
         "wx" wx "wy" wy "wz" wz
         "result" (if sx (vector sx sy) 'null))))

(defun projection-cases ()
  (let ((sizes '((1360 768) (1920 1080) (1280 960) (1600 900) (1024 768)
                 (1361 767) (2560 1440) (800 600)))
        (cases '()))
    (dotimes (i 400)
      (destructuring-bind (w h) (nth (rnd (length sizes)) sizes)
        (let* ((px (rnd-float -800d0 800d0))
               (pz (rnd-float -800d0 800d0))
               (py (rnd-float -50d0 150d0))
               (yaw (rnd-float 0d0 (* 2 pi)))
               (pitch (rnd-float -0.6d0 0.1d0))
               (dir (unit-dir yaw pitch))
               ;; Eye behind and above the player, like PSO's chase cam.
               (ex (coerce (- px (* 60 (first dir))) 'single-float))
               (ey (coerce (+ py 25) 'single-float))
               (ez (coerce (- pz (* 60 (third dir))) 'single-float))
               (zoom (let ((r (rnd 7))) (if (= r 6) nil (1- r)))) ; -1..4, nil
               (camera (list :x ex :y ey :z ez
                             :dir-x (first dir) :dir-y (second dir)
                             :dir-z (third dir) :zoom zoom))
               ;; The ghost somewhere around the player (sometimes
               ;; behind the camera, sometimes far away).
               (gx (+ px (rnd-float -300d0 300d0)))
               (gz (+ pz (rnd-float -300d0 300d0)))
               (gy (if (zerop (rnd 3))
                       ;; The own-y fallback: a single-float height.
                       (coerce (+ py (rnd-float -5d0 5d0)) 'single-float)
                       (+ py (rnd-float -20d0 20d0)))))
          (push (projection-case camera w h gx gy gz) cases))))
    ;; Fixed edge cases from the Lisp tests.
    (let ((camera '(:x 0.0 :y 0.0 :z 0.0 :dir-x 0.0 :dir-y 0.0 :dir-z 1.0 :zoom 1)))
      (push (projection-case camera 1360 768 0d0 0d0 100d0) cases)
      (push (projection-case camera 1360 768 0d0 10d0 100d0) cases)
      (push (projection-case camera 1360 768 10d0 0d0 100d0) cases)
      (push (projection-case camera 1360 768 0d0 0d0 -100d0) cases)
      (push (projection-case camera 1360 768 0d0 0d0 0d0) cases)
      (push (projection-case camera 1361 767 3d0 -2d0 50d0) cases))
    (coerce (nreverse cases) 'vector)))

(defun fov-cases ()
  (coerce
   (loop for zoom in '(nil -1 0 1 2 3 4 5)
         nconc (loop for aspect in (list (/ 1360.0 768.0) (/ 4.0 3.0) 1.25
                                         (/ 16.0 10.0) (/ 1361.0 767.0) 1.5 2.0)
                     collect (obj "zoom" (jnull zoom)
                                  "aspectBits" (sbits aspect)
                                  "fov" (eta-client::camera-fov zoom aspect))))
   'vector))

;;; --- Room alignment ----------------------------------------------------

(defun make-route ()
  "A ghost's room list: a progression over a few floors with revisits,
some kill counts missing (older server) and some zero (corridors)."
  (let ((rooms '()) (ms 0) (seen '()))
    (loop for floor from 1 to 4
          do (loop repeat (+ 10 (rnd 12))
                   do (let ((room (if (and seen (zerop (rnd 5)))
                                      (cdr (nth (rnd (length seen)) seen))
                                      (rnd 40))))
                        (incf ms (+ 500 (rnd 20000)))
                        (push (cons floor room) seen)
                        (push (list floor room ms
                                    (case (rnd 5)
                                      (0 nil)
                                      (1 0)
                                      (t (rnd 9))))
                              rooms))))
    (nreverse rooms)))

(defun route-payload (route)
  (let ((nths (make-hash-table :test 'equal)))
    (obj "run_id" 99 "quest" "ep1-q" "time_ms" (+ 1000 (third (car (last route))))
         "submitter" "teapot" "precision" "ms" "source" "pb" "pb" 0
         "rooms" (coerce
                  (loop for (floor room ms kills) in route
                        collect (let ((h (obj "floor" floor "room" room
                                              "nth" (incf (gethash (cons floor room) nths 0))
                                              "enter_ms" ms)))
                                  (when kills (setf (gethash "kills" h) kills))
                                  h))
                  'vector))))

(defun live-steps (route)
  "A live run over ROUTE: skipped rooms, detours, idle frames in the
same room, a drifting time offset."
  (let ((steps '()) (drift 0) (last-ms 0))
    (dolist (entry route)
      (destructuring-bind (floor room ms kills) entry
        (declare (ignore kills))
        (incf drift (- (rnd 3001) 1500))
        (let ((live-ms (max (1+ last-ms) (+ ms drift 400))))
          (when (zerop (rnd 10))
            (push (list floor (+ 50 (rnd 5)) (max 0 (- live-ms 300))) steps))
          (unless (zerop (rnd 7))
            (push (list floor room live-ms) steps)
            (when (zerop (rnd 4))
              (push (list floor room (+ live-ms 200)) steps)))
          (setf last-ms live-ms))))
    (nreverse steps)))

(defun alignment-case (attach-at)
  (let* ((route (make-route))
         (payload (route-payload route))
         (payload-json (com.inuoe.jzon:stringify payload))
         (ghost (eta-client::parse-ghost-splits (com.inuoe.jzon:parse payload-json)))
         (steps (live-steps route))
         (race (eta-client::make-ghost-race)))
    (obj "payload" payload-json
         "attachAt" attach-at
         "steps" (coerce
                  (loop for (floor room ms) in steps
                        for i from 0
                        do (when (= i attach-at)
                             (setf (eta-client::ghost-race-ghost race) ghost))
                        collect (obj "floor" floor "room" room "ms" ms
                                     "delta" (jnull (eta-client::ghost-race-note-room
                                                     race floor room ms))))
                  'vector)
         "cursor" (eta-client::ghost-race-cursor race)
         "matched" (eta-client::ghost-race-matched-rooms race)
         "splits" (coerce
                   (loop for s in (eta-client::ghost-race-splits race)
                         collect (obj "room" (getf s :room) "floor" (getf s :floor)
                                      "ms" (getf s :ms) "delta" (getf s :delta)))
                   'vector))))

;;; --- Track interpolation -----------------------------------------------

(defun track-case (with-y)
  (let* ((rows '()) (ms (rnd 3000)) (floor 1) (x 0d0) (z 0d0) (y 0d0))
    (dotimes (i 60)
      (push (if with-y
                (list ms floor 10 x z y)
                (list ms floor 10 x z))
            rows)
      (incf ms (case (rnd 12) (0 (+ 3000 (rnd 5000))) (1 (+ 2990 (rnd 20))) (t (+ 200 (rnd 800)))))
      (when (zerop (rnd 15)) (incf floor))
      (setf x (+ x (rnd-float -40d0 40d0))
            z (+ z (rnd-float -40d0 40d0))
            y (+ y (rnd-float -3d0 3d0))))
    (let* ((track (map 'vector (lambda (r) r) (nreverse rows)))
           (last-ms (first (aref track (1- (length track))))))
      (obj "track" track
           "queries"
           (coerce
            (loop repeat 200
                  collect (let ((q (- (rnd (+ last-ms 4000)) 1000)))
                            (multiple-value-bind (f m qx qz qy)
                                (eta-client::ghost-track-position track q)
                              (obj "ms" q
                                   "result" (if f
                                                (vector f m qx qz (jnull qy))
                                                'null)))))
            'vector)))))

;;; --- Placement ---------------------------------------------------------

(defparameter *corners*
  '(:top-right :top-left :bottom-right :bottom-left :middle-right :middle-left
    :top-center :bottom-center :custom :center))

(defun placement-cases ()
  (coerce
   (loop repeat 300
         collect (let* ((area-w (+ 100 (rnd 2500)))
                        (area-h (+ 100 (rnd 1500)))
                        (h (if (zerop (rnd 2)) 224 62))
                        (corner (nth (rnd (length *corners*)) *corners*))
                        (custom (when (plusp (rnd 4))
                                  (list (coerce (rnd-float -0.2d0 1.2d0) 'single-float)
                                        (coerce (rnd-float -0.2d0 1.2d0) 'single-float)))))
                   (multiple-value-bind (x y)
                       (eta-client::overlay-panel-origin corner custom area-w area-h
                                                         260 h 24 16)
                     (obj "corner" (string-downcase (symbol-name corner))
                          "custom" (if custom
                                       (vector (sbits (first custom)) (sbits (second custom)))
                                       'null)
                          "areaW" area-w "areaH" area-h "w" 260 "h" h
                          "result" (vector x y)))))
   'vector))

;;; --- Formatting --------------------------------------------------------

(defun format-cases ()
  (let ((deltas (append (loop for d from -12000 to 12000 by 50 collect d)
                        '(-3210 4400 -1600 1 -1 0 999 -999 1049 1050 1051 1149 1150 1151
                          2500 3500 -2500 -3500 59999 60000 125250 -125350 3600000
                          123456 -123456 99950 -99950))))
    (obj "delta"
         (coerce (loop for d in deltas
                       collect (obj "ms" d
                                    "ms1" (eta-client::format-ghost-delta d :ms)
                                    "sec" (eta-client::format-ghost-delta d :sec)))
                 'vector)
         "clock"
         (coerce (loop for ms in '(0 1 999 1000 59999 60000 61000 754321 3600000 3899999
                                   -1 -1000 -61000)
                       collect (obj "ms" ms
                                    "runTime" (eta-client::format-run-time ms)
                                    "splitClock" (eta-client::format-split-clock ms)))
                 'vector))))

(write-golden "projection.json" (projection-cases))
(write-golden "fov.json" (fov-cases))
(write-golden "alignment.json"
              (coerce (loop for attach in '(0 0 0 5 12 30)
                            collect (alignment-case attach))
                      'vector))
(write-golden "track.json" (vector (track-case nil) (track-case t)))
(write-golden "placement.json" (placement-cases))
(write-golden "format.json" (format-cases))
(format t "ok~%")
