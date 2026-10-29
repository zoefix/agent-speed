<div align="center">

<img src="assets/icon/icon-1024.png" width="128" alt="Agent Speed 图标">

# Agent Speed

**看见你的 AI agent 全速奔跑。** 为 Codex、Claude Code、Claude Desktop、DSH（DeepSeek Harness）、OpenCode、Gemini CLI、omp、Antigravity、WorkBuddy、WorkBuddy AI、ZCode、Kimi Code CLI 与 Grok Build 打造的原生实时 token 速度仪表——空闲时安静，冲到红线时全面燃烧。

[English](README.md) · [繁體中文](README.zh-TW.md) · [简体中文](README.zh-CN.md) · [日本語](README.ja.md)

<img src="docs/images/hero.png" width="760" alt="Agent Speed 悬浮窗与菜单栏">

</div>

## 安装

**macOS 14 及以上**（Apple Silicon 与 Intel）——粘贴到「终端」：

```sh
curl -fsSL https://raw.githubusercontent.com/zoefix/agent-speed/main/install.sh | sh
```

**Windows 10 / 11**（x64 与 ARM64）——粘贴到 PowerShell：

```powershell
irm https://raw.githubusercontent.com/zoefix/agent-speed/main/install.ps1 | iex
```

就这样。安装程序会从 GitHub 下载最新版本、验证 SHA-256 校验码、安装并启动。之后重新运行同一行命令即可更新。

<details>
<summary>卸载</summary>

