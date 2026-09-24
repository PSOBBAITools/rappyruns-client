# Rappy Runs Desktop Client — コア移植仕様 (Lisp → C#)

対象: `client/src` の非GUIコア (package, version, i18n, config, credentials, memory, win32, winhttp, psobb, quests, telemetry, detect, trigger-log, api-client, websocket-win32, updater, store, main の非GUI部) と `data/quest-triggers.sexp`、`tests/*.lisp`、`package.ps1` / `release.ps1` / `deliver.lisp`。
基準: `main` @ 27d3766 (client v0.60.0, 2026-09-24)。

表記:
- `file.lisp:NNN FUNC` は Lisp 側の根拠 (行番号は定義行)。
- 「**契約**」= 既存インストール・サーバー・旧クライアントとの互換に関わり、変えてはいけない/変えるなら移行手順が要るもの。
- 「**パリティ**」= サーバー側の集計・ランキングに影響するため、ビット単位で同じ結果を出すべき挙動。
- Lisp の真偽: `NIL` 以外はすべて真。**`0` や `""` も真**。C# で `if (x)` に置き換える時に最も間違えやすい (§20)。

範囲外 (別仕様): GUI (gui.lisp)、録画 (recording / ffmpeg-win32 / wgc-win32 / audio-win32)、オーバーレイ、ゴースト描画 (ghost.lisp のうち通信以外)、Pin Share リレー本体 (pinshare*.lisp)、トレイ描画。ただし外部契約 (ファイル・エンドポイント・単一インスタンス) はここに含める。

---

## 目次

1. モジュール対応と責務
2. 起動シーケンス・スレッド・コマンドライン・単一インスタンス・自動起動
3. ディスク上のファイル (外部契約)
4. S式 (sexp) の読み書き仕様
5. `data/quest-triggers.sexp` とクエスト定義
6. HTTP 層 (WinHTTP の挙動)
7. API エンドポイント一覧と JSON スキーマ
8. 認証・ペアリング・login.txt・匿名ゲストとマージ
9. ラン キュー (store) の意味論
10. 自己アップデータと「旧 Lisp クライアント → C# exe」移行
11. プロセスアタッチ・Authenticode・メモリ読み取り
12. PSOBB メモリレイアウト (全アドレス・オフセット)
13. アカウントモード (Sandbox 判定)
14. 検出ステートマシン (detect)
15. テレメトリ
16. トリガーログ / ランログ / ルームピッカー
17. ポーリングループとフレーム内処理順序
18. WebSocket トランスポート
19. i18n
20. ビルド・パッケージ・リリース
21. テスト資産 (パリティテストの種)
22. コメントに埋め込まれた既知の落とし穴 (回帰リスク)

---

## 1. モジュール対応と責務

| Lisp | 責務 | C# 推奨 | 備考 |
|---|---|---|---|
| package.lisp | パッケージ定義・export | (不要) | export 一覧 = テストが触る純粋関数の一覧として有用 |
| version.lisp | `*client-version*`、semver 解析 | `ClientVersion` | dev ビルドは `null` → 自己更新しない |
| i18n.lisp | UI 文字列 (en/ja) と `tr` | `Strings` + JSON | 247 キー、機械変換可 (§19) |
| config.lisp | `%APPDATA%\ephinea-ta-client\config.sexp` | `ConfigStore` | sexp 互換が必要 (§3, §4) |
| credentials.lisp | exe 隣の `login.txt` | `Credentials` | |
| memory.lisp | 読み取りプロトコル・LE デコード・モック | `IMemoryReader`, `MockReader` | テストの要 |
| win32.lisp | ウィンドウ探索・OpenProcess・RPM・Authenticode | `LiveReader`, `Authenticode` | |
| winhttp.lisp | HTTP(S) | `HttpClient` ラッパ | UA・タイムアウト・プロキシ (§6) |
| psobb.lisp | アドレス定数・スナップショット・インベントリ・モンスター | `Psobb*` | **パリティ** |
| quests.lisp | トリガー定義の読み込み・サーバー定義マージ・マッチ | `QuestDefs` | |
| detect.lisp | 検出ステートマシン | `Detector` | **純粋**に保つ (テスト可能性) |
| telemetry.lisp | クエスト毎テレメトリ | `Telemetry` | **パリティ** |
| trigger-log.lisp | トリガーログ・ランログ・ルーム行 | `TriggerLog`, `RunLogs` | |
| api-client.lisp | JSON API | `ApiClient` | ステータス→結果の写像が契約 |
| websocket-win32.lisp | WinHTTP WebSocket | `ClientWebSocket` | Pin Share 用 |
| updater.lisp | GitHub Releases 自己更新 | `Updater` | 旧クライアントとの互換 (§10) |
| store.lisp | ランキュー・再送・動画アップロード選定・表示ヘルパ | `RunQueue` | |
| main.lisp | ポーリングループ・起動 | `PollLoop`, `App` | 処理順序が契約 (§17) |
| rule-form.lisp | クエストルールフォームの純粋ロジック | `RuleForm` | GUI 仕様側だがテストあり |

---

## 2. 起動シーケンス・スレッド・コマンドライン・単一インスタンス・自動起動

### 2.1 起動順序 (`main.lisp:435 main`)

1. **単一インスタンス判定** (ウィンドウ・スレッド生成より前)。`already-running-p` が真なら `signal-existing-instance` → `ExitProcess(0)`。
2. `*stop-requested*`、`*really-quitting*` を偽に。
3. `load-config!`。`*language*` ← `valid-language(:language)` (`:en`/`:ja` 以外は `:en`)。`*moderator-p*` ← キャッシュ `:moderator`、`*pinshare-allowed-p*` ← キャッシュ `:pinshare-allowed` (初回フレームから UI を正しく出すため)。
4. `cleanup-old-update-files` (§10.6)。
5. `:auto-update` 真かつ `*client-version*` 非 NIL (リリースビルド) なら `startup-auto-update` — **メインウィンドウ生成前**。更新を適用する場合は戻らない (ヘルパー起動 → 終了)。
6. `load-queue!` → `load-quest-defs` (builtin)。
7. メインウィンドウ生成・表示、`refresh-runs-list`。
8. `check-server` (別スレッド: `GET /api/quests` → サーバー定義マージ)。
9. `check-token` (別スレッド: `GET /api/me`)。`on-invalid` = 401 のとき `login.txt` があればファイルログインを開始。
10. `prompt-for-token-setup`: トークン空かつ `login.txt` があればファイルログイン。**ブラウザペアリングは自動では始めない** (ボタン操作のみ)。
11. `report-startup-update` (起動時更新の結果を設定画面へ)。
12. トレイ開始、Pin Share スーパーバイザ開始、`log-session-info` (録画ログにビルド/マシン情報)、`:hw-encode` なら HW エンコーダ探査を別スレッドで。
13. `startup-minimized-p` ならウィンドウを **表示した後に** hidden (一瞬のちらつきは許容済み)。
14. ポーリングスレッド `eta-client-poll` 起動 (§17)。

### 2.2 スレッド

| スレッド名 | 役割 | 起動元 |
|---|---|---|
| (CAPI メイン) | GUI | main |
| `eta-client-poll` | ポーリングループ。**ラン送信 (`submit-queued!`) もこのスレッドで同期実行** | main |
| `eta-client-video-upload` | 動画アップロード (同時に 1 本) | poll の GUI ティック |
| `eta-client-server-check` / `-token-check` / `-pairing` / `-file-login` / `-auto-publish` / `-update-check` / `-ghost-fetch` | 単発ワーカー | 各所 |
| `eta-client-tray` | トレイ窓のメッセージループ | main |
| Pin Share relay / permission loop | 範囲外 (permission loop は 1800 秒毎に `/api/me`) | main |

### 2.3 コマンドライン引数 (`config.lisp:97,108`)

| 引数 | 効果 | 比較 |
|---|---|---|
| `--debug` | `debug-mode-p` 真 (Server URL 欄など開発者設定を表示)。config `:debug t` と同等 | `string-equal` (大文字小文字無視)、完全一致 |
| `--minimized` | 起動直後にトレイへ (config `:start-minimized` と OR) | 同上 |

- 他の引数は無視。**自己更新後の再起動は引数なし** (§10)。
- argv[0]: `quests.lisp:28 exe-adjacent-path`、`autostart-win32.lisp:96 autostart-command`、トレイアイコンは argv[0] 基準。`updater`/`credentials` は `lw:lisp-image-name` (実イメージパス) 基準。コメントで「argv[0] が相対名で CWD が変わると外れる」既知の不統一 (リファクタ backlog 行き)。**C# は `Environment.ProcessPath` に統一してよい** (挙動改善)。ただし自動起動の登録値比較 (§2.5) に注意。

### 2.4 単一インスタンス (**契約**: 旧版と新版が同時に走る移行期のため同一にする)

- 名前付きミューテックス: `CreateMutexW(NULL, FALSE, "RappyRunsClient-single-instance")`。直後の `GetLastError() == 183 (ERROR_ALREADY_EXISTS)` なら既に起動中。ハンドルはプロセス終了まで保持 (`tray-win32.lisp:575`)。
- 既存インスタンスへの通知: `FindWindowW(class="RappyRunsTrayWindow", NULL)` → `PostMessageW(hwnd, WM_APP+2 = 0x8002, 0, 0)` (`tray-win32.lisp:585`)。受信側はメインウィンドウを un-hide・前面化。
  - トレイ所有の隠しウィンドウ: クラス名 `RappyRunsTrayWindow`、ウィンドウ名 `RappyRunsTray`。トレイコールバックは `WM_APP+1`。`TaskbarCreated` 登録メッセージでアイコン再追加。
- 2 番目のインスタンスは `ExitProcess(0)` で即終了。
- C# 推奨: 同じミューテックス名・同じクラス名の message-only でない隠しトップレベル窓 (FindWindow で見つかる必要あり) を作り、`0x8002` を処理する。これで Lisp 版 ↔ C# 版どちらが先に居ても二重起動しない。

### 2.5 Windows ログオン時の自動起動 (`autostart-win32.lisp`)

- `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` の値 `RappyRunsClient` (REG_SZ)。
- 値 = `"<argv[0]>" --minimized` (ダブルクォート付き、`autostart-command`)。
- 有効判定 (`autostart-enabled-p`) = 値が存在し、**現在の `autostart-command` と文字列完全一致**。パスが違う (移動した) と「無効」表示 → 再有効化で上書き。
- 無効化 = 値削除 (存在しなくても成功扱い)。
- config には保存しない (レジストリが唯一の真実)。
- 移行注意: Lisp 版が登録した値の argv[0] 表記 (例: フルパス vs 短縮/大文字小文字) と C# の `Environment.ProcessPath` が一致しないと、チェックボックスが「オフ」に見える。比較は `StringComparison.OrdinalIgnoreCase` + パス正規化を推奨 (登録値自体は再有効化時に書き直す)。`--minimized` は必ず受け付けること。

### 2.6 終了 (`main.lisp:419 quit-app`)

`*really-quitting*`/`*stop-requested*` を立て、poll スレッドを最大 10 秒 join (録画の unwind を走らせる)、トレイアイコン削除、`ExitProcess(0)`。`lw:quit` はトレイスレッドから呼ぶと終了しないことがあったため使わない。config とキューは都度保存済みなので強制終了で失うものはない。
×ボタン: `:close-to-tray` 真ならトレイへ隠す、偽なら quit。

---

## 3. ディスク上のファイル (外部契約)

### 3.1 一覧

