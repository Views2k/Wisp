using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wisp.App.Supplementary;
using Xunit;

namespace Wisp.App.Tests;

public sealed class SupplementaryContentTests
{
    [Theory]
    [InlineData("support-note")]
    [InlineData("dashboard-banner")]
    public void SignedSlotsAreSingletonEvenForDifferentAudiences(string kind)
    {
        using var f = new SupplementaryFixture();
        var payload = f.Payload();
        var item = payload["items"]![0]!;
        item["kind"] = kind; item["url"] = null;
        Assert.Single(f.Verifier.Verify(f.Envelope(payload), f.Now).Snapshot.Items);
        var duplicate = item.DeepClone(); duplicate["id"] = "second";
        duplicate["audience"]!["platforms"] = new JsonArray("store");
        payload["items"]!.AsArray().Add(duplicate);
        Assert.Throws<SupplementaryValidationException>(() => f.Verifier.Verify(f.Envelope(payload), f.Now));
    }

    [Fact]
    public void DashboardSlotCannotCarryAnActionEvenWithAnAllowedSignedUrl()
    {
        using var f = new SupplementaryFixture();
        var payload = f.Payload(); payload["items"]![0]!["kind"] = "dashboard-banner";
        Assert.Throws<SupplementaryValidationException>(() => f.Verifier.Verify(f.Envelope(payload), f.Now));
    }

