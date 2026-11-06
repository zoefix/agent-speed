using System.Windows.Input;
using AgentSpeed.Controls;
using AgentSpeed.Core.Remote;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Remote;
using AgentSpeed.Remote;

namespace AgentSpeed.Settings.Remote;

/// <summary>Where <i>Test Connection</i> stands.</summary>
internal enum RemoteTestState
{
    Idle,
    Running,
    Reachable,
    Retrying,
    NeedsUser,
}

/// <summary>
/// The Add / Edit dialog (DESIGN 13): the destination checked as you type (REMOTE 2.1), the name with the automatic name as its
/// placeholder, Advanced with the relay folder, and <i>Test Connection</i> with its result inline. Save stays disabled while a
/// field is invalid; editing a field clears a test result.
/// </summary>
internal sealed class RemoteHostDialogViewModel : ObservableObject
{
    private readonly RemoteHostsModel _hosts;
    private readonly Localizer _loc;
    private RemoteHostDraft _draft;
    private bool _destinationEdited;
    private bool _advancedExpanded;
    private RemoteTestState _testState;
    private string _testText = string.Empty;
    private RemoteConnectionTest? _test;

    public RemoteHostDialogViewModel(RemoteHostsModel hosts, Localizer loc, RemoteHost? editing)
    {
        _hosts = hosts;
        _loc = loc;
        _draft = new RemoteHostDraft(editing);
        _destinationEdited = editing is not null;
        _advancedExpanded = !string.IsNullOrEmpty(editing?.RelayFolder);
        TestCommand = new RelayCommand(RunTest, () => TestUnavailableReason is null && _testState != RemoteTestState.Running);
        ToggleAdvancedCommand = new RelayCommand(() => AdvancedExpanded = !AdvancedExpanded);
    }

    /// <summary>Saved (the dialog closes).</summary>
    public event EventHandler? Saved;

    public ICommand TestCommand { get; }

    public ICommand ToggleAdvancedCommand { get; }

    public bool IsEditing => _draft.IsEditing;

    public string Title => _draft.Original is { } original
        ? _loc.Format("remote.sheet.edit", _hosts.Document.DisplayName(original.Id) ?? original.AutomaticName)
        : _loc["remote.sheet.add"];

    public string SaveText => _loc[IsEditing ? "remote.save" : "remote.addButton"];

    public string Destination
    {
        get => _draft.Destination;
        set
        {
            if (value == _draft.Destination)
            {
                return;
            }

            _draft = _draft with { Destination = value };
            _destinationEdited = true;
            Changed();
        }
    }

    public string Name
    {
        get => _draft.Name;
        set
        {
            if (value == _draft.Name)
            {
                return;
            }

            _draft = _draft with { Name = value };
            Changed();
        }
    }

    public string RelayFolder
    {
        get => _draft.RelayFolder;
        set
        {
            if (value == _draft.RelayFolder)
            {
                return;
            }

            _draft = _draft with { RelayFolder = value };
            Changed();
        }
    }

    /// <summary>The destination's problem under the field, once the user typed in it (or while editing a host).</summary>
    public string? DestinationError => _destinationEdited && _draft.DestinationIssue is { } issue
        ? RemoteStatusText.FieldError(issue, folder: false, _loc)
        : null;

    public string? NameError => _draft.NameIsTooLong ? _loc.Format("remote.error.tooLong", RemoteHostValidation.NameLimit) : null;

    public string? FolderError => _draft.FolderIssue is { } issue ? RemoteStatusText.FieldError(issue, folder: true, _loc) : null;

    /// <summary>The automatic name the host would get (the name field's placeholder).</summary>
    public string NamePlaceholder => _draft.AutomaticName(_hosts.Document);

    public string DestinationPlaceholder => _loc["remote.destination.placeholder"];

    public string FolderPlaceholder => RemoteHost.DefaultRelayFolder;

    /// <summary>The folder in effect, beside a collapsed Advanced.</summary>
    public string AdvancedSummary => _draft.EffectiveFolder;

    public bool AdvancedExpanded
    {
        get => _advancedExpanded;
        set
        {
            if (Set(ref _advancedExpanded, value))
            {
                OnPropertyChanged(nameof(AdvancedCollapsed));
            }
        }
    }

    public bool AdvancedCollapsed => !_advancedExpanded;

    public bool CanSave => _draft.IsValid && (IsEditing || _hosts.Document.CanAdd);

    public RemoteTestState TestState
    {
        get => _testState;
        private set
        {
            if (Set(ref _testState, value))
            {
                OnPropertyChanged(nameof(IsTesting));
                OnPropertyChanged(nameof(HasTestResult));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsTesting => _testState == RemoteTestState.Running;

    public bool HasTestResult => _testState is RemoteTestState.Reachable or RemoteTestState.Retrying or RemoteTestState.NeedsUser;

    /// <summary><c>可以連線 · Linux x86_64</c>, or the sentence the connection would show.</summary>
    public string TestText { get => _testText; private set => Set(ref _testText, value); }

    /// <summary>Why <i>Test Connection</i> is unavailable: network off, or the field that is invalid; null when it can run.</summary>
    public string? TestUnavailableReason =>
        !_hosts.NetworkEnabled ? _loc["remote.status.networkOff"]
        : _draft.DestinationIssue is { } destination ? RemoteStatusText.FieldError(destination, folder: false, _loc)
        : _draft.FolderIssue is { } folder ? RemoteStatusText.FieldError(folder, folder: true, _loc)
        : null;

    /// <summary>Adds or saves the host; an enabled one connects at once. False when a field is invalid or the list is full.</summary>
    public bool Save()
    {
        _destinationEdited = true;
        OnPropertyChanged(nameof(DestinationError));
        if (!CanSave)
        {
            return false;
        }

        var host = _draft.Host();
        if (IsEditing)
        {
            _hosts.Update(host);
        }
        else if (!_hosts.Add(host))
        {
            return false;
        }

        CancelTest();
        Saved?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void CancelTest()
    {
        _test?.Cancel();
        _test = null;
    }

    private void RunTest()
    {
        if (TestUnavailableReason is not null)
        {
            return;
        }

        CancelTest();
        TestState = RemoteTestState.Running;
        TestText = _loc["remote.test.running"];
        var destination = _draft.CleanDestination;
        RemoteConnectionTest? test = null;
        test = _hosts.StartTest(_draft.Host(), outcome =>
        {
            if (!ReferenceEquals(test, _test))
            {
                return;
            }

            _test = null;
            if (outcome.IsSupported)
            {
                TestText = _loc.Format("remote.test.ok", outcome.System ?? string.Empty);
                TestState = RemoteTestState.Reachable;
            }
            else
            {
                var status = outcome.Failure ?? RemoteStatus.Unreachable;
                TestText = RemoteStatusText.Base(status, destination, _loc);
                TestState = RemoteDots.Of(status) == RemoteDotKind.Attention ? RemoteTestState.NeedsUser : RemoteTestState.Retrying;
            }
        });
        _test = test;
    }

    private void Changed()
    {
        if (_testState != RemoteTestState.Idle || _test is not null)
        {
            CancelTest();
            TestState = RemoteTestState.Idle;
            TestText = string.Empty;
        }

        foreach (var name in new[]
                 {
                     nameof(DestinationError), nameof(NameError), nameof(FolderError), nameof(NamePlaceholder), nameof(AdvancedSummary),
                     nameof(CanSave), nameof(TestUnavailableReason),
                 })
        {
            OnPropertyChanged(name);
        }

        CommandManager.InvalidateRequerySuggested();
    }
}
