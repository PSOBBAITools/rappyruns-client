# UI シェル仕様 (C# 移植用 機能インベントリ)

対象: LispWorks 版 Rappy Runs Client (`client/`) の UI シェル一式。
C# (.NET + WebView2 HTML UI) への書き直しで機能を揃えるための一覧で、画面の見た目を写すものではない。
見た目は作り直す前提なので、ここには「何を表示し、いつ更新し、押したら何が起きるか」を書く。

- 調査時点: `main` @ 27d3766 (client v0.60.0 相当)、2026-09-24
- 行番号は調査時点のもの (`file:line`)
- 識別子は原文 (Lisp のシンボル名) のまま。i18n キーは `:key` で書く (付録 A に英日の文言表)

範囲:

| 領域 | ソース |
|---|---|
| メインウィンドウ、設定、各ダイアログ | `src/gui.lisp` (1836 行) |
| クエストルール登録フォームの純粋ロジック | `src/rule-form.lisp` |
| トレイアイコン、多重起動防止、強制終了 | `src/tray-win32.lisp` |
| Windows ログオン時の自動起動 | `src/autostart-win32.lisp` |
| 起動・終了の順序、ポーリングループ | `src/main.lisp` |
| セルフアップデート (UI 部分とヘルパースクリプト) | `src/gui.lisp` 1560-1762, `src/updater.lisp` |
| Pin Share 中継 (純粋部分 / スレッド部分) | `src/pinshare.lisp`, `src/pinshare-win32.lisp` |
| Pin Share 入力優先 DLL | `native/pinshare-input/pinshare-input.cpp`, `build.cmd` |
| Pin Share アドオン (Lua) | `data/pin-share/init.lua` (2029 行) |
| テスト | `tests/tests-pinshare.lisp` ほか `tests/tests-helpers.lisp` の一部 |

関連して参照したファイル: `config.lisp` (設定の保存先と既定値)、`store.lisp` (Runs 一覧のラベル、トースト判定)、`recording.lisp` (`notify-user` の呼び出し元)、`api-client.lisp` (UI から叩く API)、`credentials.lisp` (login.txt)、`trigger-log.lisp` (トリガーログ、Rooms 行)、`i18n.lisp`。

---

## 0. 全体像

```
main (GUI スレッド = CAPI)
 ├─ eta-client-poll          ポーリングループ 30Hz。4Hz で GUI ステータス更新
 ├─ eta-client-tray          トレイ用の隠しウィンドウとメッセージループ
 ├─ eta-client-pinshare      Pin Share 中継の監督スレッド (セッションごとに接続スレッドを追加で起動)
 ├─ eta-client-pinshare-permission  30 分ごとに /api/me で Pin Share 限定提供の可否を再確認
 └─ 必要なときだけ起動する使い捨てワーカー
      eta-client-server-check / -token-check / -pairing / -file-login /
      -quest-rule / -quest-rule-post / -auto-publish / -update-check /
      -video-upload / -pin-set-fetch / -pin-set-save / -pinshare-connection
```

原則:
- ペインの書き換えはすべて `capi:execute-with-interface-if-alive` で GUI スレッドに渡す。窓は言語切替やモデレーター判定の変化で作り直される (`rebuild-interface`) ので、他スレッドは毎回 `*interface*` を読み直す (`main.lisp:397`, `gui.lisp:11`)。
- 設定のチェックボックスはどれも「押した瞬間に反映して保存」する。Save ボタンが要るのは接続欄 (サーバー URL とトークン) だけ。
- ネットワーク処理は必ずワーカースレッドで行い、結果は GUI スレッドでダイアログかステータス行に出す。

C# への対応づけの目安:
- `execute-with-interface-if-alive` → UI スレッドの Dispatcher か WebView2 への `PostWebMessageAsJson`
- `set-pane-text` の差分キャッシュ → HTML 側に状態を差分で送る (同じ値なら送らない)
- CAPI のダイアログ → HTML モーダルか Win32 MessageBox。**モーダル中も 4Hz 更新とトレイは動き続けること**

---

## 1. メインウィンドウ `client-window` (`gui.lisp:91-542`)

- タイトル: 通常は `"Rappy Runs Client"`。クエスト中は `"<経過時間><ゴースト差>[ [REC]] - Rappy Runs Client"`。タスクバーは右から切られるので時間を先頭に置く (`update-game-status`, `gui.lisp:1818-1825`)。値が変わったときだけ SetWindowText する (`set-window-title`, `gui.lisp:1763`, `*last-window-title*`)。
- 最小幅: 100 文字 (`:visible-min-width '(:character 100)`)
- フォント: Segoe UI 12 (`*ui-font*`)。CAPI の既定フォントは小さすぎるため
- 閉じるボタン: `client-confirm-destroy` (§5.3)
- タブごとにウィンドウサイズを覚える: `remember-tab-size` (`gui.lisp:567`) と `tab-selection-callback` (`gui.lisp:582`)。表示状態が `:normal` のときだけ記録し、タブを切り替えるとそのタブで最後に使ったサイズに戻す。これが無いと窓は大きくなる一方で縮まない (a9a450b で修正)。記録は窓ごとで、作り直すと消える。HTML では 1 ウィンドウ内でタブを切り替えるだけなら不要かもしれないが、設定タブが縦に長いという事情は残る。
- 起動時に選ばれるタブ: API トークンが空なら Settings (Rooms タブの有無で位置がずれるので名前で探す)、それ以外は Runs (`gui.lisp:521-534`)
- ツールチップ: `client-help-callback` (`gui.lisp:558`)。いまは × ボタンの `:clear-list-tooltip` だけ

### 1.1 タブ構成 `client-tab-items` (`gui.lisp:68`)

| タブ | ラベル | 表示条件 |
|---|---|---|
| `runs-tab` | `:tab-runs` | 常に表示 |
| `rooms-tab` | `:tab-rooms` | `*moderator-p*` が真のとき (モデレーターか管理者) |
| `settings-tab` | `:tab-settings` | 常に表示 |

`*moderator-p*` は起動時に設定のキャッシュ `:moderator` から読み、`check-token` → `apply-moderator-role` (`gui.lisp:1472`) で /api/me の `role` と照らし直す。値が変わったときだけ窓を作り直す。変わったときに限るので、作り直しの後にもう一度走る check-token が作り直しを繰り返すことはない。

### 1.2 Runs タブ (`runs-tab`, `gui.lisp:455`)

上から順に:

1. **ステータス行** `status-row`: 3 つのペインを横に並べる
2. **クエスト行** `quest-row`: `quest-status` と右端の `clear-list-button` (×)
3. **Runs 一覧** `runs-list`
4. **操作ボタン行** `actions-row`: Upload / Recordings folder / My runs / Retry

#### 1.2.1 ステータスペイン

| ペイン | 初期値 | 更新元 | 更新頻度 |
|---|---|---|---|
| `game-status` (`gui.lisp:101`) | `:game-searching` | `update-game-status` (`gui.lisp:1771`) | ゲーム未接続時は 1Hz (`poll-search-step`)、接続中は 4Hz |
| `server-status` (`gui.lisp:105`) | `:server-not-checked` | `check-server` (`gui.lisp:1412`) | 起動時、Save、窓の作り直し、ルール登録後 |
| `token-status` (`gui.lisp:109`) | `:token-not-checked` | `check-token`, ペアリング, login.txt | 同上とペアリング中 |
| `quest-status` (`gui.lisp:113`) | `:no-active-quest` | `update-game-status` | 4Hz |

`game-status` の文言の決め方 (`gui.lisp:1784-1800`)。上から順に最初に当てはまったもの:
- 署名検証で弾いた → `:game-signature-refused` (赤)。`*psobb-rejection*` (`main.lisp:45`)
- 接続済みだがメモリを連続 60 フレーム (約 2 秒) 読めない → `:game-read-failed` (赤)。`+read-failure-frames+` (`main.lisp:10`)
- 接続済み → `:game-attached`
- それ以外 → `:game-searching`
- 録画エラーがあれば `:game-status-with-error` で後ろに付け足して赤にする

`quest-status` (`gui.lisp:1802-1814`):
- クエスト中: `"<slug>[ (+N)] - <経過時間>[ [REC]][ | vs <ゴーストのタイム> <差>]"`。N は同時に該当しているほかの定義の数。経過時間は 1 回だけ整形して、ペインとタイトルで同じ文字列を使う。別々に時計を読むと秒の境目で食い違うため (`gui.lisp:1778-1783`)
- ロビーでクエストを読み込み済み: `:quest-waiting <クエスト名>`
- それ以外: `:no-active-quest`

`server-status`:
- 成功: `:server-ok <クエスト数> <サーバー定義数> <未知の slug 数|nil>`
- 失敗: `server-status-error-text` (`api-client.lisp:49`)。WinHTTP のエラーコード 12007/12029/12030/12002/12157/12175 は `:hint-*` の平易な説明に置き換える。赤

`token-status`:
- トークンが空: `:token-unlinked`。**エラーではなく、正規の状態として扱う**
- 確認中: `:token-checking` / 成功: `:token-ok <ユーザー名>` / 401: `:token-invalid` (赤)
- 通信失敗: `token-status-error-text` (赤)
- ペアリング中: `:pairing-waiting` / `:pairing-expired` / `:pairing-failed` (赤)
- login.txt: `:file-login-checking` / `:file-login-bad-file` / `:file-login-invalid` / `:file-login-failed`

#### 1.2.2 Runs 一覧 `runs-list` (`gui.lisp:117-138`)

データは `queued-runs` (`store.lisp:302`、`%APPDATA%\ephinea-ta-client\queue.sexp` に保存される)。

| 列 | タイトル | 幅 | 値 |
|---|---|---|---|
| 1 | `:col-quest` | 34 文字 | `:quest-name`、無ければ `:quest-slug` |
| 2 | `:col-time` | 12 | `format-run-time :time-ms` (`store.lisp:89`) |
| 3 | `:col-party` | 6 | `"<人数>P"`、PB カテゴリなら `"/PB"` を付ける |
| 4 | `:col-video` | 10 | `run-video-label` (`store.lisp:265`) |
| 5 | `:col-status` | 40 | `run-status-label` (`store.lisp` 220 行付近) |

