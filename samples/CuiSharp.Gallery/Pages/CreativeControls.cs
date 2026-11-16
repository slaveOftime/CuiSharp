// Page 30 of 32: the creative controls CUI added after 1.4 — status tags, the three well
// sliders, a palette strip, an aspect picker, an option with an action, a seed field and a
// pattern meter, plus the veil and the skeleton group that hide what is not ready.
// Part of the CuiSharp.Gallery sample: Creative controls
using CuiSharp;

namespace CuiSharp.Gallery;

internal sealed partial class GalleryApp
{
    // Every one of these is edited in place by the widget, so it is a field: a fresh value per
    // frame would throw the drag away.
    private float _ratio = 0.62f;
    private float _hue = 32;
    private float _temperature = 4200;
    private float _opacityValue = 0.7f;
    private Color _opacityValueColor = new(255, 120, 40);
    private int _steps = 2;
    private int _aspect = 2;
    private int _format = 0;
    private uint _seed = 7;
    private bool _seedLocked;

    private static readonly Aspect[] AspectChoices =
    [
        new(1, 1, "1:1"), new(4, 5, "4:5"), new(3, 2, "3:2"), new(16, 9, "16:9"), new(9, 16, "9:16"),
    ];

    private static readonly string[] Formats = ["PNG", "JPG", "WebP"];

    private static readonly MeterPart[] Storage =
    [
        new("Photos", 42, ColorFor(Tone.Info)),
        new("Video", 68, ColorFor(Tone.Violet)),
        new("Docs", 11, ColorFor(Tone.Success)),
    ];

    private static readonly string[] StopNames = ["Draft", "Refined", "Variation", "Final"];

