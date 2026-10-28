using AgentSpeed.Presentation.Motion;

namespace AgentSpeed.Presentation.Orb;

/// <param name="Current">Character rolling in (or resting).</param>
/// <param name="Previous">Character rolling out.</param>
/// <param name="Progress">Linear progress 0..1 of this slot's roll; apply <see cref="RollingText.Ease"/>.</param>
/// <param name="Direction">+1 rolls up (new character enters from below), -1 rolls down.</param>
/// <param name="PreviousOffset">Where the outgoing character starts, in units of the roll distance (0 = resting).</param>
/// <param name="PreviousAlpha">Opacity the outgoing character starts from.</param>
public readonly record struct RollingSlot(
    char? Current, char? Previous, double Progress, int Direction, double PreviousOffset = 0, double PreviousAlpha = 1)
{
    public bool IsChanging => Progress < 1 && Current != Previous;

    /// <summary>Vertical offset of the outgoing character, in units of the roll distance.</summary>
    public double PreviousPosition(double eased) => PreviousOffset + (-Direction - PreviousOffset) * eased;

    public double PreviousOpacity(double eased) => PreviousAlpha * (1 - eased);

    /// <summary>Vertical offset of the incoming character, in units of the roll distance.</summary>
    public double CurrentPosition(double eased) => IsChanging ? Direction * (1 - eased) : 0;

    public double CurrentOpacity(double eased) => IsChanging ? eased : 1;
}

/// <summary>
/// Odometer-style text transition. Slots are aligned from the right so units stay in place. Only characters that
/// change roll (right-most first); a new value arriving mid-roll retargets each slot from where it currently is.
/// </summary>
public sealed class RollingText
{
    public const double Duration = 0.2;
    public const double Stagger = 0.015;

    private readonly List<SlotState> _slots = new();
    private string _current = string.Empty;

    public string Current => _current;

    public int SlotCount => _slots.Count;

    public static double Ease(double progress) => Easing.OutCubic(progress);

    public void Set(string text, int direction, double nowSeconds, bool animate)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text == _current)
        {
            return;
        }

        direction = direction >= 0 ? 1 : -1;
        var count = Math.Max(text.Length, _slots.Count);
        while (_slots.Count < count)
        {
            _slots.Add(SlotState.Empty);
        }

        var changed = 0;
        for (var k = 0; k < count; k++)
        {
            var target = CharFromRight(text, k);
            var state = _slots[k];
            if (!animate)
            {
                _slots[k] = SlotState.Resting(target, direction);
                continue;
            }

            if (target == state.To)
            {
                continue;
            }

            var eased = state.IsAnimating(nowSeconds) ? Ease(state.Progress(nowSeconds)) : 1;
            char? from;
            double fromOffset;
            double fromAlpha;
            if (eased < 0.5 && state.From is not null && state.From != state.To)
            {
                from = state.From;
                fromOffset = state.FromOffset + (-state.Direction - state.FromOffset) * eased;
                fromAlpha = state.FromAlpha * (1 - eased);
            }
            else
            {
                from = state.To;
                fromOffset = eased < 1 ? state.Direction * (1 - eased) : 0;
                fromAlpha = eased;
            }

            _slots[k] = new SlotState(from, target, nowSeconds + changed * Stagger, direction, fromOffset, fromAlpha);
            changed++;
        }

        _current = text;
        Trim(nowSeconds);
    }

    public RollingSlot Slot(int indexFromRight, double nowSeconds)
    {
        if (indexFromRight < 0 || indexFromRight >= _slots.Count)
        {
            return new RollingSlot(null, null, 1, 1);
        }

        var state = _slots[indexFromRight];
        var progress = state.From == state.To ? 1 : state.Progress(nowSeconds);
        return new RollingSlot(state.To, state.From, progress, state.Direction, state.FromOffset, state.FromAlpha);
    }

    public bool IsAnimating(double nowSeconds)
    {
        foreach (var slot in _slots)
        {
            if (slot.From != slot.To && slot.IsAnimating(nowSeconds))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Drops leading slots (left side) that hold nothing and have finished rolling out.</summary>
    private void Trim(double nowSeconds)
    {
        while (_slots.Count > 0)
        {
            var last = _slots[^1];
            if (last.To is not null || (last.From is not null && last.IsAnimating(nowSeconds)))
            {
                break;
            }

            _slots.RemoveAt(_slots.Count - 1);
        }
    }

    private static char? CharFromRight(string text, int indexFromRight)
    {
        var index = text.Length - 1 - indexFromRight;
        return index >= 0 && index < text.Length ? text[index] : null;
    }

    private readonly record struct SlotState(char? From, char? To, double Start, int Direction, double FromOffset, double FromAlpha)
    {
        public static SlotState Empty => Resting(null, 1);

        public static SlotState Resting(char? c, int direction) => new(c, c, double.NegativeInfinity, direction, 0, 1);

        public double Progress(double now) => Math.Clamp((now - Start) / Duration, 0, 1);

        public bool IsAnimating(double now) => now - Start < Duration;
    }
}
