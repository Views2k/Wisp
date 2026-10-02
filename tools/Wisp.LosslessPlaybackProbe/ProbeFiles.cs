using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Wisp.LosslessPlaybackProbe;

internal sealed class ProbeFiles : IDisposable
{
    internal const string ExpectedSha256 = "584257587584AC7A35485C58BA00EBE5595B6A4B53F0CF3F9FF2FC1D7BA47F16";
    private readonly List<SafeFileHandle> _parents = [];
    internal FileStream? SourceHandle { get; private set; }
    internal string Source { get; private set; } = "";
    internal string Output { get; private set; } = "";

    internal void Prepare(string source, string output)
    {
        Source = LocalPath(source);
        Output = LocalPath(output);
        var checkout = FindCheckout();
        var work = Path.Combine(checkout, "work") + Path.DirectorySeparatorChar;
        Need(Source.StartsWith(work, StringComparison.OrdinalIgnoreCase) &&
            Output.StartsWith(work, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(Source) == "fixture.mp4", "workspace-synthetic-fixture-required");
        HoldParents(Path.GetDirectoryName(Source)!);
        HoldParents(Path.GetDirectoryName(Output)!);
        Need(!Directory.Exists(Output) && !File.Exists(Output), "fresh-output-directory-required");
        SourceHandle = new FileStream(Source, FileMode.Open, FileAccess.Read, FileShare.Read);
        Need(SourceHandle.Length == 15097409, "generated-fixture-size-mismatch");
        Need((File.GetAttributes(Source) & FileAttributes.ReparsePoint) == 0, "reparse-source-refused");
        var finalPath = new StringBuilder(32768);
        var count = GetFinalPathNameByHandle(SourceHandle.SafeFileHandle, finalPath, (uint)finalPath.Capacity, 0);
        Need(count > 0 && count < finalPath.Capacity &&
            finalPath.ToString().Equals(@"\\?\" + Source, StringComparison.OrdinalIgnoreCase), "held-source-path-mismatch");
        Need(Convert.ToHexString(SHA256.HashData(SourceHandle)) == ExpectedSha256, "generated-fixture-hash-mismatch");
        Need(new DriveInfo(Path.GetPathRoot(Output)!).AvailableFreeSpace >= 128L * 1024 * 1024, "output-space-required");
        Need(CreateDirectory(Output, IntPtr.Zero), "fresh-output-directory-create-failed");
        HoldParents(Output);
    }

    internal static string FindCheckout()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Wisp.sln")) &&
                Directory.Exists(Path.Combine(directory.FullName, "src", "Wisp.App"))) return directory.FullName;
        throw new CheckFailure("checkout-required");
    }

    private static string LocalPath(string value)
    {
        var full = Path.GetFullPath(value);
        Need(Path.IsPathFullyQualified(value) && full.Length is > 3 and < 240 &&
            !full.StartsWith(@"\\", StringComparison.Ordinal) && full[1] == ':' &&
            !full[2..].Contains(':') && !full.EndsWith('.') && !full.EndsWith(' '), "absolute-local-path-required");
        Need(new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Fixed, "fixed-local-drive-required");
        return full;
    }

    private void HoldParents(string directory)
    {
        var chain = new Stack<string>();
        for (var part = new DirectoryInfo(directory); part is not null; part = part.Parent) chain.Push(part.FullName);
        foreach (var path in chain)
        {
            // Read-attribute handles without DELETE sharing pin each directory against replacement.
            var handle = CreateFile(path, 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); throw new CheckFailure("parent-directory-open-failed"); }
            _parents.Add(handle);
            Need(GetFileInformationByHandle(handle, out var info) &&
                (info.Attributes & (uint)FileAttributes.Directory) != 0 &&
                (info.Attributes & (uint)FileAttributes.ReparsePoint) == 0, "reparse-or-nondirectory-parent");
        }
    }

    public void Dispose()
    {
        SourceHandle?.Dispose();
        for (var i = _parents.Count - 1; i >= 0; i--) _parents[i].Dispose();
        _parents.Clear();
    }

    internal static void Need(bool condition, string code) { if (!condition) throw new CheckFailure(code); }
    [StructLayout(LayoutKind.Sequential)] private struct FileInfo
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfo info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateDirectoryW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateDirectory(string path, IntPtr security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
}

internal sealed class CheckFailure(string code) : Exception { internal string Code { get; } = code; }
