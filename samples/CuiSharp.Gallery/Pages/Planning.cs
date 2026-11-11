// Page 23 of 28: a kanban board with drag moves, a week calendar with draggable
// events, a gantt with dependencies, and a month calendar.
// Part of the CuiSharp.Gallery sample: Planning
using CuiSharp;

namespace CuiSharp.Gallery;

internal sealed partial class GalleryApp
{
    private static readonly string[] KanbanColumns = ["Backlog", "In progress", "Review", "Done"];
    private static readonly uint[] KanbanDots = [0x94a3b8, 0x3b82f6, 0xf59e0b, 0x22c55e];

    // The card model C keeps in a static array; the columns hold card ids.
    private static readonly BoardCard[] BoardCards =
    [
        new(1, "Audit the color tokens for contrast", "Design", "Maya Okafor", Tone.Violet, 3, 0, 0),
        new(2, "Virtualize the activity feed", "Perf", "Jonas Berg", Tone.Teal, 1, 2, 0),
        new(3, "Dark mode screenshots for docs", "Docs", "Aiko Tanaka", Tone.Rose, 0, 4, 0),
        new(4, "Keyboard nav in the date range picker", "A11y", "Priya Nair", Tone.Info, 5, 0, 0.6f),
        new(5, "IME composition on Windows", "Platform", "Daniel Mensah", Tone.Warning, 2, 1, 0.3f),
        new(6, "Spring presets", "Motion", "Rosa Delgado", Tone.Violet, 4, 0, 1),
        new(7, "Glyph atlas eviction", "Perf", "Lena Fischer", Tone.Teal, 0, 0, 1),
        new(8, "Retina screenshots", "Docs", "Aiko Tanaka", Tone.Rose, 1, 3, 1),
    ];

    private readonly int[][] _board = [[1, 2, 3, 0, 0, 0, 0, 0], [4, 5, 0, 0, 0, 0, 0, 0], [6, 0, 0, 0, 0, 0, 0, 0], [7, 8, 0, 0, 0, 0, 0, 0]];
    private readonly int[] _boardLen = [3, 2, 1, 2];

    private CalendarEvent[] _weekEvents =
    [
        new("Standup", "Zoom", 0, 9.5f, 9.75f, Tone.Info),
        new("Design review", "Room 2", 0, 11, 12.5f, Tone.Violet),
        new("Lunch with Maya", null, 1, 12.5f, 13.5f, Tone.Success),
        new("Standup", "Zoom", 1, 9.5f, 9.75f, Tone.Info),
        new("Release planning", "Room 1", 2, 10, 11.5f, Tone.Warning),
        new("1:1 Jonas", null, 2, 10.5f, 11, Tone.Rose),
        new("Focus time", "No meetings", 2, 14, 17, Tone.Teal),
        new("Standup", "Zoom", 3, 9.5f, 9.75f, Tone.Info),
        new("Customer call", "Northlight", 3, 15, 16, Tone.Danger),
        new("Demo day", "All hands", 4, 16, 17.5f, Tone.Violet),
        new("Gym", null, 5, 10, 11.5f, Tone.Success),
    ];

    private GanttTask[] _ganttTasks =
    [
        new("Research", "Maya Okafor", 0, 5, 1, Tone.Teal, -1),
        new("Wireframes", "Aiko Tanaka", 4, 10, 1, Tone.Teal, 0),
        new("Design system", "Rosa Delgado", 8, 17, 0.7f, Tone.Teal, 1),
        new("Core engine", "Jonas Berg", 6, 20, 0.55f, Tone.Info, 0),
        new("Platform backends", "Daniel Mensah", 14, 26, 0.2f, Tone.Teal, 3),
        new("Beta", null, 27, 27, 0, Tone.Warning, 4, Milestone: true),
        new("Docs and launch", "Priya Nair", 28, 36, 0, Tone.Success, 5),
    ];

    private Date _monthShown;
    private Date _monthPicked;
    private bool _monthSeeded;
    private readonly MonthEvent[] _monthEvents = new MonthEvent[12];
    private DrawFn? _boardBody;

    private void PagePlanning(Ui ui)
    {
        Section(ui, "Kanban", "Drag cards within and between columns. Columns can carry a work-in-progress limit.");
        if (ui.Kanban("board", out KanbanMove move, _boardBody ??= BoardBody)) ApplyMove(move);

        Section(ui, "Week calendar", "Drag events to move them in 15 minute steps, or pull the bottom edge to resize.");
        Date today = ui.DateToday(out Date now) ? now : new Date(2026, 11, 14);
        int dayNumber = today.DayNumber;
        int monday = dayNumber - ((dayNumber + 3) % 7 + 7) % 7;
        int moved = ui.WeekCalendar("week", Date.FromDayNumber(monday), _weekEvents, new WeekOpts
        {
            StartHour = 8,
            EndHour = 19,
            Height = Size.Px(420),
        });
        if (moved >= 0) ui.Toast(_weekEvents[moved].Title, new ToastOpts { Description = _weekEvents[moved].Detail });

        Section(ui, "Gantt", "Drag bars to reschedule, pull either end to change the length. Arrows follow dependencies.");
        Date origin = Date.FromDayNumber(monday - 7);
        int task = ui.Gantt("plan", _ganttTasks, origin, 42, new GanttOpts { Today = 7 + (dayNumber - monday) });
        if (task >= 0)
        {
            ui.Toast(_ganttTasks[task].Name, new ToastOpts
            {
                Description = _ganttTasks[task].Milestone ? "Milestone" : $"{_ganttTasks[task].End - _ganttTasks[task].Start} days",
            });
        }

        Section(ui, "Month calendar", "Bars for days off and trips, timed events in time order, +N more when a day is full.");
        if (!_monthSeeded) SeedMonth(Date.FromDayNumber(dayNumber));
        MonthResult month = ui.MonthCalendar("month", ref _monthShown, _monthEvents, ref _monthPicked);
        if (month.Event >= 0) ui.Toast(_monthEvents[month.Event].Title, new ToastOpts { Description = "Opened from the month calendar" });
    }

