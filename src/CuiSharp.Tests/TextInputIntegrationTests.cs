using System.Text;
using CuiSharp.Interop;

namespace CuiSharp.Tests;

public class TextInputIntegrationTests
{
    [Theory]
    [InlineData("Input")]
    [InlineData("TextArea")]
    [InlineData("Search")]
    public void NativeEditsRoundTripThroughThePooledString(string widget)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var surface = CuiSurface.Create(320, 200);
        string value = "";

        InputResult Frame()
        {
            surface.BeginFrame();
            var ui = surface.Ui;
            InputResult result;
            using (ui.Column())
            {
                var opts = new InputOpts { AutoFocus = true };
                result = widget switch
                {
                    "Input" => ui.Input("edit", ref value, opts),
                    "TextArea" => ui.TextArea("edit", ref value, opts),
                    _ => ui.Search("edit", ref value, opts),
                };
            }
            surface.EndFrame();
            return result;
        }

        Frame(); // Establish native focus and layout.
        Native.InputText(surface.Ui.Handle, Utf8.Get("é"));
        InputResult result = Frame();
        Assert.True(result.Changed);
        Assert.False(result.Submitted);
        Assert.Equal("é", value);
        Assert.Equal(2, Encoding.UTF8.GetByteCount(value));

        // A frame without an edit must not re-encode or re-decode: the same
        // string instance comes back, so steady state allocates nothing.
        string settled = value;
        Frame();
        Assert.Same(settled, value);
    }
}