    [Fact]
    public void SlotsReturnToLocalContentOnExpiryWithdrawalOrAudienceMismatch()
    {
        using var f = new SupplementaryFixture();
        var audience = new SupplementaryAudienceContext("2.6.6", "stable", "steam");
        var payload = f.Payload();
        var banner = payload["items"]![0]!;
        banner["kind"] = "dashboard-banner"; banner["url"] = null;
        var note = banner.DeepClone(); note["kind"] = "support-note"; note["id"] = "note";
        payload["items"]!.AsArray().Add(note);
        var content = f.Verifier.Verify(f.Envelope(payload), f.Now).Snapshot;
        var slots = SupplementaryContentSlots.Select(content, audience, f.Now);
        Assert.NotNull(slots.Note); Assert.NotNull(slots.Banner);
        Assert.Equal(f.Now.AddHours(2), slots.NextChange);
        Assert.Null(SupplementaryContentSlots.Select(content, audience with { Platform = "store" }, f.Now).Banner);
        Assert.Null(SupplementaryContentSlots.Select(content, audience, f.Now.AddHours(2)).Banner);
        Assert.Null(SupplementaryContentSlots.Select(content, audience, f.Now.AddDays(2)).Note);
        payload["revision"] = 2; payload["items"] = new JsonArray(note.DeepClone());
        var withdrawn = f.Verifier.Verify(f.Envelope(payload), f.Now).Snapshot;
        Assert.Null(SupplementaryContentSlots.Select(withdrawn, audience, f.Now).Banner);
        Assert.NotNull(SupplementaryContentSlots.Select(withdrawn, audience, f.Now).Note);
        banner["startsAt"] = f.Now.AddHours(1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        payload["items"] = new JsonArray(banner.DeepClone());
        var scheduled = f.Verifier.Verify(f.Envelope(payload), f.Now).Snapshot;
        Assert.Null(SupplementaryContentSlots.Select(scheduled, audience, f.Now).Banner);
        Assert.Equal(f.Now.AddHours(1), SupplementaryContentSlots.Select(scheduled, audience, f.Now).NextChange);
        Assert.NotNull(SupplementaryContentSlots.Select(scheduled, audience, f.Now.AddHours(1)).Banner);
    }

    [Fact]
    public void RepublishedContentMayPreserveAnEarlierStartWithinItsOwnBoundedLifetime()
    {
        using var f = new SupplementaryFixture();
        var payload = f.Payload();
        payload["items"]![0]!["startsAt"] = f.Now.AddHours(-1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var snapshot = f.Verifier.Verify(f.Envelope(payload), f.Now).Snapshot;
        Assert.Single(snapshot.ForAudience(new("2.6.6", "stable", "steam", "6.461.0.0"), f.Now));
        payload["items"]![0]!["startsAt"] = f.Now.AddDays(-8).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        Assert.Throws<SupplementaryValidationException>(() => f.Verifier.Verify(f.Envelope(payload), f.Now));
    }

    [Fact]
    public void SignedContentTargetsExactSupportedAudienceWithoutChangingLocalState()
    {
        using var f = new SupplementaryFixture();
        var snapshot = f.Verifier.Verify(f.Envelope(), f.Now).Snapshot;
        Assert.Single(snapshot.ForAudience(new("2.6.6", "stable", "steam", "6.461.0.0"), f.Now));
        Assert.Empty(snapshot.ForAudience(new("2.6.5", "stable", "steam"), f.Now));
        Assert.Empty(snapshot.ForAudience(new("2.6.6", "private", "steam"), f.Now));
        Assert.Empty(snapshot.ForAudience(new("2.6.6", "stable", "unknown"), f.Now));
        Assert.Empty(snapshot.ForAudience(new("2.6.6", "stable", "steam"), f.Now.AddDays(2)));
    }

    [Theory]
    [InlineData("setNativeOffset")]
    [InlineData("enableFeature")]
    [InlineData("settings")]
    public void CorrectSignatureCannotAuthorizeUnknownOperations(string field)
    {
        using var f = new SupplementaryFixture();
        var payload = f.Payload(); payload[field] = true;
        Assert.Throws<SupplementaryValidationException>(() => f.Verifier.Verify(f.Envelope(payload), f.Now));
    }

    [Fact]
    public void DuplicateJsonFieldsAreRejectedEvenWhenSigned()
    {
        using var f = new SupplementaryFixture();
        var text = f.Payload().ToJsonString().Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal);
        Assert.Throws<SupplementaryValidationException>(() => f.Verifier.Verify(f.Sign(Encoding.UTF8.GetBytes(text)), f.Now));
    }

    [Theory]
    [InlineData("purpose", "wisp-native-hud-compatibility")]
    [InlineData("issuedAt", "2099-01-01T00:00:00Z")]
    [InlineData("expiresAt", "2020-01-01T00:00:00Z")]
    [InlineData("expiresAt", "2030-01-01T00:00:00Z")]
    public void PurposeAndTimeBoundsAreEnforced(string property, string value)
    {
        using var f = new SupplementaryFixture();
        var payload = f.Payload(); payload[property] = value;
        Assert.Throws<SupplementaryValidationException>(() => f.Verifier.Verify(f.Envelope(payload), f.Now));
    }

    [Fact]
    public void SignatureMutationAndRemoteKeySubstitutionFail()
    {
        using var f = new SupplementaryFixture();
        var envelope = JsonNode.Parse(f.Envelope())!;
        envelope["signature"] = Convert.ToBase64String(new byte[64]);
        Assert.Throws<SupplementaryValidationException>(() => f.Verifier.Verify(Encoding.UTF8.GetBytes(envelope.ToJsonString()), f.Now));
        envelope = JsonNode.Parse(f.Envelope())!; envelope["keyId"] = "untrusted";
        Assert.Throws<SupplementaryValidationException>(() => f.Verifier.Verify(Encoding.UTF8.GetBytes(envelope.ToJsonString()), f.Now));
        envelope = JsonNode.Parse(f.Envelope())!; envelope["publicKey"] = "untrusted";
        Assert.Throws<SupplementaryValidationException>(() => f.Verifier.Verify(Encoding.UTF8.GetBytes(envelope.ToJsonString()), f.Now));
    }

    [Theory]
    [InlineData("http://wispoverlay.com/help")]
    [InlineData("https://wispoverlay.com.evil.invalid/help")]
    [InlineData("https://github.com/Views2k/Wisp-other")]
    [InlineData("https://github.com/elsewhere")]
    [InlineData("https://github.com/Views2k/Wisp/../../elsewhere")]
    [InlineData("https://user@wispoverlay.com/help")]
    [InlineData("https://wispoverlay.com:444/help")]
    public void ContentLinksCannotEscapeAllowedDestinations(string url) => Assert.False(SupplementaryContentVerifier.ValidUrl(url));

    [Fact]
    public void CachePreservesWithdrawalFloorAcrossRestartAndExpiry()
    {
        using var f = new SupplementaryFixture();
        var first = f.Envelope();
        var store = new SupplementaryContentStore(f.Verifier, f.Directory);
        Assert.True(store.Accept(first, f.Now) == SupplementaryContentAcceptance.Accepted, store.LastFailureCode);
        var withdrawn = f.Payload(2); withdrawn["items"] = new JsonArray();
        Assert.Equal(SupplementaryContentAcceptance.Accepted, store.Accept(f.Envelope(withdrawn), f.Now));
        var restarted = new SupplementaryContentStore(f.Verifier, f.Directory);
        Assert.Equal(2, restarted.HighestRevision);
        Assert.Empty(restarted.Current(f.Now).Items);
        Assert.Equal(SupplementaryContentAcceptance.Rejected, restarted.Accept(first, f.Now));
        Assert.Equal(0, restarted.Current(f.Now.AddDays(2)).Revision);
        Assert.Equal(2, restarted.HighestRevision);
    }

    [Fact]
    public void SameRevisionCannotChangePayloadAndConcurrentStoresReloadFloor()
    {
        using var f = new SupplementaryFixture();
        var first = new SupplementaryContentStore(f.Verifier, f.Directory);
        var second = new SupplementaryContentStore(f.Verifier, f.Directory);
        Assert.Equal(SupplementaryContentAcceptance.Accepted, first.Accept(f.Envelope(f.Payload(4)), f.Now));
        Assert.Equal(SupplementaryContentAcceptance.Rejected, second.Accept(f.Envelope(), f.Now));
        var changed = f.Payload(4); changed["reportingDisabled"] = true;
        Assert.Equal(SupplementaryContentAcceptance.Rejected, second.Accept(f.Envelope(changed), f.Now));
        Assert.Equal(SupplementaryContentAcceptance.Unchanged, second.Accept(f.Envelope(f.Payload(4)), f.Now));
    }

    [Fact]
    public void CorruptCacheDoesNotResetReplayProtection()
    {
        using var f = new SupplementaryFixture();
        System.IO.Directory.CreateDirectory(f.Directory);
        File.WriteAllText(Path.Combine(f.Directory, "content.json"), "broken");
        var store = new SupplementaryContentStore(f.Verifier, f.Directory);
        Assert.False(store.IsHealthy);
        Assert.Equal(SupplementaryContentAcceptance.CacheUnavailable, store.Accept(f.Envelope(), f.Now));
        Assert.Equal("broken", File.ReadAllText(Path.Combine(f.Directory, "content.json")));
    }

    [Fact]
    public void SignatureRequiresCanonicalBase64AndP256()
    {
        using var f = new SupplementaryFixture();
        var node = JsonNode.Parse(f.Envelope())!;
        node["payload"] = " " + node["payload"]!.GetValue<string>();
        Assert.Throws<SupplementaryValidationException>(() => f.Verifier.Verify(Encoding.UTF8.GetBytes(node.ToJsonString()), f.Now));
        using var wrong = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Throws<CryptographicException>(() => new SupplementaryContentVerifier(new Dictionary<string, byte[]> { ["test"] = wrong.ExportSubjectPublicKeyInfo() }));
    }
}

internal sealed class SupplementaryFixture : IDisposable
{
    internal DateTimeOffset Now { get; } = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    internal string Directory { get; } = Path.Combine(AppContext.BaseDirectory, "wisp-supplementary-tests", Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly Guid _session = Guid.NewGuid(), _installation = Guid.NewGuid();
    internal SupplementaryContentVerifier Verifier { get; }
    internal SupplementaryFixture() => Verifier = new(new Dictionary<string, byte[]> { ["fixture-only"] = _key.ExportSubjectPublicKeyInfo() });
    internal JsonObject Payload(int revision = 1) => new()
    {
        ["schemaVersion"] = 1, ["purpose"] = "wisp-supplementary-content", ["revision"] = revision,
        ["issuedAt"] = Now.AddMinutes(-1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
        ["expiresAt"] = Now.AddDays(1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), ["reportingDisabled"] = false,
        ["items"] = new JsonArray(new JsonObject
        {
            ["id"] = "notice", ["kind"] = "announcement", ["title"] = "Wisp update", ["message"] = "A reviewed change is available.",
            ["url"] = "https://github.com/Views2k/Wisp/releases", ["severity"] = "info", ["startsAt"] = Now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            ["expiresAt"] = Now.AddHours(2).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), ["dismissible"] = true,
            ["audience"] = new JsonObject
            { ["minVersion"] = "2.6.6", ["maxVersion"] = null, ["channels"] = new JsonArray("stable"), ["platforms"] = new JsonArray("steam"), ["gameBuilds"] = new JsonArray() }
        })
    };
    internal byte[] Envelope(JsonObject? payload = null) => Sign(Encoding.UTF8.GetBytes((payload ?? Payload()).ToJsonString()));
    internal byte[] Sign(byte[] payload) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        schemaVersion = 1, keyId = "fixture-only", payload = Convert.ToBase64String(payload),
        signature = Convert.ToBase64String(_key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
    });
    internal SupplementaryEvent Event() => new(1, Guid.NewGuid(), _session, _installation, Now, "2.6.6", "test-revision", "stable",
        "connection", "telemetry", "success", "steam", DurationMs: 20, Stage: "validation");
    public void Dispose()
    {
        _key.Dispose();
        if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
    }
}
