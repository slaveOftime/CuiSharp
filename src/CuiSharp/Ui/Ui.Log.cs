using CuiSharp.Interop;

namespace CuiSharp;

/// <summary>Draws a <see cref="LogBuffer"/>: the lines plus the filter and search toolbar.</summary>
public static class LogUiExtensions
{
    extension(Ui ui)
    {
        /// <summary>
        /// Draws <paramref name="log"/> with its toolbar: level filters, search with
        /// highlight, Follow, Copy and Clear. Scrolling up turns Follow off; reaching the
        /// bottom turns it back on.
        /// </summary>
        public void LogView(UiText id, LogBuffer log, LogViewOpts o = default)
        {
            ArgumentNullException.ThrowIfNull(log);
            log.Draw(ui.Handle, Utf8.Get(id), o.ToNative());
            WidgetRecorder.Note(ui.Handle, "LogView", id);
        }
    }
}
