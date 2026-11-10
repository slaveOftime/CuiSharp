using CuiSharp.Interop;

namespace CuiSharp;

/// <summary>
/// The text CUI's date components show, for a language other than English: the table the
/// surface published, and the way to publish one.
/// </summary>
public unsafe ref partial struct Ui
{
    /// <summary>The table this surface published, or null for English.</summary>
    public readonly Localization? Localization => _frame.Localization;

    /// <summary>
    /// Sets the month, weekday and "Today" names the date components show, and returns to English
    /// with null.
    /// </summary>
    /// <remarks>
    /// CUI keeps the pointer and reads it every frame, so the published table has to outlive the
    /// call: from here on the surface owns it, the way it owns the theme (<see cref="CuiSurface"/>
    /// and <see cref="CuiApp"/> both keep one), and its unmanaged block is rebuilt only when a name
    /// changes. One call is enough — every later frame reads the same table — and the language
    /// picker that calls it every frame pays one reference comparison. A name left null falls back
    /// to English, so a partial table is fine.
    /// </remarks>
    /// <example><code>
    /// ui.SetLocalization(_language);   // a Localization, or null for English
    /// ui.MonthCalendar("month", ref month, events, ref selected);
    /// </code></example>
    public void SetLocalization(Localization? table)
    {
        nint block = table is null ? 0 : table.Publish();
        if (block == _frame.LocalizationBlock && ReferenceEquals(table, _frame.Localization)) return;

        _frame.Localization = table;
        _frame.LocalizationBlock = block;
        Native.SetStrings(Handle, block);
    }
}
