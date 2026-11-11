// Page 16 of 28: multi-select, date ranges, time, wheel pickers, a time dial,
// a ruler slider and a histogram slider.
// Part of the CuiSharp.Gallery sample: Pickers
using CuiSharp;

namespace CuiSharp.Gallery;

internal sealed partial class GalleryApp
{
    private static readonly string[] PickerLanguages =
    ["C", "C++", "Rust", "Zig", "Go", "Swift", "Kotlin", "Odin"];

    private static readonly string[] WheelDays =
    ["1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12", "13", "14", "15", "16",
     "17", "18", "19", "20", "21", "22", "23", "24", "25", "26", "27", "28", "29", "30", "31"];

    private static readonly string[] WheelMonths =
    ["January", "February", "March", "April", "May", "June", "July", "August", "September",
     "October", "November", "December"];

    private static readonly string[] WheelYears =
    ["2020", "2021", "2022", "2023", "2024", "2025", "2026", "2027", "2028", "2029", "2030", "2031"];

    private static readonly float[] PriceBins =
    [2, 4, 7, 12, 18, 26, 31, 29, 34, 40, 37, 30, 24, 22, 18, 15, 11, 9, 7, 5, 4, 3, 2, 1];

    private readonly bool[] _pickerLangs = [true, false, true, false, false, true, false, false];
    private DateRange _range = new(new Date(2026, 11, 3), new Date(2026, 11, 14));
    private DateRange _stay = new(new Date(2026, 12, 18), new Date(2026, 12, 27));
    private int _startHour = 9, _startMinute = 30, _endHour = 18, _endMinute = 45;
    private int _wheelDay = 13, _wheelMonth = 10, _wheelYear = 6;
    private int _dialHour = 7, _dialMinute = 25;
    private float _rulerHeight = 172, _rulerTemp = 21.5f;
    private float _priceLo = 40, _priceHi = 160;

    private void PagePickers(Ui ui)
    {
        Section(ui, "Multi-select", "Chips for the chosen items, checkboxes in the list.");
        using (ui.Row(new Box { Gap = 12, Align = Align.Start }))
        {
            using (ui.Box(new Box { Width = Size.Px(320) }))
            {
                ui.MultiSelect("langs", PickerLanguages, _pickerLangs, new MultiSelectOpts
                {
                    Label = "Languages",
                    Searchable = true,
                });
            }
        }

        Section(ui, "Date range", "Presets on the left, two months to pick a start and an end.");
        using (ui.Row(new Box { Gap = 12, Wrap = true }))
        {
            ui.DateRangePicker("range", ref _range, new DateRangeOpts { Width = Size.Px(300) });
            ui.DateRangePicker("stay", ref _stay, new DateRangeOpts { Width = Size.Px(300), NoPresets = true });
        }
        using (ui.Row(new Box()))
        {
            using (ui.Card(new Card { Pad = 16 }))
            {
                // As wide as its months.
                ui.RangeCalendar("inline-range", ref _stay, 2);
            }
        }

        Section(ui, "Time", "A field with hour and minute columns, 24 hour or 12 hour.");
        using (ui.Row(new Box { Gap = 12, Wrap = true }))
        {
            ui.TimePicker("start", ref _startHour, ref _startMinute);
            ui.TimePicker("end", ref _endHour, ref _endMinute, new TimeOpts { TwelveHour = true, MinuteStep = 15 });
        }

        Section(ui, "Wheels and dial", "Drag, scroll or use the arrow keys. Several wheels make a date reel.");
        using (ui.Row(new Box { Gap = 32, Align = Align.Center }))
        {
            using (ui.Card(new Card { Dir = Dir.Row, Gap = 0, Pad = 8 }))
            {
                ui.WheelPicker("day", WheelDays, ref _wheelDay, new WheelOpts
                {
                    Width = Size.Px(64),
                    Loop = true,
                    JoinRight = true,
                });
                ui.WheelPicker("month", WheelMonths, ref _wheelMonth, new WheelOpts
                {
                    Width = Size.Px(128),
                    Loop = true,
                    JoinLeft = true,
                    JoinRight = true,
                });
                ui.WheelPicker("year", WheelYears, ref _wheelYear, new WheelOpts
                {
                    Width = Size.Px(80),
                    JoinLeft = true,
                });
            }
            ui.TimeDial("dial", ref _dialHour, ref _dialMinute, 232);
        }

        Section(ui, "Ruler and histogram", "A tape that slides under a fixed marker, and a range over its data.");
        using (ui.Row(new Box { Gap = 24, Align = Align.Start }))
        {
            using (ui.Column(new Box { Width = Size.Fill, Gap = 20 }))
            {
                ui.RulerSlider("Height", ref _rulerHeight, 120, 220, new RulerOpts { Unit = "cm" });
                ui.RulerSlider("Temperature", ref _rulerTemp, 10, 30, new RulerOpts
                {
                    Step = 0.5f,
                    MajorEvery = 4,
                    Spacing = 14,
                    Format = "%.1f",
                    Unit = "°C",
                });
            }
            using (ui.Card(new Card { Width = Size.Fill, Gap = 6 }))
            {
                ui.HistogramSlider("Price", PriceBins, ref _priceLo, ref _priceHi, 0, 240, new HistogramOpts
                {
                    Format = "$%.0f",
                });
                ui.Muted("1,204 listings in range");
            }
        }
    }
}
