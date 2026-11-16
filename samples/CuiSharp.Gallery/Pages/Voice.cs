// Page 32 of 32: the voice components CUI added after 1.4 — a waveform you can seek, a recorded
// message in a bubble, a spoken conversation on one timeline — and the small busy indicators that
// arrived beside them: spinners, a status spinner and a typing indicator.
// Part of the CuiSharp.Gallery sample: Voice
using CuiSharp;

namespace CuiSharp.Gallery;

internal sealed partial class GalleryApp
{
    // One level every 20 ms, loudest in the middle: the shape of a spoken word. The owners array
    // says which of the two voices each level belongs to.
    private static readonly float[] Clip = BuildClip();
    private static readonly byte[] Owners = BuildOwners();

    private float _played = 0.35f;
    private bool _muted;
    private VoiceNoteState _note = new(Clip, Position: 1.5f, Duration: 4.2f, Speed: 1);

    private static float[] BuildClip()
    {
        var levels = new float[180];
        for (int i = 0; i < levels.Length; i++)
        {
            double t = (double)i / levels.Length;
            double envelope = Math.Sin(t * Math.PI);
            double waves = 0.55 + 0.45 * Math.Sin(i * 0.9);
            // Every fifth stretch is a pause: no audio at all, drawn as a gap.
            levels[i] = i is > 60 and < 76 ? CuiVoice.Silent : (float)(envelope * waves);
        }
        return levels;
    }

    private static byte[] BuildOwners()
    {
        var owners = new byte[180];
        for (int i = 0; i < owners.Length; i++)
            owners[i] = i < 96 ? (byte)VoiceOwner.You : (byte)VoiceOwner.Reply;
        return owners;
    }

    private void PageVoice(Ui ui)
    {
        Section(ui, "Voice waveform", "A voice drawn as fine dots; the newest levels scroll in at " +
            "the right. With a progress pointer it becomes a clip you can seek.");
        using (ui.Card(new Card()))
        using (ui.Column(new Box { Gap = 12 }))
        {
            ui.VoiceWave("live", Clip, new VoiceWaveOpts { Owner = VoiceOwner.You });
            ui.VoiceWave("clip", Clip, ref _played, new VoiceWaveOpts
            {
                Owners = Owners,
                Clip = true,
                Pitch = 4,
            });
            ui.Secondary($"{_played:P0} played; click or use the arrow keys to seek");
        }

        Section(ui, "Voice note", "A recorded message in a bubble, its waveform as the seek bar, the " +
            "time played and a speed chip. It plays nothing itself: you play the audio.");
        using (ui.Card(new Card()))
        {
            PlayerEvents events = ui.VoiceNote("note", ref _note, new VoiceNoteOpts { Mine = true });
            ui.Secondary($"{_note.Position:0.0} of {_note.Duration:0.0} s at {_note.Speed}x, playing {_note.Playing}, events {(int)events}");
        }

        Section(ui, "Voice session", "A spoken conversation with an assistant on one timeline: the " +
            "waveform, a state label, the live transcript and Mute and End.");
        using (ui.Card(new Card()))
        {
            VoiceSessionEvents result = ui.VoiceSession("session", VoiceState.Speaking, Clip, ref _muted, new VoiceSessionOpts
            {
                Owners = Owners,
                Transcript = "Let me read the file and summarise the changes",
                Upcoming = "before you send it.",
                Speaker = VoiceOwner.Reply,
                You = "You",
                Assistant = "Assistant",
            });
            ui.Secondary($"muted {_muted}, events {(int)result}");
        }

        Section(ui, "Typing indicator", "Three bouncing dots with a name, for a reply that is being " +
            "written; they hold still with reduced motion.");
        using (ui.Card(new Card()))
            ui.TypingIndicator("Assistant");

        Section(ui, "Spinners", "A busy ring in the styles a button, a chip or a search field needs.");
        using (ui.Card(new Card()))
        using (ui.Row(new Box { Gap = 24, Align = Align.End }))
        {
            ui.Spinner(20);
            ui.Spinner(14, new SpinnerOpts { Style = SpinnerStyle.Comet });
            ui.Spinner(56, new SpinnerOpts { Style = SpinnerStyle.Radar });
            ui.Spinner(32, new SpinnerOpts { Style = SpinnerStyle.Upload, Color = ColorFor(Tone.Accent) });
        }

        Section(ui, "Status spinner", "A spinner that turns into a check or a warning when the work " +
            "it waited for is done or failed.");
        using (ui.Card(new Card()))
        using (ui.Row(new Box { Gap = 24, Align = Align.End }))
        {
            ui.StatusSpinner("idle", Status.Idle);
            ui.StatusSpinner("busy", Status.Busy);
            ui.StatusSpinner("done", Status.Success);
            ui.StatusSpinner("failed", Status.Error);
        }
    }
}
