using System.IO.MemoryMappedFiles;
using CuiSharp.Interop;

namespace CuiSharp;

internal static unsafe class SystemFontFile
{
    // CUI uses only the first face of a collection but its file loader copies the entire file.
    // Keep a read-only view instead, so untouched font tables need not be read or privately committed.
    // The caller owns the returned view and must dispose it only after destroying the context.
    internal static Font Load(nint ctx, string path, out MemoryMappedViewAccessor? backing)
    {
        backing = null;
        var view = TryMap(path);
        if (view is null) return Native.FontLoadFile(ctx, Utf8.Get(path));

        try
        {
            byte* bytes = null;
            var handle = view.SafeMemoryMappedViewHandle;
            handle.AcquirePointer(ref bytes);
            Font face;
            try
            {
                face = Native.FontLoadMemory(ctx, (nint)(bytes + view.PointerOffset), (nuint)view.Capacity, 0);
            }
            finally
            {
                handle.ReleasePointer();
            }

            if ((int)face > (int)Font.Icons) backing = view;
            return face;
        }
        finally
        {
            if (backing is null) view.Dispose();
        }
    }

    private static MemoryMappedViewAccessor? TryMap(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            long length = file.Length;
            if (length < 12) return null;

            using var mapping = MemoryMappedFile.CreateFromFile(file, null, 0, MemoryMappedFileAccess.Read,
                HandleInheritability.None, leaveOpen: true);
            return mapping.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Some files or filesystems cannot be mapped; preserve the ordinary file-load path.
            return null;
        }
    }
}
