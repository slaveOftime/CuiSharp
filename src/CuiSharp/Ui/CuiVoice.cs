using CuiSharp.Interop;

namespace CuiSharp;

/// <summary>
/// Voice helpers without a frame: the resampling the waveform strip does, for keeping a short
/// waveform with a recorded message.
/// </summary>
public static class CuiVoice
{
    /// <summary>
    /// Resamples a clip to <paramref name="columns"/> levels the way the clip strip does: each
    /// column takes the loudest level of its stretch, and a stretch with no audio stays
    /// <see cref="Silent"/>.
    /// </summary>
    /// <param name="levels">The clip's loudness, 0..1, oldest first, and <see cref="Silent"/> for no audio.</param>
    /// <param name="columns">Where the resampled levels go; its length is how many are kept.</param>
    /// <returns>How many levels were written.</returns>
    public static unsafe int Resample(ReadOnlySpan<float> levels, Span<float> columns)
    {
        if (columns.IsEmpty) return 0;
        fixed (float* source = levels)
        fixed (float* target = columns)
            return Native.VoiceResample((nint)source, levels.Length, ref *target, columns.Length);
    }

    /// <summary>A level below zero is no audio at all: silence, or the assistant thinking.</summary>
    public const float Silent = -1.0f;
}
