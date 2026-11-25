using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CuiSharp;

/// <summary>
/// Holds managed delegates that C code calls back into during a frame.
/// </summary>
/// <remarks>
/// <para>
/// CUI takes a C function pointer plus a <c>void *user</c> for the hooks it calls itself
/// (the tree table's type-ahead label, the tree sorter's comparator, a state disposer). A
/// managed object's address may not be handed to C, so each delegate is wrapped in a
/// <see cref="Holder"/> kept alive by a <see cref="GCHandle"/>, and the handle's address is
/// what CUI gets as <c>user</c>.
/// </para>
/// <para>
/// The handle is kept per <em>key</em> (the widget id, the sort's name) and reused, so even a
/// caller that passes a fresh closure every frame only pays for the closure: the handle
/// address never changes. Handles nobody asked for in the frame that just ended are released
/// at the start of the next one, which is after the last frame that could still call them.
/// </para>
/// </remarks>
internal static unsafe class FrameCallbacks
{
    /// <summary>What a <c>void *user</c> points at: the delegate and the bytes a string hook returned.</summary>
    internal sealed class Holder
    {
        /// <summary>The delegate — or, when one user pointer serves two hooks, a hooks object.</summary>
        internal object Function = null!;

        /// <summary>The UTF-8 buffer a label hook returned, kept until it is called again.</summary>
        internal byte[]? Text;
    }

    /// <summary>
    /// The tree table calls <c>label</c> and <c>can_drop</c> with one shared <c>void *user</c>,
    /// so both delegates travel in one holder.
    /// </summary>
    internal sealed class TreeHooks
    {
        internal Func<int, string?>? Label;
        internal Func<int, int, bool>? CanDrop;
    }

    private sealed class Slot
    {
        internal Holder Holder = new();
        internal GCHandle Handle;
        internal long Frame;
    }

    // A CUI context belongs to one thread, so do the handles it registered: keeping this
    // per thread also means two surfaces drawn on different threads never share a table.
    [ThreadStatic] private static Dictionary<string, Slot>? _slots;
    [ThreadStatic] private static long _frame;

    private static Dictionary<string, Slot> Slots => _slots ??= new(StringComparer.Ordinal);

    /// <summary>Releases the handles nobody asked for in the frame that just ended.</summary>
    internal static void BeginFrame()
    {
        _frame++;
        if (Slots.Count == 0) return;

        List<string>? dead = null;
        foreach (var (key, slot) in Slots)
        {
            if (slot.Frame == long.MaxValue || slot.Frame >= _frame - 1) continue;
            (dead ??= []).Add(key);
        }

        if (dead is null) return;
        foreach (string key in dead)
        {
            Slot slot = Slots[key];
            if (slot.Handle.IsAllocated) slot.Handle.Free();
            Slots.Remove(key);
        }
    }

    /// <summary>
    /// Returns the <c>void *user</c> for <paramref name="function"/> under <paramref name="key"/>.
    /// The handle is allocated once per key and its address never changes.
    /// </summary>
    internal static nint Rent(string key, Delegate function) => Rent(key, (object)function);

    /// <summary>Rents a holder for an arbitrary hooks object, e.g. a tree table's label and can-drop pair.</summary>
    internal static nint Rent(string key, object function)
    {
        if (!Slots.TryGetValue(key, out var slot))
        {
            slot = new Slot();
            slot.Holder.Function = function;
            slot.Handle = GCHandle.Alloc(slot.Holder);
            Slots[key] = slot;
        }
        else
        {
            slot.Holder.Function = function;
        }

        slot.Frame = _frame;
        return GCHandle.ToIntPtr(slot.Handle);
    }

    /// <summary>
    /// Rents the tree table's shared hooks holder: CUI passes one <c>void *user</c> to both
    /// <c>label</c> and <c>can_drop</c>, so the delegates travel in one holder whose hooks object
    /// is reused — a steady frame only rebinds the fields and allocates nothing.
    /// </summary>
    internal static nint RentTreeHooks(string key, Func<int, string?>? label, Func<int, int, bool>? canDrop)
    {
        if (!Slots.TryGetValue(key, out var slot))
        {
            slot = new Slot();
            slot.Holder.Function = new TreeHooks();
            slot.Handle = GCHandle.Alloc(slot.Holder);
            Slots[key] = slot;
        }

        var hooks = (TreeHooks)slot.Holder.Function;
        hooks.Label = label;
        hooks.CanDrop = canDrop;
        slot.Frame = _frame;
        return GCHandle.ToIntPtr(slot.Handle);
    }

    /// <summary>The holder behind a <c>void *user</c>, or null when it was already released.</summary>
    private static Holder? HolderOf(void* user) =>
        user == null ? null : GCHandle.FromIntPtr((nint)user).Target as Holder;

    /// <summary>
    /// Encodes the string a <c>const char *</c> hook returns into the holder's own buffer:
    /// CUI keeps the pointer only for the frame, and the buffer outlives one call.
    /// </summary>
    private static nint Text(Holder holder, string? text)
    {
        if (text is null)
        {
            holder.Text = null;
            return 0;
        }

        byte[] buffer = holder.Text ??= new byte[64];
        int needed = System.Text.Encoding.UTF8.GetByteCount(text) + 1;
        if (buffer.Length < needed) buffer = holder.Text = new byte[Math.Max(needed, buffer.Length * 2)];

        int written = System.Text.Encoding.UTF8.GetBytes(text, buffer);
        buffer[written] = 0;
        fixed (byte* p = buffer) return (nint)p;
    }

