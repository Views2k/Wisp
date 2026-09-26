using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Wisp.Core;

namespace Wisp.App.Laps;

// Keeps reference laps while Wisp restarts during one run of the game, as the game keeps its own
// Current Best for that run.
internal sealed class LapReferenceStore(string path, Func<string?> gameRun)
{
    private sealed record Kept(string GameRun, LapReferenceSession Session);

    internal static LapReferenceStore ForCurrentUser() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wisp", "LapReferences.json"), CurrentGameRun);

    // One run of the game: its process and when it started.
    internal static string? CurrentGameRun()
    {
        Process[] processes = [];
        try
        {
            processes = Process.GetProcessesByName("ForzaHorizon6");
            return processes.Length == 1 ? $"{processes[0].Id}:{processes[0].StartTime.ToUniversalTime().Ticks}" : null;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    internal LapReferenceSession? Load()
    {
        try
        {
            if (gameRun() is not { } run || !File.Exists(path)) return null;
            var kept = JsonSerializer.Deserialize<Kept>(File.ReadAllText(path));
            return kept?.GameRun == run ? kept.Session : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    internal void Save(LapReferenceSession? session)
    {
        try
        {
            if (session is null || gameRun() is not { } run)
            {
                File.Delete(path);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Kept(run, session)));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }
}
