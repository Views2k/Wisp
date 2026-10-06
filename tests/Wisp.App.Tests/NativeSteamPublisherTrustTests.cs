using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Wisp.App;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeSteamPublisherTrustTests
{
    [Fact]
    public void TrustedPrimaryPublisherUsesOfflinePolicyAndHoldsFileUntilStateIsClosed()
    {
        using var file = new TestFile();
        var api = new FakeTrustApi(file.Path);

        Assert.True(NativeSteamPublisherTrust.TryVerify(file.Path, api));

        Assert.Equal(["verify", "primary-signer", "close"], api.Events);
        Assert.Equal(NativeSteamPublisherTrust.AuthenticodeAction, api.Action);
        Assert.Equal(2U, api.Policy.UiChoice);
        Assert.Equal(1U, api.Policy.RevocationChecks);
        Assert.Equal(1U, api.Policy.UnionChoice);
        Assert.Equal(0x1000U | 0x80U | 0x2000U, api.Policy.ProviderFlags);
        Assert.Equal(IntPtr.Zero, api.Policy.SignatureSettings);
        Assert.Equal(IntPtr.Zero, api.Policy.PolicyCallbackData);
        Assert.Equal(IntPtr.Zero, api.Policy.SipClientData);
        Assert.Equal(IntPtr.Zero, api.Policy.UrlReference);
        Assert.Equal((uint)Marshal.SizeOf<NativeSteamPublisherTrust.TrustData>(), api.Policy.Size);
        Assert.Equal(2, api.HeldFileChecks);
        using var reopened = File.Open(file.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(4096, reopened.Length);
    }

    [Theory]
    [InlineData(unchecked((int)0x800B0100))] // No signature.
    [InlineData(unchecked((int)0x80096010))] // Bad digest.
    [InlineData(unchecked((int)0x800B0109))] // Untrusted root.
    [InlineData(unchecked((int)0x800B010C))] // Revoked.
    [InlineData(unchecked((int)0x80092013))] // Revocation evidence unavailable offline.
    [InlineData(1)] // Only zero is success.
    public void UntrustedOrUnavailableSignatureNeverReadsPublisherAndClosesState(int result)
    {
        using var file = new TestFile();
        var api = new FakeTrustApi(file.Path) { Result = result };

        Assert.False(NativeSteamPublisherTrust.TryVerify(file.Path, api));
        Assert.Equal(["verify", "close"], api.Events);
    }

    [Fact]
    public void SuccessfulReturnWithoutTrustStateCannotAuthorizeTheFile()
    {
        using var file = new TestFile();
        var api = new FakeTrustApi(file.Path) { AllocateState = false };

        Assert.False(NativeSteamPublisherTrust.TryVerify(file.Path, api));
        Assert.Equal(["verify"], api.Events);
    }

    [Fact]
    public void FailedTrustStateCleanupCannotReportSuccess()
    {
        using var file = new TestFile();
        var api = new FakeTrustApi(file.Path) { CloseResult = 1 };

        Assert.False(NativeSteamPublisherTrust.TryVerify(file.Path, api));
        Assert.Equal(["verify", "primary-signer", "close"], api.Events);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("CN=Microsoft Corporation, O=Microsoft Corporation")]
    [InlineData("CN=playground games, O=Playground Games")]
    [InlineData("CN=Playground Games, O=playground games")]
    [InlineData("CN=Other Publisher, O=Other Publisher")]
    [InlineData("CN=Playground Games, O=Other Publisher")]
    [InlineData("CN=Other Publisher, O=Playground Games")]
    [InlineData("CN=Playground Games Test, O=Playground Games")]
    [InlineData("CN=Playground Games, O=Playground Games Test")]
    [InlineData("CN=Playground Games")]
    [InlineData("O=Playground Games")]
    [InlineData("CN=Playground Games, CN=Playground Games, O=Playground Games")]
    [InlineData("CN=Playground Games, O=Playground Games, O=Playground Games")]
    public void MissingDifferentOrAmbiguousPrimaryPublisherIsRejected(string? subject)
    {
        using var file = new TestFile();
        var api = new FakeTrustApi(file.Path) { Subject = subject is null ? null : new X500DistinguishedName(subject) };

        Assert.False(NativeSteamPublisherTrust.TryVerify(file.Path, api));
        Assert.Equal(["verify", "primary-signer", "close"], api.Events);
    }

    [Theory]
    [InlineData("CN=Playground Games, O=Playground Games, C=US")]
    [InlineData("C=US, O=Playground Games, OU=Renewed Signing Service, CN=Playground Games")]
    public void VerifiedPublisherIdentityDoesNotPinCertificateOrSubjectOrdering(string subject)
    {
        using var file = new TestFile();
        var api = new FakeTrustApi(file.Path) { Subject = new X500DistinguishedName(subject) };

        Assert.True(NativeSteamPublisherTrust.TryVerify(file.Path, api));
    }

    [Theory]
    [InlineData("verify")]
    [InlineData("primary-signer")]
    [InlineData("close")]
    public void ExpectedApiFailureRejectsAndReleasesHeldFile(string stage)
    {
        using var file = new TestFile();
        var api = new FakeTrustApi(file.Path) { ThrowAt = stage };

        Assert.False(NativeSteamPublisherTrust.TryVerify(file.Path, api));
        Assert.Equal("close", api.Events[^1]);
        using var reopened = File.Open(file.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(reopened.CanWrite);
    }

    [Fact]
    public void MissingAndRelativeFilesDoNotCallTrustProvider()
    {
        var api = new FakeTrustApi(string.Empty);
        Assert.False(NativeSteamPublisherTrust.TryVerify("", api));
        Assert.False(NativeSteamPublisherTrust.TryVerify("ForzaHorizon6.exe", api));
        Assert.False(NativeSteamPublisherTrust.TryVerify(@"\\?\C:\ForzaHorizon6.exe", api));
        Assert.False(NativeSteamPublisherTrust.TryVerify(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe"), api));
        Assert.Empty(api.Events);
    }

    [Fact]
    public void FileOpenForWritingCannotBeVerified()
    {
        using var file = new TestFile();
        using var writer = File.Open(file.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var api = new FakeTrustApi(file.Path);

        Assert.False(NativeSteamPublisherTrust.TryVerify(file.Path, api));
        Assert.Empty(api.Events);
    }

    [Fact]
    public void WinTrustInteropSizesMatchWindowsSdk()
    {
        Assert.Equal(IntPtr.Size == 8 ? 32 : 16, Marshal.SizeOf<NativeSteamPublisherTrust.TrustFileInfo>());
        Assert.Equal(IntPtr.Size == 8 ? 88 : 52, Marshal.SizeOf<NativeSteamPublisherTrust.TrustData>());
        Assert.Equal(IntPtr.Size == 8 ? 40 : 24,
            Marshal.OffsetOf<NativeSteamPublisherTrust.TrustData>(nameof(NativeSteamPublisherTrust.TrustData.FileInfo)).ToInt32());
    }

    private sealed class TestFile : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wisp-trust-" + Guid.NewGuid() + ".exe");
        internal TestFile() => File.WriteAllBytes(Path, new byte[4096]);
        public void Dispose() => File.Delete(Path);
    }

    private sealed class FakeTrustApi(string path) : NativeSteamPublisherTrust.ITrustApi
    {
        internal int Result { get; init; }
        internal int CloseResult { get; init; }
        internal bool AllocateState { get; init; } = true;
        internal string? ThrowAt { get; init; }
        internal X500DistinguishedName? Subject { get; init; } = new("CN=Playground Games, O=Playground Games");
        internal List<string> Events { get; } = [];
        internal Guid Action { get; private set; }
        internal NativeSteamPublisherTrust.TrustData Policy { get; private set; }
        internal int HeldFileChecks { get; private set; }

        public int Verify(ref Guid action, ref NativeSteamPublisherTrust.TrustData data)
        {
            Action = action;
            var stage = data.StateAction == NativeSteamPublisherTrust.VerifyState ? "verify" : "close";
            Events.Add(stage);
            var info = Marshal.PtrToStructure<NativeSteamPublisherTrust.TrustFileInfo>(data.FileInfo);
            Assert.Equal((uint)Marshal.SizeOf<NativeSteamPublisherTrust.TrustFileInfo>(), info.Size);
            Assert.Equal(path, Marshal.PtrToStringUni(info.FilePath));
            Assert.NotEqual(IntPtr.Zero, info.FileHandle);
            Assert.NotEqual(new IntPtr(-1), info.FileHandle);
            Assert.Throws<IOException>(() => File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete));
            HeldFileChecks++;
            if (stage == "verify")
            {
                Policy = data;
                data.StateData = AllocateState ? new IntPtr(1234) : IntPtr.Zero;
            }
            else
            {
                Assert.Equal(NativeSteamPublisherTrust.CloseState, data.StateAction);
                Assert.Equal(new IntPtr(1234), data.StateData);
                data.StateData = IntPtr.Zero;
            }
            if (ThrowAt == stage) throw new CryptographicException("Mock trust-provider failure.");
            return stage == "verify" ? Result : CloseResult;
        }

        public X500DistinguishedName? ReadPrimarySignerSubject(IntPtr state)
        {
            Events.Add("primary-signer");
            Assert.Equal(new IntPtr(1234), state);
            if (ThrowAt == "primary-signer") throw new CryptographicException("Mock signer failure.");
            return Subject;
        }
    }
}
