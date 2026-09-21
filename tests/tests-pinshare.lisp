(in-package :ephinea-ta-client-tests)

;;; ------------------------------------------------------------------
;;; Pin Share relay: the pure half (pinshare.lisp). The file formats are
;;; a contract with the Lua addon (data/pin-share/init.lua readInbox /
;;; sendCommand) and the JSON with the relay server, so the expectations
;;; here are spelled out literally.
;;; ------------------------------------------------------------------

(defun pinshare-tab-line (&rest fields)
  (format nil "~a~%" (ephinea-ta-client::pinshare-join fields)))

(defun pinshare-parsed (json)
  (com.inuoe.jzon:parse json))

(defun run-pinshare-tests ()
  (format t "~&--- pin share relay ---~%")
  (let ((tab (string #\Tab)))
    ;; out.txt parsing
    (check "complete out.txt lines parse into (seq . fields)"
           (equal (ephinea-ta-client::parse-pinshare-outbox
                   (concatenate 'string
                                (pinshare-tab-line 10 "name" "Teapot")
                                (pinshare-tab-line 11 "clear_mine")))
                  '((10 "10" "name" "Teapot") (11 "11" "clear_mine"))))
    (check "a line the addon is still writing is left for the next read"
           (equal (ephinea-ta-client::parse-pinshare-outbox
                   (concatenate 'string
                                (pinshare-tab-line 10 "clear_mine")
                                "11" tab "add" tab "1"))
                  '((10 "10" "clear_mine"))))
    (check "out.txt without any newline yields nothing"
           (null (ephinea-ta-client::parse-pinshare-outbox
                  (concatenate 'string "10" tab "clear_mine"))))
    (check "CRLF line ends and junk lines are tolerated"
           (equal (ephinea-ta-client::parse-pinshare-outbox
                   (format nil "garbage~%12~cremove~c5~c~%~%"
                           #\Tab #\Tab #\Return))
                  '((12 "12" "remove" "5"))))

    ;; number parsing
    (check "the addon's %.3f coordinates parse exactly"
           (and (= (ephinea-ta-client::parse-pinshare-number "-123.456")
                   -123.456d0)
                (= (ephinea-ta-client::parse-pinshare-number "0.000") 0d0)
                (= (ephinea-ta-client::parse-pinshare-number "1e3") 1000d0)))
    (check "nan, inf, empty and trailing junk are refused"
           (notany #'ephinea-ta-client::parse-pinshare-number
                   '("nan" "inf" "-nan(ind)" "" "-" "." "1.5x" "1e" "#.(quit)")))
    (check "integers accept an integral decimal only"
           (and (eql (ephinea-ta-client::parse-pinshare-integer "60") 60)
                (eql (ephinea-ta-client::parse-pinshare-integer "60.000") 60)
                (null (ephinea-ta-client::parse-pinshare-integer "60.5"))))

    ;; command translation
    (let ((message (pinshare-parsed
                    (ephinea-ta-client::pinshare-command-message
                     '("1" "add" "10203" "1.500" "0.000" "-2.250" "60"
                       "集合" "3" "7")))))
      (check "add carries floor, point, ttl, label, max and room"
             (and (equal (gethash "t" message) "add")
                  (eql (gethash "floor" message) 10203)
                  (= (gethash "x" message) 1.5d0)
                  (= (gethash "z" message) -2.25d0)
                  (eql (gethash "ttl" message) 60)
                  (equal (gethash "label" message) "集合")
                  (eql (gethash "max" message) 3)
                  (eql (gethash "room" message) 7))))
    (let ((message (pinshare-parsed
                    (ephinea-ta-client::pinshare-command-message
                     '("1" "add" "1" "1.000" "2.000" "3.000" "0")))))
      (check "an old addon's short add omits max and room"
             (and (equal (gethash "label" message) "")
                  (null (nth-value 1 (gethash "max" message)))
                  (null (nth-value 1 (gethash "room" message))))))
    (check "an add with a broken coordinate is dropped"
           (null (ephinea-ta-client::pinshare-command-message
                  '("1" "add" "1" "nan" "2.000" "3.000" "0" "" "3"))))
    (let ((message (pinshare-parsed
                    (ephinea-ta-client::pinshare-command-message
                     '("1" "arrow_add" "5" "1.000" "2.000" "3.000"
                       "4.000" "5.000" "6.000" "30" "3"
                       "2.500" "3.500" "9.000" "12")))))
      (check "arrow_add carries both ends, the bend point and the room"
             (and (equal (gethash "t" message) "arrow_add")
                  (= (gethash "x1" message) 1d0)
                  (= (gethash "z2" message) 6d0)
                  (= (gethash "zm" message) 9d0)
                  (eql (gethash "ttl" message) 30)
                  (eql (gethash "max" message) 3)
                  (eql (gethash "room" message) 12))))
    (let ((message (pinshare-parsed
                    (ephinea-ta-client::pinshare-command-message
                     '("1" "arrow_move" "8" "1.000" "2.000" "3.000"
                       "4.000" "5.000" "6.000")))))
      (check "arrow_move without a bend point sends the ends only"
             (and (eql (gethash "id" message) 8)
                  (null (nth-value 1 (gethash "xm" message))))))
    (check "remove / arrow_remove / clear_* translate"
           (and (equal (gethash "id" (pinshare-parsed
                                      (ephinea-ta-client::pinshare-command-message
                                       '("1" "arrow_remove" "4"))))
                       4)
                (equal (gethash "t" (pinshare-parsed
                                     (ephinea-ta-client::pinshare-command-message
                                      '("1" "clear_all"))))
                       "clear_all")))
    (check "an unknown command is ignored"
           (null (ephinea-ta-client::pinshare-command-message
                  '("1" "teleport" "1"))))

    ;; relay state machine
    (let ((relay (ephinea-ta-client::make-pinshare-relay :channel "secret")))
      (ephinea-ta-client::pinshare-relay-skip-backlog
       relay '((5 "5" "name" "Teapot") (6 "6" "color" "FF8C00")
               (7 "7" "add" "1" "1.000" "2.000" "3.000" "60" "" "3")))
      (check "the start-up backlog keeps identity and skips stale pins"
             (and (= (ephinea-ta-client::pinshare-relay-last-seq relay) 7)
                  (equal (ephinea-ta-client::pinshare-relay-name relay)
                         "Teapot")
                  (equal (ephinea-ta-client::pinshare-relay-color relay)
                         "FF8C00")))
      (check "the backlog is not proof the addon is alive now"
             (not (ephinea-ta-client::pinshare-relay-addon-seen relay)))
      (let ((hello (mapcar #'pinshare-parsed
                           (ephinea-ta-client::pinshare-hello-messages relay))))
        (check "hello introduces channel and name, then the pin color"
               (and (= (length hello) 2)
                    (equal (gethash "channel" (first hello)) "secret")
                    (equal (gethash "name" (first hello)) "Teapot")
                    (equal (gethash "color" (second hello)) "FF8C00"))))
      (check "already-handled seqs are not replayed (and prove nothing)"
             (and (null (ephinea-ta-client::pinshare-relay-consume
                         relay '((7 "7" "clear_all")) t))
                  (not (ephinea-ta-client::pinshare-relay-addon-seen relay))))
      (check "commands arriving while disconnected are dropped for good"
             (and (null (ephinea-ta-client::pinshare-relay-consume
                         relay '((8 "8" "clear_all")) nil))
                  ;; ...but a new command is the addon speaking to us
                  (ephinea-ta-client::pinshare-relay-addon-seen relay)
                  (null (ephinea-ta-client::pinshare-relay-consume
                         relay '((8 "8" "clear_all")) t))))
      (let ((messages (ephinea-ta-client::pinshare-relay-consume
                       relay '((9 "9" "arrow_color" "")
                               (10 "10" "name" "Kettle"))
                       t)))
        (check "an empty arrow color is sent; a rename re-sends hello + colors"
               (and (= (length messages) 4)
                    (equal (gethash "t" (pinshare-parsed (first messages)))
                           "arrow_color")
                    (equal (gethash "name" (pinshare-parsed (second messages)))
                           "Kettle")
                    (equal (gethash "t" (pinshare-parsed (fourth messages)))
                           "arrow_color"))))

      ;; server -> in.txt
      (setf (ephinea-ta-client::pinshare-relay-session relay) "abcd1234")
      (ephinea-ta-client::pinshare-relay-set-status relay "connected" "")
      (ephinea-ta-client::pinshare-relay-note-message
       relay
       "{\"t\":\"state\",\"pins\":[{\"id\":3,\"owner\":\"Tea\\tpot\",\"floor\":10203,\"x\":1.5,\"y\":0,\"z\":-2.25,\"label\":\"集合\",\"no\":4,\"ownerNo\":2,\"room\":7,\"roomNo\":1,\"color\":\"ff8c00\",\"remaining\":42},{\"id\":4,\"owner\":\"Old\",\"floor\":1,\"x\":1,\"y\":2,\"z\":3,\"label\":\"\",\"no\":5,\"ownerNo\":1,\"room\":null,\"roomNo\":null,\"color\":\"\",\"remaining\":-1},{\"id\":\"bad\",\"floor\":1,\"x\":1,\"y\":2,\"z\":3}],\"arrows\":[{\"id\":9,\"owner\":\"Kettle\",\"floor\":5,\"room\":12,\"x1\":1,\"y1\":2,\"z1\":3,\"x2\":4,\"y2\":5,\"z2\":6,\"xm\":2.5,\"ym\":3.5,\"zm\":9,\"color\":\"66e0ff\",\"remaining\":30}],\"members\":[\"Teapot\",\"Kettle\"]}")
      (check "in.txt renders exactly what the addon's readInbox expects"
             (string=
              (ephinea-ta-client::render-pinshare-inbox relay 1700000000)
              (concatenate
               'string
               (pinshare-tab-line "session" "abcd1234")
               (pinshare-tab-line "time" 1700000000)
               (pinshare-tab-line "ack" 10)
               (pinshare-tab-line "status" "connected" "")
               (pinshare-tab-line "channel" "secret")
               (pinshare-tab-line "member" "Teapot")
               (pinshare-tab-line "member" "Kettle")
               (pinshare-tab-line "pin" 3 "Teapot" 10203 "1.5" "0" "-2.25"
                                  42 "集合" 4 2 "ff8c00" 1 7)
               (pinshare-tab-line "pin" 4 "Old" 1 "1" "2" "3"
                                  -1 "" 5 1 "" "" "")
               (pinshare-tab-line "arrow" 9 "Kettle" 5 "1" "2" "3" "4" "5" "6"
                                  30 "66e0ff" "2.5" "3.5" "9" 12)
               (pinshare-tab-line "end"))))
      (check "the rendered text ends the way readInbox's terminator check wants"
             (let ((text (ephinea-ta-client::render-pinshare-inbox relay 1)))
               (string= (subseq text (- (length text) 5))
                        (format nil "~%end~%"))))
      (check "a server error message is returned for the log, state untouched"
             (and (equal (ephinea-ta-client::pinshare-relay-note-message
                          relay "{\"t\":\"error\",\"message\":\"rate limited\"}")
                         "rate limited")
                  (= (length (ephinea-ta-client::pinshare-relay-pins relay)) 3)))
      (check "garbage from the server is ignored"
             (null (ephinea-ta-client::pinshare-relay-note-message
                    relay "not json")))
      (ephinea-ta-client::pinshare-relay-clear-state relay)
      (check "a disconnect empties the lists the addon draws from"
             (and (zerop (length (ephinea-ta-client::pinshare-relay-pins relay)))
                  (ephinea-ta-client::pinshare-relay-dirty relay))))

    ;; number formatting for Lua's tonumber
    (check "numbers never carry a Lisp float marker"
           (and (string= (ephinea-ta-client::pinshare-format-number 12) "12")
                (string= (ephinea-ta-client::pinshare-format-number -2.25d0)
                         "-2.25")
                (string= (ephinea-ta-client::pinshare-format-number 1.5f0)
                         "1.5")
                (notany #'alpha-char-p
                        (remove #\e (ephinea-ta-client::pinshare-format-number
                                     1.0d-7)))))

    ;; the old PowerShell relay
    (let ((inbox (concatenate 'string
                              (pinshare-tab-line "session" "feedbeef")
                              (pinshare-tab-line "time" 1000)
                              (pinshare-tab-line "end"))))
      (check "a fresh heartbeat from another relay is a conflict"
             (ephinea-ta-client::pinshare-foreign-relay-p inbox '("abcd1234") 1002))
      (check "our own earlier session is not"
             (not (ephinea-ta-client::pinshare-foreign-relay-p
                   inbox '("abcd1234" "feedbeef") 1002)))
      (check "a stale heartbeat is not"
             (not (ephinea-ta-client::pinshare-foreign-relay-p
                   inbox '("abcd1234") 1010)))
      (check "an empty in.txt is not"
             (not (ephinea-ta-client::pinshare-foreign-relay-p "" '() 1002))))

    ;; settings
    (let ((ephinea-ta-client::*config*
            (list :pinshare-channel (format nil "  se~ccret  " #\Tab))))
      (check "the passphrase is cleaned and trimmed like the server does"
             (string= (ephinea-ta-client::pinshare-channel) "secret"))
      (check "a blank server setting means the public relay"
             (string= (ephinea-ta-client::pinshare-server-url)
                      ephinea-ta-client::+pinshare-default-server+)))
    (check "the addon folder sits next to the game exe"
           (equal (pathname-directory
                   (ephinea-ta-client::pinshare-addon-dir
                    "C:/Games/EphineaPSO/PsoBB.exe"))
                  '(:absolute "Games" "EphineaPSO" "addons" "Pin Share")))
    (check "ws / wss URLs get the right default ports"
           (and (equal (multiple-value-list
                        (ephinea-ta-client::parse-websocket-url
                         "wss://relay.example/ws"))
                       '(t "relay.example" 443 "/ws"))
                (equal (multiple-value-list
                        (ephinea-ta-client::parse-websocket-url
                         "ws://localhost:8787"))
                       '(nil "localhost" 8787 "/"))))
    (check "every relay status has a line in both languages"
           (every (lambda (status)
                    (every (lambda (language)
                             (let ((ephinea-ta-client::*language* language))
                               (plusp (length (ephinea-ta-client::pinshare-status-text
                                               status)))))
                           '(:en :ja)))
                  '((:off) (:no-channel) (:waiting-game) (:connecting)
                    (:connected "secret" 2) (:connected-no-addon)
                    (:error "boom")
                    (:no-addon-plugin) (:install-failed "denied")
                    (:broken-link "C:\\Games\\addons\\Pin Share")
                    (:conflict))))))
