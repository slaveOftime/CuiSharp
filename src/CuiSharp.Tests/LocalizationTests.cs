using System.Runtime.InteropServices;
using CuiSharp.Interop;

namespace CuiSharp.Tests;

/// <summary>
/// The month, weekday and "Today" names an app translates. CUI keeps the pointer it is given and
/// reads it every frame, so these pin the three things a wrong shape hides: the slot each name has
/// to land in, that a published table survives the frames that read it — including a name edited
/// after the call and a table disposed while published — and that an entry the table leaves out
/// really falls back to the English name it did not cover.
/// </summary>
public class LocalizationTests
{
    /// <summary>The frame tests need the native library, which a checkout may not have built yet.</summary>
    private static readonly bool NativeReady = CanLoadNative();

    private static bool CanLoadNative()
    {
        try
        {
            // The call a frame makes, not a search-path guess: if this resolves, so does the rest.
            _ = Native.KeyName(Key.A);
            return true;
        }
        catch (Exception error) when (error is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static readonly string[] SpanishMonths =
        ["enero", "febrero", "marzo", "abril", "mayo", "junio",
         "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"];

    private static readonly string[] SpanishWeekdays =
        ["lunes", "martes", "miércoles", "jueves", "viernes", "sábado", "domingo"];

    [Fact]
    public unsafe void EveryNameLandsInTheSlotCuiReads()
    {
        using var table = Spanish();

        var native = (NativeStrings*)table.Publish();

        Assert.Equal("enero", Marshal.PtrToStringUTF8(native->Month[0]));
        Assert.Equal("diciembre", Marshal.PtrToStringUTF8(native->Month[11]));
        Assert.Equal("ene", Marshal.PtrToStringUTF8(native->MonthShort[0]));
        Assert.Equal("lunes", Marshal.PtrToStringUTF8(native->Weekday[0]));       // CUI weeks start on Monday
        Assert.Equal("domingo", Marshal.PtrToStringUTF8(native->Weekday[6]));
        Assert.Equal("lun", Marshal.PtrToStringUTF8(native->WeekdayShort[0]));
        Assert.Equal("lu", Marshal.PtrToStringUTF8(native->WeekdayMin[0]));
        Assert.Equal("Hoy", Marshal.PtrToStringUTF8(native->Today));
    }

    /// <summary>
    /// A name the app left out is a null pointer, not an empty string: CUI reads null as "use my
    /// English table", while "" would draw a blank month header.
    /// </summary>
    [Fact]
    public unsafe void AnEntryTheTableLeavesOutIsNull()
    {
        using var table = new Localization { Months = MonthsWith(2, "marzo") };

        var native = (NativeStrings*)table.Publish();

        Assert.Equal(0, native->Month[0]);
        Assert.Equal("marzo", Marshal.PtrToStringUTF8(native->Month[2]));
        Assert.Equal(0, native->Weekday[0]);
    }

    /// <summary>One block, so the bytes are written once: a name is UTF-8, whatever the script.</summary>
    [Fact]
    public unsafe void NamesKeepEveryByteOfTheirOwnEncoding()
    {
        using var table = new Localization
        {
            Months = MonthsWith(2, "märz"),
            Today = "今日",
        };

        var native = (NativeStrings*)table.Publish();

        Assert.Equal("märz", Marshal.PtrToStringUTF8(native->Month[2]));
        Assert.Equal("今日", Marshal.PtrToStringUTF8(native->Today));
    }

    [Fact]
    public void ATableWithNothingInItIsEnglish()
    {
        using var table = new Localization();

        // Zero is the pointer CUI reads as "my own table", so an empty table costs no block at all.
        Assert.Equal(0, table.Publish());
    }

    /// <summary>
    /// The block is rebuilt when a name changes and not before — the comparison is by reference,
    /// so a name swapped in the array the app already handed over counts as a change.
    /// </summary>
    [Fact]
    public unsafe void ANameSwappedInPlaceRebuildsTheBlockAndNothingElseDoes()
    {
        var months = MonthsWith(2, "marzo");
        using var table = new Localization { Months = months };

        nint first = table.Publish();
        Assert.NotEqual(0, first);
        Assert.Equal(first, table.Publish());   // unchanged: the same block, no second allocation

        months[2] = "März";
        nint second = table.Publish();

        Assert.NotEqual(first, second);
        Assert.Equal("März", Marshal.PtrToStringUTF8(((NativeStrings*)second)->Month[2]));
    }

    [Fact]
    public void TheCalendarDrawsThePublishedNames()
    {
        if (!NativeReady) return;
        using var surface = CuiSurface.Create(700, 420);
        using var spanish = Spanish();

        byte[] english = Draw(surface, null);
        byte[] translated = Draw(surface, spanish);

        Assert.NotEqual(english, translated);
    }

    [Fact]
    public void PublishingNullRestoresTheEnglishFrame()
    {
        if (!NativeReady) return;
        using var surface = CuiSurface.Create(700, 420);
        using var spanish = Spanish();

        byte[] english = Draw(surface, null);
        byte[] translated = Draw(surface, spanish);
        byte[] back = Draw(surface, null);

        Assert.NotEqual(english, translated);
        Assert.Equal(english, back);   // the pointer is gone and the English names are back
    }

    /// <summary>
    /// A partial table is a complete one: only the names it covers change, which is also the only
    /// way to see that the entry index reaches the month the calendar is showing.
    /// </summary>
    [Fact]
    public void AnEntryTheTableLeavesOutKeepsItsEnglishName()
    {
        if (!NativeReady) return;
        using var surface = CuiSurface.Create(700, 420);
        using var march = new Localization { Months = MonthsWith(2, "marzo") };
        using var january = new Localization { Months = MonthsWith(0, "enero") };

        byte[] english = Draw(surface, null);       // the calendar shows March
        byte[] translated = Draw(surface, march);
        byte[] elsewhere = Draw(surface, january);

        Assert.NotEqual(english, translated);
        Assert.Equal(english, elsewhere);           // January is not on screen, so nothing moved
    }

    /// <summary>
    /// "One call is enough" is the promise, so a name changed after it has to reach the next frame
    /// without publishing again — including a name swapped in the array the app already handed over.
    /// </summary>
    [Fact]
    public void AChangedNameReachesTheNextFrameWithoutPublishingAgain()
    {
        if (!NativeReady) return;
        using var surface = CuiSurface.Create(700, 420);
        var months = MonthsWith(2, "marzo");
        using var table = new Localization { Months = months };

        byte[] march = Draw(surface, table);

        months[2] = "MARZO";
        byte[] shouted = Frame(surface);            // no SetLocalization call at all

        Assert.NotEqual(march, shouted);
    }

    /// <summary>
    /// A language picker can replace the table in the middle of a frame. The frame still paints
    /// with what it drew, the block it began with is freed only at the boundary after it, and the
    /// next frame draws the new table.
    /// </summary>
    [Fact]
    public void PublishingANewTableMidFrameDrawsItAndRetiresTheOldBlock()
    {
        if (!NativeReady) return;
        using var surface = CuiSurface.Create(700, 420);
        using var first = new Localization { Months = MonthsWith(2, "marzo") };
        using var second = new Localization { Months = MonthsWith(2, "MARZO") };

        byte[] before = Draw(surface, first);

        surface.BeginFrame();
        var ui = surface.Ui;
        ui.SetLocalization(second);                 // the block the frame began with is now retired
        Calendar(ui);
        surface.EndFrame();

        Assert.Same(second, surface.Ui.Localization);
        Assert.NotEqual(before, Frame(surface));    // and the frame after paints in the new names
    }

    /// <summary>
    /// CUI holds the pointer, so the table has to outlive the call with no owner of its own: the
    /// surface is what keeps it alive, and a table nobody else mentions still answers the next
    /// frame after a full collection.
    /// </summary>
    [Fact]
    public void TheSurfaceKeepsThePublishedTableAlive()
    {
        if (!NativeReady) return;
        using var surface = CuiSurface.Create(700, 420);

        WeakReference<Localization> published = Publish(surface);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.True(published.TryGetTarget(out Localization? table), "the surface dropped the published table");
        Assert.Same(table, surface.Ui.Localization);

        surface.Ui.SetLocalization(null);
        Assert.Null(surface.Ui.Localization);
    }

    /// <summary>
    /// Disposing a published table cannot free the block: CUI still points at it while this frame
    /// draws and paints. The surface takes it, clears the pointer at the next boundary and frees it
    /// there, which is why the frame after that is English again rather than freed memory.
    /// </summary>
    [Fact]
    public void DisposingAPublishedTableIsDeferredToTheNextFrameBoundary()
    {
        if (!NativeReady) return;
        using var surface = CuiSurface.Create(700, 420);

        byte[] english = Draw(surface, null);

        var table = Spanish();
        surface.BeginFrame();
        var ui = surface.Ui;
        ui.SetLocalization(table);
        Calendar(ui);
        table.Dispose();                 // still published, and this frame has not painted yet
        surface.EndFrame();

        Assert.Null(surface.Ui.Localization);
        Assert.Throws<ObjectDisposedException>(() => table.Publish());
        Assert.Equal(english, Frame(surface));

        // The surface let go of it, so the app can publish another table.
        using var another = Spanish();
        Assert.NotEqual(english, Draw(surface, another));
    }

    /// <summary>
    /// One table belongs to one surface: only the owner is told when a name changes, so a second
    /// reader would keep the block that was replaced under it.
    /// </summary>
    [Fact]
    public void PublishingToASecondSurfaceIsRefused()
    {
        if (!NativeReady) return;
        using var first = CuiSurface.Create(700, 420);
        using var second = CuiSurface.Create(700, 420);
        using var table = Spanish();

        byte[] english = Draw(first, null);
        Assert.NotEqual(english, Draw(first, table));

        var error = Assert.Throws<InvalidOperationException>(() => second.Ui.SetLocalization(table));
        Assert.Contains("another surface", error.Message, StringComparison.Ordinal);
        Assert.Null(second.Ui.Localization);
        Assert.Same(table, first.Ui.Localization);
        Assert.NotEqual(english, Frame(first));     // the owner still draws it
    }

    [Fact]
    public void ATableCanBePublishedAgainAfterTheFirstSurfaceLetsGo()
    {
        if (!NativeReady) return;
        using var first = CuiSurface.Create(700, 420);
        using var second = CuiSurface.Create(700, 420);
        using var table = Spanish();

        first.Ui.SetLocalization(table);
        first.Ui.SetLocalization(null);

        byte[] english = Draw(second, null);
        Assert.NotEqual(english, Draw(second, table));
        Assert.Same(table, second.Ui.Localization);
    }

    [Fact]
    public void PublishingTheSameTableEveryFrameAllocatesNothing()
    {
        if (!NativeReady) return;
        using var surface = CuiSurface.Create(700, 420);
        using var spanish = Spanish();

        void FrameOnce()
        {
            surface.BeginFrame();
            var ui = surface.Ui;
            ui.SetLocalization(spanish);   // the language picker's frame: same table every time
            Calendar(ui);
            surface.EndFrame();
        }

        for (int i = 0; i < 10; i++) FrameOnce();   // JIT, arena growth, font atlas

        var perFrame = new long[5];
        for (int batch = 0; batch < perFrame.Length; batch++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20; i++) FrameOnce();
            perFrame[batch] = (GC.GetAllocatedBytesForCurrentThread() - before) / 20;
        }

        Assert.Equal(0, perFrame.Median());
    }

    private static Localization Spanish() => new()
    {
        Months = SpanishMonths,
        MonthsShort = [.. SpanishMonths.Select(name => name[..3])],
        Weekdays = SpanishWeekdays,
        WeekdaysShort = [.. SpanishWeekdays.Select(name => name[..3])],
        WeekdaysMin = [.. SpanishWeekdays.Select(name => name[..2])],
        Today = "Hoy",
    };

    private static string?[] MonthsWith(int index, string name)
    {
        var months = new string?[12];
        months[index] = name;
        return months;
    }

    private static WeakReference<Localization> Publish(CuiSurface surface)
    {
        var table = Spanish();   // the app keeps no reference past this method
        surface.BeginFrame();
        surface.Ui.SetLocalization(table);
        surface.EndFrame();
        return new WeakReference<Localization>(table);
    }

    /// <summary>Draws one frame of the component the table exists for, and returns its pixels.</summary>
    private static byte[] Draw(CuiSurface surface, Localization? table)
    {
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];
        surface.BeginFrame();
        var ui = surface.Ui;
        ui.SetLocalization(table);
        Calendar(ui);
        surface.EndFrame();
        surface.Render(pixels);
        return pixels;
    }

    /// <summary>One frame of the same component, publishing nothing: what the surface does by itself.</summary>
    private static byte[] Frame(CuiSurface surface)
    {
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];
        surface.BeginFrame();
        Calendar(surface.Ui);
        surface.EndFrame();
        surface.Render(pixels);
        return pixels;
    }

    private static void Calendar(Ui ui)
    {
        var month = new Date(2026, 3, 1);
        var selected = default(Date);
        ui.MonthCalendar("month", ref month, [], ref selected);
    }
}
