using System.Runtime.InteropServices;
using CuiSharp.Interop;

namespace CuiSharp;

/// <summary>
/// The HTTP and URL helpers the request bar and the parameters table share: a status line's
/// reason, a method's name, and a URL's query kept in step with a list of
/// <see cref="KeyValue"/> pairs. They take no <see cref="Ui"/>, so they work on a click and in
/// a unit test alike.
/// </summary>
public static unsafe class CuiHttp
{
    /// <summary>The reason phrase for a status code, e.g. "Not Found"; empty when CUI does not know it.</summary>
    public static string Reason(int status) => Marshal.PtrToStringUTF8(Native.HttpReason(status)) ?? "";

    /// <summary>The name of a method, e.g. "PATCH".</summary>
    public static string MethodName(HttpMethod method) => Marshal.PtrToStringUTF8(Native.HttpMethodName((int)method)) ?? "";

    /// <summary>
    /// Decodes a URL's query into pairs, the way the request bar's parameters table shows it.
    /// </summary>
    /// <param name="url">The URL to read the query from; its fragment is ignored.</param>
    /// <param name="capacity">The most pairs to read; a longer query is truncated.</param>
    /// <returns>Each <c>key=value</c>, decoded, with <see cref="KeyValue.Enabled"/> on.</returns>
    public static KeyValue[] ParseQuery(string url, int capacity = 64)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        var native = new NativeKvPair[capacity];
        int count;
        // No frame is implied: give the arena's bytes back before returning.
        using (Utf8.Scope())
        {
            fixed (NativeKvPair* pairs = native)
            {
                count = Native.UrlParams(Utf8.Get(url), (nint)pairs, capacity);
            }
        }

        var result = new KeyValue[count];
        for (int i = 0; i < count; i++) result[i] = KeyValue.FromNative(native[i]);
        return result;
    }

    /// <summary>
    /// Rewrites <paramref name="url"/>'s query from <paramref name="pairs"/>, encoding each key
    /// and value and keeping any fragment. Disabled pairs and pairs with no key are left out,
    /// so one list can drive both a live request and a table with rows that are switched off.
    /// </summary>
    /// <param name="url">The URL whose query is rewritten; its path and fragment are kept.</param>
    /// <param name="pairs">The parameters, in the order they should appear.</param>
    /// <param name="capacity">The longest URL to produce.</param>
    /// <returns>The new URL, or null when it did not fit.</returns>
    public static string? SetQuery(string url, ReadOnlySpan<KeyValue> pairs, int capacity = 8192)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        var native = new NativeKvPair[pairs.Length];
        for (int i = 0; i < pairs.Length; i++) native[i] = pairs[i].ToNative();

        // The URL is edited in place, so it needs its own copyable bytes rather than the arena.
        var buffer = new byte[capacity];
        fixed (byte* text = buffer)
        fixed (NativeKvPair* source = native)
        {
            Utf8.WriteFixed(url, text, capacity);
            return Native.UrlSetParams((nint)text, (ulong)capacity, (nint)source, pairs.Length) == 0
                ? null
                : Utf8.ReadFixed(text, capacity);
        }
    }
}
