using CuiSharp.Interop;

namespace CuiSharp;

/// <summary>
/// The hex viewer, including the editing case the generated wrapper cannot express.
/// </summary>
/// <remarks>
/// The generated <c>ui.HexView(id, ReadOnlySpan&lt;byte&gt;, ...)</c> copies the bytes into the
/// frame arena, which is right for a read-only view but wrong for an editor: native writes
/// the typed byte through the <c>edit</c> pointer, and a write into the arena copy would be
/// lost. This overload pins the caller's own memory for the call instead, so the edit lands
/// in the array the caller holds.
/// </remarks>
public static unsafe class HexUiExtensions
{
    extension(Ui ui)
    {
        /// <summary>
        /// Draws <paramref name="bytes"/> and lets the user overwrite them in place.
        /// </summary>
        /// <returns>
        /// What the view reported: the selection, and which byte changed when
        /// <see cref="HexResult.Edited"/> is set.
        /// </returns>
        public HexResult HexView(UiText id, Span<byte> bytes, ref HexSelection selection, HexViewOpts o = default)
        {
            fixed (byte* p = bytes)
            {
                var sel = selection.ToNative();
                var opts = o.ToNative();
                opts.Edit = (nint)p;   // the same memory: edits write straight into the span
                var result = Native.HexView(ui.Handle, Utf8.Get(id), (nint)p, (nuint)bytes.Length, ref sel, in opts);
                selection = HexSelection.FromNative(sel);
                WidgetRecorder.Note(ui.Handle, "HexView", id);
                return HexResult.FromNative(result);
            }
        }
    }
}
