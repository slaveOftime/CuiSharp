using System;
using CuiSharp;

internal static class Program
{
    private static void Main()
    {
        using var surface = CuiSurface.Create(640, 480, threads: 1);
        surface.BeginFrame();

        // Keep this probe free of LayoutScope/OpenScope until the generated AOT end thunks land.
        var ui = surface.Ui;
        ui.Heading("CUI in WebAssembly", 1);
        ui.Text("Direct LibraryImport calls from the browser runtime.");
        ui.Button("Ready");

        surface.EndFrame();
        var pixels = new byte[surface.PixelWidth * surface.PixelHeight * 4];
        surface.Render(pixels, PixelFormat.Rgba);
        Console.WriteLine($"CUI-DIRECT-PINVOKE-OK commands={surface.CommandCount} firstPixel={pixels[0]}");
    }
}
