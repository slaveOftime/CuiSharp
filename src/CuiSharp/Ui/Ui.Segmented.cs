namespace CuiSharp;

public static class SegmentedValueUiExtensions
{
    extension(Ui ui)
    {
        /// <summary>Returns the chosen index on a change, otherwise null.</summary>
        public int? Segmented(UiText id, ReadOnlySpan<string> items, int value, SegmentedOpts o = default)
            => ui.Segmented(id, items, ref value, o) ? value : null;
    }
}
