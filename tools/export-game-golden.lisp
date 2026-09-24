;;;; Differential golden for RappyRuns.Core.Game (detector, telemetry, run
;;;; JSON, trigger log, last kill, room picker, quest definitions).
;;;;
;;;; Feeds realistic snapshot sequences through the Lisp client's own
;;;; DETECTOR-STEP (plus the poll frame's LOG-TRIGGER-CHANGES /
;;;; UPDATE-LAST-KILL / UPDATE-RUN-LOGS, in poll-frame-step order) and dumps,
;;;; per step, every emitted run as the exact RUN-JSON string and the run
;;;; plist as prin1 would write it into queue.sexp. The C# port replays the
;;;; same snapshots and must match.
;;;;
;;;; Time is faked: GET-INTERNAL-REAL-TIME returns *NOW* (microseconds, the
;;;; SBCL unit) and GET-UNIVERSAL-TIME a constant, so elapsed times are exact.
;;;;
;;;; Run (from any directory; paths are resolved from this file):
;;;;   sbcl --non-interactive --load ~/quicklisp/setup.lisp \
;;;;     --eval '(push #p"<repo>/client/" asdf:*central-registry*)' \
;;;;     --eval '(ql:quickload :ephinea-ta-client :silent t)' \
;;;;     --load desktop/tools/export-game-golden.lisp

(in-package :ephinea-ta-client)

(defvar *golden-now* 0)
(defparameter +golden-universal-time+ 3967948800)

(sb-ext:without-package-locks
  (defun cl:get-internal-real-time () *golden-now*)
  (defun cl:get-universal-time () +golden-universal-time+))

