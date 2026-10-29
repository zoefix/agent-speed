<div align="center">

<img src="assets/icon/icon-1024.png" width="128" alt="Agent Speed アイコン">

# Agent Speed

**AI agent が全力で走る瞬間を、目で感じる。** Codex・Claude Code・Claude Desktop・DSH（DeepSeek Harness）・OpenCode・Gemini CLI・omp・Antigravity・WorkBuddy・WorkBuddy AI・ZCode・Kimi Code CLI・Grok Build のトークン速度をリアルタイムに映し出すネイティブ計器。待機中は静かに、レッドラインでは燃え上がる。

[English](README.md) · [繁體中文](README.zh-TW.md) · [简体中文](README.zh-CN.md) · [日本語](README.ja.md)

<img src="docs/images/hero.png" width="760" alt="Agent Speed のフローティングオーブとメニューバー">

</div>

## インストール

**macOS 14 以降**（Apple Silicon・Intel）——ターミナルに貼り付け：

```sh
curl -fsSL https://raw.githubusercontent.com/zoefix/agent-speed/main/install.sh | sh
```

**Windows 10 / 11**（x64・ARM64）——PowerShell に貼り付け：

```powershell
irm https://raw.githubusercontent.com/zoefix/agent-speed/main/install.ps1 | iex
```

これだけです。インストーラーが GitHub から最新版をダウンロードし、SHA-256 チェックサムを検証してインストール・起動します。同じコマンドを再実行すると更新できます。

<details>
<summary>アンインストール</summary>