    /// <summary>The tree table's <c>const char *(*)(void *user, int node)</c>.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static nint TreeLabelThunk(void* user, int node)
    {
        var live = Features.Live ? Hosting.LiveSession.CurrentThread : null;
        live?.EnterCallback();
        try
        {
            Holder? holder = HolderOf(user);
            string? text = holder?.Function switch
            {
                TreeHooks { Label: { } label } => label(node),
                Func<int, string?> label => label(node),
                _ => null,
            };
            return holder is null || text is null ? 0 : Text(holder, text);
        }
        finally
        {
            live?.ExitCallback();
        }
    }

    /// <summary>The tree sorter's <c>int (*cmp)(void *user, int a, int b)</c>.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static int TreeCompareThunk(void* user, int a, int b)
    {
        var live = Features.Live ? Hosting.LiveSession.CurrentThread : null;
        live?.EnterCallback();
        try
        {
            return HolderOf(user)?.Function is Comparison<int> compare ? compare(a, b) : 0;
        }
        finally
        {
            live?.ExitCallback();
        }
    }

    /// <summary>The axis labels' <c>void (*)(void *user, double value, char *out, size_t capacity)</c>: the label is written into CUI's own buffer, truncated to fit and always terminated.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe void AxisLabelThunk(void* user, double value, char* out_, nuint capacity)
    {
        var live = Features.Live ? Hosting.LiveSession.CurrentThread : null;
        live?.EnterCallback();
        try
        {
            if (capacity == 0) return;
            string? text = HolderOf(user)?.Function is Func<double, string?> label ? label(value) : null;
            var dest = new Span<byte>((byte*)out_, (int)capacity);
            if (string.IsNullOrEmpty(text) || dest.Length == 1) { dest[0] = 0; return; }

            // Drop characters until the encoding is certain to fit with room for the
            // terminator; GetMaxByteCount is an upper bound, so the encode below cannot throw.
            ReadOnlySpan<char> chars = text;
            while (chars.Length > 1 && System.Text.Encoding.UTF8.GetMaxByteCount(chars.Length) > dest.Length - 1)
                chars = chars[..(chars.Length / 2)];
            int written = System.Text.Encoding.UTF8.GetBytes(chars, dest[..^1]);
            dest[written] = 0;
        }
        finally
        {
            live?.ExitCallback();
        }
    }

    /// <summary>The tree table's <c>bool (*)(void *user, int target, int where)</c>: 1 allows the drop.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static int CanDropThunk(void* user, int target, int where)
    {
        var live = Features.Live ? Hosting.LiveSession.CurrentThread : null;
        live?.EnterCallback();
        try
        {
            return HolderOf(user)?.Function switch
            {
                TreeHooks { CanDrop: { } canDrop } => canDrop(target, where) ? 1 : 0,
                Func<int, int, bool> canDrop => canDrop(target, where) ? 1 : 0,
                _ => 1,
            };
        }
        finally
        {
            live?.ExitCallback();
        }
    }

    /// <summary>
    /// <c>void (*fn)(cui_ctx *, void *)</c>: the disposer for a per-id state block. The block
    /// starts with the handle it was registered under, which is the only way to find the
    /// delegate again: CUI passes no user pointer.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void StateDisposerThunk(nint ui, void* data)
    {
        _ = ui;
        if (data == null) return;
        nint user = *(nint*)data;
        if (user == 0) return;
        var live = Features.Live ? Hosting.LiveSession.ForContext(ui) : null;
        live?.EnterCallback();
        try
        {
            if (HolderOf((void*)user)?.Function is Action onFree) onFree();
        }
        finally
        {
            live?.ExitCallback();
        }
    }

    /// <summary>The static function pointer a label hook needs.</summary>
    internal static nint TreeLabelPointer => (nint)(delegate* unmanaged[Cdecl]<void*, int, nint>)&TreeLabelThunk;

    /// <summary>The static function pointer a comparator needs.</summary>
    internal static nint TreeComparePointer => (nint)(delegate* unmanaged[Cdecl]<void*, int, int, int>)&TreeCompareThunk;

    /// <summary>The static function pointer an axis-label hook needs.</summary>
    internal static nint AxisLabelPointer => (nint)(delegate* unmanaged[Cdecl]<void*, double, char*, nuint, void>)&AxisLabelThunk;

    /// <summary>The static function pointer a can-drop hook needs.</summary>
    internal static nint CanDropPointer => (nint)(delegate* unmanaged[Cdecl]<void*, int, int, int>)&CanDropThunk;

    /// <summary>The static function pointer a state disposer needs.</summary>
    internal static nint StateDisposerPointer => (nint)(delegate* unmanaged[Cdecl]<nint, void*, void>)&StateDisposerThunk;

    /// <summary>
    /// A handle that has to outlive a frame. CUI calls a state disposer whenever it evicts
    /// that id, which can be many frames later, so this one is never released on its own.
    /// </summary>
    internal static nint RentPersistent(string key, Delegate function)
    {
        if (Slots.TryGetValue(key, out var slot))
        {
            slot.Holder.Function = function;
            slot.Frame = long.MaxValue;
            return GCHandle.ToIntPtr(slot.Handle);
        }

        var created = new Slot { Frame = long.MaxValue };
        created.Holder.Function = function;
        created.Handle = GCHandle.Alloc(created.Holder);
        Slots[key] = created;
        return GCHandle.ToIntPtr(created.Handle);
    }
}
