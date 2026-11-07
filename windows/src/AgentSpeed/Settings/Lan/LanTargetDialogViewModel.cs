using System.Windows.Input;
using System.Windows.Threading;
using AgentSpeed.Controls;
using AgentSpeed.Core.Lan;
using AgentSpeed.Lan;
using AgentSpeed.Presentation.Lan;
using AgentSpeed.Presentation.Localization;

namespace AgentSpeed.Settings.Lan;

internal enum LanTestState
{
    Idle,
    Running,
    Succeeded,
    Failed,
}

/// <summary>
/// The Add / Edit sheet of a target (DESIGN 14): kind, address (checked by LAN 5.1 as it is typed, after a 0.3 s pause),
/// name, key, and <i>Send Test</i> with its result inline. Save is enabled while the address is valid.
/// </summary>
internal sealed class LanTargetDialogViewModel : ObservableObject
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(0.3);

    private readonly LanController _lan;
    private readonly Localizer _loc;
    private readonly LanTarget? _editing;
    private readonly DispatcherTimer _validate;
    private LanTargetKind _kind;
    private string _address;
    private string _name;
    private string _key;
    private string? _addressError;
    private bool _broadcast;
    private LanTestState _testState;
    private string _testText = string.Empty;
    private CancellationTokenSource? _test;

    public LanTargetDialogViewModel(LanController lan, Localizer loc, Dispatcher dispatcher, LanTarget? editing)
    {
        _lan = lan;
        _loc = loc;
        _editing = editing;
        _kind = editing?.Kind ?? LanTargetKind.Udp;
        _address = editing?.Address ?? string.Empty;
        _name = editing?.Name ?? string.Empty;
        _key = editing?.Key ?? string.Empty;
        _validate = new DispatcherTimer(Debounce, DispatcherPriority.Background, (_, _) => Validate(), dispatcher);
        _validate.Stop();
        TestCommand = new RelayCommand(() => _ = TestAsync(), () => CanSave && _testState != LanTestState.Running && _lan.NetworkEnabled);
        Validate();
    }

    public bool IsEditing => _editing is not null;

    public string Title => _editing is { } target ? _loc.Format("lan.sheet.edit", target.DisplayName) : _loc["lan.sheet.add"];

    public string SaveText => _loc[IsEditing ? "remote.save" : "remote.addButton"];

    public LanTargetKind Kind => _kind;

    public bool KindUdp
    {
        get => _kind == LanTargetKind.Udp;
        set
        {
            if (value)
            {
                SetKind(LanTargetKind.Udp);
            }
        }
    }

    public bool KindHttp
    {
        get => _kind == LanTargetKind.Http;
        set
        {
            if (value)
            {
                SetKind(LanTargetKind.Http);
            }
        }
    }

    public string KindHint => _loc[_kind == LanTargetKind.Udp ? "lan.target.kind.udp.hint" : "lan.target.kind.http.hint"];

    public string AddressPlaceholder => _loc[_kind == LanTargetKind.Udp ? "lan.target.address.placeholder.udp" : "lan.target.address.placeholder.http"];

    public string Address
    {
        get => _address;
        set
        {
            if (Set(ref _address, value))
            {
                OnPropertyChanged(nameof(CanSave));
                ResetTest();
                _validate.Stop();
                _validate.Start();
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>The address's problem, shown after the pause; null while it is valid or empty.</summary>
    public string? AddressError { get => _addressError; private set => Set(ref _addressError, value); }

    /// <summary>A UDP broadcast address: <c>lan.target.broadcast</c> under the field.</summary>
    public bool IsBroadcast { get => _broadcast; private set => Set(ref _broadcast, value); }

    public string Name { get => _name; set => Set(ref _name, value); }

    public string Key
    {
        get => _key;
        set
        {
            if (Set(ref _key, value))
            {
                ResetTest();
            }
        }
    }

    public bool CanSave => Parsed is not null;

    public ICommand TestCommand { get; }

    public LanTestState TestState
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

    public bool IsTesting => _testState == LanTestState.Running;

    public bool HasTestResult => _testState is LanTestState.Succeeded or LanTestState.Failed;

    public string TestText { get => _testText; private set => Set(ref _testText, value); }

    /// <summary>Why <i>Send Test</i> is unavailable (the master switch is off), for its tooltip.</summary>
    public string? TestUnavailableReason => _lan.NetworkEnabled ? null : _loc["settings.network.offHint"];

    private LanTargetAddress? Parsed => LanTargetAddress.Parse(_kind, LanText.Trim(_address));

    /// <summary>Saves the target (an enabled one sends from the next tick); false while the address is invalid.</summary>
    public bool Save()
    {
        Validate();
        if (LanSettings.NewTarget(_editing?.Id, _name, _kind, _address, _key.Length == 0 ? null : _key, _editing?.Enabled ?? true) is not { } target)
        {
            return false;
        }

        _lan.SaveTarget(target);
        return true;
    }

    public void CancelTest()
    {
        _test?.Cancel();
        _validate.Stop();
    }

    private void SetKind(LanTargetKind kind)
    {
        if (_kind == kind)
        {
            return;
        }

        _kind = kind;
        ResetTest();
        Validate();
        OnPropertyChanged(nameof(KindUdp));
        OnPropertyChanged(nameof(KindHttp));
        OnPropertyChanged(nameof(KindHint));
        OnPropertyChanged(nameof(AddressPlaceholder));
        OnPropertyChanged(nameof(CanSave));
        CommandManager.InvalidateRequerySuggested();
    }

    private void Validate()
    {
        _validate.Stop();
        var text = LanText.Trim(_address);
        var parsed = LanTargetAddress.Parse(_kind, text, out var error);
        AddressError = parsed is not null || text.Length == 0
            ? null
            : error switch
            {
                LanAddressError.Port => _loc["lan.target.address.port"],
                LanAddressError.Https => _loc["lan.target.address.https"],
                _ => _loc["lan.target.address.invalid"],
            };
        IsBroadcast = _kind == LanTargetKind.Udp && parsed is { IsBroadcast: true };
    }

    private void ResetTest()
    {
        _test?.Cancel();
        if (_testState != LanTestState.Idle)
        {
            TestState = LanTestState.Idle;
            TestText = string.Empty;
        }
    }

    private async Task TestAsync()
    {
        if (Parsed is null)
        {
            return;
        }

        _test?.Cancel();
        var test = _test = new CancellationTokenSource();
        TestState = LanTestState.Running;
        TestText = _loc["remote.test.running"];
        var status = await _lan.TestAsync(_kind, LanText.Trim(_address), _key.Length == 0 ? null : LanText.Field(_key, 1, LanTarget.KeyLimit), test.Token);
        if (test.IsCancellationRequested || !ReferenceEquals(test, _test))
        {
            return;
        }

        TestText = LanStatusText.Test(status, Environment.TickCount64, _loc);
        TestState = status.Kind == LanTargetStatusKind.Ok ? LanTestState.Succeeded : LanTestState.Failed;
    }
}
