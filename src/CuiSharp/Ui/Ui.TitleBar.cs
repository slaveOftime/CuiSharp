namespace CuiSharp;

public unsafe ref partial struct Ui
{
    /// <summary>Draws a themed title bar with optional controls before and after the title.</summary>
    /// <remarks>
    /// Draw once per frame at the top of the window. With <see cref="CuiAppOptions.CustomTitleBar"/>
    /// on Windows, the title and empty space move the window, and the window buttons are automatic.
    /// Elsewhere, including headless surfaces, the bar draws without window buttons. Use a
    /// <see cref="Disabled(bool)"/> scope if an overlay should also block dragging the caption.
    /// </remarks>
    public void TitleBar(UiText title, DrawFn? leading = null, DrawFn? trailing = null)
    {
        using var ids = Id("#window-title-bar");
        using var bar = this.Column(new Box { Width = Size.Fill });
        using (this.Row(new Box { Width = Size.Fill, Height = 48, Align = Align.Center, PadX = 12, Gap = 10, Bg = Color.SurfaceMuted }))
        {
            leading?.Invoke(this);

            const string caption = "caption";
            var chrome = _frame.TitleBar;
            using (this.Row(new Box { Id = caption, Width = Size.Fill, Height = Size.Fill, Align = Align.Center, Clip = true }))
            {
                if (chrome is not null && !IsDisabled) chrome.CaptionId = MakeId(caption);
                this.Text(title, new TextOpts { Font = Font.Medium, Size = 15, NoWrap = true });
            }

            trailing?.Invoke(this);
            if (chrome is not null)
            {
                var window = chrome.Window;
                var button = new ButtonOpts { Variant = ButtonVariant.Ghost, Size = ControlSize.Sm };
                button.Tooltip = "Minimize window";
                if (this.IconButton(Icons.Minus, button)) window.Minimize();

                if (!chrome.FixedSize)
                {
                    bool maximized = window.IsMaximized;
                    button.Tooltip = maximized ? "Restore window" : "Maximize window";
                    bool toggle = this.IconButton(maximized ? Icons.Copy : Icons.Square, button);
                    if (!IsDisabled) chrome.MaximizeId = LastId;
                    if (toggle) window.ToggleMaximize();
                }

                button.Tooltip = "Close window";
                if (this.IconButton(Icons.X, button)) window.RequestClose();
            }
        }
        this.Separator();
    }
}
