using System.IO;
using System.Text;

namespace Wisp.App.Supplementary;

internal sealed record SupplementaryInstallationIdentity(Guid Id, bool NewlyCreated);

// Independent of settings/profile exports and never derived from a device, account, address or game identity.
internal static class SupplementaryIdentityStore
{
    internal static SupplementaryInstallationIdentity? LoadOrCreate(string directory)
    {
        try
        {
            directory = Path.GetFullPath(directory);
            Directory.CreateDirectory(directory);
            for (var d = new DirectoryInfo(directory); d is not null; d = d.Parent)
                if ((d.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            var path = Path.Combine(directory, "reporting-identity.txt");
            if (File.Exists(path)) return Read(path);
            var id = Guid.NewGuid();
            // CreateNew prevents two launchers from silently replacing an identity. A truncated identity fails closed.
            try
            {
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64, FileOptions.WriteThrough);
                output.Write(Encoding.ASCII.GetBytes(id.ToString("D")));
                output.Flush(flushToDisk: true);
                return new(id, true);
            }
            catch (IOException) when (File.Exists(path)) { return Read(path); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        { return null; }
    }
    private static SupplementaryInstallationIdentity? Read(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64, FileOptions.SequentialScan);
        if (input.Length != 36) return null;
        Span<byte> bytes = stackalloc byte[36]; input.ReadExactly(bytes);
        return Guid.TryParseExact(Encoding.ASCII.GetString(bytes), "D", out var id) && SupplementarySchema.Uuid4(id) ? new(id, false) : null;
    }
}

internal static class SupplementaryRuntime
{
    internal static SupplementaryConfiguration Configuration { get; } = new(new Uri("https://wispoverlay.com/"));
    internal static IReadOnlyDictionary<string, byte[]> PublicKeys { get; } = new Dictionary<string, byte[]>
    {
        ["wisp-content-2026-10"] = Convert.FromBase64String("MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEf7eFY8JgI5z5cWp6UUTLf3H9WweHTxx5mGNBHpoB96vE6I4n1EYHIAhpB6FBEgKfEz8P1tAwwyD7qOnGvD7Ncg==")
    };
    internal static bool IsConfigured => Configuration.Origin is not null && PublicKeys.Count > 0;
}