`run-video-label` の判定順: `:video-uploaded` → `:video-uploaded` / `:video-attached` → `:video-attached` / アップロード中 → `:video-uploading <%>` / `:upload-given-up` → `:video-upload-failed` / `:untrimmed` → `video-untrimmed` (C# 追加、S07) / `:video-path` がある → `:video-saved` / それ以外は空。

`run-status-label` の分岐:
- 動画が付いている場合: 中断ラン → `:status-aborted-video-attached`、held → `:status-video-held`、承認済み → `:status-video-approved`、それ以外 → `:status-video-attached`
- `:queued`: 送信用トークン (`submission-token`) が空なら `:status-queued-unlinked`、あれば `:status-queued`
- `:submitted`: 中断 → `:status-draft-aborted`、unranked → `:status-draft-unranked`、録画ファイル無し → `:status-draft-add`、自動アップロード中 → `:status-draft-auto-upload`、それ以外 → `:status-draft-upload`。さらに `entry-note` (順位メモ `run-standing-note` とゴースト差 `ghost-note`) を ` · ` でつなぐ
- `:duplicate` / `:rejected <理由>` / `:failed <理由>`

**更新はイベントが起きたときだけ** (`refresh-runs-list`, `gui.lisp:1375`)。呼ぶ場所: ラン完了 (`main.lisp:338`)、録画ファイルの紐付け (`on-keep`, `main.lisp:390`)、アップロード進捗が 1% 動いたとき (`main.lisp:208`)、アップロード完了、ファイル消失で諦めたとき (`main.lisp:189`)、Retry、一覧クリア、窓の作り直し、起動時。
> 回帰リスク: 一定間隔で一覧を作り直すと選択とスクロール位置が消える。定期更新にするなら差分を見てからにすること。

操作:
- **ダブルクリックか Enter** → `runs-list-action-callback` (`gui.lisp:686`): 行の `:url` を既定のブラウザで開く。URL が無い行 (未送信) は何もしない。`open-in-browser` (`gui.lisp:82`) は http(s) の URL しか開かない (`valid-http-url-p`)
- **選択**: Upload ボタンが選択行を使う

#### 1.2.3 操作ボタン

| ボタン | ラベル | 処理 | 詳細 |
|---|---|---|---|
| `upload-button` (`gui.lisp:413`) | `:upload-button` | `upload-video-callback` (`gui.lisp:713`) | 選択行、選択が無ければ「YouTube リンクをまだ付けられる最新のラン」(`:video-path` があり、未添付か `hosted-video-replaceable-p`) を選ぶ。エクスプローラーでファイルを選択した状態で開き (`explorer.exe /select,"<path>"`、バックスラッシュにしてから引用符で囲む)、`https://www.youtube.com/upload` も開く。エラー時は `:no-recording-for-run` (選択行に録画が無い) / `:no-recordings-yet` / `:recording-file-missing <path>` を出す |
| `recordings-folder-button` (`gui.lisp:418`) | `:recordings-folder-button` | `open-recordings-folder-callback` (`gui.lisp:738`) | `resolve-record-dir` のフォルダを作ってから ShellExecute "open" |
| `my-runs-button` (`gui.lisp:423`) | `:my-runs-button` | `open-my-runs-callback` (`gui.lisp:695`) | `<server-url>/my/runs` を開く |
| `retry-button` (`gui.lisp:428`) | `:retry-button` | `retry-callback` (`gui.lisp:671`) | `*retry-requested*` を立てるだけ。ポーリングループが次の周回で `submit-queued!` を 1 回実行する (`maybe-submit-retry`, `main.lisp:228`)。ゲームが起動していなくても動く。未連携でも止めない (匿名で送信するため) |
| `clear-list-button` × (`gui.lisp:436`) | `"×"` とツールチップ `:clear-list-tooltip` | `clear-list-callback` (`gui.lisp:677`) | **確認ダイアログ** `:clear-list-confirm` (はい/いいえ)。はいなら `clear-runs!` (`store.lisp:310`) で未送信 (`:queued`/`:failed`) 以外をすべて消し、一覧を作り直す。サーバー上の下書きと手元の録画は残る |

### 1.3 Rooms タブ (モデレーター専用, `gui.lisp:140-157`, `520`)

- `rooms-hint`: 説明文 `:rooms-hint`
- `rooms-list` の列: `:col-area` (30 文字) / `:col-condition` (20) / `:col-trigger` (22)
  - データは `run-room-rows` (`trigger-log.lisp:276`)。いま走っているラン、または直前のランの部屋ごとに行を作る
    - `:clear` 行: その部屋で扉の floor-switch が動いていればそれ、無ければ部屋で最後に倒した敵の `(:monster-dead id)`
    - `:enemy` 行: 倒した順に敵 1 種につき 1 行
  - 条件の列: `:clear` なら `:rooms-clear`、それ以外は敵の名前 (`rooms-row-condition`, `gui.lisp:895`)
  - トリガーの列: `rule-trigger-label` (`rule-form.lisp:37`) → `warp-in` / `register:N` / `floor-switch:F:S` / `monster:ID`
- **更新**: 4Hz のティックのたびに `refresh-rooms-list` (`gui.lisp:1399`) を呼ぶが、`rooms-list-signature` = (撃破数, スイッチ数, 最新撃破 ID) が変わったときだけ作り直す。ロビーでクリックしている間に選択やスクロールが飛ばないようにするため
- **ダブルクリック** → `rooms-list-action-callback` (`gui.lisp:1147`): その行のトリガーを初期選択にしてルール登録フォームを開く (§3)。トリガーが無い行は何もしない

### 1.4 Settings タブ (`settings-tab`, `gui.lisp:518`)

グループは上から次の順 (`settings-tab-groups`, `gui.lisp:28`):
`language-group` → `connection-group` → `recording-group` → `ghost-group` → `pinshare-group` (許可されたときだけ) → `updates-group` → `tray-group` → `advanced-group`

各グループは枠付きでタイトルを出す (`:group-*`)。

#### 1.4.1 言語 `language-group`
| コントロール | 種別 | 保存先 | 処理 |
|---|---|---|---|
| `language-radio` (`gui.lisp:159`) | ラジオ (English / 日本語。ラベルは常にその言語自身で書く、`language-label`) | config `:language` (`:en`/`:ja`) | `language-changed-callback` (`gui.lisp:610`): `*language*` を変えて保存し、**窓を作り直す** (`rebuild-interface`)。CAPI はラベルを作成時に固定するため |

`rebuild-interface` (`gui.lisp:621`) が新しい窓に引き継ぐもの: 画面上の位置、未保存の Server URL・API トークン・Pin Share 合言葉の入力内容、選択中のタブ (インデックスではなく名前で合わせる。Rooms タブが増減するため)、隠れている状態 (トレイ常駐中に作り直しても窓を出さない)。`*interface*` を新しい窓に差し替えてから古い窓を destroy する。その後 runs 一覧の再描画、check-server、check-token を実行する。
> HTML 化すれば文言の差し替えだけで済み、作り直しは要らない。ただし「トレイ常駐中に窓を勝手に表示しない」という条件は守ること。

#### 1.4.2 接続 `connection-group` (`gui.lisp:467`)
| コントロール | 表示条件 | 保存先 | 処理 |
|---|---|---|---|
| `link-account-button` `:link-account-button` | 常に表示 | - | `link-account-callback` → ブラウザでのペアリング (§2.1) |
| `server-url-input` `:server-url-label` | **デバッグモードのときだけ** (`debug-mode-p`: config `:debug t` か `--debug` で起動) | config `:server-url` | Save で反映 |
| `api-token-input` (パスワード欄) `:api-token-label` | 常に表示 | config `:api-token` | Save で反映 |
| `save-button` `:save-button` | 常に表示 | - | `save-settings-callback` (`gui.lisp:596`): URL の末尾の `/` と空白を削り、トークンを `normalize-token` で整えて保存し、`check-server` と `check-token :notify t` を実行する (結果をダイアログで出す: `:token-ok-dialog` / `:token-rejected-dialog`) |

#### 1.4.3 録画 `recording-group` (`gui.lisp:476`)
| コントロール | 保存先 | 有効条件 | 処理 |
|---|---|---|---|
| `tracking-only-check` `:tracking-only-label` | `:tracking-only` | 常に有効 | 保存し、`tracking-private-check` の有効・無効を連動させる (`gui.lisp:1162`)。次のクエストから効く |
| `tracking-private-check` `:tracking-private-label` | `:tracking-private` | `:tracking-only` が真のときだけ有効 | 保存する |
| `record-audio-check` `:record-audio-label` | `:record-audio` | 常に有効 | 保存する。録画開始時に読むので次のクエストから効く |
| `record-storage-note` | - | - | `:record-storage-note <record-max-total-gb>` (作成時に 1 回だけ) |
| `video-retention-note` | - | - | `:video-retention-note` |
| `auto-publish-check` `:auto-publish-label` | **サーバー側の設定**。config `:auto-publish` はそのキャッシュ | 常に有効 | `toggle-auto-publish-callback` (`gui.lisp:1445`): オンにするときだけ**確認ダイアログ** `:auto-publish-confirm`。いいえならチェックを戻して終わる。はいならワーカーで `POST /api/me/auto-publish {"enabled":0|1}` を送り、成功したらキャッシュを保存する。失敗したらチェックを最後に分かっているサーバーの状態に戻し、`:auto-publish-failed` を出す。サーバー側の値は `check-token` → `apply-auto-publish` (`gui.lisp:1432`) で /api/me の `auto_publish` から同期する |
| `record-dir-display` + `record-dir-button` `:change-folder-button` | `:record-dir` | 常に有効 | `choose-record-dir-callback` (`gui.lisp:660`): OS のフォルダ選択ダイアログで選び、すぐ保存してラベルを `:record-dir-label <path>` に更新する |

> 設定欄を持たないもの (`+forced-config-keys+`, `config.lisp:9`): `:auto-submit t`, `:submit-aborted t`, `:completion-sound nil`, `:record-enabled t`, `:video-upload t`。`migrate-config` が保存値を消し、常に既定値になる。ffmpeg のパス (`:ffmpeg-path`)、`:hw-encode`、`:record-max-total-gb`、`:pinshare-server`、`:update-repo` も GUI には出さず、config を手で書き換えたときだけ効く。

#### 1.4.4 ゴースト `ghost-group` (`gui.lisp:482`)
| コントロール | 保存先 | 処理 |
|---|---|---|
| `ghost-race-check` `:ghost-race-label` | `:ghost-race` | 保存する。次にクエストを読み込んだときから効く |
| `ghost-overlay-check` `:ghost-overlay-label` | `:ghost-overlay` | 保存する。オフにしたらすぐ `overlay-hide!` を呼ぶ |
| `ghost-marker-check` `:ghost-marker-label` | `:ghost-marker` | 保存する。次の 4Hz 更新でオーバーレイの地図データに反映される |
| `overlay-corner-pane` (ドロップダウン) `:overlay-corner-label` | `:overlay-corner` | 項目 (`overlay-corner-items`, `gui.lisp:1301`): top-right / top-center / top-left / middle-right / middle-left / bottom-right / bottom-center / bottom-left / custom (`:corner-*`)。知らない値は top-right として扱う。オーバーレイを Ctrl+ドラッグで動かしたら、ポーリングループが `refresh-overlay-corner-pane` (`gui.lisp:1332`) を呼んで選択を custom に合わせる |

#### 1.4.5 Pin Share `pinshare-group` (`gui.lisp:492`) — 限定提供
表示条件: `*pinshare-allowed-p*` (§6.6)。**判定が変わったら、窓を作り直さずにこのグループだけを出し入れする** (`sync-pinshare-group`, `gui.lisp:46`)。4Hz ティックの中で呼ばれ、ウィンドウごとのキャッシュ `*pinshare-group-cache*` と比べて変わったときだけ layout-description を差し替える。作り直しをやめた理由 (cebc8ce): 判定はいつでも変わりうる (CHECK-TOKEN、401、30 分ごとの再確認)。そのたびに作り直すと、ゲーム中にトレイから窓が飛び出す、開いているダイアログの親が消える、ほかの作り直しと競合する、という問題が起きる。

| コントロール | 保存先 | 処理 |
|---|---|---|
| `pinshare-enabled-check` `:pinshare-enabled-label` | `:pinshare-enabled` | `toggle-pinshare-callback` (`gui.lisp:1213`): 保存し、合言葉欄の内容もあわせて保存する (先にオンにしても、先に入力しても動くように)。中継スレッドは毎ティック設定を読むので 1 秒以内に開始・停止する。**オンにすることが、addons フォルダへ書き込むことへの同意になる** |
| `pinshare-channel-input` `:pinshare-channel-label`、最大 64 文字 | `:pinshare-channel` | Enter で `apply-pinshare-channel-callback` |
| `pinshare-channel-button` `:pinshare-channel-save-button` | 同上 | 合言葉を保存する。中継は次のティックで変化に気づき、新しい合言葉で入り直す |
| `pinshare-channel-note` | - | `:pinshare-channel-note` |
| `pinshare-status-pane` | - | `pinshare-status-text *pinshare-status*` (`pinshare.lisp:463`)。4Hz で更新し、エラーは赤 |
| `pinshare-pin-set-pane` | - | `pinshare-pin-set-text` (`gui.lisp:1229`): `:pinshare-pin-set-active <名前> <作者>` / クエスト読込中でセット無し → `:pinshare-pin-set-none` / クエスト無し → `:pinshare-pin-set-none-idle`。4Hz |
| `pinshare-save-new-button` `:pinshare-save-new-button` | - | `save-pin-set-new-callback` (`gui.lisp:1276`): 事前チェック (`pinshare-save-precheck`) → `POST /api/pin-sets`。確認は出さない |
| `pinshare-save-overwrite-button` `:pinshare-save-overwrite-button` | - | `save-pin-set-overwrite-callback` (`gui.lisp:1284`): 使用中のセットが自分のもの (`mine == 1`) でなければ `:pinshare-save-not-mine`。事前チェックの後、**確認ダイアログ** `:pinshare-save-confirm-overwrite <名前> <ピン数> <矢印数>` を出してから `POST /api/pin-sets/<id>/items` |

中継の状態 (`*pinshare-status*`) と表示の対応 (`pinshare-status-text`):

| 状態 | 文言キー | 赤 |
|---|---|---|
| `(:off)` | `:pinshare-status-off` | |
| `(:not-allowed)` | `:pinshare-status-not-allowed` | |
| `(:no-channel)` | `:pinshare-status-no-channel` | |
| `(:waiting-game)` | `:pinshare-status-waiting-game` | |
| `(:connecting)` | `:pinshare-status-connecting` | |
| `(:connected channel members)` | `:pinshare-status-connected` | |
| `(:connected-no-addon)` | `:pinshare-status-no-addon` (接続済みだが、アドオンからまだ命令が来ていない。新規インストール後は Reload が必要) | |
| (C#) `AddonOutdated` | `pinshare-status-addon-outdated` (ゲームが古い版のアドオンを読み込んだまま。Reload が必要。S21) | ○ |
| `(:local-only set-name)` | `:pinshare-status-local-only` (合言葉なしでピンセットだけ表示している) | |
| `(:error text)` | `:pinshare-status-error` | ○ |
| `(:no-addon-plugin)` | `:pinshare-status-no-plugin` (Solybum のアドオンプラグインが無い) | ○ |
| `(:install-failed text)` | `:pinshare-status-install-failed` | ○ |
| `(:broken-link path)` | `:pinshare-status-broken-link` | ○ |
| `(:conflict)` | `:pinshare-status-conflict` (旧 PowerShell 版の中継が動いている) | ○ |

保存の事前チェック `pinshare-save-precheck` (`gui.lisp:1262`): クエストを読み込んでいない → `:pinshare-save-no-quest`、チャンネルのピンと矢印がどちらも 0 件 (`*pinshare-channel-items*` が nil の場合も含む = 未接続) → `:pinshare-save-no-items`。
保存結果 (`save-pin-set-in-background`, `gui.lisp:1237`): `:created` → `:pinshare-save-created <url>`、`:updated` → `refetch-pin-set` を呼んでから `:pinshare-save-updated <pins> <arrows>`、それ以外 → `:pinshare-save-failed <message|error|outcome>`。結果はダイアログで出す。

#### 1.4.6 アップデート `updates-group` (`gui.lisp:498`)
| コントロール | 保存先 | 処理 |
|---|---|---|
| `update-status-pane` | - | `:version-status <version> <note>` (`set-version-status`, `gui.lisp:1605`) |
| `auto-update-check` `:auto-update-label` | `:auto-update` | 保存する (次回起動から効く) |
| `check-updates-button` `:check-updates-button` | - | `check-for-updates` (§7.2) |

#### 1.4.7 常駐 `tray-group` (`gui.lisp:503`)
| コントロール | 保存先 | 処理 |
|---|---|---|
| `close-to-tray-check` `:close-to-tray-label` | `:close-to-tray` (既定 t) | 保存する |
| `autostart-check` `:autostart-label` | **レジストリが正** (config には保存しない) | `toggle-autostart-callback` (`gui.lisp:1593`): `set-autostart!` を実行し、**その後レジストリを読み直してチェックを実際の状態に合わせる** (失敗したら元に戻る) |
| `start-minimized-check` `:start-minimized-label` | `:start-minimized` | 保存する |
| `rank-toast-check` `:rank-toast-label` | `:rank-toast` (既定 t) | 保存する。送信のたびに読む |

#### 1.4.8 詳細 `advanced-group` (`gui.lisp:512`)
| コントロール | 表示条件 | 処理 |
|---|---|---|
| `trigger-log-check` `:trigger-log-label` | 常に表示 | `toggle-trigger-log-callback` (`gui.lisp:876`): `:trigger-log` を保存する。オンにしたときは `start-trigger-log` ですぐファイルを作ってヘッダーを書き、`:trigger-log-on <path>` のダイアログを出す。オフにしたら `close-trigger-log` |
| `register-rule-button` `:register-rule-button` | `*moderator-p*` のときだけ | `register-quest-rule-callback` (`gui.lisp:1137`) → §3 |

**トリガーログ** (`trigger-log.lisp`): 保存先は `%APPDATA%\ephinea-ta-client\trigger-log.txt`。書き込み元はポーリングスレッドの `log-trigger-changes` (`main.lisp:339`)。同じクエストの連続する 2 スナップショットを比べて次の変化を追記する:
- `<時刻> "<quest>" register N: old -> new`
- `<時刻> "<quest>" floor F switch S: off -> on`
- `<時刻> "<quest>" monster ID killed (<名前>, unitxt U)`
- 開始時のヘッダー: `=== trigger logging started <時刻> ===`

GUI スレッドとポーリングスレッドの両方から書くのでロックで直列化している。ログのオンオフに関係なく、ルール登録フォームのために `update-last-kill` と `update-run-logs` は常に動かす (`main.lisp:342-349`)。

---

## 2. アカウント連携

### 2.1 ブラウザでのペアリング (`gui.lisp:747-826`)

1. `link-account-callback` → `start-pairing-flow`。ワーカー `*pairing-process*` は 1 本だけで、動いている間はもう一度押しても何もしない
2. `POST /api/pair {"label":"Desktop client (<マシン名>)"}` → 201 `{code, interval(既定 2), expires_in(既定 600)}` (`api-client.lisp:233`)
3. `<server>/pair?code=<code>` をブラウザで開き、`token-status` に `:pairing-waiting` を出す
4. `interval` 秒ごとに `GET /api/pair/<code>` を `ceil(expires_in/interval)` 回まで呼ぶ
   - 200 で token あり → `:complete` → `finish-pairing`
   - 200 で token なし → `:pending`
   - 404 → `:gone` → `:pairing-expired`
   - 通信エラー → `:pending` 扱いで待ち続ける (Wi-Fi の瞬断では諦めない)
   - 待っている間に設定欄でトークンが貼られたら、そちらを優先して終える
   - `*stop-requested*` で終わる
   - 回数を使い切ったら `:pairing-expired`
5. 開始 (POST) 自体に失敗したら `:pairing-failed <msg>` (赤)
6. `finish-pairing` (`gui.lisp:859`): config `:api-token` を保存し、**その時点の** `*interface*` のトークン欄に書き込み (待っている間に言語切替で窓が作り直されているかもしれないため)、`check-token` を呼ぶ

### 2.2 login.txt (`gui.lisp:828-857`, `credentials.lisp`)
- 置き場所: exe と同じフォルダの `login.txt` (`lw:lisp-image-name` の親フォルダ)
- 書式: `username=...` と `password=...`。BOM、CRLF、空行、`#` で始まるコメント、キーの前後の空白を許す。`=` は最初の 1 つだけで区切る
- `prompt-for-token-setup` (`gui.lisp:758`): 起動時、トークンが空で login.txt があれば `start-file-login-flow` を始める。**ブラウザは勝手に開かない**
- `check-token` が 401 を返し、login.txt があればそれで再ログインする (`main.lisp:468-471` の `:on-invalid`)
- `POST /api/login {username,password,label:"Desktop client (<マシン名>) [login.txt]"}` → 201 `{token}` / 401
- 失敗は `token-status` に赤で出すだけ。ブラウザのペアリングには切り替えない

### 2.3 トークン確認 `check-token` (`gui.lisp:1486`)
- トークンが空: `set-pinshare-permission nil` にして `:token-unlinked` を出す (窓は作り直さない)
- それ以外: `:token-checking` を出し、ワーカーで `GET /api/me` (Bearer)
  - `:ok`: 以下を**この順に**行う
    1. `:token-ok <username>` を表示
    2. `set-pinshare-permission (pinshare-feature-p user)` — 失敗しうる処理より先に済ませる
    3. `apply-moderator-role` (役割が変わったら窓を作り直す)
    4. `apply-auto-publish`
    5. `:anon-token` があれば `POST /api/merge-anonymous {"anonymous_token":...}`。200 か 404 なら `:anon-token` を空にする。通信エラーなら残して次回に回す
    6. `*retry-requested* t` (未送信分を送る)
    7. `notify` が真なら `:token-ok-dialog`
  - `:unauthorized`: `set-pinshare-permission nil`、`:token-invalid` (赤)。notify なら `:token-rejected-dialog`。`on-invalid` を呼ぶ
  - 通信エラー: `token-status-error-text` (赤)。**Pin Share とモデレーターの判定は変えない**

---

## 3. クエストルール登録フォーム (モデレーター専用)

入口は 2 つ: Settings > 詳細の「ルール登録」ボタン (プリセット無し)、Rooms タブでのダブルクリック (その行をプリセットにする)。

流れ (`run-quest-rule-flow`, `gui.lisp:1110`):
1. ワーカーで `GET /api/quests` → `timeable-quests` (`rule-form.lisp:9`): `start` と `end` の両方を持つクエストだけに絞る
2. 0 件なら `:rule-no-parents`
3. GUI スレッドでモーダルダイアログ `quest-rule-dialog` (`gui.lisp:966`) を開く。タイトルは `:rule-dialog-title`
4. Register を押すと `post-quest-rule-in-background` → `POST /api/quests`
5. 取得に失敗したら `:rule-fetch-failed <msg>`

フォームの項目 (上から):
| 項目 | 種別 | 内容 |
|---|---|---|
| `quest-pane` `:rule-quest-label` | ドロップダウン | 親クエスト。表示は `quest-parent-label` = `"<name>  (<slug>)"`。初期選択は `detected-parent` (`rule-form.lisp:19`) で直前に遊んだ `*run-quest*` を探す: まず `game_number` で、次に episode が一致し `game_names` に名前を含むもの。見つからなければ先頭 |
| `name-pane` `:rule-name-label` | テキスト | 必須 |
| `desc-pane` `:rule-desc-label` | テキスト | 必須 |
| `end-pane` `:rule-end-label-form` | ドロップダウン | 項目 (`rule-end-items`, `gui.lisp:903`) は、プリセット (あれば先頭) + 今回のランの `run-room-rows` (`"<area> - <condition>"`) + 手入力用の 3 つ: `:rule-end-monster` → `:monster`、`:rule-end-floor-switch` → `:floor-switch`、`:rule-end-register` → `:register`。初期選択は先頭 |
| `preview-pane` | 表示のみ | ランの行を選んでいれば `"→ <trigger-label>"`、手入力を選んでいれば `:rule-manual-hint` |
| `val1-pane` `:rule-val1-label` / `val2-pane` `:rule-val2-label` | テキスト | 手入力を選んだときだけ有効。val2 は floor-switch のときだけ有効 (`qrd-end-changed`, `gui.lisp:1038`) |
| `start-pane` `:rule-start-label-form` | ドロップダウン | `:rule-start-inherit` (親の開始条件を使う、既定) / `:rule-start-warp-in` (`(:warp-in)`) |
| `ok-button` `:rule-register-ok` / `cancel-button` `:rule-cancel` | ボタン | |

入力チェック (`qrd-ok`, `gui.lisp:1062`)。**エラーでもダイアログは閉じない**:
`:rule-need-quest` → `:rule-need-name` → `:rule-need-desc` → `:rule-need-end` → 手入力の値が範囲外なら `:rule-need-values`。
手入力の範囲 (`resolve-manual-trigger`, `rule-form.lisp:69`): monster ID 0..65535、floor 0..17、switch 0..255、register 0..255。

送信本体 (`create-quest-rule`, `api-client.lisp:636`): `{"parent":slug,"name","description","end":<trigger json>,"start"?:<trigger json>}`。
trigger json は `{"type":"monster","monster":id}` / `{"type":"floor-switch","floor":F,"switch":S}` / `{"type":"register","register":N}` / `{"type":"warp-in"}` (`trigger->json`)。
結果: 201 → `:rule-created <slug>` を出し、`check-server` で新しいルールをすぐ検出定義に取り込む。409 → `:rule-duplicate <msg>`、403 → `:rule-forbidden`、400 → `:rule-rejected <msg>`、401 と通信エラー → `:rule-post-failed <msg>`。`rule-error-message` は `message`、無ければ `errors` を `; ` でつないだもの、どちらも無ければ `?`。
送信後に確認ダイアログは出さない。フォームそのものが確認の役を兼ねる。

---

## 4. 起動・終了シーケンス (`main.lisp:434-495`, `419-432`)

### 4.1 起動 `main`
1. **多重起動の防止**: `already-running-p` (`tray-win32.lisp:575`) で名前付きミューテックス `RappyRunsClient-single-instance` を作る。GetLastError が 183 (ERROR_ALREADY_EXISTS) なら `signal-existing-instance` を呼ぶ: `FindWindowW("RappyRunsTrayWindow")` で見つけた窓に `WM_APP+2` を PostMessage し、`ExitProcess(0)` で終わる。受け取った側は窓を表示する。ミューテックスのハンドルはプロセスが終わるまで持ち続ける
2. `*stop-requested*` と `*really-quitting*` を nil にする
3. `load-config!` (`%APPDATA%\ephinea-ta-client\config.sexp`、`migrate-config`)
4. `*language*`、`*moderator-p*` (キャッシュ `:moderator`)、`*pinshare-allowed-p*` (キャッシュ `:pinshare-allowed`) を設定する。**最初の描画からタブとグループを正しく出すため**
5. `cleanup-old-update-files` (`updater.lisp:270`): `RappyRunsClient.exe.old`、%TEMP% の `rappyruns-update.ps1`、`RappyRunsClient-update.zip`、`rappyruns-update-stage\` を消す
6. `:auto-update` が真でリリース版なら `startup-auto-update` を**窓を作る前に**実行する (§7.1)。更新を適用した場合はここから戻らない
7. `load-queue!` (queue.sexp)、`load-quest-defs`
8. `client-window` を作って表示し、`*interface*` に入れる
9. `refresh-runs-list` → `check-server` → `check-token :on-invalid (login.txt があれば再ログイン)` → `prompt-for-token-setup` → `report-startup-update`
10. `start-tray!` (§5)
11. `start-pinshare!` (中継の監督スレッドと許可の再確認スレッド)
12. `log-session-info` (録画ログにビルドとマシンの情報を 1 行書く)
13. `:hw-encode` が真なら `start-hw-encoder-probe`
14. `startup-minimized-p` (`config.lisp:108`: `:start-minimized` または `--minimized` 引数) が真なら、窓を一度表示してから `:hidden` にする (一瞬見える可能性はあるが、表示経路を 1 つにするため)
15. ポーリングスレッド `eta-client-poll` を起動する

### 4.2 ポーリングループ (`poll-loop`, `main.lisp:380`)
- 起動時に `cleanup-stale-recordings` (前回クラッシュで残った `rec-tmp-*.mp4` を消す)
- 毎周回 `note-poll-activity` (`main.lisp:215`): `*poll-busy-p*` = クエスト中か録画中。暇になった瞬間に、先送りしていたアップデートを 1 回だけ適用する
- 未接続: `poll-search-step` (`main.lisp:237`)。1 秒ごとにゲームを探す。見つけたら Authenticode で署名を確かめ、公式クライアントでなければ接続しない。同じ pid の判定は使い回す。`*pinshare-game-exe*` は**署名を確かめたゲームのときだけ**設定する。未接続の間も録画の停止処理、Retry、アップロード、容量整理、GUI 更新は続ける
- 接続中: `poll-frame-step` (`main.lisp:298`)。30Hz。スナップショット、検出、録画、ゴースト、ピンセット取得 (`maybe-start-pin-set-fetch`)、ラン完了時の処理、トリガーログを行う。GUI 更新とアップロード、容量整理、gdigrab のプローブは `+gui-update-interval+` = 1/4 秒ごと
- プロセスが消えたら `poll-detach-step`: `*audio-target-pid*` と `*pinshare-game-exe*` を nil にする。C# 版はさらに、デタッチ処理の後 (例外時も) でゴースト (ロード・参照・レース・カメラ) とピンセット (セット・スラッグ・ロード) を忘れる (S37)。Lisp は次のアタッチ後の最初のフレームまで残していた
- 終了時 (unwind-protect): `recorder-shutdown`、`close-trigger-log`、`close-reader`

ラン完了時の処理 (`run-completion-sounds`, `main.lisp:152`): `:completion-sound` は固定で nil なので、いまは音を鳴らさない。`handle-completed-runs` → `enqueue-run!` → `submit-queued!` → `notify-standing-toasts`。

### 4.3 終了 `quit-app` (`main.lisp:419`)
呼ばれるのは、トレイの Quit と、close-to-tray がオフのときの × ボタン。
1. `*really-quitting*` と `*stop-requested*` を t にする
2. ポーリングスレッドを最大 10 秒 join する (録画をきちんと止めるため)
3. `tray-remove-icon-now` (通知領域にアイコンの残骸を残さない)
4. `exit-process-now` = `ExitProcess(0)`。**LW:QUIT は使わない**。トレイスレッドから呼ぶと確実に終わらず、Quit を押しても何も起きないように見えたため (5872e4c)
設定とキューは変更のたびに保存しているので、いきなり終了しても何も失わない。

### 4.4 クラッシュへの備え
- プロセス全体を覆うクラッシュハンドラは**無い**。各処理を `ignore-errors` や `handler-case` で囲み、1 つが失敗してもポーリングループを止めない方針
- 残骸の掃除: 起動時の `cleanup-stale-recordings` と `cleanup-old-update-files`
- ヒープ枯渇 (2026-09-08 の全停止) はサーバー側の話なので、ここでは扱わない
- 中継スレッドはセッションで例外が起きたら `(:error msg)` にして 5 秒待ち、やり直す (`pinshare-loop`)
- C# では AppDomain.UnhandledException と TaskScheduler.UnobservedTaskException でログを取り、同じようにワーカーの例外がアプリ全体を落とさないようにする

---

## 5. トレイ (`tray-win32.lisp`)

### 5.1 仕組み
- 専用スレッド `eta-client-tray` (`tray-thread-main`, `tray-win32.lisp:526`) で次を行う: `RegisterWindowMessage("TaskbarCreated")` → ウィンドウクラス `RappyRunsTrayWindow` を登録 → 隠しウィンドウ `RappyRunsTray` を作る → `Shell_NotifyIcon(NIM_ADD)` → GetMessage ループ
- アイコン: exe に埋め込んだアイコン (ExtractIconW index 0)。取れなければ IDI_APPLICATION
- ツールチップ: `:tray-tooltip` (最大 127 文字)
- コールバックメッセージ: `WM_APP+1`、アイコン ID は 1
- `start-tray!` は何度呼んでも 1 つしか作らない。失敗してもアプリは止めない

### 5.2 イベント (`RappyTrayWndProc`, `tray-win32.lisp:466`)
| イベント | 動作 |
|---|---|
| 左ダブルクリック | `tray-show-main-window`: 表示状態を `:normal` にして前面に出す |
| 右クリック (WM_RBUTTONUP / WM_CONTEXTMENU) | `tray-popup-menu`: 項目は `:tray-show` (Show) と `:tray-quit` (Quit)。SetForegroundWindow → TrackPopupMenu(TPM_RETURNCMD) → PostMessage(WM_NULL) の順で呼ぶ (メニューが閉じなくなるのを防ぐ定番の手順) |
| バルーンのクリック (NIN_BALLOONUSERCLICK 0x405) | `tray-balloon-clicked`: `*balloon-url*` があればそれを開き、無ければ窓を表示する |
| `WM_APP+2` | 2 つ目のインスタンスからの依頼。窓を表示する |
| TaskbarCreated | Explorer が再起動したのでアイコンを登録し直す |
| WM_DESTROY | アイコンを消して PostQuitMessage |

> 回帰リスク (560918f): 日本語のメニュー項目を ASCII 前提の文字列変換で渡すと例外になり、`ignore-errors` に飲まれてメニューが**出なくなった**。C# では問題になりにくいが、ローカライズしたメニューの表示テストは入れること。

### 5.3 閉じる・最小化
- `client-confirm-destroy` (`gui.lisp:544`): `*really-quitting*` が真なら閉じる。`:close-to-tray` が真なら窓を隠して閉じるのを取り消す。それ以外は `quit-app` (本当に終了する)
- 言語切替の作り直しは `capi:destroy` を使うので、この関数を通らない
- 起動時に最小化: §4.1 の 14

### 5.4 バルーン (トースト) 通知
入口は `notify-user` (`recording.lisp:1009`) → `tray-notify` (`tray-win32.lisp:399`)。どのスレッドから呼んでもよい。トレイがまだ無ければ何もしない。Windows は同時に 1 つしか出さないので、クリック先の URL は最新の通知のものだけを覚える。タイトルは最大 63 文字、本文は最大 255 文字。アイコンは `:none`/`:info`/`:warning`。

| 事象 | 文言 | アイコン | クリック先 | 抑制の規則 |
|---|---|---|---|---|
| 送信の返答で順位が付いた (`notify-standing-toasts`, `store.lisp:205`) | `standing-toast` (`store.lisp:157`): 暫定 1 位 (2 組以上いる板で) → `:toast-rank1-*` / 3 位以内で誰かに勝った → `:toast-rank-*` / 自己ベスト更新 → `:toast-pb-*` / その板で初めての記録 → `:toast-first-*` | info | ランのページ (`:url`) | `:rank-toast` がオフなら出さない。上のどれにも当たらなければ出さない (「1 組中 1 位」や最下位は通知しない) |
| 順位は通知しないがゴーストに勝った | `ghost-toast`: `:ghost-toast-*` | info | ランのページ | 1 ランにつき 1 つだけ (順位の通知を優先する) |
| 録画を開始できない | `:notify-capture-failed-title` + `:notify-capture-blocked-text` (Windows のエラー 4551 = アプリ制御ポリシー) または `:notify-capture-failed-text` | warning | 窓を表示 | 連続して失敗している間は 1 回だけ (`*capture-failure-notified*`)。成功したらリセット |
| ハードウェアエンコーダを確かめられずソフトウェアで録画した | `:notify-software-encode-*` | info | 窓を表示 | プロセスにつき 1 回 |
| ウィンドウモードのゲームをモニターごと撮っていて、重なった窓も写る | `:notify-overlap-*` | info | 窓を表示 | プロセスにつき 1 回 |
| remux に失敗して末尾を切れなかった | `:notify-untrimmed-*` | warning | 窓を表示 | そのたびに出す |

---

## 6. 自動起動 (`autostart-win32.lisp`)

- レジストリ: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`、値の名前 `RappyRunsClient`、REG_SZ
- 値: `"<exe のフルパス>" --minimized` (`autostart-command`)
- 有効かどうか (`autostart-enabled-p`): 値があり、**いまの exe を指しているとき**だけ真。インストール先を移すと偽になり、オンにし直すと書き換わる
- 無効にするときは値を消す。値が無くても成功として扱う
- 64bit の HKEY_CURRENT_USER の定数は符号拡張した `0xFFFFFFFF80000001` を使う (C# の Registry API を使えば気にしなくてよい)

---

## 7. セルフアップデート (UI まわり)

### 7.1 起動時 `startup-auto-update` (`gui.lisp:1718`)
- `GET https://api.github.com/repos/PSOBBAITools/rappyruns-client/releases/latest` (config `:update-repo` で差し替え可)。アセット名は `RappyRunsClient.zip`
- 判定 `startup-update-decision` (`updater.lisp:65`): リリースが取れない → `:check-failed`、新しくない → `:up-to-date`、インストール先に書けない → `:not-writable`、それ以外 → `:apply`
- `:apply` のとき: 小さなスプラッシュ `update-splash` (`:update-downloading <tag> <MB> <total>`、1MB ごとに更新) を出して `%TEMP%\RappyRunsClient-update.zip` にダウンロードする。サイズと `PK\3\4` を確かめる。成功したら `:update-restarting` を出して `launch-updater-and-quit`。失敗したら `:download-failed` を記録してスプラッシュを閉じる
- 窓ができたら `report-startup-update` (`gui.lisp:1751`) が結果を出す: `:up-to-date` → ステータス行、`:not-writable` → `offer-manual-download`、`:download-failed` → 赤のステータスとダイアログ。**`:check-failed` は何も出さない**

### 7.2 手動 `check-for-updates` (`gui.lisp:1616`)
- 開発ビルド (`*client-version*` が nil) なら `:update-dev-build <リリースページ>` のダイアログだけ出す
- それ以外は `:update-checking` → ワーカーで確認し、**どの結果もダイアログで伝える**: 失敗 → `:update-check-failed-dialog`、最新 → `:update-latest-dialog`、新しい版がある → 確認せずにダウンロードする
- `run-update-download` (`gui.lisp:1671`): 書けない → `offer-manual-download` (**確認ダイアログ** `:update-not-writable-confirm`。はいならリリースページを開く)。ダウンロード失敗 → 赤 + ダイアログ。**クエスト中か録画中 (`*poll-busy-p*`) なら `*update-ready-zip*` に置いて `:update-after-run`** と表示し、暇になったら `note-poll-activity` が適用する。それ以外はすぐ `apply-update-restart`

### 7.3 引き継ぎ `launch-updater-and-quit` (`updater.lisp:288`)
- `%TEMP%\rappyruns-update.ps1` を **UTF-8 の BOM 付き**で書く (PowerShell 5.1 は BOM 無しを ANSI (cp932) として読み、日本語のパスが壊れたため)
- `powershell -NoProfile -ExecutionPolicy Bypass -File ...` を起動 → `*stop-requested*` → ポーリングスレッドを最大 10 秒 join → 窓を destroy → `lw:quit`
- スクリプトの処理 (`updater-script-text`): 旧プロセスの終了を最大 60 秒待つ → zip を stage に展開 → 新しい exe があるか確かめる → 旧 exe を `.old` に移す (10 回まで再試行) → 新しい exe を `RappyRunsClient.exe` としてコピー → `data\` と `ffmpeg\` を上書きコピー (ffmpeg は失敗しても続ける) → 新しい exe を起動 → 後片付け。失敗したら `.old` を戻して旧 exe を起動する。ログは `%TEMP%\rappyruns-update.log`
- **`data\pin-share\` (init.lua と pinshare-input.dll) もこの data コピーで更新される**。それがゲーム側へ反映されるのは中継セッションの開始時 (§8.3)

> C# 移植の際は、同じ zip 名、同じ exe 名、`data\` のレイアウトを保つか、旧クライアントから新クライアントへの移行手順を別に用意すること。

---

## 8. Pin Share

### 8.1 構成
```
[ゲーム PsoBB.exe (32bit)]
  addons\Pin Share\init.lua  (Lua アドオン。描画と入力を担当)
     │  ffi.load
     ├─ addons\Pin Share\pinshare-input.dll  (入力の優先)
     │  ファイル
     ├─ exchange\out.txt  アドオン → クライアント
     └─ exchange\in.txt   クライアント → アドオン
[Rappy Runs Client]  中継 (pinshare-win32.lisp)
     │  WebSocket (JSON)
[wss://pin-share-server-production.up.railway.app]  中継サーバー (ピン一覧の正本、番号付け、期限切れ、1 人あたりの上限)
[Rappy Runs サイト]  /api/me features、/api/quests/:slug/pins、/api/pin-sets
```
クライアントはゲームのプロセスに一切触れず、2 つのファイルを読み書きするだけ (読み取りのみという方針の範囲内)。

### 8.2 利用条件
`pinshare-wanted` (`pinshare-win32.lisp:291`) は次を上から順に見て、最初に当たった状態を返す:
1. `:pinshare-enabled` がオフ → `(:off)`
2. `*pinshare-allowed-p*` が偽 → `(:not-allowed)`
3. 合言葉が空で、ピンセットも無い → `(:no-channel)`
4. 署名を確かめたゲームが無い → `(:waiting-game)`
5. すべて満たす → `(exe channel server-url)` でセッションを始める。合言葉が空でもピンセットがあれば `local-only` セッションになり、in.txt は書くがサーバーには繋がない

合言葉は `pinshare-channel` で整える: 制御文字を除く → 前後の空白を削る → 64 文字で切る (サーバーと同じ処理)。
サーバー URL は `:pinshare-server` が空なら既定値を使う。

### 8.3 アドオンのインストールと更新 `pinshare-ensure-addon` (`pinshare-win32.lisp:198`)
セッションを始めるたびに実行する。置き場所は `<PsoBB.exe のフォルダ>\addons\Pin Share\`。
1. `addons\init.lua` (Solybum のアドオンプラグイン本体) が無い → `(:no-addon-plugin)`
2. `addons\Pin Share` がリンク先の消えたジャンクションかシンボリックリンク → `(:broken-link path)`。**消さない** (ユーザーのリンクなので)
3. 同梱の `data\pin-share\init.lua` を探す。場所は exe の隣、無ければソースツリー (`pinshare-bundled-file`、`lw:lisp-image-name` から辿る。argv[0] は相対パスのことがあるので使わない)
4. `addons\Pin Share` が**リンクでなければ**、中身を比べて違うときだけ init.lua を上書きし、`pinshare-install-input-dll` も実行する。リンク (開発者の作業コピー) には書かない。Lisp はその場で上書きしていた (書き込み失敗で途中までのスクリプトが残りうる)。C# 版 (S45) は `init.lua.new` に書き切り (ディスクまでフラッシュ) してから上書きリネーム (`File.Move(overwrite: true)`) で差し替え、失敗しても動いている init.lua には触れない。`.new` は成否にかかわらず片付け、クライアントが途中で落ちて残った `.new` も次回の確認で消す。DLL の `.new` も同じくフラッシュしてから入れ替える
5. `exchange\` を作る
6. init.lua が無ければ `(:install-failed "init.lua is missing")`、例外なら `(:install-failed msg)`
- **書くのは init.lua と pinshare-input.dll だけ**。`options.lua` (ユーザーのキー割り当て) などには触れない
- 問題があれば状態を出して 5 秒待ち、やり直す

DLL の更新 `pinshare-install-input-dll` (`pinshare-win32.lisp:158`):
- まず前回までに退避した `pinshare-input.dll.old*` を消してみる (ロックされていれば残る)
- 中身が同じなら何もしない。違えば上書きし、失敗したら (ゲームがロード中でロックされている) `pinshare-input.dll.old-<universal-time>` に**リネームしてから**新しいファイルを書く (ロード中の DLL でもリネームはできる)。それでも書けなければ退避したファイルを元に戻して失敗とする (ウイルス対策ソフトや権限の問題)。C# 版 (S39) は順序を変え、新しい中身をまず `pinshare-input.dll.new` に書き、書き切れたときだけ入れ替える (既存を `.old-<universal-time>` (使用中の名前なら `-1` などを付ける) にリネーム → `.new` を正式名にリネーム → 退避コピーは誰も掴んでいなければ消す)。入れ替え前の失敗では動いている DLL に触れず、最後のリネームに失敗したら退避コピーを戻す。ログには本当の例外 (型とメッセージ、例: アクセス拒否) を残す。取り残された `.new` は次回の更新で消す。戻すのにも失敗したら退避コピーが唯一の動く DLL なので、正式名の DLL が無い間は退避コピーを掃除しない
- どの失敗もログに書くだけ。DLL が無くてもアドオンは動き、割り当てたキーがゲームにも届くようになるだけ
- 新しい DLL が効くのは次にゲームを起動してから

### 8.4 セッション `pinshare-run-session` (`pinshare-win32.lisp:454`)
- セッション ID: `make-random-state t` を毎回作って 8 桁の 16 進にする。**配布イメージは起動ごとに同じ乱数状態から始まる**ので、毎回作り直さないと同じ ID になる。アドオンは新しいセッションを見たときだけ名前を送り直す
- **旧中継との競合チェック**: in.txt に自分のものでない (`*pinshare-sessions*` に無い) セッションがあり、`time` が 3 秒以内なら `(:conflict)` にして 2 秒ごとに見直す (旧 PowerShell 版の中継が動いていると両方のファイルを奪い合うため)
- 開始時の out.txt は**古い**ものとして扱う (`pinshare-relay-skip-backlog`): 名前と色の命令だけを取り込み、seq のカーソルは末尾まで進める。`addon-seen` は nil のまま

### 8.5 中継ループ `pinshare-relay-loop` (`pinshare-win32.lisp:312`)
- ティックは 50ms (`+pinshare-tick-seconds+`)。**ネットワークで止まらない**: 接続と受信は別スレッド (`pinshare-start-connection`) で行い、mailbox で知らせてもらう。受け取るのは `(:open socket)` / `(:failed text)` / `(:message text)`* / `(:closed)`
- 再接続: 失敗するたびに 1→2→4…最大 30 秒まで待つ。30 秒以上つながっていた接続が切れたときだけ 1 秒後に再接続する。すぐ切れる接続は失敗と同じに扱う (落ち続けているサーバーを総出で叩かないため)
- `:open` → 一覧を空にして `connected-status` にし、hello 一式を送る
- `:message` → `state` なら一覧を置き換える。`error` なら GUI の状態を `(:error "server: ...")` にする (in.txt は connected のまま)
- `:closed` → 一覧を空にして `"error" "disconnected from the server"`
- 毎ティック:
  - `*pinshare-channel-items*` = 接続中なら (pins . arrows)。GUI の保存ボタンが使う
  - ピンセットが変わったら dirty にする。local-only なら `(:local-only name)`
  - out.txt を読んで `pinshare-relay-consume` を通し、接続中なら送る
  - 接続中で、アドオンから初めて命令が来たら `:connected-no-addon` を `:connected` にする。C# はアドオンが古ければ (上の `version` の表) `AddonOutdated` にし、Reload で版が届けば戻す。local-only でも同じ判定をする
  - dirty か、前回書いてから 1 秒経っていたら in.txt を書く
- 書き込みは `in.txt.tmp` に書いてから `MoveFileEx(REPLACE_EXISTING)` で置き換える。失敗したら dirty のままにして次のティックでやり直す
- 終わるとき: 保存用の一覧を nil にし、接続を取り消し、**in.txt をピン無し・ピンセット無しで書き直す** (アドオンは数秒間まだ新しい in.txt と見なして描き続けるので、前のクエストのピンセットが残って見えないようにする)
- セッションを続けるかどうか (`pinshare-session-current-p`): `pinshare-wanted` が前と同じ値のときだけ続ける。設定、合言葉、ゲーム、ピンセットの有無が変わったら作り直す

### 8.6 ファイル形式 (アドオンとの約束事。テストで文字列をそのまま固定している)

**out.txt** (アドオン → クライアント)。1 行に 1 命令: `<seq>\t<命令>\t<引数...>\n`
- seq は増える一方 (アドオンは `os.time()*1000` から始める)。クライアントが全部読み終えていれば (ack が最後に書いた seq 以上なら)、アドオンは次に書くときにファイルを空にしてから書く
- **改行で終わっている行だけ**を読む (書きかけの行は次に回す)。CRLF も読める。seq が整数でない行と、命令の無い行は捨てる
- 数値は手書きのパーサで読む (`parse-pinshare-number`)。符号、小数、指数に対応し、nan、inf、空、後ろに余計な文字がある値は拒否する。桁数は 30 桁まで、指数は ±30 まで。Lisp のリーダーには決して通さない
- 読み込み: UTF-8 として読めなければ 1 バイト 1 文字として読む。ファイルが無いかロックされていれば空文字

| 命令 | 引数 | 送る JSON |
|---|---|---|
| `version` | 整数 (init.lua の `ADDON_VERSION`) | 送らない (C#, S21)。中継のセッションごとに1回、`name` より先にアドオンが書く。C# はゲームの隣にインストール済みの init.lua の版 (Reload で読み込まれるもの。開発者のリンク先で版が無ければ 0 = 判定しない) と比べ、このセッションで `name` が届いたのに版が無いか古ければ `AddonOutdated` (「アドオンが古い版のまま。ゲームのアドオンメニューから Reload」) を出す。バックログの name/version は判定に使わない。アドオン側は版を送れたセッションにだけ name を送る。同梱版 `PinShareRelay.BundledAddonVersion` はテストで init.lua と照合し、init.lua の中身のハッシュも版ごとに固定する (版の上げ忘れを検出)。Lisp の中継は未知の命令として捨てるので互換 |
| `name` | 名前 | 接続中なら hello 一式を送り直す (サーバーは色を名前ごとに持つため) |
| `color` | RRGGBB | `{"t":"color","color":v}` |
| `arrow_color` | RRGGBB か空 (空はピンと同じ色) | `{"t":"arrow_color","color":v}` |
| `add` | floor x y z ttl [label [max [room]]] | `{"t":"add","floor","ttl","label","x","y","z"[,"max"][,"room"]}` |
| `move` | id x y z | `{"t":"move","id","x","y","z"}` |
| `arrow_add` | floor x1 y1 z1 x2 y2 z2 ttl max [xm ym zm [room]] | `{"t":"arrow_add","floor","ttl","max","x1".."z2"[,"xm","ym","zm"][,"room"]}` |
| `arrow_move` | id x1..z2 [xm ym zm] | `{"t":"arrow_move","id",...}` |
| `remove` / `arrow_remove` | id | `{"t":<命令>,"id"}` |
| `clear_mine` / `clear_all` | なし | `{"t":<命令>}` |

- 未接続の間: name、color、arrow_color は中継の状態に取り込むだけにする (次の hello で送る)。**それ以外は捨てて後から送らない** (数分前に置いたピンが再接続後に現れないように)
- **負の id** への move / remove / arrow_move / arrow_remove はピンセットの項目なのでサーバーに送らない (`pinshare-local-item-command-p`)
- 知らない命令や形の崩れた命令は無視する
- 文字列は `pinshare-clean` で制御文字 (<32 と 127) を除く

hello 一式 (`pinshare-hello-messages`): `{"t":"hello","channel","name"}` → 色があれば `{"t":"color"}` → 矢印の色を受け取っていれば `{"t":"arrow_color"}`。

**サーバー → クライアント**
- `{"t":"state","pins":[...],"arrows":[...],"members":["name",...]}`: 丸ごと置き換える。キーが無ければ空配列 (矢印に対応していない古いサーバー)
- `{"t":"error","message":...}`: ログと GUI に出す。状態は変えない
- JSON として読めないものは無視する

pin の項目: `id, owner, floor, x, y, z, label, no, ownerNo, room, roomNo, color, remaining` (null もありうる)
arrow の項目: `id, owner, floor, room, x1..z2, xm, ym, zm, color, remaining`

**in.txt** (クライアント → アドオン)。`render-pinshare-inbox` (`pinshare.lisp:382`)。タブ区切りで、行の順番は固定:
```
session\t<8桁16進>
time\t<unix 秒>
ack\t<処理済みの最大 seq>
status\t<connecting|connected|error|local>\t<message>
alert\t<code>\t<英文>[\t<arg>]  (C#, S47。注意があるときだけ、1 件 1 行)
channel\t<合言葉>
member\t<名前>                 (人数ぶん)
pin\t<id>\t<owner>\t<floor>\t<x>\t<y>\t<z>\t<remaining>\t<label>\t<no>\t<ownerNo>\t<color>\t<roomNo>\t<room>[\t<locked>]
arrow\t<id>\t<owner>\t<floor>\t<x1>\t<y1>\t<z1>\t<x2>\t<y2>\t<z2>\t<remaining>\t<color>[\t<xm>\t<ym>\t<zm>\t<room>[\t<locked>]]
pinset\t<セット名>              (ピンセットを表示しているときだけ)
end
```
- 数値は Lua の `tonumber` で読める形にする: 整数はそのまま、小数は `e` 指数まで。**`d0` のような Lisp の浮動小数点表記を出してはいけない** (`pinshare-format-number`)
- 省略可能な値は、null やキーが無ければ空欄にする。id、floor、座標のどれかが数値でない項目は行ごと出さない
- 矢印の後ろの 4 列 (xm..room) は、xm が数値のときだけ出す (アドオンは列の位置で読むため)
- 最後は必ず `\nend\n` で終える (アドオンの `readInbox` は `"\nend\r?\n?$"` で終わっているかを見て、書きかけを弾く)
- アドオンは `time` が 5 秒より古いと「中継が動いていない」と見なし、何も描かない。だから**変化がなくても 1 秒ごとに書き直す**
- status の文字列 `connecting` / `connected` / `error` / `local` はアドオンが文字列のまま比べる
- `alert` 行 (C#, S47): クライアントから見たアドオンの状態を、ゲーム内の Pin Share 設定ウィンドウに赤字で出させる (新しく当てはまった注意は、設定ウィンドウを閉じていても画面上部に 8 秒、普段の一時表示とは別の枠で出す。同じ注意 (code・arg・英文が同じ) はアドオンの読み込みごとに一度だけで、中継の再起動で in.txt から一瞬消えて戻っても出し直さない。arg が変われば (さらに新しい版が入ったなど) もう一度出す。中継が動いていないときと Enable を切っているときは出さない)。出す注意が無いときは行ごと出さない (アドオンがこのセッションに name を送る前と、版の分からないインストールでは in.txt はバイト単位で Lisp の中継と同じ。name を送った後は、古くないアドオンにも `addon_outdated` 行が付く)。複数あれば 1 件 1 行で、アドオンは全部を読む。`code` は決まった語で、アドオンは 2 列目の英文をそのまま出す (知っている code でも言い換えない。英文が空なら出さない)。知っている code には表示の条件を足せる。知らない code は無条件で出すので、クライアントだけで足せるのは誰に出してもよい無条件の注意に限る (条件付きの code を足すときはアドオンの版も上げる)。各列は `pinshare-clean` を通す。今ある code は `addon_outdated` だけで、条件付きの注意として書く: このセッションにアドオンが name を送っていて、インストール済みの版が分かる (0 でない) あいだは常に出し、`arg` にその版を入れる。アドオンは自分の `ADDON_VERSION` がそれより小さいときだけ表示し、`arg` が無ければ表示しない。in.txt は同じゲームフォルダーの全ウィンドウで共有され、クライアントの `AddonOutdated` (上の `version` の表) は最後に name を送ったアドオンの判定でしかないため、判定はウィンドウごとにアドオン側で行う。判定の材料は out.txt の処理でしか変わらず、そこで dirty になるので次の書き込みで届く。`readInbox` は行の種類の if/elseif に else が無いので、版 1 のアドオン (と Lisp 版) は `alert` 行を読み飛ばす。つまり表示できるのは版 2 以降のアドオンで、ゲームが版 1 を読み込んだままのときは今までどおりクライアントの設定画面の `AddonOutdated` だけが知らせる。アドオンの `ADDON_VERSION` は 2 (`BundledAddonVersion` と揃える)

### 8.7 ピンセット
- 取得: クエストを読み込んだ瞬間 (`pinshare-set-fetch-wanted`, `pinshare.lisp:663`)。ゴーストと同じく、クエストのポインタが変わったら 1 回だけ取る
  - スナップショットが nil (ワープ中の読み取り失敗) なら何も変えない (ワープのたびに消して取り直さないように)
  - クエストが無くなったらセット、slug、ポインタを忘れる
  - 取得する条件: slug が 1 つ以上ある、`:pinshare-enabled`、`*pinshare-allowed-p*`、**連携済みのトークンがある** (匿名ゲストでは選べない)
  - `GET /api/quests/<先頭の slug>/pins?slugs=<残り,...>` (Bearer: 連携済みトークン)。200 で `items` があればそれを使う。404 や失敗なら nil
  - 取得中に次のクエストへ移っていたら、結果は捨てる (ポインタが一致したときだけ採用する)
- 描き方 (`build-pinshare-local-items`): id は -1, -2... の負数。owner はセット名の先頭 20 文字、`floor` には `area` を入れる、`remaining -1` (期限なし)、`locked 1`、`ownerNo` は 1 から順に振る (チャンネルの通し番号や部屋番号とは重ならない)。作った結果はセットのオブジェクトに結び付けてキャッシュする (in.txt は毎秒書くので)
- 保存: `POST /api/pin-sets` 本体 `{"quest":slug,"items":{"pins":[...],"arrows":[...]}[,"name"]}`。矢印に曲げの中点が無ければ両端の中点を補う (`pinshare-arrow-with-mid`)。サーバーは owner、id、番号を捨てる。上書きは `POST /api/pin-sets/<id>/items`。201 → created、200 → updated、400 か 403 → rejected、404 → not-found、401 → 例外

### 8.8 限定提供 (`/api/me` の features)
- `pinshare-feature-p` (`pinshare.lisp:509`): `user.features` が配列で `"pinshare"` を含んでいれば真。キーが無い (古いサーバー) か未連携なら偽
- `set-pinshare-permission` (`pinshare.lisp:497`): 値が変わったときだけフラグと config `:pinshare-allowed` を保存する。**窓には触らない**
- 判定が変わるきっかけ: `check-token` (成功したとき、401、トークンが空のとき)、`pinshare-permission-loop` (`pinshare-win32.lisp:539`、1800 秒ごと)
  - 再確認のループは通信エラーなら今の判定を保つ (サイトに繋がらないだけで中継を止めない)
  - 返答が届くまでにトークンが変わっていたら、その返答は捨てる
  - モデレーターの判定には触らない
- サーバー側の設定: 環境変数 `ETA_PINSHARE_USERS` (限定提供中、2026-09 時点で `tpot_pso,dustbox`)

### 8.9 WebSocket
- `websocket-win32.lisp`: WinHTTP の WebSocket API を同期モードで使う (TLS は OS 任せ)
- 受信を 1 本と送信を 1 本、別々のスレッドから同時に呼べる。close は、ブロックしている受信を `:closed` で返させる
- ping への応答と keep-alive (30 秒) は WinHTTP が行う。受信のタイムアウトは無限
- 既定のポートは ws が 80、wss が 443 (テストで確認している)
- C# では `ClientWebSocket` を使い、KeepAliveInterval を 30 秒にし、受信ループは専用の Task で回す

### 8.10 アドオン (Lua, `data/pin-share/init.lua`)
クライアントからは配って更新するだけ。書き直しの対象ではないが、約束事を守るために把握しておく。
- Solybum のアドオン形式 (`__addon.init` → `{name="Pin Share", version="1.0.0", present, key_pressed, toggleable=true}`)。メインメニューにボタン "Pin Share" を足し、設定ウィンドウを開閉する
- 設定は `addons/Pin Share/options.lua`。クライアントは上書きしない。キーは `enable, cursorKey(F6=117), feetKey(F7), clearKey(F8), mouseButton(1=右), arrowKey(Shift=16), deleteKey(16), ttl(60), maxPins(3), label, showDistance, clampToEdge, hideWhenMenu, maxCursorDist(3000), fontScale, orderMode, orderMax, moveOthers, pinFilter, filterKey, colorMode, customColor, arrowColorMode, arrowCustomColor, frontKey, frontDist, padFeet/Mod, padClear/Mod, padFilter/Mod, padFront/Mod`
- `present` は毎フレーム呼ばれ、次を行う: コントローラーの割り当て待ちの処理 → `syncInputBindings` (DLL に割り当てを渡す。前回と同じなら渡さない) → `pollInput` (DLL の poll。DLL が隠す処理を続けるための合図も兼ねる) → 200ms ごとに `readInbox`、`syncName`、`syncColor` → 描画とマウス操作
- ピンを置けるかどうか (`canPlacePin`): 中継が動いている (session があり、`time` が 5 秒以内) かつ status が `connected`。満たさなければ画面に "Pin Share: Rappy Runs Client is not relaying" / "not connected to server" を出す
- ドラッグで動かしたピンや消したピンは、サーバーから反映が届くまで 3 秒待つ。届かなければ元に戻して失敗を知らせる
- 設定ウィンドウに中継の状態を出す: "Relay: not running" (クライアントを起動して Settings で Pin Share をオンにするよう促す) / "Server: connected" / "Server: not connected (no passphrase) - pin set only" / "Server: <error>" / "Server: connecting..."。Channel、Members、Pin set も出す

### 8.11 入力優先 DLL (`native/pinshare-input/pinshare-input.cpp`)
目的: アドオンに割り当てたキーとパッドのボタンを**ゲームから隠す** (例えば F6 を押したときにゲームの機能まで動かないように)。割り当てていないキーには触れない。
- ビルド: `build.cmd`。VS2022 の x86 用ツール、`/MT /O2 /LD`。PsoBB.exe は 32bit。`package.ps1` が `data\pin-share\pinshare-input.dll` を作り、ビルドに失敗したらリリースも止める
- 読み込み: アドオンが `ffi.load("addons\\Pin Share\\pinshare-input.dll")` で読む。`pinshare_input_version() ~= 1` なら使わない
- DllMain: `GET_MODULE_HANDLE_EX_FLAG_PIN` で自分をアンロードできなくし (書き換えたコードが DLL を指しているため)、初期化スレッドを立てる。ログは `pinshare-input.log` に最大 200 行
- **キーボード (vtable の差し替え)**: 読み込まれている `dinput8.dll` (アドオンプラグインのラッパー) の中から、MSVC の RTTI 名 `.?AVImguiDInputDevice@@` を手がかりに TypeDescriptor → CompleteObjectLocator → vtable と辿る。30 スロットがすべてそのモジュールの中を指しているかで確かめる (GetDeviceState のスロットだけは、ほかのツールが先に差し替えていてもよい)。見つけたら slot 9 (`GetDeviceState`) を `HookGetState` に置き換える。元の関数を呼んだ後、`cb == 256` (キーボード) でアドオンが生きていれば、隠すキーの DIK を 0 にする。プラグインとアドオンは元のキー入力を見られる。見つかるまで 200ms 間隔で 50 回試す
- **パッド (XInputGetState のフック)**: `xinput1_4.dll` の `XInputGetState` と序数 100 (`XInputGetStateEx`)、読み込み済みなら `xinput1_3` と `xinput9_1_0` も、ホットパッチ点 (`mov edi,edi` と直前の 5 バイトの隙間) に `jmp` を書く。ほかのツールが先にフックしていれば、その先へつなぐ。`FilterPad` の処理:
  - トリガーは疑似ボタン (LT=0x10000, RT=0x20000) として扱い、64 で押下、32 で解放というヒステリシスを付ける
  - 組み合わせ (モディファイア + ボタン) を先に、単独を後に判定する
  - 単独でも割り当てられていて、同時に別の組み合わせのモディファイアでもあるボタンは、離したときに判定する
  - 押された瞬間から離すまで隠す
  - 発火したものは `g_fired` のビットに積む
- **安全策**: アドオンからの呼び出し (set_keys / set_pad / poll) が 2 秒途絶えたら、すべての隠しを止める (アドオンが止まった、エラーになった、再読み込み中)。止まる前の押下は poll で捨てる
- 公開関数 (cdecl): `pinshare_input_version()`→1、`pinshare_input_status()` (bit0 キーボードをフック済み、bit1 パッドをフック済み、bit2 試行を終えた)、`pinshare_input_set_keys(const int* vks, int n)` (VK→DIK の変換。Ctrl、Alt、Shift、Enter は左右両方。ナビゲーションキーは表で決め打ち)、`pinshare_input_set_pad(const unsigned* mains, const unsigned* mods, int n)` (最大 16)、`pinshare_input_poll()` (前回から発火した割り当てのビット)、`pinshare_input_pad_raw()` (隠す前のボタンの状態。割り当て画面用)
- アドオンが隠すキーは `cursorKey, feetKey, clearKey, filterKey, frontKey` だけ。押しながら使う `arrowKey` と `deleteKey` (Shift など) は隠さない
- C# 移植の際は、**この DLL と init.lua はそのまま同梱する** (クライアントの言語とは関係ない)。移植で要るのは、配置と更新の処理 (§8.3) だけ

---

## 9. 設定の保存先一覧

| 保存先 | 内容 |
|---|---|
| `%APPDATA%\ephinea-ta-client\config.sexp` | 設定の plist (§1.4 の各キー、`:api-token`、`:anon-token`、`:moderator`、`:pinshare-allowed`、`:overlay-position` ほか)。**変更のたびにファイル全体を書き直す** |
| `%APPDATA%\ephinea-ta-client\queue.sexp` | Runs 一覧 (送信済みの項目からは telemetry を除く) |
| `%APPDATA%\ephinea-ta-client\trigger-log.txt` | トリガーログ |
| `HKCU\...\Run\RappyRunsClient` | 自動起動 |
| `<exe のフォルダ>\login.txt` | ファイルでのログイン (ユーザーが置く) |
| `<exe のフォルダ>\data\pin-share\{init.lua,pinshare-input.dll}` | 配布するアドオン |
| `<exe のフォルダ>\data\quest-triggers.sexp` | 組み込みのクエスト定義 |
| `<PSOBB>\addons\Pin Share\{init.lua,pinshare-input.dll,options.lua,exchange\{out.txt,in.txt,in.txt.tmp}}` | ゲーム側 |
| `%TEMP%\RappyRunsClient-update.zip`, `rappyruns-update.ps1`, `rappyruns-update-stage\`, `rappyruns-update.log` | アップデート |
| 録画フォルダ (`:record-dir`、既定 `~\Videos\RappyRuns\`) | 録画 |

> C# 版で設定ファイルの形式を変えるなら、初回起動時に config.sexp と queue.sexp を読み込む移行処理が要る。sexp のキーワード plist を読むだけの小さなパーサで足りる。

---

## 10. 外部 API (UI シェルから呼ぶもの)

| メソッドとパス | 用途 | 認証 |
|---|---|---|
| `GET /api/quests` | サーバー確認、ルールフォームの親クエスト | なし |
| `POST /api/quests` | ルール登録 | モデレーターのトークン |
| `POST /api/pair` / `GET /api/pair/:code` | ペアリング | なし |
| `POST /api/login` | login.txt でのログイン | なし |
| `GET /api/me` | トークン確認。`username`, `role`, `auto_publish` (0/1), `features[]` | Bearer |
| `POST /api/me/auto-publish` | `{"enabled":0|1}` | Bearer |
| `POST /api/merge-anonymous` | `{"anonymous_token"}` | Bearer (連携済み) |
| `GET /api/quests/:slug/pins?slugs=` | ピンセットの取得 | Bearer (連携済み) |
| `POST /api/pin-sets`, `POST /api/pin-sets/:id/items` | ピンセットの保存 | Bearer (連携済み) |
| `GET https://api.github.com/repos/<repo>/releases/latest` | アップデートの確認 | `Accept: application/vnd.github+json` |
| ブラウザで開く URL | `/my/runs`、`/pair?code=`、ランの `:url`、`https://www.youtube.com/upload`、GitHub のリリースページ | - |

---

## 11. テスト (`tests/tests-pinshare.lisp`、SBCL で実行)

アドオンとの約束事 (ファイル形式) と、サーバーとの JSON を文字列のまま固定している。C# 版でも同じ入力と期待値でテストを作ること。

- out.txt の読み取り: 完全な行 / 書きかけの行は次に回す / 改行が 1 つも無い / CRLF と壊れた行 / `%.3f` の座標をそのまま読める / nan、inf、空、後ろの余計な文字を拒否 / 整数は小数部が 0 のものだけ
- 命令の変換: add (すべての引数 / 古いアドオンの短い add / 座標が壊れた add は捨てる)、arrow_add (曲げと部屋あり)、arrow_move (曲げなし)、remove と clear 系、知らない命令は無視
- 状態: 開始時の未処理分は名前と色だけ取り込み、seq を飛ばす / その未処理分はアドオンが生きている証拠にしない / hello は channel と名前、次に色 / 処理済みの seq は繰り返さない / 未接続の間の命令は捨てる / 矢印の色が空でも送る / 名前が変わったら hello と色を送り直す
- in.txt: 出力の文字列を丸ごと比べる (上の例を参照) / 最後が `\nend\n` / サーバーのエラーは状態を変えない / JSON でないものは無視 / 切断で一覧を空にする / 数値に Lisp の浮動小数点表記が出ない
- 競合: 新しい別セッションは競合 / 自分の以前のセッションは競合しない / 古い heartbeat と空の in.txt も競合しない
- 設定: 合言葉を整える / サーバー URL が空なら既定値 / アドオンのフォルダの位置 / ws と wss の既定ポート
- ピンセット: locked、負の id、セット名が owner / 持ち主ごとの番号だけ振る / キャッシュは 1 セットにつき 1 回作る / 矢印は曲げ、部屋、locked の順 / `pinset` 行は `end` の前 / セットが無ければ出さない / 長い名前は切る / 負の id の操作はサーバーに送らない / 保存の本体 / 曲げの無い古いサーバーの矢印には中点を補う / 読み取り失敗ではセットを保つ / クエストが無くなれば忘れる
- 限定提供: 古い /api/me (features 無し) と未連携はどちらも「使えない」
- i18n: 中継の状態すべてに英日両方の文言がある
- ほかのファイル (`tests-helpers.lisp`): `detected-parent`、`rule-trigger-label`、`moderator-role-p`、`standing-toast` の各分岐、`startup-update-decision`、`updater-script-text`

---

## 12. 過去の不具合修正から見た回帰リスク (コメントと履歴から)

| # | 内容 | 出典 |
|---|---|---|
| R1 | 4Hz のティックで、変わっていないペインまで描き直すとちらつく。**値が変わったときだけ**画面に送る。比べるのは画面から読み戻した値ではなく、渡した値にすること | `set-pane-text` `gui.lisp:1348-1373`, 047b117, PR #293 |
| R2 | Runs と Rooms の一覧を定期的に作り直すと選択とスクロールが消える。イベントが起きたときか、中身の変化を見てから更新する | `refresh-runs-list`, `rooms-list-signature` |
| R3 | 判定の変化 (モデレーター / Pin Share) のたびに窓を作り直すと、トレイ常駐中に窓が出てしまい、ダイアログの親も消える。トレイに隠れている状態を保ち、Pin Share は該当部分だけを出し入れする | `sync-pinshare-group`, `rebuild-interface`, cebc8ce, 0854241 |
| R4 | 多重起動: 自動起動と手動起動が重なると、トレイ常駐のコピーが際限なく増える。名前付きミューテックスで防ぎ、既存のインスタンスの窓を出す | 5872e4c |
| R5 | Quit が終わらない: トレイスレッドから普通の終了処理を呼ぶと、プロセスが残る。録画を止めてから、トレイアイコンを消して、プロセスを強制終了する | `quit-app` |
| R6 | 日本語のトレイメニューが出ない (文字列変換の例外を握りつぶしていた)。exe のパスが日本語のときのアイコン取得も同じ | 560918f, `tray-win32.lisp:96-127` |
| R7 | アップデートの ps1 が BOM 無しだと、日本語や OneDrive のパスで入れ替えに失敗する。クエスト中や録画中は exe を入れ替えない | `updater.lisp:307-311`, `run-update-download` |
| R8 | Pin Share の in.txt: 1 秒ごとの heartbeat、tmp に書いてから置き換える、`\nend\n` で終える、Lisp の浮動小数点表記を出さない、列の位置を守る。どれかが崩れるとアドオンは何も描かない | `pinshare.lisp`, テスト |
| R9 | Pin Share のセッション ID を毎回同じにすると、アドオンが名前を送り直さず、名前の無いピンになる。起動のたびに安全な乱数で作ること | `pinshare-run-session` |
| R10 | 未接続の間に溜まった命令や、起動時に残っていた out.txt を後から送ると、古いピンが現れる。捨てること | `pinshare-relay-consume`, `skip-backlog` |
| R11 | アドオンの配置: 開発者のジャンクションを上書きしない、リンク先の消えたリンクを消さない、options.lua に触れない、ロード中の DLL はリネームしてから置く | `pinshare-ensure-addon`, `pinshare-install-input-dll` |
| R12 | セッションを抜けるときに in.txt を空にしないと、前のクエストのピンセットが数秒残って見える | `pinshare-relay-loop` の unwind |
| R13 | ワープ中の読み取り失敗 (nil) を「クエスト無し」と取り違えると、ピンセットやゴーストをワープのたびに取り直してしまう | `pinshare-set-fetch-wanted` |
| R14 | 再接続: 落ち続けているサーバーを全クライアントが毎秒叩かないよう、30 秒つながった接続でなければ待ち時間を倍にしていく | `pinshare-relay-loop` の `:closed` |
| R15 | 限定提供の再確認: 通信エラーで判定を消さない。返答が届く前にトークンが変わっていたら捨てる | `pinshare-permission-loop` |
| R16 | 署名を確かめていないゲームには接続せず、Pin Share のファイルも書かない | `poll-search-step` |
| R17 | 自動起動のチェックボックスは、書き込んだ後にレジストリを読み直す。exe を移したら「無効」と表示する | `toggle-autostart-callback`, `autostart-enabled-p` |
| R18 | 自動公開の切り替えは、オンにするときだけ確認し、失敗したらサーバーの値に戻す | `toggle-auto-publish-callback` |
| R19 | ペアリングやアップデートの完了時は、**その時点の**窓に書く (待っている間に窓が作り直されているかもしれない) | `finish-pairing`, `apply-update-restart` |
| R20 | 未連携は正規の状態。赤で出さず、ブラウザも勝手に開かない。login.txt だけは自動でログインしてよい | `check-token`, `prompt-for-token-setup` |
| R21 | 経過時間を 1 回だけ整形して、ペインとタイトルで使い回す (秒の境目でずれないように) | `update-game-status` |
| R22 | 録画開始の失敗通知は連続失敗ごとに 1 回だけ。順位の通知は良い知らせのときだけ (周回のたびに鳴らさない) | `recording.lisp:990-1015`, `standing-toast` |
| R23 | Explorer の `/select,` はバックスラッシュのパスを引用符で囲んで渡す | `open-file-in-explorer` |
| R24 | 同梱ファイルの場所は exe の実際のパスから求める (argv[0] は相対パスのことがある) | `pinshare-bundled-file`, `credentials-directory` |

---

## 付録 A. i18n キー (対象ファイルで使っているもの)

列: キー / 使っているファイル / English / 日本語。`~a` や `~d` などは Lisp の format 指示子 (C# では `{0}` などに置き換える)。`~%` は改行。`~@[...~]` と `~:[...~;...~]` は条件付きの表示。
※ キー名を機械的に拾ったので、使っていないキーが少し混じっている可能性がある。

| キー | ファイル | English | 日本語 |
|---|---|---|---|
| `api-token-label` | gui | API token | API トークン |
| `auto-publish-confirm` | gui | Publish every upload automatically?~%~%New recordings will appear on the public leaderboard the moment they finish uploading, without you watching them first. Anything the recording caught goes public with it. | アップロードを自動で公開しますか?~%~%今後の録画はアップロード完了と同時に、内容を確認しないまま公開リーダーボードに掲載されます。録画に映り込んだものもそのまま公開されます。 |
| `auto-publish-failed` | gui | Could not update the auto-publish setting on the server: ~a | 自動公開設定をサーバーに反映できませんでした: ~a |
| `auto-publish-label` | gui | Publish uploads automatically (skip the manual check on the site) | アップロードを自動で公開する (サイトでの手動確認をスキップ) |
| `auto-update-label` | gui | Update automatically at startup | 起動時に自動でアップデートする |
| `autostart-label` | gui | Start with Windows | Windows 起動時に自動で起動する |
| `change-folder-button` | gui | Change folder... | フォルダを変更... |
| `check-updates-button` | gui | Check for updates now | 今すぐアップデートを確認 |
| `choose-record-dir` | gui | Choose the recordings folder | 録画フォルダを選択 |
| `clear-list-confirm` | gui | Clear the list?~%~%Runs not submitted yet are kept. Recordings stay on disk and drafts stay on the server (videos can still be added on the site), but this client forgets which recording belongs to which draft.~%~%Nothing is deleted on the server. | 一覧をクリアしますか?~%~%未送信の記録は残ります。録画ファイルとサーバー上の下書きもそのまま残ります (動画の追加はサイトからできます) が、どの録画がどの下書きのものかの対応はこのクライアントから消えます。~%~%サーバー上のデータは削除されません。 |
| `clear-list-tooltip` | gui | Clear list | 一覧をクリア |
| `close-to-tray-label` | gui | Minimize to the tray on close instead of quitting | 閉じたときに終了せずトレイに最小化する |
| `col-area` | gui | Area | エリア |
| `col-condition` | gui | Condition | 条件 |
| `col-party` | gui | Party | 人数 |
| `col-quest` | gui | Quest | クエスト |
| `col-status` | gui | Status | 状態 |
| `col-time` | gui | Time | タイム |
| `col-trigger` | gui | Trigger | トリガー |
| `col-video` | gui | Video | 動画 |
| `corner-bottom-center` | gui | Bottom center | 下中央 |
| `corner-bottom-left` | gui | Bottom left | 左下 |
| `corner-bottom-right` | gui | Bottom right | 右下 |
| `corner-custom` | gui | Custom (Ctrl+drag the overlay) | カスタム (Ctrl+ドラッグで移動) |
| `corner-middle-left` | gui | Middle left | 左中央 |
| `corner-middle-right` | gui | Middle right | 右中央 |
| `corner-top-center` | gui | Top center | 上中央 |
| `corner-top-left` | gui | Top left | 左上 |
| `corner-top-right` | gui | Top right | 右上 |
| `file-login-bad-file` | gui | Token: login.txt is unreadable (needs username= and password= lines, UTF-8) | トークン: login.txt を読み取れません (username= と password= の行が必要・UTF-8) |
| `file-login-checking` | gui | Token: logging in with login.txt... | トークン: login.txt でログイン中... |
| `file-login-failed` | gui | Token: login.txt login failed (~a) | トークン: login.txt ログイン失敗 (~a) |
| `file-login-invalid` | gui | Token: login.txt username or password rejected | トークン: login.txt のユーザー名またはパスワードが正しくありません |
| `game-attached` | gui | Game: attached | ゲーム: 接続済み |
| `game-read-failed` | gui | Game: attached but cannot read memory - try running the client as administrator | ゲーム: 接続済みですがメモリを読み取れません — クライアントを管理者として実行してみてください |
| `game-searching` | gui | Game: searching... | ゲーム: 探索中... |
| `game-signature-refused` | gui | Game: not the official Ephinea client (~a) - recording refused | ゲーム: Ephinea 公式クライアントではないため記録しません (~a) |
| `game-status-with-error` | gui | ~a / recording error: ~a | ~a / 録画エラー: ~a |
| `ghost-marker-label` | gui | Show the ghost in the game view (a marker where the ghost is running) | ゴーストの姿をゲーム画面内に表示 (ゴーストの現在位置にマーカー) |
| `ghost-note` | store | vs ghost ~a | ゴースト比 ~a |
| `ghost-overlay-label` | gui | Floating in-game overlay (timer, gap and room splits) | ゲーム画面にオーバーレイ表示 (タイマー・タイム差・部屋別タイム) |
| `ghost-race-label` | gui | Race a ghost: fetch a reference run at quest load and show the live gap | ゴーストと競う: クエスト開始時に基準記録を取得してタイム差を表示 |
| `ghost-toast-text` | store | ~a - ~a, ~a ahead of the ghost | ~a - ~a、ゴーストより ~a 速い |
| `ghost-toast-title` | store | Ghost beaten! | ゴーストに勝利! |
| `group-advanced` | gui | Advanced | 上級者向け |
| `group-connection` | gui | Connection | 接続 |
| `group-ghost` | gui | Ghost race | ゴーストレース |
| `group-language` | gui | Language | 言語 |
| `group-pinshare` | gui | Pin Share | Pin Share (ピン共有) |
| `group-recording` | gui | Recording | 録画 |
| `group-tray` | gui | Tray | 常駐 |
| `group-updates` | gui | Updates | アップデート |
| `hint-address` | api | server address not found - check the Server URL | サーバーアドレスが見つかりません - サーバー URL を確認してください |
| `hint-connect` | api | could not connect - server down, or no internet? | 接続できませんでした - サーバー停止中またはインターネット未接続? |
| `hint-timeout` | api | connection timed out | 接続がタイムアウトしました |
| `hint-tls` | api | secure connection (https) failed | セキュア接続 (https) に失敗しました |
| `link-account-button` | gui | Link with the site (approve in browser) | サイトと連携する (ブラウザで承認) |
| `my-runs-button` | gui | Open My Runs (add videos) | My Runs を開く (動画の追加) |
| `no-active-quest` | gui | No active quest | 実行中のクエストなし |
| `no-recording-for-run` | gui | This run has no saved recording.~%~%Videos are only saved when recording is enabled while the quest is played. | この記録には保存された録画がありません。~%~%動画は、クエストのプレイ中に録画が有効だった場合にのみ保存されます。 |
| `no-recordings-yet` | gui | No saved recordings to upload yet.~%~%Videos are saved automatically when a recorded quest completes. | アップロードできる録画はまだありません。~%~%録画中のクエストが完了すると、動画は自動で保存されます。 |
| `notify-capture-blocked-text` | recording | Windows blocked ffmpeg.exe (app control policy). Allow it in Windows Security; recording resumes from the next quest. | Windows のアプリ制御ポリシーが ffmpeg.exe をブロックしました。Windows セキュリティで許可すると、次のクエストから録画が再開されます。 |
| `notify-capture-failed-text` | recording | ffmpeg could not be started, so this quest is not being recorded. The next quest retries automatically. | ffmpeg を起動できなかったため、このクエストは録画されていません。次のクエストで自動的に再試行します。 |
| `notify-capture-failed-title` | recording | Recording did not start | 録画を開始できませんでした |
| `notify-overlap-text` | recording | The game window cannot be captured directly on this PC, so the screen area under it is recorded instead. Windows overlapping the game (Discord, a browser...) will show up in the video - keep them off the game window while a quest is recording. | このPCではゲームウィンドウを直接キャプチャできないため、ゲームのある画面領域を録画しています。ゲームに重なったウィンドウ(Discord・ブラウザなど)は動画に写り込むため、クエスト録画中はゲームの上に重ねないでください。 |
| `notify-overlap-title` | recording | Other windows may appear in the recording | 他のウィンドウが録画に写る可能性があります |
| `notify-software-encode-text` | recording | The hardware encoder check could not run at startup, so recording uses software encoding - the game may feel slower. It switches back automatically once ffmpeg can start. | 起動時にハードウェアエンコーダを確認できなかったため、ソフトウェアエンコードで録画しています。ゲームが重く感じられる場合があります。ffmpeg が起動できるようになれば自動的に復帰します。 |
| `notify-software-encode-title` | recording | Recording on the CPU | CPU エンコードで録画中 |
| `notify-untrimmed-text` | recording | The recording could not be re-encoded, so it was kept as it is - including the few seconds ffmpeg went on recording after the quest ended. If you switched away from the game there, your desktop is in those seconds. Watch the end before sharing it. | 録画を再エンコードできなかったため、そのまま保存しました。クエスト終了後に ffmpeg が録画し続けた数秒間も含まれています。その間にゲームから他の画面へ切り替えていた場合、デスクトップが写っています。共有する前に終わりを確認してください。 |
| `notify-untrimmed-title` | recording | Check the end of this recording | この録画の終わりを確認してください |
| `overlay-corner-label` | gui | Overlay position | オーバーレイの表示位置 |
| `pairing-expired` | gui | Token: pairing expired - press \"Link with the site\" again, or paste a token | トークン: 接続の有効期限が切れました。「サイトと連携」をもう一度押すか、トークンを貼り付けてください |
| `pairing-failed` | gui | Token: pairing could not start (~a) | トークン: 接続を開始できません (~a) |
| `pairing-waiting` | gui | Token: approve the connection in your browser... | トークン: ブラウザで接続を承認してください... |
| `pinshare-channel-label` | gui | Party passphrase | 合言葉 |
| `pinshare-channel-note` | gui | Same passphrase = shared pins. Anyone who knows it can see and remove them; make it hard to guess. | 同じ合言葉の人同士でピンを共有します。知っている人は誰でも見たり消したりできるので、推測されにくいものに。 |
| `pinshare-channel-save-button` | gui | Apply passphrase | 合言葉を適用 |
| `pinshare-enabled-label` | gui | Share ground pins and arrows with your party (adds the Pin Share addon to the game) | パーティーと地面のピン・矢印を共有する (ゲームに Pin Share アドオンを追加します) |
| `pinshare-pin-set-active` | gui | Pin set: \"~a\" by ~a | ピンセット: 「~a」(作者 ~a) |
| `pinshare-pin-set-none` | gui | Pin set: none for this quest (choose one on the quest's Pin sets tab on the site) | ピンセット: このクエストでは未選択 (サイトのクエストページの「ピンセット」タブで選べます) |
| `pinshare-pin-set-none-idle` | gui | Pin set: load a quest to use the one chosen on the site | ピンセット: クエストを読み込むと、サイトで選んだセットを表示します |
| `pinshare-save-confirm-overwrite` | gui | Replace every pin and arrow of \"~a\" with the ~d pin(s) and ~d arrow(s) in the channel now? The set's own locked pins on screen are NOT kept - only what is in the channel. Everyone using the set gets the new ones. | 「~a」のピンと矢印を、今チャンネルにあるピン ~d 本・矢印 ~d 本で置き換えますか? 画面に出ているこのセットの固定ピンは含まれません (チャンネルにあるものだけが保存されます)。このセットを使っている全員に反映されます。 |
| `pinshare-save-created` | gui | Saved as a private pin set. Name it, describe it and choose who can see it on the site:~%~a | 非公開のピンセットとして保存しました。名前・説明・公開範囲はサイトで設定できます:~%~a |
| `pinshare-save-failed` | gui | Could not save the pin set: ~a | ピンセットを保存できませんでした: ~a |
| `pinshare-save-new-button` | gui | Save pins as a new set | 今のピンを新しいセットとして保存 |
| `pinshare-save-no-items` | gui | There are no pins or arrows in the channel to save. Place them in-game first (Pin Share must be connected). | 保存するピンや矢印がチャンネルにありません。先にゲーム内で置いてください (Pin Share の接続が必要です)。 |
| `pinshare-save-no-quest` | gui | Load the quest first: a pin set is saved for the quest you are in. | 先にクエストを読み込んでください。ピンセットは今いるクエストに保存されます。 |
| `pinshare-save-not-mine` | gui | The set in use is not yours (or none is in use), so there is nothing to overwrite. Save a new set instead. | 使用中のセットが自分のものではない (または未使用) ため、上書きできません。新しいセットとして保存してください。 |
| `pinshare-save-overwrite-button` | gui | Overwrite my set in use | 使用中の自分のセットに上書き |
| `pinshare-save-updated` | gui | Pin set updated (~d pins, ~d arrows). | ピンセットを更新しました (ピン ~d 本、矢印 ~d 本)。 |
| `pinshare-status-broken-link` | pinshare | Pin Share: ~a is a link to a folder that no longer exists - delete or re-create the link | Pin Share: ~a はリンク先がなくなったリンクです - リンクを削除するか作り直してください |
| `pinshare-status-conflict` | pinshare | Pin Share: the old relay client is still running - close its black window | Pin Share: 旧中継クライアントが起動中です - 黒いウィンドウを閉じてください |
| `pinshare-status-connected` | pinshare | Pin Share: connected - ~a (~d in the channel) | Pin Share: 接続済み - ~a (チャンネルに ~d 人) |
| `pinshare-status-connecting` | pinshare | Pin Share: connecting... | Pin Share: 接続中... |
| `pinshare-status-error` | pinshare | Pin Share: ~a | Pin Share: ~a |
| `pinshare-status-install-failed` | pinshare | Pin Share: could not install the addon (~a) | Pin Share: アドオンを配置できませんでした (~a) |
| `pinshare-status-local-only` | pinshare | Pin Share: showing pin set \"~a\" (no passphrase - not sharing with the party) | Pin Share: ピンセット「~a」を表示中 (合言葉なし - パーティーとは共有していません) |
| `pinshare-status-no-addon` | pinshare | Pin Share: connected - waiting for the in-game addon (just installed? press Reload in the game's addon menu) | Pin Share: 接続済み - ゲーム内アドオンの応答待ち (配置直後はゲームのアドオンメニューで Reload を押してください) |
| `pinshare-status-no-channel` | pinshare | Pin Share: enter a party passphrase | Pin Share: 合言葉を入力してください |
| `pinshare-status-no-plugin` | pinshare | Pin Share: the game has no addon plugin (addons\\init.lua not found) | Pin Share: ゲームに addon プラグインがありません (addons\\init.lua が見つかりません) |
| `pinshare-status-not-allowed` | pinshare | Pin Share: not available for this account yet (limited rollout; needs a linked account) | Pin Share: このアカウントではまだ利用できません (限定提供中・サイト連携が必要です) |
| `pinshare-status-off` | pinshare | Pin Share: off | Pin Share: オフ |
| `pinshare-status-waiting-game` | pinshare | Pin Share: waiting for the game | Pin Share: ゲームの起動を待っています |
| `quest-waiting` | gui | ~a (waiting for start) | ~a (開始待ち) |
| `rank-toast-label` | gui | Desktop notification when a run ranks up (provisional #1 / top 3 / PB) | ランクイン時にデスクトップ通知を表示 (暫定1位・トップ3・自己ベスト) |
| `record-audio-label` | gui | Record game audio (only the game is heard, not Discord etc.) | ゲーム音声を録音する (ゲームの音のみ。Discord などの音は入りません) |
| `record-dir-label` | gui | Recordings folder: ~a | 録画フォルダ: ~a |
| `record-storage-note` | gui | The recordings folder is capped at ~a GB; the oldest videos are removed past that. | 録画フォルダは最大 ~a GB までで、超過分は古い動画から自動削除されます。 |
| `recording-file-missing` | gui | The recording file is missing:~%~%~a | 録画ファイルが見つかりません:~%~%~a |
| `recordings-folder-button` | gui | Open recordings folder | 録画フォルダを開く |
| `register-rule-button` | gui | Register quest rule... | クエストルールを登録... |
| `retry-button` | gui | Submit pending runs | 未送信の記録を送信 |
| `rooms-clear` | gui | clear | クリア |
| `rooms-hint` | gui | Rooms and enemies of this run - double-click one to register a rule from it. | この Run の部屋・敵。行をダブルクリックするとそれを条件にルールを登録できます。 |
| `rule-cancel` | gui | Cancel | キャンセル |
| `rule-created` | gui | Quest rule created: ~a | クエストルールを作成しました: ~a |
| `rule-desc-label` | gui | Description | 説明 |
| `rule-dialog-title` | gui | Register quest rule | クエストルールを登録 |
| `rule-duplicate` | gui | A rule with that name already exists: ~a | その名前のルールは既に存在します: ~a |
| `rule-end-floor-switch` | gui | A floor switch fires (floor-switch) | フロアスイッチが立つ (floor-switch) |
| `rule-end-label-form` | gui | Clears when | クリア条件 |
| `rule-end-monster` | gui | A specific enemy dies (monster) | 特定の敵を倒す (monster) |
| `rule-end-register` | gui | A quest register is set (register) | クエストレジスタがセットされる (register) |
| `rule-fetch-failed` | gui | Could not fetch the quest list: ~a | クエスト一覧の取得に失敗しました: ~a |
| `rule-forbidden` | gui | Your account is not a moderator; quest rules cannot be created. | モデレーター権限がないため、クエストルールを作成できません。 |
| `rule-manual-hint` | gui | Manual: fill the value fields below (monster=id, floor-switch=floor & switch, register=value). | 手動: 下の値欄に入力 (monster=id、floor-switch=フロアとswitch、register=値)。 |
| `rule-name-label` | gui | Rule name | ルール名 |
| `rule-need-desc` | gui | Enter a description. | 説明を入力してください。 |
| `rule-need-end` | gui | Choose a clear condition. | クリア条件を選んでください。 |
| `rule-need-name` | gui | Enter a rule name. | ルール名を入力してください。 |
| `rule-need-quest` | gui | Select a quest. | クエストを選択してください。 |
| `rule-need-values` | gui | Enter valid values for the manual trigger (id 0-65535, floor 0-17, switch/register 0-255). | 手動トリガーの値を正しく入力してください (id 0-65535・フロア 0-17・switch/register 0-255)。 |
| `rule-no-parents` | gui | No timeable quests are available to base a rule on. | ルールの土台にできる計測対象クエストがありません。 |
| `rule-post-failed` | gui | Could not create the rule: ~a | ルールの作成に失敗しました: ~a |
| `rule-quest-label` | gui | Quest (auto-detected from your run) | クエスト(プレイ内容から自動判定) |
| `rule-register-ok` | gui | Register | 登録 |
| `rule-rejected` | gui | The rule was rejected: ~a | ルールが拒否されました: ~a |
| `rule-start-inherit` | gui | Inherit from the parent quest | 親クエストから継承する |
| `rule-start-label-form` | gui | Starts when | 開始条件 |
| `rule-start-warp-in` | gui | On warp-in | ワープイン時 (warp-in) |
| `rule-val1-label` | gui | Value 1 | 値1 |
| `rule-val2-label` | gui | Value 2 | 値2 |
| `save-button` | gui | Save & verify | 保存して確認 |
| `server-bad-url` | api | Server: the Server URL looks wrong - fix it and press Save & verify | サーバー: サーバー URL が正しくないようです - 修正して「保存して確認」を押してください |
| `server-check-failed` | api | Server: check failed (~a) | サーバー: 確認に失敗しました (~a) |
| `server-error-prefix` | api | Server: ~a | サーバー: ~a |
| `server-not-checked` | gui | Server: not checked | サーバー: 未確認 |
| `server-ok` | gui | Server: OK (~d quests, ~d timed category~:p~@[; ~d local trigger~:p unknown~]) | サーバー: OK (クエスト ~d 件、計測カテゴリ ~d 件~@[、不明なローカルトリガー ~d 件~]) |
| `server-unexpected` | api | Server: unexpected response (~a) - is the URL right? | サーバー: 予期しない応答 (~a) - URL は正しいですか? |
| `server-url-label` | gui | Server URL | サーバー URL |
| `standing-behind` | store | PB +~a | 自己ベスト +~a |
| `standing-first` | store | first run on this board | このボード初記録 |
| `standing-pb` | store | PB! -~a | 自己ベスト更新! -~a |
| `standing-rank` | store | prov. #~d of ~d | 暫定~d/~d位 |
| `start-minimized-label` | gui | Start minimized to the tray | 起動時にトレイに最小化して起動する |
| `status-aborted-video-attached` | store | aborted - video attached, private | 中断 - 動画紐付け済み・非公開 |
| `status-draft-aborted` | store | aborted - private, recording kept locally | 中断 - 非公開・録画はローカル保存のみ |
| `status-draft-add` | store | draft - double-click to add video | 下書き - ダブルクリックで動画を追加 |
| `status-draft-auto-upload` | store | draft - video uploads automatically | 下書き - 動画は自動でアップロードされます |
| `status-draft-unranked` | store | submitted (record only, not ranked) | 送信済み (記録専用・ランキング対象外) |
| `status-draft-upload` | store | draft - use Upload to YouTube | 下書き - 「YouTube にアップロード」を使ってください |
| `status-duplicate` | store | duplicate (already on server) | 重複 (サーバーに登録済み) |
| `status-failed` | store | failed: ~a | 失敗しました: ~a |
| `status-queued` | store | queued | 送信待ち |
| `status-queued-unlinked` | store | saved on this PC - submits once the server is reachable | この PC に保存済み - サーバーに接続できると送信されます |
| `status-rejected` | store | rejected: ~a | 拒否されました: ~a |
| `status-video-approved` | store | video attached - approved | 動画紐付け済み - 承認済み |
| `status-video-attached` | store | video attached - awaiting review | 動画紐付け済み - 承認待ち |
| `status-video-held` | store | video uploaded - publish it in the browser | 動画アップロード済み - ブラウザで公開してください |
| `tab-rooms` | gui | Rooms | 部屋 |
| `tab-runs` | gui | Runs | 記録 |
| `tab-settings` | gui | Settings | 設定 |
| `toast-first-text` | store | ~a ~a - your first time on this board. Click to open the run. | ~a ~a - このボードへの最初の記録です。クリックで記録ページを開きます。 |
| `toast-first-title` | store | First run on this board! | このボードに初記録! |
| `toast-pb-text` | store | ~a ~a - ~a faster than your previous best. Click to open the run. | ~a ~a - 自己ベストを~a更新しました。クリックで記録ページを開きます。 |
| `toast-pb-title` | store | New personal best! | 自己ベスト更新! |
| `toast-rank-text` | store | ~a ~a - ~a on this board. Click to open the run. | ~a ~a - このボードで~aです。クリックで記録ページを開きます。 |
| `toast-rank-title` | store | Provisional #~d! | 暫定~d位にランクイン! |
| `toast-rank1-text` | store | ~a ~a - the current top time on this board. Click to open the run. | ~a ~a - このボードの現在のトップタイムです。クリックで記録ページを開きます。 |
| `toast-rank1-title` | store | Provisional #1! | 暫定1位です! |
| `token-checking` | gui | Token: checking... | トークン: 確認中... |
| `token-could-not-verify` | api | Token: could not verify (~a) | トークン: 確認できませんでした (~a) |
| `token-invalid` | gui | Token: invalid or revoked | トークン: 無効または失効済み |
| `token-not-checked` | gui | Token: not checked | トークン: 未確認 |
| `token-ok` | gui | Token: OK (~a) | トークン: OK (~a) |
| `token-ok-dialog` | gui | Token OK - authenticated as ~a. | トークン OK - ~a として認証されました。 |
| `token-rejected-dialog` | gui | The server rejected the API token (unauthorized).~%~%Paste a fresh one from the site's token page. | サーバーが API トークンを拒否しました (認証エラー)。~%~%サイトのトークンページから新しいトークンを貼り付けてください。 |
| `token-unlinked` | gui | Not linked - runs upload privately (share by URL); link to publish them | 未連携 - 記録は非公開でアップロードされます (URL で共有可)。連携すると公開できます |
| `tracking-only-label` | gui | Tracking-only mode (no recording; times are never ranked) | 録画なし(記録専用)モード (録画せず、記録はランキング対象外) |
| `tracking-private-label` | gui | Keep record-only times private (you and moderators only) | 記録専用の記録を非公開にする (本人とモデレーターのみ) |
| `tray-quit` | tray | Quit | 終了 |
| `tray-show` | tray | Show | 表示 |
| `tray-tooltip` | tray | Rappy Runs Client | Rappy Runs クライアント |
| `trigger-log-label` | gui | Log trigger changes (for finding switch IDs of new categories) | トリガーの変化をログに記録する (新カテゴリのスイッチ ID 調査用) |
| `trigger-log-on` | gui | Trigger logging is on. Play the segment, then open:~%~%~a~%~%The floor switch (or register) that flips when the room is cleared is your end trigger. | トリガーログを有効にしました。対象の区間をプレイしてから、次のファイルを開いてください:~%~%~a~%~%部屋のクリア時に変化するフロアスイッチ (またはレジスタ) が終了トリガーです。 |
| `update-after-run` | gui | ~a downloaded - installs after this run | ~a ダウンロード済み - 現在のプレイ終了後にインストールします |
| `update-check-failed` | gui | update check failed | アップデートの確認に失敗しました |
| `update-check-failed-dialog` | gui | Could not check for updates - network trouble, GitHub rate limiting, or no release published yet. | アップデートを確認できませんでした - ネットワークの問題、GitHub のレート制限、またはリリースが未公開の可能性があります。 |
| `update-checking` | gui | checking for updates... | アップデートを確認中... |
| `update-dev-build` | gui | This is a dev build (no version baked in), so there is nothing to compare against.~%~%Releases live at:~%~a | これは開発ビルドです (バージョン情報が埋め込まれていません)。比較対象がないため確認できません。~%~%リリースはこちら:~%~a |
| `update-download-failed` | gui | download failed | ダウンロードに失敗しました |
| `update-download-failed-dialog` | gui | The update download failed or did not verify. Nothing was changed; try again later. | アップデートのダウンロードに失敗したか、検証を通りませんでした。何も変更されていません。後でもう一度お試しください。 |
| `update-downloading` | gui | downloading ~a... ~d~@[ / ~d~] MB | ~a をダウンロード中... ~d~@[ / ~d~] MB |
| `update-latest-dialog` | gui | You are on the latest version (~a). | 最新バージョン (~a) を使用しています。 |
| `update-not-writable` | gui | install folder not writable | インストール先フォルダに書き込めません |
| `update-not-writable-confirm` | gui | The client's folder is not writable, so the update cannot be applied automatically.~%~%Open the download page to update by hand? | クライアントのフォルダに書き込めないため、アップデートを自動で適用できません。~%~%ダウンロードページを開いて手動で更新しますか? |
| `update-restarting` | gui | installing ~a - restarting... | ~a をインストール中 - 再起動します... |
| `update-up-to-date` | gui | up to date | 最新です |
| `upload-button` | gui | Upload to YouTube | YouTube にアップロード |
| `version-status` | gui | Version: ~a~@[ - ~a~] | バージョン: ~a~@[ - ~a~] |
| `video-attached` | gui,store | attached | 紐付け済み |
| `video-retention-note` | gui | Top-10 videos are kept; others are deleted after 90 days (see the site's Client page). | 上位10位の動画は保持され、それ以外は90日で削除されます (詳細はサイトの Client ページ)。 |
| `video-saved` | store | saved | 保存済み |
| `video-upload-failed` | store | upload failed | アップロード失敗 |
| `video-untrimmed` | store (C#) | saved - check the end | 保存済み - 終わりを確認 |
| `video-uploaded` | store | uploaded | アップロード済み |
| `video-uploading` | store | uploading ~d% | アップロード中 ~d% |
