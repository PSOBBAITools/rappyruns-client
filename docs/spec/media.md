# メディア系サブシステム移植仕様 (録画 / オーディオ / WGC / オーバーレイ / ゴースト)

対象: LispWorks 版 Rappy Runs クライアント (`client/src`) → C# (.NET + WebView2) 版。
目的: Lisp を読み直さずに挙動を完全再現できること。挙動差分を入れる場合は「意図的変更」として明示すること。

- 出典ファイル (行番号は 2026-09-24 時点の main `27d3766`):
  - `recording.lisp` (1435 行) — 録画ステートマシン・argv 生成・純関数群
  - `ffmpeg-win32.lisp` (1026 行) — プロセス起動・プローブ・モニター解決・ライブバックエンド
  - `wgc-win32.lisp` (627 行) — Windows.Graphics.Capture ウィンドウキャプチャ
  - `audio-win32.lisp` (821 行) — WASAPI プロセスループバック / エンドポイントループバック
  - `overlay-win32.lisp` (1292 行) — ゲーム上オーバーレイ
  - `ghost.lisp` (552 行) — ゴーストレース (取得・部屋整列・投影・配置)
  - 駆動側: `main.lisp` (poll loop), `gui.lisp` (4Hz ステータス更新・アップロードボタン), `detect.lisp` (状態遷移), `store.lisp` (キュー・アップロード・保持), `api-client.lisp`, `psobb.lisp` (カメラアドレス), `win32.lisp`, `winhttp.lisp`, `config.lisp`, `i18n.lisp`
  - テスト: `client/tests/tests-recorder.lisp`, `client/tests/tests-ghost.lisp`