```sh
curl -fsSL https://raw.githubusercontent.com/zoefix/agent-speed/main/install.sh | sh -s -- --uninstall
```

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/zoefix/agent-speed/main/install.ps1))) -Uninstall
```

`--purge` / `-Purge` を付けると設定も削除します。Windows では「設定 → アプリ」からも削除できます。
</details>

## 特長

- **フローティングオーブ**——スモークガラスの計器と 36 セグメントの LED ゲージ。速度が上がるにつれて称号もランクアップ——「徒歩」「自転車」から「新幹線」「戦闘機」へ、レッドラインに達すれば「ロケット」。ゲージは熱のスペクトルに沿って点灯し、彗星がリングを駆け、火花が散り、残り火が舞い上がり、応答が完了するたびに衝撃波が広がります。今日のピークを更新するとお祝いします。
- **5 つのオーブスタイル、オーブはいくつでも**——「LED ゲージ」「針式タコメーター」「リキッドエナジー」「リアクター」「ミニマルリング」。オーブごとにソースを自由に組み合わせられ（すべて、ひとつのツール、Claude Code＋Codex など）、どんな壁紙の上でも読みやすいガラスのネームプレートが付きます。
- **メニューバー／タスクバー**——幅が揺れないコンパクトなゲージと数値。tok/s、金額、または tok/s に現在の金額と今日の合計を添えた表示から選べ、ネットワーク速度も追加できます。Windows ではタスクバーの時計の隣に組み込まれます。
- **ダッシュボード**——円形メーターの中で、スピード称号に合わせた光る線画の乗り物が走ります。徒歩から自転車で走り出し、レッドラインではロケットへ。さらにライブグラフ、ソース別の速度、作業中のすべての agent（サブ agent を含む）とその状態、今日の集計。ソースをクリックすると使用履歴が開きます。
- **費用**——agent の作業を各モデルの公式 API 定価で見積もった金額：tok/s の横に 1 秒あたりの金額をリアルタイムに、ダッシュボードに今日の費用、使用履歴に日ごとの費用を表示します。初期設定は米ドルで、全 38 通貨から選べます（為替レートは毎日更新）。定額プランで使うソースは除外できます。価格表はこのリポジトリから毎日自動で更新されます。
- **通信量**——agent ごと、またはコンピューター全体のアップロード／ダウンロード速度を、オーブに小さな 2 行で表示。メニューバー／タスクバーとダッシュボードにも表示でき、使用履歴に日ごとの通信量を記録します。Windows で agent ごとの通信量を見るには、任意の小さなヘルパーサービスを一度だけインストールします（管理者の承認が必要）。
- **称号テーマ**——9 種類のスピード称号：「乗り物」「動物」「風力階級」「宇宙」「仙道修行」「武芸の道」「魔法の位階」「ゲームランク」「エンジニアの道」。自分だけの称号も最大 8 個まで作れます。乗り物以外のテーマでは、ダッシュボードのメーターに光る称号バッジが表示されます。
- **使用履歴**——最も古い記録から毎日の出力・入力トークン、リクエスト数、平均速度とピーク、費用と通信量を、ソース別に今日・昨日・任意の期間でグラフ表示。モデルごとの速度も確認でき、CSV に書き出せます。保存するのは日ごとの集計だけなので、ツールが古い記録を削除しても履歴は残ります。
- **ライブ送信（任意）**——初期設定はオフ。参加したいときは、選んだユーザー名で、[Codex Radar](https://codexradar.com/) のスピードランキングなど好きな API にライブの数値を共有できます。会話の内容、ファイル、プロジェクト名は一切送りません。詳しくは[プライバシー](#プライバシー)へ。
- **実行モード**——信頼できる記録から Standard／Fast／Ultrafast や各 agent のアクセラレーター、モデルバリアント、ルーティングを表示。既定値と不明を区別し、履歴は専用 CSV に出力できます。速度の計算式は変わりません。
- **アバターと複数デバイス（任意の API 拡張）**——アバターのプレビュー・変更・削除と、既存デバイスの承認が必要なワンタイムコードによる連携。各デバイスの鍵は独立し、連携解除後も計上済みの履歴は残ります。受信サービスには[拡張 API のスキーマ](spec/report-schema.json)の対応が必要です。
- **完全ネイティブ**——macOS は SwiftUI + AppKit、Windows は WPF と Fluent／Mica。ライト・ダーク・自動テーマ。English、繁體中文、简体中文、日本語。
- **人にやさしい**——エフェクトの強さは「オフ」から「全開」まで。「視差効果を減らす」やハイコントラストに対応し、クリック透過、画面端へのスナップ、ディスプレイごとの位置記憶、ログイン時の自動起動を備えています。
- **常に最新**——バックグラウンドで GitHub Releases を確認し、ワンクリックでアップデート。SHA-256 で検証します。

<p align="center"><img src="docs/images/orb-styles.png" width="760" alt="5 つのオーブスタイル：LED ゲージ、針式タコメーター、リキッドエナジー、リアクター、ミニマルリング"></p>

<p align="center"><img src="docs/images/orbs.png" width="760" alt="それぞれ別のソースを表示し、ガラスのネームプレートが付いた複数のオーブ"></p>

<p align="center"><img src="docs/images/hero-vehicle.gif" width="480" alt="ダッシュボードのメーター内の乗り物（自転車からロケットまで）"></p>

<p align="center"><img src="docs/images/dashboards.png" width="560" alt="ダーク（tok/s）とライト（1 秒あたりの金額）のダッシュボード。今日の費用と通信量を表示"></p>

<p align="center"><img src="docs/images/history.png" width="760" alt="日ごとの出力と費用を表示した使用履歴ウィンドウ"></p>

## 速度の測り方

Agent Speed は、agent がすでにローカルに書き出している記録だけを読み取ります。

| ソース | フォルダ |
| --- | --- |
| Codex（CLI・IDE・デスクトップ） | `~/.codex/sessions` と `archived_sessions`、圧縮された記録を含む（または `$CODEX_HOME`） |
| Claude Code（CLI・IDE） | `~/.claude/projects`（または `$CLAUDE_CONFIG_DIR/projects`） |
| Claude Desktop（Code と agent セッション） | `~/.claude/projects`（自動で区別） |
| DSH（`dsh`、デスクトップと CLI） | `~/.dsh/sessions`（または `$DSH_HOME/sessions`） |
| OpenCode（CLI とデスクトップ） | `~/.local/share/opencode/opencode.db`（または `$XDG_DATA_HOME/opencode`） |
| Gemini CLI | `~/.gemini/tmp`（または `$GEMINI_CLI_HOME/.gemini/tmp`） |
| omp（oh-my-pi） | `~/.omp/agent/sessions` と `~/.omp/profiles` 内の各プロファイル（または `$PI_CODING_AGENT_DIR/sessions`） |
| Antigravity（`agy` CLI・デスクトップアプリ・IDE・拡張機能） | `~/.gemini/antigravity-cli`、`~/.gemini/antigravity`、`~/.gemini/antigravity-ide`（それぞれの `conversations` フォルダ） |
| WorkBuddy（Tencent、中国版） | `~/.workbuddy/projects` |
| WorkBuddy AI（国際版） | `~/.workbuddy-ai/projects` |
| ZCode | `~/.zcode/cli/db/db.sqlite`（または `$ZCODE_DATA_BASE_DIR/.zcode/cli/db`） |
| Kimi Code CLI（TypeScript v2） | `~/.kimi-code/sessions`（または `$KIMI_CODE_HOME/sessions`） |
| Grok Build | `~/.grok/sessions`（または `$GROK_HOME/sessions`） |
| Trae（使用量の一部のみ） | macOS `~/Library/Application Support/{Trae,Trae CN,TRAE SOLO,TRAE SOLO CN}`、Windows `%APPDATA%/{Trae,Trae CN,TRAE SOLO,TRAE SOLO CN}` 内の `ModularData/ai-agent/database.db`（旧 `ai-chat` も対応） |

Trae の暗号化されたローカルデータベースを読み取り専用で扱い、平文コピーを保存しません。
完了ターンの最終リクエストに、照合できた以前のリクエストの正確な使用量を重複なく追加します。
**使用量と API 相当コストは不完全な場合があります。** 対応するログの HTTP 送信から `DONE` までを測定し、
初回トークン待機を含め、後続ツールの実行時間を除きます。時間不明でも確かな使用量は集計します。
生成中は既存の推定機能を使い、実測と区別します。実測の参考値がなければ速度を推定しません。
macOS Trae CN の完了データで照合し、動作状態の遷移は合成イベントで検証しました。
設定ではデータフォルダを指定できます。Windows はシステム SQLite のメモリ逆シリアル化対応が必要です。
[Trae の制限](spec/research/trae.md)をご覧ください。

Grok Build のターン別・モデル別バッチに保存されたトークン数、API 所要時間、実際の呼び出し回数を、既存の速度エンジンで計算します。サブエージェントの使用量は親に集約されるため一度だけ計上し、子の動作状態も表示します。個々の呼び出しのタイムラインは保存されないため、バッチ内の並列実行速度は復元できません。

**速度＝出力トークン ÷ モデルが実際にそれらに費やした時間**——リクエスト送信から応答完了まで、最初のトークンを待つ時間を含みます。複数の agent は合算されます。ストリーミング中の応答はその agent の最近の実測速度で推定し、完了した瞬間に実測値で補正します。「総スループット」（入力＋出力）にも切り替えられます。

テスト可能な完全な仕様は [`spec/METRICS.md`](spec/METRICS.md) にあります。両プラットフォームのアプリは同じテストデータと独立した[リファレンス実装](spec/tools/reference.py)で相互検証されています。

Claude Desktop の通常のチャット会話はローカルに保存されないため、計測できません。WorkBuddy でクラウド実行された会話も集計されません。

## プライバシー

読み取り専用・ローカルのみで動作し、テレメトリもアカウントもありません。Agent Speed が自動で行うネットワーク通信は次の 3 種類だけで、どれもあなたに関する情報を送りません：GitHub Releases でのアップデート確認、このリポジトリで公開しているモデル価格表の 1 日 1 回のダウンロード（GitHub、予備に jsDelivr）、そして米ドル以外の通貨で費用を表示するときだけ行う公開為替レートの 1 日 1 回のダウンロード（open.er-api.com、予備に fawazahmed0 currency API と Frankfurter）。全員が同じファイルを取得するため、サーバーがあなたの通貨・モデル・使用量を知ることはありません。設定のスイッチ 1 つですべての通信をオフにでき、その場合はアプリ内蔵の価格表とレートで費用を計算します。Agent Speed は会話の内容を保存・表示せず、表示するのはトークン数・モデル名・プロジェクトフォルダー名などと、タスクのスコアに使う手順の種類（下記）だけです。使用履歴は日別の集計（各日の API 換算費用を含む）とタスクごとの小さなスコアの記録だけを、お使いのコンピューター上の小さなデータベース（`~/Library/Application Support/AgentSpeed`、`%LOCALAPPDATA%\AgentSpeed`）に保存し、設定から消去できます。アップデートはインストール前にリリースの SHA-256 チェックサムで検証します。チェックサムはリリースと一緒に公開されるため、破損や改ざんされたダウンロードは検出できますが、リポジトリ自体の侵害は防げません。価格表は厳格なルールで検証される単なるデータで、署名はされていません。

通信量：Agent Speed はこのコンピューター上で、プログラムが送受信したバイト数だけを数えます。内容・アドレス・接続先は一切読みません。このうち agent のバイト数を送信するのは、オンにしたライブ送信（下記）だけで、送信先は選んだサービスです。Windows で agent ごとの通信量を表示するには、任意のヘルパーサービスが必要です。権限の少ない「ローカル サービス」アカウントで動作し、プロセスごとのバイト数だけを数えます。インストールと削除のときに、それぞれ一度だけ管理者の承認が必要です（[詳細](spec/WINDOWS-SERVICE.md)）。

タスクのスコア：agent が問題を解決しているのか堂々巡りしているのかを判断するため、Agent Speed は記録を読む際に、agent が実行したコマンド、その出力、加えた編集をメモリ上でのみ分析します。残すのは手順の種類（`cargo test` の実行など）、元に戻せない短いダイジェスト、回数、終了コード、時刻と、タスクごとのスコア付きの記録だけで、テキストそのものを保存・表示したり、ログに書いたりすることはありません。送信するのは、ライブ送信をオンにしてモデルの共有もオンのままにしている場合だけで、その内容は現在のスコア、思考の速度、そしてオンにしてから完了したタスクのモデルごとの集計（タスク数、スコア、成功・停滞・修正の回数）という数値のみです。コマンドやその出力、ファイル名やプロジェクト名、オンにする前のタスクは送信しません。

ライブ送信（任意・初期設定はオフ。ダッシュボードで一度だけ案内します）：API（公開ランキングサービスなど。組み込みの選択肢は Codex Radar のスピードランキング `deng.codexradar.com`）に対してオンにすると、Agent Speed は現在の速度、直近数秒に使った token 数、そしてオンのままにした項目——米ドル建ての API 換算費用、agent の通信量、使用中のモデル（サービスが対応していれば、モデルごとの知能スコア、思考の速度、完了したタスクの集計を含む）、ツールごとの内訳——を、選んだユーザー名（初期値はコンピューター名。ワンクリックでランダムな名前にできます）と一緒にそのサービスへ送信します。agent が作業している間は数秒ごとに送信し、休んでいる間は止まります。会話の内容、プロンプト、コマンドやその出力、ファイル・フォルダー・プロジェクトの名前、使用履歴は一切送信せず、送信に失敗したデータを後から送り直すこともありません。各送信はこのコンピューターから出ない鍵で署名されます。サービスは同じデバイスからの送信であることは確認できますが、数値が正しいかどうかは確認できません。設定から一時停止・オフにしたり、サービスにデバイスの削除を依頼したりできます。

モデル共有を有効にし、サービスが対応している場合、任意の API 拡張でモデル別の Harness リクエスト数、実行モード、上記の知能に関する数値も送信します。アバターのアップロードとワンタイムコードによるデバイス連携は明示的な操作のみで、受信側の[拡張 API のスキーマ](spec/report-schema.json)対応が必要です。定期レポートに画像は含めず、秘密鍵はデバイスごとに独立しています。ネットワーク全体のスイッチを切ると、プロフィール関連の通信も停止します。

Agent Speed が接続する可能性のあるホストとそのタイミングは、すべて [`spec/NETWORK.md`](spec/NETWORK.md) に記載しています。

## ソースからビルド

<details>
<summary>macOS（Xcode 26 以降）</summary>

```sh
cd macos
swift test
./scripts/build-app.sh          # → dist/AgentSpeed-macOS.zip
```
</details>

<details>
<summary>Windows（.NET 10 SDK。macOS／Linux からのクロスビルドも可）</summary>

```sh
dotnet test windows/tests/AgentSpeed.Core.Tests
dotnet test windows/tests/AgentSpeed.Presentation.Tests
./windows/scripts/publish.sh    # → dist/AgentSpeed-Windows-{x64,arm64}.zip
```
</details>

## リンク

[Codex Radar](https://codexradar.com/)

## ライセンス

[MIT](LICENSE) © 2026 zoefix
