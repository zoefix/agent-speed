using AgentSpeed.Core.Remote;

namespace AgentSpeed.Core.Tests.Remote;

/// <summary>The bundled relay executables and their digests (REMOTE 3.1, 4.2).</summary>
public sealed class RemoteRelayBundleTests
{
    [Fact]
    public void FindsBothArchitecturesWithTheirDigests()
    {
        using var relays = new FakeRelayBundle(version: "1.7.0");
        var x86 = relays.Bundle.Executable(RemoteArch.X86_64);
        var arm = relays.Bundle.Executable(RemoteArch.Aarch64);
        Assert.NotNull(x86);
        Assert.NotNull(arm);
        Assert.Equal(TestBytes.Sha256(File.ReadAllBytes(relays.Folder.Combine("agent-speed-relay-linux-x86_64"))), x86.Sha256);
        Assert.NotEqual(x86.Sha256, arm.Sha256);
        Assert.Equal("agent-speed-relay-1.7.0-x86_64-" + x86.Sha256[..12], x86.InstalledName);
        Assert.StartsWith("agent-speed-relay-1.7.0-aarch64-", arm.InstalledName, StringComparison.Ordinal);
        Assert.Equal(600_000, x86.Size);
        Assert.Equal(RemoteArch.Aarch64, arm.Arch);
        Assert.Equal(relays.Folder.Combine("agent-speed-relay-linux-aarch64"), arm.Path);
        Assert.Same(x86, relays.Bundle.Executable(RemoteArch.X86_64));
    }

    [Fact]
    public void OverrideFolderComesFromTheEnvironment()
    {
        using var relays = new FakeRelayBundle();
        var bundle = RemoteRelayBundle.Current("1.7.0", new Dictionary<string, string> { [RemoteRelayBundle.OverrideVariable] = relays.Folder.Path });
        Assert.Equal(relays.Folder.Path, bundle.Directory);
        Assert.Equal(relays.X86.Sha256, bundle.Executable(RemoteArch.X86_64)?.Sha256);
        Assert.Equal("AGENT_SPEED_RELAY_DIR", RemoteRelayBundle.OverrideVariable);

        var embedded = RemoteRelayBundle.Current("1", new Dictionary<string, string>(), typeof(RemoteRelayBundleTests).Assembly);
        Assert.Null(embedded.Directory);
        Assert.Same(typeof(RemoteRelayBundleTests).Assembly, embedded.Resources);
        Assert.Same(typeof(RemoteRelayBundleTests).Assembly,
            RemoteRelayBundle.Current("1", new Dictionary<string, string> { [RemoteRelayBundle.OverrideVariable] = "" }, typeof(RemoteRelayBundleTests).Assembly).Resources);
        var overridden = RemoteRelayBundle.Current("1", new Dictionary<string, string> { [RemoteRelayBundle.OverrideVariable] = relays.Folder.Path },
            typeof(RemoteRelayBundleTests).Assembly);
        Assert.Null(overridden.Resources);
        Assert.Equal(relays.X86.Sha256, overridden.Executable(RemoteArch.X86_64)?.Sha256);
        Assert.Null(new RemoteRelayBundle(relays.Folder.Combine("missing"), "1").Executable(RemoteArch.X86_64));
        Assert.Null(new RemoteRelayBundle(null, "1").Executable(RemoteArch.X86_64));
    }

    [Fact]
    public void EmbeddedRelaysAreReadFromTheResourcesAndCheckedAgainstTheirDigest()
    {
        var assembly = typeof(RemoteRelayBundleTests).Assembly;
        var bundle = RemoteRelayBundle.FromResources(assembly, "1.7.0");
        var x86 = bundle.Executable(RemoteArch.X86_64);
        Assert.NotNull(x86);
        var bytes = "fake embedded relay x86_64\n"u8.ToArray();
        Assert.Equal(TestBytes.Sha256(bytes), x86.Sha256);
        Assert.Equal(new StreamReader(assembly.GetManifestResourceStream("AgentSpeed.Relay.linux-x86_64.sha256")!).ReadToEnd().Trim(), x86.Sha256);
        Assert.Equal(bytes.Length, x86.Size);
        Assert.Null(x86.Path);
        Assert.Equal("agent-speed-relay-1.7.0-x86_64-" + x86.Sha256[..12], x86.InstalledName);
        using (var stream = x86.Open())
        using (var copy = new MemoryStream())
        {
            stream.CopyTo(copy);
            Assert.Equal(bytes, copy.ToArray());
        }

        Assert.Same(x86, bundle.Executable(RemoteArch.X86_64));
        Assert.Null(bundle.Executable(RemoteArch.Aarch64));
        Assert.Null(RemoteRelayBundle.FromResources(typeof(RemoteRelayBundle).Assembly, "1").Executable(RemoteArch.X86_64));
    }

    [Fact]
    public void DigestFollowsAChangedFile()
    {
        using var relays = new FakeRelayBundle();
        File.WriteAllText(relays.Folder.Combine("agent-speed-relay-linux-x86_64"), "replaced");
        var changed = relays.Bundle.Executable(RemoteArch.X86_64);
        Assert.Equal(TestBytes.Sha256("replaced"), changed?.Sha256);
        Assert.Equal(8, changed?.Size);
    }

    [Fact]
    public void EmptyFilesAndFoldersAreNoRelay()
    {
        using var folder = new TempDirectory();
        File.WriteAllBytes(folder.Combine("agent-speed-relay-linux-x86_64"), []);
        Directory.CreateDirectory(folder.Combine("agent-speed-relay-linux-aarch64"));
        var bundle = new RemoteRelayBundle(folder.Path, "1");
        Assert.Null(bundle.Executable(RemoteArch.X86_64));
        Assert.Null(bundle.Executable(RemoteArch.Aarch64));
    }
}
