using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Wisp.App;

// Package provenance is separate from the Store pack's bounded image guards, not a file hash.
internal readonly record struct NativeStorePackageIdentity(string PackageFullName, string ExecutablePath)
{
    private const string PackagePrefix = "Microsoft.ForteBaseGame_";
    private const string PackageSuffix = "_x64__8wekyb3d8bbwe";
    internal const int StoreOrigin = 3;
    private const int InsufficientBuffer = 122;
    private const uint MaximumPackageNameCharacters = 512;
    private const uint MaximumPathCharacters = 32768;

    internal static bool TryRead(SafeProcessHandle handle, string executablePath, out NativeStorePackageIdentity identity) =>
        TryRead(handle, executablePath, WindowsPackageApi.Instance, out identity);

    internal static bool TryRead(
        SafeProcessHandle handle, string executablePath, IPackageApi api, out NativeStorePackageIdentity identity)
    {
        identity = default;
        if (handle is null || handle.IsClosed || handle.IsInvalid)
        {
            return false;
        }

        try
        {
            // No-package and unavailable API results are ordinary nonmatches; Steam still uses its file fingerprint.
            if (!TryReadString((ref uint length, char[]? buffer) => api.GetFullName(handle, ref length, buffer),
                    MaximumPackageNameCharacters, out var name) ||
                !IsGamePackageFamily(name) ||
                api.GetOrigin(name, out var origin) != 0 || origin != StoreOrigin ||
                !TryReadString((ref uint length, char[]? buffer) => api.GetPath(name, 0, ref length, buffer),
                    MaximumPathCharacters, out var originalPath) ||
                !TryReadString((ref uint length, char[]? buffer) => api.GetPath(name, 2, ref length, buffer),
                    MaximumPathCharacters, out var effectivePath))
            {
                return false;
            }

            return TryValidate(name, origin, originalPath, effectivePath, executablePath, out identity);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or
            ObjectDisposedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool TryValidate(
        string? packageFullName, int origin, string? originalPackagePath, string? effectivePackagePath,
        string? executablePath, out NativeStorePackageIdentity identity)
    {
        identity = default;
        if (!IsGamePackageFamily(packageFullName) ||
            origin != StoreOrigin || string.IsNullOrWhiteSpace(originalPackagePath) ||
            string.IsNullOrWhiteSpace(effectivePackagePath) || string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        try
        {
            var original = NativeHudFingerprintCache.NormalizePath(originalPackagePath);
            var effective = NativeHudFingerprintCache.NormalizePath(effectivePackagePath);
            var executable = NativeHudFingerprintCache.NormalizePath(executablePath);
            // A versioned pack does not authorize an unverified installation projection.
            if (original != effective ||
                executable != NativeHudFingerprintCache.NormalizePath(Path.Combine(effective, "ForzaHorizon6.exe")))
            {
                return false;
            }

            identity = new NativeStorePackageIdentity(packageFullName!, executable);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    // Package-family provenance is only the first gate; the factory still requires an exact trusted build map.
    private static bool IsGamePackageFamily(string? name)
    {
        if (name is null || name.Length < PackagePrefix.Length + PackageSuffix.Length + 7 ||
            name.Length > PackagePrefix.Length + PackageSuffix.Length + 23 ||
            !name.StartsWith(PackagePrefix, StringComparison.Ordinal) || !name.EndsWith(PackageSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = name[PackagePrefix.Length..^PackageSuffix.Length].Split('.');
        return parts.Length == 4 && parts.All(part => part.Length is >= 1 and <= 5 &&
            part.All(char.IsAsciiDigit) && ushort.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _));
    }

    internal static bool MatchesExecutableFileAlias(string expectedPath, string observedPath) =>
        MatchesExecutableFileAlias(expectedPath, observedPath, WindowsFileAliasApi.Instance);

    internal static bool MatchesExecutableFileAlias(string expectedPath, string observedPath, IFileAliasApi api)
    {
        try
        {
            var expected = NativeHudFingerprintCache.NormalizePath(expectedPath);
            var observed = NativeHudFingerprintCache.NormalizePath(observedPath);
            if (!IsLocalGameExecutable(expected) || !IsLocalGameExecutable(observed))
            {
                return false;
            }

            // Keep both attribute-only handles open so file identifiers cannot be recycled between reads.
            using var first = api.OpenAttributes(expected);
            if (first.IsInvalid || first.IsClosed)
            {
                return false;
            }

            using var second = api.OpenAttributes(observed);
            return !second.IsInvalid && !second.IsClosed &&
                api.TryReadIdentity(first, out var firstIdentity) &&
                api.TryReadIdentity(second, out var secondIdentity) &&
                MatchesFileIdentity(firstIdentity, secondIdentity);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or
            DllNotFoundException or EntryPointNotFoundException or ObjectDisposedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsLocalGameExecutable(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\' &&
        string.Equals(Path.GetFileName(path), "ForzaHorizon6.exe", StringComparison.OrdinalIgnoreCase);

    internal static bool MatchesFileIdentity(FileAliasIdentity first, FileAliasIdentity second) =>
        first.IsValid && second.IsValid && first.Volume == second.Volume &&
        first.FileIdLow == second.FileIdLow && first.FileIdHigh == second.FileIdHigh &&
        first.Length == second.Length && first.CreationTime == second.CreationTime && first.LastWriteTime == second.LastWriteTime;

    internal readonly record struct FileAliasIdentity(
        ulong Volume, ulong FileIdLow, ulong FileIdHigh, ulong Length, ulong CreationTime, ulong LastWriteTime, uint Attributes)
    {
        internal bool IsValid => Volume != 0 && (FileIdLow != 0 || FileIdHigh != 0) &&
            Length is >= 4096 and <= 2UL * 1024 * 1024 * 1024 && CreationTime != 0 && LastWriteTime != 0 &&
            (Attributes & (0x10U | 0x40U)) == 0;
    }

    internal interface IFileAliasApi
    {
        SafeFileHandle OpenAttributes(string path);
        bool TryReadIdentity(SafeFileHandle handle, out FileAliasIdentity identity);
    }

    private sealed class WindowsFileAliasApi : IFileAliasApi
    {
        internal static readonly WindowsFileAliasApi Instance = new();

        public SafeFileHandle OpenAttributes(string path) =>
            CreateFile(path, 0x80, 0x1 | 0x2 | 0x4, IntPtr.Zero, 3, 0x80, IntPtr.Zero);

        public bool TryReadIdentity(SafeFileHandle handle, out FileAliasIdentity identity)
        {
            identity = default;
            if (!GetFileInformationByHandle(handle, out var information) ||
                !GetFileInformationByHandleEx(handle, 18, out var fileId, 24))
            {
                return false;
            }

            identity = new FileAliasIdentity(fileId.Volume, fileId.Low, fileId.High,
                ((ulong)information.SizeHigh << 32) | information.SizeLow,
                ((ulong)information.CreationHigh << 32) | information.CreationLow,
                ((ulong)information.WriteHigh << 32) | information.WriteLow, information.Attributes);
            return identity.IsValid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedFileId
        {
            public ulong Volume, Low, High;
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern SafeFileHandle CreateFile(
            string path, uint access, uint share, IntPtr security, uint disposition, uint attributes, IntPtr template);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(
            SafeFileHandle handle, int informationClass, out ExtendedFileId information, uint size);
    }

    private delegate int StringQuery(ref uint length, char[]? buffer);

    private static bool TryReadString(StringQuery query, uint maximumCharacters, out string value)
    {
        value = string.Empty;
        uint length = 0;
        if (query(ref length, null) != InsufficientBuffer || length < 2 || length > maximumCharacters)
        {
            return false;
        }

        var capacity = length;
        var buffer = new char[capacity];
        if (query(ref length, buffer) != 0 || length < 2 || length > capacity ||
            buffer[length - 1] != '\0' || Array.IndexOf(buffer, '\0', 0, (int)length - 1) >= 0)
        {
            return false;
        }

        value = new string(buffer, 0, (int)length - 1);
        return true;
    }

    internal interface IPackageApi
    {
        int GetFullName(SafeProcessHandle handle, ref uint length, char[]? buffer);
        int GetOrigin(string fullName, out int origin);
        int GetPath(string fullName, int pathType, ref uint length, char[]? buffer);
    }

    private sealed class WindowsPackageApi : IPackageApi
    {
        internal static readonly WindowsPackageApi Instance = new();
        public int GetFullName(SafeProcessHandle handle, ref uint length, char[]? buffer) =>
            GetPackageFullName(handle, ref length, buffer);
        public int GetOrigin(string fullName, out int origin) => GetStagedPackageOrigin(fullName, out origin);
        public int GetPath(string fullName, int pathType, ref uint length, char[]? buffer) =>
            GetPackagePathByFullName2(fullName, pathType, ref length, buffer);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int GetPackageFullName(SafeProcessHandle process, ref uint length, [Out] char[]? fullName);

        [DllImport("kernelbase.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int GetStagedPackageOrigin(string fullName, out int origin);

        [DllImport("kernelbase.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int GetPackagePathByFullName2(string fullName, int pathType, ref uint length, [Out] char[]? path);
    }
}
