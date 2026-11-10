using System.Runtime.InteropServices;

namespace CuiSharp;

/// <summary>
/// The native library that draws could not be loaded, or does not match the managed one. The
/// loader's own message has no context — a real app reported
/// <c>version `GLIBC_2.43' not found</c>, raised at the first frame, inside a test — so this type
/// names what was shipped, what it promises, and what to do about it.
/// </summary>
public sealed class CuiSharpNativeException : InvalidOperationException
{
    internal CuiSharpNativeException(string message, Exception? inner) : base(message, inner) { }

    /// <summary>Explains a failed native call, keeping the loader's message as the inner error.</summary>
    internal static CuiSharpNativeException From(Exception error)
    {
        string what = error switch
        {
            DllNotFoundException =>
                "The native CUI library is missing: this build carries no library for the platform, " +
                "or it was removed from the output.",
            BadImageFormatException =>
                "The native CUI library has the wrong architecture or format for this process: a " +
                "64-bit app with a 32-bit library, or a library built against a different libc.",
            EntryPointNotFoundException =>
                "The native CUI library does not match this managed assembly. The two ship as a " +
                "pair: the same package version, the same RID.",
            _ => "The native CUI library could not be used.",
        };

        return new CuiSharpNativeException(
            $"""
            {what}

            Packaged RIDs: {CuiNative.Rids}.
            Linux binaries need glibc {CuiNative.LinuxFloor} or newer; an older container, distribution
            or WSL image fails at load with "version `GLIBC_x.y' not found".

            To build the native library for this machine, run native/build-native.sh (or .ps1) in the
            CuiSharp repository, then point the build at it with the RID's -p:CuiNative*Dir
            (WinDir, WinArm64Dir, LinuxDir, LinuxArm64Dir or OsxDir). See skills/cui-dotnet/native-abi.md.

            Loader message: {error.Message}
            """,
            error);
    }
}

/// <summary>
/// What the packaged native library promises. Kept here as one place the build and the docs are
/// checked against (<c>NativeAbiClaimTests</c> reads <c>CuiSharp.targets</c> and
/// <c>skills/cui-dotnet/native-abi.md</c> and fails when they disagree with this).
/// </summary>
internal static class CuiNative
{
    /// <summary>The RIDs the package carries a native library for.</summary>
    internal static readonly string[] RidList = ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-arm64"];

    /// <summary>The glibc floor the Linux archive is built against.</summary>
    internal const string LinuxFloor = "2.31";

    /// <summary>The RIDs as one readable string.</summary>
    internal static string Rids => string.Join(", ", RidList);
}
