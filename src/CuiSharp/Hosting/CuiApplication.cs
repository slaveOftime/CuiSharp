using Microsoft.Extensions.DependencyInjection;

namespace CuiSharp.Hosting;

/// <summary>
/// The composition root, generic-host style: services, window options, then one
/// call to run the root component on the calling thread.
/// </summary>
/// <example><code>
/// var builder = CuiApplication.CreateBuilder(args);
/// builder.Services.AddSingleton&lt;TodoService&gt;();
/// builder.Services.AddSingleton&lt;TodoPage&gt;();
/// builder.Window = builder.Window with { Title = "Todos", Width = 640, Height = 560 };
///
/// using var app = builder.Build();
/// return app.Run&lt;TodoPage&gt;();
/// </code></example>
public sealed class CuiApplication : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly CuiAppOptions _window;
    private readonly string[] _args;

    internal CuiApplication(ServiceProvider services, CuiAppOptions window, string[] args)
    {
        _services = services;
        _window = window;
        _args = args;
    }

    /// <summary>Starts a builder with the framework's dependency injection container.</summary>
    /// <param name="args">Command-line arguments, kept for hosted services and diagnosability.</param>
    public static CuiApplicationBuilder CreateBuilder(string[]? args = null) => new(args ?? []);

    /// <summary>The container, for resolving a component yourself when you do not use <see cref="Run{TRoot}"/>.</summary>
    public IServiceProvider Services => _services;

    /// <summary>
    /// Resolves the root component and runs the UI loop on the calling thread until
    /// the window closes. The window owns the loop: CUI's callback drives every
    /// frame, including the frames the OS asks for while a window is being resized.
    /// </summary>
    public int Run<TRoot>() where TRoot : Component
    {
        // The container owns the root: it was registered in it, so disposing the
        // application disposes the component exactly once.
        var root = _services.GetRequiredService<TRoot>();
        return Run(root);
    }

    /// <summary>
    /// Runs an already-built root component in a window, or — when the process was started
    /// headless — answers frames and commands on stdin/stdout instead. Useful for tests and tools.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A tool runs the app the same way a person does, so the frames it inspects are the frames
    /// the app really draws: <c>CUISHARP_HEADLESS=1 dotnet run</c> then <c>tree</c>, <c>click</c>,
    /// <c>png</c> on the standard streams. See <see cref="HeadlessLoop"/>.
    /// </para>
    /// <para>
    /// The root is disposed with the application, not here: it is either the container's (it was
    /// registered in it) or the caller's (it was passed in). <see cref="Run{TRoot}"/> takes the
    /// first road, so disposing it twice would be the difference.
    /// </para>
    /// </remarks>
    public int Run(Component root)
    {
        ArgumentNullException.ThrowIfNull(root);
        root.Initialize();
        return CuiApp.Run(_window, ui => Host(root, ui), _args);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        FrameTrace.Close();
        _services.Dispose();
    }

    // One frame: draw the tree, record what it cost, then keep the loop awake while
    // work is outstanding.
    private static void Host(Component root, Ui ui)
    {
        // Work that finished on a pool thread is applied here, on the UI thread, so
        // this frame draws it and no component has to poll.
        Completions.Drain();

        Component.BeginFrame();
        long allocated = FrameStats.Begin();

        root.Render(ui);

        FrameStats.End(allocated);

        // CUI sleeps between frames unless something asks for one. A mutation or a
        // live feed finishing on a pool thread cannot wake a sleeping window, so a
        // pending operation has to be reflected here.
        if (PendingWork.Any || Component.AnyWantFrames) ui.RequestRedraw();

        // This frame has drawn whatever finished before it, so the owed wake-up is
        // spent. Without this the loop would never sleep again after one operation.
        PendingWork.MarkDrawn();

        FrameTrace.Frame();
    }
}

/// <summary>Collects services and window options before the window exists.</summary>
public sealed class CuiApplicationBuilder
{
    private readonly ServiceCollection _services = new();

    internal CuiApplicationBuilder(string[] args) => Args = args;

    /// <summary>The container. Register services and components here.</summary>
    public IServiceCollection Services => _services;

    /// <summary>Command-line arguments, as handed to the process.</summary>
    public string[] Args { get; }

    /// <summary>Window options: title, size, renderer, theme, accent.</summary>
    public CuiAppOptions Window { get; set; } = new();

    /// <summary>Builds the container. The window itself is created by <see cref="CuiApplication.Run{TRoot}"/>.</summary>
    public CuiApplication Build() => new(_services.BuildServiceProvider(new ServiceProviderOptions
    {
        // Validate eagerly: a missing registration should fail at startup, not
        // halfway through the first frame.
        ValidateOnBuild = true,
        ValidateScopes = true,
    }), Window, Args);
}
