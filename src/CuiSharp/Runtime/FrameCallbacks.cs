using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CuiSharp;

/// <summary>
/// Holds managed delegates that C code calls back into during a frame.
/// </summary>
/// <remarks>
/// Native receives a <see cref="GCHandle"/> token, never a managed object's address. Widget
/// callbacks use slots owned by the host through paint. The tree sorter and persistent state
/// disposer use the separately keyed thread-local registrations.
/// </remarks>
internal static unsafe class FrameCallbacks
{
    private sealed class Holder
    {
        internal Delegate Function = null!;
    }

    // Each submission gets a distinct slot even if two widgets have the same id. The host
    // resets the slots only after native has finished the last frame (including paint).
    internal sealed class WidgetSlots(UiFrameState owner)
    {
        private readonly List<WidgetSlot> _slots = [];
        private int _used;

        private WidgetSlot Rent()
        {
            if (_used == _slots.Count)
            {
                var slot = new WidgetSlot(owner);
                slot.Handle = GCHandle.Alloc(slot);
                _slots.Add(slot);
            }
            return _slots[_used++];
        }

        internal nint RentAxis(Func<double, string?> label)
        {
            var slot = Rent();
            slot.AxisLabel = label;
            return GCHandle.ToIntPtr(slot.Handle);
        }

        internal nint RentTree(Func<int, string?>? label, Func<int, TreeDrop, bool>? canDrop)
        {
            var slot = Rent();
            slot.TreeLabel = label;
            slot.CanDrop = canDrop;
            return GCHandle.ToIntPtr(slot.Handle);
        }

        internal void Reset()
        {
            for (int i = 0; i < _used; i++)
            {
                _slots[i].AxisLabel = null;
                _slots[i].TreeLabel = null;
                _slots[i].CanDrop = null;
            }
            _used = 0;
        }

        internal void Release()
        {
            foreach (var slot in _slots) slot.Handle.Free();
            _slots.Clear();
            _used = 0;
        }
    }

    private sealed class WidgetSlot(UiFrameState owner)
    {
        internal GCHandle Handle;
        internal Func<double, string?>? AxisLabel;
        internal Func<int, string?>? TreeLabel;
        internal Func<int, TreeDrop, bool>? CanDrop;

        internal void Report(Exception error)
        {
            if (owner.Faults is { } faults) faults.Add(error);
            else
            {
                // Reporting to a redirected/closed stream must not unwind into C either.
                try { Console.Error.WriteLine(error); }
                catch (Exception) { }
            }
        }
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
    internal static nint Rent(string key, Delegate function)
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

    internal static nint RentAxisLabel(UiFrameState frame, Func<double, string?> label) =>
        (frame.Callbacks ??= new(frame)).RentAxis(label);

    internal static nint RentTreeHooks(UiFrameState frame, Func<int, string?>? label, Func<int, TreeDrop, bool>? canDrop) =>
        (frame.Callbacks ??= new(frame)).RentTree(label, canDrop);

    private static Holder? HolderOf(void* user) =>
        user == null ? null : GCHandle.FromIntPtr((nint)user).Target as Holder;

    private static WidgetSlot? WidgetOf(void* user) =>
        user == null ? null : GCHandle.FromIntPtr((nint)user).Target as WidgetSlot;

    /// <summary>The tree table's <c>const char *(*)(void *user, int node)</c>.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static nint TreeLabelThunk(void* user, int node)
    {
        var live = Features.Live ? Hosting.LiveSession.CurrentThread : null;
        live?.EnterCallback();
        try
        {
            var slot = WidgetOf(user);
            try
            {
                // CUI reads the pointer once we have returned; a pin inside this thunk is too short.
                return Utf8.Get(slot?.TreeLabel?.Invoke(node));
            }
            catch (Exception error)
            {
                slot?.Report(error);
                return 0;
            }
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
    internal static void AxisLabelThunk(void* user, double value, byte* output, nuint capacity)
    {
        var live = Features.Live ? Hosting.LiveSession.CurrentThread : null;
        live?.EnterCallback();
        try
        {
            if (output == null || capacity == 0) return;
            var slot = WidgetOf(user);
            var destination = new Span<byte>(output, (int)Math.Min(capacity, (nuint)int.MaxValue));
            destination[0] = 0;
            try
            {
                WriteAxisLabel(slot?.AxisLabel?.Invoke(value), destination);
            }
            catch (Exception error)
            {
                slot?.Report(error);
            }
        }
        finally
        {
            live?.ExitCallback();
        }
    }

    internal static void WriteAxisLabel(string? text, Span<byte> destination)
    {
        if (destination.IsEmpty) return;
        int length = 0;
        if (text is not null)
            foreach (var rune in text.EnumerateRunes())
            {
                if (rune.Value == 0 || rune.Utf8SequenceLength >= destination.Length - length) break;
                length += rune.EncodeToUtf8(destination[length..]);
            }
        destination[length] = 0;
    }

    /// <summary>The tree table's <c>bool (*)(void *user, int target, int where)</c>: 1 allows the drop.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static byte CanDropThunk(void* user, int target, int where)
    {
        var live = Features.Live ? Hosting.LiveSession.CurrentThread : null;
        live?.EnterCallback();
        try
        {
            var slot = WidgetOf(user);
            try
            {
                return slot?.CanDrop?.Invoke(target, (TreeDrop)where) == true ? (byte)1 : (byte)0;
            }
            catch (Exception error)
            {
                slot?.Report(error);
                return 0;
            }
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
    internal static nint AxisLabelPointer => (nint)(delegate* unmanaged[Cdecl]<void*, double, byte*, nuint, void>)&AxisLabelThunk;

    /// <summary>The static function pointer a can-drop hook needs.</summary>
    internal static nint CanDropPointer => (nint)(delegate* unmanaged[Cdecl]<void*, int, int, byte>)&CanDropThunk;

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
