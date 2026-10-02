using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Wisp.MpvOfflineDecodeProbe;

internal sealed class ProbeFiles : IDisposable
{
    internal const string SourceHash = "48A33FB214FE9F4D4802FAF88E71E61280C2C8CC680F2DF4A0135A1416806ABD";
    internal const string DllHash = "1E23E98611C99137565B684D7D59B5BF30DC3B81F3C8E606DF6D503F0BD6B8A3";
    internal const string HeaderHash = "1ACF99EE77C8C2A6F1D1993BD81BBC8A91D27FB5924E80171670E6139A4BD353";
    internal const string OracleHash = "CE20DFAC0739354A4992D5977120E4C5E241C649BF1F9DA3A754A85E3F4DF3A2";
    private readonly List<IDisposable> _held = [];
    internal string Source { get; private set; } = "";
    internal string Dll { get; private set; } = "";
    internal string Output { get; private set; } = "";
    internal Dictionary<string, List<int>> Oracle { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal void Prepare(string source, string dependency, string output)
    {
        Source = Local(source); dependency = Local(dependency); Output = Local(output);
        var checkout = FindCheckout();
        var work = Path.Combine(checkout, "work") + Path.DirectorySeparatorChar;
        Need(dependency.StartsWith(work, StringComparison.OrdinalIgnoreCase) && Output.StartsWith(work, StringComparison.OrdinalIgnoreCase), "owned-work-directory-required");
        Need(Path.GetFileName(Source) == "29e58a5054f547e7b7fc7535c16252da.mp4", "reported-clip-required");
        HoldParents(Path.GetDirectoryName(Source)!); HoldParents(dependency); HoldParents(Path.GetDirectoryName(Output)!);
        Verify(Source, 965569830, SourceHash);
        Dll = Path.Combine(dependency, "libmpv-2.dll");
        Verify(Dll, 126284288, DllHash);
        HoldParents(Path.Combine(dependency, "include", "mpv"));
        Verify(Path.Combine(dependency, "include", "mpv", "client.h"), 86136, HeaderHash);
        // The static artifact must not acquire unverified app-local native dependencies.
        Need(Directory.EnumerateFiles(dependency, "*.dll", SearchOption.TopDirectoryOnly).Count() == 1, "single-pinned-runtime-required");
        var oracle = Path.Combine(checkout, "work", "clips-short-save-20261001", "ffmpeg-rgb.sha256");
        HoldParents(Path.GetDirectoryName(oracle)!);
        var stream = HoldFile(oracle);
        Need(stream.Length <= 64 * 1024 && Hash(stream) == OracleHash, "oracle-identity-mismatch");
        stream.Position = 0;
        var frameIndices = new HashSet<int>();
        using (var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true))
        {
            while (reader.ReadLine() is { } line)
            {
                if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(',').Select(value => value.Trim()).ToArray();
                Need(parts.Length == 6 && parts[0] == "0" && parts[4] == "24883200" &&
                    int.TryParse(parts[2], out var index) && index is >= 0 and < 148 && parts[5].Length == 64 && parts[5].All(Uri.IsHexDigit), "oracle-format-invalid");
                var frameIndex = int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
                Need(frameIndices.Add(frameIndex), "oracle-duplicate-index");
                if (!Oracle.TryGetValue(parts[5], out var matching)) { matching = []; Oracle.Add(parts[5], matching); }
                matching.Add(frameIndex);
            }
        }
        Need(frameIndices.Count == 148, "full-oracle-required");
        Need(new DriveInfo(Path.GetPathRoot(Output)!).AvailableFreeSpace >= 256L * 1024 * 1024, "output-space-required");
        Need(CreateDirectory(Output, IntPtr.Zero), "fresh-output-required");
        HoldParents(Output);
    }

    private void Verify(string path, long bytes, string hash)
    {
        var file = HoldFile(path);
        Need(file.Length == bytes && Hash(file) == hash, "pinned-file-mismatch");
    }
    private FileStream HoldFile(string path)
    {
        Need((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "reparse-file-refused");
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); _held.Add(file);
        var final = new StringBuilder(32768);
        var count = GetFinalPathNameByHandle(file.SafeFileHandle, final, (uint)final.Capacity, 0);
        Need(count > 0 && count < final.Capacity && final.ToString().Equals(@"\\?\" + path, StringComparison.OrdinalIgnoreCase), "held-path-mismatch");
        return file;
    }
    private void HoldParents(string directory)
    {
        var chain = new Stack<string>();
        for (var part = new DirectoryInfo(directory); part is not null; part = part.Parent) chain.Push(part.FullName);
        foreach (var path in chain)
        {
            var handle = CreateFile(path, 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero); _held.Add(handle);
            Need(!handle.IsInvalid && GetFileInformationByHandle(handle, out var info) &&
                (info.Attributes & (uint)FileAttributes.Directory) != 0 &&
                (info.Attributes & (uint)FileAttributes.ReparsePoint) == 0, "held-regular-parent-required");
        }
    }
    private static string Local(string path)
    {
        var full = Path.GetFullPath(path);
        Need(Path.IsPathFullyQualified(path) && full.Length is > 3 and < 240 && !full.StartsWith(@"\\", StringComparison.Ordinal) &&
            full[1] == ':' && !full[2..].Contains(':') && !full.EndsWith('.') && !full.EndsWith(' '), "absolute-local-path-required");
        Need(new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Fixed, "fixed-drive-required"); return full;
    }
    private static string FindCheckout()
    {
        for (var part = new DirectoryInfo(AppContext.BaseDirectory); part is not null; part = part.Parent)
            if (File.Exists(Path.Combine(part.FullName, "Wisp.sln"))) return part.FullName;
        throw new ProbeFailure("checkout-required");
    }
    private static string Hash(Stream stream) { stream.Position = 0; return Convert.ToHexString(SHA256.HashData(stream)); }
    internal static void Need(bool condition, string code) { if (!condition) throw new ProbeFailure(code); }
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

internal sealed class ProbeFailure(string code) : Exception { internal string Code { get; } = code; }
