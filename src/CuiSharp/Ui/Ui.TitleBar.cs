using CuiSharp.Interop;

namespace CuiSharp;

public unsafe ref partial struct Ui
{
    /// <summary>True when this window has opted into an app-drawn caption and the backend supports it.</summary>
    public bool HasCustomTitleBar => _frame.TitleBar is not null;

    /// <summary>Draws a standard themed title bar. For a custom layout, compose a regular row with
    /// <see cref="TitleBarDragRegion"/> and <see cref="TitleBarButton"/> instead.</summary>
    /// <remarks>Draw at the top of the frame. Window features require <see cref="CuiAppOptions.CustomTitleBar"/>;
    /// elsewhere this draws a themed heading without window controls.</remarks>
    public void TitleBar(UiText title)
    {
        using var bar = this.Column(new Box { Width = Size.Fill });
        using (this.Row(new Box { Width = Size.Fill, Height = 48, Align = Align.Center, PadX = 12, Gap = 10, Bg = Color.SurfaceMuted }))
        {
            using (TitleBarDragRegion(new Box { Dir = Dir.Row, Width = Size.Fill, Height = Size.Fill, Clip = true }))
                this.Text(title, new TextOpts { Font = Font.Medium, Size = 15, NoWrap = true });

            TitleBarButton(WindowButton.Minimize);
            TitleBarButton(WindowButton.Maximize);
            TitleBarButton(WindowButton.Close);
        }
        this.Separator();
    }

    /// <summary>Opens a box whose laid-out bounds move the window and support the system caption gestures.</summary>
    /// <remarks>
    /// Use for static content or empty space, not for buttons, inputs, or scroll areas. Controls belong
    /// beside the region. Multiple regions are allowed. Without custom chrome or inside a disabled
    /// scope this is an ordinary box. Draw it again each frame for as long as it should be draggable.
    /// </remarks>
    public LayoutScope TitleBarDragRegion(Box o = default)
    {
        var chrome = _frame.TitleBar;
        if (chrome is null || IsDisabled) return this.Box(o);

        var native = o.ToNative();
        uint id;
        if (native.Id == 0)
        {
            // Only named boxes have queryable rectangles. Keep the auto IDs private to chrome
            // and distinct even when two regions share the caller's current ID scope.
            Native.PushIdInt(Handle, chrome.DragRegionCount);
            using var key = new IdScope(Handle);
            native.Id = Utf8.Get("#window-drag-region"u8);
            id = Native.BoxBegin(Handle, in native);
        }
        else
        {
            id = Native.BoxBegin(Handle, in native);
        }

        chrome.AddDragRegion(id);
        return new LayoutScope(Handle, &Native.BoxEnd, "Box", o.Id, nativeId: id);
    }

    /// <summary>Draws one window button at the current position, using a stock icon unless one is supplied.</summary>
    /// <remarks>
    /// Does not draw outside custom chrome. <see cref="WindowButton.Maximize"/> toggles between
    /// maximize and restore; <see cref="WindowButton.Restore"/> draws only when maximized.
    /// Neither draws on a fixed-size window. An enabled maximize or restore button is also the
    /// system's maximize target, so Windows can offer Snap Layouts.
    /// </remarks>
    public void TitleBarButton(WindowButton button, ButtonOpts? o = null, UiText icon = default)
    {
        var chrome = _frame.TitleBar;
        bool resize = button is WindowButton.Maximize or WindowButton.Restore;
        if (chrome is null || (resize && chrome.FixedSize)) return;

        var window = chrome.Window;
        bool restore = resize && window.IsMaximized;
        if (button == WindowButton.Restore && !restore) return;
        var (glyph, tooltip) = button switch
        {
            WindowButton.Minimize => (Icons.Minus, "Minimize window"),
            WindowButton.Restore or WindowButton.Maximize when restore => (Icons.Copy, "Restore window"),
            WindowButton.Maximize => (Icons.Square, "Maximize window"),
            WindowButton.Close => (Icons.X, "Close window"),
            _ => throw new ArgumentOutOfRangeException(nameof(button)),
        };
        var options = o ?? new ButtonOpts { Size = ControlSize.Sm };
        options.Tooltip ??= tooltip;
        // The glyph doubles as the native button label. Isolate it from app icon buttons
        // without overriding an automation ID the caller supplied.
        Native.PushId(Handle, Utf8.Get("#window-button"u8));
        using var group = new IdScope(Handle);
        Native.PushIdInt(Handle, (int)button);
        using var key = new IdScope(Handle);
        bool clicked = this.IconButton(icon.IsEmpty ? glyph : icon, options);
        if (resize && !options.Disabled && !options.Loading && !IsDisabled)
            chrome.MaximizeId = LastId;

        if (!clicked) return;
        switch (button)
        {
            case WindowButton.Minimize: window.Minimize(); break;
            case WindowButton.Maximize or WindowButton.Restore: window.ToggleMaximize(); break;
            case WindowButton.Close: window.RequestClose(); break;
        }
    }
}
