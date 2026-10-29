using System.Diagnostics;
using System.Windows.Threading;
using AgentSpeed.Presentation.Perf;
using AgentSpeed.Theme;

namespace AgentSpeed.Perf;

/// <summary>
/// Runs one score card window for a surface (the dashboard, or all boards): <see cref="PerfCardHover"/>'s timing on a
/// dispatcher timer, the rows' card requests, and an update on every engine tick while a card is open. Rows call
/// <see cref="Hover"/>, <see cref="Leave"/>, <see cref="ShowNow"/> (Space), <see cref="Focus"/> and <see cref="Hide"/> (Esc).
/// A key names a row (the row element, or a board's row slot), never the agent in it: rows are reused as agents re-sort,
/// and the request's <see cref="PerfCardRequest.CardKey"/> tells agents apart.
/// </summary>
internal sealed class PerfCardPresenter : IDisposable
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private readonly PerfCardHover _hover = new();
    private readonly DispatcherTimer _timer;
    private readonly Func<PerfCardAppearance> _appearance;
    private readonly Func<string> _language;
    private readonly Dictionary<object, Func<PerfCardRequest?>> _requests = new();
    private PerfCardWindow? _window;
    private bool _disposed;

    /// <param name="appearance">The current appearance, read whenever the card is drawn.</param>
    /// <param name="languageCode">The app's language, for the card window's line breaking and fonts.</param>
    public PerfCardPresenter(Dispatcher dispatcher, Func<PerfCardAppearance> appearance, Func<string> languageCode)
    {
        _appearance = appearance ?? throw new ArgumentNullException(nameof(appearance));
        _language = languageCode ?? throw new ArgumentNullException(nameof(languageCode));
        _timer = new DispatcherTimer(DispatcherPriority.Input, dispatcher);
        _timer.Tick += (_, _) => Run(_hover.Tick(Now));
    }

    /// <summary>The row whose card is open, or null.</summary>
    public object? ShownKey => _hover.Shown;

    public bool IsShown => _hover.IsShown;

    /// <summary>Raised when the open card's row changes (null: closed), so the row can say so to screen readers.</summary>
    public event EventHandler? ShownChanged;

    private static double Now => Clock.Elapsed.TotalSeconds;

    /// <summary>The pointer entered a row; <paramref name="request"/> builds its card when it is due.</summary>
    public void Hover(object key, PerfCardHost host, Func<PerfCardRequest?> request)
    {
        _requests[key] = request;
        Run(_hover.Hover(key, host, Now));
    }

    public void Leave(object key) => Run(_hover.Leave(key, Now));

    /// <summary>Space on a focused row, or the row's card action: the card opens at once.</summary>
    public void ShowNow(object key, PerfCardHost host, Func<PerfCardRequest?> request)
    {
        _requests[key] = request;
        Run(_hover.Show(key, host));
    }

    /// <summary>The keyboard focus moved to a row (null: away from the rows); an open card follows it.</summary>
    public void Focus(object? key, PerfCardHost host, Func<PerfCardRequest?>? request)
    {
        if (key is not null && request is not null)
        {
            _requests[key] = request;
        }

        Run(_hover.Focus(key, host));
    }

    /// <summary>Esc, the surface closing, or its rows going away.</summary>
    public void Hide() => Run(_hover.Hide());

    /// <summary>The engine tick: the open card takes its row's new content, its number following DESIGN 12's motion.</summary>
    public void Refresh()
    {
        if (_hover.Shown is { } key)
        {
            Present(PerfCardAction.None, key);
        }
    }

    /// <summary>A row went away: its card closes if it was open.</summary>
    public void Forget(object key)
    {
        _requests.Remove(key);
        if (Equals(_hover.Shown, key))
        {
            Hide();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _window?.Close();
        _window = null;
        _requests.Clear();
    }

    private void Run(PerfCardAction action)
    {
        if (_disposed)
        {
            return;
        }

        switch (action)
        {
            case PerfCardAction.Show or PerfCardAction.Switch when _hover.Shown is { } key:
                Present(action, key);
                ShownChanged?.Invoke(this, EventArgs.Empty);
                break;
            case PerfCardAction.Hide:
                _window?.Dismiss();
                ShownChanged?.Invoke(this, EventArgs.Empty);
                break;
        }

        Schedule();
    }

    private void Present(PerfCardAction action, object key)
    {
        if (!_requests.TryGetValue(key, out var build) || build() is not { } request)
        {
            Run(_hover.Hide());
            return;
        }

        _window ??= new PerfCardWindow();
        _window.Present(action, request, _appearance(), AppFonts.XmlLanguageFor(_language()));
    }

    private void Schedule()
    {
        _timer.Stop();
        if (_hover.NextDue is { } due)
        {
            _timer.Interval = TimeSpan.FromSeconds(Math.Max(0.001, due - Now));
            _timer.Start();
        }
    }
}
