using CuiSharp.Interop;

namespace CuiSharp;

/// <summary>
/// The hand-written parts of the surface CUI exposes but the generator cannot shape on
/// its own: the terminal, whose buffer the app owns, and the frame wakeup timer.
/// </summary>
public static unsafe class TerminalUiExtensions
{
    extension(Ui ui)
    {
        /// <summary>
        /// Draws <paramref name="terminal"/> and reports what the user did in it.
        /// </summary>
        /// <remarks>
        /// The terminal buffer lives in <see cref="Terminal"/>, which owns the text and
        /// the line ring, so the widget outlives a resize or a theme change. Nothing here
        /// allocates per frame: the scrollback is written by <see cref="Terminal.Write(string?)"/>
        /// and the frame only draws it.
        /// </remarks>
        public TerminalResult Terminal(UiText id, Terminal terminal, TerminalOpts o = default)
        {
            ArgumentNullException.ThrowIfNull(terminal);
            TerminalResult result = terminal.Draw(ui.Handle, Utf8.Get(id), o.ToNative());
            WidgetRecorder.Note(ui.Handle, "Terminal", id);
            return result;
        }

        /// <summary>
        /// Asks for one more frame after <paramref name="seconds"/>, even when nothing else
        /// would wake the window. Use it for a timer, a toast timeout or a poll interval.
        /// </summary>
        /// <remarks>
        /// A redraw request is immediate; this one is delayed. The nearest deadline wins
        /// when several are pending.
        /// </remarks>
        public void RequestWakeup(float seconds) => Native.RequestWakeup(ui.Handle, seconds);
    }
}
