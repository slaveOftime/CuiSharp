using System.Diagnostics.CodeAnalysis;

namespace CuiSharp;

/// <summary>
/// Switches the trimmer and the NativeAOT compiler act on, so an app that does not need a
/// feature does not pay for it in the binary.
/// </summary>
/// <remarks>
/// A switch is turned off in the app's project file, which is where a size decision belongs:
/// <code>
/// &lt;ItemGroup&gt;
///   &lt;RuntimeHostConfigurationOption Include="CuiSharp.Headless" Value="false" Trim="true" /&gt;
/// &lt;/ItemGroup&gt;
/// </code>
/// </remarks>
internal static class Features
{
    /// <summary>
    /// The headless protocol tools drive an app with (<c>dotnet cui</c>). On by default: an app
    /// that cannot be inspected by a tool is an app an agent cannot help with. Turning it off
    /// removes it, and <c>dotnet cui</c> then says the app did not start headless.
    /// </summary>
    [FeatureSwitchDefinition("CuiSharp.Headless")]
    internal static bool Headless => !AppContext.TryGetSwitch("CuiSharp.Headless", out bool on) || on;
}
