using System.Runtime.InteropServices;
using Microsoft.JSInterop;

namespace WasmProbe;

public static class ProbeBridge
{
    public static string Result = "probing...";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint SwCreateDel();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SwDestroyDel(nint sw);

    [JSInvokable("InitSyms")]
    public static void InitSyms(int createPtr, int destroyPtr)
    {
        try
        {
            if (createPtr == 0 || destroyPtr == 0) { Report("cui-wasm-FAIL ptrs"); return; }
            var create = Marshal.GetDelegateForFunctionPointer<SwCreateDel>(createPtr);
            var destroy = Marshal.GetDelegateForFunctionPointer<SwDestroyDel>(destroyPtr);
            var sw = create();
            var ok = sw != 0;
            if (ok) destroy(sw);
            Report(ok ? "cui-wasm-ok" : "cui-wasm-FAIL");
        }
        catch (Exception e) { Report("cui-wasm-EX " + e.GetType().Name + " " + e.Message); }
    }

    private static void Report(string result)
    {
        Result = result;
        Console.WriteLine("PROBE-RESULT " + result);
    }
}
