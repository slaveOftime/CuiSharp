using CuiSharp.Hosting;

namespace CuiSharp.Testing;

/// <summary>
/// A windowless CuiSharp app for tests. Same thing as <see cref="HeadlessApp"/>, under the name
/// a test wants to read:
/// </summary>
/// <example><code>
/// using var app = TestApp.Start(new CounterPage(), 800, 600);
/// app.FindButton("Add").Click();
/// Assert.Equal(1, page.Count);
/// Assert.Empty(app.Faults);
/// Assert.True(app.PaintedAnything());
/// </code></example>
/// <remarks>
/// The driving is done by the framework, not here, because an agent needs the same app:
/// <c>dotnet cui tree</c> and <c>dotnet cui mcp</c> drive a real process through
/// <see cref="HeadlessLoop"/>, and a test drives the same class in memory.
/// </remarks>
public sealed class TestApp : HeadlessApp
{
    private TestApp(CuiSurface surface, Component? root, DrawFn? draw = null) : base(surface, root, draw) { }

    /// <summary>Starts a harness that draws <paramref name="draw"/> for every frame.</summary>
    public static new TestApp Start(
        DrawFn draw,
        int width = 1024,
        int height = 768,
        float scale = 1f,
        Theme theme = Theme.Light,
        Accent accent = Accent.Neutral)
    {
        ArgumentNullException.ThrowIfNull(draw);
        var app = new TestApp(
            CuiSurface.Create(width, height, scale, theme: theme, accent: accent), root: null, draw);
        app.Frame();
        return app;
    }

    /// <summary>Starts a harness around one component, initializing it the way the host does.</summary>
    public static new TestApp Start(
        Component root,
        int width = 1024,
        int height = 768,
        float scale = 1f,
        Theme theme = Theme.Light,
        Accent accent = Accent.Neutral)
    {
        ArgumentNullException.ThrowIfNull(root);
        var app = new TestApp(
            CuiSurface.Create(width, height, scale, theme: theme, accent: accent), root);
        app.Frame();
        return app;
    }
}