| パス | 形式 | 書き手 | 読み手 | 備考 |
|---|---|---|---|---|
| `%APPDATA%\ephinea-ta-client\config.sexp` | sexp plist 1 フォーム, UTF-8 (BOM なし) | client | client | §3.2。`APPDATA` 未設定時はホームディレクトリ |
| `%APPDATA%\ephinea-ta-client\queue.sexp` | sexp list, UTF-8 | client | client | §3.3 |
| `%APPDATA%\ephinea-ta-client\trigger-log.txt` | テキスト追記, UTF-8 | client | 人間 (モデレーター) | §16.1。8 MiB 超で `trigger-log.old.txt` へローテーション (C# で追加。Lisp はローテーションなしで開発機に 469MB 実在) |
| `<exeDir>\login.txt` | `key=value` テキスト | ユーザー | client | §8.3 |
| `<exeDir>\data\quest-triggers.sexp` | sexp | リリース zip | client | §5。開発時はソース dir |
| `<exeDir>\data\pin-share\init.lua`, `pinshare-input.dll` | バイナリ/Lua | リリース zip | Pin Share (範囲外) | ゲームの `addons\Pin Share\` にコピーされる |
| `<exeDir>\ffmpeg\ffmpeg.exe`, `LICENSE.txt` | | リリース zip | 録画 | 無ければ PATH の `ffmpeg.exe` |
| `<exeDir>\RappyRunsClient.exe.old` | | 更新ヘルパー | client が起動時に削除 | §10 |
| `<exeDir>\eta-write-probe.tmp` | 1 バイト | 書き込み可否プローブ | 直後に削除 | |
| `%TEMP%\ephinea-ta-recording.log` (+`.old`) | テキスト, UTF-8 | client | 診断アップロード | 1MB でローテーション。行頭 `MM-DD HH:MM:SS ` |
| `%TEMP%\RappyRunsClient-update.zip` | zip | 更新 | 更新ヘルパー | |
| `%TEMP%\rappyruns-update.ps1` | PowerShell, **UTF-8 BOM 付き** | 更新 | powershell.exe | |
| `%TEMP%\rappyruns-update-stage\` | dir | ヘルパー | | |
| `%TEMP%\rappyruns-update.log` | Transcript | ヘルパー | 人間 | |
| `%USERPROFILE%\Videos\RappyRuns\` | 録画 mp4 | 録画 | アップロード | `:record-dir` 空時。旧 `Videos\EphineaTA\` のみ存在なら rename 移行 |
| `HKCU\...\Run\RappyRunsClient` | REG_SZ | client | Windows | §2.5 |

`%TEMP%` は **実行時に** 取得すること (Lisp で UIOP のキャッシュがビルドマシンの temp を焼き込み、診断ログが全く書かれなかった事故 v0.41.0: `recording.lisp:355`)。

### 3.2 config.sexp のキー (`config.lisp:13 *default-config*`)

読み込み: `read-sexp-file` (1 フォームのみ読む、`*read-eval*` nil、壊れていれば NIL) → NIL なら `*default-config*` のコピー → `migrate-config`。
値の解決: `config-value key` = ファイルの plist に key があればその値 (**値が NIL でも** それを採用)、無ければデフォルト。
保存: `save-config!` = 現在の plist 全体を上書き (`:if-exists :supersede`、**非アトミック**)。ファイルに無いキーは書かれない (初回起動時のみ全デフォルトが書かれる)。未知キーは温存される。

| キー | 既定 | 型 | 意味 / 備考 |
|---|---|---|---|
| `:server-url` | `"https://rappyruns-production.up.railway.app"` | string | 保存時 `string-right-trim "/ "`。API URL は `right-trim "/"` + path |
| `:api-token` | `""` | string | 連携済みアカウントの Bearer トークン。使用時 `normalize-token` (前後の空白/Tab/CR/LF 除去) |
| `:anon-token` | `""` | string | 匿名ゲストトークン (§8.4)。マージ成功/404 で `""` に |
| `:language` | `:en` | keyword | `:en` / `:ja` |
| `:auto-submit` | `t` | **強制** | 保存値は migrate で削除 → 常に既定 |
| `:submit-aborted` | `t` | **強制** | 中断ランも送信 (サーバー側で非公開) |
| `:auto-update` | `t` | bool | 起動時の無人自動更新 |
| `:completion-sound` | `nil` | **強制** | |
| `:trigger-log` | `nil` | bool | トリガーログ ON |
| `:record-enabled` | `t` | **強制** | |
| `:tracking-only` | `nil` | bool | 記録専用モード: 録画なし・unranked 送信 |
| `:tracking-private` | `nil` | bool | tracking-only 時に private 送信 |
| `:record-audio` | `t` | bool | |
| `:video-upload` | `t` | **強制** | |
| `:record-max-total-gb` | `20` | number | 録画フォルダ上限 GB (0/空 = 無制限)。`round(gb*1024^3)` バイト |
| `:auto-publish` | `nil` | bool | サーバー `users.auto_publish` のキャッシュ (真実はサーバー) |
| `:hw-encode` | `t` | bool | |
| `:ffmpeg-path` | `""` | string | |
| `:record-dir` | `""` | string | |
| `:moderator` | `nil` | bool | `/api/me` role のキャッシュ |
| `:close-to-tray` | `t` | bool | |
| `:rank-toast` | `t` | bool | |
| `:ghost-race` | `t` | bool | |
| `:ghost-overlay` | `nil` | bool | |
| `:ghost-marker` | `t` | bool | |
| `:overlay-corner` | `:top-right` | keyword | `:top-right :top-left :bottom-right :bottom-left :middle-right :middle-left :top-center :bottom-center :custom` |
| `:overlay-position` | `nil` | `(xfrac yfrac)` | 単精度浮動小数 2 要素リスト。`:custom` 時のみ |
| `:pinshare-allowed` | `nil` | bool | `/api/me` features キャッシュ |
| `:pinshare-enabled` | `nil` | bool | |
| `:pinshare-channel` | `""` | string | |
| `:pinshare-server` | `""` | string | 空 = `wss://pin-share-server-production.up.railway.app` |
| `:start-minimized` | `nil` | bool | |
| `:debug` | `nil` | bool | |
| (隠し) `:update-repo` | (なし) | string | 更新元 `owner/name` 上書き (テスト用) |
| (隠し) `:wgc-disable` | (なし) | bool | 録画 (範囲外) |
| (廃止) `:token-prompt-shown` | — | — | migrate で削除 |
| (廃止だが実ファイルに残存) `:overlay-capturable`, `:ghost-video` | — | — | 未知キーとして温存されている。C# も温存推奨 |

強制キー (`+forced-config-keys+`): `:auto-submit :submit-aborted :completion-sound :record-enabled :video-upload` — **読み込み時に削除**、常に既定値。

実ファイル例 (開発機、トークン伏字):
```
(:PINSHARE-ALLOWED COMMON-LISP:T :PINSHARE-CHANNEL "teapot" :PINSHARE-ENABLED COMMON-LISP:T :GHOST-RACE COMMON-LISP:T :OVERLAY-CAPTURABLE COMMON-LISP:T :OVERLAY-POSITION (0.7894558 0.020304569) :OVERLAY-CORNER :CUSTOM :GHOST-VIDEO COMMON-LISP:T :GHOST-OVERLAY COMMON-LISP:T :TRACKING-PRIVATE COMMON-LISP:NIL :TRACKING-ONLY COMMON-LISP:T :AUTO-PUBLISH COMMON-LISP:T :MODERATOR COMMON-LISP:T :RECORD-AUDIO COMMON-LISP:T :LANGUAGE :JA :RECORD-DIR "" :FFMPEG-PATH "" :TRIGGER-LOG COMMON-LISP:T :SERVER-URL "https://rappyruns-production.up.railway.app" :API-TOKEN "<REDACTED>")
```
- `T`/`NIL` は **`COMMON-LISP:T` / `COMMON-LISP:NIL`** と書かれる (`*package*` を KEYWORD にして prin1 しているため)。
- キー順序は不定 (新規キーは先頭に push される)。

### 3.3 queue.sexp (`store.lisp:290 save-queue!`)

- 中身: `*runs*` のうち **active なエントリだけ** (`entry-active-p`, §9.2) のリスト、新しい順。空なら `COMMON-LISP:NIL`。
- 各エントリ = 検出器のラン plist (§14.7) + ステータス系キー (§9.1)。`:status` が `:queued`/`:failed` 以外のエントリは `:telemetry` を除去して保存 (`persistable-entry`)。
- `:queued`/`:failed` エントリは **テレメトリ全量** を含む (長いランで MB 級)。
- 保存タイミング: `enqueue-run!`、`update-run!`、`clear-runs!` の都度 (全体上書き、非アトミック)。
- 読み込み: 起動時 `load-queue!` で `*runs*` の末尾に append。読めなければ黙って空。
- 時刻キー `:finished-at`、`:next-upload-at` は **CL universal time (1900-01-01 00:00:00 UTC 起点の秒)**。Unix 秒 = UT − 2208988800。
- `:video-path` は文字列 (namestring)。

### 3.4 移行方針 (推奨)

| ファイル | 推奨 |
|---|---|
| config.sexp | C# は **sexp を読み書き**できるようにし、少なくとも移行期間は同ファイルに互換形式で書き戻す (Lisp 版へのロールバック/ダウングレードで設定・トークンを失わないため)。書き込みは temp + `File.Replace` でアトミックに。未知キー温存。 |
| queue.sexp | 起動時に 1 回インポート (queued/failed/動画未送信エントリを失わないため)。以後は C# 独自形式でよいが、インポート済みの印として `queue.sexp.migrated` にリネーム等。ロールバック時の二重送信はサーバーの重複検出 (submitter × quest × time_ms) で吸収される。 |
| trigger-log.txt | 同パス・同行形式で追記 (モデレーター手順書・`quest-triggers.sexp` のコメントがこのパスを案内している)。 |
| data/quest-triggers.sexp | **sexp のまま読む** (旧アップデータは `data\*` をコピーするだけで、Lisp 版と C# 版が同じ data を共有する可能性がある)。JSON 版を追加で同梱するのは可。 |

---

## 4. S式 (sexp) の読み書き仕様

C# 実装に必要な最小サブセット。

### 4.1 Lisp 側の書き方 (`config.lisp:68 write-sexp-file`)
`with-standard-io-syntax` + `*print-readably*` nil + `*package*` = KEYWORD で `prin1`。帰結:
- キーワード: `:SERVER-URL` (大文字、コロン接頭)。
- `T` / `NIL`: `COMMON-LISP:T` / `COMMON-LISP:NIL`。空リストも `COMMON-LISP:NIL`。
- 文字列: `"..."`、エスケープは `\"` と `\\` のみ。非 ASCII (日本語クエスト名など) は UTF-8 生書き。改行も生で入り得る。
- 整数: 10 進 (負号可)。ユニバーサルタイムは 10 桁超。
- 浮動小数: 単精度は `0.7894558` の形 (`*read-default-float-format*` = single-float のため指数マーカーなし)。大きい/小さい値は `1.0e7` / `1.0e-5` 形式もあり得る。倍精度が出ると `1.5d0`。
- ドット対: alist が `((5 . 1234) ("Resta" . 2) (:MONOMATE . 1))` のように出る (キーは整数・文字列・キーワード)。
- 1 行に全部 (改行なし)。

### 4.2 Lisp 側の読み方 (`read-sexp-file`)
`*read-eval*` nil で `read` を 1 回。**`*package*` は束縛していない** (配布 exe では CL-USER 相当)。
C# が書いたファイルを Lisp 版が読めるための制約 (ダウングレード互換):
- キーワードは必ず `:` 付き。裸のシンボルを書かない (CL-USER に intern される)。
- 真偽は `T`/`NIL` または `COMMON-LISP:T`/`COMMON-LISP:NIL`。大文字小文字は自由 (リーダが upcase)。
- 浮動小数は必ず小数点を含める (`1` は整数になる)。`NaN`/`Infinity` を書かない。
- 文字列内の `"` と `\` をエスケープ。
- `#` で始まる表記 (`#(...)`, `#.`) を書かない。

### 4.3 C# リーダの要件
トークン: `(` `)` `.`(ドット対) 文字列 数値 (整数/小数/指数 `e`/`E`/`d`/`D`/`f`/`s`) キーワード `:X` パッケージ修飾シンボル `COMMON-LISP:T` `CL:NIL` 裸シンボル `T` `NIL`。`;` コメント (quest-triggers.sexp は `;;` コメント多数)。空白は改行含む。シンボルは大文字小文字を無視して比較。

---

## 5. `data/quest-triggers.sexp` とクエスト定義

### 5.1 ファイル形式
- 先頭 `;;;` コメント群のあと、**1 つのリスト** の中に plist エントリが並ぶ。131 エントリ (2026-09-24 時点)。`*read-eval*` nil、`*package*` = KEYWORD で読む。
- エントリキー:

| キー | 型 | 必須 | 意味 |
|---|---|---|---|
| `:slug` | string | ○ | サーバーのクエスト slug (サーバー seed の slugify と一致必須) |
| `:episode` | int | ○ | サイト上のエピソード 1/2/4 |
| `:names` | list of string | ○ | ゲーム内クエスト名 (日本語名を含むことあり: `"Maximum Attack E:森"`) |
| `:number` | int | (ほぼ全て) | ゲーム内クエスト番号 |
| `:start` | trigger | ○ | 開始条件 |
| `:end` | trigger | ○ | 終了条件 |

- トリガー形式 (内部表現):

| 内部 | JSON (サーバー) | 意味 | 件数 (builtin) |
|---|---|---|---|
| `(:register N)` | `{"type":"register","register":N}` | クエストレジスタ N の u16 が非 0 | 163 |
| `(:floor-switch F S)` | `{"type":"floor-switch","floor":F,"switch":S}` | フロア F のスイッチ S のビットが立つ | 52 |
| `(:warp-in)` | `{"type":"warp-in"}` | 人間 (NPC 除く) が floor>0 かつ非ワープ中 (**開始専用**) | 49 |
| `(:monster-dead ID)` | `{"type":"monster","monster":ID}` | その run 中に生存確認済みのエンティティ ID が 0HP になった (**終了専用**) | 2 (A New Hope solo 等) |

- JSON→内部変換 (`quests.lisp:73 json-trigger`): 未知の type は NIL。サーバーエントリは start と end の両方が非 NIL の場合のみ計測対象 (`server-quest->def`)。
- 内部→JSON (`api-client.lisp:616 trigger->json`): 上表のとおり。NIL → NIL。

### 5.2 読み込み場所
`exe-adjacent-path "data/quest-triggers.sexp"` (argv[0] のディレクトリ基準) → 無ければ ASDF ソースディレクトリ (開発時)。

### 5.3 サーバー定義とのマージ (`quests.lisp:46 recompute-quest-defs`)
- `*quest-defs*` = サーバー定義 (GET `/api/quests` のうち計測可能なもの) ++ builtin のうち slug がサーバー定義と衝突しないもの。**サーバーが勝つ**。順序はサーバー定義が先。
- サーバー定義は `check-server` 時 (起動時、設定保存時、言語切替による再構築時、ルール作成後) に全置換。オフライン時は builtin のみ。
- `unknown-slugs`: builtin slug のうちサーバーの slug 一覧に無いもの → ステータス表示に件数 (誤設定検出)。

### 5.4 マッチ (`quests.lisp:111 quest-def-matches-p`)
psostats 準拠: `number` が正でかつ定義の `:number` と `eql` ならマッチ (**エピソード不問**)。または `name` が非 NIL かつ (`episode` が NIL か定義の episode と一致) かつ名前が `:names` に完全一致 (`string=`)。
`find-quest-defs` は **全マッチを定義順で** 返す (フルクリア + セグメントカテゴリ「(2 Rooms)」等が同一クエストに複数)。
スナップショットに `:quest-name` が無い (quest-ptr 0 / data-ptr 0) 場合は空 (`detect.lisp:93 snapshot-quest-defs`)。

### 5.5 既知のデータ上の注意 (sexp 内コメントより)
- Mop-up/Patrol 系は psostats の register 開始だと受注〜P2 滞在 ~10 秒を含むため `(:warp-in)` 開始に変更済み。
- MAE エリア別 (930-940) は終了 `register 50` (254 はクリア 3〜22 秒後で、窓内で離脱されると立たない)。
- A New Hope は full-clear 定義なし (end 未判明)、solo カテゴリは `(:monster-dead 5475)` 終了。

---

## 6. HTTP 層 (WinHTTP の挙動) — `winhttp.lisp`, `api-client.lisp:152 http-request`

| 項目 | 値 | C# 注意 |
|---|---|---|
| User-Agent | `ephinea-ta-client` (WinHttpOpen の agent) | **必須**: GitHub API は UA 無しを 403 にする。`HttpClient` は既定で UA を送らない |
| プロキシ | `WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY`(4)、失敗時 `NO_PROXY`(1) | `HttpClientHandler` 既定 (システムプロキシ) で概ね同等 |
| TLS | SChannel、Windows 証明書ストア | .NET 既定で同等 |
| タイムアウト (通常) | resolve 10s / connect 10s / send 30s / receive 30s (**receive は ReadData 1 回ごと**) | `HttpClient.Timeout` は全体時間なので大きめ + 読み取り単位のキャンセルで近似 |
| タイムアウト (動画アップロード) | 10s / 10s / send 60s (WriteData 1 回毎) / receive 300s | サーバーがストレージへリレーし終えるまで応答しないため長い |
| タイムアウト (WebSocket 接続) | 10/10/3/10s、接続後 receive 無限 | §18 |
| リダイレクト | WinHTTP 既定で追従 | GitHub の旧リポ名 301 と asset の CDN リダイレクトに必須 |
| ヘッダ | `Authorization: Bearer <token>` (token 指定時)、`Content-Type: application/json` (body がある時のみ)、追加ヘッダ (GitHub: `Accept: application/vnd.github+json`) | |
| ボディ | UTF-8 | |
| 応答 | ステータスコード + UTF-8 文字列 | |
| エラー | WinHTTP 失敗は `"<Func> failed (Windows error N)"` の `api-error` | エラー文言から N を抜いてヒント表示 (§6.1) |
| リトライ | **HTTP 層では一切しない**。上位 (キュー・アップロード) の責務 | |

1 リクエストごとにセッション・接続を開いて閉じる (keep-alive 再利用なし)。

### 6.1 エラー表示ヒント (`api-client.lisp:40`)
`(Windows error N)`: 12007 → `:hint-address`、12029/12030 → `:hint-connect`、12002 → `:hint-timeout`、12157/12175 → `:hint-tls`。`"Bad URL"` を含む → `:server-bad-url`、`"-> "` を含む (想定外ステータス) → `:server-unexpected`。C# では `HttpRequestException`/`SocketError` から同等の分類を作る。

### 6.2 URL
- `api-url server path` = `server` の末尾 `/` を除去 + `path`。
- `parse-url`: `scheme://host[:port]/path`、scheme 無しは `api-error "Bad URL: ..."`。ポート不正は **api-error ではない** エラー (テストで固定済み: "parse-url junk port signals a non-api error (pinned)")。
- `valid-http-url-p`: `http://` か `https://` で始まり、空白・制御文字 (≤ U+0020) を含まない URL のみブラウザで開く (ShellExecute 防御)。
- `url-encode-component`: UTF-8 バイト単位、`[A-Za-z0-9-_.~]` 以外を `%XX` (大文字 16 進)。

---

## 7. API エンドポイント一覧と JSON スキーマ

ベース URL = config `:server-url`。認証は `Authorization: Bearer`。「token」列: L = 連携トークン (`:api-token`)、S = `submission-token` (L があれば L、なければ匿名ゲスト)、なし = 送らない。

| メソッド パス | token | リクエスト | 応答の扱い |
|---|---|---|---|
| GET `/api/quests` | なし | — | 200 → JSON 配列。それ以外 → api-error |
| POST `/api/pair` | なし | `{"label"?: string}` | 201 かつ `code` 文字列 → `(code, interval ?? 2, expires_in ?? 600)`。他 → api-error |
| GET `/api/pair/{code}` | なし | — | 200+`token` 文字列 → complete; 200 → pending; 404 → gone; 他 → api-error |
| POST `/api/login` | なし | `{"username","password","label"?}` | 201+`token` → ok; 401 → unauthorized; 他 → api-error |
| POST `/api/register-anonymous` | なし | `{"label"?}` | 201+`token` → `(token, username)`; 他 (レート制限含む) → api-error |
| POST `/api/merge-anonymous` | L | `{"anonymous_token": string}` | 200 → ok; 404 → gone; 他 → api-error |
| GET `/api/me` | L | — | 200 → user; 401 → unauthorized; 他 → api-error |
| POST `/api/me/auto-publish` | L | `{"enabled": 0|1}` (整数) | 200 → 成功; 401/他 → api-error |
| POST `/api/runs` | S | ラン JSON (§7.2) | 201 created / 200 duplicate / 400,403 rejected / 401 api-error("Invalid or revoked API token") / 他 api-error |
| POST `/api/quests` | L (モデレーター) | `{"parent","name","description","end"?,"start"?}` | 201 created / 409 duplicate / 403 forbidden / 400 rejected / 401,他 api-error |
| GET `/api/quests/{slug}/ghost?slugs=a,b&difficulty=X&party_size=N&pb=0` | S | — | 200 → ok+payload; 404 → none; 401,他 → api-error |
| GET `/api/quests/{slug}/pins?slugs=a,b` | L | — | 200 ok / 404 none / 401,他 api-error |
| POST `/api/pin-sets` | L | Pin Share 保存 body | 201 created / 200 updated / 400,403 rejected / 404 not-found / 401,他 api-error |
| POST `/api/pin-sets/{id}/items` | L | 同上 | 同上 |
| POST `/api/runs/{id}/video-file[?offset_ms=N]` | S | 生 mp4 ストリーム, `Content-Type: video/mp4`, Content-Length は WinHTTP が総長から付与 | 200/201 → attached (payload.`duplicate` 真なら duplicate); 400/403/404/409/411/413 → rejected; 401,他 → api-error |
| POST `/api/runs/{id}/diagnostics` | S | `{"log": string, "client_version"?: string}` | 200 → true、他 → false (ベストエフォート) |
| GET `https://api.github.com/repos/{repo}/releases/latest` | なし | `Accept: application/vnd.github+json` | §10 |
| WSS `wss://pin-share-server-production.up.railway.app` | — | Pin Share プロトコル (範囲外) | §18 |

### 7.1 応答 JSON の使用フィールド

- `/api/quests` 要素: `slug`, `name`, `episode`, `category`, `game_names` (配列), `game_number`, `start`/`end` (トリガー JSON または null)。
- `/api/me`: `username`, `role` (`"moderator"`/`"admin"` でモデレーター), `auto_publish` (0/1; `eql 1` で真), `features` (文字列配列; `"pinshare"` を含めば Pin Share 許可)。
- `/api/runs` 応答: `id` (int, server-id), `url`, `errors` (配列), `message`, `standing` = `{rank, parties, previous_best_ms?, delta_ms?}`。
- video-file 応答: `status` (`"held"`/`"approved"`/...), `duplicate` (真偽), `error` (`"pending-limit"` 等), `message`。
- ghost 応答: `quest`, `run_id`, `time_ms`, `submitter`, `source`, `pb`, `precision` (`"ms"` 以外は秒精度), `rooms[] {floor, room, nth, enter_ms, kills?}`, `track[]` (5 要素行) と `track_y[]` (高さ、別配列)。

### 7.2 POST /api/runs のリクエスト (`api-client.lisp:534 run-json`) — **契約/パリティ**

トップレベル (「条件」列が「非 NIL」= Lisp で NIL でない。**0 や "" でも送る**):

| JSON キー | 由来 | 条件 |
|---|---|---|
| `quest` | `:quest-slug` | 常に |
| `time_ms` | `:time-ms` (int ≥1) | 常に |
| `party_size` | `:party-size` | 常に |
| `pb` | `true` | `:pb` 真のときのみ (false は送らない) |
| `episode` | `:episode` (定義の episode) | 非 NIL |
| `difficulty` | `"Normal"`/`"Hard"`/`"Very Hard"`/`"Ultimate"`/`"Anguish N"` | 非 NIL |
| `death_count` | int (0 も送る) | 非 NIL (テレメトリがあれば常に数値) |
| `aborted` | `true` | 中断ランのみ |
| `account_mode` | `"sandbox"`/`"normal"` | 判定ありのみ (§13)。**名前色そのものは送らない** (テストで固定) |
| `unranked` | `true` | tracking-only 時 |
| `private` | `true` | tracking-only + tracking-private 時 |
| `submitter_section_id` | 文字列 (例 `"Skyly"`) | 非 NIL |
| `players` | 配列 `{name, class, level?, section_id?, guild_card?}` | 常に (人間のみ、開始フレームのパーティ) |
| `notes` | `"Auto-submitted by ephinea-ta-client (<quest-name or slug>[, aborted][, record only])"` | 常に |
| `telemetry` | §7.3 | テレメトリがあれば |

テスト "the run payload is exactly the known fields - no name colour" がキー集合を固定している。

### 7.3 telemetry オブジェクト (`api-client.lisp:445 telemetry-json`)

| キー | 型 | 備考 |
|---|---|---|
| `frame_keys` | 24 文字列配列 | `["t","hp","tp","pb","meseta","floor","room","x","z","shifta","deband","inv","state","monsters","kills","map_var","ft","dt","ct","damage","weapon","player_locs","monster_locs","map"]` |
| `frames` | 配列の配列 | §15.3。`player_locs`/`monster_locs` 列はオブジェクト化 |
| `death_count`, `kills`, `meseta_charged`, `tp_used` | int | 無ければ 0 |
| `items_used` | `{snake_key: count}` | キー `:moon-atomizer` → `"moon_atomizer"`。**count ≤ 0 は省く** |
| `techs_cast` | `{"Resta": n}` | 0 省略 |
| `time_by_state` | `{"<state-id>": ms}` | 0 省略 |
| `traps_used` | `{"dt"?,"ft"?,"ct"?}` | 0 省略 |
| `weapons` | 配列 `{id, type, display, seconds, attacks, techs}` | type = `"weapon"`/`"frame"`/`"barrier"`/`"unit"`/`"mag"`。登場順 (古い順) |
| `events` | 配列 `{t, type, floor?, room?, ms?}` | floor/room は 0 でも送る (Lisp で 0 は真) |
| `track` | 配列 `[ms, floor, map, x, z, y]` | 空なら省略 (6 要素; サーバーは 5 要素互換で読む) |
| `monsters` | 配列 `{id, unitxt_id, spawn_ms, name?, killed_ms?, frame1?:true}` | 空なら省略 |
| `bosses` | `{"<monster-id>": {name, unitxt_id, spawn_t, hp:[...], killed_t?}}` | 空なら省略 |
| `player_damage` | `{"<player-index>": hp}` | 0 省略 |
| `last_hits` | `{"<player-index>": n}` | 0 省略 |
| `monster_hp_pool` | int 配列 (フレーム毎) | 常に |
| `max_party_pb_shifta` | int | 非 NIL 時 |
| `illegal_shifta`, `fast_warps` | `true` | 真のときのみ |

JSON の数値: 浮動小数 (座標等) は **単精度の最短表現** (`12.3`)。C# で `double` に変換してから直列化すると `12.300000190734863` になる → `float` のまま `System.Text.Json` に渡す (最短往復表現) こと。

### 7.4 ゴーストのクエリ (`api-client.lisp:679 fetch-ghost-splits`)
パス `/api/quests/{slug}/ghost` + `?` + `&` 連結: `slugs=<他カテゴリ slug をカンマ連結、エンコードなし>`、`difficulty=<url-encode>`、`party_size=N`、`pb=N`。パラメータ無しなら `?` も付けない。クライアントは常に `pb=0` (開始時点では No PB が確定しているため)。フェッチはクエストロード毎に 1 回 (quest-ptr で消費)、応答到着時に同じロードが続いている場合のみ採用。条件: 定義マッチあり・`:ghost-race` 真・`submission-token` 非空。

---

## 8. 認証・ペアリング・login.txt・匿名ゲストとマージ

### 8.1 トークンの種類
- `:api-token`: 連携済み。空 = 「未連携」(サポートされた状態、エラーではない)。
- `:anon-token`: 匿名ゲスト。未連携でも計測・送信できるよう、**最初の送信直前**に自動登録。サーバー上では private (共有 URL のみ)。
- `submission-token` (`api-client.lisp:302`): 正規化した api-token が非空ならそれ、でなければ正規化 anon-token、でなければ `""`。ラン送信・動画アップロード・診断・ゴーストで使用。
- 正規化 (`normalize-token`): 前後の Space/Tab/CR/LF を除去、NIL → `""`。

### 8.2 ブラウザペアリング (`gui.lisp:783 run-pairing-flow`) — ボタン起点のみ
1. `POST /api/pair {"label": "Desktop client (<MachineName>)"}` (マシン名が取れなければ `"Desktop client"`)。
2. ブラウザで `<server>/pair?code=<code>` を開く。
3. `ceiling(expires_in / interval)` 回、`interval` 秒待って `GET /api/pair/{code}`。
   - 停止要求、または待機中に api-token が設定された → 中止。
   - transport エラー (api-error) は pending 扱いで継続。
   - gone → 「期限切れ」、complete → `finish-pairing(token)`。
   - 回数を使い切ったら「期限切れ」。
4. 開始自体の失敗 → 赤字で失敗表示 (次回起動で再試行)。
5. 同時に 1 ワーカーのみ。

### 8.3 login.txt (`credentials.lisp`, `gui.lisp:836`)
- 場所: exe と同じフォルダの `login.txt` (`lw:lisp-image-name` 基準)。
- 解析 (`parse-credentials`): 行分割 (`\n`)、各行を Space/Tab/CR/U+FEFF でトリム、空行と `#` 始まりを無視、最初の `=` で分割、キーは小文字化・トリム、`username`/`password` のみ採用 (後勝ち)。両方非空でなければ (NIL, NIL)。**パスワードに `=` を含められる**。UTF-8 で読めなければ (NIL, NIL)。
- フロー: 読めない → `:file-login-bad-file` 赤字。`POST /api/login {username, password, label: "Desktop client (<Machine>) [login.txt]"}` → ok なら `finish-pairing`、401 → `:file-login-invalid`、エラー → `:file-login-failed`。ブラウザペアリングは決して併発させない。
- 起動条件: (a) api-token 空 + login.txt あり (起動時)、(b) `check-token` が 401 を得た + login.txt あり (失効トークンの自己修復)。

### 8.4 匿名ゲスト (`store.lisp:66 ensure-submission-token`)
- `submit-queued!` の冒頭で呼ばれる。`submission-token` が非空ならそれを返す。
- 空なら `POST /api/register-anonymous {"label": "Desktop client (<Machine>) [guest]"}` (マシン名なし時 `"Desktop client [guest]"`)。成功で `:anon-token` 保存 → config 保存。
- **あらゆるエラー**で NIL (「今は無理」) → キューは `:queued` のまま温存、次のパスで再登録を試みる。

### 8.5 check-token とマージ (`gui.lisp:1486 check-token`)
- api-token 空: Pin Share 許可を偽に、`:token-unlinked` 表示。ネットワークなし。
- 非空: 別スレッドで `GET /api/me`。
  - ok: `:token-ok <username>` 表示 → Pin Share 許可 = features に `"pinshare"` → モデレーター判定変化時のみ config 保存 + ウィンドウ再構築 → `auto_publish` 同期 → **anon-token が非空なら `POST /api/merge-anonymous`**、ok/gone で `:anon-token` を `""` にして保存、api-error なら温存 (次回検証で再試行) → `*retry-requested*` を立てる (キューの一括再送)。
  - unauthorized: Pin Share 許可偽、`:token-invalid` 赤字、`on-invalid` 実行 (login.txt 自己修復)。
  - エラー: `:token-could-not-verify` 赤字 (トークン自体は疑わない)。
- 呼ばれるタイミング: 起動時、設定保存時 (`notify` でダイアログ)、ペアリング/ログイン完了時、言語切替の再構築時。
- `finish-pairing`: トークン保存 → config 保存 → トークン欄更新 → `check-token`。

---

## 9. ラン キュー (store) の意味論

### 9.1 エントリのキー

| キー | 付与 | 意味 |
|---|---|---|
| (検出器ランの全キー) | 検出時 | §14.7 |
| `:ghost-delta-ms`, `:ghost-time-ms`, `:ghost-label` | `annotate-ghost-runs` (enqueue 前) | ゴーストがそのランをカバーする場合 |
| `:video-offset-ms` | 録画が `nconc` でラン plist 末尾に追加 (**enqueue 前**、同一フレームの recorder-step が先に走る) | 動画上のタイマー開始位置 |
| `:unranked`, `:run-private` | `apply-tracking-mode` (enqueue 時に 1 回だけ) | 後の設定変更で再スタンプしない。中断ランは対象外 |
| `:status` | enqueue で `:queued` | `:queued` / `:submitted` / `:duplicate` / `:rejected` / `:failed` |
| `:url`, `:server-id` | 送信結果 | created/duplicate |
| `:standing-rank`, `:standing-parties`, `:standing-prev-ms`, `:standing-delta-ms` | created の `standing` | prev/delta は両方整数の時のみ |
| `:reason` | rejected/failed | rejected: `"<message> <errors を "; " 連結>"`、failed: api-error 文言 |
| `:video-path` | 録画保存時 (`link-video-file!`) | |
| `:video-attached`, `:video-uploaded`, `:held`, `:approved` | アップロード成功 | held/approved は応答 `status` |
| `:next-upload-at` | バックオフ | universal time |
| `:upload-given-up`, `:upload-error` | 恒久失敗 / ファイル消失 / api-error 12 連続 | |
| `:upload-failures` | api-error の連続回数 (C# 追加、S17) | サーバーが応答すれば消す。Lisp は無視 |
| `:video-url` | (手動 URL 添付; GUI 側) | `hosted-video-replaceable-p` 判定用 |

### 9.2 active 判定 (`store.lisp:25 entry-active-p`) — 永続化・トリム対象外の条件
`:status ∈ {:queued, :failed}`、または (`:video-path` かつ `:server-id` かつ ¬`:aborted` かつ ¬`:unranked` かつ ¬`:video-attached` かつ ¬`:upload-given-up`)。

### 9.3 操作
- `enqueue-run!`: 先頭に `(:status :queued . run)` を push → トリム (finished は最新 50 件 `+max-finished-runs+`、active は無制限) → 保存。
- `update-run!` (`store.lisp:344`): コピーにキーを上書き → status が submitted/duplicate/rejected なら **`:telemetry` をメモリからも削除** (8GB 機でのヒープ肥大対策) → ロック下で `*runs*` 内の **EQ 一致** エントリを置換 → トリム → 保存。
  - 注意: 古いコピーで update すると置換対象が見つからず**黙って失われる** (返り値だけ新しい)。C# は安定 ID (GUID) で管理すること。
- `same-run-p`: `(quest-slug, time-ms, finished-at)` の一致 = 自然キー (録画→エントリ紐付けに使用)。
- `clear-runs!`: unsent (queued/failed) 以外を全削除 → 保存。サーバー上のドラフト・ディスク上の録画は残る。
- `load-queue!`: 起動時に末尾 append。

### 9.4 送信 (`submit-queued!`, `submit-entry!`, `submission-updates`)
- `ensure-submission-token` が NIL → 何もしない (NIL)。
- `:queued`/`:failed` 全件を順に `POST /api/runs`。結果:
  - created → `:submitted`, url, server-id, standing。
  - duplicate (200) → `:duplicate`, url, server-id。
  - rejected (400/403) → `:rejected`, reason。
  - api-error (401 含む) → `:failed`, reason。
- **いつ再送されるか**: (a) 新しいランが完了したとき (`handle-completed-runs` → `submit-queued!`、`:auto-submit` 強制真)、(b) 「保留中を送信」ボタン、(c) `check-token` 成功時 (`*retry-requested*`)。**定期的な自動リトライは無い**。
- 送信は poll スレッド上で同期 (最大 10+10+30+30 秒ブロックし得る)。
- 送信後 `notify-standing-toasts` (§9.6)。

### 9.5 動画アップロード (`upload-candidate`, `upload-entry-video!`, `main.lisp:172 maybe-start-upload`)
- 開始条件: `:video-upload` (強制真) かつ ¬`*poll-busy-p*` (クエスト中/録画中でない) かつ レコーダ `:idle` かつ 前のアップロードスレッドが死んでいる。GUI ティック (250ms) 毎と未アタッチ時の検索ループ毎に評価。
- 候補: キューを**古い順**に走査し、`video-path ∧ server-id ∧ ¬aborted ∧ ¬unranked ∧ ¬video-attached ∧ ¬upload-given-up ∧ (next-upload-at 無し or ≤ now)`。ファイルが消えていれば `:upload-given-up t` にして次へ (Lisp は GUI 再描画要求を返す。C# はその更新が `Changed` を上げるので候補だけを返す)。
- 結果処理:
  - いずれの結果でもまず診断 (`POST /diagnostics`, 録画ログ末尾 64KB + マシン概要) をベストエフォート送信。
  - attached/duplicate → `:video-attached t :video-uploaded t :held (status=="held") :approved (status=="approved")`。**ローカルファイルは消さない** (保持期間スイープに任せる)。
  - rejected: `error == "pending-limit"` → `:next-upload-at now+3600`、それ以外 → `:upload-given-up t :upload-error <message|error|"rejected">`。
  - api-error → `:next-upload-at now+300`, `:upload-error`, `:upload-failures +1`。C# は 12 回連続 (約 1 時間) で `:upload-given-up t` にして止める (S17、Lisp は無限に再送)。回数は queue.sexp に残るので再起動をまたぐ。api-error 以外の応答 (attached/duplicate/rejected) で回数を消す。ホスト名が引けない失敗 (オフライン) は回数に数えない。諦めた回だけは診断を送る。クライアントに手動の再アップロード操作は無いので、諦めた動画はサイトで手動添付する。
- 進捗: `*upload-progress*` = `(server-id done total)`、整数 % が変わった時だけ再描画。
- 保持期間 (`apply-recording-retention`, 120 秒毎、レコーダ idle 時のみ): 上限超過で、protected (アップロード待ち) は除外、uploaded (attached) を先に、各層内は古い順に削除。

### 9.6 表示ヘルパ (純粋、テストあり; GUI 仕様と共有)
`format-run-time` (`m:ss.mmm`、1 時間超も分表記 `65:00.000`)、`format-split-clock` (`m:ss`)、`format-improvement-ms` (<60s は `"%.2fs"`、以上は run-time)、`run-standing-note` (PB 比較を先頭、他パーティ ≥2 のときだけ順位 `#r of n`)、`standing-toast` (1 位かつ parties≥2 / 3 位以内かつ rank<parties / PB 更新 / 初記録 の順で最初の 1 つ)、`ghost-toast` (standing トーストが無い時のみ、ゴーストに勝った時)、`run-status-label`、`run-video-label`。

---

## 10. 自己アップデータと「旧 Lisp クライアント → C# exe」移行

### 10.1 定数 (**契約**)
- リポジトリ: `PSOBBAITools/rappyruns-client` (config `:update-repo` で上書き可)。旧名 `psobb-teapot/rappyruns-client`, `rappyruns-client-releases`, `ephinea-ta-client-releases` は GitHub リダイレクト頼み。**これらの名前で新リポジトリを作らない・fork しない** (リダイレクトが壊れ、旧クライアントが更新不能になる)。
- 資産名: `RappyRunsClient.zip` (サイトのダウンロードボタンも同じ)。
- exe 名: `RappyRunsClient.exe` (実行中の exe 名に関わらずこの名前でインストール)。

### 10.2 発見 (`fetch-latest-release`, `parse-release-json`)
`GET https://api.github.com/repos/{repo}/releases/latest` (UA `ephinea-ta-client`, `Accept: application/vnd.github+json`, 認証なし = 60 回/時制限)。200 以外・通信失敗 → NIL (「今日は更新なし」)。
解析: `tag_name` が文字列、`assets` 配列のうち `name == "RappyRunsClient.zip"` の最初の要素の `browser_download_url` (文字列必須)、`size` (正の整数なら採用)。
- `/releases/latest` はプレリリースとドラフトを返さない → プレリリースで先行テスト可能。

### 10.3 バージョン比較 (`version.lisp`)
- `parse-version`: 先頭の `v`/`V` を任意で除き、`.` 区切りでちょうど 3 つの非負整数。`-rc1` 等の接尾辞・2 要素・4 要素・空は NIL。
- `update-available-p current latest` = 両方解析でき、かつ数値比較で current < latest。dev (`NIL`) は常に偽。
- `startup-update-decision`: release NIL → `:check-failed`、更新なし → `:up-to-date`、install dir 書込不可 → `:not-writable`、それ以外 → `:apply`。

### 10.4 ダウンロードと検証 (`download-update!`, `valid-update-zip-p`)
- 保存先 `%TEMP%\RappyRunsClient-update.zip`。200 のときだけファイル作成 (64KB チャンク、進捗は MB 単位で表示)。
- 検証: ファイルサイズ == API の `size` (size が無ければ省略) **かつ** 先頭 4 バイト == `50 4B 03 04` (`PK\3\4`)。
- **署名・ハッシュ検証は無い**。失敗時はファイル削除して NIL。
- 書き込み可否: `<exeDir>\eta-write-probe.tmp` を作って消せるか (Program Files 下で非昇格なら不可 → 手動ダウンロード案内)。

### 10.5 適用 (`launch-updater-and-quit`, `updater-script-text`)
1. `%TEMP%\rappyruns-update.ps1` を **UTF-8 BOM 付き**で書く (BOM なしだと PowerShell 5.1 が ANSI(cp932) と解釈し、OneDrive デスクトップ等の非 ASCII パスで swap が全滅した)。
2. `powershell.exe -NoProfile -ExecutionPolicy Bypass -File <script>` を起動。
3. poll スレッドを止め (最大 10 秒 join、録画を安全に終了)、ウィンドウ破棄、`lw:quit`。
4. スクリプト (パスはすべて `'...'` 単一引用、`'` は二重化):
   - Transcript を `%TEMP%\rappyruns-update.log` へ。
   - 旧 PID を `Wait-Process -Timeout 60`、終わらなければ例外 (`$stillRunning`=真、再起動しない)。
   - stage (`%TEMP%\rappyruns-update-stage`) を消して `Expand-Archive`。
   - `stage\RappyRunsClient.exe` が無ければ例外。
   - 実行中だった exe を `<exe>.old` へ Move (最大 10 回、1 秒間隔リトライ)。
   - `Copy-Item stage\RappyRunsClient.exe <installDir>\RappyRunsClient.exe`。
   - `stage\data` があれば `<installDir>\data` を作り `data\*` を再帰上書きコピー (**マージ、古いファイルは消さない**)。
   - `stage\ffmpeg` があれば同様 (失敗しても続行)。
   - `Start-Process <target> -WorkingDirectory <installDir>` (**引数なし**)。
   - zip・stage を削除。
   - catch: `.old` があり exe が無ければ (新名と旧名が違えば新名 exe を消してから) `.old` を戻す。旧プロセスが残っていなければ旧 exe を起動。
5. 起動時 `cleanup-old-update-files`: `<exeDir>\RappyRunsClient.exe.old`、`%TEMP%\rappyruns-update.ps1`、`%TEMP%\RappyRunsClient-update.zip`、`%TEMP%\rappyruns-update-stage\` を削除。

### 10.6 実行タイミング
- 起動時 (`:auto-update` 真 & リリースビルド): メインウィンドウ前にスプラッシュでダウンロード → 即適用 (**確認ダイアログなし、無人**)。失敗は `:download-failed` として後で表示。
- 設定の「更新を確認」ボタン: 全結果をダイアログで報告。ダウンロード後、`*poll-busy-p*` (クエスト中 or 録画中) なら `*update-ready-zip*` に保留し、`note-poll-activity` がアイドルになった最初のフレームで 1 回だけ適用。
- **定期チェックは無い** (起動時とボタンのみ)。

### 10.7 旧 Lisp クライアントは次のリリースが C# exe でもインストールできるか — 結論と条件

**結論: できる。ただし以下の条件をすべて満たす必要がある。**

| # | 条件 | 理由 |
|---|---|---|
| 1 | タグは `vX.Y.Z` 厳密形式で `0.60.x` より大 (例 `v1.0.0`) | parse-version が接尾辞を拒否 |
| 2 | 非プレリリースで公開、資産名 `RappyRunsClient.zip` | `/releases/latest` と資産名一致 |
| 3 | zip のルートに `RappyRunsClient.exe` | スクリプトが `stage\RappyRunsClient.exe` を要求 |
| 4 | **exe 単体で起動できること** (self-contained single-file、ネイティブ DLL 同梱 = `PublishSingleFile` + `SelfContained` + `IncludeNativeLibrariesForSelfExtract`) | ヘルパーは exe・`data\*`・`ffmpeg\*` **しかコピーしない**。ルートの DLL、`*.runtimeconfig.json`、`WebView2Loader.dll`、`runtimes\` 等は捨てられる。framework-dependent だと .NET 未導入 PC で起動不能 |
| 5 | 追加ファイルが必要なら `data\` 配下に置く (例 `data\ui\*.html`、`data\webview2\WebView2Loader.dll`) | `data\*` は再帰コピーされる (package.ps1 のコメントも同じ手法で Pin Share アドオンを既存インストールへ届けている) |
| 6 | 引数なし起動で正常動作 | 再起動は引数なし (`--minimized` も付かない) |
| 7 | 起動時に `RappyRunsClient.exe.old` と `%TEMP%` の残骸を削除 | 旧 exe (約 50MB) が残る |
| 8 | WebView2 Runtime 不在時に落ちずに案内/ブートストラップ | 失敗しても**ロールバックされない**: 新 exe が起動さえすればスクリプトは成功扱い。ユーザーは `.old` を手動で戻すしかない |
| 9 | サイズ: Content-Length は DWORD (< 4GB)、受信タイムアウトは ReadData 単位 30 秒 | 実質制限なし (現行 zip 66MB、ffmpeg 同梱) |
| 10 | zip は Expand-Archive (PS 5.1) で展開できる通常の deflate zip | Compress-Archive 製はエントリ名の区切りが `\` (現行 zip も `data\quest-triggers.sexp`)。C# 側の自前展開で `\` を扱えること |
| 11 | 旧 exe 名が `RappyRunsClient.exe` 以外 (例 `RappyRunsClient (1).exe`) のインストールでは、新 exe は正規名で置かれ、旧名は `.old` になる | 自動起動レジストリの値は旧名パスを指したまま → C# 起動時に自動起動が有効なら正規パスで書き直すのが望ましい |
| 12 | APPDATA の config.sexp / queue.sexp を読めること | 設定・トークン・未送信ランの引き継ぎ (§3.4) |

その他の注意:
- 旧版の起動時自動更新は**全ユーザーに一斉・無人**で C# を入れる (auto-update 既定 ON)。段階展開したい場合は、まずプレリリースで配布し、テスターは config.sexp に `:UPDATE-REPO "owner/test-repo"` を書いて別リポジトリから取得する手が使える (ただしプレリリースは `/latest` に出ないので、テスト用リポジトリに通常リリースとして置く)。
- 実行中 exe は Windows 上で**リネーム可・削除/上書き不可**。C# の新アップデータは PowerShell を使わず「自分を `.old` にリネーム → 新 exe を配置 → 再起動」も可能。PowerShell 版を踏襲するなら BOM を忘れない。
- ダウングレード (C# に致命的問題 → Lisp 版を再リリース) に備え、C# アップデータも「zip ルートの exe + data + ffmpeg」を扱える汎用性を保つ。バージョン比較は同じ semver 規則で、より大きいタグを付けて出す。
- SmartScreen/MOTW: WinHTTP ダウンロード + Expand-Archive では MOTW は付かない。C# 実装で `HttpClient` を使っても同様。
- サイトのダウンロードボタンも同じ資産を指すので、新規ユーザーも同じ zip を手動展開する。

---

## 11. プロセスアタッチ・Authenticode・メモリ読み取り

### 11.1 ウィンドウ探索 (`win32.lisp:222,267`)
- 対象タイトル (完全一致): `"Ephinea: Phantasy Star Online Blue Burst"`、`"PHANTASY STAR ONLINE Blue Burst"`。
- まず `FindWindowW(NULL, title)` を順に (アイドル時の安価なガード; 見つからなければ列挙しない)。
- 見つかれば `EnumWindows` で全トップレベル窓の `GetWindowTextW` (256 文字) がどちらかに完全一致するものを `(hwnd, pid)` で収集 (2 窓プレイ: ショップ + プレイ)。列挙失敗時は FindWindow の 1 つに fallback。
- 見つからない間は 1 秒毎に再探索。

### 11.2 オープン (`open-reader-for`)
`OpenProcess(PROCESS_VM_READ (0x10) | PROCESS_QUERY_INFORMATION (0x400), FALSE, pid)`。失敗時は **直後に** `GetLastError` を取得し、`(pid, err)` が前回と同じなら再ログしない。err 5 なら「管理者として実行」ヒント。ウィンドウタイトル (`GetWindowTextW`) を保持 (録画用)。

### 11.3 候補選択 (`choose-psobb-reader`)
- 候補 1 つ: そのまま返す (信頼判定は poll 側)。
- 複数: 各候補を **ファイルベースの Authenticode のみ** で判定 (メモリは読まない)。信頼ゼロ → 最初の非信頼候補を未読のまま返す (poll 側で「非公式」と報告させる)。信頼 1 → それ。信頼複数 → quest-ptr (`0x00A95AA8`) が非 0 の最初のもの、なければ最初。選ばれなかった reader は閉じる。

### 11.4 信頼判定 (`main.lisp:52 psobb-trust-rejection`, `win32.lisp:520 authenticode-verify`, `psobb.lisp:837`)
- 実行ファイルパス: `QueryFullProcessImageNameW(handle, 0, buf[1024])`。取得失敗 → `:invalid` (fail closed)。
- `WinVerifyTrust(NULL, WINTRUST_ACTION_GENERIC_VERIFY_V2 {00AAC56B-CD44-11d0-8CC2-00C04FC295EE}, data)`:
  - `dwUIChoice = WTD_UI_NONE (2)`, `fdwRevocationChecks = 0`, `dwUnionChoice = WTD_CHOICE_FILE (1)`, `dwStateAction = WTD_STATEACTION_VERIFY (1)`, `dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL (0x1000)` (ネットワークに出ない)。
  - 戻り 0 → `:valid`、署名者 CN = `WTHelperProvDataFromStateData` → `WTHelperGetProvSignerFromChain(prov, 0, FALSE, 0)` → `pasCertChain[0].pCert` → `CertGetNameStringW(cert, CERT_NAME_SIMPLE_DISPLAY_TYPE=4, 0, NULL, buf, 256)`。
  - `0x800B0100 (TRUST_E_NOSIGNATURE)` → `:unsigned`。その他 → `:invalid`。
  - 最後に必ず `WTD_STATEACTION_CLOSE (2)` で再呼び出し。
  - タイムスタンプ副署名により期限切れ証明書でも `:valid` になる → だから拇印ではなく CN を固定。
- 受理: status `:valid` かつ CN ∈ `{"Terry Chatman"}` (完全一致)。それ以外は拒否 → **アタッチしない = 検出・録画・送信すべてしない**。
- 拒否は `(pid, path, status, signer)` として保持し、同じ pid では再ハッシュしない。PSOBB 窓が消えたらクリア。
- 拒否理由表示: unsigned → 「署名なし」、valid (=CN 不一致) → 「署名者 "X"」、他 → 「検証できません」。
- アタッチ成功で `*audio-target-pid*` (録画音声) と `*pinshare-game-exe*` (画像パス) を設定。

### 11.5 読み取り (`read-block`)
- `ReadProcessMemory(handle, addr, buf, size, &read)`; 成功かつ `read == size` のみ成功。失敗は NIL を返し、`GetLastError` を `last-read-error` に保存 (単発失敗は正常: ワープ・リロード中の null ポインタ追跡)。
- 64bit プロセスから 32bit PSOBB をクロスビット読み取り (アドレスは 32bit)。
- 生存確認: `GetExitCodeProcess == 259 (STILL_ACTIVE)`。
- 連続 60 フレーム (≈2 秒) スナップショット無し → ログ 1 回 + GUI「接続済みだが読めない (管理者で実行)」。
- 全マルチバイト値はリトルエンディアン。

### 11.6 デコード関数 (`memory.lisp`)
- u16/u32 LE。
- `u32-float`: IEEE754 単精度。指数 255 (Inf/NaN) は `±3.4e38` にクランプ、非正規化数対応。C# は `BitConverter.Int32BitsToSingle` + Inf/NaN クランプ (NaN の符号ビットに従う)。
- `u64-double`: 同様、指数 2047 → `±1.7e308`。
- `decode-utf16-z`: UTF-16LE を NUL まで (BMP のみ、サロゲート結合なし)。
- `decode-ascii-z` (guild card): NUL まで、印字可能 ASCII (32..126) のみ採用、他はスキップ。

---

## 12. PSOBB メモリレイアウト (**パリティ**、`psobb.lisp`)

出典は psostats-client (MIT) の転記。全アドレスは Ephinea 32bit クライアント用。

### 12.1 グローバル

| 名前 | アドレス | 型 | 用途 |
|---|---|---|---|
| base-player-array | `0x00A94254` | u32 × 12 | プレイヤー構造体ポインタ (スロット 0..11)。48 バイト 1 回で読む (失敗時スロット毎) |
| my-player-index | `0x00A9C4F4` | u8 | 自分のスロット。**読めなければスナップショット全体が NIL** |
| episode | `0x00A9B1C8` | u16 | raw 0→1, 1→2, 2→4, 3→4, 他→NIL (`read-episode`) |
| quest-pointer | `0x00A95AA8` | u32 | クエスト構造体。0 = クエスト未ロード |
| floor-switches | `0x00AC9FA0` | 32 B × 18 フロア | |
| difficulty | `0x00A9CD68` | u16 | 0=Normal..3=Ultimate |
| current-map | `0x00AAFC9C` | u16 | 実マップ番号 (エリア名) |
| map-variation | `0x00AAFC98` | u16 | |
| camera-position | `0x00A48780` | f32×3 | (+dir を含め 24B 1 回) |
| camera-direction | `0x00A4878C` | f32×3 | |
| camera-zoom | `0x009ACEDC` | u32 | 0-4 |
| fast-burst | base `0x5B92DA`, step `0x5B92DF` | | `a = *(u32*)0x5B92DA; p = *(u32*)(a+0x5B92DF); fast = (*(u16*)p == 0)` |
| item-array-pointer | `0x00A8D81C` | u32 | |
| item-array-count | `0x00A8D820` | **u16 として読む** | 0..4096 のみ受理 |
| pmt-pointer | `0x00A8DC94` | u32 | ItemPMT |
| unitxt-pointer | `0x00A9CD50` | u32 | 文字列テーブル |
| npc-array-pointer | `0x007B4BA2` | u32 (非整列) | エンティティポインタ配列 |
| npc-count | `0x00AAE164` | u32 | 0..512 |
| player-count | `0x00AAE168` | u32 | 0..12 |
| ephinea-monster-hp-table | `0x00B5F800` | u32 | HP テーブル (stride 32) |
| ephinea-hp-scale | `0x00B5F804` | f64 | Anguish 判定 |

### 12.2 クエスト構造体
- `quest-ptr + 0x19C` → data-ptr (u32)、`quest-ptr + 0x2C` → register-ptr (u32)。
- data: `+0x10` quest number (u16)、`+0x18` quest name UTF-16 最大 64 バイト (32 文字)、前後の空白 (`" "`) をトリム。
- registers: 256 個、stride 4、値は各先頭 u16。1024 バイト 1 回で読む。`register-set-p(i)` = `u16@4i != 0` (範囲外は偽)。
- floor switch: `byte = switches[32*floor + switch/8]`、`mask = 0x80 >> (switch % 8)` (**MSB が switch 0**)。

### 12.3 プレイヤー構造体 (ブロック `[+0x028, +0xE50)` = 0xE28 B を 1 回で読む)

| オフセット | 型 | フィールド |
|---|---|---|
| 0x028 | u16 | room |
| 0x038/0x03C/0x040 | f32 | x / y / z |
| 0x060 | u16 | facing |
| 0x278 | f32 | shifta 倍率 → `shifta-level` |
| 0x284 | f32 | deband 倍率 → `shifta-level` |
| 0x2BC / 0x2BE | u16 | max HP / max TP |
| 0x334 / 0x336 | u16 | HP / TP |
| 0x33E | u16 | state ビット、`& 0x04` = warping |
| 0x348 | u16 | action state (5,6,7 = 攻撃、8 = テク詠唱) |
| 0x3F0 | u16 | floor (クエストのフロアスロット) |
| 0x428 | UTF-16 24 B (12 文字) | name、先頭が Tab なら 2 文字 (`\tE` 言語マーカー) を除去 |
| 0x464 | u16 | current tech id |
| 0x520 | f32 | PB ゲージ |
| 0x720 | u32 | 無敵フレーム残 (>0 で invincible) |
| 0x89C / 0x89D / 0x89F | u8 | damage / freeze / confuse トラップ数 |
| 0x930 | ASCII 8 B | guild card (空白トリム、空なら NIL) |
| 0x948 | u32 ARGB | name color (§13) |
| 0x960 | u16 | class = bits 8-11、section id = bits 0-7 |
| 0xE44 | u16 | level (0 起点 → +1) |
| 0xE4C | u32 | meseta |

- クラス ID: 0 HUmar, 1 HUnewearl, 2 HUcast, 3 RAmar, 4 RAcast, 5 RAcaseal, 6 FOmarl, 7 FOnewm, 8 FOnewearl, 9 HUcaseal, 0xA FOmar, 0xB RAmarl。
- セクション ID: 0 Viridia, 1 Greenill, 2 Skyly, 3 Bluefull, 4 Purplenum, 5 Pinkal, 6 Redria, 7 Oran, 8 Yellowboze, 9 Whitill。
- クラス別最大シフタ (psostats): HUmar 3, HUnewearl 20, HUcast 3, HUcaseal 3, RAmar 15, RAmarl 20, RAcast 0, RAcaseal 0, FOmar/FOmarl/FOnewm/FOnewearl 30。
- `max-party-pb-shifta(party)` = `max(21 + 20*(size-1), 各メンバーのクラス最大)` (size は最低 1、NPC 含むパーティ)。
- `shifta-level(m)`: m が NIL/0 → 0。それ以外 `level = 1 + floor((|m|*100 - 10)/1.3 + 0.5)`、m<0 なら負。**単精度演算**で再現すること (境界値パリティ)。
- NPC: `npc = guild_card が存在し、かつ ASCII 数字以外を含む` (`npc-guild-card-p`)。guild card 無し (空) は人間扱い。NPC の guild card 欄は名前の先頭 (`"Mr.X"`, `"Ch@osM@g"`, `"Heatclif"`)。
- `read-players`: スロット順を保つ。ポインタ 0 と読めない構造体は除外。各要素に `:index` (スロット番号)。

### 12.4 スナップショット (`read-snapshot`)
```
{ episode, my-index, players[], difficulty(u16 raw), map, map-variation,
  fast-burst(bool), quest-ptr(0 if unreadable),
  // quest-ptr != 0 のときだけ:
  quest-name(trimmed|NIL), quest-number(|NIL), anguish(1..3|NIL),
  registers(1024B|NIL), floor-switches(576B|NIL) }
```
poll 側で追加 (`augment-snapshot`、quest-ptr≠0 のときのみ): `monsters` (毎フレーム)、`camera` (`:ghost-overlay` ∧ `:ghost-marker` のとき毎フレーム)、`inventory` (1 秒に 1 回)。各読み取りの例外は NIL に握りつぶす。

### 12.5 Anguish (`psobb.lisp:169-197`)
HP テーブルポインタ非 0 のとき f64 スケールを読み、`> 1.15` なら `{1:1.30, 2:1.82, 3:2.50}` の最も近いレベル。`difficulty-label` = difficulty 名が `"Ultimate"` かつ anguish ありなら `"Anguish N"`、それ以外は名前 (範囲外 NIL)。

### 12.6 インベントリ (`read-inventory`)
- count (u16) と array ptr。array から `4*count` バイトのポインタ列。各アイテムは `[+0xE4, +0x191)` (173 B) を 1 回で読む。
- 項目: owner `+0xE4` u8, type `+0xF2`, group `+0xF3`, index `+0xF4`, tool count `+0x104` (値 = `raw XOR ((item_addr + 0x104) & 0xFF)`), equipped `+0x190` (bit0)。
- `owner == my-index ∧ equipped 奇数` → 装備 (`read-equipped-item`): id = `sprintf("%04X%04X", u16@+0xDA, u16@+0xD8)` (大文字 16 進、word1 が上位)。type 0 weapon / 1+group1 frame / 1+group2 barrier / 1+group3 unit / 2 mag。
- `type == 3 ∧ owner == my-index` → 消耗品 (group,index): (0,0) monomate, (0,1) dimate, (0,2) trimate, (1,0) monofluid, (1,1) difluid, (1,2) trifluid, (3,*) sol-atomizer, (4,*) moon-atomizer, (5,*) star-atomizer, (7,*) telepipe。
- 表示名: unitxt `+4` テーブル経由の名前 (キャッシュ)、ItemPMT 索引 `item-pmt-index(group, index, type-offset, stride)`: weapon (0x00, 44)、frame/barrier (0x04, 32; group-1)、unit (0x08, 20; group 0)、mag (0x10, 28; group=group, index=group)。
- weapon 表示 `"<name>[ +grind][ [special]] [native/abeast/machine/dark|hit]"`: grind `+0x1F5`、特殊 `+0x1F6` (通常表)、S ランク (`0x70≤group≤0x88` または `0xA5≤group≤0xA9`) は index を S ランク特殊表で引く。属性 `+0x1C8` から 3 組 (area u8, percent i8)、area 1..5 = native/abeast/machine/dark/hit。
- frame `"<name> [dfp|evp] [Ns]"` (`+0x1B9`,`+0x1BA`,`+0x1B8`)、barrier `"<name> [dfp|evp]"` (`+0x1E4`,`+0x1E5`)、unit `"<name>"`、mag `"<name> [def/pow/dex/mind]"` (`+0x1C0` u16×4 を 100 で floor)。名前不明は `"?"`。
- 特殊表は `psobb.lisp:502,510` をそのまま移植。

### 12.7 モンスター (`read-monsters`, `read-monster`)
- `array = *0x007B4BA2`、`npc = *0x00AAE164 (≤512)`、`pc = *0x00AAE168 (≤12)`。ポインタ列は `array + 4*pc` から `4*npc` バイト。エンティティ index = `pc + i`。
- 各エンティティ `[+0x1C, +0x37C)` を 1 回読み。unitxt `+0x378` が 0 なら非モンスター。
- id `+0x1C` (u16)、x/y/z `+0x38/+0x3C/+0x40`、facing `+0x60`、paralyzed `u16@+0x25C == 0x10`、status `u16@+0x268` (0x02 frozen, 0x12 confused)、last attacker `u16@+0x2D8`。
- HP 優先順: (1) ボス特殊 (unitxt 45: index0 → `+0x6B4`, 他 → `+0x39C`; unitxt 73: index0 → `+0x704`, 他 → `+0x7AC`) を個別読み、(2) 一括 HP ブロック: live id の最小〜最大スパン ≤ 2048 なら `hp_table + 32*low` から `32*span` を 1 回読み、`hp = u16@(32*(id-low)+4)`、(3) テーブルから個別 `u16@(hp_table + 4 + 32*id)`、(4) テーブル無し → `u16@+0x334`。`hp > 0x8000` → 0 (撃破時アンダーフロー)。
- Zu/Pazuzu (unitxt 94/95): `y += f32@+0x418`。
- 名前: unitxt `+16` テーブル、UTF-16 32 B (キャッシュ)。
- ボス名 (`boss-name`): 44 Sil Dragon; 45 index0 Dal Ra Lie; 46 index31/32 "Vol Opt ver. 2 (1)/(2)"; 47 Dark Falz; 73 index0 Barba Ray; 76 Gol Dragon; 77 Gal Gryphon; 78 Olga Flow; 106/107/108 Saint-Million/Shambertin/Kondrieu: index<5 → "<base> Tail (index)", index<9 → "<base> Head (index-4)"。

### 12.8 テクニック ID
0 Foie, 1 Gifoie, 2 Rafoie, 3 Barta, 4 Gibarta, 5 Rabarta, 6 Zonde, 7 Gizonde, 8 Razonde, 9 Grants, 0xA Deband, 0xB Jellen, 0xC Zalure, 0xD Shifta, 0xE Ryuker, 0xF Resta, 0x10 Anti, 0x11 Reverser, 0x12 Megid。

---

## 13. アカウントモード (Sandbox 判定) — `psobb.lisp:221-280`, `detect.lisp:150-186`

- 名前色 (player +0x948, ARGB): Sandbox = `0xFFAB9423` (2026-09-22 実測)、通常 = `0xFFFFFFFF`。
- 判定は **RGB のみ** (`& 0xFFFFFF`、alpha 無視)。`0`/NIL = 未設定 (判定なし)。既知 2 色以外 = **判定なし (NIL)**。「通常」扱いにしない。
- ship・guild card 番号・文字列 "sandbox" では判別できない (PR #304 で ship 判定を試して revert)。
- 検出器では**クエストロード単位**で自分の色を保持 (`update-name-color-tracking`): 重み sandbox 3 / normal 2 / 未知 1 / なし 0。各フレームで、保持中の重みが 3 未満のとき、今フレームの色の重みが**真に大きい**場合のみ置換。sandbox は確定 (以後不変)、白は後から sandbox に訂正され得る、白は未知色に上書きされない。
- ロードの最初のフレームから (トラッカー開始前から) 更新。`reset-detector` (ロード切替・ロビー) で忘れる (同一ゲームプロセスでアカウントが変わり得るため)。
- 同一ロードの全ラン (フルクリア + セグメント + 中断) が同じ判定を持つ。
- 送信: `account_mode` = `"sandbox"`/`"normal"`、判定なしなら省略 (サーバーは normal 扱い)。
- ログ: `log-account-mode` が録画ログに `account-mode: <mode|no verdict> (name color XXXXXXXX)` を、読みが変わった時 or 前回から 300 秒経過時に書く (診断アップロードの末尾 64KB に残るように)。

---

## 14. 検出ステートマシン (`detect.lisp`) — **パリティ**

純粋関数: スナップショット (または NIL) を入力し、完了ランのリストを返す。時間は単調時計 (`get-internal-real-time`)。C# は `Stopwatch.GetTimestamp` 等を注入可能にしてテストで制御すること (Lisp テストは `sleep` を使っている)。

### 14.1 状態

```
Detector {
  state: Idle | InQuest        // active tracker が 1 つでもあれば InQuest
  armed: bool                  // 「クエスト未ロード」を一度見たら真。途中アタッチ対策
  questPtr: uint?              // リロード検出用
  trackers: List<Tracker>      // このロードの全トラッカー (done 含む、古い順)
  myPb: float?                 // 前フレームの自分の PB ゲージ
  pbFlag: bool                 // PB カテゴリ
  seenAlive: Set<ushort>       // このロードで hp>0 を見たモンスター id
  killedIds: Set<ushort>       // 生存確認後に 0hp を見た id
  myNameColor: uint?
  telemetry: Telemetry?        // 最初のトラッカー開始時に生成
}
Tracker { def, startTime, party, mySectionId, questName, difficulty(label), done }
```

### 14.2 `detector-step(snapshot)` (`detect.lisp:258`)

1. **snapshot == NIL** (ゲーム消失 / my-index 読めず): `abandon-trackers` → reset → `armed = false` → 中断ランを返す。
2. **quest-ptr が 0 / 無い** (ロビー・フリーフィールド): `abandon-trackers` → reset → `armed = true` → 中断ランを返す。
3. **quest-ptr ≠ 0**:
   1. `questPtr` が既にあり今回と異なる (リロード/別クエスト) → `aborted = abandon-trackers` → reset (**armed は維持** → 新ロードで即開始可)。`questPtr = ptr`。
   2. `update-name-color-tracking` (毎フレーム)。
   3. `armed` なら、`snapshot-quest-defs` の各定義 (定義順) について、同じ def のトラッカーが**このロードに存在しない** (done 含む) かつ start トリガー成立 → `start-tracker`、`started` に記録。
   4. active トラッカーがあれば: `update-pb-tracking` → `update-kill-tracking` → `telemetry-step`。
   5. 全トラッカー (古い順) について、done でなく今フレーム開始でもないもの: end トリガー (killedIds 付き) 成立 → `finish-tracker` を `completed` に。
   6. `state` 更新。
   7. 返り値 = `aborted` (リロードで打ち切ったもの) を先に、続いて今フレームの完了を **トラッカー順**で。

要点:
- 同一フレームで start と end が両方成立しても、**開始フレームでは終了判定しない** (古いデータ対策)。次フレームで完了。
- クエスト完了後、ロードされたままトリガーが立ち続けても再開始しない (def ごとに 1 ロード 1 回)。ロビーを経由すると再武装。
- アタッチ直後は `detector-step(NIL)` を 1 回呼ぶ (§17) → armed=false → **途中アタッチしたクエストは計測しない**。
- 未知クエスト (定義マッチなし) は Idle のまま。

### 14.3 トリガー評価 (`trigger-met-p`)
- `register N`: §12.2。
- `floor-switch F S`: §12.2。
- `warp-in`: `players` に「人間 (`person-p`) ∧ `floor > 0` ∧ ¬warping」が 1 人以上。`person-p` = NPC でない、または自分 (my-index 一致) (自分は guild card が何であれ人間)。
- `monster-dead ID`: `ID ∈ killedIds`。

### 14.4 開始 (`start-tracker`)
- このロード最初のトラッカーなら: `pbFlag = false` (`pb-category-at-start-p` は常に NIL — 開始時のゲージ満タンやシフタは PB 扱いしない、誤検出の教訓)、`myPb = 自分の PB`、テレメトリ生成 (`start-time` = 今、`max-party-pb-shifta` = NPC 込みパーティから §12.3)。
- トラッカー: `startTime = now`、`party = party-of(snapshot)` (**NPC 除外**、`class` がある者のみ、各 `{name, class, level, section-id, guild-card}`)、`mySectionId` = 自分の section、`questName` = スナップショットのクエスト名、`difficulty = difficulty-label(difficulty, anguish)`。

### 14.5 PB カテゴリ (`update-pb-tracking`) — 誤検出修正の産物
- 自分の PB が NIL → 何もしない。
- 自分が warping → `myPb = NIL` (ベースラインを捨てる; ワープのゼロ化を比較しない)。
- それ以外: `previous != NIL ∧ previous ≥ 99.0 ∧ previous - pb > 50.0` → `pbFlag = true`。その後 `myPb = pb`。
- 理由: テレパイプで P2 に戻るとゲームがゲージを 0 にする (run 1717 の誤分類、DB 86 件修正)。満タンからの 1 フレーム崩落のみを PB 発動とみなす。
- `pbFlag` はロード単位 (全トラッカー共通)。

### 14.6 撃破追跡 (`update-kill-tracking`) — active トラッカーがある間だけ
各モンスター: `hp > 0` → seenAlive に追加。`hp == 0 ∧ seenAlive に居る ∧ killedIds に無い` → killedIds に追加。
(テレメトリの kill 判定とは規則が違う — §15.4)

### 14.7 完了ラン plist (`finish-tracker`)

| キー | 値 |
|---|---|
| `:quest-slug` | def.slug |
| `:quest-name` | トラッカー開始時のクエスト名 |
| `:episode` | def.episode (スナップショットではない) |
| `:time-ms` | `max(1, round(ms since startTime))` (**CL の round = 偶数丸め**) |
| `:party-size` | party の人数 (NPC 除外) |
| `:pb` | pbFlag |
| `:players` | party |
| `:submitter-section-id` | |
| `:difficulty` | ラベル文字列 |
| `:account-mode` | §13 (NIL 可) |
| `:my-name-color` | 整数 (キューとログ用、**送信しない**) |
| `:death-count` | テレメトリの死亡数 |
| `:telemetry` | `telemetry-run-data` (完了時点のスナップショット。セグメントは早い時点の累計) |
| `:finished-at` | universal time (秒) |
| `:aborted` | 中断時のみ `t` |

### 14.8 中断 (`abandon-trackers`)
active トラッカーのうち経過 ≥ **15000ms** (`+abort-min-ms+`) のものだけ `:aborted t` で emit (短いものはノイズとして捨てる)。発生契機: ロビー帰還、リロード、ゲーム終了、スナップショット NIL。

### 14.9 公開アクセサ
`detector-active-def` (最古の未完了トラッカーの def)、`detector-active-count`、`detector-elapsed-ms` (最古の未完了トラッカーの経過)。

---

## 15. テレメトリ (`telemetry.lisp`) — **パリティ** (psostats consolidateFrame 準拠)

### 15.1 生成と入力
最初のトラッカー開始時に生成。`telemetry-step` は active トラッカーがある間、毎フレーム (≈30Hz) 呼ばれる。`:monsters` は毎フレーム、`:inventory` は約 1 秒毎に同梱される。

### 15.2 1 ステップの処理順 (`telemetry-step`)
`elapsed = round(ms since start)`、`second = floor(elapsed/1000)`、`delta = 前 tick からの ms (初回 0, 負は 0)`。
1. `monsters` があれば `update-monster-tracking`。
2. fast-burst 中に NPC でない誰かが warping → `fast-warps = true`。
3. 自分 (`me`) が居れば:
   - `max-party-pb-shifta` があり `me.shifta > limit` → `illegal-shifta = true`。
   - `update-state-tracking(delta)`: 現在 state に delta を加算 (`time_by_state`)。攻撃状態 {5,6,7} に非攻撃状態から入ったら現在武器の `attacks++`。state 8 に入ったら `techs++` と `techs_cast[tech名]++` (名前不明は数えない)。
   - `update-death-tracking`: 前 HP > 0 かつ今 HP == 0 → 死亡数++、イベント `{t:second, type:"death"}`。
   - `update-room-tracking`: `(floor, room)` が前回と異なれば (初回含む) イベント `{t, type:"room", floor, room, ms:elapsed}` (ゴーストの ms 精度スプリット; `"room"` の綴りはサーバーの契約テストで固定)。
   - `update-track-recording`: 前回サンプルから ≥250ms (または初回) かつ件数 < 86400 → 行 `[elapsed, floor, map, round1(x), round1(z), round1(y)]`。
   - `update-map-tracking`: map が前回と異なれば `{t, type:"floor", floor:map}` (初回は記録しない)。
   - `update-resource-tracking`: トラップ 3 種が全て整数なら、各種で減少分を `traps_used` (dt/ft/ct) に加算。TP 減少分を `tp_used`。meseta 減少 かつ floor>0 → `meseta_charged` に加算。
   - `inventory` があれば `update-inventory-tracking`: 前回の消耗品 plist と比較し、**ちょうど 1 減った**キーだけ `items_used++` (2 以上の減少は捨て/移動扱い、スタック消滅 = キー消失は数えない — psostats 準拠)。装備中の各アイテムの `seconds++` (種類・表示名付きで初登場時にエントリ作成)、武器なしなら `"Bare Handed"` の seconds++。`current-weapon-id` = 武器 id または `"Bare Handed"`。
   - `second > last-frame-second` (初期値 -1) なら `push-frame`。

### 15.3 フレーム行 (`push-frame`) — `frame_keys` 順
| 列 | 値 |
|---|---|
| t | second |
| hp, tp | me (無ければ 0) |
| pb | `round(me.pb)` (偶数丸め) |
| meseta | **meseta_charged 累計** (所持金ではない) |
| floor, room | me |
| x, z | `round1(me.x)`, `round1(me.z)` |
| shifta, deband | レベル |
| inv | 無敵なら 1 |
| state | action state |
| monsters | 生存モンスター数 (最後のサンプル) |
| kills | 累計 kill |
| map_var | map-variation |
| ft, dt, ct | 現在の freeze / damage / confuse トラップ **所持数** |
| damage | 自分 (my-index) の与ダメージ累計 |
| weapon | current-weapon-id |
| player_locs | `{key: [floor, room, x, y, z, facing, warping01]}`、key = guild card か name か `""` (NPC 含む全プレイヤー) |
| monster_locs | `{id: [x, y, z, facing, hp, frozen01, paralyzed01, confused01]}` (最後のモンスターサンプルのうち hp>0) |
| map | 実マップ番号 |

同時に: 各ボスの HP 履歴に現在 HP を追加、`monster_hp_pool` 系列に現在プール値を追加。
`round1(v) = round(v*10)/10.0` (単精度、偶数丸め)。

### 15.4 モンスター追跡 (`update-monster-tracking`)
- id 毎の状態表。初見 → `{alive: true, hp, spawn_ms: elapsed, killed_ms: nil, frame1: nil}` (**初見で hp 0 でも alive 扱い**; 次サンプルで 0 なら撃破として数える — 検出器の killedIds 規則とは異なる。パリティのため両方そのまま移植)。
- `alive ∧ hp == 0` → alive=false, killed_ms=elapsed, `kills++`、unitxt ∉ {34,45,73,68} なら `frame1 = (killed_ms - spawn_ms < 60)`、`last_hits[attacker]++`、状態 hp > 0 ならその残 HP を `player_damage[attacker]` に加算、状態 hp=0。
- `alive ∧ hp < 前 hp` → 差分を `player_damage[attacker]` に加算、hp 更新。(`alive ∧ hp ≥ 前` なら hp 更新)
- 生存数 (hp>0) とプール (全 hp 合計) を更新、サンプル保存。
- ボス: `boss-name(unitxt, index)` が非 NIL なら初見時に登録 (同 unitxt の既登録数 n>0 なら `"<name> (n)"`)、`spawn_t = second`、killed_ms がセットされ killed_t 未設定なら `floor(killed_ms/1000)`。

### 15.5 run data (`telemetry-run-data`)
`frames`(古い順), `events`(古い順), `track`(古い順), `death-count`, `meseta-charged`, `kills`, `tp-used`, `traps-used`, `items-used`/`techs-cast`/`time-by-state`/`player-damage`/`last-hits` (alist、**古い順**)、`weapons` (古い順、`(:id ... :display :type :seconds :attacks :techs)`)、`monsters` (spawn_ms 昇順)、`bosses` (spawn 順、hp 古い順)、`monster-hp-pool`、`max-party-pb-shifta`、`illegal-shifta`、`fast-warps`。
キューに sexp で書けること (リスト・文字列・数値のみ) が前提だった。

---

## 16. トリガーログ / ランログ / ルームピッカー (`trigger-log.lisp`)

### 16.1 trigger-log.txt (**契約**: モデレーターが読む)
- パス `%APPDATA%\ephinea-ta-client\trigger-log.txt`、UTF-8、追記、初回使用時に開いて開きっぱなし、書込毎ではなく変更があったフレームで flush。GUI とポーリングの両スレッドから書くのでロック。
- ON にした瞬間: 
  ```
  === trigger logging started HH:MM:SS ===
  Play the segment; each register / floor-switch change is listed below.
  ```
- 毎フレーム (`:trigger-log` 真のとき、`log-trigger-changes`): 前フレームと今フレームが**同じ quest-ptr で両方クエスト名あり**のときだけ差分を出す:
  ```
  HH:MM:SS "<quest>" register N: old -> new
  HH:MM:SS "<quest>" floor F switch S: off -> on
  HH:MM:SS "<quest>" monster ID killed (<name|?>, unitxt U)
  ```
  `"<quest>"` は `~s` (ダブルクォート付き、`"` と `\` をエスケープ)。時刻はローカル時刻。レジスタは 0..255 を id 順、スイッチはバイト順・ビット順 (floor = i/32, switch = 8*(i%32)+bit)。
- OFF で stream を閉じる。C# はローテーションを追加: 書く直前に、開いているストリームの位置が 8 MiB (`TriggerLog.MaxBytes`) を超えていればストリームを閉じる。開くときにディスク上のファイルが 8 MiB を超えていれば `trigger-log.old.txt` へ改名 (前の世代は上書き、1 世代のみ) し、新しいファイルの先頭に `=== trigger log rotated HH:MM:SS; earlier lines are in trigger-log.old.txt ===` を書く (起動時・再 ON 時も同じ)。改名に失敗したら元のファイルへ追記を続け、さらに 8 MiB 伸びたら再試行する。セッションヘッダーは回した後に書くので、常に自分の行と同じファイルに入る。

### 16.2 撃破差分 (`newly-killed-monsters`)
前フレームで hp>0、今フレームで hp==0 のモンスター (今フレームの順序)。

### 16.3 `*last-kill*` (`update-last-kill`)、毎フレーム (トグル無関係)
クエスト未ロードで NIL、quest-ptr が変わったフレームで NIL、それ以外は今フレームの最後の撃破で `{id, name, unitxt}` を更新。

### 16.4 ランログ (`update-run-logs`)
- quest-ptr≠0 で、前フレームが無い or quest-ptr が違う → ログをリセットし `*run-quest* = {number, name, episode}`。
- 撃破: `{id, name, unitxt, floor, room (自分の), map, time}` を先頭に。スイッチ 0→1: `{floor, switch, room (自分の), time}`。
- クエスト未ロード中は保持 (ロビーでもルール登録できる)。

### 16.5 ルーム (`run-rooms`, `run-room-rows`)
撃破を `(floor, room)` で初出順にグループ化。各ルームのクリア候補スイッチ = 同じ room のスイッチのうち最後の撃破時刻に最も近いもの (`room-clear-switch`)。行: ルーム毎に `:clear` 行 1 つ (スイッチがあれば `(:floor-switch F S)`、無ければ最後の敵の `(:monster-dead ID)`) + 敵毎 (id 重複除去、撃破順) の `:enemy` 行 `(:monster-dead ID)`。ラベル `"<area> · room <n>"` (map 不明なら `"Room <n>"`)。
- マップ名表 (`+map-names+`, 46 要素、サーバー views.lisp と契約テストで一致固定): `trigger-log.lisp:247` をそのまま移植。範囲外は `"Map N"`。

---

## 17. ポーリングループとフレーム内処理順序 (`main.lisp`)

### 17.1 定数
| 名前 | 値 |
|---|---|
| poll interval | 1/30 秒 (処理後に待つ; 実周期 = 処理時間 + 33ms) |
| search interval | 1 秒 (未アタッチ時) |
| GUI tick | 250ms |
| heavy sample (inventory) | 1 秒 |
| read failure 閾値 | 60 フレーム |
| retention sweep | 120 秒 |
| account-mode ログ再出力 | 300 秒 |

待機は `*stop-requested*` で即時解除される条件付き待ち。

### 17.2 ループ (`poll-loop`)
起動時 `cleanup-stale-recordings`。各反復の先頭で `note-poll-activity`: `busy = detector InQuest ∨ recorder Recording`。非 busy で保留更新があれば 1 回適用。
- 未アタッチ → `poll-search-step`: `open-psobb-reader` → 信頼判定 (拒否ならクローズ、拒否を保持)。拒否中に PSOBB 窓が消えたら拒否をクリア。音声 PID / Pin Share exe を設定。アタッチできたら `detector-step(NIL)` (**再アタッチ時に武装解除**)。できなければ: 録画停止処理を 1 ステップ、再送要求処理、アップロード開始判定、保持スイープ、ステータス更新、1 秒待機。
- アタッチ中にプロセス死亡 → `poll-detach-step`: reader クローズ、PID クリア、`detector-step(NIL)` (中断ラン emit — ただしこの戻り値は**捨てられている**、§22)、録画 1 ステップ。前スナップショットも破棄。
- アタッチ中 → `poll-frame-step` (§17.3)。
- 終了時: 録画シャットダウン、トリガーログを閉じる、reader クローズ。

### 17.3 1 フレームの処理順 (`poll-frame-step`) — 順序が意味を持つ
1. `snapshot = augment(read-snapshot)` (例外は NIL)。
2. `runs = detector-step(snapshot)`。
3. 読み取り健全性カウンタ (§11.5)。
4. `recorder-step(state, runs, windowTitle)` — ここで各ランに `:video-offset-ms` が `nconc` される (enqueue より前)。
5. ゴーストフェッチ開始判定 (quest ロード時 1 回)。
6. Pin Set フェッチ判定。
7. ゴーストレース step。
8. `runs` があれば: 完了音 (強制オフ) → `handle-completed-runs` (ゴースト注釈 → 各ランを account-mode ログ → tracking スタンプ → enqueue → `submit-queued!` (同期) → トースト) → 一覧再描画。
9. `:trigger-log` なら差分ログ。
10. `update-last-kill`、`update-run-logs` (前スナップショットと比較)。
11. 再送要求 (`*retry-requested*`) があれば `submit-queued!`。
12. 250ms 毎: アップロード開始判定、保持スイープ、(録画有効かつ非 busy) gdigrab プローブ、ステータス更新 (読み取り失敗表示含む)。
13. 1/30 秒待機。次フレームの `previous-snapshot = snapshot` (NIL も含む)。

### 17.4 `run-headless` (テスト/デモ)
スナップショット列を新しい検出器に流し、完了ランを集める。C# でもゴールデンテスト用に同等 API を用意する。

---

## 18. WebSocket トランスポート (`websocket-win32.lisp`) — Pin Share 用

- URL: `ws://`/`wss://` を http/https に読み替えてパース (既定ポート 80/443)。
- 接続: WinHTTP で GET + `WINHTTP_OPTION_UPGRADE_TO_WEB_SOCKET` → 101 以外は `"WebSocket upgrade refused (HTTP N)"`。接続中タイムアウト 10/10/3/10s、完了後 receive は無限 (WinHTTP 自身の keep-alive ping 30 秒で死活検出)、send 3 秒 (リレースレッドがアドオンへ 1 秒毎のハートビート義務を持つため)。
- 送信: UTF-8 テキスト 1 メッセージ、ロック下 (クローズとの競合防止)。
- 受信: 64KB チャンクで再構成、UTF-8 メッセージのみ返す。不正 UTF-8 は捨てて次を待つ。バイナリは捨てる。**累計 4MB 超で切断** (`:closed`)。close フレーム/エラーで `:closed`。
- クローズ: `WinHttpWebSocketShutdown(1000)` (Close ではない: Close は相手の close を待ち、別スレッドのブロック中受信がそれを飲み込むため) → ハンドル解放。どのスレッドからも冪等、ブロック中の受信は `:closed` で戻る。
- C# は `ClientWebSocket` で同等にできる (UA/プロキシ設定に注意)。

---

## 19. i18n (`i18n.lisp`)

- 言語: `:en`, `:ja` (`*languages*`)。不正値は `:en`。ラベル: `"English"` / `"日本語"` (常に自言語表記)。
- 表: `*strings*` = plist `key → ("english" "japanese")`、**247 キー**。キーは kebab-case キーワード (例 `:token-ok`, `:update-downloading`)。
- `tr key &rest args` = 現在言語の文字列を **CL `format` で必ず整形** (引数なしでも `~%` 等が処理される)。キー欠落はエラー。
- 使用ディレクティブ (全文字列を集計): `~a` 160, `~%` 57, `~d` 32, `~@[ ... ~]` 6, `~:p` 2 (英語の複数形 `category~:p` → 直前引数が 1 以外で `s`)。`~s` はエラーメッセージのみ。**機械変換可能**:
  - JSON 例: `{"token-ok": {"en": "Token: OK ({0})", "ja": "トークン: OK ({0})"}}`。
  - 変換規則: `~a`/`~d` → 位置引数 `{n}`、`~%` → `\n`、`~@[X~]` → 「次の引数が null/false なら X ごと省略、あれば X を展開 (X 内の `~d` がその引数を消費)」、`~:p` → 直前の引数が 1 でなければ `s`。該当箇所は 3 キーのみ (`:server-ok`, `:version-status`, `:update-downloading`) なので手で ICU 風に書き直してもよい。
  - 変換後に「全キーが両言語を持つ」「全エントリが両言語で整形できる」テスト (既存 `run-i18n-tests` 相当) を残す。
- 言語切替は即時 (Lisp は窓を再構築)、config に保存。
- `signature-status-label` は §11.4。
- ステータス文言の一部はサーバー/モデレーター運用の文書に出てくる (例: 「Log trigger changes」)。UI 文言変更時は `quest-triggers.sexp` のコメントとの整合に注意。

---

## 20. ビルド・パッケージ・リリース

### 20.1 現行 (Lisp)
- `deliver.lisp`: LispWorks 8.1 `-build`。fasl キャッシュを**毎回削除**してから quickload (defstruct アクセサのインライン化で古い fasl が古いスロット配置を参照し、v0.14.6 で壊れた exe を出荷した教訓)。`client/VERSION` (X.Y.Z) を `*client-version*` に焼き込み、不正なら失敗。出力 `dist/RappyRunsClient.exe` (約 50MB)、アイコン `icon.ico`、LispWorks スプラッシュ無し。
- `package.ps1`: `dist/RappyRunsClient.zip` を作る。中身:
  ```
  RappyRunsClient.exe
  data\quest-triggers.sexp
  data\pin-share\init.lua
  data\pin-share\pinshare-input.dll   (native/pinshare-input/build.cmd で毎回ビルド、VS2022 C++ 必須、失敗は致命)
  ffmpeg\ffmpeg.exe + ffmpeg\LICENSE.txt   (vendor/ffmpeg にあれば。exe があって LICENSE が無ければ失敗 (GPL))
  ```
  `Compress-Archive` (PS 5.1) 使用 → エントリ区切りが `\`。
- `release.ps1 vX.Y.Z [-NotesFile f] [-Prerelease] [-Clobber]`: タグ形式検証、`client/VERSION` と一致しなければ拒否、LispWorks ビルド (`Start-Process -Wait`、GUI サブシステム exe なので明示待機)、package、ソースミラー (`scripts/publish-client-source.ps1`、リリース前にタグ位置を合わせる)、`gh release create <tag> <zip> --repo PSOBBAITools/rappyruns-client --title "Rappy Runs Client <tag>"` (notes 無指定時 `"Rappy Runs Client <tag>."`)。`-Clobber` は既存リリースの資産差し替え。
  - 運用メモ: release.ps1 はリダイレクト無しで呼ぶ (MEMORY: pinshare-key-conflict)。

### 20.2 C# への要求
- 同じ zip レイアウト・資産名・タグ形式・リポジトリを維持 (§10.7)。
- バージョンは 1 箇所 (VERSION ファイル or csproj) から exe に焼き込み、タグとの一致をリリーススクリプトで検査。dev ビルドは「バージョンなし」= 自己更新しない、を維持。
- self-contained single-file x64。WebView2 の扱いを決める (Evergreen 前提 + 不在時ブートストラップ案内)。
- ffmpeg 同梱と LICENSE 検査、pinshare-input.dll ビルドを継承。

---

## 21. テスト資産 (パリティテストの種)

実行: SBCL/LispWorks で `client-tests.lisp` をロード → `run-client-tests` (失敗数を返す)。ゲームもサーバーも不要。モックメモリ (`make-mock-reader` = `(address . bytes)` 領域のリスト) で実レイアウトを組み立てる (`make-player-block`, `make-game-regions` — C# 移植時もこのビルダーをそのまま作るとテストが機械的に移せる)。

| ファイル | 件数 (check) | スイート | カバー範囲 |
|---|---|---|---|
| client-tests.lisp | — | ハーネス | モックビルダー、スイート実行順 |
| tests-memory.lisp | 99 | memory, inventory, extended-player, telemetry, monster-read, psostats-telemetry | LE デコード、f32/f64、スナップショット (エピソード/クエスト名/番号/プレイヤー/レジスタ/スイッチ)、インベントリ (自分の装備・消耗品 XOR、>60 アイテム、ゴミ count)、プレイヤー全フィールド、実測 sandbox バイト列、名前色判定 (alpha 無視)、shifta レベル、テレメトリ (1 秒 1 フレーム、死亡・kill・meseta・monomate・Resta・素手・state 時間)、モンスター読み (HP テーブル、位置、状態異常、Zu 高さ、アンダーフロー、HP 一括ブロックとスパン上限)、fast burst、ボス名、PB シフタ上限、ダメージ/ラストヒット/frame1/ボス HP 履歴/HP プール/illegal shifta/fast warp、JSON 形 |
| tests-detect.lisp | 104 | payload, detect-telemetry, detect, anguish, monster-clear | ラン JSON の全フィールドと**キー集合固定**、TTF フルフロー、再開始しない、ロビーで再武装、途中アタッチ無視、名前色の全遷移 (開始フレーム欠落、未知色、白→sandbox 訂正、sandbox 不変、同一ロード同一判定、クエスト間のアカウント変更、中断ランにも付与)、PB (開始時満タンは No PB、発動で PB、テレパイプ 60→0 は No PB、ワープ跨ぎ満タン→0 も No PB)、セグメント並行 (セグメント先に emit、フル後、再開始なし)、warp-in (P2 では開始せず、NPC 単独では開始せず、NPC はパーティ外)、未知クエスト、ゲーム消失、中断 (短いと無し、20 秒で emit、各フィールド、ゲーム終了でも、完了後は再 emit なし)、Anguish 全段、monster-dead (別の敵では終わらない、0HP でしか見ていない敵では終わらない) |
| tests-quests.lisp | 51 | server-defs, gdv-segment, trigger-log, quest-rule, room-picker | サーバー定義マージ (計測可能のみ、builtin 共存、トリガー変換、空で再取得すると server 分のみ消える)、GDV の reset セグメント + フルクリア、トリガーログ (即時作成、スイッチ差分行)、撃破差分・last-kill (離脱/リロードでクリア)、trigger->json 全種と往復、ルームピッカー (0→1 のみ、room 相関、map 名、行生成、ロビー保持、新ロードでリセット) |
| tests-helpers.lisp | 137 | pure-helper, rule-form, ux-helper, updater, config-migration | parse-url、u64-double (1.0/-2.5/1.30/Inf/非正規化)、warp-in/NPC/party-of、npc 判定 (ASCII 数字のみ)、unknown-slugs、ルールフォーム、状態ラベル、時間整形、standing ノート/トースト全分岐、トリム、unlinked/submission-token、登録不能時のキュー温存、debug、URL 検証、エラー文言、normalize-token、**updater (版解析・比較・起動時判定・release JSON・zip 検証・ヘルパースクリプトの形)**、config migration (強制キー削除、録画フォルダ移行) |
| tests-misc.lisp | 69 | signature-policy, i18n, upload-queue, retention, credentials, diagnostics | 署名ポリシー、i18n 完全性・整形、アップロード候補 (古い順、バックオフ、given-up、ファイル消失、aborted/unranked 除外、offset URL)、active 判定、tracking スタンプ、動画ラベル、保持期間 (上限、protected、uploaded 優先、idle のみ)、login.txt 解析 (BOM/CRLF/コメント/空)、ログ末尾抽出 |
| tests-recorder.lisp | 179 | recorder, video-flow | 録画 (範囲外仕様)。ffmpeg 引数、ファイル名、セッション、offset/trim |
| tests-ghost.lisp | 83 | ghost, ghost-overlay | ゴースト解析・レース・オーバーレイデータ (範囲外仕様) |
| tests-pinshare.lisp | 48 | pinshare | Pin Share 純粋部 (範囲外仕様) |

合計 約 770 check。
パリティ戦略の提案:
1. モックビルダーと全スイートを C# (xUnit) に移植 (ラベルをテスト名に)。
2. **差分ゴールデン**: Lisp 側に小さなダンプ関数を足して (リポジトリは変更せず別ブランチ/スクラッチで)、同一スナップショット列に対する `detector-step` 出力と `run-json` 文字列を JSON で書き出し、C# の出力とキー順無視で比較。実プレイの trigger-log や録画ログから再現したシナリオも追加。
3. 実機: psobb 実走で Lisp 版と C# 版を同時にアタッチ (読み取り専用なので可能) し、同じランの time_ms 差・payload 差を確認 (ただし単一インスタンスミューテックスで同時起動できないので、C# 側に開発用フラグで回避を用意)。

---

## 22. コメントに埋め込まれた既知の落とし穴 (回帰リスク)

| # | 落とし穴 | 出典 | C# での対策 |
|---|---|---|---|
| 1 | 開始時の PB ゲージ満タン/シフタを PB 扱いすると誤検出。テレパイプで P2 に戻るとゲージが 0 になる (run 1717、DB 86 件修正) | detect.lisp:120,129 | §14.5 をそのまま。ワープ中はベースライン破棄 |
| 2 | NPC (A New Hope の Mr.X 等) がプレイヤー配列に入り人数が 1〜4P にぶれた。guild card 非数字 = NPC、自分は常に人間、NPC は Shifta 上限計算には含める、warp-in 開始には数えない、fast-warps にも数えない | psobb.lisp:297, detect.lisp:237 | `person-p` と `include-npcs` の使い分けを厳守 |
| 3 | 名前色: 白は設定途中の値かもしれない / 未知色は normal にしない / sandbox は確定 / ロード単位 | psobb.lisp:221, detect.lisp:296 | 重み付き更新をそのまま |
| 4 | 途中アタッチでは計測しない (armed)。再アタッチも武装解除 | detect.lisp:158, main.lisp:262 | |
| 5 | 開始フレームでは終了判定しない | detect.lisp:437 | |
| 6 | 1 def 1 ロード 1 回 (done トラッカーをロード中保持) | detect.lisp:163 | |
| 7 | 中断は 15 秒未満を捨てる | detect.lisp:384 | |
| 8 | `(:monster-dead)` は生存確認後の 0HP のみ (0HP で湧く敵で誤クリアしない)。テレメトリの kill は初見 0HP も数える (psostats 準拠) — 規則が違うのは意図的 | detect.lisp:216, telemetry.lisp:273 | 統一しない |
| 9 | MAE エリア別は register 50 終了 (254 は遅延・未設定あり)。Mop-up/Patrol は warp-in 開始 | quest-triggers.sexp | データで持つ |
| 10 | サーバー定義が builtin に勝つ | quests.lisp:46 | |
| 11 | Lisp の真偽: 0/"" は真。`death_count 0`、`floor 0`/`room 0` イベント、`episode` 等は送る | api-client.lisp:491 | null 判定のみで省略 |
| 12 | `pb`/`aborted`/`unranked`/`private`/`illegal_shifta`/`fast_warps`/`frame1` は真の時だけ送る | api-client.lisp:540 | |
| 13 | 単精度浮動小数を double 化して JSON に出すと桁が崩れる | — | float のまま直列化 |
| 14 | CL の `round` は偶数丸め (time_ms, round1, pb) | detect.lisp:178 | `MidpointRounding.ToEven` |
| 15 | GitHub API は UA 必須 | winhttp.lisp:136 | UA `ephinea-ta-client` |
| 16 | アップデートヘルパー .ps1 は BOM 必須 (cp932 で非 ASCII パス破損) | updater.lisp:307 | |
| 17 | 旧アップデータは exe/data/ffmpeg 以外をコピーしない | updater.lisp:126-148 | single-file 必須 |
| 18 | 旧リポジトリ名を再利用・fork するとリダイレクトが壊れる | updater.lisp:14 | 運用ルールとして明記 |
| 19 | `%TEMP%` をビルド時に固定しない (v0.41.0 事故) | recording.lisp:355 | 実行時取得 |
| 20 | config/queue の書き込みが非アトミック → 壊れると黙って既定値 (トークン喪失) | config.lisp:68 | temp + Replace、壊れたら退避 |
| 21 | `update-run!` は EQ 置換で、古いコピーからの更新は黙って消える | store.lisp:344 | 安定 ID |
| 22 | 送信後のテレメトリはメモリからも捨てる (ヒープ肥大) | store.lisp:351 | |
| 23 | 動画アップロード後もローカルは消さない (壊れたアップロードを回復不能にした教訓)。保持スイープのみが削除 | store.lisp:575 | |
| 24 | 中断ラン・unranked の動画はアップロードしない (リセット周回でストレージ圧迫) | store.lisp:478 | |
| 25 | pending-limit は 1 時間、通信失敗は 5 分バックオフ | store.lisp:467 | |
| 26 | クエスト中・録画中はアップロードしない (回線・ディスク競合でゲームがラグる)、更新も適用しない | main.lisp:172,215 | |
| 27 | 失敗ランの再送は新ラン完了 / ボタン / トークン検証成功のときだけ (定期リトライなし) | store.lisp:409 | 挙動を変えるなら意図的に |
| 28 | トークン検証で 401 以外の失敗はトークンを疑わない。login.txt の自己修復は 401 のみ | gui.lisp:1486 | |
| 29 | マージ失敗 (通信) は anon-token を残して次回再試行、404 は捨てる | gui.lisp:1532 | |
| 30 | 匿名登録失敗は「今は無理」— キューは queued のまま | store.lisp:66 | |
| 31 | ReadProcessMemory の単発失敗は正常 (ワープ・リロード)。60 フレーム連続で初めて警告 | win32.lisp:209, main.lisp:10 | |
| 32 | `GetLastError` は失敗直後に取る (他の FFI 呼び出しで上書きされる) | win32.lisp:306,352, tray-win32.lisp:580 | `SetLastError=true` + 即 `Marshal.GetLastWin32Error` |
| 33 | 2 窓プレイ: 非信頼窓のメモリは読まない、信頼窓の中でクエスト中のものを選ぶ | win32.lisp:589 | |
| 34 | Authenticode は失効確認でネットワークに出ない (`CACHE_ONLY_URL_RETRIEVAL`)、CN 固定 (拇印ではなく) | win32.lisp:520 | |
| 35 | 読み取りはブロック単位にまとめる (プレイヤー 1 回、モンスター 1 回 + HP 一括、アイテム 1 回)。30Hz でシステムコール数が予算 | psobb.lisp:361,617,780 | 同じバッチ化 |
| 36 | HP 一括読みはスパン 2048 id 上限 (ゴミ id で MB 読み防止) | psobb.lisp:709 | |
| 37 | item count は u16 で読む (定数コメントの u32 ではない)、上限 4096 | psobb.lisp:621 | |
| 38 | 消耗品の個数は所在アドレス下位バイトと XOR | psobb.lisp:666 | |
| 39 | フロアスイッチのビット順は MSB が 0 番 | psobb.lisp:460 | |
| 40 | episode raw 2 → 4 (raw 3 も 4) | psobb.lisp:400 | |
| 41 | 単一インスタンス: ミューテックス名・トレイ窓クラス・WM_APP+2 | tray-win32.lisp:309 | 同一値 |
| 42 | 自動起動値の完全一致比較 (移動で無効表示) | autostart-win32.lisp:121 | |
| 43 | 再起動は引数なし → 自動起動 + 更新の組み合わせでウィンドウが出る | updater.lisp:149 | 既知の挙動 |
| 44 | ゴースト結果は応答時点でロードが同じときのみ採用、ロード無しで即破棄 (古いゴーストが次のクエストに混ざらない) | ghost.lisp:415,452 | |
| 45 | 録画の `:video-offset-ms` は enqueue 前に同一 plist へ追記 (共有構造前提) | recording.lisp:1115 | 明示的に「完了ラン → 録画にオフセットを問い合わせ → enqueue」の順にする |
| 46 | `poll-detach-step` の `detector-step(NIL)` が返す中断ランは捨てられている (ゲーム終了時の 15 秒超中断ランはキューに入らない)。一方、スナップショットが NIL になるフレーム (my-index 読み失敗) では `poll-frame-step` 経由で中断ランが**キューに入る** | main.lisp:292, 304 | 現行挙動を仕様として固定するか、意図的に直すかを決める (直すとサーバー上の aborted ランが増える) |
| 47 | my-index が一度読めないだけでスナップショット NIL → 走行中トラッカーが中断扱いになり武装解除される | psobb.lisp:418, detect.lisp:401 | 現行パリティとしては同じだが、リスクとして記録 |
| 48 | trigger-log.txt は無制限に肥大 (実例 469MB) | trigger-log.lisp:34 | C# で 8 MiB ローテーションを追加 (§16.1、パス・形式は維持) |
| 49 | 名前キャッシュ (アイテム/モンスター) はプロセス生涯で消えない (unitxt は静的前提) | psobb.lisp:479,684 | |
| 50 | 言語切替・モデレーター変化で窓を作り直す際、トレイに隠れている状態を維持 (最小化起動直後にゲーム上へ窓を出さない) | gui.lisp:621 | WebView2 なら再構築不要だが「隠れ状態を勝手に解除しない」は守る |
