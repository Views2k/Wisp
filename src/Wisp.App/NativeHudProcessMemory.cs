using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Wisp.Core;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace Wisp.App;

public sealed class NativeHudProcessMemoryFactory : INativeHudProcessMemoryFactory
{
    private static readonly NativeHudFingerprintCache SharedFingerprints = new(new NativeHudFingerprintFileSystem());
    private readonly NativeCompatibilityCatalog _catalog;
    private readonly NativeHudFingerprintCache _fingerprints;
    private string _compatibilityStatus = "Built-in compatibility; awaiting FH6";

    public NativeHudProcessMemoryFactory() : this(NativeCompatibilityRuntime.Catalog)
    {
    }

    public NativeHudProcessMemoryFactory(NativeCompatibilityCatalog catalog)
        : this(catalog, SharedFingerprints)
    {
    }

    internal NativeHudProcessMemoryFactory(NativeCompatibilityCatalog catalog, NativeHudFingerprintCache fingerprints)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _fingerprints = fingerprints ?? throw new ArgumentNullException(nameof(fingerprints));
    }

    public long CompatibilityGeneration => _catalog.Generation;
    public string CompatibilityStatus => Volatile.Read(ref _compatibilityStatus);

    public bool TryOpen(out INativeHudProcessMemory? memory, out NativeAssistProviderStatus status)
    {
        var opened = TryOpenConcrete(out var concrete, out status);
        memory = concrete;
        return opened;
    }

    internal bool TryOpenConcrete(out NativeHudProcessMemory? memory, out NativeAssistProviderStatus status)
    {
        memory = null;
        status = NativeAssistProviderStatus.GameNotRunning;
        Process[] processes = [];
        NativeHudProcessMemory? candidate = null;
        string? candidateStatus = null;
        try
        {
            processes = Process.GetProcessesByName("ForzaHorizon6");
            var sawUnsupported = false;
            foreach (var process in processes)
            {
                if (!TryOpenProcess(process, out var current, out var currentStatus))
                {
                    sawUnsupported |= currentStatus == NativeAssistProviderStatus.UnsupportedBuild;
                    status = currentStatus;
                    continue;
                }

                if (candidate is not null)
                {
                    current!.Dispose();
                    status = NativeAssistProviderStatus.PlayerNotUnique;
                    SetStatus("Multiple matching FH6 processes; attachment refused");
                    return false;
                }

                candidate = current;
                candidateStatus = CompatibilityStatus;
            }

            if (candidate is null)
            {
                if (sawUnsupported)
                {
                    status = NativeAssistProviderStatus.UnsupportedBuild;
                }
                else if (processes.Length == 0)
                {
                    SetStatus("Built-in compatibility; awaiting FH6");
                }

                return false;
            }

            memory = candidate;
            candidate = null;
            status = NativeAssistProviderStatus.Ready;
            SetStatus(candidateStatus ?? "Verified FH6 compatibility selected");
            return true;
        }
        catch (Exception exception) when (NativeHudFingerprintCache.IsExpectedFailure(exception))
        {
            status = NativeAssistProviderStatus.AccessDenied;
            SetStatus("FH6 process identity could not be read");
            return false;
        }
        finally
        {
            candidate?.Dispose();
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    internal bool TrySelectCompatibility(
        NativeHudProcessIdentity identity,
        out NativeHudCompatibilityPack? pack,
        out NativeHudExecutableFingerprint fingerprint,
        out NativeAssistProviderStatus status)
    {
        pack = null;
        fingerprint = default;
        status = NativeAssistProviderStatus.UnsupportedBuild;
        if (!NativeHudProcessMemory.IsValidModuleRange(identity.ModuleBase, identity.ImageSize))
        {
            SetStatus("FH6 module image bounds are invalid");
            return false;
        }

        if (!_fingerprints.TryGet(identity, out fingerprint, out status))
        {
            SetStatus("FH6 executable identity could not be verified or changed during validation");
            return false;
        }

        pack = _catalog.Find(fingerprint.Metadata.Version, fingerprint.Metadata.Length, fingerprint.Sha256);
        if (pack is null)
        {
            status = NativeAssistProviderStatus.UnsupportedBuild;
            var reason = _catalog.GetUnavailableReason(
                fingerprint.Metadata.Version, fingerprint.Metadata.Length, fingerprint.Sha256);
            SetStatus($"FH6 {fingerprint.Metadata.Version}; SHA-256 {fingerprint.Sha256[..12]}; " +
                (reason ?? "no trusted exact pack"));
            return false;
        }

        if (identity.ImageSize != pack.ImageSize)
        {
            pack = null;
            status = NativeAssistProviderStatus.UnsupportedBuild;
            SetStatus("FH6 module image size does not match the exact compatibility pack");
            return false;
        }

        status = NativeAssistProviderStatus.Ready;
        SetStatus($"FH6 {pack.GameVersion}; SHA-256 {pack.ExecutableSha256[..12]}; pack {pack.Id} r{pack.Revision}");
        return true;
    }

    private bool TryOpenProcess(Process process, out NativeHudProcessMemory? memory, out NativeAssistProviderStatus status)
    {
        memory = null;
        status = NativeAssistProviderStatus.AccessDenied;
        SafeProcessHandle? handle = null;
        try
        {
            var identity = CaptureIdentity(process);
            if (!TrySelectCompatibility(identity, out var pack, out var fingerprint, out status))
            {
                return false;
            }

            handle = NativeHudProcessMemory.OpenReadOnly(process.Id);
            if (handle.IsInvalid)
            {
                status = NativeAssistProviderStatus.AccessDenied;
                SetStatus("The verified FH6 process could not be opened read-only");
                return false;
            }

            process.Refresh();
            if (CaptureIdentity(process) != identity ||
                !NativeHudProcessMemory.HandleMatchesIdentity(handle, identity) ||
                !_fingerprints.IsCurrent(identity, fingerprint) ||
                !ReferenceEquals(pack, _catalog.Find(fingerprint.Metadata.Version, fingerprint.Metadata.Length, fingerprint.Sha256)))
            {
                status = NativeAssistProviderStatus.ReadFailure;
                SetStatus("FH6 process or compatibility identity changed during attachment");
                return false;
            }

            memory = new NativeHudProcessMemory(handle, identity.ModuleBase, pack!);
            handle = null;
            status = NativeAssistProviderStatus.Ready;
            return true;
        }
        catch (Exception exception) when (NativeHudFingerprintCache.IsExpectedFailure(exception))
        {
            status = exception is UnauthorizedAccessException or Win32Exception
                ? NativeAssistProviderStatus.AccessDenied
                : NativeAssistProviderStatus.ReadFailure;
            SetStatus("FH6 process or executable identity could not be verified");
            return false;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private static NativeHudProcessIdentity CaptureIdentity(Process process)
    {
        var module = process.MainModule ?? throw new InvalidOperationException("The main module is unavailable.");
        var path = NativeHudFingerprintCache.NormalizePath(module.FileName);
        if (module.ModuleMemorySize <= 0 ||
            !Path.GetFileName(path).Equals("ForzaHorizon6.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The process image identity is invalid.");
        }

        return new NativeHudProcessIdentity(
            process.Id, process.StartTime.ToUniversalTime().Ticks, path,
            (ulong)module.BaseAddress, (uint)module.ModuleMemorySize);
    }

    private void SetStatus(string status) => Volatile.Write(ref _compatibilityStatus, status);
}

public sealed class NativeHudProcessMemory : INativeHudProcessMemory
{
    internal const uint RequiredProcessAccess = 0x0010 | 0x1000;
    internal const ulong UserAddressLimit = 0x0000800000000000;
    private static readonly Lazy<NativeHudProcessMemoryFactory> DefaultFactory = new(() => new NativeHudProcessMemoryFactory());
    private readonly SafeProcessHandle _handle;

    internal NativeHudProcessMemory(SafeProcessHandle handle, ulong moduleBase, NativeHudCompatibilityPack compatibilityPack)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(compatibilityPack);
        if (handle.IsInvalid || handle.IsClosed || !IsValidModuleRange(moduleBase, compatibilityPack.ImageSize))
        {
            throw new ArgumentException("The read-only process identity is invalid.");
        }

        _handle = handle;
        ModuleBase = moduleBase;
        CompatibilityPack = compatibilityPack;
    }

    public ulong ModuleBase { get; }
    public NativeHudCompatibilityPack CompatibilityPack { get; }

    public static bool TryOpen(out NativeHudProcessMemory? memory, out NativeAssistProviderStatus status) =>
        DefaultFactory.Value.TryOpenConcrete(out memory, out status);

    public bool TryReadByte(ulong address, out byte value)
    {
        value = default;
        return CanRead(address, 1) && ReadProcessMemoryByte(_handle, (nint)address, out value, 1, out var read) && read == 1;
    }

    public bool TryReadUInt32(ulong address, out uint value)
    {
        value = default;
        return CanRead(address, 4) && ReadProcessMemoryUInt32(_handle, (nint)address, out value, 4, out var read) && read == 4;
    }

    public bool TryReadUInt64(ulong address, out ulong value)
    {
        value = default;
        return CanRead(address, 8) && ReadProcessMemoryUInt64(_handle, (nint)address, out value, 8, out var read) && read == 8;
    }

    public bool TryReadSingle(ulong address, out float value)
    {
        value = default;
        return CanRead(address, 4) && ReadProcessMemorySingle(_handle, (nint)address, out value, 4, out var read) && read == 4;
    }

    public bool TryReadBytes(ulong address, Span<byte> destination)
    {
        if (destination.IsEmpty || !CanReadBytes(address, (ulong)destination.Length))
        {
            destination.Clear();
            return false;
        }

        ref var first = ref MemoryMarshal.GetReference(destination);
        if (ReadProcessMemoryBytes(
                _handle,
                (nint)address,
                ref first,
                (nuint)destination.Length,
                out var read) &&
            read == (nuint)destination.Length)
        {
            return true;
        }

        destination.Clear();
        return false;
    }

    public void Dispose() => _handle.Dispose();

    internal static bool IsValidModuleRange(ulong moduleBase, uint imageSize) =>
        imageSize is >= 4096 and <= 1024 * 1024 * 1024 &&
        moduleBase % 8 == 0 && IsValidReadSpan(moduleBase, imageSize);

    internal static bool IsValidReadSpan(ulong address, ulong length) =>
        length > 0 && address >= 0x10000 && address < UserAddressLimit && length <= UserAddressLimit - address;

    internal static SafeProcessHandle OpenReadOnly(int processId) => OpenProcess(RequiredProcessAccess, false, processId);

    internal static bool HandleMatchesIdentity(SafeProcessHandle handle, NativeHudProcessIdentity identity)
    {
        if (!GetProcessTimes(handle, out var creation, out _, out _, out _) ||
            NativeHudFingerprintFileSystem.FileTimeTicks(creation) != identity.StartTimeUtcTicks)
        {
            return false;
        }

        var path = new StringBuilder(32768);
        uint length = (uint)path.Capacity;
        return QueryFullProcessImageName(handle, 0, path, ref length) &&
               NativeHudFingerprintCache.NormalizePath(path.ToString()) == identity.ExecutablePath;
    }

    private bool CanRead(ulong address, ulong length) =>
        !_handle.IsInvalid && !_handle.IsClosed && address % length == 0 && IsValidReadSpan(address, length);

    private bool CanReadBytes(ulong address, ulong length) =>
        !_handle.IsInvalid && !_handle.IsClosed && IsValidReadSpan(address, length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out FILETIME creation, out FILETIME exit, out FILETIME kernel, out FILETIME user);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint size);

    [DllImport("kernel32.dll", EntryPoint = "ReadProcessMemory", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemoryByte(SafeProcessHandle process, nint address, out byte buffer, nuint size, out nuint read);

    [DllImport("kernel32.dll", EntryPoint = "ReadProcessMemory", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemoryUInt32(SafeProcessHandle process, nint address, out uint buffer, nuint size, out nuint read);

    [DllImport("kernel32.dll", EntryPoint = "ReadProcessMemory", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemoryUInt64(SafeProcessHandle process, nint address, out ulong buffer, nuint size, out nuint read);

    [DllImport("kernel32.dll", EntryPoint = "ReadProcessMemory", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemorySingle(SafeProcessHandle process, nint address, out float buffer, nuint size, out nuint read);

    [DllImport("kernel32.dll", EntryPoint = "ReadProcessMemory", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemoryBytes(SafeProcessHandle process, nint address, ref byte buffer, nuint size, out nuint read);
}

internal readonly record struct NativeHudProcessIdentity(int ProcessId, long StartTimeUtcTicks, string ExecutablePath, ulong ModuleBase, uint ImageSize);
internal readonly record struct NativeHudFileMetadata(long Length, long CreationTimeUtcTicks, long LastWriteTimeUtcTicks, uint VolumeSerialNumber, ulong FileIndex, string Version);
internal readonly record struct NativeHudExecutableFingerprint(NativeHudFileMetadata Metadata, string Sha256);

internal interface INativeHudFingerprintFileSystem
{
    NativeHudFileMetadata ReadMetadata(string path);
    string ComputeSha256(string path);
}

internal sealed class NativeHudFingerprintCache
{
    internal const long MaximumExecutableLength = 2L * 1024 * 1024 * 1024;
    internal const long FileTimestampToleranceTicks = 2 * TimeSpan.TicksPerSecond;
    private readonly object _gate = new();
    private readonly INativeHudFingerprintFileSystem _files;
    private readonly int _capacity;
    private readonly Dictionary<ProcessGeneration, ObservedExecutable> _entries = [];
    private readonly Queue<ProcessGeneration> _order = new();
    private long _evictedThroughStartTimeUtcTicks;

    internal NativeHudFingerprintCache(INativeHudFingerprintFileSystem files, int capacity = 32)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        if (capacity is < 1 or > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    internal int EntryCount
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    internal int CachedFingerprintCount
    {
        get
        {
            lock (_gate)
            {
                return _entries.Values.Count(entry => entry.Fingerprint.HasValue);
            }
        }
    }

    internal bool TryGet(
        NativeHudProcessIdentity identity,
        out NativeHudExecutableFingerprint fingerprint,
        out NativeAssistProviderStatus status)
    {
        fingerprint = default;
        status = NativeAssistProviderStatus.UnsupportedBuild;
        lock (_gate)
        {
            try
            {
                if (identity.ProcessId <= 0 || identity.StartTimeUtcTicks <= 0 ||
                    identity.StartTimeUtcTicks > DateTime.MaxValue.Ticks ||
                    !NativeHudProcessMemory.IsValidModuleRange(identity.ModuleBase, identity.ImageSize))
                {
                    return false;
                }

                identity = identity with { ExecutablePath = NormalizePath(identity.ExecutablePath) };
                var key = new ProcessGeneration(identity.ProcessId, identity.StartTimeUtcTicks);
                if (_entries.TryGetValue(key, out var observed))
                {
                    if (observed.Blocked || observed.Identity != identity)
                    {
                        observed.Block();
                        return false;
                    }
                }
                else if (identity.StartTimeUtcTicks <= _evictedThroughStartTimeUtcTicks)
                {
                    // Eviction cannot make a previously seen, still-running image eligible again.
                    return false;
                }

                var before = _files.ReadMetadata(identity.ExecutablePath);
                var validMetadata = IsValidMetadata(before) && FilePredatesProcess(before, identity.StartTimeUtcTicks);
                if (observed is null)
                {
                    observed = new ObservedExecutable(identity, validMetadata ? before : default);
                    Remember(key, observed);
                }

                if (!validMetadata || observed.Metadata != before)
                {
                    observed.Block();
                    return false;
                }

                if (observed.Fingerprint is { } cached)
                {
                    fingerprint = cached;
                    status = NativeAssistProviderStatus.Ready;
                    return true;
                }

                var hash = _files.ComputeSha256(identity.ExecutablePath);
                var after = _files.ReadMetadata(identity.ExecutablePath);
                if (before != after)
                {
                    observed.Block();
                    status = NativeAssistProviderStatus.ReadFailure;
                    return false;
                }

                if (!IsValidSha256(hash))
                {
                    status = NativeAssistProviderStatus.ReadFailure;
                    return false;
                }

                fingerprint = new NativeHudExecutableFingerprint(before, hash.ToUpperInvariant());
                observed.Fingerprint = fingerprint;
                status = NativeAssistProviderStatus.Ready;
                return true;
            }
            catch (Exception exception) when (IsExpectedFailure(exception))
            {
                status = exception is UnauthorizedAccessException or Win32Exception
                    ? NativeAssistProviderStatus.AccessDenied
                    : NativeAssistProviderStatus.ReadFailure;
                return false;
            }
        }
    }

    internal bool IsCurrent(NativeHudProcessIdentity identity, NativeHudExecutableFingerprint fingerprint)
    {
        lock (_gate)
        {
            try
            {
                identity = identity with { ExecutablePath = NormalizePath(identity.ExecutablePath) };
                var key = new ProcessGeneration(identity.ProcessId, identity.StartTimeUtcTicks);
                if (!_entries.TryGetValue(key, out var observed) || observed.Blocked ||
                    observed.Fingerprint != fingerprint)
                {
                    return false;
                }

                if (observed.Identity != identity || _files.ReadMetadata(identity.ExecutablePath) != observed.Metadata)
                {
                    observed.Block();
                    return false;
                }

                return true;
            }
            catch (Exception exception) when (IsExpectedFailure(exception))
            {
                return false;
            }
        }
    }

    private void Remember(ProcessGeneration key, ObservedExecutable observed)
    {
        while (_entries.Count >= _capacity)
        {
            var evicted = _order.Dequeue();
            _entries.Remove(evicted);
            _evictedThroughStartTimeUtcTicks = Math.Max(_evictedThroughStartTimeUtcTicks, evicted.StartTimeUtcTicks);
        }

        _entries.Add(key, observed);
        _order.Enqueue(key);
    }

    internal static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Length > 32767 || path.IndexOf('\0') >= 0 || path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            throw new ArgumentException("The executable path is invalid.", nameof(path));
        }

        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            path = @"\\" + path[8..];
        }
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            path = path[4..];
        }

        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The executable path must be absolute.", nameof(path));
        }

        return Path.GetFullPath(path).ToUpperInvariant();
    }

    internal static bool IsExpectedFailure(Exception exception) =>
        exception is Win32Exception or InvalidOperationException or UnauthorizedAccessException or
            IOException or CryptographicException or ArgumentException or NotSupportedException;

    private static bool IsValidMetadata(NativeHudFileMetadata metadata) =>
        metadata.Length is >= 4096 and <= MaximumExecutableLength &&
        metadata.CreationTimeUtcTicks >= 0 && metadata.CreationTimeUtcTicks <= DateTime.MaxValue.Ticks &&
        metadata.LastWriteTimeUtcTicks >= 0 && metadata.LastWriteTimeUtcTicks <= DateTime.MaxValue.Ticks &&
        IsSafeVersion(metadata.Version);

    private static bool FilePredatesProcess(NativeHudFileMetadata metadata, long processStartTimeUtcTicks) =>
        metadata.CreationTimeUtcTicks - processStartTimeUtcTicks <= FileTimestampToleranceTicks &&
        metadata.LastWriteTimeUtcTicks - processStartTimeUtcTicks <= FileTimestampToleranceTicks;

    private static bool IsSafeVersion(string? version)
    {
        if (version is null || version.Length is < 7 or > 23)
        {
            return false;
        }

        var parts = version.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        foreach (var part in parts)
        {
            if (part.Length is < 1 or > 5)
            {
                return false;
            }

            var number = 0;
            foreach (var digit in part)
            {
                if (digit is < '0' or > '9')
                {
                    return false;
                }

                number = number * 10 + digit - '0';
            }

            if (number > ushort.MaxValue)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidSha256(string? hash) =>
        hash is { Length: 64 } && hash.All(character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f');

    private readonly record struct ProcessGeneration(int ProcessId, long StartTimeUtcTicks);

    private sealed class ObservedExecutable(NativeHudProcessIdentity identity, NativeHudFileMetadata metadata)
    {
        internal NativeHudProcessIdentity Identity { get; } = identity;
        internal NativeHudFileMetadata Metadata { get; } = metadata;
        internal NativeHudExecutableFingerprint? Fingerprint { get; set; }
        internal bool Blocked { get; private set; }

        internal void Block()
        {
            Blocked = true;
            Fingerprint = null;
        }
    }
}

internal sealed class NativeHudFingerprintFileSystem : INativeHudFingerprintFileSystem
{
    public NativeHudFileMetadata ReadMetadata(string path)
    {
        using var file = OpenStableFile(path);
        if (!GetFileInformationByHandle(file.SafeFileHandle, out var information))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The executable file identity is unavailable.");
        }

        var length = ((ulong)information.FileSizeHigh << 32) | information.FileSizeLow;
        var version = length is >= 4096 and <= NativeHudFingerprintCache.MaximumExecutableLength
            ? FileVersionInfo.GetVersionInfo(path).FileVersion?.Trim() ?? string.Empty
            : string.Empty;
        return new NativeHudFileMetadata(
            (long)Math.Min(length, (ulong)long.MaxValue),
            FileTimeTicks(information.CreationTime),
            FileTimeTicks(information.LastWriteTime),
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow,
            version);
    }

    public string ComputeSha256(string path)
    {
        using var file = OpenStableFile(path);
        if (file.Length is < 4096 or > NativeHudFingerprintCache.MaximumExecutableLength)
        {
            throw new IOException("The executable length is outside the supported bounds.");
        }

        return Convert.ToHexString(SHA256.HashData(file));
    }

    internal static long FileTimeTicks(FILETIME time)
    {
        var fileTime = ((long)(uint)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;
        return DateTime.FromFileTimeUtc(fileTime).Ticks;
    }

    private static FileStream OpenStableFile(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal uint FileAttributes;
        internal FILETIME CreationTime;
        internal FILETIME LastAccessTime;
        internal FILETIME LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }
}
