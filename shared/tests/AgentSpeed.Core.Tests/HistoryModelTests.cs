using AgentSpeed.Core.History;
using AgentSpeed.Core.IO.Sqlite;
using AgentSpeed.Core.Monitoring;

namespace AgentSpeed.Core.Tests;

/// <summary>Measured output and time per model in the daily history (METRICS 8) and the store upgrade that adds them.</summary>
public sealed class HistoryModelTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly long _t = ClaudeLines.T0;

    public void Dispose() => _temp.Dispose();

    private string StorePath => _temp.Combine("store", "history.sqlite");

    private SourceLocations Locations => new(
        _temp.Combine("codex", "sessions"), _temp.Combine("claude", "projects"), _temp.Combine("dsh", "sessions"),
        _temp.Combine("opencode"), []);

    [Fact]
    public void EachModelKeepsItsMeasuredOutputAndTime()
    {
        _temp.WriteFile("claude/projects/p/a.jsonl", string.Concat(
            ClaudeLines.UserText(_t, "go") + "\n",
            ClaudeLines.Assistant(_t + 2000, "msg_a", 100) + "\n",
            ClaudeLines.UserText(_t + 5000, "more") + "\n",
            ClaudeLines.Assistant(_t + 9000, "msg_b", 300) + "\n"));
        using var store = HistoryStore.Open(StorePath);
        var analyzer = new HistoryAnalyzer(store);
        analyzer.Run(Locations, new HashSet<SourceKind> { SourceKind.ClaudeCode }, TimeZoneInfo.Utc, CancellationToken.None);

        var model = Assert.Single(Assert.Single(analyzer.Days()).Models);
        Assert.Equal((2L, 400L, 400L, 6000L), (model.Requests, model.Output, model.MeasuredOutput, model.MeasuredMs));
        Assert.Equal(400 / 6.0, model.AverageSpeed!.Value, 9);

        using var reopened = HistoryStore.Open(StorePath);
        var stored = Assert.Single(Assert.Single(new HistoryAnalyzer(reopened).Days()).Models);
        Assert.Equal(model, stored);
    }

    [Fact]
    public void AVersionOneStoreGainsTheColumnsAndEveryFileIsAnalysedAgain()
    {
        _temp.WriteFile("claude/projects/p/a.jsonl",
            ClaudeLines.UserText(_t, "go") + "\n" + ClaudeLines.Assistant(_t + 2000, "msg_a", 100) + "\n");
        using (var store = HistoryStore.Open(StorePath))
        {
            new HistoryAnalyzer(store).Run(Locations, new HashSet<SourceKind> { SourceKind.ClaudeCode }, TimeZoneInfo.Utc, CancellationToken.None);
        }

        using (var old = SqliteConnection.Open(StorePath, SqliteOpenMode.ReadWriteCreate))
        {
            old.Execute("""
                CREATE TABLE models_v1 (file_id INTEGER NOT NULL, day TEXT NOT NULL, source TEXT NOT NULL,
                  model TEXT NOT NULL, requests INTEGER NOT NULL, output INTEGER NOT NULL,
                  PRIMARY KEY (file_id, day, source, model)) WITHOUT ROWID;
                INSERT INTO models_v1 SELECT file_id, day, source, model, requests, output FROM contribution_models;
                DROP TABLE contribution_models;
                ALTER TABLE models_v1 RENAME TO contribution_models;
                UPDATE files SET version = 1;
                PRAGMA user_version = 1;
                """);
        }

        using var upgraded = HistoryStore.Open(StorePath);
        var analyzer = new HistoryAnalyzer(upgraded);
        Assert.Equal(0, Assert.Single(Assert.Single(analyzer.Days()).Models).MeasuredMs);

        var report = analyzer.Run(Locations, new HashSet<SourceKind> { SourceKind.ClaudeCode }, TimeZoneInfo.Utc, CancellationToken.None);

        Assert.Equal(1, report.FilesAnalyzed);
        var model = Assert.Single(Assert.Single(analyzer.Days()).Models);
        Assert.Equal((100L, 2000L), (model.MeasuredOutput, model.MeasuredMs));
        using var check = SqliteConnection.Open(StorePath, SqliteOpenMode.ReadOnly);
        Assert.Equal(HistoryStore.SchemaVersion, check.ScalarInt64("PRAGMA user_version"));
    }

    [Fact]
    public void ModelSpeedIsNullWithoutMeasurements() => Assert.Null(new ModelUsage("m", 3, 30).AverageSpeed);
}
