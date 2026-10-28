using AgentSpeed.Core;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Tests.TestSupport;

namespace AgentSpeed.Presentation.Tests;

public class SourceFoldersTests
{
    private static readonly string Gemini = Path.Combine(Path.GetTempPath(), ".gemini");
    private static readonly string Cli = Path.Combine(Gemini, "antigravity-cli");
    private static readonly string Desktop = Path.Combine(Gemini, "antigravity");
    private static readonly string Ide = Path.Combine(Gemini, "antigravity-ide");
    private static readonly string[] Stores = [Cli, Desktop, Ide];

    [Fact]
    public void AChosenFolderIsNamedAloneEvenWhenItIsMissing()
    {
        var chosen = Path.Combine(Path.GetTempPath(), "elsewhere", "conversations");
        Assert.Equal([chosen], SourceFolders.Antigravity(chosen, Stores, _ => true));
        Assert.Equal([chosen], SourceFolders.Antigravity(chosen, Stores, _ => false));
    }

    [Fact]
    public void TheDefaultStoresFoundAreNamedInReadingOrder()
    {
        Assert.Equal([Cli, Ide], SourceFolders.Antigravity(null, Stores, path => path != Desktop));
        Assert.Equal([Desktop], SourceFolders.Antigravity("  ", Stores, path => path == Desktop));
        Assert.Equal(Stores, SourceFolders.Antigravity(null, Stores, _ => true));
    }

    [Fact]
    public void WithoutAnyStoreTheFirstDefaultIsNamed()
    {
        Assert.Equal([Cli], SourceFolders.Antigravity(null, Stores, _ => false));
        Assert.Empty(SourceFolders.Antigravity(null, [], _ => true));
    }

    [Fact]
    public void ExistingFoldersOnDiskAreFound()
    {
        using var temp = new TempDirectory();
        string[] stores = [temp.Combine("antigravity-cli"), temp.Combine("antigravity"), temp.Combine("antigravity-ide")];
        Directory.CreateDirectory(stores[1]);
        Directory.CreateDirectory(stores[2]);
        Assert.Equal([stores[1], stores[2]], SourceFolders.Antigravity(null, stores, Directory.Exists));
    }

    [Fact]
    public void TheMonitorDefaultsStartWithTheCliStore()
    {
        var defaults = LiveMonitor.DefaultAntigravityStores;
        Assert.Equal(["antigravity-cli", "antigravity", "antigravity-ide"], defaults.Select(Path.GetFileName));
        Assert.Equal([defaults[0]], SourceFolders.Antigravity(null, defaults, _ => false));
    }

    [Fact]
    public void ArgumentsAreRequired()
    {
        Assert.Throws<ArgumentNullException>(() => SourceFolders.Antigravity(null, null!, _ => true));
        Assert.Throws<ArgumentNullException>(() => SourceFolders.Antigravity(null, Stores, null!));
    }
}
