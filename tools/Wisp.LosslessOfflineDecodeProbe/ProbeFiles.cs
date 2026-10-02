using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Wisp.LosslessOfflineDecodeProbe;

internal sealed class ProbeFiles : IDisposable
{
    private readonly List<IDisposable> _held = [];
    internal string Source { get; private set; } = "";
    internal string Output { get; private set; } = "";
    internal string Native { get; private set; } = "";
    internal string ManagedHash { get; private set; } = "";
    internal string ManifestHash { get; private set; } = "";

    internal void Prepare(string source, string sha, long bytes, string payload, string output)
    {
        Source = Local(source); Output = Local(output); payload = Local(payload);
        var checkout = FindCheckout();
        Need(Path.GetFileName(Source) == "29e58a5054f547e7b7fc7535c16252da.mp4", "explicit-reported-clip-required");
        Need(Output.StartsWith(Path.Combine(checkout, "work") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "checkout-output-required");
        Need(payload.StartsWith(Path.Combine(checkout, "artifacts", "private-candidates") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "retained-payload-required");
        Need(bytes is > 0 and <= 4L * 1024 * 1024 * 1024 && sha.Length == 64 && sha.All(Uri.IsHexDigit), "explicit-source-identity-required");
        HoldParents(Path.GetDirectoryName(Source)!);
        HoldParents(payload);
        HoldParents(Path.GetDirectoryName(Output)!);
        var input = HoldFile(Source);
        Need(input.Length == bytes && Hash(input).Equals(sha, StringComparison.OrdinalIgnoreCase), "source-identity-mismatch");
        Native = Path.Combine(payload, "libvlc", "win-x64");
        var manifest = Path.Combine(checkout, "LICENSES", "libvlc-3.0.24-source-manifest.json");
        using var manifestStream = File.OpenRead(manifest);
        ManifestHash = Hash(manifestStream);
        manifestStream.Position = 0;
        using var document = JsonDocument.Parse(manifestStream);
        var expected = document.RootElement.GetProperty("nativeFiles").EnumerateArray().ToArray();
        Need(expected.Length == 24, "minimal-decoder-manifest-required");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in expected)
        {
            var relative = file.GetProperty("path").GetString()!;
            Need(!Path.IsPathRooted(relative) && !relative.Contains(':') && !relative.Split('/', '\\').Any(part => part is "" or "." or ".."), "manifest-path-invalid");
            Need(paths.Add(relative.Replace('/', Path.DirectorySeparatorChar)), "manifest-duplicate-path");
            var full = Path.Combine(Native, relative.Replace('/', Path.DirectorySeparatorChar));
            HoldParents(Path.GetDirectoryName(full)!);
            var held = HoldFile(full);
            Need(held.Length == file.GetProperty("bytes").GetInt64() && Hash(held).Equals(file.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase), "native-decoder-mismatch");
        }
        Need(RegularFiles(Native).Select(path => Path.GetRelativePath(Native, path)).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(paths), "extra-decoder-files-refused");
        ManagedHash = Hash(HoldFile(Path.Combine(payload, "LibVLCSharp.dll")));
        using var actualManaged = File.OpenRead(typeof(LibVLCSharp.Shared.LibVLC).Assembly.Location);
        Need(Hash(actualManaged) == ManagedHash, "managed-decoder-mismatch");
        Need(new DriveInfo(Path.GetPathRoot(Output)!).AvailableFreeSpace >= 512L * 1024 * 1024, "output-space-required");
        Need(CreateDirectory(Output, IntPtr.Zero), "fresh-output-directory-required");
        HoldParents(Output);
    }

    private FileStream HoldFile(string path)
    {
        Need((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "reparse-file-refused");
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _held.Add(file);
        var final = new StringBuilder(32768);
        var count = GetFinalPathNameByHandle(file.SafeFileHandle, final, (uint)final.Capacity, 0);
        Need(count > 0 && count < final.Capacity && final.ToString().Equals(@"\\?\" + path, StringComparison.OrdinalIgnoreCase), "held-file-path-mismatch");
        return file;
    }

    private void HoldParents(string directory)
    {
        var chain = new Stack<string>();
        for (var part = new DirectoryInfo(directory); part is not null; part = part.Parent) chain.Push(part.FullName);
        foreach (var path in chain)
        {
            var handle = CreateFile(path, 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            _held.Add(handle);
            Need(!handle.IsInvalid && GetFileInformationByHandle(handle, out var info) &&
                (info.Attributes & (uint)FileAttributes.Directory) != 0 &&
                (info.Attributes & (uint)FileAttributes.ReparsePoint) == 0, "regular-held-parent-required");
        }
    }

    private static IEnumerable<string> RegularFiles(string directory)
    {
        Need((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0, "reparse-decoder-directory");
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(path);
            Need((attributes & FileAttributes.ReparsePoint) == 0, "reparse-decoder-entry");
            if ((attributes & FileAttributes.Directory) != 0)
                foreach (var file in RegularFiles(path)) yield return file;
            else yield return path;
        }
    }

    private static string Local(string path)
    {
        var full = Path.GetFullPath(path);
        Need(Path.IsPathFullyQualified(path) && full.Length is > 3 and < 240 && !full.StartsWith(@"\\", StringComparison.Ordinal) &&
            full[1] == ':' && !full[2..].Contains(':') && !full.EndsWith('.') && !full.EndsWith(' '), "absolute-local-path-required");
        Need(new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Fixed, "fixed-local-drive-required");
        return full;
    }

    private static string FindCheckout()
    {
        for (var part = new DirectoryInfo(AppContext.BaseDirectory); part is not null; part = part.Parent)
            if (File.Exists(Path.Combine(part.FullName, "Wisp.sln"))) return part.FullName;
        throw new ProbeFailure("checkout-required");
    }

    internal static string Hash(Stream stream) { stream.Position = 0; return Convert.ToHexString(SHA256.HashData(stream)); }
    internal static void Need(bool condition, string reason) { if (!condition) throw new ProbeFailure(reason); }
    public void Dispose() { for (var i = _held.Count - 1; i >= 0; --i) _held[i].Dispose(); }

    [StructLayout(LayoutKind.Sequential)] private struct FileInfo
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfo info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateDirectoryW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateDirectory(string path, IntPtr security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
}

internal sealed class ProbeFailure(string reason) : Exception { internal string Reason { get; } = reason; }
