using CuiSharp.Interop;

namespace CuiSharp;

/// <summary>
/// Cron schedules without a frame: when an expression runs next, using the same parser the
/// schedule picker shows its next runs with. Five fields (minute, hour, day, month, weekday),
/// with <c>*</c>, lists, ranges, steps and the month and weekday names.
/// </summary>
public static class CuiCron
{
    /// <summary>
    /// The first time after <paramref name="after"/> that <paramref name="cron"/> matches.
    /// </summary>
    /// <param name="cron">The five-field expression.</param>
    /// <param name="after">The moment to search from.</param>
    /// <param name="next">When it runs next.</param>
    /// <param name="utcOffsetMinutes">The time zone to read the expression in, in minutes east of UTC.</param>
    /// <returns>False when the expression is invalid or does not run in the next five years.</returns>
    public static bool TryNext(string cron, DateTimeOffset after, out DateTimeOffset next, int utcOffsetMinutes = 0)
    {
        long found = 0;
        if (Native.CronNext(Utf8.Get(cron), after.ToUnixTimeSeconds(), utcOffsetMinutes, ref found) == 0)
        {
            next = default;
            return false;
        }

        next = DateTimeOffset.FromUnixTimeSeconds(found);
        return true;
    }
}
