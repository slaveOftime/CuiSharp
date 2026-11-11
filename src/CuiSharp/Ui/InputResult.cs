using CuiSharp.Interop;

namespace CuiSharp;

/// <summary>
/// What a text field reported this frame. CUI returns these as bit flags; the
/// struct keeps them readable and prevents an <c>int</c> being mistaken for a
/// count or an index.
/// </summary>
public readonly struct InputResult
{
    private readonly int _flags;

    internal InputResult(int flags) { _flags = flags; }

    /// <summary>The user edited the text; the value was written back.</summary>
    public bool Changed => (_flags & CuiFlags.CUI_CHANGED) != 0;

    /// <summary>The user pressed Enter (or the field's submit gesture).</summary>
    public bool Submitted => (_flags & CuiFlags.CUI_SUBMITTED) != 0;

    /// <summary>The field gained keyboard focus this frame.</summary>
    public bool Focused => (_flags & CuiFlags.CUI_FOCUSED) != 0;

    /// <summary>The field lost keyboard focus this frame.</summary>
    public bool Blurred => (_flags & CuiFlags.CUI_BLURRED) != 0;

    /// <summary>True when anything at all was reported.</summary>
    public bool Any => _flags != 0;

    /// <summary>The raw flag bits, for code that must forward them unchanged.</summary>
    public int Flags => _flags;

    public override string ToString()
    {
        if (_flags == 0) return "none";

        Span<char> text = stackalloc char[36];
        int length = 0;
        if (Changed) length = Append(text, length, "changed");
        if (Submitted) length = Append(text, length, "submitted");
        if (Focused) length = Append(text, length, "focused");
        if (Blurred) length = Append(text, length, "blurred");
        return new string(text[..length]);

        static int Append(Span<char> destination, int length, ReadOnlySpan<char> part)
        {
            if (length != 0)
            {
                destination[length++] = ',';
                destination[length++] = ' ';
            }

            part.CopyTo(destination[length..]);
            return length + part.Length;
        }
    }
}
