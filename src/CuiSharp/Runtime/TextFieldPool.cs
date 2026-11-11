using System.Runtime.InteropServices;
using System.Text;

namespace CuiSharp;

/// <summary>
/// Per-id UTF-8 buffers behind <c>ui.Input("name", ref _name)</c>.
/// <para>
/// CUI reads and writes a NUL-terminated buffer, so the value an editable field
/// owns cannot be a managed string. The pool keeps one unmanaged buffer per
/// widget id: the caller's string is encoded only when it is a different
/// instance than last frame, and decoded only on the frames native code reports
/// an edit. Steady state costs no allocation and no copying.
/// </para>
/// <para>
/// Entries unused for <see cref="IdleFrames"/> frames are freed, so a dialog's
/// fields do not live forever after it closes.
/// </para>
/// </summary>
internal sealed unsafe class TextFieldPool
{
    internal const int IdleFrames = 600;

    internal sealed class Slot
    {
        internal byte* Buffer;
        internal int Capacity;

        /// <summary>The managed string currently encoded in <see cref="Buffer"/>.</summary>
        internal string? Encoded;

        internal int LastSeen;
    }

    private readonly Dictionary<uint, Slot> _slots = new();
    private int _frame;

    internal void BeginFrame()
    {
        _frame++;
        if (_slots.Count == 0 || _frame % 60 != 0) return;

        List<uint>? stale = null;
        foreach (var (id, slot) in _slots)
        {
            if (_frame - slot.LastSeen > IdleFrames) (stale ??= []).Add(id);
        }
        if (stale is null) return;

        foreach (uint id in stale) Remove(id);
    }

    /// <summary>
    /// Returns the slot for <paramref name="id"/>, encoding <paramref name="value"/>
    /// when it differs from what the buffer already holds.
    /// </summary>
    internal Slot Rent(uint id, string? value, int capacityHint)
    {
        if (!_slots.TryGetValue(id, out var slot))
        {
            slot = new Slot { Capacity = Math.Max(capacityHint, 64) };
            slot.Buffer = (byte*)NativeMemory.AllocZeroed((nuint)slot.Capacity);
            _slots.Add(id, slot);
        }
        else if (capacityHint > slot.Capacity)
        {
            // The caller asked for more room than the buffer has: the widget is told the
            // slot's capacity, so growing only on demand would under-report it.
            byte* grown = (byte*)NativeMemory.AllocZeroed((nuint)capacityHint);
            NativeMemory.Free(slot.Buffer);
            slot.Buffer = grown;
            slot.Capacity = capacityHint;
            slot.Encoded = null;   // the value must be written into the new buffer
        }

        slot.LastSeen = _frame;
        if (!ReferenceEquals(slot.Encoded, value)) Write(slot, value);
        return slot;
    }

    /// <summary>Decodes a buffer native code just edited and records it as current.</summary>
    internal static string Decode(Slot slot)
    {
        int length = 0;
        while (slot.Buffer[length] != 0) length++;
        string value = length == 0 ? string.Empty : Encoding.UTF8.GetString(slot.Buffer, length);
        slot.Encoded = value;
        return value;
    }

    /// <summary>
    /// True when native code changed the text in the buffer since it was last written or
    /// decoded. Compares bytes, so a widget with no "changed" flag (a composer, a
    /// property row) can still avoid allocating a string per frame.
    /// </summary>
    internal static bool Changed(Slot slot)
    {
        string? encoded = slot.Encoded ?? string.Empty;
        for (var i = 0; i < encoded.Length; i++)
        {
            byte b = slot.Buffer[i];
            if (b == 0) return true;
            if (b < 0x80)
            {
                if (b != encoded[i]) return true;
                continue;
            }

            // A multi-byte character: decode just this one and compare.
            int need = b >= 0xF0 ? 4 : b >= 0xE0 ? 3 : 2;
            string one = Encoding.UTF8.GetString(slot.Buffer, need);
            if (i + one.Length > encoded.Length || string.CompareOrdinal(encoded, i, one, 0, one.Length) != 0) return true;
            i += one.Length - 1;
        }
        return slot.Buffer[encoded.Length] != 0;
    }

    /// <summary>Marks the buffer as written by the caller, so it is not re-encoded.</summary>
    internal static void Filled(Slot slot, string? value) => slot.Encoded = value;

    private static void Write(Slot slot, string? value)
    {
        value ??= string.Empty;

        int bytes = Encoding.UTF8.GetByteCount(value);
        int needed = bytes + 1;
        if (needed > slot.Capacity)
        {
            int capacity = Math.Max(needed, slot.Capacity * 2);
            byte* grown = (byte*)NativeMemory.AllocZeroed((nuint)capacity);
            NativeMemory.Free(slot.Buffer);
            slot.Buffer = grown;
            slot.Capacity = capacity;
        }

        Encoding.UTF8.GetBytes(value, new Span<byte>(slot.Buffer, bytes));
        slot.Buffer[bytes] = 0;
        slot.Encoded = value;
    }

    private void Remove(uint id)
    {
        if (!_slots.Remove(id, out var slot)) return;
        NativeMemory.Free(slot.Buffer);
        slot.Buffer = null;
    }

    /// <summary>Frees every buffer. For tests and shutdown.</summary>
    internal void Clear()
    {
        foreach (var (_, slot) in _slots)
        {
            NativeMemory.Free(slot.Buffer);
            slot.Buffer = null;
        }

        _slots.Clear();
        _frame = 0;
    }
}
