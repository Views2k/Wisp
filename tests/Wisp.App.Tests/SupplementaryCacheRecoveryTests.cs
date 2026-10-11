using System.IO;
using System.Diagnostics;
using System.Text;
using Wisp.App.Supplementary;
using Xunit;

namespace Wisp.App.Tests;

public sealed class SupplementaryCacheRecoveryTests
{
    [Fact]
    public void RedirectedProfileAllowsStableIdentityAndSignedCacheAcrossRestarts()
    {
        using var f = new SupplementaryFixture();
        Directory.CreateDirectory(f.Directory);
        var target = Path.Combine(f.Directory, "profile-target");
        Directory.CreateDirectory(target);
        var link = Path.Combine(f.Directory, "profile-alias");
        CreateJunction(link, target);
        try
        {
            var directory = Path.Combine(link, "Wisp", "Supplementary");
            var root = Path.Combine(link, "Wisp");
            Assert.Null(SupplementaryIdentityStore.LoadOrCreate(directory));
            Assert.False(File.Exists(Path.Combine(directory, "reporting-identity.txt")));
            var firstIdentity = Assert.IsType<SupplementaryInstallationIdentity>(SupplementaryIdentityStore.LoadOrCreate(directory, root));
            var nextIdentity = Assert.IsType<SupplementaryInstallationIdentity>(SupplementaryIdentityStore.LoadOrCreate(directory, root));
            Assert.Equal(firstIdentity.Id, nextIdentity.Id);
            Assert.False(nextIdentity.NewlyCreated);
            var content = Path.Combine(directory, "Content");
            var first = new SupplementaryContentStore(f.Verifier, content, root);
            Assert.True(first.IsHealthy);
            Assert.Equal(SupplementaryContentAcceptance.Accepted, first.Accept(f.Envelope(f.Payload(2)), f.Now));
            var restarted = new SupplementaryContentStore(f.Verifier, content, root);
            Assert.True(restarted.IsHealthy);
            Assert.Equal(2, restarted.HighestRevision);
            Assert.Equal(SupplementaryContentAcceptance.Rejected, restarted.Accept(f.Envelope(), f.Now));
        }
        finally { Directory.Delete(link); }
    }

    [Theory]
    [InlineData("Wisp")]
    [InlineData("Wisp/Supplementary")]
    [InlineData("Wisp/Supplementary/Content")]
    public void LinksInsideOwnedSubtreeAreRejectedBeforeCreatingFiles(string relative)
    {
        using var f = new SupplementaryFixture();
        var target = Path.Combine(f.Directory, "outside");
        Directory.CreateDirectory(target);
        var link = Path.Combine(f.Directory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        CreateJunction(link, target);
        try
        {
            var root = Path.Combine(f.Directory, "Wisp");
            var directory = Path.Combine(root, "Supplementary");
            if (relative != "Wisp/Supplementary/Content")
                Assert.Null(SupplementaryIdentityStore.LoadOrCreate(directory, root));
            var content = new SupplementaryContentStore(f.Verifier, Path.Combine(directory, "Content"), root);
            Assert.Equal(SupplementaryContentAcceptance.CacheUnavailable, content.Accept(f.Envelope(), f.Now));
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void OwnedRootCannotAuthorizeSiblingOrParentStorage()
    {
        using var f = new SupplementaryFixture();
        var root = Path.Combine(f.Directory, "Wisp");
        Assert.Null(SupplementaryIdentityStore.LoadOrCreate(root + "Other", root));
        Assert.Null(SupplementaryIdentityStore.LoadOrCreate(f.Directory, root));
        Assert.False(Directory.Exists(f.Directory));
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("oversized")]
    [InlineData("locked")]
    [InlineData("retired-key")]
    public async Task UnreadableContentReceiptKeepsReportingAndSupportWithoutResettingReplayFloor(string failure)
    {
        using var f = new SupplementaryFixture();
        using var replacementKey = new SupplementaryFixture();
        Directory.CreateDirectory(f.Directory);
        var path = Path.Combine(f.Directory, "content.json");
        Assert.Equal(SupplementaryContentAcceptance.Accepted, new SupplementaryContentStore(f.Verifier, f.Directory).Accept(f.Envelope(), f.Now));
        if (failure == "corrupt") File.WriteAllText(path, "broken");
        if (failure == "oversized") File.WriteAllBytes(path, new byte[180 * 1024 + 1]);
        var original = File.ReadAllBytes(path);
        using var locked = failure == "locked" ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None) : null;
        var verifier = failure == "retired-key" ? replacementKey.Verifier : f.Verifier;
        var store = new SupplementaryContentStore(verifier, f.Directory);
        var transport = new RecordingTransport();
        using var client = new SupplementaryClient(new(new Uri("https://console.example.invalid/")), store, new FixedClock(f.Now), transport);
        Assert.False(store.IsHealthy);
        Assert.True(client.IsReportingEnabled);
        Assert.True(client.TryEnqueue(f.Event()));
        Assert.True(await client.FlushBatchAsync(TestContext.Current.CancellationToken));
        var report = new SupplementarySupportReport(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), f.Now,
            "2.6.6", "test", "stable", "feedback", "Example", "Fixture feedback.");
        Assert.Equal(SupplementaryRequestStatus.Success, (await client.SubmitSupportAsync(report, TestContext.Current.CancellationToken)).Status);
        await client.RefreshContentAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, transport.Calls);
        Assert.Empty(client.Content.Items);
        Assert.Equal(SupplementaryContentAcceptance.CacheUnavailable, store.Accept(
            failure == "retired-key" ? replacementKey.Envelope(replacementKey.Payload(2)) : f.Envelope(f.Payload(2)), f.Now));
        locked?.Dispose();
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => now; }
    private static void CreateJunction(string path, string target)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        var start = new ProcessStartInfo("powershell.exe")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add($"$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path {Quote(path)} -Target {Quote(target)} | Out-Null");
        using var process = Process.Start(start)!;
        if (!process.WaitForExit(10000)) { process.Kill(entireProcessTree: true); throw new TimeoutException("Disposable junction creation timed out."); }
        Assert.Equal(0, process.ExitCode);
    }
    private sealed class RecordingTransport : ISupplementaryTransport
    {
        internal int Calls;
        public Task<SupplementaryResponse> SendAsync(string route, byte[]? body, int maximumResponseBytes, CancellationToken cancellation)
        {
            Calls++;
            return Task.FromResult(new SupplementaryResponse(SupplementaryRequestStatus.Success,
                Encoding.UTF8.GetBytes(route.EndsWith("support", StringComparison.Ordinal)
                    ? "{\"schemaVersion\":1,\"reference\":\"WSP-12345678901234567890123456789012\",\"nextAllowedAt\":\"2026-10-11T00:00:00.000Z\",\"quota\":{\"policy\":\"installation-utc-day\",\"limit\":1}}"
                    : "{\"schemaVersion\":1,\"accepted\":1,\"duplicate\":0}")));
        }
        public void Dispose() { }
    }
}