```sh
curl -fsSL https://raw.githubusercontent.com/zoefix/agent-speed/main/install.sh | sh -s -- --uninstall
```

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/zoefix/agent-speed/main/install.ps1))) -Uninstall
```

加上 `--purge` / `-Purge` 会同时删除设置。Windows 也可以在「设置 → 应用」中卸载。
</details>

## 功能

- **悬浮窗**——烟熏玻璃质感的仪表，36 格 LED 表盘。速度一路攀升时，称号也跟着升级——从「步行」「自行车」到「高铁」「战斗机」，冲到红线就是「火箭」；表盘沿着热度光谱点亮、彗星绕圈疾驰、火花四溅、余烬升腾，每完成一次回应就释放一道冲击波；打破今日纪录还会庆祝。
- **五种悬浮窗样式，想开几个就开几个**——「LED 仪表」「指针转速表」「能量液球」「反应堆」「极简圆环」。每个悬浮窗都能自由组合来源（全部、单个工具，或 Claude Code＋Codex），下方的玻璃名牌在任何壁纸上都清晰易读。
- **菜单栏／任务栏**——精简的仪表与数字，宽度固定不跳动；可显示 tok/s、金额，或 tok/s 加上实时花费与今日总额，也能加上网络速度。Windows 版直接嵌在任务栏里、时钟旁边。
- **面板**——圆形仪表里有一台跟着速度称号变化的发光线条载具：从步行、蹬着自行车出发，一路换到红线时的火箭；另有实时曲线、各来源速度、所有正在工作的 agent（含子 agent）及其状态、今日统计。点一下来源就能打开它的用量历史。
- **费用**——按各模型官方 API 标价估算 agent 的工作值多少钱：tok/s 旁的每秒实时花费、面板上的今日费用，以及用量历史里每天的费用。默认美元，也能选 38 种货币中的任意一种（每天更新汇率）；包月订阅的来源可以排除。价格表每天从本项目自动更新。
- **网络流量**——各 agent 或整台电脑的上传／下载速度，以两行小字显示在悬浮窗上，菜单栏／任务栏与面板也能看到，用量历史记录每天的流量。Windows 若要看各 agent 的流量，需要安装一次可选的小型辅助服务（需管理员同意）。
- **称号主题**——九套速度称号：「交通工具」「动物」「蒲福风级」「宇宙」「修真境界」「武学境界」「魔法位阶」「游戏段位」「工程师生涯」，也能自定义最多 8 组。选择交通工具以外的主题时，面板圆圈会显示发光的称号徽章。
- **用量历史**——从最早的记录开始，每天的输出与输入 token、请求数、平均速度与峰值、费用与网络流量，可按来源，以今天、昨天或任意日期区间绘成图表，并查看各模型的速度、导出 CSV。只保存每日汇总，即使工具删除了旧记录，历史也不会消失。
- **实时上报（可选）**——默认关闭；想参与的话，可以用你取的用户名，把实时数字分享给你选择的 API，例如 [Codex Radar](https://codexradar.com/) 的速蹬天梯。从不发送对话内容、文件或项目名称，详见[隐私](#隐私)。
- **执行模式**——依据可靠记录显示 Standard／Fast／Ultrafast，以及各 agent 的加速卡、模型变体或路由选择，区分默认设置与未知；历史可单独导出模式 CSV，沿用既有测速公式。
- **头像与多设备（可选 API 扩展）**——预览、更换、移除头像，通过一次性配对码由原设备批准后合并身份；每台设备保留自己的密钥，解绑不影响已入账历史。接收端须支持[新版 API 结构](spec/report-schema.json)。
- **全原生**——macOS 使用 SwiftUI + AppKit，Windows 使用 WPF 与 Fluent／Mica。浅色、深色、自动主题；繁體中文、简体中文、English、日本語。
- **人性化**——特效强度从「关闭」到「热血全开」，遵循「减弱动态效果」与高对比度设置，支持鼠标穿透、边缘磁吸、记住每块屏幕上的位置、登录时自动启动。
- **永远最新**——后台检查 GitHub Releases，有新版一键更新，并以 SHA-256 验证。

<p align="center"><img src="docs/images/orb-styles.png" width="760" alt="五种悬浮窗样式：LED 仪表、指针转速表、能量液球、反应堆、极简圆环"></p>

<p align="center"><img src="docs/images/orbs.png" width="760" alt="多个悬浮窗，各自追踪不同来源并附玻璃名牌"></p>

<p align="center"><img src="docs/images/hero-vehicle.gif" width="480" alt="面板圆圈里的载具，从自行车到火箭"></p>

<p align="center"><img src="docs/images/dashboards.png" width="560" alt="深色（tok/s）与浅色（每秒花费）面板，含今日费用与网络流量"></p>

<p align="center"><img src="docs/images/history.png" width="760" alt="用量历史窗口，含每日输出与费用"></p>

## 速度怎么算

Agent Speed 只读取 agent 本来就写在你电脑上的记录：

| 来源 | 文件夹 |
| --- | --- |
| Codex（CLI、IDE、桌面版） | `~/.codex/sessions` 与 `archived_sessions`，含压缩记录（或 `$CODEX_HOME`） |
| Claude Code（CLI、IDE） | `~/.claude/projects`（或 `$CLAUDE_CONFIG_DIR/projects`） |
| Claude Desktop（Code 与 agent 会话） | `~/.claude/projects`，自动区分 |
| DSH（`dsh`，桌面版与命令行） | `~/.dsh/sessions`（或 `$DSH_HOME/sessions`） |
| OpenCode（命令行与桌面版） | `~/.local/share/opencode/opencode.db`（或 `$XDG_DATA_HOME/opencode`） |
| Gemini CLI | `~/.gemini/tmp`（或 `$GEMINI_CLI_HOME/.gemini/tmp`） |
| omp（oh-my-pi） | `~/.omp/agent/sessions`，以及 `~/.omp/profiles` 里的每个配置文件（或 `$PI_CODING_AGENT_DIR/sessions`） |
| Antigravity（`agy` 命令行、桌面版、IDE 与扩展） | `~/.gemini/antigravity-cli`、`~/.gemini/antigravity`、`~/.gemini/antigravity-ide`（其中的 `conversations` 文件夹） |
| WorkBuddy（腾讯，国内版） | `~/.workbuddy/projects` |
| WorkBuddy AI（国际版） | `~/.workbuddy-ai/projects` |
| ZCode | `~/.zcode/cli/db/db.sqlite`（或 `$ZCODE_DATA_BASE_DIR/.zcode/cli/db`） |
| Kimi Code CLI（TypeScript v2） | `~/.kimi-code/sessions`（或 `$KIMI_CODE_HOME/sessions`） |
| Grok Build | `~/.grok/sessions`（或 `$GROK_HOME/sessions`） |
| Trae（仅部分用量） | macOS `~/Library/Application Support/{Trae,Trae CN,TRAE SOLO,TRAE SOLO CN}`；Windows `%APPDATA%/{Trae,Trae CN,TRAE SOLO,TRAE SOLO CN}` 下的 `ModularData/ai-agent/database.db`，兼容旧版 `ai-chat` 数据库 |

Trae 适配只读本地加密数据库，不落盘保存明文副本。统计已完成回合的末次请求，并在精确用量能够对账时
补充此前每次模型请求，避免重复计数。**token 用量及 API 等价成本仍可能不完整。** 请求日志匹配后，
以 HTTP 发送到流式 `DONE` 计算 TPS，包含首字等待并排除后续工具执行；缺少计时仍保留可靠用量。
新鲜的请求状态接入项目现有实时估速，并区分估算与实测；没有实测参考值时不估速。
设置支持自定义数据目录并保留历史不完整提示。已核对 macOS Trae CN 原生完成记录，实时状态切换通过
合成事件回放测试；Windows 需要系统 SQLite 支持内存反序列化。详见 [Trae 数据边界](spec/research/trae.md)。

Grok Build 提供每回合、每模型的原生批次记录：Token、API 耗时与实际调用数交给同一套速度引擎计算。子 agent 用量已合并在父会话中，因此只计算一次，仍会显示子 agent 的活动状态；上游未保存每次调用的独立时间线，无法重建批次内的并行速度。

**速度＝输出 token ÷ 模型实际花在这些 token 上的时间**——从发出请求到回应完成，含等待第一个字。多个 agent 并行时会加总。回应仍在进行时，先以该 agent 近期的实测速度估算，完成的瞬间立即以实测值校正。也可以切换成「总吞吐量」（输入＋输出）。

完整且可测试的规格见 [`spec/METRICS.md`](spec/METRICS.md)。两个平台的程序都以同一份测试数据，以及一份独立的[参考实现](spec/tools/reference.py)交叉验证。

Claude Desktop 普通聊天对话不会保存在本机，因此无法统计；WorkBuddy 在云端运行的对话也不计入。

## 隐私

只读、只在本机运行，没有遥测、无需账号。Agent Speed 只会自动发出三种网络请求，而且都不会发送任何关于你的数据：向 GitHub Releases 检查更新、每天从本项目下载公开的模型价格表（GitHub，以 jsDelivr 为备用），以及仅在你选择以美元以外的货币显示费用时，每天下载公开汇率（open.er-api.com，以 fawazahmed0 currency API 和 Frankfurter 为备用）。每位用户下载的都是同一份文件，因此服务器无从得知你的货币、模型或用量。设置里的一个开关即可关闭所有网络连接，此时费用改用应用内置的价格表和汇率。Agent Speed 从不保存或显示对话内容，只显示 token 数量、模型名称、项目文件夹名称等信息，以及任务评分用到的步骤类型（见下文）。用量历史只保存每日汇总（含每日的 API 等值费用）和每个任务一条小小的分数记录，存放在你电脑上的一个小型数据库（`~/Library/Application Support/AgentSpeed`、`%LOCALAPPDATA%\AgentSpeed`），可在设置中清除。更新在安装前会用该版本发布的 SHA-256 校验和验证；校验和与版本一同发布，因此能拦截损坏或被篡改的下载文件，但无法防范仓库本身被入侵。价格表是按严格规则校验的纯数据，没有数字签名。

网络流量：Agent Speed 只在本机计算各程序收发的字节数，从不读取内容、网址或连接对象；只有在你开启实时上报（见下文）时，才会把 agent 的字节数发送给你选择的服务。在 Windows 上，要显示各 agent 的流量需要安装可选的辅助服务；它以权限受限的“本地服务”账户运行，只统计每个程序的字节数，安装和卸载各需要一次管理员确认（[详细说明](spec/WINDOWS-SERVICE.md)）。

任务评分：为了判断 agent 是在解决问题还是原地打转，Agent Speed 会在读取记录时，在内存中分析 agent 运行的命令、命令的输出和所做的修改。它只保留步骤的类型（例如一次 `cargo test`）、无法还原的短哈希值、次数、退出码和时间，以及每个任务一条含分数的记录；这些文字本身从不保存、显示或写入日志。只有在你开启实时上报、并保留模型分享时，才会发送其中的数字：当前的分数、思考速度，以及开启上报后各模型完成任务的统计（任务数、分数、成功、卡住和修正的次数）。只有数字，从不发送命令、命令输出、文件或项目名称，也不会发送开启之前的任务。

实时上报（可选，默认关闭；面板会询问一次）：为某个 API（例如公开排行榜服务，像是内置的 Codex Radar 速蹬天梯 `deng.codexradar.com`）开启后，Agent Speed 会把你的实时速度、最近几秒用掉的 token 数，以及你保留的项目——以美元计的 API 等值费用、agent 的网络流量、使用中的模型（服务支持时，含各模型的智力分数、思考速度和完成任务的统计）、各工具的明细——连同你选择的用户名（默认是电脑名称，一键即可换成随机名称）发送给该服务。agent 工作时每隔几秒上报一次，空闲时停止。它从不发送对话内容、提示词、命令或命令输出、文件／文件夹／项目名称或用量历史，上报失败的数据也不会补发。每条上报都用只保存在你电脑上的密钥签名；服务能确认数据来自同一台设备，但无法确认数字是否准确。可以在设置中暂停、关闭，或要求服务忘记你的设备。

可选 API 扩展会在模型分享开启、且服务支持时，额外发送每模型的 Harness 请求明细、执行模式，以及上述智力相关的数字。头像上传与一次性设备配对仅由你明确操作，且接收端必须支持[扩展 API](spec/report-schema.json)；图片不会放入周期报文。配对不共享私钥，网络总开关也会停用个人资料请求。

Agent Speed 可能连接的所有主机与时机，都列在 [`spec/NETWORK.md`](spec/NETWORK.md)。

## 从源码构建

<details>
<summary>macOS（Xcode 26 及以上）</summary>

```sh
cd macos
swift test
./scripts/build-app.sh          # → dist/AgentSpeed-macOS.zip
```
</details>

<details>
<summary>Windows（.NET 10 SDK；也能在 macOS／Linux 交叉构建）</summary>

```sh
dotnet test windows/tests/AgentSpeed.Core.Tests
dotnet test windows/tests/AgentSpeed.Presentation.Tests
./windows/scripts/publish.sh    # → dist/AgentSpeed-Windows-{x64,arm64}.zip
```
</details>

## 友情链接

[Codex Radar](https://codexradar.com/)

## 许可证

[MIT](LICENSE) © 2026 zoefix
