// Page 27 of 28: knobs, a level meter, channel faders with meters, and a tour
// that points at them.
// Part of the CuiSharp.Gallery sample: Audio
using CuiSharp;

namespace CuiSharp.Gallery;

internal sealed partial class GalleryApp
{
    private static readonly string[] FaderNames = ["Kick", "Snare", "Bass", "Keys", "Vocals", "FX"];

    private static readonly float[] FaderMarks = [6, 0, -6, -12, -24, -48];

    private float _cutoff = 1800, _resonance = 0.3f, _attack = 12, _panKnob = 0.15f;
    private readonly float[] _channelFaders = [-4, -9, 0, -15, -6, -2];
    private int _audioTick;
    private int _audioTour = -1;

    // The tour points at widgets by id: each target is ui.LastId taken right after
    // the control it names. The steps live in one retained array.
    private readonly TourStep[] _audioSteps =
    [
        new(0, "Turn a knob", "Drag up and down. Hold Shift for fine steps, double-click to reset.", Placement.BottomStart),
        new(0, "Watch the levels", "Bars fall back after each peak; the mark above them holds the peak a moment.", Placement.TopStart),
        new(0, "Ride the faders", "Click a fader, then use the wheel or the arrow keys.", Placement.LeftStart),
    ];

    // Per-strip and master level pairs, written in place every frame: the meters
    // read them, and a fresh array per frame would be allocation.
    private readonly float[][] _stripLevels =
    [
        new float[2], new float[2], new float[2], new float[2], new float[2], new float[2],
    ];

    private readonly float[] _masterLevels = new float[2];

    private void PageAudio(Ui ui)
    {
        _audioTick++;
        for (int k = 0; k < 6; k++)
        {
            for (int c = 0; c < 2; c++)
            {
                float t = (_audioTick + (c * 5)) / 60.0f;
                float beat = ((t * (1.8f + (0.2f * k))) + (k * 0.31f)) % 1.0f;
                _stripLevels[k][c] = (0.3f + (0.1f * k)) * (0.2f + (0.8f * MathF.Exp(-beat * (2.5f + k))));
            }
        }
        _masterLevels[0] = _stripLevels[0][0] * 1.4f;
        _masterLevels[1] = _stripLevels[4][1] * 1.3f;

        if (ui.Button("Take the tour", new ButtonOpts { Icon = Icons.Sparkles, Variant = ButtonVariant.Outline })) _audioTour = 0;

        using (ui.Row(new Box { Gap = 16, Wrap = true, Align = Align.Start }))
        {
            using (ui.Card(new Card { Gap = 16, MinWidth = 280 }))
            {
                ui.Heading("Synth", 4);
                using (ui.Row(new Box { Gap = 14, Wrap = true }))
                {
                    ui.Knob("Cutoff", ref _cutoff, 20, 20000, new KnobOpts
                    {
                        Unit = "Hz",
                        LogScale = true,
                        Reset = 1000,
                    });
                    _audioSteps[0].Target = ui.LastId;
                    ui.Knob("Resonance", ref _resonance, 0, 1, new KnobOpts { Format = "%.2f" });
                    ui.Knob("Attack", ref _attack, 1, 2000, new KnobOpts { Unit = "ms", LogScale = true, Reset = 10 });
                    ui.Knob("Pan", ref _panKnob, -1, 1, new KnobOpts { Bipolar = true, Format = "%+.2f" });
                }
                ui.LevelMeter("master", _masterLevels, new LevelMeterOpts
                {
                    Horizontal = true,
                    Length = 240,
                    Scale = true,
                    Segmented = true,
                });
                _audioSteps[1].Target = ui.LastId;
            }
            using (ui.Card(new Card { Dir = Dir.Row, Gap = 8, Wrap = true }))
            {
                for (int k = 0; k < 6; k++)
                {
                    using (ui.Id(k))
                    {
                        ui.Fader(FaderNames[k], ref _channelFaders[k], -48, 6, new FaderOpts
                        {
                            Unit = "dB",
                            Length = 180,
                            Ticks = k == 0 ? FaderMarks : null,
                            Meter = _stripLevels[k],
                        });
                        if (k == 2) _audioSteps[2].Target = ui.LastId;
                    }
                }
            }
        }

        if (_audioTour >= 0)
        {
            // The widget moves the step through the ref and sets it back to -1 when the tour
            // ends; its result is the action taken, so it is not the step.
            _ = ui.Tour("audio-tour", _audioSteps, ref _audioTour);
        }
    }
}
