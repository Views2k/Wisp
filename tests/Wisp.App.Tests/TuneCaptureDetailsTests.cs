using System.IO;
using Wisp.App.Tunes;
using Wisp.Core;
using Wisp.Core.Tunes;
using Xunit;

namespace Wisp.App.Tests;

public sealed class TuneCaptureDetailsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UsesVerifiedBuildMetadata(bool store)
    {
        var pack = store ? NativeHudBuildContract.StoreBuiltIn : NativeHudBuildContract.BuiltIn;
        var details = TuneCaptureDetails.Create(TuneCaptureStage.ReadTune, pack);
        Assert.Contains($"Wisp: {ApplicationVersionInfo.MachineVersion}", details, StringComparison.Ordinal);
        Assert.Contains($"Game platform: {(store ? "Xbox/Store" : "Steam")}", details, StringComparison.Ordinal);
        Assert.Contains($"Game version: {pack.GameVersion}", details, StringComparison.Ordinal);
        if (ApplicationVersionInfo.DiagnosticBuildId is { } id)
            Assert.Contains($"Private build: {id}", details, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("C:\\Users\\PrivatePerson\\Documents\\secret.txt")]
    [InlineData("\\\\private-device\\share\\secret.txt")]
    [InlineData("private-person@example.invalid Bearer SECRET_VALUE 100.99.88.77")]
    [InlineData("The current car could not be read.\nPrivatePerson\npassword=SECRET_VALUE")]
    public void OmitsArbitraryExceptionMessagesAndPrivateData(string privateText)
    {
        var error = new IOException(privateText, new ArgumentException(privateText));
        error.Data["private"] = privateText;
        var details = TuneCaptureDetails.Create(TuneCaptureStage.ReadTune, exception: error);
        Assert.DoesNotContain(privateText, details, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET_VALUE", details, StringComparison.Ordinal);
        Assert.Contains("Message omitted because it may contain private information.", details, StringComparison.Ordinal);
        Assert.Contains("Error type: IOException", details, StringComparison.Ordinal);
        Assert.Contains("Error code: 0x", details, StringComparison.Ordinal);
        Assert.Contains("Game platform: Not verified", details, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsKnownThrowingMethodWithoutStackOrAddress()
    {
        var reader = new NativeTuneRead(new Memory(), CancellationToken.None);
        var error = Assert.ThrowsAny<Exception>(() => reader.Bytes(0x123, 0));
        var details = TuneCaptureDetails.Create(TuneCaptureStage.ReadTune, exception: error);
        Assert.Contains("Method: NativeTuneRead.Bytes", details, StringComparison.Ordinal);
        Assert.DoesNotContain("0x123", details, StringComparison.Ordinal);
        Assert.DoesNotContain(".cs:", details, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", details, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("The required tuning table schema does not match the supported build.")]
    [InlineData("The required tuning projection has an invalid row count.")]
    [InlineData("The required tuning values contain an invalid or duplicate identity.")]
    [InlineData("Local tuning metadata could not be verified (required-table-integrity-failed).")]
    [InlineData("Local tuning metadata could not be verified (sqlite-prepare-code-1).")]
    [InlineData("Local tuning metadata could not be verified (sqlite-read-rows-code--4).")]
    public void KeepsRecognizedValidationReason(string message)
    {
        var details = TuneCaptureDetails.Create(TuneCaptureStage.ReadTune, exception: new InvalidDataException(message));
        Assert.Contains($"Message: {message}", details, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sqlite-prepare-code-1 C:\\Users\\PrivatePerson")]
    [InlineData("sqlite-prepare-code-1\nprivate-person@example.invalid")]
    [InlineData("private-device-code-1")]
    [InlineData("required-table-integrity-failed C:\\private")]
    [InlineData("sqlite-prepare-code-999999999999999999999999")]
    public void RejectsUnrecognizedSqliteStageOrInjectedSuffix(string stage)
    {
        var details = TuneCaptureDetails.Create(TuneCaptureStage.ReadTune,
            exception: new InvalidDataException($"Local tuning metadata could not be verified ({stage})."));
        Assert.DoesNotContain(stage, details, StringComparison.Ordinal);
        Assert.Contains("Message omitted", details, StringComparison.Ordinal);
    }

    [Fact]
    public void TypedAssetFailureIncludesMeasuredSizeAndPagesWithoutItsArbitraryMessage()
    {
        var error = new TuneAssetValidationException(TuneAssetFailureCode.HeaderPageCount, 2048, 3, "PRIVATE_VALUE");
        var details = TuneCaptureDetails.Create(TuneCaptureStage.ReadTune, exception: error);
        Assert.Contains("Asset check: HeaderPageCount", details, StringComparison.Ordinal);
        Assert.Contains("Actual asset size (bytes): 2048", details, StringComparison.Ordinal);
        Assert.Contains("Maximum asset size (bytes): 67108864", details, StringComparison.Ordinal);
        Assert.Contains("Actual page count: 3", details, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE_VALUE", details, StringComparison.Ordinal);
    }

    [Fact]
    public void StreamFailureIncludesItsNumericShape()
    {
        var error = new TuneAssetStreamValidationException(1, 1024, 4096, 2048, 16);
        var details = TuneCaptureDetails.Create(TuneCaptureStage.ReadTune, exception: error);
        Assert.Contains("Asset check: StreamShape", details, StringComparison.Ordinal);
        Assert.Contains("Actual asset size (bytes): 2048", details, StringComparison.Ordinal);
        Assert.Contains("Stream flag: 1", details, StringComparison.Ordinal);
        Assert.Contains("Chunk size (bytes): 1024", details, StringComparison.Ordinal);
        Assert.Contains("Allocated size (bytes): 4096", details, StringComparison.Ordinal);
        Assert.Contains("Chunk vector size (bytes): 16", details, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(NativeAssistProviderStatus.GameNotRunning, TuneCaptureStatus.GameNotRunning)]
    [InlineData(NativeAssistProviderStatus.UnsupportedBuild, TuneCaptureStatus.UnsupportedBuild)]
    [InlineData(NativeAssistProviderStatus.AccessDenied, TuneCaptureStatus.Unavailable)]
    [InlineData(NativeAssistProviderStatus.ReadFailure, TuneCaptureStatus.Unavailable)]
    public async Task OpenFailureKeepsItsSpecificStatus(NativeAssistProviderStatus provider, TuneCaptureStatus expected)
    {
        await using var service = new TuneCaptureService(new Factory(provider), (_, _) => throw new InvalidOperationException());
        var result = await service.RequestSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.Status);
        Assert.Contains($"Open status: {provider}", result.Details, StringComparison.Ordinal);
        Assert.Contains("Stage: OpenGame", result.Details, StringComparison.Ordinal);
        Assert.Contains("Game version: Not verified", result.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DecodeFailureIsReportedWithoutAnException()
    {
        await using var service = new TuneCaptureService(new Factory(), (_, _) => NativeTuneCaptureTests.Input() with { CaptureComplete = false });
        var result = await service.RequestSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TuneCaptureStatus.Unavailable, result.Status);
        Assert.Contains("Decode status: IncompleteCapture", result.Details, StringComparison.Ordinal);
        Assert.Contains("Method: TuneDecoder.TryDecode", result.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureExceptionIncludesBuildAndSanitizedDetails()
    {
        await using var service = new TuneCaptureService(new Factory(), (_, _) => throw new IOException("private message"));
        var result = await service.RequestSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TuneCaptureStatus.Unavailable, result.Status);
        Assert.Contains("Stage: ReadTune", result.Details, StringComparison.Ordinal);
        Assert.Contains($"Game version: {NativeHudBuildContract.BuiltIn.GameVersion}", result.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("private message", result.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessDoesNotCarryErrorDetails()
    {
        await using var service = new TuneCaptureService(new Factory(), (_, _) => NativeTuneCaptureTests.Input());
        var result = await service.RequestSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.True(result.Success);
        Assert.Empty(result.Details);
    }

    [Theory]
    [InlineData(0, "NoCar", "No current car is available.")]
    [InlineData(2, "AmbiguousCar", "Wisp could not identify one current car.")]
    public async Task VerifiedSelectionFailureExplainsRefreshAndReportsCount(int count, string reason, string message)
    {
        await using var service = new TuneCaptureService(new Factory(), (_, _) => throw new TuneCarSelectionException(count));
        var result = await service.RequestSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TuneCaptureStatus.Unavailable, result.Status);
        Assert.Null(result.Snapshot);
        Assert.Equal(message + " Drive in the open world, then refresh.", result.Message);
        Assert.Contains("Car selection: " + reason, result.Details, StringComparison.Ordinal);
        Assert.Contains($"Verified local car candidates: {count}", result.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PointerFailureDoesNotSuggestOpenWorldOrClaimNoCar()
    {
        await using var service = new TuneCaptureService(new Factory(),
            (_, _) => throw new InvalidDataException("The current car structure is unavailable."));
        var result = await service.RequestSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TuneCaptureStatus.Unavailable, result.Status);
        Assert.DoesNotContain("open world", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Car selection:", result.Details, StringComparison.Ordinal);
        Assert.Contains("The current car structure is unavailable.", result.Details, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData((int)TuneLayoutFailure.DescriptorProfile)]
    [InlineData((int)TuneLayoutFailure.CodeGuardHash)]
    [InlineData((int)TuneLayoutFailure.ProviderSlotMismatch)]
    public async Task AdmittedGameLayoutFailureHasSpecificReasonAndRemainsUnsupported(int code)
    {
        var reason = (TuneLayoutFailure)code;
        await using var service = new TuneCaptureService(new Factory(), (_, _) => throw new TuneLayoutException(reason));
        var result = await service.RequestSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TuneCaptureStatus.UnsupportedBuild, result.Status);
        Assert.Equal(TuneCaptureService.LayoutUnavailableMessage, result.Message);
        Assert.Contains("Layout check: " + reason, result.Details, StringComparison.Ordinal);
        Assert.Contains("Stage: ReadTune", result.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("Open status:", result.Details, StringComparison.Ordinal);
    }

    private sealed class Factory(NativeAssistProviderStatus status = NativeAssistProviderStatus.Ready) : INativeHudProcessMemoryFactory
    {
        public bool TryOpen(out INativeHudProcessMemory? memory, out NativeAssistProviderStatus observed)
        {
            observed = status;
            memory = status == NativeAssistProviderStatus.Ready ? new Memory() : null;
            return memory is not null;
        }
    }

    private sealed class Memory : INativeHudProcessMemory
    {
        public ulong ModuleBase => 0x140000000;
        public string SessionIdentity => "isolated-test-session";
        public bool TryReadByte(ulong address, out byte value) { value = 0; return false; }
        public bool TryReadUInt32(ulong address, out uint value) { value = 0; return false; }
        public bool TryReadUInt64(ulong address, out ulong value) { value = 0; return false; }
        public bool TryReadSingle(ulong address, out float value) { value = 0; return false; }
        public bool TryReadBytes(ulong address, Span<byte> destination) => false;
        public void Dispose() { }
    }
}
