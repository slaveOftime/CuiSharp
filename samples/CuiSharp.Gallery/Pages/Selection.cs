// Page 4 of 9: checkboxes, switches, radios, sliders, chips.
// Part of the CuiSharp.Gallery sample: Selection
using CuiSharp;

namespace CuiSharp.Gallery;

internal sealed partial class GalleryApp
{
    private void PageSelection(Ui ui)
    {
        using (ui.Box(new Box { Dir = Dir.Grid, Columns = 2, MinColumnWidth = 260, Gap = 16 }))
        {
            using (ui.Column(new Box { Gap = 12 }))
            {
                Section(ui, "Checkbox", null);
                using (ui.Card(new Card { Gap = 14 }))
                {
                    ui.Checkbox("Email notifications", ref _newsletter,
                        new CheckOpts { Description = "Product news, at most once a month." });
                    ui.Checkbox("Marketing", ref _marketing,
                        new CheckOpts { Description = "Offers from our partners." });
                    ui.Checkbox("Disabled option", ref _terms, new CheckOpts { Disabled = true });
                }
            }

            using (ui.Column(new Box { Gap = 12 }))
            {
                Section(ui, "Switch", null);
                using (ui.Card(new Card { Gap = 14 }))
                {
                    ui.Switch("Wi-Fi", ref _wifi);
                    ui.Switch("Bluetooth", ref _bluetooth);
                    ui.Switch("Airplane mode", ref _airplane,
                        new CheckOpts { Description = "Turns off all radios." });
                }
            }
        }

        Section(ui, "Radio group", null);
        using (ui.Card(new Card { Gap = 12 }))
        {
            ui.Radio("Every change", ref _plan, 0,
                new CheckOpts { Description = "Save as you type. Uses the most disk." });
            ui.Radio("Every 5 minutes", ref _plan, 1,
                new CheckOpts { Description = "A good balance for most projects." });
            ui.Radio("Only when I save", ref _plan, 2,
                new CheckOpts { Description = "Nothing is written until you press Ctrl S." });
        }

        Section(ui, "Radio cards", "For choices that need a sentence of explanation.");
        ui.RadioCards("places", Places, ref _planCard);

        Section(ui, "Sliders", "Drag past either end: the track stretches like rubber.");
        using (ui.Card(new Card { Gap = 20 }))
        {
            ui.Slider("Volume", ref _volume, 0, 100, new SliderOpts { ShowValue = true });
            ui.Slider("Brightness", ref _brightness, 0, 100,
                new SliderOpts { Step = 10, ShowValue = true, Format = "%.0f%%" });
            ui.RangeSlider("Price range", ref _lo, ref _hi, 0, 100);
        }

        Section(ui, "Chips and rating", null);
        using (ui.Card(new Card { Gap = 16 }))
        {
            using (ui.Row(new Box { Gap = 8, Wrap = true }))
            {
                for (int i = 0; i < ChipNames.Length; i++)
                {
                    bool on = _chips[i];
                    if (ui.Chip(ChipNames[i], ref on)) _chips[i] = on;
                }
            }
            ui.Rating("rating", ref _rating, 5);
        }
    }
}
