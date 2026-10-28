using AgentSpeed.Core.Money;

namespace AgentSpeed.Presentation.Cost;

/// <summary>One row of the currency picker (DESIGN 9, Currency): code, localized name and symbol.</summary>
/// <param name="Group">The group's title; empty while searching, when the groups collapse into one ranked list.</param>
/// <param name="IsCurrent">The selected display currency (it carries the check).</param>
public sealed record CurrencyPickerRow(string Code, string Name, string Symbol, string Group, bool IsCurrent)
{
    /// <summary>How accessibility reads the row: <c>TWD, 新臺幣</c>.</summary>
    public string AccessibleName => $"{Code}, {Name}";
}

/// <summary>The rows and keyboard movement of the currency picker (DESIGN 9, Currency).</summary>
public static class CurrencyPickerList
{
    public static bool IsSearching(string? query) => !string.IsNullOrWhiteSpace(query);

    /// <summary>
    /// <c>settings.currency.common</c> (USD, the UI language's home currencies, the other common ranks) then
    /// <c>settings.currency.all</c> by localized name; while searching, one ranked list of the matches (code first).
    /// </summary>
    public static IReadOnlyList<CurrencyPickerRow> Rows(CostDisplay display, string? query)
    {
        ArgumentNullException.ThrowIfNull(display);
        var table = display.Currencies;
        var language = display.Language;
        var current = display.Currency.Code;
        if (IsSearching(query))
        {
            return [.. table.Search(query!, language, display.Name, display.EnglishName).Select(c => Row(display, c, string.Empty, current))];
        }

        var loc = display.Localizer;
        var common = loc["settings.currency.common"];
        var all = loc["settings.currency.all"];
        return
        [
            .. table.CommonGroup(language).Select(c => Row(display, c, common, current)),
            .. table.OtherGroup(language, display.Name).Select(c => Row(display, c, all, current)),
        ];
    }

    /// <summary>The row highlighted when the list opens: the current currency when listed, else the first; -1 when empty.</summary>
    public static int InitialIndex(IReadOnlyList<CurrencyPickerRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].IsCurrent)
            {
                return i;
            }
        }

        return rows.Count > 0 ? 0 : -1;
    }

    /// <summary>
    /// ↑ / ↓: moves the highlight by <paramref name="step"/> and stops at either end. Without a highlight, ↓ starts at the
    /// first row and ↑ at the last; -1 when the list is empty.
    /// </summary>
    public static int Move(int index, int step, int count)
    {
        if (count <= 0)
        {
            return -1;
        }

        var from = index >= 0 && index < count ? index : step > 0 ? -1 : count;
        return Math.Clamp(from + step, 0, count - 1);
    }

    private static CurrencyPickerRow Row(CostDisplay display, Currency currency, string group, string current) =>
        new(currency.Code, display.Name(currency), currency.SymbolFor(display.Language), group, currency.Code == current);
}
