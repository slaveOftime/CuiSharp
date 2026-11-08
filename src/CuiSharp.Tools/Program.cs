using System.Text;
using CuiSharp.Tools;

// A redirected stdout is read by a tool, a script or an agent, so it is UTF-8 — the encoding the
// protocol is defined in. `Console.Out` follows the Windows console code page instead, which turns
// a label like "Ctrl+Tab · theme" into bytes that are not UTF-8 at all. A terminal keeps its own
// encoding, because a person is looking at it.
if (Console.IsOutputRedirected)
{
    using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
    return Cli.Run(args, stdout, Console.Error, Console.In);
}

return Cli.Run(args, Console.Out, Console.Error, Console.In);
