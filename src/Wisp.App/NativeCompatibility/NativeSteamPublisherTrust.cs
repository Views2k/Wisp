using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Wisp.App;

// Only the unknown-build path uses this gate. The caller still verifies the
// file/process identity before and after it, and validates the reader layout.
internal static class NativeSteamPublisherTrust
{
    internal static readonly Guid AuthenticodeAction = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
    internal const uint NoUi = 2;
    internal const uint VerifyState = 1;
    internal const uint CloseState = 2;
    internal const uint OfflineProviderFlags = 0x1000 | 0x80 | 0x2000;

    internal static bool TryVerify(string executablePath) => TryVerify(executablePath, WindowsTrustApi.Instance);

    internal static bool TryVerify(string executablePath, ITrustApi api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var pathPointer = IntPtr.Zero;
        var filePointer = IntPtr.Zero;
        try
        {
            if (string.IsNullOrWhiteSpace(executablePath) || executablePath.Length < 3 ||
                !char.IsAsciiLetter(executablePath[0]) || executablePath[1] != ':' ||
                executablePath[2] is not ('\\' or '/') || !Path.IsPathFullyQualified(executablePath)) return false;
            // Keep this handle open through state cleanup. Denying write/delete
            // sharing binds the signature and signer to the same file contents.
            using var file = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is < 4096 or > NativeHudFingerprintCache.MaximumExecutableLength) return false;
            pathPointer = Marshal.StringToCoTaskMemUni(executablePath);
            var fileInfo = new TrustFileInfo
            {
                Size = checked((uint)Marshal.SizeOf<TrustFileInfo>()),
                FilePath = pathPointer,
                FileHandle = file.SafeFileHandle.DangerousGetHandle()
            };
            filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, filePointer, false);
            var data = new TrustData
            {
                Size = checked((uint)Marshal.SizeOf<TrustData>()),
                UiChoice = NoUi,
                RevocationChecks = 1, // WTD_REVOKE_WHOLECHAIN, using cached evidence only.
                UnionChoice = 1, // WTD_CHOICE_FILE
                FileInfo = filePointer,
                StateAction = VerifyState,
                ProviderFlags = OfflineProviderFlags
            };
            var action = AuthenticodeAction;
            try
            {
                // WinVerifyTrust returns a LONG status, not a Win32 last-error value.
                if (api.Verify(ref action, ref data) != 0 || data.StateData == IntPtr.Zero) return false;
                return IsPlaygroundGamesPublisher(api.ReadPrimarySignerSubject(data.StateData));
            }
            finally
            {
                if (data.StateData != IntPtr.Zero)
                {
                    data.StateAction = CloseState;
                    if (api.Verify(ref action, ref data) != 0)
                        throw new InvalidOperationException("The signature verification state could not be closed.");
                }
                GC.KeepAlive(file);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            CryptographicException or ArgumentException or InvalidOperationException or NotSupportedException or
            Win32Exception or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
        finally
        {
            if (filePointer != IntPtr.Zero) Marshal.FreeHGlobal(filePointer);
            if (pathPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pathPointer);
        }
    }

    internal static bool IsPlaygroundGamesPublisher(X500DistinguishedName? subject)
    {
        if (subject is null) return false;
        string? commonName = null;
        string? organization = null;
        var commonNameCount = 0;
        var organizationCount = 0;
        foreach (var part in subject.EnumerateRelativeDistinguishedNames(reversed: false))
        {
            if (part.HasMultipleElements) return false;
            switch (part.GetSingleElementType().Value)
            {
                case "2.5.4.3":
                    if (++commonNameCount != 1) return false;
                    commonName = part.GetSingleElementValue();
                    break;
                case "2.5.4.10":
                    if (++organizationCount != 1) return false;
                    organization = part.GetSingleElementValue();
                    break;
            }
        }
        return string.Equals(commonName, "Playground Games", StringComparison.Ordinal) &&
               string.Equals(organization, "Playground Games", StringComparison.Ordinal);
    }

    internal interface ITrustApi
    {
        int Verify(ref Guid action, ref TrustData data);
        X500DistinguishedName? ReadPrimarySignerSubject(IntPtr state);
    }

    // Field order/widths follow Windows SDK wintrust.h, including the Windows 8
    // signature-settings pointer. Native BOOL fields below are four-byte ints.
    [StructLayout(LayoutKind.Sequential)]
    internal struct TrustFileInfo
    {
        public uint Size;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    private sealed class WindowsTrustApi : ITrustApi
    {
        internal static readonly WindowsTrustApi Instance = new();

        public int Verify(ref Guid action, ref TrustData data) => WinVerifyTrust(new IntPtr(-1), ref action, ref data);

        public X500DistinguishedName? ReadPrimarySignerSubject(IntPtr state)
        {
            var provider = WTHelperProvDataFromStateData(state);
            if (provider == IntPtr.Zero) return null;
            var signerPointer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            if (signerPointer == IntPtr.Zero) return null;
            var signer = Marshal.PtrToStructure<ProviderSigner>(signerPointer);
            if (signer.Size < Marshal.SizeOf<ProviderSigner>() || signer.Error != 0 ||
                signer.CertificateCount == 0 || (signer.SignerType & 0x10) != 0) return null;
            var certificatePointer = WTHelperGetProvCertFromChain(signerPointer, 0);
            if (certificatePointer == IntPtr.Zero) return null;
            var certificate = Marshal.PtrToStructure<ProviderCertificate>(certificatePointer);
            if (certificate.Size < Marshal.SizeOf<ProviderCertificate>() || certificate.Certificate == IntPtr.Zero ||
                certificate.Error != 0 || certificate.TestCertificate != 0) return null;
            using var leaf = new X509Certificate2(certificate.Certificate);
            return new X500DistinguishedName(leaf.SubjectName.RawData);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProviderSigner
        {
            public uint Size;
            public System.Runtime.InteropServices.ComTypes.FILETIME VerifyAsOf;
            public uint CertificateCount;
            public IntPtr CertificateChain;
            public uint SignerType;
            public IntPtr SignerInfo;
            public uint Error;
            public uint CounterSignerCount;
            public IntPtr CounterSigners;
            public IntPtr ChainContext;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProviderCertificate
        {
            public uint Size;
            public IntPtr Certificate;
            public int Commercial;
            public int TrustedRoot;
            public int SelfSigned;
            public int TestCertificate;
            public uint RevokedReason;
            public uint Confidence;
            public uint Error;
            public IntPtr TrustListContext;
            public int TrustListSignerCertificate;
            public IntPtr CtlContext;
            public uint CtlError;
            public int IsCyclic;
            public IntPtr ChainElement;
        }

        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);

        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);

        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provider, uint signer,
            [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);

        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint certificate);
    }
}
