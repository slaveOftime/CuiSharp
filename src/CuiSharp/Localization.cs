using System.Runtime.InteropServices;
using System.Text;
using CuiSharp.Interop;

namespace CuiSharp;

/// <summary>
/// The month, weekday and "Today" names the date components show, for another language: the
/// table <see cref="Ui.SetLocalization(Localization?)"/> hands to CUI.
/// <para>
/// CUI keeps the pointer it is given and reads it every frame, so this table cannot live in the
/// frame arena. This class owns one unmanaged block, rebuilt only when a name changes, and the
/// surface that published it keeps this object alive and frees a replaced block at a frame
/// boundary. Every entry is optional: a null falls back to the English name, so a partial table
/// is a complete one.
/// </para>
/// <para>
/// Weeks start on Monday, as in every CUI calendar, so <see cref="Weekdays"/> starts at Monday.
/// A table belongs to one surface: publishing it to a second one throws, because only the surface
/// that owns it is told when a name changes and a block is replaced.
/// </para>
/// </summary>
/// <example><code>
/// using var es = new Localization
/// {
///     Months = ["enero", "febrero", "marzo", "abril", "mayo", "junio",
///               "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"],
///     MonthsShort = ["ene", "feb", "mar", "abr", "may", "jun", "jul", "ago", "sep", "oct", "nov", "dic"],
///     Today = "Hoy",
/// };
///
/// CuiApp.Run(new CuiAppOptions { Title = "Agenda" }, ui =>
/// {
///     ui.SetLocalization(es);   // once: a name changed later reaches the next frame by itself
///     ui.MonthCalendar("month", ref month, events, ref selected);
/// });
/// </code></example>
public sealed unsafe class Localization : IDisposable
{
    private const int MonthSlots = 12;
    private const int WeekdaySlots = 7;

    /// <summary>month, month_short, weekday, weekday_short, weekday_min, today: the slots of a cui_strings.</summary>
    private const int Slots = (MonthSlots * 2) + (WeekdaySlots * 3) + 1;

    private string?[]? _months;
    private string?[]? _monthsShort;
    private string?[]? _weekdays;
    private string?[]? _weekdaysShort;
    private string?[]? _weekdaysMin;
    private string? _today;

    // What the live block holds, slot for slot. A name changed in place between two publishes is a
    // different reference, which is what makes the rebuild compare references instead of text.
    private readonly string?[] _published = new string?[Slots];

    private NativeStrings* _block;

    // The one surface this table is published to. It is what tells a name change where to go, and
    // what makes a second surface a mistake instead of a table two contexts disagree about.
    private UiFrameState? _host;
    private bool _disposed;

    /// <summary>Twelve month names, index 0 = January. Null entries fall back to English.</summary>
    public string?[]? Months
    {
        get => _months;
        set { Check(value, MonthSlots); _months = value; }
    }

    /// <summary>Twelve short month names, index 0 = January. Null entries fall back to English.</summary>
    public string?[]? MonthsShort
    {
        get => _monthsShort;
        set { Check(value, MonthSlots); _monthsShort = value; }
    }

    /// <summary>Seven weekday names, index 0 = Monday. Null entries fall back to English.</summary>
    public string?[]? Weekdays
    {
        get => _weekdays;
        set { Check(value, WeekdaySlots); _weekdays = value; }
    }

    /// <summary>Seven short weekday names, index 0 = Monday. Null entries fall back to English.</summary>
    public string?[]? WeekdaysShort
    {
        get => _weekdaysShort;
        set { Check(value, WeekdaySlots); _weekdaysShort = value; }
    }

    /// <summary>Seven minimal weekday names, index 0 = Monday. Null entries fall back to English.</summary>
    public string?[]? WeekdaysMin
    {
        get => _weekdaysMin;
        set { Check(value, WeekdaySlots); _weekdaysMin = value; }
    }

    /// <summary>The word the "Today" button shows. Null falls back to English.</summary>
    public string? Today
    {
        get => _today;
        set => _today = value;
    }