    private void PageCreativeControls(Ui ui)
    {
        Section(ui, "Status tag", "An icon, a label and a fill tinted by the state, for a table cell, " +
            "a card header or a group of tags. A removable tag reports its click.");
        using (ui.Card(new Card()))
        using (ui.Row(new Box { Gap = 8, Wrap = true }))
        {
            foreach (TagStatus state in Enum.GetValues<TagStatus>())
            {
                if (state == TagStatus.Custom) continue;
                // One id per state: a constant id inside a loop gives every tag the same state.
                using (ui.Id(state.ToString()))
                    ui.StatusTag(null, state);
            }

            if (ui.StatusTag("In progress", TagStatus.InProgress, new StatusTagOpts { Removable = true }))
                _lastTag = "removable tag clicked";
            ui.StatusTag("Custom", TagStatus.Custom, new StatusTagOpts
            {
                Icon = Icons.Tag,
                Tone = Tone.Teal,
            });
            ui.Secondary(_lastTag);
        }

        Section(ui, "Ratio slider", "A whole split into two named parts, with a bar between them to " +
            "drag; the split stops where a part would get too narrow for its name.");
        using (ui.Card(new Card()))
        {
            ui.RatioSlider("COFFEE", "MILK", ref _ratio, new RatioSliderOpts { Tone = Tone.Warning });
            ui.Secondary($"{_ratio:P0} coffee, {1 - _ratio:P0} milk");
        }

        Section(ui, "Gradient slider", "The track is the value itself: hue, white balance or opacity, " +
            "with the thumb filled with the color under it.");
        using (ui.Card(new Card()))
        using (ui.Column(new Box { Gap = 12 }))
        {
            ui.GradientSlider("Hue", ref _hue, 0, 360, new GradientSliderOpts
            {
                Track = GradientTrack.Hue,
                Swatch = true,
                Tool = Icons.Target,
                ToolTooltip = "Pick a color",
            });
            ui.GradientSlider("Temperature", ref _temperature, 2000, 8000, new GradientSliderOpts
            {
                Track = GradientTrack.Temperature,
            });
            ui.GradientSlider("Opacity", ref _opacityValue, 0, 1, ref _opacityValueColor, new GradientSliderOpts
            {
                Track = GradientTrack.Opacity,
                Color = _opacityValueColor,
            });
        }

        Section(ui, "Step slider", "A few discrete stops marked with dots; the thumb snaps to each " +
            "and glides between them.");
        using (ui.Card(new Card()))
            ui.StepSlider("Variations", ref _steps, 1, 4, new StepSliderOpts
            {
                Labels = StopNames,
                Tool = Icons.RefreshCw,
                ToolTooltip = "Generate",
            });

        Section(ui, "Palette strip", "A joined row of swatches: a click locks one, the shuffle " +
            "recolors the unlocked ones from a seed the caller keeps.");
        using (ui.Card(new Card()))
        {
            int changed = ui.PaletteStrip("Color palette", ref _seed, _paletteColors, _paletteLocked,
                new PaletteStripOpts { Value = $"{_paletteLocked.Count(l => l)} locked, seed {_seed}" });
            ui.Secondary($"changed {(changed & 1) != 0}, action {(changed & 2) != 0}");
        }

        Section(ui, "Aspect picker", "Each choice drawn as a rectangle of its own proportions, with " +
            "the size in pixels at the right of the label.");
        using (ui.Card(new Card()))
            ui.AspectPicker("Aspect ratio", ref _aspect, new AspectPickerOpts
            {
                Ratios = AspectChoices,
                Custom = true,
            });

        Section(ui, "Option with an action", "A compact segmented choice and the button of the " +
            "action it feeds, for a value that saves, exports or downloads.");
        using (ui.Card(new Card()))
            ui.OptionAction("Save as", Formats, ref _format, Icons.Download, new OptionActionOpts
            {
                Tooltip = "Download",
            });

        Section(ui, "Seed field", "A seed in the monospaced font: the dice rolls a new one and the " +
            "lock keeps it fixed.");
        using (ui.Card(new Card()))
            ui.SeedField("Seed", ref _seed, ref _seedLocked, new SeedFieldOpts
            {
                Value = _seedLocked ? "Same image every run" : "Random each run",
            });

        Section(ui, "Pattern meter", "A whole bar of parts, told apart by dot density as well as " +
            "color, with a legend that says how much each one holds.");
        using (ui.Card(new Card()))
            ui.PatternMeter("storage", Storage, new PatternMeterOpts
            {
                Label = "Storage",
                Unit = "GB",
                Total = 160,
            });

        Section(ui, "Skeleton group", "Placeholders for content that is loading, drawn inside one " +
            "group that shares a single shine.");
        using (ui.Card(new Card()))
        using (ui.SkeletonGroup(new SkeletonGroupOpts { Dither = true }))
        using (ui.Column(new Box { Gap = 8 }))
        {
            ui.Skeleton(Size.Fill, 14);
            ui.Skeleton(Size.Fill, 14);
            ui.Skeleton(Size.Px(160), 14);
        }

        Section(ui, "Veil", "A cover over what is behind it, which the caller reveals with a bool: " +
            "for a first-run tour or a dialog that must be dismissed first.");
        using (ui.Card(new Card()))
        using (ui.Veil("cover", ref _veiled, new VeilOpts { Label = "Covered", Hint = "Click to lift the veil" }))
        using (ui.Column(new Box { Gap = 8 }))
        {
            ui.Text("This card is behind the veil until you click it.");
            ui.Skeleton(Size.Fill, 12);
        }
    }

    private string _lastTag = "";
    private bool _veiled = true;
    private readonly Color[] _paletteColors =
    [
        ColorFor(Tone.Info), ColorFor(Tone.Violet), ColorFor(Tone.Success),
        ColorFor(Tone.Warning), ColorFor(Tone.Danger),
    ];
    private readonly bool[] _paletteLocked = new bool[5];

    private static Color ColorFor(Tone tone) => tone switch
    {
        Tone.Info => new Color(59, 130, 246),
        Tone.Violet => new Color(139, 92, 246),
        Tone.Success => new Color(34, 197, 94),
        Tone.Warning => new Color(245, 158, 11),
        Tone.Danger => new Color(239, 68, 68),
        Tone.Teal => new Color(20, 184, 166),
        _ => new Color(148, 163, 184),
    };
}
