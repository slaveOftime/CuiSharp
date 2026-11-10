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
/// surface that published it keeps this object alive. Every entry is optional: a null falls back
/// to the English name, so a partial table is a complete one.
/// </para>
/// <para>
/// Weeks start on Monday, as in every CUI calendar, so <see cref="Weekdays"/> starts at Monday.
/// A table belongs to one app: publish it to one <see cref="CuiSurface"/> or <see cref="CuiApp"/>.
/// Two contexts reading one table breaks the moment its names change, because only the publishing
/// context is told about the rebuilt block.
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
///     ui.SetLocalization(es);   // once is enough: CUI reads the same table every frame
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
    private List<nint>? _retired;
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
    /// The pointer CUI reads this frame, rebuilt only when a name changed. Zero means every entry
    /// is null, which is what English is: CUI then uses its own table.
    /// </summary>
    internal nint Publish()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_block != null && !Changed()) return (nint)_block;

        Retire();
        for (int slot = 0; slot < Slots; slot++) _published[slot] = At(slot);
        _block = Build();
        return (nint)_block;
    }

    /// <summary>
    /// Frees the blocks a previous table used, at a frame boundary: CUI holds the pointer it was
    /// given and reads it while that frame draws and paints, so the free waits until no frame can
    /// be reading the old one. Called by the surface at the top of a frame.
    /// </summary>
    internal void ReleaseRetired()
    {
        if (_retired is null) return;
        foreach (nint block in _retired) NativeMemory.Free((void*)block);
        _retired.Clear();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Free();
        GC.SuppressFinalize(this);
    }

    ~Localization() => Free();

    private void Free()
    {
        if (_block != null) NativeMemory.Free(_block);
        _block = null;
        ReleaseRetired();
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

    /// <summary>The pointer table, then every name, NUL-terminated: one block, one free.</summary>
    private NativeStrings* Build()
    {
        int bytes = 0;
        for (int slot = 0; slot < Slots; slot++)
            if (_published[slot] is { } name) bytes += Encoding.UTF8.GetByteCount(name) + 1;
        if (bytes == 0) return null;   // nothing translated: a NULL table is English

        var block = (NativeStrings*)NativeMemory.AllocZeroed((nuint)(sizeof(NativeStrings) + bytes));

        // cui_strings is six pointer arrays in a row with nothing between them (the ABI test pins
        // their offsets), so the slot index is the pointer index, and the names follow the table.
        var pointers = (nint*)block;
        byte* text = (byte*)block + sizeof(NativeStrings);
        for (int slot = 0; slot < Slots; slot++)
        {
            if (_published[slot] is not { } name) continue;

            int length = Encoding.UTF8.GetBytes(name, new Span<byte>(text, bytes));
            text[length] = 0;
            pointers[slot] = (nint)text;
            text += length + 1;
            bytes -= length + 1;
        }

        return block;
    }

    private void Retire()
    {
        if (_block == null) return;
        (_retired ??= []).Add((nint)_block);
        _block = null;
    }

    private static void Check(string?[]? names, int count)
    {
        if (names is not null && names.Length != count)
            throw new ArgumentException($"{count} names expected, {names.Length} given.", nameof(names));
    }
}
