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
    /// changes. One call is enough — a name changed later reaches the next frame by itself, and
    /// every frame reads the same table until then. A name left null falls back to English, so a
    /// partial table is fine.
    /// <para>
    /// A table belongs to one surface: publishing it to a second one throws, because only the
    /// owner is told when a name changes and a block is replaced.
    /// </para>
    /// </remarks>
    /// <example><code>
    /// ui.SetLocalization(_language);   // a Localization, or null for English
    /// ui.MonthCalendar("month", ref month, events, ref selected);
    /// </code></example>
    public void SetLocalization(Localization? table)
    {
        if (table is null)
        {
            if (_frame.Localization is null) return;

            _frame.Localization.Detach(_frame);
            _frame.Localization = null;
            _frame.LocalizationBlock = 0;
            Native.SetStrings(Handle, 0);
            return;
        }

        // Attaching first is what refuses a second surface, and what gives a replaced block a home.
        nint block = table.Attach(_frame);
        if (ReferenceEquals(_frame.Localization, table) && block == _frame.LocalizationBlock) return;

        _frame.Localization?.Detach(_frame);
        _frame.Localization = table;
        _frame.LocalizationBlock = block;
        Native.SetStrings(Handle, block);
    }
}
