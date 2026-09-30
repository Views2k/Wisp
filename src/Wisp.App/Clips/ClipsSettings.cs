using System.IO;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace Wisp.App.Clips;

// App-wide recorder preferences; these do not belong to a HUD or car profile.
public sealed class ClipsSettings
{
    public static IReadOnlyList<int> LengthChoices { get; } = Array.AsReadOnly(Enumerable.Range(1, 10).Select(value => value * 30).ToArray());
    public static IReadOnlyList<int> ResolutionChoices { get; } = Array.AsReadOnly(new[] { 360, 480, 720, 1080, 1440, 2160 });
    public static IReadOnlyList<int> FrameRateChoices { get; } = Array.AsReadOnly(new[] { 30, 60 });

    internal const string RecordingPathTooLongMessage = "Wisp's private clip storage path is too long for recording and playback.";

    // The player cannot open media paths of 260 UTF-16 characters or more.
    // Keep this recording policy separate from existing-library normalization.
    internal static bool CanRecordToDirectory(string? directory) =>
        TryNormalizeStorageDirectory(directory, out var normalized) &&
        Path.Combine(normalized, Guid.Empty.ToString("N") + ".mp4").Length < 260;

    public bool Enabled { get; set; }
    public bool ShowCaptureBorder { get; set; }
    public int LengthSeconds { get; set; } = 60;
    public int ResolutionHeight { get; set; } = 1080;
    public int FrameRate { get; set; } = 60;
    public int Quality { get; set; } = 75;
    public bool RemindersEnabled { get; set; } = true;
    public string StorageDirectory { get; set; } = "";
    public bool UsesPrivateLibrary { get; set; }
    public string LegacyLibraryDirectory { get; set; } = "";
    internal static string DefaultLibraryDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wisp", "Clips");
    public bool ToggleShortcutEnabled { get; set; }
    public OverlayHotkeyModifiers ToggleShortcutModifiers { get; set; } = OverlayHotkeyModifiers.Control | OverlayHotkeyModifiers.Alt;
    public Key ToggleShortcutKey { get; set; } = Key.F8;
    public bool SaveShortcutEnabled { get; set; }
    public OverlayHotkeyModifiers SaveShortcutModifiers { get; set; } = OverlayHotkeyModifiers.Control | OverlayHotkeyModifiers.Alt;
    public Key SaveShortcutKey { get; set; } = Key.F9;

    [JsonIgnore]
    public OverlayHotkeyChord ToggleShortcut => new(ToggleShortcutModifiers, ToggleShortcutKey);
    [JsonIgnore]
    public OverlayHotkeyChord SaveShortcut => new(SaveShortcutModifiers, SaveShortcutKey);

    public ClipsSettings Clone() => (ClipsSettings)MemberwiseClone();

    public void Normalize()
    {
        if (!LengthChoices.Contains(LengthSeconds)) LengthSeconds = 60;
        if (!ResolutionChoices.Contains(ResolutionHeight)) ResolutionHeight = 1080;
        if (!FrameRateChoices.Contains(FrameRate)) FrameRate = 60;
        Quality = Math.Clamp(Quality, 10, 100);
        if (!TryNormalizeStorageDirectory(StorageDirectory, out var directory))
        {
            StorageDirectory = "";
        }
        else StorageDirectory = directory;
        if (!UsesPrivateLibrary)
        {
            LegacyLibraryDirectory = StorageDirectory;
            UsesPrivateLibrary = true;
        }
        if (!TryNormalizeStorageDirectory(LegacyLibraryDirectory, out var legacy)) LegacyLibraryDirectory = "";
        else LegacyLibraryDirectory = legacy;
        if (!OverlayHotkeyChord.TryCreate(ToggleShortcutModifiers, ToggleShortcutKey, out _, out _))
        {
            ToggleShortcutEnabled = false;
            ToggleShortcutModifiers = OverlayHotkeyModifiers.Control | OverlayHotkeyModifiers.Alt;
            ToggleShortcutKey = Key.F8;
        }
        if (!OverlayHotkeyChord.TryCreate(SaveShortcutModifiers, SaveShortcutKey, out _, out _))
        {
            SaveShortcutEnabled = false;
            SaveShortcutModifiers = OverlayHotkeyModifiers.Control | OverlayHotkeyModifiers.Alt;
            SaveShortcutKey = Key.F9;
        }
        if (ToggleShortcutEnabled && SaveShortcutEnabled && ToggleShortcut == SaveShortcut)
            SaveShortcutEnabled = false;
    }

    // Syntax only. The library checks existing path components and actual access
    // when it opens storage; normalization does not create or inspect folders.
    public static bool TryNormalizeStorageDirectory(string? value, out string directory)
    {
        if (TryNormalizeDirectory(value, out directory) &&
            Path.GetPathRoot(directory) is { Length: 3 } root &&
            char.IsAsciiLetter(root[0]) && root[1] == ':') return true;
        directory = "";
        return false;
    }

    // Export destinations can be user-selected network paths; live recording
    // storage is stricter because it requires a local, seekable disk spool.
    internal static bool TryNormalizeDirectory(string? value, out string directory)
    {
        directory = "";
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            if (!Path.IsPathFullyQualified(value) || value.StartsWith(@"\\?\", StringComparison.Ordinal) ||
                value.StartsWith(@"\\.\", StringComparison.Ordinal) || value.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                return false;
            // Win32 normalization can convert device names or trim a trailing
            // dot before the normalized components are examined.
            var originalRoot = Path.GetPathRoot(value)!;
            var originalComponents = value[originalRoot.Length..].Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            if (originalComponents.Any(component => !IsSafePathComponent(component))) return false;
            var full = Path.GetFullPath(value);
            var root = Path.GetPathRoot(full)!;
            var components = full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            if (components.Any(component => !IsSafePathComponent(component))) return false;
            directory = Path.TrimEndingDirectorySeparator(full);
            return true;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    internal static bool IsSafePathComponent(string component)
    {
        if (component.Length == 0 || component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            component.EndsWith(' ') || component.EndsWith('.')) return false;
        var name = component.Split('.')[0].ToUpperInvariant();
        if (name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$") return false;
        return !(name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) ||
            name.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(name[3]));
    }
}
