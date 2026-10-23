# Windows Antigravity support

This local build adds independent **Antigravity Desktop** and **Antigravity CLI (agy)** sources to the existing Windows monitor, dashboard, settings, history and CSV export.

The default roots are `~/.gemini/antigravity/conversations` and `~/.gemini/antigravity-cli/conversations`. Each conversation is a `.db` SQLite database. Database and WAL changes are polled once per second. Existing history uses the normal ten-minute refresh and manual Refresh action. Source toggles persist independently; old settings enable both sources by default.

Live activity also reads each root's sibling `conversation_summaries.db`, including its WAL. Antigravity can mark a conversation RUNNING before it persists the next planner step. During this gap the latest recorded step still looks idle; the running summary now supplies the in-flight phase and start time. The existing engine estimates speed from recent measured responses and labels it as estimated. Completed requests correct the measurement with actual token counts. Summary changes are applied even if the conversation database itself has not changed. Executing/waiting tools remain tools, idle/killed summaries stop estimation, and a missing/unreadable summary clears cached activity. With no recent measurement to use as a reference, no arbitrary speed is invented.

Only usage, model and timing protobuf fields are decoded. Conversations are opened read-only in short WAL-aware transactions. No source schema changes, prompt/response text extraction, telemetry or additional network calls are introduced. Old `.pb` conversation files are not supported.

Usage follows the existing normalized contract: input includes uncached input plus cache-read and cache-write tokens; output already includes reasoning, which is also exposed separately. One generation metadata row is one request, even when it references several output/tool steps. Its stable key includes the client, conversation filename and row index. Repeat polling updates the same key instead of adding a request.

End time is the latest `finished_generating_at` among the referenced steps. Generation duration includes time to first token plus streaming duration. Both fields must exist; a present zero duration is valid. Missing duration yields usage without a speed estimate. Partial metadata rows are skipped and reported. SQLite/schema failures produce unreadable/unsupported warnings and preserve previously collected values.

The field contract was checked against descriptors embedded in the locally installed Antigravity binaries on 2026-10-23. It is an internal storage format and may change after an Antigravity update. This implementation is Windows-only; macOS remains unchanged. Installing an official Agent Speed release can replace this local feature.

Build with the .NET 10 SDK:

```powershell
dotnet test windows/tests/AgentSpeed.Core.Tests
dotnet test windows/tests/AgentSpeed.Presentation.Tests
dotnet publish windows/src/AgentSpeed/AgentSpeed.csproj -c Release -r win-x64 --self-contained true -o .local/release -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:Version=1.2.0 -p:InformationalVersion=1.2.0+antigravity.2
```

The live monitor's startup file/byte counters refer to transcript files; Antigravity database requests appear in the source totals and session counts. No artificial requests or source logs are generated for acceptance testing.
