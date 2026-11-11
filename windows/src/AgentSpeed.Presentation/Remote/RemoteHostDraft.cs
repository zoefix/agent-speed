using System.Globalization;
using AgentSpeed.Core.Remote;

namespace AgentSpeed.Presentation.Remote;

/// <summary>The Add / Edit dialog's fields (DESIGN 13) and their REMOTE 2.1 checks, as the user types.</summary>
public sealed record RemoteHostDraft
{
    private const string PreviewId = "00000000-0000-4000-8000-000000000000";

    public RemoteHostDraft(RemoteHost? editing = null)
    {
        Original = editing;
        Destination = editing?.Destination ?? string.Empty;
        Name = editing?.Name ?? string.Empty;
        RelayFolder = editing?.RelayFolder ?? string.Empty;
        QuotaClaudeCli = editing?.QuotaClaudeCli ?? false;
    }

    /// <summary>The host being edited; null while adding.</summary>
    public RemoteHost? Original { get; }

    public string Destination { get; init; }

    public string Name { get; init; }

    public string RelayFolder { get; init; }

    /// <summary>The host's Claude Code status line bridge (REMOTE 11.3), off for a new host.</summary>
    public bool QuotaClaudeCli { get; init; }

    public bool IsEditing => Original is not null;

    /// <summary>Spaces and line breaks around a pasted destination are dropped; inside it they stay errors.</summary>
    public string CleanDestination => Destination.Trim();

    public string CleanFolder => RelayFolder.Trim();

    public RemoteFieldIssue? DestinationIssue => RemoteHostValidation.DestinationIssue(CleanDestination);

    /// <summary>An empty folder is the default one.</summary>
    public RemoteFieldIssue? FolderIssue => CleanFolder.Length == 0 ? null : RemoteHostValidation.FolderIssue(CleanFolder);

    /// <summary>The name keeps at most 32 characters after trimming (REMOTE 2.1).</summary>
    public bool NameIsTooLong => new StringInfo(Name.Trim()).LengthInTextElements > RemoteHostValidation.NameLimit;

    public bool IsValid => DestinationIssue is null && FolderIssue is null && !NameIsTooLong;

    /// <summary>The folder in effect: the one typed, else <c>~/.cache/agent-speed</c>.</summary>
    public string EffectiveFolder => CleanFolder.Length == 0 ? RemoteHost.DefaultRelayFolder : CleanFolder;

    /// <summary>The host the fields describe: a new id (enabled) while adding, else the edited host's id, switch and unknown members; the bridge as the draft has it.</summary>
    public RemoteHost Host(string? newId = null) => new(CleanDestination, Original?.Id ?? newId ?? RemoteHost.NewId())
    {
        Name = Name,
        Enabled = Original?.Enabled ?? true,
        RelayFolder = CleanFolder.Length == 0 ? null : CleanFolder,
        QuotaClaudeCli = QuotaClaudeCli,
        ExtraJson = Original?.ExtraJson,
    };

    /// <summary>The name the host would get without a custom one, made distinct from the other hosts (the name's placeholder).</summary>
    public string AutomaticName(RemoteHostsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (DestinationIssue is not null)
        {
            return string.Empty;
        }

        var unnamed = Host(Original?.Id ?? PreviewId) with { Name = null };
        var preview = IsEditing ? document.Updating(unnamed) : document.Adding(unnamed);
        return preview?.DisplayName(unnamed.Id) ?? unnamed.AutomaticName;
    }
}
