namespace Wisp.Core;

// A circuit race started from the grid begins its first timed lap at the start line, but FH6 does
// not report that crossing as a new lap: the packet lap stays 0 and no lap time is recorded. Its
// lap clock, which ran from the race start, restarts at the line while the race clock runs on.
// The live lap tracker and saved-run review both treat that one sample as the first lap's start.
internal static class GridStartLine
{
    // Lap clock readings at or below this are at the line, as for any other lap start.
    internal const float AtLine = .25f;

    // The same packet lap with no lap timed yet, the lap clock back at the line, and the race clock
    // still running. A rewind or restart takes the race clock back too, so neither matches.
    internal static bool IsFirstCrossing(LapTelemetry before, LapTelemetry after) =>
        after.LapNumber == before.LapNumber &&
        before.LastLapSeconds <= .01f && after.LastLapSeconds <= .01f &&
        before.CurrentLapSeconds > AtLine && after.CurrentLapSeconds <= AtLine &&
        after.RaceSeconds + .01f >= before.RaceSeconds && after.RaceSeconds - before.RaceSeconds <= 1;
}