- 表記: `NAME` は Lisp 関数/変数名、`file.lisp:行` は定義行。「必須」= パリティ要件、「推奨」= C# での実装指針。
- 数値の丸め: Common Lisp の `round` は **偶数丸め (banker's)**。C# `Math.Round` の既定 (`MidpointRounding.ToEven`) と一致するので **既定のまま使うこと** (`AwayFromZero` にしない)。`floor` は負方向への切り捨て (C# の整数除算 `/` はゼロ方向なので負数で異なる → `Math.Floor` か `Math.DivRem` 補正を使う)。

---

## 0. 全体像とスレッド構成

| スレッド (Lisp 名) | 役割 | C# 対応 (推奨) |
|---|---|---|
| `eta-client-poll` (`poll-loop`, main.lisp:380) | 30Hz ポール。スナップショット→検出→`recorder-step`→ゴースト→(4Hz) GUI/アップロード/保持/プローブ | 専用 `Thread` (高精度タイマー)。`Task` のスレッドプールに乗せない |
| `eta-hw-encoder-probe` | HW エンコーダ + QSV ゼロコピーチェーンのプローブ | 背景 Task |
| `eta-gdigrab-probe` | gdigrab 可読性プローブ | 背景 Task |
| `eta-wgc-capture` | WGC フレーム→名前付きパイプ給餌 | 専用 Thread (タイミング精度が要る) |
| `eta-audio-capture` | WASAPI→名前付きパイプ給餌 (MTA COM) | 専用 Thread (MTA) |
| `eta-ffmpeg-stop` | 音声 EOF 後 3 秒待って `q` 送信 | 背景 Task |
| `eta-client-overlay` | オーバーレイウィンドウ + メッセージループ | 専用 STA Thread + Win32 メッセージポンプ |
| `eta-client-ghost-fetch` | ゴースト取得 HTTP | 背景 Task |
| `eta-client-video-upload` | 動画アップロード (1 本ずつ) | 背景 Task |

共有状態は「単一変数の差し替え (新規に cons した値)」で受け渡す方針 (`*live-camera*`, `*ghost*`, `*overlay-ghost-data*` 等)。C# では不変オブジェクトの参照を `Volatile.Write`/`Interlocked.Exchange` で差し替える形が等価。**オーバーレイスレッドは config を直接読まない** (値は `overlay-show!` の引数で渡る) — この境界は維持すること。

### 0.1 poll loop 1 フレームの順序 (`poll-frame-step`, main.lisp:298) — 順序は必須

1. `read-snapshot` → `augment-snapshot` (クエストロード中のみ `:monsters`、`:ghost-overlay` かつ `:ghost-marker` 有効時のみ `:camera` を毎フレーム、`:inventory` は 1 秒毎)
2. `runs = detector-step(detector, snapshot)`
3. `recorder-step(recorder, detector.state, runs, reader.window-title)` ← **runs の enqueue より前**。ここで各 run に `:video-offset-ms` が破壊的に追記され (NCONC)、それが後段で送信キューに乗る
4. `maybe-start-ghost-fetch(snapshot)` / `maybe-start-pin-set-fetch` / `ghost-race-step(detector, snapshot)`
5. runs があれば `run-completion-sounds` → `handle-completed-runs` (`annotate-ghost-runs` → `enqueue-run!` → 自動 submit) → 一覧更新
6. トリガーログ・キル追跡など
7. 前回 GUI 更新から 250ms 超なら (4Hz): `maybe-start-upload` → `maybe-sweep-recordings` → (`recording-enabled-p` かつ `*poll-busy-p*` 偽なら) `maybe-start-gdigrab-probe(window-title)` → `update-game-status` (内部で `update-ghost-overlay`)
8. `1/30` 秒待機 (停止要求で即起床)

未アタッチ時 (`poll-search-step`, main.lisp:237): 1 秒毎にゲーム探索。見つからない間も `recorder-step(recorder, detector.state, '(), nil)` を呼び停止処理を進め、アップロード・保持スイープを回す。アタッチ時に `*audio-target-pid*` = PSOBB の PID。プロセス死亡時 (`poll-detach-step`) は `*audio-target-pid*` を nil にし、`detector-step(nil)` の後に `recorder-step` を 1 回。

`*poll-busy-p*` = `detector.state == :in-quest` **or** `recorder.state == :recording` (`note-poll-activity`, main.lisp:215)。

終了時: `recorder-shutdown` (§1.10)。単一インスタンスガード (`already-running-p`) が前提 — パイプ名が固定 (§1.6/§2) なので **2 インスタンス同時録画は不可** (必須: 単一インスタンス維持)。

---

## 1. 録画パイプライン

### 1.1 設定キー (config.lisp:13)

| キー | 既定 | 備考 |
|---|---|---|
| `:record-enabled` | t | **強制キー** (GUI なし、保存値は migrate で削除)。常に録画 |
| `:video-upload` | t | 強制キー。常に自動アップロード |
| `:tracking-only` | nil | 真なら録画しない (`recording-enabled-p` = `record-enabled && !tracking-only`, recording.lisp:1289) |
| `:record-audio` | t | ゲーム音声を録る |
| `:hw-encode` | t | GPU エンコーダを使う |
| `:ffmpeg-path` | "" | 空=exe 隣 `ffmpeg/ffmpeg.exe`、無ければ `ffmpeg.exe` (PATH) (`resolve-ffmpeg-path`, :488) |
| `:record-dir` | "" | 空=`%USERPROFILE%\Videos\RappyRuns\` (`resolve-record-dir`, :466) |
| `:record-max-total-gb` | 20 | 録画フォルダ上限。0/空=無制限。GB=1024^3 |
| `:wgc-disable` | (未定義) | 隠しエスケープハッチ。真なら WGC を使わない |
| `:auto-publish` | nil | サーバー側フラグのキャッシュ (§1.13) |

### 1.2 レコーダー状態機械 (`recorder-step`, recording.lisp:1296)

構造体 `recorder` (:973): `backend, state(:idle|:recording|:stopping|:remuxing), capture, capture-start-real, tmp-path, session-runs, run-end-ms, last-detector-state(:idle), stop-deadline, pending-keep-p, pending-run, final-path, remux-capture, remux-deadline, on-keep, last-error`。

毎フレーム:

1. **先に run を計上**: `completed-runs` 非空かつ state ∈ {:recording, :stopping} なら `note-run-video-timing` (§1.9) → `session-runs` に追記。(完走フレームで検出器が同時に :idle になるため、停止判定より前に必須)
2. state 別:
   - `:idle`: `detector-state == :in-quest` **かつ** 前フレームが :in-quest でない (エッジトリガ) **かつ** `recording-enabled-p` **かつ** `window-title` 非 nil → `start-recording`。失敗しても :idle のまま、**同じクエスト中は再試行しない** (次のクエストで再試行)。クエスト途中で tracking-only をオフにしても開始しない。
   - `:recording`: ffmpeg が死んでいたら `abort-capture("ffmpeg exited unexpectedly")` (tmp 削除・エラー表示)。そうでなく `detector-state == :idle` なら `begin-stop`。
   - `:stopping`: ffmpeg 死亡 → `finalize-capture`。`stop-deadline` 到達 → `backend-kill-capture` (TerminateProcess, 1 回だけ。deadline を nil に)。
   - `:remuxing`: remux 死亡 → `finish-remux`。`remux-deadline` 到達 → kill (1 回)。次フレームで `finish-remux` が失敗扱いにする。
3. `last-detector-state = detector-state`。

定数: `+stop-grace-seconds+`=8 (:319), `+remux-grace-seconds+`=180 (:324), `+keep-tail-seconds+`=2 (:331)。

`begin-stop` (:1162): `best = best-session-run(session-runs)`。`pending-keep-p = best != nil`、`final-path = deduplicate-path(record-dir + run-video-filename(best))`、`stop-deadline = now + 8s`、state=:stopping、`backend-request-stop` (例外は無視)。
`finalize-capture` (:1194): `backend-close-capture`→ keep なら `begin-remux`、でなければ tmp 削除 + `reset-recorder`。
`begin-remux` (:1206): **duration はここで計算** (stopping 中に届いた run も含めるため)。`build-remux-args(tmp, final, duration-ms = session-video-duration-ms(run-end-ms))` で起動。起動失敗なら即 `save-recording(remuxed=nil)`。成功なら deadline = now+180s、state=:remuxing。
`finish-remux` (:1234): exit code 0 なら ok。ok でなければ final-path (部分出力) を削除。→ `save-recording(remuxed=ok)`。
`save-recording` (:1246): remuxed なら tmp 削除、でなければ tmp を final-path へ rename (上書き)。**remuxed でない場合**: `last-error = "remux failed; recording kept whole - its tail is untrimmed"` + トレイ通知 `:notify-untrimmed-*`。その後 `on-keep(final-path, pending-run)` (例外は握りつぶす)。C# は on-keep に untrimmed フラグも渡し、エントリに `:untrimmed t` を付けて自動アップロードの対象から外す (S07。Lisp はデスクトップが写りうる末尾ごと自動アップロードしていた)。ファイルはローカルに残してリンクする (Video 列は `video-untrimmed` "saved - check the end")。録画から 14 日間 (サーバーのドラフト寿命) は active のまま一覧と queue.sexp に残り、保持スイープからも守られる。その後は通常のスイープの対象。プレイヤーは終わりを確認してからサイトで手動添付する。rename 失敗時は `last-error = "could not save recording: ~a"`。最後に必ず `reset-recorder`。
`reset-recorder` (:1179): capture 系フィールド全消去、state=:idle (`last-error` は消さない)。

`best-session-run` (:567): aborted でない run の中で `time-ms` 最大。完走が 1 件もなければ aborted を含めた最大。

`on-keep` (main.lisp:390): `link-video-file!(run, path)` (キューエントリを `(quest-slug, time-ms, finished-at)` の自然キーで探し `:video-path` を設定) → 一覧更新。

### 1.3 `start-recording` (recording.lisp:1017)

1. `:hw-encode` 有効かつ `*hw-encoder-probe-state* == :spawn-failed` なら HW プローブを背景で再実行 (今回の録画は現状の判定を使う)。
2. `output = recording-tmp-path()`、`audio-pid = :record-audio ? *audio-target-pid* : nil`、`audio-pipe = audio-pid ? "\\.\pipe\ephinea-ta-audio" : nil`、`encoder = :hw-encode ? *hw-video-encoder* : nil`。
3. **ソース選択** (§1.4): `wgc = backend-wgc-capture()`; `monitor = wgc ? nil : backend-capture-monitor()`。
4. argv = `build-ffmpeg-args(window-title, output, audio-pipe, monitor, wgc, encoder, gpu-chain = (encoder=="h264_qsv" && *hw-fullscreen-gpu-chain*), low-memory = low-memory-machine-p())`。
5. `backend-start-capture(ffmpeg, args, output, audio-pipe, audio-pid, wgc-session=wgc.session)`。
6. 成功: capture/開始時刻 (`get-internal-real-time`、**spawn 完了後**)/tmp/空の runs/`run-end-ms=nil`/`last-error=nil`/state=:recording/`*capture-failure-notified*=nil`。さらに:
   - `:hw-encode` かつ encoder nil かつ probe-state :spawn-failed かつ未通知 → 1 プロセス 1 回 `:notify-software-encode-*` (info)。
   - `monitor.crop` あり (= ウィンドウモードが ddagrab 経由) かつ未通知 → 1 プロセス 1 回 `:notify-overlap-*` (info)。
7. 失敗: `last-error = error ?? "could not start ffmpeg"`。連続失敗ストリーク中 1 回だけ通知 `:notify-capture-failed-title` + 本文はメッセージに `"(Windows error 4551)"` を含めば `:notify-capture-blocked-text` (Smart App Control)、それ以外 `:notify-capture-failed-text` (icon :warning)。

### 1.4 キャプチャソース選択 (決定表)

優先順 (上から最初に成立したもの):

| # | 条件 | ソース | 実装 |
|---|---|---|---|
| 1 | WGC 利用可 (`wgc-available-p`) かつ `:wgc-disable` 偽 かつ PSOBB ウィンドウあり かつ `window-covers-monitor-p` 偽 (=確実にウィンドウモード) かつ `start-wgc-session` 成功 | **WGC** (rawvideo パイプ) | `backend-wgc-capture` ffmpeg-win32.lisp:832 |
| 2 | ウィンドウなし | gdigrab (フォールバック) | `psobb-capture-monitor` :696 → nil |
| 3 | MonitorFromWindow/GetWindowRect/GetMonitorInfo 失敗 | gdigrab | 同上 |
| 4 | DXGI にモニターのデバイス名が見つからない | gdigrab | 同上 (見えた出力一覧をログ) |
| 5 | ウィンドウ矩形がモニター矩形を包含 (`rect-covers-p`: `wl<=ml && wt<=mt && wr>=mr && wb>=mb`) = フルスクリーン/ボーダーレス | **ddagrab 全画面** `(:output-idx :adapter :width :height)` | 同上 |
| 6 | ウィンドウモードで gdigrab プローブ判定が現ウィンドウに対し `:usable` | **gdigrab** (意図的・重なり耐性あり) | `gdigrab-verdict-usable-p` recording.lisp:687 |
| 7 | ウィンドウモードでクライアント矩形→モニター相対クロップが得られる | **ddagrab + crop** (重なったウィンドウが写る → 通知) | `capture-crop-rect` :593 |
| 8 | クロップ不能 (64px 未満) | gdigrab | |

- `window-covers-monitor-p` (:814) は **問い合わせ失敗時に t を返す** (不明ならウィンドウモード扱いしない = WGC を使わずモニター経路へ)。必須。
- WGC は「フルスクリーンでは使わない」: ddagrab なら重なりが起こり得ず QSV ゼロコピーが使えるため。
- 各分岐は recording log に理由を残す (`capture check: ...` 行, §1.12)。ログ文言は診断で読まれるので **同一文言推奨**。

#### 1.4.1 矩形関数 (純関数・テストでピン留め)

- `capture-crop-rect(client, monitor)` (:593): 画面座標 `(l t r b)`。`left=max(cl,ml)`, `top=max(ct,mt)`, `w=2*floor((min(cr,mr)-left)/2)`, `h=2*floor((min(cb,mb)-top)/2)`。`w>=64 && h>=64` なら `(left-ml, top-mt, w, h)`、それ以外 nil。定数 `+capture-crop-min-pixels+`=64。
  - 例: `(160 90 1760 990)`/`(0 0 1920 1080)` → `160 90 1600 900`; `(2080 90 3680 990)`/`(1920 0 3840 1080)` → `160 90 1600 900`; `(-100 -50 924 718)` → `0 0 924 718`; `(100 100 1123 867)` → `100 100 1022 766`; `(0 0 32 32)` → nil。
- `wgc-crop-rect(client, window, frameW, frameH)` (:611): `x=max(0,cl-wl)`, `y=max(0,ct-wt)`, `w=2*floor(min(cr-cl, frameW-x)/2)`, `h=2*floor(min(cb-ct, frameH-y)/2)`; 64 未満なら nil (その場合クロップなしで全フレーム録画)。
  - 例: client `(108 131 1388 1091)`, window `(100 100 1396 1099)`, 1296x999 → `(8 31 1280 960)`; client `(108 131 1389 1092)` → 同じ; client `(100 100 130 130)`, window `(100 100 140 140)`, 40x40 → nil。
- `window-client-screen-rect(hwnd)` (:554): `GetClientRect` + `ClientToScreen((0,0))` → `(left, top, left+w, top+h)`。
- `record-scale-dimensions(w, h, cap=1080)` (:265): `h<=cap` なら `(2*floor(w/2), 2*floor(h/2))`、else `(2*round(w*cap/(2*h)), 2*floor(cap/2))` (**round は偶数丸め**)。例: 3200x1800→1920x1080、2560x1600→1728x1080、1440x900→1440x900、1367x899→1366x898。

#### 1.4.2 DXGI 出力解決 (`dxgi-output-index-for-device`, ffmpeg-win32.lisp:499) — 必須

- `MONITORINFOEXW.szDevice` (`\\.\DISPLAYn`) を、`CreateDXGIFactory1(IID_IDXGIFactory {7B7166EC-21C7-44AE-B21A-C9AE321AE369})` で **全アダプタ (最大 8) × 全出力 (最大 8)** を列挙して `DXGI_OUTPUT_DESC.DeviceName` と文字列一致させる。返り値 `(output-index, adapter-index)` (output-index はアダプタ内相対)。
- **`DISPLAYn → n-1` の推測は禁止** (run 1047: 隣のモニター=Discord を録画したプライバシー事故)。
- 不一致時ログ: `capture check: no DXGI output named "<name>" (saw "<name>[adapter k]", ...)`。
- 注: Lisp は `IID_IDXGIFactory` (Factory1 ではない) を要求している (その機では Factory1 IID が E_NOINTERFACE だったため)。C# で Vortice/SharpDX 等を使うなら `IDXGIFactory1` で問題ないが、列挙順序 = ddagrab の `output_idx` の意味と一致する必要がある (同じ DXGI 列挙なので一致する)。

#### 1.4.3 gdigrab プローブ (ffmpeg-win32.lisp:578-694, recording.lisp:658-697)

- 目的: WGC 非対応機 (古い Win10) のみ。ウィンドウモードで GDI がこのウィンドウを読めるか実証できたら gdigrab (重なり耐性) を使う。
- **WGC 利用可かつ `:wgc-disable` 偽なら一切起動しない** (`maybe-start-gdigrab-probe` :658)。
- 起動条件: poll loop の 4Hz 枠で、`recording-enabled-p` かつ非 busy (クエスト外・非録画) かつ window-title 非 nil かつ実行中プローブなし かつ 現在の判定が現ウィンドウ ID と不一致 かつ 前回開始から 10 秒以上 (`+gdigrab-probe-min-interval-seconds+`=10)。
- ウィンドウ ID (`psobb-window-probe-key` :596) = `(hwnd アドレス, (クライアント幅, 高さ))`、64px 未満なら nil (プローブしない)。
- 判定 (`probe-gdigrab-window` :612): stderr を `%TEMP%\eta-gdigrab-probe-stderr.txt` に出し、タイムアウト 15 秒 (`+hw-probe-timeout-seconds+`)。exit≠0 → `:failed`; stderr 読めない → `:failed` (「黒でない証明なし」を usable にしない); stderr に `black_start` を含む → `:black`; それ以外 `:usable`。終了後: 生きていれば TerminateProcess(1)、stderr ファイルは **録画ログに転記せず** 削除。
- 判定値 `*gdigrab-probe-verdict*` = `(:hwnd addr :size (w h) :result r)`。使用時 (`gdigrab-verdict-usable-p`) は hwnd と size の両方一致 **かつ** `:usable` のみ真。不一致/black/failed/なし → ddagrab (安全側)。
- ログ: `gdigrab probe: window <addr> (<w> <h>) -> <USABLE|BLACK|FAILED>`。
- 既知の弱点: 暗い場面で `:black` 誤判定がキャッシュされる (2026-08-06 実地確認)。安全側なので仕様として維持。

### 1.5 HW エンコーダプローブ (ffmpeg-win32.lisp:245-337)

- 起動時 (`main`、`:hw-encode` 真なら) に背景で 1 回。`:spawn-failed` の間は `start-recording` 毎に再試行。実行中なら重ねない。
- 候補順 `+hw-encoder-candidates+` = `h264_nvenc`, `h264_amf`, `h264_qsv` (recording.lisp:174)。**`h264_mf` は入れない** (HW が無い機で SW MFT に黙ってフォールバックする)。
- 各候補: argv (`hw-encoder-probe-args` :240)
  `-hide_banner -loglevel error -f lavfi -i color=black:size=256x256:rate=30 -frames:v 8 -c:v <enc> -f null -`
  タイムアウト 15 秒 (超過で kill)、exit 0 で採用。
- 結果: `*hw-video-encoder*` = 採用名 or nil; `*hw-encoder-probe-state*` = 1 候補でも起動できれば `:done`、1 つも起動できなければ `:spawn-failed` (例: Smart App Control "Windows error 4551")。ffmpeg パス解決自体が例外でも `:spawn-failed`。
- `h264_qsv` 採用時のみ QSV ゼロコピーチェーンをプローブ (`hw-gpu-chain-probe-args` :248):
  `-hide_banner -loglevel error -f lavfi -i ddagrab=output_idx=0:framerate=30:draw_mouse=0 -frames:v 8 -vf hwmap=derive_device=qsv,vpp_qsv=w=1280:h=720:format=nv12:out_color_matrix=bt709:out_range=tv -c:v h264_qsv -f null -`
  exit 0 → `*hw-fullscreen-gpu-chain*` = t。AMD 相当 (scale_d3d11/vpp_amf) は同梱 ffmpeg で壊れているため使わない。
- ログ: `hw encoder probe: using <enc|libx264 (no hardware encoder)>[ (provisional - ffmpeg would not start)]`, `gpu chain probe: fullscreen captures <stay on the GPU (hwmap -> vpp_qsv)|keep the hwdownload fallback>`。
- プローブ前に始まった録画は libx264 で走る (許容)。

### 1.6 ffmpeg argv 仕様 (`build-ffmpeg-args`, recording.lisp:825) — **バイト単位で一致させること**

定数: framerate 30、preset `veryfast`、CRF 29 (低メモリ 31)、x264 threads = `max(2, min(8, floor(NUMBER_OF_PROCESSORS/2)))` (取得不能時 cores=4) (`encoder-thread-count` :140)、HW VBR `3500k`/maxrate `7M`/bufsize `14M` (低メモリ `2500k`/`5M`/`10M`)、低メモリ判定 = 物理メモリ < 12 GiB (`GlobalMemoryStatusEx.ullTotalPhys`、取得不能なら非低メモリ) (:218,:234)。

argv の構成 (この順で連結):

```
[頭]    -y -loglevel error -probesize 32 -analyzeduration 0
[映像入力] video-input-args
[音声入力] (audio-pipe 時) -f s16le -ar 48000 -ac 2 -thread_queue_size 1024 -i \\.\pipe\ephinea-ta-audio
[エンコーダ] video-encoder-args
[フィルタ] -vf <capture-filter-chain>
[音声符号] (audio-pipe 時) -c:a aac -b:a 160k
[尾]    -movflags +frag_keyframe+empty_moov <output-path>
```

**映像入力** (`video-input-args` :706):
- WGC でない かつ `monitor.adapter > 0` の場合のみ先頭に `-init_hw_device d3d11va=dda:<adapter> -filter_hw_device dda` (adapter 0 / `:adapter` 無しは argv 不変 — 必須)。
- WGC: `-f rawvideo -pixel_format bgra -video_size <W>x<H> -framerate 30 -i \\.\pipe\ephinea-ta-video` (W,H はウィンドウ全体フレームサイズ)
- ddagrab: `-f lavfi -i ddagrab=output_idx=<idx>:framerate=30:draw_mouse=0`
- gdigrab: `-f gdigrab -framerate 30 -draw_mouse 0 -i title=<ウィンドウタイトル>` (タイトルは `GetWindowTextW` の実値。完全一致が必要)

**エンコーダ** (`video-encoder-args` :750):
- HW: `-c:v <enc> -b:v 3500k -maxrate 7M -bufsize 14M -bf 0`
- x264: `-c:v libx264 -preset veryfast -threads <N> -crf 29 -bf 0 -pix_fmt yuv420p`

**フィルタ** (`capture-filter-chain` :771)。`SCALE` = `scale=-2:trunc(min(1080\,ih)/2)*2:flags=fast_bilinear:out_color_matrix=bt709:out_range=tv` (**`\,` はリテラルのバックスラッシュ+カンマ**。フィルタグラフ内のカンマエスケープ)。`TAGS` = `setparams=color_primaries=bt709:color_trc=iec61966-2-1`。`FMT` = HW なら `format=nv12`、x264 なら `format=yuv420p`。

| 条件 | -vf |
|---|---|
| monitor かつ crop なし かつ HW かつ gpu-chain かつ adapter≤0 | `hwmap=derive_device=qsv,vpp_qsv=w=<W>:h=<H>:format=nv12:out_color_matrix=bt709:out_range=tv,TAGS` (W,H = `record-scale-dimensions(monitor.width, monitor.height)`) |
| WGC かつ crop あり | `crop=<w>:<h>:<x>:<y>,SCALE,FMT,TAGS` |
| monitor crop あり | `hwdownload,format=bgra,crop=<w>:<h>:<x>:<y>,SCALE,FMT,TAGS` |
| monitor (crop なし、ゼロコピー不可) | `hwdownload,format=bgra,SCALE,FMT,TAGS` |
| それ以外 (gdigrab / WGC crop なし) | `SCALE,FMT,TAGS` |

注: gpu-chain フラグは monitor が無い (gdigrab) 場合 argv を変えない。crop ありは gpu-chain でも hwdownload 経路 (crop_qsv なし)。

**具体例 (verbatim)**:

(a) gdigrab + x264 + 音声 (8 論理コア)
```
-y -loglevel error -probesize 32 -analyzeduration 0 -f gdigrab -framerate 30 -draw_mouse 0 -i "title=Ephinea: Phantasy Star Online Blue Burst" -f s16le -ar 48000 -ac 2 -thread_queue_size 1024 -i \\.\pipe\ephinea-ta-audio -c:v libx264 -preset veryfast -threads 4 -crf 29 -bf 0 -pix_fmt yuv420p -vf scale=-2:trunc(min(1080\,ih)/2)*2:flags=fast_bilinear:out_color_matrix=bt709:out_range=tv,format=yuv420p,setparams=color_primaries=bt709:color_trc=iec61966-2-1 -c:a aac -b:a 160k -movflags +frag_keyframe+empty_moov C:\Users\u\Videos\RappyRuns\rec-tmp-20260924-213000-0a1b2c3d.mp4
```
(音声セッション起動後に `-f s16le -ar 48000 -ac 2` は実フォーマットで置換される — §1.7。プロセスループバックなら `-f f32le -ar 48000 -ac 2`)

(b) WGC (1296x999, crop 8,31,1280x960) + NVENC
```
... -probesize 32 -analyzeduration 0 -f rawvideo -pixel_format bgra -video_size 1296x999 -framerate 30 -i \\.\pipe\ephinea-ta-video -f f32le -ar 48000 -ac 2 -thread_queue_size 1024 -i \\.\pipe\ephinea-ta-audio -c:v h264_nvenc -b:v 3500k -maxrate 7M -bufsize 14M -bf 0 -vf crop=1280:960:8:31,scale=-2:trunc(min(1080\,ih)/2)*2:flags=fast_bilinear:out_color_matrix=bt709:out_range=tv,format=nv12,setparams=color_primaries=bt709:color_trc=iec61966-2-1 -c:a aac -b:a 160k -movflags +frag_keyframe+empty_moov <out>
```

(c) フルスクリーン 2560x1600 + QSV ゼロコピー
```
... -f lavfi -i ddagrab=output_idx=0:framerate=30:draw_mouse=0 ... -c:v h264_qsv -b:v 3500k -maxrate 7M -bufsize 14M -bf 0 -vf hwmap=derive_device=qsv,vpp_qsv=w=1728:h=1080:format=nv12:out_color_matrix=bt709:out_range=tv,setparams=color_primaries=bt709:color_trc=iec61966-2-1 ...
```

(d) セカンダリアダプタ (adapter 1, output 1)
```
-y -loglevel error -probesize 32 -analyzeduration 0 -init_hw_device d3d11va=dda:1 -filter_hw_device dda -f lavfi -i ddagrab=output_idx=1:framerate=30:draw_mouse=0 ... -vf hwdownload,format=bgra,scale=...,format=yuv420p,setparams=...
```

**音声の除去/置換**:
- `strip-audio-args(args, pipe)` (:948): 連続部分列 `-f s16le -ar 48000 -ac 2 -thread_queue_size 1024 -i <pipe>` と `-c:a aac -b:a 160k` を除去 (音声セッション生成失敗時の映像のみフォールバック)。
- `retarget-audio-args(args, fmt, rate, ch)` (:957): 最初の `"s16le"` の位置 p について `args[p]=fmt, args[p+2]=rate, args[p+4]=ch`。

**なぜこの形か** (変更禁止の理由):
- `-bf 0`: 断片化 MP4 が B フレームの並べ替え遅延を 2 フレームの映像開始オフセットとして焼き込み、ブラウザとローカルプレイヤーで 67ms 同期がずれた (run 92)。pts=dts 0 起点が唯一全プレイヤー一致。
- `-probesize 32 -analyzeduration 0`: 映像入力のプローブを 1 フレームに抑え、音声パイプ接続時刻 ≒ 映像 t=0 にする (§1.7 の同期アンカー)。
- `fast_bilinear`: lanczos で 25fps→18fps に落ち音ズレした実測あり。
- ライブで loudnorm (-af) は **禁止**: 先読みで映像が 17fps に絞られた。正規化は remux で。
- bt709 明示変換 + FMT をスケール直後 + setparams: 未タグ BT.601 がブラウザで BT.709 解釈され色ずれ (run 1368)。codec レベルの `-color_*` は同梱 ffmpeg 8 のエンコーダが無視するのでフレームプロパティで付ける。
- `+frag_keyframe+empty_moov`: TerminateProcess されても再生可能。

### 1.7 プロセス起動・停止 (ffmpeg-win32.lisp:98-238, 857-1002)

- **コマンドライン組み立て** (`quote-windows-arg` :98, `argv->command-line` :123): プログラム名も含め各引数を CommandLineToArgvW 規則でクォート (空文字 or 空白/タブ/`"` を含む場合のみ `"` で囲む; `"` 直前のバックスラッシュは 2n+1 個; 末尾バックスラッシュは倍化)。スペース区切り連結。→ C# `ProcessStartInfo.ArgumentList` の規則と同等。
- `CreateProcessW(NULL, cmdline, …, bInheritHandles=TRUE, CREATE_NO_WINDOW(0x08000000) | BELOW_NORMAL_PRIORITY_CLASS(0x4000), …)`、`STARTF_USESTDHANDLES`、stdin = 匿名パイプ読み端 (書き端は非継承に設定して保持)、stderr = `<出力パス>.stderr.txt` (GENERIC_WRITE, FILE_SHARE_READ, CREATE_ALWAYS, 継承可) — 作れなければ stderr なしで続行。stdout は未設定 (NULL)。
  - 全 ffmpeg (録画・remux・プローブ) が **below-normal 優先度** (必須: x264 が PSOBB のフレーム時間を奪った)。推奨: C# では P/Invoke `CreateProcess` で同一フラグ。`Process` を使う場合は起動直後に `PriorityClass = BelowNormal`、`CreateNoWindow = true`、stderr はファイルへ (パイプ中継でも可だが、ffmpeg がパイプ詰まりでブロックしないよう非同期読み出し必須)。
- 生存判定 `GetExitCodeProcess == STILL_ACTIVE(259)`; 成功判定 exit code 0。
- **グレースフル停止** (`backend-request-stop` :950): 音声あり → `stop-audio-session` (パイプ EOF) → 別スレッドで `+audio-drain-seconds+`=3 秒待ってから stdin に `"q\n"` (2 バイト)。音声なし → 即 `"q\n"`。WGC セッションはここでは止めない (ffmpeg 終了でパイプ書き込みが失敗しフィーダーが自然終了)。
- **強制終了** (`backend-kill-capture` :966): 音声停止、WGC 停止、`TerminateProcess(h, 1)`。
- **クローズ** (`backend-close-capture` :992, 冪等): 音声停止、WGC を stop+close (所有権はキャプチャトークン)、stderr 転記 (`transcribe-capture-stderr`: 末尾 8192 文字 `+stderr-transcript-chars+` を空白以外があれば `ffmpeg stderr (<path>):\n<tail>` としてログへ、ファイル削除)、ハンドル close。
- **起動失敗時**: WGC セッションは渡された時点で所有済み → 必ず stop+close (フィーダーが永遠にパイプ接続待ちになるのを防ぐ)。音声セッションも停止。ログ `capture start FAILED: <cond>\n  ffmpeg=<p> output=<o> pid=<pid>`、戻り値 `(nil, "<cond>")` (エラー文字列は GUI とトレイ通知の 4551 判定に使う: Win32 エラーメッセージは `could not start <prog> (Windows error <n>)` 形式 — 必須: この書式を保つ)。
- 起動成功ログ: `capture argv: <完全なコマンドライン>`、`capture started: audio=<:GAME|:DESKTOP|:NONE|NIL> wgc=<T|NIL>`。
- 音声セッション生成は ffmpeg 起動 **前** (パイプサーバ端が先に存在している必要)。音声側の例外は `audio session failed (video-only fallback), pid=<pid>: <cond>` をログして映像のみで続行。
- remux (`backend-start-remux` :923): 同じ `spawn-process`、stderr は `<最終パス>.stderr.txt`。
- ディレクトリ作成: `ensure-directories-exist(output)`。

### 1.8 remux / トリム (`build-remux-args`, recording.lisp:902)

```
-y -loglevel error -i <tmp> [-t <S>.<mmm>] -c:v copy -af loudnorm=I=-24:TP=-1.5:LRA=11,aresample=48000 -c:a aac -b:a 160k -movflags +faststart <final>
```
- `-t` は `-i` の **後** (出力オプション = そこで書き込み停止。入力シークではない)。値はミリ秒整数から手で `floor(ms/1000) + "." + 3桁ゼロ埋め(ms%1000)` (浮動小数を経由しない。例 702345 → `702.345`)。
- `+record-loudness-lufs+` = -24 (issue 84: -16→-20→-24)。
- タイムスタンプ補正 (`-itsoffset`、`atrim`) は **入れない** (run 88: 負の先頭 pts でブラウザ再生不能)。
- 映像はストリームコピー、音声のみ再エンコード。

### 1.9 動画オフセットとトリム量 (`note-run-video-timing` :1102, `session-video-duration-ms` :1142) — 最重要

完走 run が届くたび (state ∈ {:recording, :stopping}):
```
elapsed_ms = round(1000 * (now - capture_start_real) / ticks_per_sec)   // capture_start_real = spawn 完了直後
run_end_ms = max(run_end_ms ?? -inf, elapsed_ms)                          // aborted 含む全 run で更新
for run in runs:
    offset = elapsed_ms - run.time_ms
    if offset >= 0 && run.video_offset_ms is null: run.video_offset_ms = offset   // 共有 plist を破壊的に更新 (送信キューが同じ値を見る)
duration_ms = run_end_ms + 2000   (run_end_ms が null なら null)
```
- **トリムを `video_offset_ms + time_ms` から逆算してはならない**: 録画を開始させた run は検出器がトラッカーを起こした後に同フレームで ffmpeg を spawn するため、開始時刻が常に映像 t=0 より前 → offset が負 → スタンプされない。逆算方式では実運用で一度もトリムが発動しなかった (run 5348 のデスクトップ写り込み)。
- 壁時計は映像タイムスタンプより先行するので誤差は「切りすぎない」方向。
- 既知の積み残し (S06): このため本番ではほぼ全 run で `video_offset_ms` が未設定。パリティとしては **負なら付けない** を維持 (クランプ 0 は未決定の仕様変更)。
- テスト期待値: キャプチャ 700 秒経過時に 599.123 秒 run → offset ∈ [100000, 102000]; 同状況の remux `-t` ∈ [702, 703]; キャプチャ 5 秒で 599 秒 run → offset なし、`-t` ∈ [7, 8]。

### 1.10 シャットダウン・起動時掃除

- `recorder-shutdown(timeout=8)` (:1365): :recording なら `begin-stop`; :stopping なら 50ms ポーリングで最大 8 秒待ち、生存なら kill → `finalize-capture`; :remuxing なら最大 8 秒待ち → kill → `finish-remux` (失敗扱い=断片化原本を rename)。
- `cleanup-stale-recordings` (:1380): poll loop 開始時、録画フォルダの `rec-tmp-*.mp4` を全削除 (前回クラッシュの残骸)。※ `.stderr.txt` 残骸は掃除しない (パリティ; 改善可)。
- アプリ終了 (`quit-app`): 停止要求 → poll スレッドを最大 10 秒 join → トレイ削除 → `ExitProcess`。

### 1.11 ファイル名と場所

- 録画フォルダ (`resolve-record-dir` :466): `:record-dir` が非空ならそれ。空なら `%USERPROFILE%\Videos\RappyRuns\`。旧 `Videos\EphineaTA\` だけが存在する場合は rename で移行し、rename 失敗 (ファイル使用中など) なら旧フォルダを使い続ける (`default-record-dir-choice`: 新が存在 or 旧が無い → `:use-new`、それ以外 `:migrate`)。
- 作業ファイル (`recording-tmp-path` :543): `rec-tmp-YYYYMMDD-HHMMSS-<token>.mp4` (ローカル時刻)。token = 32bit 乱数の 8 桁小文字 16 進 (`recording-token` :512)。**乱数はプロセス実行時にシード** (固定シードだと複数インスタンスが同名を生成し 2 つの ffmpeg が同一ファイルに書いて破損した: runs 418-424)。C# は `RandomNumberGenerator`/`Random.Shared` で可。
- 最終ファイル (`run-video-filename` :552): `<quest-name|quest-slug|"run"> <m>'<ss>.<mmm> (<YYYY>-<MM>-<DD> <HH><MM>).mp4`。分はゼロ埋めなし、時刻は run の `:finished-at` (無ければ現在) のローカル時刻。例 `Towards the Future 9'59.123 (2026-07-04 2130).mp4`。その後 `sanitize-filename` (`\/:*?"<>|` と U+0020 未満の文字を `-` に)。
- 重複回避 (`deduplicate-path` :524): 既存なら最後の `.` の前に ` (2)`, ` (3)`… を付与 (Explorer 風)。
- stderr: `<出力パス>.stderr.txt` (キャプチャと remux で別ファイル)。

### 1.12 録画ログと診断

- パス: `%TEMP%\ephinea-ta-recording.log` (`recording-log-path` :356)。**実行時に Windows に問い合わせる** (LispWorks の UIOP がビルド機のパスを焼き込み、v0.41.0 で全ユーザーのログが書かれなかった)。C# は `Path.GetTempPath()` で可。
- 書式: 1 行 `MM-DD HH:MM:SS <message>` (ローカル時刻)、UTF-8 追記。書き込み前に 1 MiB (`+recording-log-max-bytes+`) を超えていれば `ephinea-ta-recording.old` へ上書き移動。例外は一切投げない。
- `win32-log` もこのログに流れる (アタッチ・オーバーレイ topmost 失敗等)。
- 起動時 `session: client <ver>, <software-type> <software-version>, ram-gb <n>, cores <n>, ffmpeg <path>`。
- 診断レポート (`diagnostics-report` :430) — サーバーへ送る本文 (サーバー側がパースする可能性があるので **書式維持**):
  ```
  client <ver>
  os <type> <version>
  ram-gb <n> cores <n>
  hw-encoder <enc|NIL> gpu-chain <T|NIL> low-memory <T|NIL>
  --- recording log tail (<path>, exists <T|NIL>) ---
  <ログ末尾 65536 文字 | (no recording log)>
  ```
  (`+diagnostics-tail-chars+`=65536。Lisp の真偽表記 `T`/`NIL` がそのまま出る点に注意)
- 主要ログ文言 (診断で grep される; 同一文言推奨): `capture check: no PSOBB window` / `capture check: window/monitor query failed` / `capture check: window=<rect> monitor=<rect> (<dev>) covers=<T|NIL> dxgi-idx=<i> adapter=<a>` / `capture check: monitor unresolvable in DXGI, staying on gdigrab` / `capture check: windowed, probe-verified gdigrab (overlap-proof window capture)` / `capture check: unusable client rect <r>, staying on gdigrab` / `capturing via ddagrab output_idx=<i> (<w>x<h>)[ crop=<c>]` / `capturing via wgc <w>x<h>[ crop=<c>]` / `wgc support: <YES|NO>` / `wgc session ready: <w>x<h> @30` / `wgc session failed (falling back): <cond>` / `wgc capture ended: <n> frames (<m> fresh), connect <ms> ms` / `wgc capture loop died: <cond>` / `wgc close: feeder thread still alive, leaking session`。

### 1.13 アップロード・held/publish・保持

**自動アップロード** (`maybe-start-upload` main.lisp:172, `upload-candidate` store.lisp:473, `upload-entry-video!` :558):
- 条件: `:video-upload` かつ 非 busy かつ `recorder.state == :idle` かつ 前のアップロードスレッドが終了済み。4Hz 枠と未アタッチ時の 1Hz 枠で呼ぶ。
- 候補: キューの **最古** から、`video-path` あり・`server-id` あり・非 aborted・非 unranked・未 attached・未 given-up・`next-upload-at` 経過済み。ファイルが消えていればその場で `upload-given-up` にして次へ (一覧更新を促す)。
- HTTP: `POST /api/runs/<id>/video-file[?offset_ms=<n>]`、`Authorization: Bearer <submission-token>`、`Content-Type: video/mp4`、Content-Length は WinHTTP が総長から生成、本体は 1 MiB チャンクでストリーム (`winhttp-upload-file` winhttp.lisp:276)、タイムアウト resolve 10s / connect 10s / send 60s (チャンク毎) / receive 300s。4 GiB 以上は送らない。進捗は整数 % が変わった時だけ UI 更新。
- 応答: 200/201 → JSON `duplicate` 真なら `:duplicate` 他は `:attached`; 400/403/404/409/411/413 → `:rejected`; 401 → エラー "Invalid or revoked API token"; その他 → エラー。
  - attached/duplicate: `:video-attached t :video-uploaded t :held (status=="held") :approved (status=="approved")`。**ローカルファイルは削除しない** (保持スイープに任せる。即削除は壊れたアップロードを復旧不能にした)。
  - rejected: `error == "pending-limit"` → `next-upload-at = now + 3600s`; それ以外 → `upload-given-up t :upload-error <message|code|"rejected">`。
  - 通信例外 → `next-upload-at = now + 300s`, `:upload-error`。C# はボディ送信開始後の失敗と 5xx だけを数え、300 秒から倍々 (上限 6 時間) で待ち、12 回連続で `upload-given-up` (core §9.5)。
  - どの結果でも続けて診断送信: `POST /api/runs/<id>/diagnostics` JSON `{"log": <report>, "client_version": <ver>}` (失敗は無視)。
- **held**: サーバーはクライアントからの動画を `held` (非公開) で受ける。公開はブラウザで本人が行う (issue 105)。クライアントは表示ラベル `:status-video-held` ("video uploaded - publish it in the browser") のみ。自動公開はサーバー側ユーザーフラグ `auto_publish` (GUI チェック → `POST /api/me/auto-publish {"enabled":0|1}`、ON 時は確認ダイアログ、失敗時チェックを戻す。`/api/me` の `auto_publish` で再同期)。
- 既知リスク (S17): 本番 WinHTTP がボディ未読 close を RST で受けると応答が読めず 300 秒毎に再送し続ける。C# はこの種の失敗を数え、指数バックオフで約 1 日半 (12 回) 後に諦める。サーバー停止 (接続失敗) では諦めない。

**手動 YouTube フロー** (`upload-video-callback` gui.lisp:713):
- 対象: 選択行 (動画パスがあるもの)。未選択なら最新の「動画パスあり かつ (未 attached または ホスト動画が差し替え可能 = `video-uploaded` かつ `video-url` なし)」。
- 選択行に動画なし → メッセージ `:no-recording-for-run`; 候補なし → `:no-recordings-yet`; ファイル無し → `:recording-file-missing`。
- 実行: `ShellExecuteW(NULL, "open", "explorer.exe", "/select,\"<バックスラッシュ化したパス>\"", NULL, SW_SHOWNORMAL)` → 続けて `https://www.youtube.com/upload` を既定ブラウザで開く。
- 「録画フォルダを開く」: フォルダを作成してから `ShellExecuteW("open", <dir>)`。

**保持 (ローカル容量上限)** (`apply-recording-retention` store.lisp:529, `recordings-to-evict` recording.lisp:1396):
- 120 秒毎 (`+retention-interval-seconds+`)、recorder が :idle の時のみ。上限 = `:record-max-total-gb * 1024^3` (0/未設定なら何もしない)。
- 対象ファイル: 録画フォルダの `*.mp4` のうち名前が `rec-tmp-` で始まらないもの (サイズ, 更新日時)。
- 保護 (削除しない): キュー上で `server-id` あり・非 aborted・非 unranked・未 given-up・**未 attached** のエントリの `video-path`。
- 優先削除: attached 済み (サイトにコピーあり)。
- 順序: 保護を除外 → 更新日時の古い順 → attached を前に出す安定ソート → 合計が上限以下になるまで先頭から削除。どのキューにも無いファイルは中間層。

### 1.14 オーディオキャプチャ (audio-win32.lisp)

- パイプ: `\\.\pipe\ephinea-ta-audio`、`CreateNamedPipeW(PIPE_ACCESS_OUTBOUND=2, byte/blocking=0, maxInstances=1, outBuf=1 MiB, inBuf=0, timeout=0)`。
- セッション生成 (`start-audio-session` :780) は **同期**でアクティベーションまで行い実フォーマットを確定 (argv 置換のため)。WASAPI が全滅してもセッションは返る (無音を供給) — 映像は音声に依存しない。パイプが作れない時だけ nil。
- **プロセスループバック** (Win10 2004+, `activate-process-loopback` :458): `ActivateAudioInterfaceAsync(L"VAD\\Process_Loopback", IID_IAudioClient{1CB9AD4C-DBFA-4C32-B178-C2F568A703B2}, PROPVARIANT{VT_BLOB(65), AUDIOCLIENT_ACTIVATION_PARAMS{ActivationType=PROCESS_LOOPBACK(1), TargetProcessId=<PSOBB pid>, ProcessLoopbackMode=INCLUDE_TARGET_PROCESS_TREE(0)}}, handler)`。完了イベントを最大 3000ms 待ち → `GetActivateResult` → `Initialize(SHARED, LOOPBACK(0x20000)|EVENTCALLBACK(0x40000), hnsBufferDuration=2000000 (200ms), periodicity=0, WAVEFORMATEX{IEEE_FLOAT(3), 2ch, 48000Hz, avg=384000, align=8, bits=32, cbSize=0})` → `SetEventHandle` → `GetService(IAudioCaptureClient{C8ADBD64-E71E-48A0-A4DE-185C395CD317})` → `Start`。scope `:game`、`f32le/48000/2`、frame 8 バイト。アクティベーションはグローバルロックで直列化。完了ハンドラは IAgileObject も応答すること (MTA から呼ばれる)。
- **フォールバック: エンドポイントループバック** (`activate-endpoint-loopback` :528): `MMDeviceEnumerator.GetDefaultAudioEndpoint(eRender, eMultimedia)` → `Activate(IAudioClient)` → `GetMixFormat` → sample format: 32bit かつ (IEEE_FLOAT or EXTENSIBLE) → `f32le`、16bit かつ (PCM or EXTENSIBLE) → `s16le`、他はエラー。`Initialize(SHARED, LOOPBACK のみ, 200ms, 0, mixFormat)`。イベントなし (20ms ポーリング)。scope `:desktop` (全システム音)。
- 全失敗: scope `:none`、既定 `f32le/48000/2/8` で無音供給。
- **キャプチャループ** (`audio-capture-loop` :659): MTA で COM 初期化 → `ConnectNamedPipe` (ERROR_PIPE_CONNECTED=535 も成功) → `connect-ms` 記録 → **ペーシング起点をパイプ接続時刻に再アンカー** (これで映像 t=0 と 20ms 以内。旧方式は 567ms 遅れ) → 停止フラグまでループ:
  - イベントあり: `WaitForSingleObject(event, 50)`; なし: 20ms 待機。
  - `drain-packets`: `GetNextPacketSize`>0 の間 `GetBuffer` → SILENT フラグ(2) なら同フレーム数の 0 を書く、そうでなければデータ書き込み → `ReleaseBuffer`。書き込み失敗 (ffmpeg 消滅) でループ終了。
  - 無音補填: `expected = floor(elapsed * rate)`, `behind = expected - written`; `behind > rate/10` (100ms 分) なら behind フレームの 0 を書いて追いつく (ソースが黙っても音声時間=映像時間)。0 バッファは 100ms 分。
  - 終了処理: `IAudioClient.Stop` → release → event close → **`FlushFileBuffers(pipe)`** (close は未読データを捨てるため) → `CloseHandle(pipe)` = ffmpeg への EOF → CoUninitialize。
- 停止 (`stop-audio-session` :811): 停止フラグ + 自分でパイプにクライアント接続して即閉じる (Connect 待ちのスレッドを起こす)。
- 注意: ループバックは **ミキサー後** の信号。アプリ音量 5% の実例あり → float32 で取り、remux の loudnorm で持ち上げる設計。
- 推奨 (C#): NAudio 等の既製ラッパーは Process Loopback に未対応のものが多い。`ActivateAudioInterfaceAsync` を直接 P/Invoke し `IActivateAudioInterfaceCompletionHandler` + `IAgileObject` を実装すること。

### 1.15 WGC キャプチャ (wgc-win32.lisp)

- 可否 (`wgc-available-p` :227、プロセス内キャッシュ): `RoInitialize(MTA)`、`GraphicsCaptureSession` の statics と `GraphicsCaptureItem` の interop ファクトリが取得でき、`GraphicsCaptureSession.IsSupported()` が真。例外は全て非対応扱い (Win10 1903+ 想定)。
- セッション生成 (`start-wgc-session` :309, **録画開始時に同期で、失敗は全てここで**):
  1. `D3D11CreateDevice(NULL, HARDWARE, NULL, BGRA_SUPPORT(0x20), NULL, 0, SDK 7)`
  2. IDXGIDevice → `CreateDirect3D11DeviceFromDXGIDevice` → WinRT `IDirect3DDevice`
  3. `IGraphicsCaptureItemInterop.CreateForWindow(hwnd)` → item、`item.Size` が 64 未満ならエラー
  4. `Direct3D11CaptureFramePool.CreateFreeThreaded(device, B8G8R8A8UIntNormalized(87), numberOfBuffers=2, size)` (**デリゲート/FrameArrived は使わない**、ポーリング)
  5. `pool.CreateCaptureSession(item)`; `IsCursorCaptureEnabled=false` (任意)、`IsBorderRequired=false` (任意・OS が拒否したら無視)
  6. ステージングテクスチャ (W×H, B8G8R8A8_UNORM, USAGE_STAGING(3), CPU_ACCESS_READ(0x20000))、フレームバッファ W*H*4 を **0 埋め** (遅延時は黒を送る)
  7. パイプ `\\.\pipe\ephinea-ta-video` (OUTBOUND, byte/blocking, 1 instance, outBuf 4 MiB)
  8. フィーダースレッド起動 (キャプチャ開始はまだ)
- フィーダー (`wgc-capture-loop` :528):
  1. `ConnectNamedPipe` (ffmpeg が開くまで待つ) → `StartCapture` (**映像 t=0 = 最初に配送されたフレーム**)
  2. 最初のフレームを最大 100×10ms 待つ
  3. 30fps 固定ループ: 新フレームがあれば全部ドレインして最新をステージングへ `CopyResource`→`Map`→行ピッチを詰めてバッファへ→`Unmap`; 無ければ前フレームを再送 (rawvideo は一定間隔が必要。ddagrab の dup_frames 相当)。1 フレーム書き込み失敗で終了。締切 += 1/30 秒、先行なら sleep、**1 秒以上遅れたら締切を今に戻す** (バースト追いつき禁止)。
  4. 終了でパイプ close (= ffmpeg の映像 EOF)、統計ログ。
  - 毎フレーム取得した COM 一時オブジェクトは必ず解放 (プールは 2 バッファ、リークで枯渇)。
  - ウィンドウリサイズ時にプールの Recreate はしない (パリティ)。プールサイズ固定なので CopyResource は成立する。
- 停止 (`stop-wgc-session`): 停止フラグ + パイプへ自分で接続して待ちを解除。クローズ (`close-wgc-session`, 冪等): 停止フラグ、フィーダー最大 2 秒 join、まだ生きていれば **解放せずリーク** (生きたスレッドの下で解放しない)、session/pool を IClosable.Close してから Release、残りを Release、バッファ解放、パイプ close。
- 推奨 (C#): CsWinRT (`Windows.Graphics.Capture`, `net8.0-windows10.0.19041.0` 以上) + Vortice.Direct3D11。`IsBorderRequired` は 20348+ API なので存在チェック。

---

## 2. オーバーレイ (overlay-win32.lisp)

### 2.1 駆動 (4Hz, `update-ghost-overlay` :1254)

- まず `*overlay-dragged-pos*` があれば消費し config に `:overlay-corner :custom`, `:overlay-position <pos>` を保存・GUI の選択肢を更新 (**show 引数を作る前に**)。
- `:ghost-overlay` 真 かつ 検出器 `:in-quest` → `overlay-show!(line1, line2, delta-state, ghost-data, corner, custom-pos)`:
  - line1 = `format-run-time(detector-elapsed-ms)` + (録画中なら ` REC`)。`format-run-time` = `m:ss.mmm` (分ゼロ埋めなし、1 時間超でも分で表示 `65:00.000`)。
  - line2 = `ghost-vs-text(race)` (`vs 2:03.456` or `vs 2:03.456 -3.2s`) またはゴーストなしで nil。
  - delta-state = delta nil→`:neutral`、負→`:ahead`、それ以外→`:behind`。
  - ghost-data = race と telemetry 経過があれば `ghost-overlay-data(race, elapsed, marker=:ghost-marker)` (§3.6)。
- それ以外 → `overlay-hide!` (wanted=false, ghost-data=nil。スレッドは残す)。
- `overlay-show!` は初回にオーバーレイスレッドを遅延起動。`*overlay-dragged-pos*` が未消費でない時だけ `*overlay-drop-pos*` をクリア。
- 設定 `:ghost-overlay` 既定 nil、`:ghost-marker` 既定 t、`:overlay-corner` 既定 `:top-right`、`:overlay-position` 既定 nil。GUI でオーバーレイをオフにすると即 `overlay-hide!`。

### 2.2 ウィンドウ

- クラス名 `RappyRunsOverlay`、タイトル `RappyRunsOverlay`、スタイル `WS_POPUP`、拡張 `WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`、初期 (0,0,260,224)、非表示で作成。クラスの背景ブラシ・カーソルなし、style 0。
- `SetLayeredWindowAttributes(hwnd, 0xFF00FF, 215, LWA_ALPHA|LWA_COLORKEY)`: マゼンタ画素は完全透過、他は α=215。
- `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE=0x11)` — **必須**: ddagrab のモニター複製録画にオーバーレイが焼き込まれないため (Win10 2004 未満は失敗して無視)。
- フォント: `CreateFontW(20, 0,0,0, FW_SEMIBOLD=600, 0,0,0, DEFAULT_CHARSET=1, 0,0, CLEARTYPE_QUALITY=5, 0, "Segoe UI")` と同 16 (小)。ブラシ: キー色、背景、ゴースト色 (スレッド毎に 1 回生成)。
- タイマー ID 1: 通常 100ms (10Hz)、マーカー有効時 33ms (30Hz)。必要時のみ再設定。
- メッセージループはオーバーレイスレッド専有。スレッド終了時 (unwind-protect) にフォント・ブラシ・バックバッファを必ず解放。
- DPI: **Lisp コードは DPI 認識を宣言していない** (実効モードは LispWorks ランタイム/manifest 依存・要実機確認)。全座標は `GetClientRect`/`ClientToScreen` の値をそのまま使い、フォントは固定ピクセル (20/16) で DPI スケールしない。C# では PerMonitorV2 を推奨するが、高 DPI 機で文字サイズの見え方が変わり得る (§4 リスク)。

### 2.3 レイアウト定数

| 名前 | 値 |
|---|---|
| `+overlay-width+` | 260 |
| `+overlay-compact-height+` | 62 (ゴーストなしのタイマーピル) |
| `+overlay-split-top+` | 58 |
| `+overlay-split-row-height+` | 20 |
| `+overlay-split-rows+` | 8 (ghost.lisp:379) |
| `+overlay-ghost-height+` | 58 + 8*20 + 6 = **224** |
| `+overlay-margin-x+` / `-y+` | 24 / 16 |
| `+overlay-alpha+` | 215 |

色 (COLORREF `0x00BBGGRR` → RGB):

| 名前 | COLORREF | RGB |
|---|---|---|
| key | `#xFF00FF` | #FF00FF (マゼンタ) |
| bg | `#x201410` | #101420 |
| text | `#xF0F0F0` | #F0F0F0 |
| ahead (緑) | `#x78DC50` | #50DC78 |
| behind (赤) | `#x6E6EFF` | #FF6E6E |
| ghost (橙) | `#x00A8FF` | #FFA800 |

### 2.4 配置 (`overlay-position` :575, 純関数は ghost.lisp:274-313)

- 毎 tick、ゲームウィンドウ (`FindWindowW(NULL, "Ephinea: Phantasy Star Online Blue Burst")` → 無ければ `"PHANTASY STAR ONLINE Blue Burst"`) のクライアント画面矩形を取得。取得失敗 → nil (直前配置を維持して表示継続)。幅または高さ ≤0 → `:degenerate` (最小化 → 隠す)。
- パネル原点 (`overlay-panel-position`): ドラッグ中の位置 > ドロップ直後の位置 > 設定 (`overlay-panel-origin(corner, custom, areaW, areaH, 260, 現在高さ, 24, 16)`)。
- `overlay-corner-origin`: x = left 系 (`:top-left :middle-left :bottom-left`) → 24; `:top-center :bottom-center` → `max(0, floor((W-w)/2))`; その他 (right 系・未知) → `max(0, W-w-24)`。y = bottom 系 → `max(0, H-h-16)`; `:middle-left :middle-right` → `max(0, floor((H-h)/2))`; その他 → 16。
- `overlay-panel-origin`: corner が `:custom` かつ custom が 2 要素の数値リスト → `(round(clamp01(fx) * max(0,W-w)), round(clamp01(fy) * max(0,H-h)))`; 不正なら corner-origin (未知 corner = top-right 扱い)。
  - テスト値 (W=1360,H=768,w=260,h=352): top-right (1076,16) / top-left (24,16) / bottom-right (1076,400) / bottom-left (24,400) / middle-right (1076,208) / top-center (550,16) / 200x200 領域 bottom-right (0,0) / custom (0.5,1.0) → (550,416) / custom (-0.5,1.5) → (0,416)。
- **フルモード** (`overlay-marker-wanted-p`: ghost-data に `:marker` 真 かつ track が非空ベクタ): ウィンドウ = クライアント領域全体、30Hz。そうでなければウィンドウ = パネルサイズ (260×高さ) をパネル位置に、10Hz。
- 配置 `(x,y,w,h)` が前回と同じなら `SetWindowPos` を呼ばない。呼ぶ時は `SWP_NOZORDER|SWP_NOACTIVATE`。

### 2.5 tick (`overlay-timer-tick` :1007)

1. 必要レートとタイマー間隔が違えば再設定。
2. wanted かつゲームウィンドウあり → 配置。ゲーム無し or degenerate → `overlay-conceal` (ドラッグ終了、入力無効化、`SW_HIDE`、バックバッファ解放、topmost タイムスタンプをクリア)。
3. `:fit` または一度でも配置済み → 未表示なら `ShowWindow(SW_SHOWNOACTIVATE)`。
   - `foreground = (GetForegroundWindow() == game)`。
   - Ctrl 武装: `GetAsyncKeyState(VK_CONTROL) < 0 && foreground` → `WS_EX_TRANSPARENT` を外す (`SetWindowLongPtrW(GWL_EXSTYLE)` + `SetWindowPos(NOMOVE|NOSIZE|NOZORDER|NOACTIVATE|FRAMECHANGED)`)。武装解除かつドラッグ中でなければ戻す。
   - **topmost 再主張** (`overlay-keep-topmost` :977): foreground の時だけ、前回から 1000ms 以上経過 (またはタイムスタンプ nil) で `SetWindowPos(hwnd, HWND_TOPMOST, 0,0,0,0, NOMOVE|NOSIZE|NOACTIVATE)`。タイムスタンプは呼び出し前に更新。失敗ストリークの初回だけログ `overlay: HWND_TOPMOST re-assert failed - the panel may be sitting behind the game`。C# は表示ごとの初回の成功 `overlay: HWND_TOPMOST re-asserted` もログする。さらに表示状態の変化 (`overlay: shown at X,Y WxH (panel|full client area), game hwnd H` / `overlay: hidden: <理由>`、理由の文字列は `not wanted (no quest running, or the overlay setting is off)` / `game window not found` / `game client area is empty (minimized?)` / `game client rect unreadable before the first placement`) と表示中のモード・サイズ変化 (`overlay: placement now ...`、移動だけでは書かない。5 秒に 1 行までにまとめ、その間に抑えた変化の数を次の行に ` (+N changes)` と付ける。抑えた変化は 5 秒経てば新たな変化が無くても書き、隠す直前にも書く。表示行も 5 秒の区切りを始める)。ウィンドウ生成に失敗したら `overlay: window creation failed - the overlay cannot show` を 1 度だけ書く を変化時だけ録画ログへ書く (S08、tick ごとには書かない)。一時的な rect 取得失敗で前回位置を保つ場合は shown のまま扱い、書かない。 (2026-08-21: 他の topmost アプリ/表示モード遷移でバンドから落ち、ゲームの裏に居続けた不具合の修正。必須)
   - `InvalidateRect(hwnd, NULL, FALSE)`。

### 2.6 描画 (`overlay-paint` :820, `overlay-draw-frame-1` :729)

- WM_PAINT: `BeginPaint` → バックバッファ (互換 DC + 互換ビットマップ、サイズ変化時のみ再生成、新規作成成功まで旧を保持、失敗サイズを記憶して毎フレーム再試行しない) に合成 → `BitBlt(SRCCOPY)` 1 回 → `EndPaint` (例外でも必ず)。合成が例外でも BitBlt は行う。バッファが無ければウィンドウ DC へ直描き。合成は `SaveDC`/`RestoreDC(-1)` で囲む。
- WM_ERASEBKGND: wParam の DC をキー色で (現在サイズ、未確定なら 260×224) 塗って 1 を返す (拡大時の未定義領域の黒フラッシュ防止)。
- 合成順:
  1. 全面キー色で塗る。
  2. ビューポート原点をパネル原点へ (フルモード時のみ非 0)。パネル背景 `(0,0)-(260, 高さ)` を bg 色で。
  3. 大フォント、`SetBkMode(TRANSPARENT)`、text 色で line1 を (12,6)。line2 があれば delta-state 色 (ahead 緑 / behind 赤 / neutral 白) で (12,32)。
  4. ゴースト有効時: スプリット行 (§2.7)。
  5. 原点を戻す。フルモードかつゴースト有効ならマーカー (§2.8)。
- パネル高さ: ゴーストデータありなら 224、なしなら 62。
- テキストは必ず不透明パネル/ピル上 (ClearType がキー色とブレンドしてフリンジが出るのを防ぐ)。TextOutW (Unicode)。

### 2.7 部屋スプリット行 (`overlay-draw-splits` :647)

- `data.splits` (新しい順・最大 8) を **反転して古い順**に、y = 58, 78, …。小フォント。
- 各行: x=12 に `tr(:overlay-room, room)` (`Room <n>` / `部屋 <n>`)、x=116 に入室時刻 `format-split-clock(ms)` (`m:ss`)、x=180 に `format-ghost-delta(delta, precision)` (delta<0 緑、それ以外赤)。

### 2.8 画面内ゴーストマーカー (`overlay-draw-ghost-marker` :670)

- 実効経過 = `data.elapsed-ms + (now - data.elapsed-at)` (4Hz スナップショット間を補間)。
- ゴースト位置 = `ghost-track-position(track, elapsed)` (§3.5)、ただし `elapsed > ghost-time-ms` (ゴースト完走後) または開始前は nil → 描かない。
- 条件: `*live-camera*` 非 nil かつ ゴースト floor == `data.floor` (自分のフロア)。
- 投影: `ghost-screen-position(camera, clientW, clientH, gx, gy ?? data.own-y ?? 0.0, gz)` (§3.7)。`-50 < sx < W+50` かつ `-50 < sy < H+50` の時のみ描画。
- 点: 橙ブラシ・NULL_PEN で `Ellipse(sx-6, sy-6, sx+7, sy+7)` (半径 6)。
- ラベル: `data.label` が空でない文字列ならそれ、他は `"ghost"`。小フォントで `GetTextExtentPoint32W` → tw, th。ピル: bottom=sy-10, top=bottom-th-4, left=sx-floor(tw/2)-6, right=sx+ceil(tw/2)+6 を bg 色で塗り、橙文字を (left+6, top+2)。

### 2.9 Ctrl+ドラッグ

- WM_NCHITTEST: ドラッグ中 or 点がパネル上 → HTCLIENT、それ以外 → HTTRANSPARENT (-1, マーカー上のクリックもゲームへ)。lParam は符号付き 16bit ×2 (負座標のマルチモニター対応)。
- LBUTTONDOWN: カーソル (`GetCursorPos`) がパネル上なら grab オフセットを記録し `SetCapture`。外れなら DefWindowProc。
- MOUSEMOVE (ドラッグ中): `slackX = max(0, areaW-260)`, `slackY = max(0, areaH-高さ)`, `px = clamp(cx - grabDx - left, 0, slackX)`, 同 py、`pos = (slackX==0 ? 0.0 : px/slackX, slackY==0 ? 0.0 : py/slackY)`、再配置 + 再描画。
- LBUTTONUP / WM_CAPTURECHANGED: ドラッグ終了。`*overlay-dragged-pos*` (永続化用) を **先に**、`*overlay-drop-pos*` (スナップバック防止) を後に書く (順序必須)、`ReleaseCapture`。
- スレッド再起動時はドラッグ状態を全リセット。

---

## 3. ゴースト (ghost.lisp)

### 3.1 取得 (`ghost-fetch-wanted` :415, `maybe-start-ghost-fetch` :452, `fetch-ghost-splits` api-client.lisp:679)

- キーは検出器ではなく **クエストポインタ** (ロード画面の数秒で通信を隠す)。
- `quest-ptr` が 0/無し → `*ghost-fetch-ptr* = nil`, `*ghost* = nil` (ロビーで忘れる。同じアドレスで再ロードされても再取得するため)。
- `quest-name` あり かつ ptr が前回と違う → ptr 記録、`*ghost* = nil`、`find-quest-defs(number, episode, name)` の defs があり `:ghost-race` 真 (既定 t) かつ トークン (ゲスト含む) が空でない時に取得。
- `GET /api/quests/<slug1>/ghost?slugs=<slug2>,<slug3>&difficulty=<url-enc>&party_size=<n>&pb=0` (slugs は他カテゴリがある時のみ、difficulty は `difficulty-label(difficulty, anguish)` 例 `Very%20Hard`, party_size = `max(1, 人数(NPC除く))`, pb は常に 0)。Bearer = submission-token。200 → パース、404 → なし、401/その他 → エラー (握りつぶし)。
- 応答到着時に ptr がまだ同じなら `*ghost*` に設定 (古い取得結果を捨てる)。
- URL エンコードは UTF-8 パーセントエンコード、非予約文字 `A-Za-z0-9-_.~` 以外全て `%XX` (大文字)。

### 3.2 ペイロード (`parse-ghost-splits` :96)

```json
{"run_id":42,"quest":"ep1-towards-the-future","time_ms":123456,"submitter":"teapot",
 "precision":"ms"|"sec","source":"target"|"pb","pb":0|1,
 "rooms":[{"floor":1,"room":10,"nth":1,"enter_ms":0,"kills":3}, ...],
 "track":[[ms,floor,map,x,z], ...], "track_y":[y0,y1,...]}
```
- `rooms` がベクタでない or `time_ms` が整数でない → nil。
- precision は `"ms"` のみ `:ms`、他は `:sec`。`pb` は整数の時のみ。`kills` は数値なら round、欠落は nil (=不明=表示する)。
- track: 各行はベクタで長さ 5〜6 かつ全要素数値のもののみ採用。`track_y[i]` (i は **生の track インデックス**。不正行を落としても後続の高さがずれない) が数値で行が 5 要素なら 6 要素目として付加。6 要素行はそのまま。
- **ワイヤ互換**: サーバーは行を 5 要素に保ち高さを `track_y` で別送 (v0.51/v0.52 クライアントの `(= (length row) 5)` フィルタ対策)。

### 3.3 レース状態 (`ghost-race-step` :198)

- 検出器が `:in-quest` でなければ `*ghost-race* = nil`, `*live-camera* = nil`。
- `:in-quest` なら race を (無ければ) 生成 (ゴースト未着でも部屋進行を開始時から追う)。`*live-camera* = snapshot.camera` (毎フレーム 30Hz)。
- `*ghost*` があり race 未設定で、ゴーストの quest slug がロード中クエストの defs に含まれるなら attach (途中参加可)。
- 自分のプレイヤーと telemetry があれば `elapsed = telemetry-elapsed-ms(now)` (**クエストテレメトリ時計 = 最初のトラッカー開始**) で `ghost-race-note-room(race, me.floor ?? 0, me.room ?? 0, elapsed)` と `note-position(floor, me.y ?? 0.0)`。

### 3.4 部屋整列 (`ghost-race-note-room` :151) — ストリーミング貪欲整列

```
key = (floor, room)
if key != race.last_key:
    race.last_key = key
    for i in [cursor, min(len(rooms), cursor + 15)):        // +ghost-match-lookahead+ = 15
        e = rooms[i]
        if e && e.floor == floor && e.room == room && e.enter_ms is number:
            delta = elapsed - e.enter_ms
            cursor = i + 1; race.delta_ms = delta; matched_rooms++
            if e.kills is null or e.kills > 0:
                splits.push_front({room, floor, ms: elapsed, delta})
            break
return race.delta_ms
```
- 見つからない部屋は直前の gap を維持し split も記録しない。kills=0 (通路) は gap は動かすが行は出さない。
- テスト: rooms `[(1,10,0),(1,11,30000),(1,10,60000),(2,1,90000)]` → (1,10,800)→800; 同室 5000→800; (1,11,34000)→4000; (1,99,40000)→4000; (1,10,55000)→-5000; matched=3。スキップ: (1,10,500) 後 (2,1,92000)→2000。後着: ゴースト無しで (1,10,800)→nil、attach 後 (1,11,31000)→1000。先読み外 (21 番目) は不一致・カーソル不変。
- splits は新しい順、各要素は新規生成 (オーバーレイスレッドが途中状態を安全に読むため)。

### 3.5 トラック補間 (`ghost-track-position` :239)

- 空 or `elapsed < track[0].ms` → nil。
- 二分探索で `ms <= elapsed` の最後の行 lo。最終行なら値そのまま。
- 次行と同じ floor かつ `next.ms - ms < 3000` (`+track-lerp-max-gap-ms+`) かつ `next.ms > ms` なら `f=(elapsed-ms)/(next.ms-ms)` で x, z (と両方に y があれば y) を線形補間。それ以外はスナップ (ワープで壁を滑らない)。
- 戻り値 `(floor, map, x, z, y|null)`。
- テスト: track `[(0,1,10,0,0),(1000,1,10,10,20),(5000,2,11,50,50)]`: 500→(1,10,5,10); 3000→(1,10,20)で x=10,z=20 (フロア変化でスナップ); 999999→(2,..,50,50); -1→nil。`[(0,..,0),(8000,..,100)]` 4000 → x=0 (間隔超過でスナップ)。

### 3.6 オーバーレイ用スナップショット (`ghost-overlay-data` :383)

ゴーストと own-floor の両方がある時のみ:
`{marker, floor=own_floor, own_y, track, ghost_time_ms, label, precision, splits(先頭 8), elapsed_ms, elapsed_at=now}`。無ければ nil (= コンパクトピル)。

### 3.7 カメラ投影 (`ghost-screen-position` :337, `camera-fov` :326) — DropBox Tracker / PartyMemberTracker アドオン式の忠実移植

カメラ読み取り (`read-camera`, psobb.lisp:386): PSOBB プロセスメモリから
- `0x00A48780` から 24 バイト一括: f32 x, y, z (視点位置), f32 dirX, dirY, dirZ (単位視線ベクトル, `0x00A4878C`)
- `0x009ACEDC` u32 zoom (0-4、読めなければ 0)

```
fovDeg = 2*atan(0.56470588 * aspect) * 180/π  -  (zoom-1)*0.600  -  min(zoom,1)*0.300
        (zoom = clamp(zoom ?? 1, 0, 4); 0.56470588 = 768/1360)
fov = fovDeg * π/180

projection(camera, W, H, wx, wy, wz):
  if camera 欠落 or W<=0 or H<=0 or 数値欠落: null
  v = (wx-ex, wy-ey, wz-ez); len = |v|; if len <= 1e-3: null
  v /= len
  fdp = dir·v; if fdp <= 1e-7: null          // 後方・ゼロ方向(ロード画面)
  aspect = W / H
  det = aspect*H / (2*tan(fov/2))
  s = det / fdp
  p = s*v
  right = (-dz, 0, dx)                       // dir × (0,1,0)、正規化しない
  up    = (-dx*dy, dx*dx+dz*dz, -dy*dz)      // right × dir、正規化しない
  sx = round(W/2 + (right.x*p.x + right.z*p.z))
  sy = round(H/2 - (up.x*p.x + up.y*p.y + up.z*p.z))
```
- **基底ベクトルを正規化しないのは意図的** (FOV ヒューリスティックがこのスケールで調整されている)。「修正」禁止。
- 計算は単精度/倍精度どちらでも可だが、Lisp は f32 入力に倍精度定数 (`pi` は long-float) で計算。C# は double で統一推奨。round は偶数丸め。
- テスト: 原点・dir (0,0,1)・zoom 1、1360×768: (0,0,100)→(680,384); (0,10,100)→sy<384; (±10,0,100) → 680 を中心に左右対称; (0,0,-100)→null; (0,0,0)→null; dir 全 0→null; camera nil→null。`camera-fov(1, 1360/768)` ∈ (1.5, 1.65) rad; `fov(0,1.5) > fov(4,1.5)`。

### 3.8 表示とフィニッシュ比較

- `format-ghost-delta(d, precision)` (:481): 符号 `-`(負)/`+`(0 含む非負)。`:ms` → `{sign}{|d|/1000.0 を小数 1 桁}s` (例 -3210→`-3.2s`, 4000→`+4.0s`); `:sec` → `{sign}{round(|d|/1000)}s` (偶数丸め; 4400→`+4s`, -1600→`-2s`)。
- `ghost-vs-text` (:490): ゴースト未 attach → nil; `vs <format-run-time(ghost.time_ms)>` + (delta あれば ` <delta>`)。
- ステータス行接尾辞 (`ghost-status-suffix`): ` | <vs-text>`。ウィンドウタイトル接尾辞 (`ghost-title-suffix`): delta がある時のみ ` <delta>`。
- 完走時比較 (`annotate-ghost-runs` :535, `ghost-covers-run-p` :523): `*ghost*` があり、run の quest-slug == ghost.quest-slug、非 aborted、ghost.time_ms が整数、かつ (source=="target" or ghost.pb null or ghost.pb == (run.pb ? 1 : 0)) なら run に `:ghost-delta-ms (run.time - ghost.time)`, `:ghost-time-ms`, `:ghost-label (label ?? "")` を付加 (公式タイム同士なので ms 精度)。テスト: ghost 123456、run 113456 → -10000。これらはトースト (`ghost-toast`, store.lisp) と送信で使われる。

---

## 4. 回帰リスク一覧 (コメント・メモに記録された既知バグ修正)

優先度順。◎ = 事故実績あり/プライバシー影響。

1. ◎ **末尾トリム (run 5348)**: remux の `-t` は `run-end-ms` (キャプチャ経過の壁時計) + 2 秒。video_offset から逆算しない。remux 失敗時は未トリム原本 + トレイ通知。
2. ◎ **DXGI 出力はデバイス名一致で全アダプタ探索 (run 1047)**: `DISPLAYn-1` 推測禁止。不一致は gdigrab。adapter>0 は `-init_hw_device d3d11va=dda:N -filter_hw_device dda`、adapter 0 は argv 不変。
3. ◎ **ソース選択 (run 949 黒画面 / 重なり写り込み)**: ウィンドウモードは WGC 優先; 状態不明はフルスクリーン扱い; gdigrab は hwnd+サイズ一致の `:usable` 判定がある時のみ; ddagrab+crop 時は重なり警告を 1 回。
4. ◎ **オーバーレイの録画除外**: `WDA_EXCLUDEFROMCAPTURE` 必須 (フルスクリーン ddagrab に焼き込まれる)。
5. **A/V 同期 (run 92/88)**: `-bf 0`、`-probesize 32 -analyzeduration 0`、音声ペーシングはパイプ接続時刻起点 (567ms→<20ms)、remux でタイムスタンプ補正しない、B フレーム禁止。
6. **色 (run 1368)**: 全経路でスケール (or vpp_qsv) が bt709/limited で RGB→YUV 変換し直後に YUV format、末尾 setparams。codec オプションでのタグ付けは無効。
7. **一時ファイル名の衝突 (runs 418-424)**: プロセス毎実行時シードの乱数トークン、最終名の ` (n)` 重複回避、単一インスタンス。
8. **オーバーレイ Z 順喪失 (2026-08-21)**: 1 秒毎・ゲームがフォアグラウンドの時のみ HWND_TOPMOST 再主張、conceal 後は即再主張。
9. **ライブ loudnorm 禁止 (17fps)** と **音量**: 正規化は remux の `loudnorm=I=-24:TP=-1.5:LRA=11,aresample=48000`。ループバックはミキサー後なので float32 取得。
10. **HW エンコーダ**: `h264_mf` 除外; Smart App Control (4551) で `:spawn-failed` → 録画開始毎に再プローブ + CPU エンコード通知 1 回; 4551 専用メッセージ。
11. **性能 (MacBook Air Boot Camp 等)**: 全 ffmpeg を BELOW_NORMAL; x264 threads = コア/2 (2..8); `fast_bilinear`; 1080p 上限; QSV ゼロコピー; 低メモリ (<12GiB) プロファイル。
12. **断片化 MP4 + グレースフル停止**: `+frag_keyframe+empty_moov`、`q\n` → 8 秒で kill、kill 後も保持; 音声 EOF → 3 秒ドレイン → `q`; パイプ close 前に FlushFileBuffers (未読データ破棄防止)。
13. **WGC 資源管理**: 起動失敗時にセッション (フィーダー) を必ず停止/解放; 毎フレーム COM 一時物解放 (2 バッファ枯渇); フィーダー生存中は解放せずリーク; 初回フレームまで最大 1 秒待ち、バッファ 0 埋め; 1 秒以上遅延で追いつきバースト禁止。
14. **v0.49.0 資産欠落**: WGC コードも同梱 ffmpeg も無い zip が公開された。C# 版もリリース前にパッケージ内容 (ffmpeg.exe + LICENSE.txt、GPL ビルドはライセンス同梱必須) とサイズを検証するゲートを置くこと。
15. **ログパスの焼き込み (v0.41.0)**: temp パスは実行時取得。診断に「パスと存在有無」を含める。
16. **ゴースト track のワイヤ互換**: 行 5 要素 + `track_y` 別送、生インデックスで zip。kills 欠落=表示、0=非表示。
17. **オーバーレイのちらつき (PR #281)**: バックバッファ + 単一 BitBlt、WM_ERASEBKGND はキー色、DC 状態は Save/Restore、失敗サイズ記憶、conceal 時バッファ解放。
18. **Ctrl ドラッグ**: NCHITTEST でパネル外は HTTRANSPARENT; Ctrl 武装はゲームがフォアグラウンドの時のみ; dragged→drop の書き込み順; スレッド再起動でドラッグ状態リセット。
19. **アップロード後のローカル保持**: 即削除しない (壊れたアップロードの復旧不能事故)。保持スイープは未アップロードを保護、アップロード済みを先に削除、idle 時のみ。
20. **メモリ (8GB 機)**: 送信済みエントリのテレメトリをメモリからも削除 (store.lisp `update-run!`)。C# でも大きな run データを長期保持しない。
21. **サーバー側タイムアウト/ヒープ (2026-09-08)**: アップロードが 20 秒無通信になるとサーバー側 IO-TIMEOUT。1 MiB チャンク連続送信を維持; 413/401/duplicate の未読 close に対する応答読み取り (S17) は C# の HttpClient では改善可能だが挙動差分として記録。
22. **検出→録画の順序**: `recorder-step` は enqueue より前 (video_offset_ms を送信キューに乗せるため)。runs の計上は停止判定より前。
23. **エッジトリガ**: 失敗した開始をクエスト途中で再試行しない; 途中で tracking-only 変更しても途中開始しない。
24. **ゴースト取得の陳腐化**: クエストポインタで 1 ロード 1 回、ロビーで忘れる、応答到着時に ptr 一致確認、attach 時に defs 所属確認。
25. **DPI**: Lisp 版は DPI 認識未宣言。C# で PerMonitorV2 にすると座標系・フォント見かけサイズが変わる可能性 → 実機 (125%/150%) でオーバーレイ位置とマーカー投影を確認すること。

---

## 5. テスト移植チェックリスト

`tests-recorder.lisp` (モックバックエンドで状態機械を駆動) と `tests-ghost.lisp` の各 `check` を xUnit 等へ 1 対 1 で移植すること。主要グループ:

- 状態機械: 開始 (:idle→:in-quest)、同フレーム完走の計上、停止要求、remux 引数 (入力 tmp・最終名・`+faststart`)、remux 成功で tmp 削除・rename なし、idle 復帰、offset スタンプ、トリム `-t` 値と位置、負 offset 非スタンプ時のトリム、放棄クエスト削除、セグメントのみ保持、最長 run 命名、完走セグメント > 長い aborted、開始失敗 (エラー・同クエスト再試行なし・次クエスト再試行)、途中死亡、q 無視→8 秒後 kill 1 回・保持、remux 失敗→部分出力削除+rename、remux 起動不能→即 rename、remux ハング→kill、shutdown、タイトル無しで録画なし、起動時 stale 削除、無効化/tracking-only。
- 純関数: sanitize、ファイル名、best-session-run、session-video-duration-ms、argv 各種 (§1.6 の全分岐・adapter・WGC・QSV・crop・低メモリ・スレッド数)、strip/retarget、rect-covers-p (`(0 0 1920 1032)` は最大化=非フルスクリーン)、crop 各種、scale 寸法、プローブ argv、blackdetect パース、判定一致。
- 動画フロー: on-keep タイミング、submission-updates、link-video-file!、アクティブ判定とトリム、永続化時テレメトリ除去、clear、ラベル各種 (held/approved/aborted)。
- ゴースト: パース、整列、後着、先読み、接尾辞、完走注釈、差分書式、URL エンコード、取得ゲート、track パースと track_y、補間、スプリット履歴と上限、kills フィルタ、`format-split-clock` (754321→`12:34`, 61000→`1:01`)、投影、FOV、配置 8 方向とカスタム。
