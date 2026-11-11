using AgentSpeed.Controls;
using AgentSpeed.Core.Quota.Network;

namespace AgentSpeed.Settings.Sources;

/// <summary>
/// ZCode's key and region under its switch (DESIGN 5; QUOTAS 9.8): a pasted key goes to Credential Manager and the field
/// then reads as saved with Remove, never showing the key again; the region picks z.ai or bigmodel.cn.
/// </summary>
internal sealed class ZcodeFieldsViewModel : ObservableObject
{
    private readonly Func<IQuotaSettingsActions?> _actions;
    private bool _saved;
    private bool? _expected;
    private ZaiRegion _region;

    public ZcodeFieldsViewModel(Func<IQuotaSettingsActions?> actions)
    {
        _actions = actions;
        RemoveCommand = new RelayCommand(() =>
        {
            _actions()?.RemoveZcodeKey();
            Expect(false);
        });
    }

    /// <summary>A key is in Credential Manager.</summary>
    public bool IsSaved { get => _saved; private set => Set(ref _saved, value); }

    public RelayCommand RemoveCommand { get; }

    /// <summary>The picker's choice: 0 for z.ai, 1 for bigmodel.cn.</summary>
    public int RegionIndex
    {
        get => _region == ZaiRegion.BigmodelCn ? 1 : 0;
        set
        {
            var region = value == 1 ? ZaiRegion.BigmodelCn : ZaiRegion.Global;
            if (value is not (0 or 1) || region == _region)
            {
                return;
            }

            _region = region;
            OnPropertyChanged();
            _actions()?.SetZcodeRegion(region);
        }
    }

    /// <summary>Saves a pasted key; false when it is not a key or Credential Manager refused it.</summary>
    public bool Save(string text)
    {
        if (_actions()?.SaveZcodeKey(text) != true)
        {
            return false;
        }

        Expect(true);
        return true;
    }

    public void Update(ZaiRegion region, bool saved)
    {
        if (region != _region)
        {
            _region = region;
            OnPropertyChanged(nameof(RegionIndex));
        }

        // A key saved or removed here reads at once; the quotas confirm it on their next publish.
        if (_expected is { } expected && expected != saved)
        {
            return;
        }

        _expected = null;
        IsSaved = saved;
    }

    private void Expect(bool saved)
    {
        _expected = saved;
        IsSaved = saved;
    }
}
