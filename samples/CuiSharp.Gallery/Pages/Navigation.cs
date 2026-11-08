// Page 5 of 9: tabs, breadcrumbs, pagination, stepper.
// Part of the CuiSharp.Gallery sample: Navigation
using CuiSharp;

namespace CuiSharp.Gallery;

internal sealed partial class GalleryApp
{
    private void PageNavigation(Ui ui)
    {
        Section(ui, "Tabs", "The indicator slides to the active tab.");
        using (ui.Card(new Card { Gap = 16 }))
        {
            ui.Tabs("tabs", TabNames, ref _tab);
            using (ui.Column(new Box { Gap = 6, PadY = 4 }))
            {
                ui.Label(TabNames[_tab]);
                ui.Secondary("Tabs switch between views of the same thing. Content changes in place.");
            }
            ui.Tabs("tabs2", Tab2Names, ref _tab2, new TabsOpts
            {
                Variant = TabsVariant.Pill,
                Icons = Tab2Icons,
            });
        }

        Section(ui, "Breadcrumb and pagination", null);
        using (ui.Card(new Card { Gap = 18 }))
        {
            int crumb = ui.Breadcrumb(Crumbs);
            if (crumb >= 0) ui.Toast(Crumbs[crumb]);
            ui.Pagination("pages", ref _pageIndex, 12);
        }

        Section(ui, "Stepper", null);
        using (ui.Card(new Card { Gap = 18 }))
        {
            ui.Stepper(Steps, _step);
            using (ui.Row(new Box { Gap = 8, Wrap = true }))
            {
                if (ui.Button("Back", new ButtonOpts { Disabled = _step == 0 })) _step--;
                if (ui.Button(_step >= 3 ? "Finish" : "Next", new ButtonOpts
                {
                    Variant = ButtonVariant.Primary,
                    IconRight = Icons.ArrowRight,
                }))
                {
                    if (_step < 3) _step++;
                    else ui.Toast("All set", new ToastOpts { Tone = Tone.Success });
                }
            }
        }
    }
}