    /// <summary>
    /// Publishes this table to <paramref name="host"/>, which owns it from here, and answers the
    /// pointer CUI reads. A second surface is a mistake: only the owner republishes when a name
    /// changes, so the other context would keep reading the replaced block.
    /// </summary>
    internal nint Attach(UiFrameState host)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_host is { } current && !ReferenceEquals(current, host))
            throw new InvalidOperationException(
                "this Localization is published to another surface. One table belongs to one app: " +
                "give each surface its own, or call ui.SetLocalization(null) on the first one.");

        _host = host;
        return Publish();
    }

    /// <summary>Lets the surface go, so the table can be published elsewhere or disposed.</summary>
    internal void Detach(UiFrameState host)
    {
        if (ReferenceEquals(_host, host)) _host = null;
    }

    /// <summary>
    /// The pointer CUI reads, rebuilt only when a name changed. Zero means every entry is null,
    /// which is what English is: CUI then uses its own table.
    /// </summary>
    internal nint Publish()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_block != null && !Changed()) return (nint)_block;

        // Build first, let go afterwards: a name that cannot be encoded must not leave CUI
        // pointing at a block this class no longer owns.
        NativeStrings* built = Build();
        Retire(_block);
        _block = built;
        for (int slot = 0; slot < Slots; slot++) _published[slot] = At(slot);
        return (nint)_block;
    }

    /// <summary>
    /// Frees the table now, or — while a surface still reads it — hands the block to that surface,
    /// which frees it at the next frame boundary. Either way the frames after that one draw English.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_host is { } host)
        {
            host.Orphan(this, (nint)_block);
            _host = null;
        }
        else if (_block != null)
        {
            NativeMemory.Free(_block);
        }

        _block = null;
        GC.SuppressFinalize(this);
    }

    // A table a surface still holds cannot be collected, so this only runs for one nobody published
    // or one that was detached again; there is no frame to wait for.
    ~Localization()
    {
        if (_block != null) NativeMemory.Free(_block);
    }

    /// <summary>One slot of the table as the managed arrays currently have it.</summary>
    private string? At(int slot) => slot switch
    {
        < 12 => Named(_months, slot),
        < 24 => Named(_monthsShort, slot - 12),
        < 31 => Named(_weekdays, slot - 24),
        < 38 => Named(_weekdaysShort, slot - 31),
        < 45 => Named(_weekdaysMin, slot - 38),
        _ => _today,
    };

    private static string? Named(string?[]? names, int index) =>
        names is not null && index < names.Length ? names[index] : null;

    private bool Changed()
    {
        for (int slot = 0; slot < Slots; slot++)
            if (!ReferenceEquals(_published[slot], At(slot)))
                return true;
        return false;
    }

    /// <summary>A replaced block goes to the surface that may still be reading it, or is freed here.</summary>
    private void Retire(NativeStrings* block)
    {
        if (block == null) return;
        if (_host is { } host) host.Retire((nint)block);
        else NativeMemory.Free(block);
    }

    /// <summary>The pointer table, then every name, NUL-terminated: one block, one free.</summary>
    private NativeStrings* Build()
    {
        int bytes = 0;
        for (int slot = 0; slot < Slots; slot++)
            if (At(slot) is { } name) bytes = checked(bytes + Encoding.UTF8.GetByteCount(name) + 1);
        if (bytes == 0) return null;   // nothing translated: a NULL table is English

        var block = (NativeStrings*)NativeMemory.AllocZeroed(checked((nuint)sizeof(NativeStrings) + (nuint)bytes));

        // cui_strings is six pointer arrays in a row with nothing between them (the ABI test pins
        // their offsets), so the slot index is the pointer index, and the names follow the table.
        var pointers = (nint*)block;
        byte* text = (byte*)block + sizeof(NativeStrings);
        for (int slot = 0; slot < Slots; slot++)
        {
            if (At(slot) is not { } name) continue;

            int length = Encoding.UTF8.GetBytes(name, new Span<byte>(text, bytes));
            text[length] = 0;
            pointers[slot] = (nint)text;
            text += length + 1;
            bytes -= length + 1;
        }

        return block;
    }

    private static void Check(string?[]? names, int count)
    {
        if (names is not null && names.Length != count)
            throw new ArgumentException($"{count} names expected, {names.Length} given.", nameof(names));
    }
}