    // The board body runs through a DrawFn, so it is a fixed method over the page
    // state, not a closure per frame.
    private void BoardBody(Ui ui)
    {
        for (int c = 0; c < 4; c++)
        {
            using (ui.KanbanColumn(KanbanColumns[c], _boardLen[c], new KanbanColumnOpts
            {
                Dot = Color.Rgb(KanbanDots[c]),
                Limit = c == 1 ? 3 : 0,
                Width = 264,
            }, out bool addClicked))
            {
                if (addClicked) ui.Toast("New card", new ToastOpts { Description = KanbanColumns[c] });
                for (int k = 0; k < _boardLen[c]; k++)
                {
                    BoardCard? card = FindCard(_board[c][k]);
                    if (card is null) continue;
                    using (ui.KanbanCard(card.Id, out bool clicked))
                    {
                        if (clicked) ui.Toast(card.Title);
                        ui.Badge(card.Tag, new BadgeOpts { Tone = card.Tone, Size = ControlSize.Sm });
                        ui.Text(card.Title, new TextOpts { Font = Font.Medium, Size = 13.5f, LineHeight = 1.35f });
                        if (card.Progress > 0 && card.Progress < 1) ui.Progress(card.Progress, new ProgressOpts { Height = 4 });
                        using (ui.Row(new Box { Gap = 10, Align = Align.Center, Width = Size.Fill }))
                        {
                            ui.Avatar(card.Who, new AvatarOpts { Size = 22 });
                            ui.Spacer();
                            if (card.Files > 0)
                            {
                                ui.Icon(Icons.Paperclip, 12, Color.TextMuted);
                                ui.Text($"{card.Files % 10}", new TextOpts { Size = 12, Color = Color.TextMuted });
                            }
                            if (card.Comments > 0)
                            {
                                ui.Icon(Icons.MessageSquare, 12, Color.TextMuted);
                                ui.Text($"{card.Comments % 10}", new TextOpts { Size = 12, Color = Color.TextMuted });
                            }
                            if (card.Progress >= 1) ui.Icon(Icons.CircleCheck, 14, Color.Success);
                        }
                    }
                }
            }
        }
    }

    private static BoardCard? FindCard(int id)
    {
        foreach (BoardCard card in BoardCards)
        {
            if (card.Id == id) return card;
        }
        return null;
    }

    // The board reports a drop; the app moves the card in its own data.
    private void ApplyMove(KanbanMove m)
    {
        int[] from = _board[m.FromColumn], to = _board[m.ToColumn];
        int at = -1;
        for (int k = 0; k < _boardLen[m.FromColumn]; k++)
        {
            if (from[k] == m.Card) at = k;
        }

        if (at < 0 || (m.ToColumn != m.FromColumn && _boardLen[m.ToColumn] >= 8)) return;
        for (int k = at; k < _boardLen[m.FromColumn] - 1; k++) from[k] = from[k + 1];
        _boardLen[m.FromColumn]--;
        int idx = m.ToIndex < 0 ? 0 : m.ToIndex > _boardLen[m.ToColumn] ? _boardLen[m.ToColumn] : m.ToIndex;
        for (int k = _boardLen[m.ToColumn]; k > idx; k--) to[k] = to[k - 1];
        to[idx] = m.Card;
        _boardLen[m.ToColumn]++;
    }

    private void SeedMonth(Date now)
    {
        Date first = new(now.Year, now.Month, 1);
        int baseDay = first.DayNumber;
        string[] titles =
        [
            "Standup", "Design review", "Offsite", "1:1", "Release", "Planning",
            "Customer call", "Lunch", "Retro", "Holiday", "Demo", "Board meeting",
        ];
        for (int k = 0; k < 12; k++)
        {
            bool span = k is 2 or 9;
            Date start = Date.FromDayNumber(baseDay + ((k * 5) % 27));
            _monthEvents[k] = new MonthEvent(
                titles[k],
                start,
                span ? Date.FromDayNumber(baseDay + ((k * 5) % 27) + 2) : default,
                9.0f + (k % 8),
                span || k == 4,
                (Tone)(1 + (k % 8)));
        }
        _monthSeeded = true;
    }

    /// <summary>A kanban card; the columns reference these by id.</summary>
    private sealed record BoardCard(
        int Id, string Title, string Tag, string Who, Tone Tone, int Comments, int Files, float Progress);
}
