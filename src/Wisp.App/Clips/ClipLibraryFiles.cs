using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Wisp.App.Clips;

// Copies address only a held directory and a single generated filename. Existing
// media is opened without write/delete sharing while its bytes are compared.
internal static class ClipLibraryFiles
{
    private const uint Read = 0x81, ReadWriteDelete = 0x10083;

    internal static SafeFileHandle OpenDirectory(string path, bool create)
    {
        if (!ClipsSettings.TryNormalizeDirectory(path, out var normalized))
            throw new ArgumentException("Choose a folder using a full path.", nameof(path));
        var root = Path.GetPathRoot(normalized)!;
        var nativeRoot = root.StartsWith(@"\\", StringComparison.Ordinal) ? @"\??\UNC\" + root[2..] : @"\??\" + root;
        var current = Open(null, nativeRoot, 0xA1, 3, 1, true);
        try
        {
            foreach (var component in normalized[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                var next = Open(current, component, 0xA1, 3, create ? 3u : 1u, true);
                current.Dispose(); current = next;
            }
            return current;
        }
        catch { current.Dispose(); throw; }
    }

    internal static FileStream OpenRead(SafeFileHandle directory, string name)
    {
        var handle = Open(directory, name, Read, 1, 1, false);
        try { return new FileStream(handle, FileAccess.Read, 65536, isAsync: false); }
        catch { handle.Dispose(); throw; }
    }

    internal static bool IsMissing(IOException error) => (error.HResult & 0xffff) is 2 or 3;

    // Deletes a library video only if it still has its recorded size. Fails with a
    // sharing violation while another program holds it without delete sharing.
    internal static bool DeleteMedia(SafeFileHandle directory, string name, long expectedBytes)
    {
        SafeFileHandle handle;
        try { handle = Open(directory, name, 0x10080, 5, 1, false); }
        catch (IOException error) when (IsMissing(error)) { return false; }
        using (handle)
        {
            if (!GetFileInformationByHandle(handle, out var info)) throw Error((uint)Marshal.GetLastWin32Error());
            if (((long)info.SizeHigh << 32 | info.SizeLow) != expectedBytes)
                throw new InvalidDataException("The clip file size does not match its saved metadata. The file has been kept.");
            Delete(handle);
            return true;
        }
    }

    internal static bool IsInUse(Exception error) => error is IOException && (error.HResult & 0xffff) is 32 or 33;

    internal static async Task<bool> CopyOrVerifyAsync(FileStream source, string directory, string name,
        bool reuseIdentical, CancellationToken token, string? existingAlternativeName = null)
    {
        token.ThrowIfCancellationRequested();
        if (source.Length is <= 0 or > ClipLibrary.MaximumMediaBytes) throw new InvalidDataException("The clip file size is invalid.");
        using var parent = OpenDirectory(directory, create: true);
        if (existingAlternativeName is not null && await ExistingMatchesAsync(source, parent, existingAlternativeName, reuseIdentical, token).ConfigureAwait(false)) return false;
        if (await ExistingMatchesAsync(source, parent, name, reuseIdentical, token).ConfigureAwait(false)) return false;
        using var temporary = Open(parent, $".wisp-clip-copy-{Guid.NewGuid():N}.tmp", ReadWriteDelete, 1, 2, false);
        var renamed = false;
        try
        {
            source.Position = 0;
            await using (var output = DuplicateStream(temporary))
            {
                await source.CopyToAsync(output, 65536, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
                if (output.Length != source.Length) throw new IOException("The clip copy was incomplete. The original has been kept.");
            }
            token.ThrowIfCancellationRequested();
            try { RenameNew(temporary, parent, name); renamed = true; return true; }
            catch (IOException error) when ((error.HResult & 0xffff) is 80 or 183)
            {
                if (await ExistingMatchesAsync(source, parent, name, reuseIdentical, token).ConfigureAwait(false)) return false;
                throw;
            }
        }
        finally
        {
            if (!renamed)
            {
                try { Delete(temporary); }
                catch (IOException) { /* Preserve an owned temporary if its checked deletion fails. */ }
            }
        }
    }

    private static async Task<bool> ExistingMatchesAsync(FileStream source, SafeFileHandle parent, string name,
        bool reuseIdentical, CancellationToken token)
    {
        FileStream existing;
        try { existing = OpenRead(parent, name); }
        catch (IOException error) when (IsMissing(error)) { return false; }
        await using (existing)
        {
            if (!reuseIdentical || existing.Length != source.Length) throw Collision();
            source.Position = 0;
            var originalHash = await SHA256.HashDataAsync(source, token).ConfigureAwait(false);
            var existingHash = await SHA256.HashDataAsync(existing, token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(originalHash, existingHash)) throw Collision();
            return true;
        }
    }

    private static IOException Collision() => new("A different file already uses this clip filename. Nothing was replaced. Choose another export folder.");
    private static SafeFileHandle Open(SafeFileHandle? parent, string name, uint access, uint sharing, uint disposition, bool directory)
    {
        if (name.Length is 0 or > 30000 || parent is not null &&
            (!ClipsSettings.IsSafePathComponent(name) || name.IndexOfAny(['\\', '/', ':']) >= 0))
            throw new IOException("The clip filename is invalid.");
        var text = Marshal.StringToHGlobalUni(name); var pointer = IntPtr.Zero; var held = false;
        try
        {
            parent?.DangerousAddRef(ref held);
            var unicode = new UnicodeString { Buffer = text, Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)(name.Length * 2)) };
            pointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>()); Marshal.StructureToPtr(unicode, pointer, false);
            var attributes = new ObjectAttributes { Length = Marshal.SizeOf<ObjectAttributes>(), Root = parent?.DangerousGetHandle() ?? IntPtr.Zero, Name = pointer, Attributes = 0x1040 };
            var status = NtCreateFile(out var handle, access | 0x100000, ref attributes, out _, IntPtr.Zero, directory ? 0x10u : 0x80u,
                sharing, disposition, 0x20u | (directory ? 1u : 0x40u), IntPtr.Zero, 0);
            if (status < 0) { handle.Dispose(); throw Error(RtlNtStatusToDosError(status)); }
            try
            {
                if (!GetFileInformationByHandle(handle, out var info)) throw Error((uint)Marshal.GetLastWin32Error());
                if ((info.Attributes & 0x400) != 0 || ((info.Attributes & 0x10) != 0) != directory)
                    throw new IOException("This linked or cloud-managed folder is unsupported. Choose a local folder without links; existing clips are kept.");
                return handle;
            }
            catch { handle.Dispose(); throw; }
        }
        finally { if (held) parent!.DangerousRelease(); if (pointer != IntPtr.Zero) Marshal.FreeHGlobal(pointer); Marshal.FreeHGlobal(text); }
    }

    private static FileStream DuplicateStream(SafeFileHandle handle)
    {
        if (!DuplicateHandle(GetCurrentProcess(), handle, GetCurrentProcess(), out var copy, 0, false, 2)) throw Error((uint)Marshal.GetLastWin32Error());
        try { return new FileStream(copy, FileAccess.ReadWrite, 65536, isAsync: false); }
        catch { copy.Dispose(); throw; }
    }

    private static void RenameNew(SafeFileHandle file, SafeFileHandle parent, string name)
    {
        var bytes = Encoding.Unicode.GetBytes(name);
        var offset = Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.First)).ToInt32();
        var size = checked(Marshal.SizeOf<RenameInformation>() + bytes.Length);
        var pointer = Marshal.AllocHGlobal(size); var held = false;
        try
        {
            parent.DangerousAddRef(ref held);
            for (var index = 0; index < size; index++) Marshal.WriteByte(pointer, index, 0);
            Marshal.WriteIntPtr(pointer, Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.Root)).ToInt32(), parent.DangerousGetHandle());
            Marshal.WriteInt32(pointer, Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.Bytes)).ToInt32(), bytes.Length);
            Marshal.Copy(bytes, 0, IntPtr.Add(pointer, offset), bytes.Length);
            var status = NtSetInformationFile(file, out _, pointer, (uint)size, 10);
            if (status < 0) throw Error(RtlNtStatusToDosError(status));
        }
        finally { if (held) parent.DangerousRelease(); Marshal.FreeHGlobal(pointer); }
    }

    private static void Delete(SafeFileHandle file)
    {
        var pointer = Marshal.AllocHGlobal(1);
        try { Marshal.WriteByte(pointer, 1); if (!SetFileInformationByHandle(file, 4, pointer, 1)) throw Error((uint)Marshal.GetLastWin32Error()); }
        finally { Marshal.FreeHGlobal(pointer); }
    }
    private static IOException Error(uint code) => code switch
    {
        2 => new FileNotFoundException("The indexed clip file could not be found. Existing files are kept."),
        3 => new DirectoryNotFoundException("The clip folder could not be found. Existing files are kept."),
        _ => new IOException("The clip folder could not be accessed. Check folder access and free space; existing files are kept.", unchecked((int)(0x80070000 | code)))
    };
    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { internal ushort Length, MaximumLength; internal IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] private struct ObjectAttributes { internal int Length; internal IntPtr Root, Name; internal uint Attributes; internal IntPtr SecurityDescriptor, SecurityQualityOfService; }
    [StructLayout(LayoutKind.Sequential)] private struct IoStatus { internal IntPtr Status; internal UIntPtr Information; }
    [StructLayout(LayoutKind.Sequential)] private struct Information { internal uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
    [StructLayout(LayoutKind.Sequential)] private struct RenameInformation { internal uint Flags; internal IntPtr Root; internal uint Bytes; internal ushort First; }
    [DllImport("ntdll.dll", ExactSpelling = true)] private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes, out IoStatus status, IntPtr allocationSize, uint fileAttributes, uint sharing, uint disposition, uint options, IntPtr eaBuffer, uint eaLength);
    [DllImport("ntdll.dll", ExactSpelling = true)] private static extern uint RtlNtStatusToDosError(int status);
    [DllImport("ntdll.dll", ExactSpelling = true)] private static extern int NtSetInformationFile(SafeFileHandle handle, out IoStatus status, IntPtr information, uint bytes, int informationClass);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Information information);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, IntPtr information, uint bytes);
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool DuplicateHandle(IntPtr sourceProcess, SafeFileHandle source, IntPtr targetProcess, out SafeFileHandle target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
}
