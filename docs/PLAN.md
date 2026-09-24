# Rappy Runs Desktop (C#) 移行計画

2026-09-24 作成。LispWorks クライアント (`client/`, v0.60.0) を C# + WebView2 の HTML UI に
書き換える。**M1 = 現行クライアントと機能パリティ**。gamepad 統合 (`~/src/psobb-gamepad-manager`) は M1 後の機能追加。

仕様書 (移植の正本。Lisp を読み直さずに実装できる粒度):

| 文書 | 範囲 |
|---|---|
| [spec/core.md](spec/core.md) | 起動・単一インスタンス・設定/キュー・sexp・HTTP/API・認証・メモリ・検出・テレメトリ・アップデータ・i18n・テスト |
| [spec/media.md](spec/media.md) | 録画 (WGC/ddagrab/gdigrab・WASAPI・ffmpeg・末尾トリム)・アップロード・オーバーレイ・ゴースト |
| [spec/ui-shell.md](spec/ui-shell.md) | 画面/操作一覧・トレイ/通知・自動起動・Pin Share (中継・アドオン・入力 DLL) |

## 技術構成

- **.NET 10 (LTS)**、`PublishSingleFile` + `SelfContained` + `IncludeNativeLibrariesForSelfExtract`。
  旧アップデータは zip ルートの exe・`data\*`・`ffmpeg\*` しかコピーしないため (core §10.7)、
  exe 以外の追加物 (UI の静的ファイル等) は `data\` 配下に置くか exe に埋め込む。
- **ホスト**: WinForms の最小ウィンドウ + WebView2。UI とは JSON メッセージ (`PostWebMessageAsJson` / `WebMessageReceived`) で通信。
  WebView2 Runtime 不在時はネイティブのダイアログで案内 (起動失敗はロールバックされないため必須)。
- **フロント**: TypeScript + Vite + Svelte。ビルド成果物は exe に埋め込み、仮想ホスト名で配信。
- **Win32/WinRT**: CsWin32 (P/Invoke 生成)、WGC は `Windows.Graphics.Capture` を直接。WASAPI はプロセスループバックを直接 COM で。
- **オーバーレイ**: M1 は現行と同じレイヤードウィンドウ (カラーキー + `WDA_EXCLUDEFROMCAPTURE`) を移植。表現力の強化は M1 後。
- **ビルド/リリース**: GitHub Actions でビルド→zip。`pinshare-input.dll` (C++) も Actions の MSVC でビルド。

## ディレクトリ

```
desktop/
  RappyRuns.sln
  src/RappyRuns.Core/      純粋ロジック (sexp, 検出, API, キュー, ゴースト計算, i18n)。Win32 非依存
  src/RappyRuns.Win/       Win32/WinRT (メモリ, 録画, 音声, オーバーレイ, トレイ, Pin Share 入出力)
  src/RappyRuns.App/       ホスト exe (WebView2, IPC, 起動シーケンス)
  ui/                      Svelte フロント
  tests/RappyRuns.Tests/   xUnit。golden/ に Lisp 由来の期待値
  native/pinshare-input/   client/ から移設 (M1 切り替え時)
  docs/
