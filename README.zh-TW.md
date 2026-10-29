<div align="center">

<img src="assets/icon/icon-1024.png" width="128" alt="Agent Speed 圖示">

# Agent Speed

**看見你的 AI agent 全速奔跑。** 為 Codex、Claude Code、Claude Desktop、DSH（DeepSeek Harness）、OpenCode、Gemini CLI、omp、Antigravity、WorkBuddy、WorkBuddy AI、ZCode、Kimi Code CLI 與 Grok Build 打造的原生即時 token 速度儀表——閒置時安靜，衝到紅線時全面燃燒。

[English](README.md) · [繁體中文](README.zh-TW.md) · [简体中文](README.zh-CN.md) · [日本語](README.ja.md)

<img src="docs/images/hero.png" width="760" alt="Agent Speed 懸浮窗與選單列">

</div>

## 安裝

**macOS 14 以上**（Apple Silicon 與 Intel）——貼到「終端機」：

```sh
curl -fsSL https://raw.githubusercontent.com/zoefix/agent-speed/main/install.sh | sh
```

**Windows 10 / 11**（x64 與 ARM64）——貼到 PowerShell：

```powershell
irm https://raw.githubusercontent.com/zoefix/agent-speed/main/install.ps1 | iex
```

就這樣。安裝程式會從 GitHub 下載最新版本、驗證 SHA-256 校驗碼、安裝並啟動。之後重新執行同一行指令即可更新。

<details>
<summary>解除安裝</summary>

