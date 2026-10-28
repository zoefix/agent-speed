using System.Text;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Tests.TestSupport;

namespace AgentSpeed.Presentation.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private string SettingsPath => _temp.Combine("settings.json");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void MissingFileYieldsDefaults()
    {
        var store = new SettingsStore(SettingsPath);
        var result = store.Load();
        Assert.Equal(SettingsLoadStatus.Missing, result.Status);
        Assert.Same(AppSettings.Default, result.Settings);
        Assert.Null(result.BackupPath);
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void PathsAreNormalized()
    {
        var store = new SettingsStore(SettingsPath);
        Assert.Equal(Path.GetFullPath(SettingsPath), store.FilePath);
        Assert.Equal(store.FilePath + ".corrupt", store.CorruptBackupPath);
        Assert.Throws<ArgumentException>(() => new SettingsStore(""));
        Assert.Throws<ArgumentNullException>(() => new SettingsStore(null!));
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[]")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("﻿{\"redline\": ")]
    public void CorruptFileIsSetAsideAndDefaultsAreUsed(string content)
    {
        var bytes = new UTF8Encoding(false).GetBytes(content);
        File.WriteAllBytes(SettingsPath, bytes);
        var store = new SettingsStore(SettingsPath);

        var result = store.Load();

        Assert.Equal(SettingsLoadStatus.RecoveredFromCorruption, result.Status);
        Assert.Same(AppSettings.Default, result.Settings);
        Assert.Equal(store.FilePath + ".corrupt", result.BackupPath);
        Assert.False(File.Exists(store.FilePath));
        Assert.True(File.Exists(store.CorruptBackupPath));
        Assert.Equal(bytes, File.ReadAllBytes(store.CorruptBackupPath));

        Assert.Equal(SettingsLoadStatus.Missing, store.Load().Status);
    }

    [Fact]
    public void OversizedFileIsSetAsideEvenWhenValid()
    {
        var content = "{ \"redline\": 800 }" + new string(' ', 2 * 1024 * 1024);
        File.WriteAllText(SettingsPath, content);
        var store = new SettingsStore(SettingsPath);

        var result = store.Load();

        Assert.Equal(SettingsLoadStatus.RecoveredFromCorruption, result.Status);
        Assert.Equal(2000, result.Settings.RedlineOutput);
        Assert.False(File.Exists(store.FilePath));
        Assert.Equal(content.Length, new FileInfo(store.CorruptBackupPath).Length);
    }

    [Fact]
    public void NewCorruptionReplacesAnOlderBackup()
    {
        var store = new SettingsStore(SettingsPath);
        File.WriteAllText(store.CorruptBackupPath, "old backup");
        File.WriteAllText(SettingsPath, "{broken");

        Assert.Equal(SettingsLoadStatus.RecoveredFromCorruption, store.Load().Status);
        Assert.Equal("{broken", File.ReadAllText(store.CorruptBackupPath));
    }

    [Fact]
    public void ValidFileWithBomLoads()
    {
        File.WriteAllText(SettingsPath, "{ \"redline\": 800, \"orb\": { \"size\": 112 } }", new UTF8Encoding(true));
        var result = new SettingsStore(SettingsPath).Load();
        Assert.Equal(SettingsLoadStatus.Loaded, result.Status);
        Assert.Equal(800, result.Settings.RedlineOutput);
        Assert.Equal(112, result.Settings.OrbSize);
    }

    [Fact]
    public void SaveThenLoadRoundTrips()
    {
        var settings = SettingsSerializerTests.CustomSettings();
        var store = new SettingsStore(SettingsPath);

        store.Save(settings);
        var result = store.Load();

        Assert.Equal(SettingsLoadStatus.Loaded, result.Status);
        SettingsSerializerTests.AssertEquivalent(settings, result.Settings);
    }

    [Fact]
    public void SaveWritesUtf8WithoutBom()
    {
        var store = new SettingsStore(SettingsPath);
        store.Save(AppSettings.Default);
        var bytes = File.ReadAllBytes(SettingsPath);
        Assert.Equal((byte)'{', bytes[0]);
    }

    [Fact]
    public void SavingTwiceReplacesTheFileAndLeavesNoTemporaryFiles()
    {
        var store = new SettingsStore(SettingsPath);
        store.Save(AppSettings.Default with { RedlineOutput = 600 });
        store.Save(AppSettings.Default with { RedlineOutput = 1200, Effects = EffectsIntensity.Soft });

        var result = store.Load();
        Assert.Equal(SettingsLoadStatus.Loaded, result.Status);
        Assert.Equal(1200, result.Settings.RedlineOutput);
        Assert.Equal(EffectsIntensity.Soft, result.Settings.Effects);

        var files = _temp.Files();
        Assert.Equal([store.FilePath], files.Select(Path.GetFullPath));
        Assert.DoesNotContain(files, f => f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SaveCreatesMissingFolders()
    {
        var store = new SettingsStore(_temp.Combine("a", "b", "settings.json"));
        store.Save(AppSettings.Default);
        Assert.True(File.Exists(store.FilePath));
        Assert.Equal(SettingsLoadStatus.Loaded, store.Load().Status);
    }

    [Fact]
    public void SaveAfterRecoveryWritesAFreshFile()
    {
        File.WriteAllText(SettingsPath, "{oops");
        var store = new SettingsStore(SettingsPath);
        Assert.Equal(SettingsLoadStatus.RecoveredFromCorruption, store.Load().Status);

        store.Save(AppSettings.Default with { OrbSize = 196 });
        var result = store.Load();
        Assert.Equal(SettingsLoadStatus.Loaded, result.Status);
        Assert.Equal(196, result.Settings.OrbSize);
        Assert.True(File.Exists(store.CorruptBackupPath));
    }

    [Fact]
    public void SaveRejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new SettingsStore(SettingsPath).Save(null!));
    }
}