```

## パリティの担保

1. **ゴールデンテスト**: `client/tests` (約 770 チェック) の純粋部分を SBCL で実行し、入力と出力を JSON に書き出すスクリプトを
   `client/tests/export-golden.lisp` として足す。C# 側は同じ JSON を流して一致を確認。対象: 検出状態機械、PB/テレパイプ判定、
   NPC 判定、sexp 読み書き、run/telemetry JSON、バージョン比較、ゴースト投影・部屋照合、末尾トリム計算、Pin Share の in/out.txt。
2. **数値の罠**: CL の `round` は偶数丸め (= C# 既定)、`floor` は負の無限大方向、single-float は double に広げない (core リスク 7)。
3. **実機チェックリスト**: 仕様書の回帰リスク表 (core 50 件 / media 25 件 / ui-shell 24 件) をフェーズごとの受け入れ項目にする。

## フェーズ

| # | 内容 | 完了条件 |
|---|---|---|
| P0 ✅ | 雛形: sln、WebView2 ホスト、IPC、i18n の JSON 化 (機械変換)、Actions でのビルドと zip | 空の画面が単一 exe で出る。旧アップデータの展開手順 (zip ルート exe) で起動できる |
| P1 🔧 | Core: sexp、config/queue 読み書き (sexp 互換で書き戻す)、HTTP/API、認証/ペアリング/ゲスト、クエスト定義、ゴールデンテスト基盤 | ゴールデン一致。実 config.sexp を読み書きして Lisp 版が読み戻せる |
| P2 🔧 | ゲーム接続: ウィンドウ探索、Authenticode、メモリ、検出、テレメトリ、trigger-log、送信キュー | 実ゲームでラン検出→送信 (録画なし) |
| P3 🔧 | シェルと UI: 単一インスタンス (旧版と同じミューテックス/クラス名)、トレイ/通知、自動起動、メイン画面 (状態・ラン一覧・設定・Rooms・ルール登録)、アップデータ | 現行 GUI の全操作が新 UI で可能 |
| P4 🔧 | 録画: 取り込み方式の選択、WGC/ddagrab/gdigrab、WASAPI、ffmpeg、停止/リマックス/トリム、アップロード、保管と削除 | 窓/フルスクリーン/2 GPU で実走録画。色と音ズレが一致 |
| P5 🔧 | オーバーレイとゴースト | 実走でゴースト表示、Z 順維持、録画に写らない |
| P6 🔧 | Pin Share: 中継、アドオン/DLL の導入、ピンセット、機能ゲート | 限定ユーザー環境で送受信 |
| P7 ▶ | ドッグフード: テスト用リポジトリにリリースし、テスターは `:UPDATE-REPO` で取得 (core §10.7) | 開発者 + 限定ユーザーで数日実走 |
| P8 | 切り替え: 本リポジトリに `v1.0.0` を非プレリリースで公開 → 旧版が起動時に自動で入れ替え | 本番ユーザーの更新を確認 |

P1〜P2 と P3 の UI 部分は並行可能 (IPC の型を先に固める)。

### 進捗 (2026-09-24)

凡例: ✅ 完了 / 🔧 実装・単体テスト済みで実機確認待ち / ▶ 進行中

- P1〜P6 は PR #330 でマージ (xUnit 1166 件、Lisp の SBCL 出力とのゴールデン照合を含む)。実ゲームでの確認はまだ (P7 で行う)
- ブリッジ版 Lisp **v0.61.0 を公開済み** (PR #329)。開発機の本番クライアントが実際に v0.61.0 へ自動更新し、目印を書くことを確認
- P7: ドッグフード版 **v0.90.0** を `PSOBBAITools/rappyruns-client-dogfood` に公開。開発機で「v0.61.0 のコピー → `:UPDATE-REPO` で dogfood を指す → ブリッジのヘルパーが C# 版を無人インストール → C# 版が起動・目印・`.old` 掃除 → 実設定 (トークン・言語・モデレーター・Pin Share) を引き継ぎ」まで通しで確認済み
- P7 実プレイ (2026-09-24、開発者、v0.90.0): ラン検出と送信 (通常・中断・記録専用の 3 種、サーバーの runs 12112〜12118)、WGC 録画 + ゲーム音、動画アップロード (242MB) と自動公開、Pin Share の接続と入力 DLL の導入、アカウント判定を確認
- P7 の残り: ゲーム上のオーバーレイ (ゴースト表示・Z 順・録画に写らないこと)、Pin Share のピンのやりとり、フルスクリーン / ddagrab の録画、ブラウザペアリング、限定ユーザーでの数日の実走
- P8 (`v1.0.0` を本リポジトリに非プレリリースで公開) は、P7 の実プレイ確認と、稼働中クライアントの大半が v0.61.0 以上になるまでの期間を置いてから

#### ドッグフードの始め方 (テスター)

1. クライアントが v0.61.0 以上であることを確認して終了する
2. `%APPDATA%\ephinea-ta-client\config.sexp` の先頭の `(` の直後に `:UPDATE-REPO "PSOBBAITools/rappyruns-client-dogfood" ` を足す
3. クライアントを起動すると C# 版が入る (起動しなければ自動で元に戻る)
4. 戻すときはキーを消し、本番の最新版 zip を手で展開する

新しいドッグフード版は `desktop/VERSION` を上げてコミットし、`.\desktop\release.ps1 vX.Y.Z -Dogfood`。

### P0 で決めた契約 (2026-09-24)

- **起動完了マーカー** (ブリッジ版アップデータ用): `%TEMP%\rappyruns-client-started.txt` に `<pid> <version>` の 1 行 (UTF-8)。
  UI が host に `app.hello` を送った時点で 1 回書く。ブリッジ版はこのファイルを消してから新 exe を起動し、
  新プロセスの PID が書かれるのを待つ。
- **IPC**: `{kind:"request",id,method,params}` → `{kind:"response",id,ok,result|error}`、host からの通知は `{kind:"event",name,data}`。
  C# `Host/IpcHost.cs` と TS `ui/src/lib/ipc.ts` が両端。メソッド名は `領域.動詞` (例 `app.hello`)。
- **UI の配信**: Vite のビルドを exe に埋め込み、`https://app.rappyruns.internal/` を `WebResourceRequested` で返す。
  それ以外への遷移はすべて既定ブラウザで開く。WebView2 のユーザーデータは `%LOCALAPPDATA%\ephinea-ta-client\WebView2`。
- **i18n**: `strings.json` は `desktop/tools/export-i18n.lisp` で i18n.lisp から機械変換 (`{n}` / `{n?…}` / `{n#単数|複数}`)。
  Lisp の `FORMAT` 出力をゴールデンにして C# と TS の両方で照合。英語の "categorys" は i18n.lisp 側で直した (`~:@p`)。
- **バージョン**: `desktop/VERSION` がリリース版の正本。`package.ps1 -Version X.Y.Z` がこれと一致を検査して exe に焼き込む。
  指定なしは dev ビルド (自己更新しない)。
- **検証済み**: 旧 Lisp の `updater-script-text` が生成したスクリプトそのもので zip を適用し、新 exe が引数なしで起動して
  マーカーを書くことを確認 (`data\` はマージ、旧 exe は `.old`)。

## 切り替え時の安全策

- 旧版は起動時に**全ユーザーへ無人で** C# 版を入れる。新 exe は起動さえすれば成功扱いで、自動ロールバックはない。
- 対策: **ブリッジ版 (Lisp v0.61.x)** を先に出し、アップデータに「新 exe が N 秒以内に起動完了の印を書かなければ `.old` に戻す」を足す。
  切り替えまでに十分な期間を置き、稼働中クライアントの大半をブリッジ版にしてから `v1.0.0` を出す。
- config.sexp は M1 の間は sexp 互換で書き戻す (Lisp 版に戻しても設定とトークンが残る)。queue.sexp は取り込み後に `.migrated` へ。
- 自動起動のレジストリ値が旧 exe 名を指している場合は正規パスで書き直す。

## 決定事項 (2026-09-24)

1. **ブリッジ版を出す。** Lisp v0.61.x のアップデータに「新 exe が起動完了の印を N 秒以内に書かなければ `.old` へ戻して旧 exe を再起動」を足す。
   C# 側は起動シーケンスの最後 (WebView2 初期化成功後) に印を書く。P7 と並行で出し、`v1.0.0` 公開までに期間を空ける。
2. **パリティより良い挙動を優先してよい** (簡単に直せる場合、または旧挙動に合わせるほうが大変な場合)。直した点はパリティのゴールデンから外し、
   この表に理由を残す:

   | 旧挙動 (core §22 #46/#47) | C# 版の挙動 |
   |---|---|
   | メモリ読み取りが 1 フレーム失敗しただけでスナップショット NIL → 走行中のトラッカーを中断して武装解除 | プロセスが生きている間の読み取り失敗は猶予 (連続 1 秒程度) を置き、回復すればそのまま計測を続ける。猶予を超えたら中断 |
   | ゲーム終了時の 15 秒超の中断ランは黙って捨てる (戻り値の取りこぼし) | ロビー帰還・リロードと同じく中断ランとして送信キューに入れる (サーバー側で非公開、動画はアップロードしない) |
   | 未送信が 0 件でも Retry (`submit-queued!`) が先に匿名ゲストを登録する | 未送信 (`:queued`/`:failed`) が無ければ何もしない (空のキューでサーバーにアカウントを作らない) |
   | トレイ・Pin Share・ポーリング開始・check-server/check-token は窓の生成直後 (CAPI は表示 = UI 完成) | 窓の表示時に開始する。WebView2 のページ読み込み (`app.hello`) を待つのは起動完了マーカーと起動時更新の報告だけ。ページが壊れていても計測と送信は止まらない |
   | 言語切替・モデレーター判定の変化で窓を作り直す | 作り直さない。文言は UI が切り替え、Rooms タブ等は `state.moderator` で出し入れする |
   | ラン送信 (`submit-queued!`) は poll スレッド上で同期 (core §9.4)。サーバーが落ちていると送信待ちの間フレームが止まり、クエスト中なら計測時間が伸び録画のステップも止まる | 送信は専用のバックグラウンドワーカー 1 本で行う (1 回に 1 パス、要求は合流)。完了ランのキュー投入は従来どおり poll スレッドで (`:video-offset-ms` 刻印・ゴースト注釈の後に) 行い、その後で送信パスを要求する。トーストはワーカーから出す。終了時は HTTP を中止して最大 3 秒だけ待つ (キューは 1 件ごとに保存済み) |
   | 更新後の掃除は `RappyRunsClient.exe.old` だけ。別名の exe から更新すると `<旧名>.old` が残り続ける | インストール先の `*.exe.old` をすべて掃除する。別名の exe から更新するとき既存の `RappyRunsClient.exe` も `.old` へ退避し、ロールバックで元に戻す |
   | 開発用コピーでも録画フォルダの古いファイル掃除と容量スイープが動く | 開発用コピー (`RAPPYRUNS_CONFIG_DIR` / 多重起動) では両方とも行わない。自分のキューが本体の録画を知らず、共有フォルダの本体の録画を消しうるため |

3. **DPI は M1 では現行の見た目に合わせる。** ただしプロセス全体を DPI 非対応にすると WebView2 の UI がにじむため、
   プロセスは PerMonitorV2、**オーバーレイのスレッドだけ** `SetThreadDpiAwarenessContext(UNAWARE)` で現行と同じ座標・文字サイズにする。
   オーバーレイを高 DPI に対応させるのは M1 後の表現力強化と一緒に行う。
