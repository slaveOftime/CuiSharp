using System.Text;
using CuiSharp.Tools;

// Everything this tool prints — protocol payloads, labels, build reports — is UTF-8, and the
// Windows legacy code page turns a label like "Ctrl+Tab · theme" into mojibake for both a person
// and an agent. A real console is fixed at the source by switching its output encoding; that
// throws IOException when the handle is not a console (redirected, piped, or detached), which is
// exactly when the explicit writers below are needed. Both paths end in UTF-8, and the writers
// are disposed on exit so nothing buffered is lost.
try
{
    Console.InputEncoding = Encoding.UTF8;
    Console.OutputEncoding = Encoding.UTF8;
}
catch (IOException)
{
    // Not a console handle: the redirected readers and writers below carry the encoding instead.
}

TextReader input = Console.In;
TextWriter output = Console.Out;
TextWriter error = Console.Error;
using StreamReader? redirectedIn = Console.IsInputRedirected
    ? new StreamReader(Console.OpenStandardInput(), Encoding.UTF8)
    : null;
using StreamWriter? redirectedOut = Console.IsOutputRedirected
    ? new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true }
    : null;
using StreamWriter? redirectedErr = Console.IsErrorRedirected
    ? new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true }
    : null;
if (redirectedIn is not null) input = redirectedIn;
if (redirectedOut is not null) output = redirectedOut;
if (redirectedErr is not null) error = redirectedErr;

return Cli.Run(args, output, error, input);
