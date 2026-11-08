// Custom composition without an interface: a static class with a C# 14
// extension block adds members directly to Ui, so a hand-written control is
// called exactly like a built-in one — ui.Spark(...) — with no base class, no
// registration and no allocation per frame.
using CuiSharp;

namespace CuiSharp.Gallery;

/// <summary>Custom controls used by the gallery. Copy this file to start your own.</summary>
internal static class CustomControls
{
    extension(Ui ui)
    {
        /// <summary>A line chart small enough to sit in a table cell.</summary>
        /// <remarks>The array is read during paint; do not mutate it until the frame ends.</remarks>
        public void Spark(UiText id, float[] values, Color color, float width, float height)
        {
            var result = ui.Canvas(id, Size.Px(width), Size.Px(height), (paint, box) =>
                DrawSpark(paint, box, values, color));

            if (!result.Hovered || values.Length < 2) return;

            float step = width / (values.Length - 1);
            int index = Math.Clamp((int)(result.MouseX / (step <= 0 ? 1 : step)), 0, values.Length - 1);
            ui.Tooltip($"{values[index]:0.0}");
        }

        /// <summary>Horizontal bars with a value gutter, drawn from scratch.</summary>
        public void Bars(UiText id, float[] values, float width, float height)
        {
            ui.Canvas(id, Size.Px(width), Size.Px(height), (paint, box) =>
            {
                float row = box.H / Math.Max(1, values.Length);
                float barH = Math.Max(4, row * 0.45f);
                float w = box.W * 0.72f;

                for (int i = 0; i < values.Length; i++)
                {
                    float y = box.Y + i * row + (row - barH) / 2;
                    paint.DrawRect(new RectF(box.X, y, w, barH), Color.SurfaceMuted, barH / 2);
                    paint.DrawRect(new RectF(box.X, y, w * values[i], barH), Color.Series(i), barH / 2);
                    paint.DrawText($"{values[i] * 100:0}%", box.X + w + 8, y + barH - 2, Color.TextMuted, 11);
                }
            });
        }
    }

    private static void DrawSpark(Ui ui, RectF box, float[] values, Color color)
    {
        if (values.Length < 2) return;

        float min = float.MaxValue, max = float.MinValue;
        foreach (float v in values)
        {
            if (v < min) min = v;
            if (v > max) max = v;
        }
        float span = max - min;
        if (span <= 0) span = 1;

        float step = box.W / (values.Length - 1);
        var points = new (float X, float Y)[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            float y = box.Y + box.H - (values[i] - min) / span * box.H;
            points[i] = (box.X + i * step, y);
        }

        // Area under the line, then the line itself.
        for (int i = 1; i < points.Length; i++)
        {
            ui.DrawRect(new RectF(points[i - 1].X, points[i - 1].Y, step, box.Y + box.H - points[i - 1].Y),
                Color.FromToken(Token.AccentSubtle));
        }
        ui.DrawPolyline(points, 1.8f, color);
        ui.DrawCircle(points[^1].X - 1.5f, points[^1].Y, 2.5f, color);
    }
}
