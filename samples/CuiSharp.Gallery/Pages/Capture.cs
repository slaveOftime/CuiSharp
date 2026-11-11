// Page 17 of 28: dropzones, an avatar upload and a signature pad.
// Part of the CuiSharp.Gallery sample: Capture
using CuiSharp;

namespace CuiSharp.Gallery;

internal sealed partial class GalleryApp
{
    private string _dropped = "";
    private ulong _avatarTexture;

    private void PageCapture(Ui ui)
    {
        Section(ui, "Dropzone", "Drop files from your desktop, or click to browse.");
        ui.Dropzone("files", new DropzoneOpts
        {
            Description = "PNG, JPG or PDF, up to 25 MB",
            Accept = "png;jpg;jpeg;pdf",
        });
        if (ui.DroppedFileCount > 0) _dropped = ui.DroppedFile(0) ?? "";
        using (ui.Row(new Box { Gap = 12, Wrap = true }))
        {
            using (ui.Box(new Box { Width = Size.Fill }))
            {
                ui.Dropzone("compact", new DropzoneOpts
                {
                    Compact = true,
                    Title = "Attach a file",
                    Description = "Any type",
                });
            }
            using (ui.Box(new Box { Width = Size.Fill }))
            {
                ui.Dropzone("images", new DropzoneOpts
                {
                    Compact = true,
                    Title = "Add images",
                    Description = "PNG or JPG",
                    Icon = Icons.Image,
                    Accept = "png;jpg;jpeg",
                });
            }
        }
        if (_dropped.Length > 0) ui.Muted(_dropped);

        Section(ui, "Avatar upload", "Drop an image on the avatar.");
        using (ui.Row(new Box { Gap = 20, Align = Align.Center }))
        {
            // The widget loads the dropped image into the handle and frees the old one.
            ui.AvatarUpload("avatar", "Maya Okafor", ref _avatarTexture, 88);
            using (ui.Column(new Box { Gap = 4 }))
            {
                ui.Label("Profile photo");
                ui.Secondary("At least 256 by 256 pixels.");
            }
        }

        Section(ui, "Signature", "Draw with the mouse or a pen. Faster strokes are thinner, like ink.");
        using (ui.Box(new Box { Width = Size.Px(520) }))
        {
            ui.SignaturePad("signature", new SignatureOpts { Height = 180 });
        }
    }
}
