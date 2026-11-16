// Page 31 of 32: several people in one document — presence, live cursors, a selection ring and
// a selection bar, a result card, the canvas controls rail and the text format bar.
// Part of the CuiSharp.Gallery sample: Collaboration
using CuiSharp;

namespace CuiSharp.Gallery;

internal sealed partial class GalleryApp
{
    // Who is here. Positions are canvas points under the view the rail below moves, so both are
    // fields: a fresh array per frame would throw away the drag.
    private NodeView _view = new(0, 0, 1);
    private bool _grid = true;
    private bool _snap = true;
    private readonly CanvasToggle[] _canvasToggles =
    [
        new(Icons.Grid3x3, "Show grid") { On = true },
        new(Icons.Target, "Snap to grid"),
    ];

    // The cards a selection bar acts on. Native clears the flags itself when the bar is used.
    private readonly bool[] _cardsSelected = [true, false, true, false];

    private static readonly SelectionAction[] CardActions =
    [
        new(Icons.Copy, "Duplicate"),
        new(Icons.Download, "Export"),
        new(Icons.Trash2, "Delete", Tone.Danger),
    ];

    private TextFormat _textFormat = new(Tone.Neutral, Size: 15);

    private static readonly LiveUser[] Collaborators =
    [
        new("Kate", ColorFor(Tone.Violet), X: 46, Y: 62, Says: "adding the price row"),
        new("Sam", ColorFor(Tone.Success), X: 148, Y: 26, Speaking: true),
        new("Ari", ColorFor(Tone.Warning), X: 210, Y: 96, Away: true),
        new("Nia", ColorFor(Tone.Info), X: 96, Y: 140),
    ];

    private static readonly TextStyle[] Styles =
    [
        new("Header", 44, 22),
        new("Title", 30, 18),
        new("Subtitle", 22, 16),
        new("Normal text", 15, 14),
    ];

    private void PageCollaboration(Ui ui)
    {
        Section(ui, "Presence pill", "Who is here, as overlapping avatars in their colors. " +
            "Whoever is speaking gets a ring, moving level bars and their name.");
        using (ui.Card(new Card()))
            ui.PresencePill("people", Collaborators, new PresencePillOpts { Size = 30, MaxVisible = 3 });

        Section(ui, "Live cursors", "Other people's pointers over the previous component, each " +
            "gliding to its new position with the name it was given.");
        using (ui.Card(new Card()))
        using (ui.Box(new Box { Height = Size.Px(180), Width = Size.Fill }))
        {
            ui.LiveCursors("cursors", Collaborators, ref _view);
        }

        Section(ui, "Canvas controls", "A rail of zoom presets, a fit button and the caller's own " +
            "toggles, over the previous component. The toggles' state comes back in the span.");
        using (ui.Card(new Card()))
        using (ui.Box(new Box { Height = Size.Px(200), Width = Size.Fill }))
        {
            int asked = ui.CanvasControls("canvas", ref _view, _canvasToggles, new CanvasControlsOpts
            {
                Content = new RectF(0, 0, 640, 480),
            });
            ui.Secondary($"zoom {_view.Zoom:0.00} at ({_view.X:0}, {_view.Y:0}), flags {asked}, " +
                $"grid {_canvasToggles[0].On}, snap {_canvasToggles[1].On}");
            _grid = _canvasToggles[0].On;
            _snap = _canvasToggles[1].On;
        }

        Section(ui, "Selection ring", "A ring in someone's color around the previous component, for " +
            "\"this item is selected by Kate\", with an optional name flag on its corner.");
        using (ui.Card(new Card()))
        using (ui.Row(new Box { Gap = 12 }))
        {
            ui.Text("Selected by Sam");
            ui.SelectionRing(ColorFor(Tone.Success), new SelectionRingOpts { Name = "Sam", Radius = 8 });
        }

        Section(ui, "Selection bar", "How many items are selected and what can be done with them; " +
            "the clear button and Esc clear the caller's flags.");
        using (ui.Card(new Card()))
        {
            int action = ui.SelectionBar("cards", _cardsSelected, CardActions, new SelectionBarOpts
            {
                CountFormat = "%d cards selected",
            });
            ui.Secondary($"{_cardsSelected.Count(s => s)} selected, last action {action}");
        }

        Section(ui, "Result card", "A picture with rounded corners, a caption and an optional status " +
            "chip, for what a generation or an import produced.");
        using (ui.Card(new Card()))
        using (ui.Row(new Box { Gap = 12 }))
        {
            ui.ResultCard("result-1", 0, "Sunset plain", new ResultCardOpts
            {
                Caption = "1024 x 1024, PNG",
                Badge = "Done",
                BadgeTone = Tone.Success,
                ImageW = 160,
                ImageH = 120,
            });
            ui.ResultCard("result-2", 0, "Fog forest", new ResultCardOpts
            {
                Caption = "still rendering",
                Badge = "2 of 4",
                BadgeTone = Tone.Info,
                ImageW = 160,
                ImageH = 120,
            });
        }

        Section(ui, "Format bar", "The text controls a selection needs: a style, a color, bold, " +
            "underline, a link and the alignment, kept in the caller's TextFormat.");
        using (ui.Card(new Card()))
        {
            ui.FormatBar("format", ref _textFormat, new FormatBarOpts
            {
                Styles = Styles,
                Footer = "Text options",
            });
            ui.Secondary($"style {_textFormat.Style}, {_textFormat.Size:0} pt, bold {_textFormat.Bold}, " +
                $"align {_textFormat.Align}");
        }
    }
}
