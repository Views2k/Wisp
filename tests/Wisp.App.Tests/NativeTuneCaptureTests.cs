using System.Collections.Immutable;
using System.IO;
using Wisp.App;
using Wisp.App.Tunes;
using Wisp.Core;
using Wisp.Core.Tunes;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeTuneCaptureTests
{
    [Fact]
    public void TuneCapabilityDoesNotInheritOtherSteamOrStoreSupport()
    {
        Assert.True(NativeTuneCapture.Supports(NativeHudBuildContract.BuiltIn));
        Assert.False(NativeTuneCapture.Supports(NativeHudBuildContract.PreviousSteamBuiltIn));
        Assert.False(NativeTuneCapture.Supports(NativeHudBuildContract.StoreBuiltIn));
    }

    [Fact]
    public void ReadBudgetAndCancellationStopBeforeCallingMemory()
    {
        var memory = new Memory();
        var read = new NativeTuneRead(memory, CancellationToken.None, maximumBytes: 4);
        _ = read.Bytes(0x10000, 4);
        Assert.Throws<InvalidDataException>(() => read.Bytes(0x10000, 1));
        Assert.Equal(1, memory.Reads);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new NativeTuneRead(memory, cancellation.Token).Bytes(0x10000, 1));
        Assert.Equal(1, memory.Reads);
    }

    [Fact]
    public void ChangedBytesRejectTheSnapshot()
    {
        var memory = new Memory();
        var read = new NativeTuneRead(memory, CancellationToken.None);
        _ = read.Bytes(0x10000, 4);
        memory.Fill = 1;
        Assert.Throws<TuneChangedException>(read.VerifyStable);
    }

    [Fact]
    public void AssetDecoderRejectsUnexpectedBytesBeforeOpeningSQLite()
    {
        Assert.Throws<InvalidDataException>(() => TuneAssetCapture.Decode(new byte[4], new byte[1024], new byte[256], TestContext.Current.CancellationToken));
    }

    [Fact]
    public void LookupUsesOrdinalAndParentRatherThanPartIdSuffix()
    {
        var metadata = new TuneAssetMetadata([
            new(TunePartId.Brakes, 1229, 123000, 3, null),
            new(TunePartId.Brakes, 999, 123000, 1, null),
            new(TunePartId.Drivetrain, 1229, 44, 4, 77),
            new(TunePartId.Transmission, 77, 99, 3, null),
            new(TunePartId.Transmission, 88, 99, 1, null)]);
        var resolved = metadata.Resolve(1229, [new(TunePartId.Brakes, 123000, null),
            new(TunePartId.Drivetrain, 44, null), new(TunePartId.Transmission, 99, null)]);
        Assert.Equal(3, resolved[0].Level);
        Assert.Equal(3, resolved[2].Level);
        Assert.Null(metadata.Resolve(1229, [new(TunePartId.Brakes, 123001, null)])[0].Level);
        Assert.Equal(-1, metadata.Resolve(1229, [new(TunePartId.Brakes, -1, null)])[0].Level);
    }

    [Fact]
    public void DuplicatePartLookupIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new TuneAssetMetadata([
            new(TunePartId.Brakes, 1, 2, 3, null), new(TunePartId.Brakes, 1, 2, 4, null)]));
    }

    [Fact]
    public async Task ConcurrentRequestsCoalesceAndOneCancelledWaiterDoesNotCancelTheOther()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var factory = new Factory();
        await using var service = new TuneCaptureService(factory, (_, token) =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5), token));
            return Input();
        });
        using var caller = new CancellationTokenSource();
        var first = service.RequestSnapshotAsync(caller.Token);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        var second = service.RequestSnapshotAsync(TestContext.Current.CancellationToken);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        release.Set();
        Assert.True((await second).Success);
        Assert.Equal(1, factory.Opens);
    }

    [Fact]
    public async Task SameSessionRefreshRetainsReviewedSnapshotButRestartAndInvalidationDoNot()
    {
        var factory = new Factory();
        await using var service = new TuneCaptureService(factory, (_, _) => Input());
        var first = (await service.RequestSnapshotAsync(TestContext.Current.CancellationToken)).Snapshot!;
        Assert.True(service.IsCurrent(first));
        var refresh = (await service.RequestSnapshotAsync(TestContext.Current.CancellationToken)).Snapshot!;
        Assert.True(service.IsCurrent(first));
        Assert.True(service.IsCurrent(refresh));
        factory.Session = "another-session";
        var restarted = (await service.RequestSnapshotAsync(TestContext.Current.CancellationToken)).Snapshot!;
        Assert.False(service.IsCurrent(first));
        Assert.True(service.IsCurrent(restarted));
        service.Invalidate();
        Assert.False(service.IsCurrent(restarted));
    }

    [Fact]
    public async Task CarChangeAndCompatibilityChangeInvalidateOldSnapshots()
    {
        var factory = new Factory();
        var ordinal = 1;
        await using var service = new TuneCaptureService(factory, (_, _) => Input() with { CarOrdinal = ordinal });
        var first = (await service.RequestSnapshotAsync(TestContext.Current.CancellationToken)).Snapshot!;
        ordinal = 2;
        var second = (await service.RequestSnapshotAsync(TestContext.Current.CancellationToken)).Snapshot!;
        Assert.False(service.IsCurrent(first));
        Assert.True(service.IsCurrent(second));
        factory.Generation++;
        Assert.False(service.IsCurrent(second));
    }

    [Fact]
    public async Task CompatibilityObservationNotifiesOnceAndReleasesObservationDemand()
    {
        var factory = new Factory();
        await using var service = new TuneCaptureService(factory, (_, _) => Input());
        var notifications = 0;
        service.Invalidated += (_, _) => notifications++;
        Assert.False(service.NeedsGameObservation);
        var accepted = (await service.RequestSnapshotAsync(TestContext.Current.CancellationToken)).Snapshot!;
        Assert.True(service.NeedsGameObservation);
        factory.Generation++;
        service.ObserveCompatibilityGeneration();
        service.ObserveCompatibilityGeneration();
        Assert.False(service.IsCurrent(accepted));
        Assert.False(service.NeedsGameObservation);
        Assert.Equal(1, notifications);
        Assert.Equal(1, factory.Opens);
    }

    [Fact]
    public async Task ActiveCaptureRequestsGameObservationUntilItFinishes()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var service = new TuneCaptureService(new Factory(), (_, token) =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5), token));
            throw new InvalidDataException("Fixture verification failed.");
        });
        var pending = service.RequestSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.True(service.NeedsGameObservation);
        release.Set();
        Assert.Equal(TuneCaptureStatus.Unavailable, (await pending).Status);
        Assert.False(service.NeedsGameObservation);
    }

    [Fact]
    public async Task FailedVerificationInvalidatesThePreviouslyAcceptedSnapshot()
    {
        var fail = false;
        await using var service = new TuneCaptureService(new Factory(), (_, _) =>
            fail ? throw new InvalidDataException("Fixture verification failed.") : Input());
        var first = await service.RequestSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(first.Snapshot);
        Assert.True(service.IsCurrent(first.Snapshot));
        fail = true;
        var rejected = await service.RequestSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TuneCaptureStatus.Unavailable, rejected.Status);
        Assert.Null(rejected.Snapshot);
        Assert.False(service.IsCurrent(first.Snapshot));
    }

    [Fact]
    public async Task LateReadAfterInvalidationCannotPublishASnapshot()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var service = new TuneCaptureService(new Factory(), (_, _) =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            return Input();
        });
        var pending = service.RequestSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        service.Invalidate();
        release.Set();
        Assert.Null((await pending).Snapshot);
    }

    internal static TuneDecodeInput Input()
    {
        var words = Enumerable.Repeat(BitConverter.SingleToUInt32Bits(.5f), 46).ToImmutableArray();
        var range = new TuneRange(1, 2);
        return new TuneDecodeInput
        {
            CapturedAtUtc = DateTimeOffset.UtcNow,
            GameVersion = TuneDecoder.SupportedGameVersion,
            ExecutableSha256 = TuneDecoder.SupportedExecutableSha256,
            ExecutableVerified = true,
            CaptureComplete = true,
            Coherent = true,
            LocalProviderCount = 1,
            CarOrdinal = 1,
            Drivetrain = TuneDrivetrain.AllWheelDrive,
            ObservedGearEntryCount = 7,
            UnitPreference = 0,
            NormalizedCopies = [words, words, words],
            Bounds = new(range, range, range, range, range, range),
            Conversions = new Dictionary<TuneQuantity, TuneConversion>
            {
                [TuneQuantity.Number] = new(0, 1, false),
                [TuneQuantity.Percentage] = new(1, 1, false),
                [TuneQuantity.Pressure] = new(41, 1, false),
                [TuneQuantity.Angle] = new(47, 1, false),
                [TuneQuantity.SpringRate] = new(54, 1, false),
                [TuneQuantity.RideHeight] = new(14, 1, false),
                [TuneQuantity.Downforce] = new(60, 1, false)
            }.ToImmutableDictionary(),
            Format = new(.017453292f, .5, -.5, .1, 10),
            CarRanges = Enum.GetValues<TuneFieldId>().ToImmutableDictionary(id => id, _ => range),
            SpringScale = 1,
            PartLevelsResolved = true,
            Parts = Enum.GetValues<TunePartId>().Select(kind => new TunePart(kind, 1, 3)).ToImmutableArray()
        };
    }

    private sealed class Factory : INativeHudProcessMemoryFactory
    {
        internal int Opens;
        internal string Session = "test-session";
        internal long Generation;
        public long CompatibilityGeneration => Generation;
        public bool TryOpen(out INativeHudProcessMemory? memory, out NativeAssistProviderStatus status)
        {
            Interlocked.Increment(ref Opens);
            memory = new Memory { SessionIdentity = Session };
            status = NativeAssistProviderStatus.Ready;
            return true;
        }
    }
    private sealed class Memory : INativeHudProcessMemory
    {
        internal int Reads;
        internal byte Fill;
        public ulong ModuleBase => 0x140000000;
        public string SessionIdentity { get; init; } = "test-session";
        public bool TryReadByte(ulong address, out byte value) { value = 0; return false; }
        public bool TryReadUInt32(ulong address, out uint value) { value = 0; return false; }
        public bool TryReadUInt64(ulong address, out ulong value) { value = 0; return false; }
        public bool TryReadSingle(ulong address, out float value) { value = 0; return false; }
        public bool TryReadBytes(ulong address, Span<byte> destination) { Reads++; destination.Fill(Fill); return true; }
        public void Dispose() { }
    }
}
