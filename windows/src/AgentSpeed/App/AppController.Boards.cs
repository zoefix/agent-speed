using AgentSpeed.Boards.Live;
using AgentSpeed.Interop;
using AgentSpeed.Menus;
using AgentSpeed.Presentation.Boards;
using AgentSpeed.Presentation.Layout;
using AgentSpeed.Presentation.Menus;

namespace AgentSpeed.App;

/// <summary>The leaderboard boards (DESIGN 2c): their manager, menu and the board commands of <see cref="IAppCommands"/>.</summary>
internal sealed partial class AppController : IBoardHost
{
    private BoardManager? _boards;

    private void StartBoards()
    {
        _boards = new BoardManager(_ui, _orbClock!, _settings, _theme, _speed, _loc, this, OrbDiscs) { Titles = _titles, Cost = _cost.Display };
        _boards.Apply();
    }

    /// <summary>The shown orbs' discs in physical pixels: boards keep clear of them.</summary>
    private IReadOnlyList<OrbDisc> OrbDiscs()
    {
        if (_orbs is null)
        {
            return [];
        }

        var discs = new List<OrbDisc>();
        foreach (var (id, center) in _orbs.Centers)
        {
            if (Settings.Orbs.Find(id) is { Visible: true } orb)
            {
                var scale = Monitors.FromPoint(new POINT((int)center.X, (int)center.Y))?.Scale ?? 1;
                discs.Add(new OrbDisc(center, orb.RadiusPx(scale)));
            }
        }

        return discs;
    }

    public void ShowBoardMenu(string boardId)
    {
        if (_menu is null || _quitting)
        {
            return;
        }

        HideTooltip();
        if (BoardMenu.Build(this, boardId, Settings, SourceAvailability.From(_speed.Snapshot.Sources), _loc, _remoteHosts?.MenuStates(_loc)) is { } menu)
        {
            _menu.Show(menu);
        }
    }

    /// <summary>The dashboard footer's boards menu: <c>boards.show</c> / <c>boards.hide</c>, <c>boards.add</c>.</summary>
    private void ShowDashboardBoardsMenu()
    {
        var level = new MenuLevel([.. QuickMenuModel.BoardRows(Settings.Boards, _loc)]);
        _dashboard?.ShowBoardsMenu(MenuBuilder.Build(level, this, _loc));
    }

    public void OpenBoardSettings(string? boardId)
    {
        OpenSettings("orb");
        _settingsModel?.Boards?.Select(boardId);
    }

    /// <summary>A new board next to the orb or board it came from (else at the main display's corner).</summary>
    public void AddBoard(string? fromId)
    {
        BoardObstacle? origin = null;
        if (fromId is not null && _boards?.SquareOf(fromId) is { } square)
        {
            origin = square;
        }
        else if (fromId is not null && _orbs?.Centers.TryGetValue(fromId, out var center) == true && Settings.Orbs.Find(fromId) is { } orb)
        {
            var scale = Monitors.FromPoint(new POINT((int)center.X, (int)center.Y))?.Scale ?? 1;
            origin = BoardObstacle.Disc(center, orb.RadiusPx(scale));
        }

        _boards?.Add(origin);
    }

    public void RemoveBoard(string boardId)
    {
        var owner = _settingsWindow is { IsActive: true } ? _settingsWindow : null;
        if (Settings.Boards.Find(boardId) is not { } board || !BoardMenu.ConfirmRemove(owner, board, _loc))
        {
            return;
        }

        UpdateSettings(s => s with { Boards = s.Boards.Remove(boardId) });
    }
}
