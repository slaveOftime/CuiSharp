// Page 21 of 28: syntax-highlighted code blocks and a collapsible JSON viewer.
// Part of the CuiSharp.Gallery sample: Code
using CuiSharp;

namespace CuiSharp.Gallery;

internal sealed partial class GalleryApp
{
    private const string CounterCode = """
        #include <cui/cui.h>

        /* A counter with a button */
        static int clicks = 0;

        void frame(cui_ctx *ui) {
            if (cui_button(ui, "Click me", .variant = CUI_BUTTON_PRIMARY)) clicks++;
            cui_textf(ui, "Clicked %d times", clicks);
            float ratio = clicks / 10.0f;   // grows with each click
        }

        """;

    private const string FibCode = """
        def fib(n: int) -> int:
            # the slow way, on purpose
            if n < 2:
                return n
            return fib(n - 1) + fib(n - 2)

        print([fib(k) for k in range(10)])

        """;

    private const string ShellCode = """
        git clone https://github.com/yappologistic/CUI.git
        cmake -S CUI -B build
        cmake --build build
        ./build/examples/gallery  # run it

        """;

    private const string PackageJson = """
        {"name": "cui", "version": "1.1.0", "private": false, "downloads": 128400,
         "platforms": ["windows", "macos", "linux"],
         "renderer": {"backend": "opengl", "fallback": "software", "vsync": true, "msaa": null},
         "authors": [{"name": "Maya", "commits": 412}, {"name": "Jonas", "commits": 388}]}
        """;

    private void PageCode(Ui ui)
    {
        Section(ui, "Code block", "Highlighting for C, C++, JavaScript, TypeScript, Python, Rust, Go, JSON and shell.");
        ui.CodeBlock("c-sample", CounterCode, new CodeOpts
        {
            Lang = "c",
            Title = "counter.c",
            LineNumbers = true,
            HighlightFrom = 7,
        });
        using (ui.Row(new Box { Gap = 16, Align = Align.Start, Width = Size.Fill }))
        {
            using (ui.Box(new Box { Width = Size.Grow(1) }))
            {
                ui.CodeBlock("py", FibCode, new CodeOpts { Lang = "python", Title = "fib.py" });
            }
            using (ui.Box(new Box { Width = Size.Grow(1) }))
            {
                ui.CodeBlock("sh", ShellCode, new CodeOpts { Lang = "shell" });
            }
        }

        Section(ui, "JSON viewer", "Collapsible, with counts. Click a brace to fold it.");
        ui.JsonView("json", PackageJson, new JsonOpts { ExpandDepth = 2 });
        ui.JsonView("broken", """{"ok": true, "oops": [1, 2,}""");
    }
}