```sh
curl -fsSL https://raw.githubusercontent.com/zoefix/agent-speed/main/install.sh | sh -s -- --uninstall
```

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/zoefix/agent-speed/main/install.ps1))) -Uninstall
```

加上 `--purge` / `-Purge` 會一併刪除設定。Windows 也可以從「設定 → 應用程式」移除。
</details>

## 功能

- **懸浮窗**——煙燻玻璃質感的儀表，36 格 LED 錶盤。速度一路攀升時，稱號也跟著升級——從「步行」「自行車」到「高鐵」「戰鬥機」，衝到紅線就是「火箭」；錶盤沿著熱度光譜點亮、彗星繞圈疾馳、火花四濺、餘燼升騰，每完成一次回應就釋放一道衝擊波；打破今日紀錄還會慶祝。
- **五種懸浮窗樣式，想開幾個就開幾個**——「LED 儀表」「指針轉速錶」「能量液球」「反應爐」「極簡圓環」。每個懸浮窗都能自由組合來源（全部、單一工具，或 Claude Code＋Codex），下方的玻璃名牌在任何桌布上都清楚好讀。
- **選單列／工作列**——精簡的儀表與數字，寬度固定不跳動；可顯示 tok/s、金額，或 tok/s 加上即時花費與今日總額，也能加上網路速度。Windows 版直接嵌在工作列裡、時鐘旁邊。
- **面板**——圓形儀表裡有一台跟著速度稱號變化的發光線條載具：從步行、蹬著自行車出發，一路換到紅線時的火箭；另有即時曲線、各來源速度、所有正在工作的 agent（含子 agent）與其狀態、今日統計。點一下來源就能打開它的用量歷史。
- **費用**——以各模型官方 API 牌價估算 agent 的工作值多少錢：tok/s 旁的每秒即時花費、面板上的今日費用，以及用量歷史裡每天的費用。預設美元，也能選 38 種貨幣中的任一種（每天更新匯率）；月費訂閱的來源可以排除。價格表每天從本專案自動更新。
- **網路流量**——各 agent 或整台電腦的上傳／下載速度，以兩行小字顯示在懸浮窗上，選單列／工作列與面板也看得到，用量歷史記錄每天的流量。Windows 若要看各 agent 的流量，需要安裝一次選用的小型輔助服務（需系統管理員同意）。
- **稱號主題**——九套速度稱號：「交通工具」「動物」「蒲福風級」「宇宙」「修真境界」「武學境界」「魔法位階」「遊戲段位」「工程師職涯」，也能自訂最多 8 組。選交通工具以外的主題時，面板圓圈會顯示發光的稱號徽章。
- **用量歷史**——從最早的紀錄開始，每天的輸出與輸入 token、請求數、平均速度與峰值、費用與網路流量，可依來源，以今天、昨天或任意日期區間繪成圖表，並看各模型的速度、匯出 CSV。只保存每日彙總，即使工具刪除了舊紀錄，歷史也不會消失。
- **即時上報（選用）**——預設關閉；想參與的話，可以用你取的使用者名稱，把即時數字分享給你選的 API，例如 [Codex Radar](https://codexradar.com/) 的速蹬天梯。從不傳送對話內容、檔案或專案名稱，詳見[隱私](#隱私)。
- **執行模式**——依可靠紀錄顯示 Standard／Fast／Ultrafast，以及各 agent 的加速卡、模型變體或路由選擇，分清設定預設與未知；歷史可另行匯出模式 CSV，沿用既有測速公式。
- **頭像與多設備（選用 API 擴充）**——預覽、更換、移除頭像，以一次性配對碼經原設備核准後合併身分；每台設備保留自己的金鑰，解除不影響已入帳歷史。接收端須支援[新版 API 結構](spec/report-schema.json)。
- **全原生**——macOS 使用 SwiftUI + AppKit，Windows 使用 WPF 與 Fluent／Mica。淺色、深色、自動主題；繁體中文、简体中文、English、日本語。
- **人性化**——特效強度從「關閉」到「熱血全開」，遵循「減少動態效果」與高對比設定，支援滑鼠穿透、邊緣磁吸、記住每個螢幕上的位置、登入時自動啟動。
- **永遠最新**——背景檢查 GitHub Releases，有新版一鍵更新，並以 SHA-256 驗證。

<p align="center"><img src="docs/images/orb-styles.png" width="760" alt="五種懸浮窗樣式：LED 儀表、指針轉速錶、能量液球、反應爐、極簡圓環"></p>

<p align="center"><img src="docs/images/orbs.png" width="760" alt="多個懸浮窗，各自追蹤不同來源並附玻璃名牌"></p>

<p align="center"><img src="docs/images/hero-vehicle.gif" width="480" alt="面板圓圈裡的載具，從自行車到火箭"></p>

<p align="center"><img src="docs/images/dashboards.png" width="560" alt="深色（tok/s）與淺色（每秒花費）面板，含今日費用與網路流量"></p>

<p align="center"><img src="docs/images/history.png" width="760" alt="用量歷史視窗，含每日輸出與費用"></p>

## 速度怎麼算

Agent Speed 只讀取 agent 本來就寫在你電腦上的紀錄：

| 來源 | 資料夾 |
| --- | --- |
| Codex（CLI、IDE、桌面版） | `~/.codex/sessions` 與 `archived_sessions`，含壓縮紀錄（或 `$CODEX_HOME`） |
| Claude Code（CLI、IDE） | `~/.claude/projects`（或 `$CLAUDE_CONFIG_DIR/projects`） |
| Claude Desktop（Code 與 agent 工作階段） | `~/.claude/projects`，自動區分 |
| DSH（`dsh`，桌面版與命令列） | `~/.dsh/sessions`（或 `$DSH_HOME/sessions`） |
| OpenCode（命令列與桌面版） | `~/.local/share/opencode/opencode.db`（或 `$XDG_DATA_HOME/opencode`） |
| Gemini CLI | `~/.gemini/tmp`（或 `$GEMINI_CLI_HOME/.gemini/tmp`） |
| omp（oh-my-pi） | `~/.omp/agent/sessions`，以及 `~/.omp/profiles` 裡的每個設定檔（或 `$PI_CODING_AGENT_DIR/sessions`） |
| Antigravity（`agy` 命令列、桌面版、IDE 與擴充功能） | `~/.gemini/antigravity-cli`、`~/.gemini/antigravity`、`~/.gemini/antigravity-ide`（其中的 `conversations` 資料夾） |
| WorkBuddy（騰訊，中國版） | `~/.workbuddy/projects` |
| WorkBuddy AI（國際版） | `~/.workbuddy-ai/projects` |
| ZCode | `~/.zcode/cli/db/db.sqlite`（或 `$ZCODE_DATA_BASE_DIR/.zcode/cli/db`） |
| Kimi Code CLI（TypeScript v2） | `~/.kimi-code/sessions`（或 `$KIMI_CODE_HOME/sessions`） |
| Grok Build | `~/.grok/sessions`（或 `$GROK_HOME/sessions`） |
| Trae（僅部分用量） | macOS `~/Library/Application Support/{Trae,Trae CN,TRAE SOLO,TRAE SOLO CN}`；Windows `%APPDATA%/{Trae,Trae CN,TRAE SOLO,TRAE SOLO CN}` 下的 `ModularData/ai-agent/database.db`，相容舊版 `ai-chat` 資料庫 |

Trae 適配唯讀本機加密資料庫，不將明文副本寫入磁碟。統計已完成回合的末次請求，並在精確用量可核對時
補充此前每次模型請求，避免重複計數。**token 用量及 API 等價成本仍可能不完整。** 請求日誌對應後，
以 HTTP 傳送到串流 `DONE` 計算 TPS，包含首字等待並排除後續工具執行；缺少計時仍保留可靠用量。
新的請求狀態接入現有即時估速，並區分估算與實測；沒有實測參考值時不估速。
設定支援自訂資料目錄並保留歷史不完整提示。已核對 macOS Trae CN 原生完成紀錄，即時狀態切換通過
合成事件回放測試；Windows 需要系統 SQLite 支援記憶體反序列化。詳見 [Trae 資料限制](spec/research/trae.md)。

Grok Build 提供每回合、每模型的原生批次紀錄：Token、API 耗時與實際呼叫數交給同一套速度引擎計算。子 agent 用量已合併在父工作階段中，因此只計算一次，仍會顯示子 agent 的活動狀態；上游未保存每次呼叫的獨立時間線，無法重建批次內的並行速度。

**速度＝輸出 token ÷ 模型實際花在這些 token 上的時間**——從送出請求到回應完成，含等待第一個字。多個 agent 平行時會加總。回應還在進行中時，先以該 agent 近期的實測速度估算，完成的瞬間立即以實測值校正。也可以切換成「總吞吐量」（輸入＋輸出）。

完整且可測試的規格見 [`spec/METRICS.md`](spec/METRICS.md)。兩個平台的程式都以同一份測試資料，以及一份獨立的[參考實作](spec/tools/reference.py)交叉驗證。

Claude Desktop 一般聊天對話不會存在本機，因此無法統計；WorkBuddy 在雲端執行的對話也不會計入。

## 隱私

唯讀、只在本機運作，沒有遙測、不需帳號。Agent Speed 只會自動發出三種網路請求，而且都不會傳送任何關於你的資料：向 GitHub Releases 檢查更新、每天從本專案下載公開的模型價格表（GitHub，以 jsDelivr 為備援），以及只有在你選擇以美元以外的貨幣顯示費用時，每天下載公開匯率（open.er-api.com，以 fawazahmed0 currency API 和 Frankfurter 為備援）。每位使用者下載的都是同一份檔案，所以伺服器無從得知你的貨幣、模型或用量。設定裡的一個開關就能關閉所有網路連線，此時費用改用 App 內建的價格表與匯率。Agent Speed 從不儲存或顯示對話內容，只顯示 token 數量、模型名稱、專案資料夾名稱等資訊，以及任務評分用到的步驟類型（見下文）。用量歷史只保存每日總計（含每日的 API 等值費用）和每個任務一筆小小的分數紀錄，存在你電腦上的一個小型資料庫（`~/Library/Application Support/AgentSpeed`、`%LOCALAPPDATA%\AgentSpeed`），可在設定中清除。更新在安裝前會以該版本發布的 SHA-256 校驗碼驗證；校驗碼與版本一起發布，因此能攔下損壞或遭竄改的下載檔，但無法防範儲存庫本身遭入侵。價格表是依嚴格規則檢查的純資料，沒有數位簽章。

網路流量：Agent Speed 只在本機計算各程式收發的位元組數，從不讀取內容、網址或連線對象；只有在你開啟即時上報（見下文）時，才會把 agent 的位元組數傳送給你選擇的服務。在 Windows 上，要顯示各 agent 的流量需要安裝選用的輔助服務；它以權限受限的「本機服務」帳戶執行，只統計每個程式的位元組數，安裝和移除各需要一次系統管理員確認（[詳細說明](spec/WINDOWS-SERVICE.md)）。

任務評分：為了判斷 agent 是在解決問題還是原地打轉，Agent Speed 會在讀取紀錄時，於記憶體中分析 agent 執行的指令、指令的輸出和所做的修改。它只保留步驟的類型（例如一次 `cargo test`）、無法還原的短雜湊值、次數、結束代碼和時間，以及每個任務一筆含分數的紀錄；這些文字本身從不儲存、顯示或寫進日誌。只有在你開啟即時上報、並保留模型分享時，才會傳送其中的數字：目前的分數、思考速度，以及開啟上報後各模型完成任務的統計（任務數、分數、成功、卡住和修正的次數）。只有數字，從不傳送指令、指令輸出、檔案或專案名稱，也不會傳送開啟之前的任務。

即時上報（選用，預設關閉；面板會詢問一次）：為某個 API（例如公開排行榜服務，像是內建的 Codex Radar 速蹬天梯 `deng.codexradar.com`）開啟後，Agent Speed 會把你的即時速度、最近幾秒用掉的 token 數，以及你保留的項目——以美元計的 API 等值費用、agent 的網路流量、使用中的模型（服務支援時，含各模型的智力分數、思考速度和完成任務的統計）、各工具的明細——連同你選擇的使用者名稱（預設是電腦名稱，一鍵就能換成隨機名稱）傳送給該服務。agent 工作時每隔幾秒上報一次，閒下來就停止。它從不傳送對話內容、提示詞、指令或指令輸出、檔案／資料夾／專案名稱或用量歷史，上報失敗的資料也不會補傳。每筆上報都以只存在你電腦上的金鑰簽章；服務能確認資料來自同一台裝置，但無法確認數字是否正確。可以在設定中暫停、關閉，或要求服務忘記你的裝置。

選用 API 擴充會在模型分享開啟、且服務支援時，額外傳送每模型的 Harness 請求明細、執行模式，以及上述智力相關的數字。頭像上傳與一次性設備配對僅由你明確操作，且接收端必須支援[擴充 API](spec/report-schema.json)；圖片不會放進週期報文。配對不共用私鑰，網路總開關也會停用個人資料請求。

Agent Speed 可能連線的所有主機與時機，都列在 [`spec/NETWORK.md`](spec/NETWORK.md)。

## 從原始碼建置

<details>
<summary>macOS（Xcode 26 以上）</summary>

```sh
cd macos
swift test
./scripts/build-app.sh          # → dist/AgentSpeed-macOS.zip
```
</details>

<details>
<summary>Windows（.NET 10 SDK；也能在 macOS／Linux 交叉建置）</summary>

```sh
dotnet test windows/tests/AgentSpeed.Core.Tests
dotnet test windows/tests/AgentSpeed.Presentation.Tests
./windows/scripts/publish.sh    # → dist/AgentSpeed-Windows-{x64,arm64}.zip
```
</details>

## 友情連結

[Codex Radar](https://codexradar.com/)

## 授權

[MIT](LICENSE) © 2026 zoefix