;; The trigger log's stamp is local wall-clock time; pin it.
(setf (fdefinition 'time-of-day) (lambda () "12:00:00"))

(defparameter *golden-root*
  (merge-pathnames "../" (make-pathname :name nil :type nil
                                        :defaults (or *load-truename* *default-pathname-defaults*))))

(defun golden-path (relative) (merge-pathnames relative *golden-root*))

;;; ------------------------------------------------------------------
;;; Snapshot builders: the plists read-snapshot + augment-snapshot make
;;; ------------------------------------------------------------------

(defun player (&key (index 0) (name "Ryu") (class "HUcast") (section-id "Skyly") (level 100)
                    guild-card npc (name-color #xFFFFFFFF) (floor 1) (room 0)
                    (x 0.0) (y 0.0) (z 0.0) (facing 0) warping (state 1) (current-tech 0)
                    (hp 500) (max-hp 500) (tp 200) (max-tp 200) (meseta 10000) (pb 0.0)
                    (shifta 0) (deband 0) invincible (damage-traps 5) (freeze-traps 5)
                    (confuse-traps 5))
  (list :index index :name name :class class :section-id section-id :level level
        :guild-card guild-card :npc (or npc (npc-guild-card-p guild-card)) :name-color name-color
        :floor floor :room room :x x :y y :z z :facing facing :warping warping :state state
        :current-tech current-tech :hp hp :max-hp max-hp :tp tp :max-tp max-tp :meseta meseta
        :pb pb :shifta shifta :deband deband :invincible invincible
        :damage-traps damage-traps :freeze-traps freeze-traps :confuse-traps confuse-traps))

(defun registers (&rest id-values)
  (let ((bytes (make-array 1024 :element-type '(unsigned-byte 8) :initial-element 0)))
    (loop :for (id value) :on id-values :by #'cddr
          :do (setf (aref bytes (* 4 id)) (ldb (byte 8 0) value)
                    (aref bytes (1+ (* 4 id))) (ldb (byte 8 8) value)))
    bytes))

(defun switches (&rest floor-switch)
  (let ((bytes (make-array (* 32 18) :element-type '(unsigned-byte 8) :initial-element 0)))
    (loop :for (f s) :on floor-switch :by #'cddr
          :for off := (+ (* 32 f) (floor s 8))
          :do (setf (aref bytes off) (logior (aref bytes off) (ash #x80 (- (mod s 8))))))
    bytes))

(defun monster (id hp &key (unitxt 5) (index (+ 4 (mod id 100))) (name "Booma") (last-attacker 0)
                           (x 1.25) (y 0.0) (z -2.5) (facing 0) frozen confused paralyzed)
  (list :id id :unitxt unitxt :index index :name name :hp hp :last-attacker last-attacker
        :x x :y y :z z :facing facing :frozen frozen :confused confused :paralyzed paralyzed))

(defun weapon (id display &optional (type :weapon)) (list :id id :type type :display display))

(defun inventory (&key equipment consumables)
  (list :equipment equipment
        :weapon (find :weapon equipment :key (lambda (e) (getf e :type)))
        :consumables consumables))

(defun lobby-snap (&key (players (list (player :floor 0))))
  (list :episode 1 :my-index 0 :players players :difficulty 0 :map 0 :map-variation 0
        :fast-burst nil :quest-ptr 0))

(defun quest-snap (&key (ptr #x0A000000) (name "Towards the Future") (number 118) (episode 1)
                        (players (list (player))) (difficulty 0) (map 1) (map-variation 0)
                        fast-burst anguish (registers (registers)) (switches (switches))
                        (monsters nil monsters-p) (inventory nil inventory-p))
  (append (list :episode episode :my-index 0 :players players :difficulty difficulty :map map
                :map-variation map-variation :fast-burst fast-burst :quest-ptr ptr
                :quest-name name :quest-number number :anguish anguish
                :registers registers :floor-switches switches)
          (when monsters-p (list :monsters monsters))
          (when inventory-p (list :inventory inventory))))

;;; ------------------------------------------------------------------
;;; JSON encoding of the inputs (what the C# replay parses)
;;; ------------------------------------------------------------------

(defun obj (&rest kvs)
  (let ((h (make-hash-table :test 'equal)))
    (loop :for (k v) :on kvs :by #'cddr :do (setf (gethash k h) v))
    h))

(defun plist-json (plist keys)
  "KEYS: (lisp-key json-key &optional encoder); only keys present in PLIST."
  (let ((h (make-hash-table :test 'equal)))
    (loop :for (key json-key encoder) :in keys
          :for cell := (member key plist)
          :when cell
            :do (let ((v (second cell)))
                  (setf (gethash json-key h)
                        (cond (encoder (funcall encoder v))
                              ((null v) 'null)
                              (t v)))))
    h))

(defun bool (v) (if v t nil))

(defparameter +player-keys+
  '((:index "index") (:name "name") (:class "class") (:section-id "section_id") (:level "level")
    (:guild-card "guild_card") (:npc "npc" bool) (:name-color "name_color") (:floor "floor")
    (:room "room") (:x "x") (:y "y") (:z "z") (:facing "facing") (:warping "warping" bool)
    (:state "state") (:current-tech "current_tech") (:hp "hp") (:max-hp "max_hp") (:tp "tp")
    (:max-tp "max_tp") (:meseta "meseta") (:pb "pb") (:shifta "shifta") (:deband "deband")
    (:invincible "invincible" bool) (:damage-traps "damage_traps") (:freeze-traps "freeze_traps")
    (:confuse-traps "confuse_traps")))

(defparameter +monster-keys+
  '((:id "id") (:unitxt "unitxt") (:index "index") (:name "name") (:hp "hp")
    (:last-attacker "last_attacker") (:x "x") (:y "y") (:z "z") (:facing "facing")
    (:frozen "frozen" bool) (:confused "confused" bool) (:paralyzed "paralyzed" bool)))

(defun registers-json (bytes)
  (if (null bytes)
      'null
      (let ((h (make-hash-table :test 'equal)))
        (dotimes (id 256 h)
          (let ((v (bytes-u16 bytes (* 4 id))))
            (when (plusp v) (setf (gethash (princ-to-string id) h) v)))))))

(defun switches-json (bytes)
  (if (null bytes)
      'null
      (coerce (loop :for i :below (length bytes)
                    :nconc (loop :for bit :below 8
                                 :when (logtest (aref bytes i) (ash #x80 (- bit)))
                                   :collect (vector (floor i 32) (+ (* 8 (mod i 32)) bit))))
              'vector)))

(defun equipment-json (e) (obj "id" (getf e :id) "type" (string-downcase (symbol-name (getf e :type)))
                               "display" (getf e :display)))

(defun inventory-json (inv)
  (if (null inv)
      'null
      (obj "equipment" (coerce (mapcar #'equipment-json (getf inv :equipment)) 'vector)
           "weapon" (if (getf inv :weapon) (equipment-json (getf inv :weapon)) 'null)
           "consumables" (coerce (loop :for (k v) :on (getf inv :consumables) :by #'cddr
                                       :collect (vector (string-downcase (symbol-name k)) v))
                                 'vector))))

(defun snapshot-json (snap)
  (if (null snap)
      'null
      (plist-json snap
                  `((:episode "episode") (:my-index "my_index")
                    (:players "players" ,(lambda (ps) (coerce (mapcar (lambda (p) (plist-json p +player-keys+)) ps) 'vector)))
                    (:difficulty "difficulty") (:map "map") (:map-variation "map_variation")
                    (:fast-burst "fast_burst" bool) (:quest-ptr "quest_ptr") (:quest-name "quest_name")
                    (:quest-number "quest_number") (:anguish "anguish")
                    (:registers "registers" registers-json) (:floor-switches "floor_switches" switches-json)
                    (:monsters "monsters" ,(lambda (ms) (coerce (mapcar (lambda (m) (plist-json m +monster-keys+)) ms) 'vector)))
                    (:inventory "inventory" inventory-json)))))

(defun lisp-print (datum)
  (with-standard-io-syntax
    (let ((*print-readably* nil)
          (*package* (find-package :keyword)))
      (prin1-to-string datum))))

(defun trigger-json-or-null (trigger) (or (trigger->json trigger) 'null))

;;; ------------------------------------------------------------------
;;; Scenario driver: poll-frame-step order for the detection parts
;;; ------------------------------------------------------------------

(defvar *steps*)
(defvar *detector*)
(defvar *previous*)

(defun reset-scenario ()
  (setf *detector* (make-detector) *previous* nil *steps* '()
        *last-kill* nil *run-kill-log* '() *run-switch-log* '() *run-quest* nil))

(defun step! (snapshot &key (dt 33333))
  (incf *golden-now* dt)
  (let* ((runs (detector-step *detector* snapshot))
         (log-lines (let ((out (make-string-output-stream)))
                      (let ((*trigger-log-stream* out))
                        (log-trigger-changes *previous* snapshot))
                      (let ((text (get-output-stream-string out)))
                        (coerce (remove "" (uiop:split-string text :separator (string #\Newline))
                                        :test #'string=)
                                'vector)))))
    (update-last-kill *previous* snapshot)
    (update-run-logs *previous* snapshot)
    (setf *previous* snapshot)
    (push (obj "now" *golden-now*
               "snapshot" (snapshot-json snapshot)
               "runs" (coerce (mapcar (lambda (run)
                                        (obj "run_json" (run-json run)
                                             "sexp" (lisp-print run)))
                                      runs)
                              'vector)
               "state" (string-downcase (symbol-name (detector-state *detector*)))
               "active" (detector-active-count *detector*)
               "log_lines" log-lines
               "last_kill" (if *last-kill*
                               (obj "id" (getf *last-kill* :id)
                                    "name" (or (getf *last-kill* :name) 'null)
                                    "unitxt" (or (getf *last-kill* :unitxt) 'null))
                               'null))
          *steps*)
    runs))

(defun room-rows-json ()
  (coerce (mapcar (lambda (row)
                    (obj "area" (getf row :area)
                         "kind" (string-downcase (symbol-name (getf row :kind)))
                         "name" (or (getf row :name) 'null)
                         "trigger" (lisp-print (getf row :trigger))))
                  (run-room-rows))
          'vector))

(defvar *scenarios* '())

(defmacro defscenario (name (&key server-quests) &body body)
  `(progn
     (load-quest-defs (asdf:system-relative-pathname :ephinea-ta-client "data/quest-triggers.sexp"))
     (set-server-quest-defs (jzon:parse ,(or server-quests "[]")))
     (reset-scenario)
     (setf *golden-now* 1000000000)
     ,@body
     (push (obj "name" ,name
                "server_quests" (jzon:parse ,(or server-quests "[]"))
                "start_now" 1000000000
                "steps" (coerce (reverse *steps*) 'vector)
                "run_quest" (if *run-quest*
                                (obj "number" (or (getf *run-quest* :number) 'null)
                                     "name" (or (getf *run-quest* :name) 'null)
                                     "episode" (or (getf *run-quest* :episode) 'null))
                                'null)
                "room_rows" (room-rows-json))
           *scenarios*)))

;;; ------------------------------------------------------------------
;;; Scenarios
;;; ------------------------------------------------------------------

(defun ttf-players (&key (pb 0.0) warping (hp 500) (meseta 10000) (room 0) (floor 1) (state 1)
                         (x 10.04) (z -3.06) (y 0.55) (tech 0) (shifta 0) (traps 5) (tp 200)
                         (name-color #xFFFFFFFF))
  (list (player :name "Ryu" :class "HUcast" :section-id "Skyly" :level 142 :guild-card "42001234"
                :pb pb :warping warping :hp hp :meseta meseta :room room :floor floor
                :state state :x x :z z :y y :current-tech tech :shifta shifta :tp tp
                :damage-traps traps :freeze-traps traps :confuse-traps (max 0 (- traps 1))
                :name-color name-color)
        (player :index 1 :name "Elly" :class "FOnewearl" :section-id "Whitill" :level 120
                :guild-card "42009999" :floor floor :room room :x 1.5 :y 0.0 :z 2.25 :facing 7)))

;; 1. Towards the Future, full flow with the whole telemetry surface.
(defscenario "ttf-full-flow" ()
  (step! (lobby-snap))
  (step! (quest-snap :players (ttf-players :floor 0) :monsters '() :inventory (inventory)))
  ;; Start register, then ~4 seconds of play at 30 Hz.
  (let ((equipment (list (weapon "00010203" "Charge Vulcan +9 [0/0/0/0|0]")
                         (weapon "00AA0001" "Thirteen [10|10] [4s]" :frame)
                         (weapon "00BB0002" "Red Ring [0|0]" :barrier)
                         (weapon "00CC0003" "Varuna [5/200/40/0]" :mag))))
    (loop :for frame :from 0 :below 130
          :for hp := (cond ((< 60 frame 65) 0) (t (- 500 frame)))
          :for booma-hp := (max 0 (- 200 (* frame 5)))
          :for monsters := (list (monster 1003 booma-hp :last-attacker (if (> frame 20) 1 0)
                                          :x (+ 1.25 (* 0.1 frame)) :frozen (evenp frame))
                                 (monster 1004 (if (< frame 50) 150 0) :name "Rag Rappy" :unitxt 6
                                          :last-attacker 0 :paralyzed t)
                                 (monster 2044 (max 0 (- 5000 (* 30 frame))) :unitxt 44 :index 5
                                          :name "Sil Dragon" :last-attacker 1)
                                 (monster 1010 (if (< frame 3) 0 40) :name "Monest" :unitxt 3))
          :do (step! (quest-snap
                      :registers (registers 12 1)
                      :switches (if (> frame 70) (switches 1 5) (switches))
                      :players (ttf-players :hp hp
                                            :meseta (if (> frame 40) 9900 10000)
                                            :room (floor frame 45)
                                            :state (cond ((< 10 frame 14) 5) ((< 30 frame 33) 8)
                                                         ((< 60 frame 65) 15) (t 1))
                                            :tech #x0F
                                            :traps (if (> frame 80) 3 5)
                                            :tp (if (> frame 31) 180 200)
                                            :x (+ 10.04 (* 0.37 frame)) :z (- -3.06 (* 0.21 frame))
                                            :y (* 0.05 frame)
                                            :shifta (if (> frame 100) 25 0))
                      :map (if (> frame 90) 2 1)
                      :map-variation 3
                      :fast-burst (> frame 120)
                      :monsters monsters
                      :inventory (when (zerop (mod frame 30))
                                   (inventory :equipment equipment
                                              :consumables (list :monomate (- 5 (floor frame 30))
                                                                 :telepipe 2
                                                                 :moon-atomizer (if (> frame 60) 1 3)))))
                     :dt (if (zerop frame) 33333 33000)))
    ;; End register.
    (step! (quest-snap :registers (registers 12 1 254 1) :players (ttf-players) :monsters '()))
    ;; Triggers stay set: no restart.
    (step! (quest-snap :registers (registers 12 1 254 1) :players (ttf-players) :monsters '()))
    (step! (lobby-snap))))

;; 2. PB: a telepipe trip (partial gauge zeroed), a warp across a full gauge,
;; then a second load with a real Photon Blast discharge.
(defscenario "pb-telepipe-and-discharge" ()
  (step! (lobby-snap))
  (step! (quest-snap :players (ttf-players :pb 80.0) :registers (registers 12 1)))
  (step! (quest-snap :players (ttf-players :pb 60.0) :registers (registers 12 1)))
  (step! (quest-snap :players (ttf-players :pb 0.0) :registers (registers 12 1)))
  (step! (quest-snap :players (ttf-players :pb 100.0) :registers (registers 12 1)))
  (step! (quest-snap :players (ttf-players :pb 100.0 :warping t) :registers (registers 12 1)))
  (step! (quest-snap :players (ttf-players :pb 0.0) :registers (registers 12 1)))
  (step! (quest-snap :players (ttf-players :pb 0.0) :registers (registers 12 1 254 1)))
  (step! (lobby-snap))
  (step! (quest-snap :ptr #x0A100000 :players (ttf-players :pb 99.5) :registers (registers 12 1)))
  (step! (quest-snap :ptr #x0A100000 :players (ttf-players :pb 99.5) :registers (registers 12 1)))
  (step! (quest-snap :ptr #x0A100000 :players (ttf-players :pb 49.4) :registers (registers 12 1)))
  (step! (quest-snap :ptr #x0A100000 :players (ttf-players :pb 3.0) :registers (registers 12 1 254 1)) :dt 1500000))

;; 3. Segment category (server-defined) timed alongside the full clear.
(defscenario "segments"
    (:server-quests "[{\"slug\":\"ep1-towards-the-future-2-rooms\",\"episode\":1,\"game_number\":118,\"game_names\":[\"Towards the Future\"],\"start\":{\"type\":\"register\",\"register\":12},\"end\":{\"type\":\"register\",\"register\":100}}]")
  (step! (lobby-snap))
  (step! (quest-snap :players (ttf-players :floor 0)))
  (step! (quest-snap :registers (registers 12 1) :players (ttf-players)))
  (step! (quest-snap :registers (registers 12 1) :players (ttf-players :room 2)) :dt 12345678)
  (step! (quest-snap :registers (registers 12 1 100 1) :players (ttf-players :room 3)) :dt 777777)
  (step! (quest-snap :registers (registers 12 1 100 1) :players (ttf-players :room 4)) :dt 30000000)
  (step! (quest-snap :registers (registers 12 1 100 1 254 1) :players (ttf-players :room 5)) :dt 500)
  (step! (quest-snap :registers (registers 12 1 100 1 254 1) :players (ttf-players :room 5))))

;; 4. Warp-in with a quest NPC in a player slot (Endless Nightmare #1).
(defscenario "warp-in-npc" ()
  (flet ((en1 (my-floor &key (regs (registers)) warping)
           (quest-snap :name "Endless Nightmare #1" :number 108 :registers regs
                       :players (list (player :name "Ryu" :guild-card "42001234" :floor my-floor
                                              :warping warping)
                                      (player :index 1 :name "Mr.X" :class "HUmar" :guild-card "Mr.X"
                                              :floor 1 :shifta 21)))))
    (step! (lobby-snap))
    (step! (en1 0))
    (step! (en1 1 :warping t))
    (step! (en1 1))
    (step! (en1 1) :dt 2000000)
    (step! (en1 1 :regs (registers 30 1)) :dt 61234567)))

;; 5. Abandons: a quick reset (noise), a long run abandoned to the lobby,
;; and a reload (new quest pointer) mid-run that restarts in the same frame.
(defscenario "abandon-and-reload" ()
  (step! (lobby-snap))
  (step! (quest-snap :registers (registers 12 1) :players (ttf-players)))
  (step! (lobby-snap) :dt 3000000)
  (step! (quest-snap :ptr #x0A200000 :registers (registers 12 1) :players (ttf-players)))
  (step! (quest-snap :ptr #x0A200000 :registers (registers 12 1) :players (ttf-players :hp 0)) :dt 10000000)
  (step! (lobby-snap) :dt 10000000)
  (step! (quest-snap :ptr #x0A300000 :registers (registers 12 1) :players (ttf-players)))
  (step! (quest-snap :ptr #x0A300000 :registers (registers 12 1) :players (ttf-players)) :dt 16000000)
  (step! (quest-snap :ptr #x0A400000 :registers (registers 12 1) :players (ttf-players)))
  (step! (quest-snap :ptr #x0A400000 :registers (registers 12 1 254 1) :players (ttf-players)) :dt 42424242))

;; 6. The game disappears mid-run (snapshot NIL), then a fresh attach.
(defscenario "game-exit" ()
  (step! (lobby-snap))
  (step! (quest-snap :registers (registers 12 1) :players (ttf-players)))
  (step! (quest-snap :registers (registers 12 1) :players (ttf-players)) :dt 15999999)
  (step! nil)
  ;; Not armed after a NIL: the still-loaded quest must not start.
  (step! (quest-snap :registers (registers 12 1) :players (ttf-players)))
  (step! (lobby-snap))
  (step! (quest-snap :ptr #x0A500000 :registers (registers 12 1) :players (ttf-players)))
  (step! (quest-snap :ptr #x0A500000 :registers (registers 12 1) :players (ttf-players)) :dt 20000000)
  (step! nil))

;; 7. Anguish 2 on Ultimate.
(defscenario "anguish" ()
  (step! (lobby-snap))
  (step! (quest-snap :difficulty 3 :anguish 2 :registers (registers 12 1) :players (ttf-players)))
  (step! (quest-snap :difficulty 3 :anguish 2 :registers (registers 12 1 254 1) :players (ttf-players))
         :dt 123456789))

;; 8. A New Hope Gal Gryphon (builtin (:monster-dead 5475)), NPC partners,
;; a Gal Gryphon that spawns at 0 hp first and the real kill.
(defscenario "monster-clear-a-new-hope" ()
  (flet ((anh (monsters &key (floor 1))
           (quest-snap :name "A New Hope" :number 0 :episode 2 :map 30
                       :players (list (player :name "Ryu" :class "RAmar" :guild-card "42001234" :floor floor)
                                      (player :index 1 :name "Rico" :class "FOnewearl" :guild-card "Rico" :floor 1)
                                      (player :index 2 :name "Heathcliff" :class "HUcast" :guild-card "Heatclif" :floor 1))
                       :monsters monsters)))
    (step! (lobby-snap))
    (step! (anh '() :floor 0))
    (step! (anh (list (monster 5474 0 :unitxt 77 :name "Gal Gryphon" :index 20)
                      (monster 5475 0 :unitxt 77 :name "Gal Gryphon" :index 21))))
    (step! (anh (list (monster 5474 9000 :unitxt 77 :name "Gal Gryphon" :index 20 :last-attacker 0)
                      (monster 5475 0 :unitxt 77 :name "Gal Gryphon" :index 21))))
    (step! (anh (list (monster 5474 0 :unitxt 77 :name "Gal Gryphon" :index 20 :last-attacker 1)
                      (monster 5475 0 :unitxt 77 :name "Gal Gryphon" :index 21))) :dt 30000000)
    (step! (anh (list (monster 5474 0 :unitxt 77 :name "Gal Gryphon" :index 20)
                      (monster 5475 9500 :unitxt 77 :name "Gal Gryphon" :index 21))) :dt 5000000)
    (step! (anh (list (monster 5474 0 :unitxt 77 :name "Gal Gryphon" :index 20)
                      (monster 5475 0 :unitxt 77 :name "Gal Gryphon" :index 21 :last-attacker 0))) :dt 40000000)))

;; 9. Account mode across loads: no colour, then white, then sandbox; an
;; unknown colour; a second load on a normal account.
(defscenario "account-mode-transitions" ()
  (step! (lobby-snap))
  (step! (quest-snap :registers (registers 12 1) :players (ttf-players :name-color 0)))
  (step! (quest-snap :registers (registers 12 1) :players (ttf-players :name-color #xFFFFFFFF)))
  (step! (quest-snap :registers (registers 12 1) :players (ttf-players :name-color #xDEADBEEF)))
  (step! (quest-snap :registers (registers 12 1) :players (ttf-players :name-color #xFFAB9423)))
  (step! (quest-snap :registers (registers 12 1) :players (ttf-players :name-color #xFFFFFFFF)))
  (step! (quest-snap :registers (registers 12 1 254 1) :players (ttf-players :name-color 0)) :dt 2000000)
  (step! (lobby-snap))
  (step! (quest-snap :ptr #x0A600000 :registers (registers 12 1) :players (ttf-players :name-color #x00FF0000)))
  (step! (quest-snap :ptr #x0A600000 :registers (registers 12 1 254 1) :players (ttf-players :name-color #x80FFFFFF)) :dt 3000000))

;; 10. GDV reset (server-defined, floor switches) alongside the full clear,
;; with register/switch churn for the trigger log and the room picker.
(defscenario "gdv-reset-floor-switches"
    (:server-quests "[{\"slug\":\"ep2-gdv-reset\",\"episode\":2,\"game_number\":944,\"start\":{\"type\":\"floor-switch\",\"floor\":5,\"switch\":0},\"end\":{\"type\":\"floor-switch\",\"floor\":5,\"switch\":2}},{\"slug\":\"ep1-display-only\",\"episode\":1}]")
  (flet ((gdv (sw regs room monsters)
           (quest-snap :name "Maximum Attack E: Gal Da Val" :number 944 :episode 2 :map 30
                       :switches sw :registers regs :monsters monsters
                       :players (list (player :name "Ryu" :guild-card "42001234" :floor 5 :room room)))))
    (step! (lobby-snap))
    (step! (gdv (switches) (registers) 1 (list (monster 11 100 :unitxt 64 :name "Dolmolm"))))
    (step! (gdv (switches 5 0) (registers 7 3) 1 (list (monster 11 100 :unitxt 64 :name "Dolmolm"))))
    (step! (gdv (switches 5 0) (registers 7 4) 1 (list (monster 11 0 :unitxt 64 :name "Dolmolm")
                                                       (monster 12 80 :unitxt 64 :name "Dolmolm"))) :dt 4000000)
    (step! (gdv (switches 5 0 5 9) (registers 7 4) 2 (list (monster 12 0 :unitxt 64 :name nil))))
    (step! (gdv (switches 5 0 5 9 5 2) (registers 7 4 50 1) 2 (list (monster 13 70 :unitxt 65 :name "Recon"))) :dt 9000000)
    (step! (gdv (switches 5 9 5 2) (registers 7 4 50 1) 3 (list (monster 13 0 :unitxt 65 :name "Recon"))))
    (step! (gdv (switches 5 9 5 2) (registers 7 4 50 1 254 1) 3 '()) :dt 60000000)
    (step! (lobby-snap))))

;; 11. Attached mid-quest (no lobby first): never timed.
(defscenario "mid-quest-attach" ()
  (step! (quest-snap :registers (registers 12 1) :players (ttf-players)))
  (step! (quest-snap :registers (registers 12 1 254 1) :players (ttf-players)) :dt 30000000)
  (step! (lobby-snap))
  (step! (quest-snap :ptr #x0A700000 :registers (registers) :players (ttf-players)))
  (step! (quest-snap :ptr #x0A700000 :registers (registers 12 1) :players (ttf-players)))
  (step! (quest-snap :ptr #x0A700000 :registers (registers 12 1 254 1) :players (ttf-players)) :dt 1))

;;; ------------------------------------------------------------------
;;; Quest definitions as the Lisp reads them
;;; ------------------------------------------------------------------

(defun defs-json ()
  (load-quest-defs (asdf:system-relative-pathname :ephinea-ta-client "data/quest-triggers.sexp"))
  (set-server-quest-defs (vector))
  (coerce (mapcar (lambda (def)
                    (obj "slug" (quest-def-slug def)
                         "episode" (or (quest-def-episode def) 'null)
                         "names" (coerce (quest-def-names def) 'vector)
                         "number" (or (quest-def-number def) 'null)
                         "start" (trigger-json-or-null (quest-def-start def))
                         "end" (trigger-json-or-null (quest-def-end def))))
                  *quest-defs*)
          'vector))

(ensure-directories-exist (golden-path "tests/RappyRuns.Tests/golden/game/"))

(with-open-file (out (golden-path "tests/RappyRuns.Tests/golden/game/detector-scenarios.json")
                     :direction :output :if-exists :supersede :external-format :utf-8)
  (write-string (jzon:stringify (obj "ticks_per_second" internal-time-units-per-second
                                     "universal_time" +golden-universal-time+
                                     "log_stamp" "12:00:00"
                                     "scenarios" (coerce (reverse *scenarios*) 'vector))
                                :pretty t)
                out))

(with-open-file (out (golden-path "tests/RappyRuns.Tests/golden/game/quest-defs.json")
                     :direction :output :if-exists :supersede :external-format :utf-8)
  (write-string (jzon:stringify (defs-json) :pretty t) out))

;; jzon's single-float text (Schubfach) for the values telemetry can carry.
(with-open-file (out (golden-path "tests/RappyRuns.Tests/golden/game/json-floats.json")
                     :direction :output :if-exists :supersede :external-format :utf-8)
  (write-string
   (jzon:stringify
    (coerce (loop :for f :in (list 0.0 -0.0 1.0 -1.0 10.0 12.3 -3.1 0.1 0.2 0.3 1.2 2.2 99.5 100.0 123456.7
                                   1234567.0 9999999.0 1.0e7 1.5e7 12345678.0 0.001 0.00099 0.0001 1.0e-5
                                   3.4e38 3.4028235e38 1.17549435e-38 1.4e-45 -12.3 16777216.0 0.33333334
                                   5.9604645e-8 65504.0 1.0e10 7.0e-4)
                  :collect (obj "bits" (ldb (byte 32 0) (sb-kernel:single-float-bits f))
                                "text" (jzon:stringify f)))
            'vector)
    :pretty t)
   out))

(format t "~&game golden: ~d scenarios, ~d steps~%"
        (length *scenarios*)
        (reduce #'+ *scenarios* :key (lambda (s) (length (gethash "steps" s)))))
