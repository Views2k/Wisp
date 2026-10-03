using System.IO;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using Wisp.App.CrashDiagnostics;
using Wisp.App.DebugLogging;
using Wisp.App.Tunes;
using Xunit;

namespace Wisp.App.Tests;

public sealed class CrashReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CaptureNeverReadsMessageToStringDataOrPrivateSource()
    {
        var error = new PrivateException();
        error.Data["private"] = "PRIVATE_DATA";
        var report = CrashReport.Capture(error, CrashOrigin.UiDispatcher, true, Now);
        var text = report.Format();
        var json = JsonSerializer.Serialize(report);
        Assert.False(error.MessageRead); Assert.False(error.ToStringRead);
        Assert.DoesNotContain("PRIVATE", text + json, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(PrivateException), text + json, StringComparison.Ordinal);
        Assert.Contains("Exception 1: Exception", text, StringComparison.Ordinal);
        Assert.Contains("Process terminating: Yes", text, StringComparison.Ordinal);
        Assert.Contains("Product methods: Not available", text, StringComparison.Ordinal);
        Assert.Contains("UTC: 2026-10-03T12:00:00.0000000+00:00", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CapturesBoundedInnerCausesWithoutMessages()
    {
        Exception error = new IOException("PRIVATE_INNER");
        for (var i = 0; i < 20; i++) error = new AggregateException("PRIVATE_OUTER", error);
        var report = CrashReport.Capture(error, CrashOrigin.UnobservedTask, false, Now);
        Assert.Equal(CrashReport.MaximumExceptions, report.Exceptions.Length);
        Assert.All(report.Exceptions, item => Assert.Equal(nameof(AggregateException), item.Type));
        Assert.DoesNotContain("PRIVATE", report.Format(), StringComparison.Ordinal);
        Assert.Contains("Process terminating: No", report.Format(), StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureIdentifiesTheBinaryAndRuntimeWithoutMachineIdentity()
    {
        var report = CrashReport.Capture(new IOException(), CrashOrigin.UiDispatcher, true, Now);
        Assert.Equal(typeof(App).Assembly.ManifestModule.ModuleVersionId, report.ModuleVersionId);
        Assert.Equal(Environment.OSVersion.Version.ToString(), report.WindowsVersion);
        Assert.Equal(RuntimeInformation.ProcessArchitecture, report.ProcessArchitecture);
        var text = report.Format();
        Assert.Contains($"Assembly module: {report.ModuleVersionId:D}", text, StringComparison.Ordinal);
        Assert.Contains($"Windows: {report.WindowsVersion}", text, StringComparison.Ordinal);
        Assert.Contains($"Process architecture: {report.ProcessArchitecture}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void HistoricalBuildIdentitySurvivesPersistenceAndOldReportsRemainReadable()
    {
        using var directory = new TemporaryDirectory();
        var historical = Report() with
        {
            ModuleVersionId = new Guid("10000000-0000-0000-0000-000000000001"),
            PrivateBuildId = "clips-native-hdr-20261003-02",
            WindowsVersion = "10.0.26100.0",
            ProcessArchitecture = Architecture.X64
        };
        var store = new CrashReportStore(directory.Path);
        Assert.True(store.TrySave(historical));
        var loaded = new CrashReportStore(directory.Path).LatestPending()!;
        Assert.Equal(historical.ModuleVersionId, loaded.ModuleVersionId);
        Assert.Equal(historical.PrivateBuildId, loaded.PrivateBuildId);
        Assert.Equal(historical.WindowsVersion, loaded.WindowsVersion);
        Assert.Equal(historical.ProcessArchitecture, loaded.ProcessArchitecture);
        var legacy = JsonSerializer.SerializeToNode(Report())!.AsObject();
        legacy.Remove(nameof(CrashReport.ModuleVersionId));
        legacy.Remove(nameof(CrashReport.WindowsVersion));
        legacy.Remove(nameof(CrashReport.ProcessArchitecture));
        var legacyText = legacy.Deserialize<CrashReport>()!.Format();
        Assert.Contains("Assembly module: Not available", legacyText, StringComparison.Ordinal);
        Assert.Contains("Windows: Not available", legacyText, StringComparison.Ordinal);
        Assert.Contains("Process architecture: Not available", legacyText, StringComparison.Ordinal);
    }

    [Fact]
    public void CapturesRealProductMethodWithoutSourceFileOrArguments()
    {
        var error = Assert.Throws<TuneAssetValidationException>(() => TuneAssetCapture.Decode(
            new byte[4], new byte[1024], new byte[256], TestContext.Current.CancellationToken));
        var text = CrashReport.Capture(error, CrashOrigin.BackgroundThread, true, Now).Format();
        Assert.Contains("Wisp.App.Tunes.TuneAssetCapture.Decode", text, StringComparison.Ordinal);
        Assert.DoesNotContain(".cs:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("byte[", text, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DiskSymbolsAndMetadataAreValidatedAgainBeforeCopy()
    {
        var report = Report() with
        {
            WispVersion = "PRIVATE_VERSION",
            RuntimeVersion = "PRIVATE_RUNTIME",
            PrivateBuildId = "C:\\PRIVATE_PATH",
            ModuleVersionId = Guid.Empty,
            WindowsVersion = "C:\\PRIVATE_OS_PATH",
            ProcessArchitecture = (Architecture)int.MaxValue,
            Exceptions = [new("PRIVATE_TYPE", -1, ["Wisp.App.PRIVATE_TYPE.Method", "C:\\PRIVATE_PATH", "private-person@example.invalid"])]
        };
        var text = report.Format();
        Assert.DoesNotContain("PRIVATE", text, StringComparison.Ordinal);
        Assert.DoesNotContain("example.invalid", text, StringComparison.Ordinal);
        Assert.Contains("Wisp: Not available", text, StringComparison.Ordinal);
        Assert.Contains("CLR: Not available", text, StringComparison.Ordinal);
        Assert.Contains("Assembly module: Not available", text, StringComparison.Ordinal);
        Assert.Contains("Windows: Not available", text, StringComparison.Ordinal);
        Assert.Contains("Process architecture: Not available", text, StringComparison.Ordinal);
        Assert.Contains("Exception 1: Exception", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RealProductConstructorsRemainUsefulWithoutExposingParameters()
    {
        var symbol = CrashReportSymbols.Method(typeof(App).GetConstructor(Type.EmptyTypes));
        Assert.Equal("Wisp.App.App.ctor", symbol);
        Assert.True(CrashReportSymbols.IsKnownMethod(symbol));
        Assert.False(CrashReportSymbols.IsKnownMethod("Wisp.App.PRIVATE_NAME.ctor"));
        Assert.False(CrashReportSymbols.IsKnownMethod("Wisp.App.App.ctor(C:\\PRIVATE_PATH)"));
    }

    [Fact]
    public void StoreRetainsEightAndAcknowledgesOnlyDisplayedAndOlderReports()
    {
        using var directory = new TemporaryDirectory();
        var store = new CrashReportStore(directory.Path);
        var reports = Enumerable.Range(0, 10).Select(i => Report() with { Id = Guid.NewGuid(), TimeUtc = Now.AddSeconds(i) }).ToArray();
        foreach (var report in reports) Assert.True(store.TrySave(report));
        using var json = JsonDocument.Parse(File.ReadAllBytes(System.IO.Path.Combine(directory.Path, "reports.json")));
        Assert.Equal(CrashReportStore.MaximumReports, json.RootElement.GetProperty("Reports").GetArrayLength());
        Assert.Equal(reports[^1].Id, store.LatestPending()!.Id);
        Assert.True(store.AcknowledgeThrough(reports[^2].Id));
        Assert.Equal(reports[^1].Id, new CrashReportStore(directory.Path).LatestPending()!.Id);
        Assert.True(store.AcknowledgeThrough(reports[^1].Id));
        Assert.Null(new CrashReportStore(directory.Path).LatestPending());
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void FailedAtomicWriteKeepsCommittedReportAndDismissalPending()
    {
        using var directory = new TemporaryDirectory();
        var original = Report();
        Assert.True(new CrashReportStore(directory.Path).TrySave(original));
        var store = new CrashReportStore(directory.Path, (_, _) => throw new IOException("PRIVATE_STORAGE"));
        Assert.False(store.TrySave(Report() with { Id = Guid.NewGuid() }));
        Assert.False(store.AcknowledgeThrough(original.Id));
        Assert.Equal(original.Id, new CrashReportStore(directory.Path).LatestPending()!.Id);
    }

    [Fact]
    public void StartupRemovesOwnedTemporaryFilesWithoutTouchingOtherNamesOrDirectories()
    {
        using var directory = new TemporaryDirectory();
        var store = new CrashReportStore(directory.Path);
        var report = Report();
        Assert.True(store.TrySave(report));
        var owned = System.IO.Path.Combine(directory.Path, $"reports-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(owned, "partial report");
        var retained = new[] { "reports-note.tmp", "reports-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.tmp", "other.tmp" }
            .Select(name => System.IO.Path.Combine(directory.Path, name)).ToArray();
        foreach (var path in retained) File.WriteAllText(path, "unrelated");
        var folder = System.IO.Path.Combine(directory.Path, $"reports-{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(folder);
        var child = System.IO.Path.Combine(folder, "keep.txt"); File.WriteAllText(child, "keep");

        Assert.Equal(report.Id, store.LatestPending()!.Id);

        Assert.False(File.Exists(owned));
        Assert.All(retained, path => Assert.Equal("unrelated", File.ReadAllText(path)));
        Assert.Equal("keep", File.ReadAllText(child));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TemporaryCleanupIsBoundedAndCanFinishOnTheNextAttempt(bool startup)
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        for (var i = 0; i < CrashReportStore.MaximumCleanupEntries + 3; i++)
            File.WriteAllText(System.IO.Path.Combine(directory.Path, $"reports-{Guid.NewGuid():N}.tmp"), "partial report");
        var store = new CrashReportStore(directory.Path);

        if (startup) Assert.Null(store.LatestPending());
        else Assert.False(store.TryDeleteAll());

        Assert.Equal(3, Directory.EnumerateFiles(directory.Path).Count());
        Assert.True(store.TryDeleteAll());
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public void FatalSaveDoesNotScanOrDeleteAnEarlierTemporary()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var earlier = System.IO.Path.Combine(directory.Path, $"reports-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(earlier, "earlier partial report");
        var store = new CrashReportStore(directory.Path);

        Assert.True(store.TrySave(Report()));

        Assert.Equal("earlier partial report", File.ReadAllText(earlier));
        Assert.True(store.TryReadAll(out var reports)); Assert.Single(reports);
    }

    [Fact]
    public void BusyTemporaryMakesExplicitDeletionFailAndCanBeRetried()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var temporary = System.IO.Path.Combine(directory.Path, $"reports-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temporary, "partial report");
        var store = new CrashReportStore(directory.Path);
        using (var held = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(store.TryDeleteAll());
            Assert.True(held.Length > 0);
            Assert.True(File.Exists(temporary));
        }
        Assert.True(store.TryDeleteAll());
        Assert.False(File.Exists(temporary));
    }

    [Fact]
    public void DeletingAnOwnedHardlinkLeavesItsOtherNameAndContentsIntact()
    {
        using var directory = new TemporaryDirectory();
        var storeDirectory = System.IO.Path.Combine(directory.Path, "reports");
        Directory.CreateDirectory(storeDirectory);
        var outside = System.IO.Path.Combine(directory.Path, "keep.txt");
        File.WriteAllText(outside, "keep outside contents");
        var temporary = System.IO.Path.Combine(storeDirectory, $"reports-{Guid.NewGuid():N}.tmp");
        Assert.True(CreateHardLinkW(temporary, outside, IntPtr.Zero), $"Hardlink creation failed: {Marshal.GetLastWin32Error()}");

        Assert.True(new CrashReportStore(storeDirectory).TryDeleteAll());

        Assert.False(File.Exists(temporary));
        Assert.Equal("keep outside contents", File.ReadAllText(outside));
    }

    [Fact]
    public void CorruptOrOversizedStoreDoesNotPreventFutureRecording()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var path = System.IO.Path.Combine(directory.Path, "reports.json");
        File.WriteAllText(path, "PRIVATE_INVALID_JSON");
        var store = new CrashReportStore(directory.Path);
        Assert.Null(store.LatestPending());
        Assert.True(store.TrySave(Report()));
        File.WriteAllBytes(path, new byte[CrashReportStore.MaximumStoreBytes + 1]);
        Assert.Null(store.LatestPending());
        var newest = Report() with { Id = Guid.NewGuid() };
        Assert.True(store.TrySave(newest));
        Assert.Equal(newest.Id, store.LatestPending()!.Id);
    }

    [Fact]
    public void InvalidStoreDirectoryIsANonThrowingLoggingFailure()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var blocked = System.IO.Path.Combine(directory.Path, "file");
        File.WriteAllText(blocked, "test");
        var store = new CrashReportStore(blocked);
        Assert.False(store.TrySave(Report()));
        Assert.Null(store.LatestPending());
    }

    [Fact]
    public void StorePersistsOnlySanitizedSymbols()
    {
        using var directory = new TemporaryDirectory();
        var report = Report() with { Exceptions = [new("PRIVATE_NAME", 0, ["C:\\PRIVATE_PATH", "PRIVATE_METHOD"])] };
        var store = new CrashReportStore(directory.Path);
        Assert.True(store.TrySave(report));
        var json = File.ReadAllText(System.IO.Path.Combine(directory.Path, "reports.json"));
        Assert.DoesNotContain("PRIVATE", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE", store.LatestPending()!.Format(), StringComparison.Ordinal);
    }

    [Fact]
    public void FullHealthHistoryFitsBudgetAndSurvivesPersistence()
    {
        using var directory = new TemporaryDirectory();
        var sample = new HealthContextSample
        {
            TimestampUtc = Now,
            CollectionGapMs = 1e15,
            IncomingDatagramsPerSecond = 1e15,
            ProcessedPacketsPerSecond = 1e15,
            AcceptedPackets = long.MaxValue,
            RejectedPackets = long.MaxValue,
            PacketAgeMs = 1e15,
            ListenerRunning = true,
            ListenerError = true,
            GameActive = true,
            GameTimestampAdvancing = true,
            OverlayExpectedVisible = true,
            UiHeartbeatAgeMs = 1e15,
            DispatcherDelayMs = 1e15,
            DispatcherProbePending = true,
            CompositionCallbacksPerSecond = 1e15,
            CompositionCallbackAgeMs = 1e15,
            CompositionMaximumGapMs = 1e15,
            NativeExpected = true,
            NativeReadAttempts = long.MaxValue,
            NativeReadFailures = long.MaxValue,
            NativeAgeMs = 1e15,
            CpuPercent = 100,
            WorkingSetBytes = long.MaxValue,
            ManagedHeapBytes = long.MaxValue,
            Gen2Collections = int.MaxValue,
            CollectorFailures = long.MaxValue,
            Renderer = new(long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue,
                long.MaxValue, int.MinValue, 1e15, 1e15, 1e15)
        };
        var samples = Enumerable.Range(0, HealthContextRecorder.SampleCapacity)
            .Select(i => sample with { TimestampUtc = Now.AddSeconds(i * 2) }).ToImmutableArray();
        var events = Enumerable.Range(0, HealthContextRecorder.BreadcrumbCapacity)
            .Select(i => new HealthBreadcrumb(Now.AddSeconds(i), HealthEventCode.PlayerChanged, int.MinValue,
                Wisp.App.Clips.ClipRecorderState.WaitingForGame, HealthPlayerState.Playing))
            .ToImmutableArray();
        var symbols = typeof(App).Assembly.GetTypes().SelectMany(type => type.GetMethods(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance))
            .Select(CrashReportSymbols.Method).OfType<string>().Distinct(StringComparer.Ordinal)
            .OrderByDescending(symbol => symbol.Length).Take(CrashReport.MaximumMethods).ToArray();
        Assert.Equal(CrashReport.MaximumMethods, symbols.Length);
        var report = Report() with
        {
            ModuleVersionId = Guid.NewGuid(),
            WindowsVersion = "2147483647.2147483647.2147483647",
            ProcessArchitecture = Architecture.Arm64,
            Context = new(Now, samples, events, long.MaxValue),
            Exceptions = Enumerable.Range(0, CrashReport.MaximumExceptions)
                .Select(_ => new CrashExceptionInfo("TuneAssetStreamValidationException", int.MinValue, symbols)).ToArray()
        };
        Assert.True(HealthContextRecorder.IsValidSnapshot(report.Context));
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(report).Length < CrashReportStore.MaximumReportBytes);
        var store = new CrashReportStore(directory.Path);
        Assert.True(store.TrySave(report));
        var loaded = new CrashReportStore(directory.Path).LatestPending()!;
        Assert.Equal(HealthContextRecorder.SampleCapacity, loaded.Context!.Samples.Length);
        Assert.Equal(HealthContextRecorder.BreadcrumbCapacity, loaded.Context.Breadcrumbs.Length);
        Assert.Contains("Composition callbacks and renderer submissions are not displayed-frame measurements.", loaded.Format(), StringComparison.Ordinal);
        Assert.Contains("RendererQueuedToSubmitAgeMs", loaded.Format(), StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidHealthContextIsExcludedWithoutLosingTheError()
    {
        var report = Report() with { Context = new(Now, [new HealthContextSample { TimestampUtc = Now, CpuPercent = double.NaN }], []) };
        var safe = report.Sanitize();
        Assert.NotNull(safe); Assert.Null(safe.Context); Assert.Single(safe.Exceptions);
        Assert.Contains("Recent health context: Not available", safe.Format(), StringComparison.Ordinal);
    }

    internal static CrashReport Report(bool terminating = true) => new(Guid.NewGuid(), Now, "2.6.1", "clips-test-20261003",
        "8.0.0", terminating ? CrashOrigin.UiDispatcher : CrashOrigin.UnobservedTask, terminating,
        [new(nameof(IOException), unchecked((int)0x80004005), [])]);

    private sealed class PrivateException : Exception
    {
        internal bool MessageRead, ToStringRead;
        public override string Message { get { MessageRead = true; return "PRIVATE_MESSAGE"; } }
        public override string ToString() { ToStringRead = true; return "PRIVATE_STACK"; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string fileName, string existingFileName, IntPtr securityAttributes);

    private sealed class TemporaryDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WispCrashReportTests", Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
