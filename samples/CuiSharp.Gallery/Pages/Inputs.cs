// Page 3 of 9: text fields, tags, codes, pickers.
// Part of the CuiSharp.Gallery sample: Inputs
using CuiSharp;

namespace CuiSharp.Gallery;

internal sealed partial class GalleryApp
{
    private void PageInputs(Ui ui)
    {
        Section(ui, "Text fields", "Labels above, hints and errors below.");
        using (ui.Card(new Card { Gap = 16 }))
        {
            using (ui.Box(new Box { Dir = Dir.Grid, Columns = 2, MinColumnWidth = 260, Gap = 16 }))
            {
                ui.Input("name", ref _name, new InputOpts { Label = "Full name", Placeholder = "Maya Okafor" });
                ui.Input("email", ref _email, new InputOpts
                {
                    Label = "Email",
                    Icon = Icons.AtSign,
                    Hint = "We never share your email.",
                });
                ui.Input("password", ref _password, new InputOpts
                {
                    Label = "Password",
                    Password = true,
                    Placeholder = "At least 8 characters",
                    Error = _password.Length is > 0 and < 8 ? "Use 8 or more characters." : null,
                });
                ui.Search("search", ref _search, new InputOpts { Label = "Search", Placeholder = "Search projects" });
            }
            ui.TextArea("bio", ref _bio, new InputOpts
            {
                Label = "About",
                Height = 96,
                Hint = "Markdown is supported.",
            });
        }

        Section(ui, "Tags, codes and password strength", null);
        using (ui.Card(new Card { Gap = 16 }))
        {
            ui.TagInput("tags", ref _tags, new TagOpts { Label = "Topics", Placeholder = "Add a topic" });
            using (ui.Column(new Box { Gap = 8 }))
            {
                ui.Label("Verification code");
                if (ui.OtpInput("otp", ref _otp, 6))
                {
                    ui.Toast("Code entered", new ToastOpts { Tone = Tone.Success });
                }
            }
            using (ui.Column(new Box { Gap = 8 }))
            {
                ui.Input("new-pw", ref _password, new InputOpts
                {
                    Label = "New password",
                    Password = true,
                    Placeholder = "Try a long passphrase",
                });
                ui.PasswordStrength(_password);
            }
        }

        Section(ui, "Pickers", null);
        using (ui.Card(new Card { Gap = 16 }))
        {
            using (ui.Box(new Box { Dir = Dir.Grid, Columns = 2, MinColumnWidth = 260, Gap = 16 }))
            {
                ui.Select("country", CountryNames, ref _country, new SelectOpts { Label = "Country" });
                ui.Select("framework", LanguageNames, ref _framework, new SelectOpts
                {
                    Label = "Language",
                    Searchable = true,
                    Placeholder = "Pick a language",
                });
                ui.DatePicker("due", ref _date, new DateOpts { Label = "Due date" });
                ui.Number("qty", ref _quantity, 0, 99, new NumberOpts { Label = "Seats", Width = Size.Fill });
            }
            using (ui.Column(new Box { Gap = 6 }))
            {
                ui.Label("Brand color");
                ui.ColorPicker("brand", ref _brand);
            }
        }
    }
}
