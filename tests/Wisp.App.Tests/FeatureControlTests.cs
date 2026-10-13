using System.IO;
using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using Wisp.App.Supplementary;
using Xunit;

namespace Wisp.App.Tests;

public sealed class FeatureControlTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RealPollingTimerDeliversServiceSignedPauseAndRestoreWithinOneMinuteEach()
    {
        using var fixture = new Fixture();
        using var stream = typeof(FeatureControlTests).Assembly.GetManifestResourceStream("Wisp.FeatureControls.ServiceSchema1")!;
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: TestContext.Current.CancellationToken);
        var root = json.RootElement;
        var keys = new Dictionary<string, byte[]> { [root.GetProperty("keyId").GetString()!] = Convert.FromBase64String(root.GetProperty("publicKey").GetString()!) };
        var clock = new RealTimerClock(DateTimeOffset.Parse(root.GetProperty("at").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        var store = new FeatureControlStore(new(keys), fixture.Directory);
        var handler = new ServicePolicyHandler([]);
        using var transport = new SupplementaryHttpTransport(new Uri("https://example.invalid/"), clock, handler);
        await using var controller = new AppController(new AppSettings(), _ => { }, new NoStartupRegistration());
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var application = new FeatureControlApplication(controller.ApplyFeatureControlsAsync);
        using var client = new FeatureControlClient(new(new Uri("https://example.invalid/")), store, "2.6.8", "stable", state =>
        {
            application.Apply(state);
            if (state.Revision == 1) paused.TrySetResult();
            if (state.Revision == 2) restored.TrySetResult();
        }, clock, transport, () => 1);
        client.Start();
        try
        {
            await handler.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var requested = System.Diagnostics.Stopwatch.GetTimestamp();
            Volatile.Write(ref handler.Body, System.Text.Encoding.UTF8.GetBytes(root.GetProperty("paused").GetRawText()));
            await paused.Task.WaitAsync(TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
            var pauseElapsed = System.Diagnostics.Stopwatch.GetElapsedTime(requested);
            Assert.False(controller.FeatureAllowed("run-recording"));
            Assert.False(controller.FeatureAllowed("tune-read"));
            requested = System.Diagnostics.Stopwatch.GetTimestamp();
            Volatile.Write(ref handler.Body, System.Text.Encoding.UTF8.GetBytes(root.GetProperty("restored").GetRawText()));
            await restored.Task.WaitAsync(TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
            var restoreElapsed = System.Diagnostics.Stopwatch.GetElapsedTime(requested);
            Assert.True(controller.FeatureAllowed("run-recording"));
            Assert.True(controller.FeatureAllowed("tune-read"));
            Assert.True(pauseElapsed < TimeSpan.FromMinutes(1));
            Assert.True(restoreElapsed < TimeSpan.FromMinutes(1));
            Assert.Equal(3, handler.Calls);
            output.WriteLine($"Real timer with service-signed policies and HTTP handler fixture: pause {pauseElapsed.TotalSeconds:F3}s; restore {restoreElapsed.TotalSeconds:F3}s. External ingress and installed-client delivery are separate checks.");
        }
        finally
        {
            client.Dispose();
            await client.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await application.LastFinalization;
        }
    }

    private sealed class RealTimerClock(DateTimeOffset initial) : TimeProvider
    {
        private readonly long _started = global::System.Diagnostics.Stopwatch.GetTimestamp();
        public override DateTimeOffset GetUtcNow() => initial + global::System.Diagnostics.Stopwatch.GetElapsedTime(_started);
    }

    [Fact]
    public async Task PresentationControlsKeepPreferencesAndRejectBackgroundReactivationWhilePaused()
    {
        var settings = new AppSettings
        {
            AnimatedBackground = true,
            BackgroundParticlesEnabled = true,
            AccelerationShiftCueEnabled = true,
            TractionCueEnabled = true
        };
        await using var controller = new AppController(settings, _ => { }, new NoStartupRegistration());
        var before = JsonSerializer.Serialize(settings);
        controller.ViewModel.IsTractionCueActive = true;
        await controller.ApplyFeatureControlsAsync(new(1, ImmutableDictionary<string, string>.Empty
            .Add("background-animation", "Animation paused.").Add("traction-cue", "Traction cue paused.")
            .Add("shift-guidance", "Shift guidance paused.")));
        Assert.False(controller.ViewModel.EffectiveBackgroundAnimation);
        controller.ViewModel.AnimatedBackground = false;
        controller.ViewModel.AnimatedBackground = true;
        Assert.False(controller.ViewModel.EffectiveBackgroundAnimation);
        Assert.False(controller.ViewModel.IsTractionCueActive);
        Assert.False(controller.ShiftCueRuntimeEnabled);
        Assert.False(controller.ViewModel.NativeGaugeFrame.ShiftCue.Enabled);
        Assert.False(controller.ViewModel.CanStartShiftCalibration);
        Assert.Equal("Shift guidance paused.", controller.ViewModel.ShiftCueStatus);
        Assert.Equal(before, JsonSerializer.Serialize(settings));
        await controller.ApplyFeatureControlsAsync(new(2, ImmutableDictionary<string, string>.Empty));
        Assert.True(controller.ViewModel.EffectiveBackgroundAnimation);
        Assert.True(controller.ShiftCueRuntimeEnabled);
        Assert.False(controller.ViewModel.NativeGaugeFrame.ShiftCue.Enabled);
        Assert.False(controller.ViewModel.IsTractionCueActive);
        Assert.Equal(before, JsonSerializer.Serialize(settings));
    }

    [Fact]
    public async Task TireCalibrationPauseKeepsSavedGeometryAndBlocksResetUntilRestored()
    {
        var settings = new AppSettings();
        await using var controller = new AppController(settings, _ => { }, new NoStartupRegistration());
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var estimator = (Wisp.Core.RollingRadiusEstimator)typeof(AppController).GetField("_calibration", flags)!.GetValue(controller)!;
        var state = RunTestData.State() with
        {
            GroundSpeedMetersPerSecond = 30,
            WheelRotationRadiansPerSecond = new(100, 100, 100, 100),
            NormalizedSuspensionTravel = new(.5f, .5f, .5f, .5f)
        };
        for (var i = 0; i < 12; i++) estimator.Observe(state);
        Assert.True(estimator.Get(state.CarOrdinal).IsTrusted);
        typeof(AppController).GetField("_lastProcessedState", flags)!.SetValue(controller, state);
        await controller.ApplyFeatureControlsAsync(new(1, ImmutableDictionary<string, string>.Empty.Add("tire-calibration", "Learning is paused.")));
        Assert.False(controller.RelearnCurrentTires());
        Assert.True(estimator.Get(state.CarOrdinal).IsTrusted);
        controller.ViewModel.Update(state, new Wisp.Core.IndicatedSpeed(0, 0, true, false, "Rear"), estimator.Observe(state),
            default(Wisp.Telemetry.ReceiverStatistics), TimeSpan.Zero, Wisp.Core.SpeedUnit.MilesPerHour, 60,
            refreshDiagnostics: true, updateGForce: false);
        Assert.False(controller.ViewModel.CanRelearnCurrentTires);
        Assert.Equal("Learning is paused.", controller.ViewModel.TireCalibrationActionHint);
        await controller.ApplyFeatureControlsAsync(new(2, ImmutableDictionary<string, string>.Empty));
        Assert.True(controller.ViewModel.CanRelearnCurrentTires);
        Assert.True(controller.RelearnCurrentTires());
        Assert.False(estimator.Get(state.CarOrdinal).IsTrusted);
    }

    [Fact]
    public async Task ControllerRetriesTheSameRevisionAfterAnInterruptedApplication()
    {
        await using var controller = new AppController(new AppSettings(), _ => { }, new NoStartupRegistration());
        var attempts = 0;
        controller.FeatureControlsChanged += (_, _) => { if (++attempts == 1) throw new InvalidOperationException("Interrupted observer."); };
        var state = new FeatureControlState(1, ImmutableDictionary<string, string>.Empty.Add("hud", "Paused."));
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ApplyFeatureControlsAsync(state));
        Assert.False(controller.FeatureAllowed("hud"));
        await controller.ApplyFeatureControlsAsync(state);
        Assert.Equal(2, attempts);
        await controller.ApplyFeatureControlsAsync(new(0, ImmutableDictionary<string, string>.Empty));
        Assert.Equal(2, attempts);
        Assert.False(controller.FeatureAllowed("hud"));
    }

    [Theory]
    [InlineData("boost-gauge")]
    [InlineData("g-force-gauge")]
    [InlineData("tire-temperature-gauge")]
    [InlineData("power-gauge")]
    [InlineData("torque-gauge")]
    [InlineData("drift-hud")]
    [InlineData("lap-delta")]
    [InlineData("lap-map")]
    public async Task OverlayControlsPreservePreferencesAndRestoreTheExactRequestedSubset(string feature)
    {
        var settings = new AppSettings
        {
            GForceEnabled = true,
            BoostGaugeEnabled = true,
            TireTemperatureGaugeEnabled = true,
            PowerGaugeEnabled = true,
            TorqueGaugeEnabled = true,
            DriftGaugeEnabled = true,
            LapDeltaEnabled = true,
            LapMapEnabled = true
        };
        await using var controller = new AppController(settings, _ => throw new InvalidOperationException("Preferences changed."), new NoStartupRegistration());
        var before = JsonSerializer.Serialize(settings);
        var disabled = ImmutableDictionary<string, string>.Empty.Add(feature, "Temporarily paused.");
        await controller.ApplyFeatureControlsAsync(new(1, disabled));
        Assert.False(controller.HudFeatureAllowed(feature));
        foreach (var other in new[] { "boost-gauge", "g-force-gauge", "tire-temperature-gauge", "power-gauge", "torque-gauge", "drift-hud", "lap-delta", "lap-map" }.Where(x => x != feature))
            Assert.True(controller.HudFeatureAllowed(other));
        await controller.ApplyFeatureControlsAsync(new(2, disabled.Add("hud", "All overlays paused.")));
        Assert.False(controller.IsAttachedGForceMeterEnabled);
        Assert.False(controller.IsStandaloneGForceWindowEnabled);
        Assert.False(controller.IsDetachedBoostGaugeEnabled);
        Assert.False(controller.IsDetachedTireTemperatureGaugeEnabled);
        Assert.False(controller.IsDetachedPowerGaugeEnabled);
        Assert.False(controller.IsDetachedTorqueGaugeEnabled);
        await controller.ApplyFeatureControlsAsync(new(3, disabled));
        Assert.False(controller.HudFeatureAllowed(feature));
        await controller.ApplyFeatureControlsAsync(new(4, ImmutableDictionary<string, string>.Empty));
        Assert.True(controller.HudFeatureAllowed(feature));
        Assert.Equal(before, JsonSerializer.Serialize(settings));
    }

    [Fact]
    public async Task SlowFinalizationDoesNotDelayTheNextSignedPolicyOrRestore()
    {
        using var f = new Fixture();
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new FeatureControlGate();
        var application = new FeatureControlApplication(state =>
        {
            gate.Apply(state);
            return state.Disabled.Count > 0 ? finish.Task : Task.CompletedTask;
        });
        var transport = new Transport { Response = new(SupplementaryRequestStatus.Success, f.Sign(f.Payload())) };
        using var client = f.Client(transport, application.Apply);
        try
        {
            await client.RefreshAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(gate.IsAllowed("clip-recording"));
            Assert.Equal(1, application.PendingFinalizations);
            var restored = f.Payload(2);
            restored["rules"] = new JsonArray();
            transport.Response = new(SupplementaryRequestStatus.Success, f.Sign(restored));
            f.Clock.Now += FeatureControlClient.MaximumInterval;
            await client.RefreshAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(2, gate.State.Revision);
            Assert.True(gate.IsAllowed("clip-recording"));
            Assert.Equal(1, application.PendingFinalizations);
            Assert.False(finish.Task.IsCompleted);
        }
        finally { finish.TrySetResult(); }
    }

    [Fact]
    public async Task FinalizationFailureRemainsObservableWithoutBlockingNewControls()
    {
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var application = new FeatureControlApplication(_ => finish.Task);
        application.Apply(new(4, ImmutableDictionary<string, string>.Empty));
        finish.SetException(new IOException("Synthetic save failure."));
        await application.LastFinalization;
        Assert.Equal(4, application.FailedRevision);
        Assert.Equal(0, application.PendingFinalizations);
    }

    [Fact]
    public async Task ActualServiceSignedBytesPassTransportCacheAndRecordingConsumers()
    {
        using var fixture = new Fixture();
        using var stream = typeof(FeatureControlTests).Assembly.GetManifestResourceStream("Wisp.FeatureControls.ServiceSchema1")!;
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: TestContext.Current.CancellationToken);
        var root = json.RootElement;
        var keys = new Dictionary<string, byte[]> { [root.GetProperty("keyId").GetString()!] = Convert.FromBase64String(root.GetProperty("publicKey").GetString()!) };
        Assert.Equal(ControlledFeatures.All.Order(), root.GetProperty("catalog").EnumerateArray().Select(x => x.GetString()!).Order());
        var clock = new Clock(DateTimeOffset.Parse(root.GetProperty("at").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        var store = new FeatureControlStore(new(keys), fixture.Directory);
        var handler = new ServicePolicyHandler(System.Text.Encoding.UTF8.GetBytes(root.GetProperty("paused").GetRawText()));
        using var transport = new SupplementaryHttpTransport(new Uri("https://example.invalid/"), clock, handler);
        var settings = new AppSettings();
        settings.Clips.Enabled = true;
        await using var controller = new AppController(settings, _ => throw new InvalidOperationException("Control policy must not save preferences."), new NoStartupRegistration());
        var before = JsonSerializer.Serialize(settings);
        using var client = new FeatureControlClient(new(new Uri("https://example.invalid/")), store, "2.6.8", "stable",
            state => controller.ApplyFeatureControlsAsync(state).GetAwaiter().GetResult(), clock, transport);
        await client.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, store.HighestRevision);
        Assert.False(controller.FeatureAllowed("tune-read"));
        Assert.False(controller.Clips.CanToggle);
        Assert.False(controller.Runs.CanToggleRecording);
        Assert.Equal(before, JsonSerializer.Serialize(settings));
        handler.Body = System.Text.Encoding.UTF8.GetBytes(root.GetProperty("restored").GetRawText());
        await client.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, store.HighestRevision);
        Assert.True(controller.FeatureAllowed("tune-read"));
        Assert.False(controller.Clips.ClippingEnabled);
        Assert.Equal(before, JsonSerializer.Serialize(settings));
        Assert.Equal(2, handler.Calls);
    }

    private sealed class ServicePolicyHandler(byte[] body) : System.Net.Http.HttpMessageHandler
    {
        internal byte[] Body = body;
        internal int Calls;
        internal readonly TaskCompletionSource FirstRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(System.Net.Http.HttpMethod.Get, request.Method);
            Assert.Equal("/api/v1/feature-controls", request.RequestUri!.PathAndQuery);
            Assert.Null(request.Content);
            Interlocked.Increment(ref Calls);
            var response = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new System.Net.Http.ByteArrayContent(Volatile.Read(ref Body)) };
            response.Content.Headers.ContentType = new("application/json");
            response.Headers.CacheControl = new() { NoStore = true };
            FirstRequest.TrySetResult();
            return Task.FromResult(response);
        }
    }

    [Fact]
    public void ControllerAppliesAndRestoresRecordingControlsWithoutChangingPreferences() => OnDispatcher(async () =>
    {
        var settings = new AppSettings();
        settings.Clips.Enabled = true;
        var writes = 0;
        await using var controller = new AppController(settings, _ => writes++, new NoStartupRegistration());
        var before = JsonSerializer.Serialize(settings);
        var state = new FeatureControlState(1, new[] { "run-recording", "clip-recording", "tune-read", "support", "news" }
            .ToImmutableDictionary(key => key, _ => "Temporarily paused."));
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var recorder = (Clips.ClipRecorderService)typeof(AppController).GetField("_clipRecorder", flags)!.GetValue(controller)!;
        var lifecycle = (SemaphoreSlim)typeof(Clips.ClipRecorderService).GetField("_lifecycle", flags)!.GetValue(recorder)!;
        await lifecycle.WaitAsync(TestContext.Current.CancellationToken);
        var paused = controller.ApplyFeatureControlsAsync(state);
        Assert.False(paused.IsCompleted);
        await Task.Run(() => lifecycle.Release(), TestContext.Current.CancellationToken);
        await paused;
        await Dispatcher.Yield(DispatcherPriority.DataBind);
        Assert.False(recorder.Snapshot.Enabled);
        Assert.False(controller.Clips.ClippingEnabled);
        Assert.False(controller.FeatureAllowed("run-recording"));
        Assert.False(controller.Clips.CanToggle);
        Assert.False(controller.Runs.CanToggleRecording);
        await controller.Clips.RestoreEnabledPreferenceAsync();
        Assert.True(settings.Clips.Enabled);
        Assert.Equal(before, JsonSerializer.Serialize(settings));
        Assert.Equal(0, writes);
        await controller.ApplyFeatureControlsAsync(new(2, ImmutableDictionary<string, string>.Empty));
        Assert.True(controller.FeatureAllowed("run-recording"));
        Assert.True(controller.FeatureAllowed("support"));
        Assert.False(controller.Clips.ClippingEnabled);
        Assert.Equal(before, JsonSerializer.Serialize(settings));
        await controller.ApplyFeatureControlsAsync(state);
        Assert.Equal(2, controller.FeatureControls.State.Revision);
        Assert.True(controller.FeatureAllowed("run-recording"));
    });

    private static void OnDispatcher(Func<Task> test)
    {
        Exception? failure = null;
        using var finished = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = dispatcher.InvokeAsync(async () =>
            {
                try { await test(); }
                catch (Exception error) { failure = error; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); finished.Set(); }
            });
            Dispatcher.Run();
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(finished.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken), "Feature control test exceeded its bounded dispatcher deadline.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void GateRejectsUnknownControlsAndAllowsExpiryAtTheCurrentRevision()
    {
        var gate = new FeatureControlGate();
        Assert.False(gate.IsAllowed("execute"));
        Assert.Throws<ArgumentException>(() => gate.Apply(new(1, ImmutableDictionary<string, string>.Empty.Add("execute", "x"))));
        Assert.True(gate.Apply(new(3, ImmutableDictionary<string, string>.Empty.Add("hud", "Paused"))));
        Assert.False(gate.IsAllowed("hud"));
        Assert.True(gate.Apply(new(3, ImmutableDictionary<string, string>.Empty)));
        Assert.True(gate.IsAllowed("hud"));
        Assert.False(gate.Apply(new(2, ImmutableDictionary<string, string>.Empty.Add("hud", "Old"))));
    }

    private sealed class NoStartupRegistration : IStartupRegistrationService
    {
        public void Apply(bool startWithWindows, bool startWithForza) { }
    }

    [Fact]
    public void SignedPolicyDisablesOnlySelectedFeaturesForItsAudienceAndLifetime()
    {
        using var f = new Fixture();
        var payload = f.Payload();
        var rule = payload["rules"]![0]!;
        rule["features"] = new JsonArray("clip-recording", "tune-read");
        rule["minVersion"] = "2.6.8";
        rule["maxVersion"] = "2.6.9";
        var policy = f.Verifier.Verify(f.Sign(payload), f.Now).Policy;
        Assert.Equal(2, policy.Disabled("2.6.8", "stable", f.Now).Count);
        Assert.Empty(policy.Disabled("2.6.7", "stable", f.Now));
        Assert.Empty(policy.Disabled("2.6.10", "stable", f.Now));
        Assert.Empty(policy.Disabled("2.6.8", "private", f.Now));
        Assert.Empty(policy.Disabled("invalid", "stable", f.Now));
        Assert.Empty(policy.Disabled("2.6.8", "stable", f.Now.AddHours(1)));
        Assert.DoesNotContain("updates", policy.Disabled("2.6.8", "stable", f.Now).Keys);
    }

    [Theory]
    [InlineData("purpose")]
    [InlineData("unknown-field")]
    [InlineData("unknown-feature")]
    [InlineData("duplicate-feature")]
    [InlineData("duplicate-rule")]
    [InlineData("empty-features")]
    [InlineData("too-many-rules")]
    [InlineData("invalid-range")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("long-lifetime")]
    [InlineData("oversized-reason")]
    [InlineData("rule-exceeds-policy")]
    public void MalformedSignedPoliciesAreRejected(string fault)
    {
        using var f = new Fixture();
        var p = f.Payload();
        var rule = p["rules"]![0]!;
        switch (fault)
        {
            case "purpose": p["purpose"] = "wisp-supplementary-content"; break;
            case "unknown-field": p["execute"] = "invalid"; break;
            case "unknown-feature": rule["features"] = new JsonArray("arbitrary-command"); break;
            case "duplicate-feature": rule["features"] = new JsonArray("clip-recording", "clip-recording"); break;
            case "duplicate-rule": p["rules"]!.AsArray().Add(rule.DeepClone()); break;
            case "empty-features": rule["features"] = new JsonArray(); break;
            case "too-many-rules": p["rules"] = new JsonArray(Enumerable.Range(0, 33).Select(_ => rule.DeepClone()).ToArray()); break;
            case "invalid-range": rule["minVersion"] = "3.0.0"; rule["maxVersion"] = "2.6.8"; break;
            case "expired": p["expiresAt"] = Stamp(f.Now); break;
            case "future": p["issuedAt"] = Stamp(f.Now.AddMinutes(6)); break;
            case "long-lifetime": p["expiresAt"] = Stamp(f.Now.AddDays(8)); break;
            case "oversized-reason": rule["reason"] = new string('x', 301); break;
            case "rule-exceeds-policy": rule["expiresAt"] = Stamp(f.Now.AddHours(3)); break;
        }
        Assert.Throws<SupplementaryValidationException>(() => f.Verifier.Verify(f.Sign(p), f.Now));
    }

    [Fact]
    public void InvalidSignatureAndContentPurposeCannotAuthorizeControls()
    {
        using var f = new Fixture();
        var outer = JsonNode.Parse(f.Sign(f.Payload()))!;
        outer["signature"] = Convert.ToBase64String(new byte[64]);
        Assert.Throws<SupplementaryValidationException>(() => f.Verifier.Verify(JsonSerializer.SerializeToUtf8Bytes(outer), f.Now));
        var contentVerifier = new SupplementaryContentVerifier(f.Keys);
        Assert.Throws<SupplementaryValidationException>(() => contentVerifier.Verify(f.Sign(f.Payload()), f.Now));
    }

    [Fact]
    public void StorePersistsRevisionAndRejectsReplayAfterExpiryOrRestart()
    {
        using var f = new Fixture();
        var store = f.Store();
        var bytes = f.Sign(f.Payload(5));
        Assert.Equal(SupplementaryContentAcceptance.Accepted, store.Accept(bytes, f.Now));
        Assert.Equal(SupplementaryContentAcceptance.Unchanged, store.Accept(bytes, f.Now));
        var restarted = f.Store();
        Assert.Single(restarted.Current(f.Now).Disabled("2.6.8", "stable", f.Now));
        Assert.Equal(5, restarted.HighestRevision);
        Assert.Equal(SupplementaryContentAcceptance.Rejected, restarted.Accept(f.Sign(f.Payload(4)), f.Now));
        var conflict = f.Payload(5);
        conflict["rules"] = new JsonArray();
        Assert.Equal(SupplementaryContentAcceptance.Rejected, restarted.Accept(f.Sign(conflict), f.Now));
        Assert.Empty(restarted.Current(f.Now.AddHours(3)).Rules);
        Assert.Equal(5, f.Store().HighestRevision);
        var restore = f.Payload(6);
        restore["rules"] = new JsonArray();
        Assert.Equal(SupplementaryContentAcceptance.Accepted, restarted.Accept(f.Sign(restore), f.Now));
        Assert.Empty(f.Store().Current(f.Now).Rules);
    }

    [Fact]
    public void DiskRegressionAndCorruptionDoNotUndoAnAcceptedDisable()
    {
        using var f = new Fixture();
        var store = f.Store();
        Assert.Equal(SupplementaryContentAcceptance.Accepted, store.Accept(f.Sign(f.Payload(5)), f.Now));
        File.Delete(Path.Combine(f.Directory, "controls.json"));
        Assert.Equal(SupplementaryContentAcceptance.CacheUnavailable, store.Accept(f.Sign(f.Payload(6)), f.Now));
        Assert.False(store.IsHealthy);
        Assert.Single(store.Current(f.Now).Rules);
        File.WriteAllText(Path.Combine(f.Directory, "controls.json"), "invalid");
        var restarted = f.Store();
        Assert.False(restarted.IsHealthy);
        Assert.Equal(SupplementaryContentAcceptance.CacheUnavailable, restarted.Accept(f.Sign(f.Payload(6)), f.Now));
    }

    [Fact]
    public void CacheLeaseFailurePreservesCurrentPolicyAndCanRecover()
    {
        using var f = new Fixture();
        var store = f.Store();
        Assert.Equal(SupplementaryContentAcceptance.Accepted, store.Accept(f.Sign(f.Payload()), f.Now));
        using (var lease = new FileStream(Path.Combine(f.Directory, "controls.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal(SupplementaryContentAcceptance.CacheUnavailable, store.Accept(f.Sign(f.Payload(2)), f.Now));
        Assert.Equal(1, store.HighestRevision);
        Assert.Equal(SupplementaryContentAcceptance.Accepted, store.Accept(f.Sign(f.Payload(2)), f.Now));
    }

    [Fact]
    public async Task EmptyInvalidAndUnavailableResponsesPreservePolicyWhileExpiryRestoresAvailability()
    {
        using var f = new Fixture();
        var states = new List<FeatureControlState>();
        var transport = new Transport { Response = new(SupplementaryRequestStatus.Success, f.Sign(f.Payload())) };
        using var client = f.Client(transport, states.Add);
        await client.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Single(states[^1].Disabled);
        foreach (var response in new[] { new SupplementaryResponse(SupplementaryRequestStatus.Success, []),
            new(SupplementaryRequestStatus.Success, [1, 2]), new(SupplementaryRequestStatus.Unavailable, []) })
        {
            transport.Response = response;
            await client.RefreshAsync(TestContext.Current.CancellationToken);
            Assert.Single(client.State.Disabled);
        }
        f.Clock.Now += TimeSpan.FromHours(2);
        await client.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Empty(states[^1].Disabled);
        Assert.Equal(1, states[^1].Revision);
    }

    [Fact]
    public async Task IndependentWorkerAppliesDisableAndRestoreWithinOneMinute()
    {
        using var f = new Fixture();
        var times = new List<(DateTimeOffset At, FeatureControlState State)>();
        var waits = new List<TimeSpan>();
        var transport = new Transport { Response = new(SupplementaryRequestStatus.Success, f.Sign(f.Payload())) };
        var stop = new CancellationTokenSource();
        var requested = f.Clock.Now;
        using var client = f.Client(transport, state => times.Add((f.Clock.Now, state)), async (duration, token) =>
        {
            waits.Add(duration);
            if (waits.Count == 2) { stop.Cancel(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return; }
            f.Clock.Now += duration;
            var restore = f.Payload(2);
            restore["rules"] = new JsonArray();
            transport.Response = new(SupplementaryRequestStatus.Success, f.Sign(restore));
        });
        using var registration = stop.Token.Register(client.Dispose);
        client.Start();
        await client.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Contains(times, item => item.State.Revision == 1 && item.State.Disabled.Count == 1 && item.At - requested < TimeSpan.FromMinutes(1));
        Assert.Contains(times, item => item.State.Revision == 2 && item.State.Disabled.Count == 0 && item.At - requested < TimeSpan.FromMinutes(1));
        Assert.All(waits, interval => Assert.InRange(interval, FeatureControlClient.MinimumInterval, FeatureControlClient.MaximumInterval));
        Assert.Equal(2, transport.Calls);
    }

    [Fact]
    public async Task FailedConsumerIsRetriedAndConcurrentRefreshCannotOverlap()
    {
        using var f = new Fixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new Transport
        {
            Response = new(SupplementaryRequestStatus.Success, f.Sign(f.Payload())),
            Before = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); },
        };
        var deliveries = 0;
        using var client = f.Client(transport, _ => { if (++deliveries == 1) throw new InvalidOperationException(); });
        var first = client.RefreshAsync(TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await client.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, transport.Calls);
        release.SetResult();
        await first;
        await client.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, deliveries);
        Assert.Single(client.State.Disabled);
    }

    [Fact]
    public async Task RequestDeadlineCancelsTheActualInFlightRequestWithoutAnOverlappingRetry()
    {
        using var f = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        var transport = new Transport
        {
            Before = async token =>
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { cancelled = true; throw; }
            },
        };
        using var client = f.Client(transport, _ => { });
        var request = client.RefreshAsync(TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(FeatureControlClient.RequestDeadline, f.Clock.Timer!.DueTime);
        f.Clock.Timer.Fire();
        await request.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(cancelled);
        Assert.Equal(1, transport.Calls);
        Assert.True(FeatureControlClient.MaximumInterval + 2 * FeatureControlClient.RequestDeadline < TimeSpan.FromMinutes(1));
    }

    private static string Stamp(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now = now;
        internal DeadlineTimer? Timer;
        public override DateTimeOffset GetUtcNow() => Now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            Timer = new(callback, state, dueTime);
    }

    private sealed class DeadlineTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        internal TimeSpan DueTime = dueTime;
        private bool _disposed;
        internal void Fire() { if (!_disposed) callback(state); }
        public bool Change(TimeSpan due, TimeSpan period) { DueTime = due; return !_disposed; }
        public void Dispose() => _disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class Transport : ISupplementaryTransport
    {
        internal SupplementaryResponse Response = new(SupplementaryRequestStatus.Success, []);
        internal Func<CancellationToken, Task>? Before;
        internal int Calls;
        public async Task<SupplementaryResponse> SendAsync(string route, byte[]? body, int maximum, CancellationToken cancellation)
        {
            Assert.Equal("/api/v1/feature-controls", route);
            Assert.Null(body);
            Assert.Equal(FeatureControlVerifier.MaximumEnvelopeBytes, maximum);
            Calls++;
            if (Before is not null) await Before(cancellation);
            return Response;
        }
        public void Dispose() { }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal DateTimeOffset Now { get; } = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "WispFeatureControlTests", Guid.NewGuid().ToString("N"));
        internal IReadOnlyDictionary<string, byte[]> Keys { get; }
        internal FeatureControlVerifier Verifier { get; }
        internal Clock Clock { get; }
        internal Fixture()
        {
            Keys = new Dictionary<string, byte[]> { ["fixture"] = _key.ExportSubjectPublicKeyInfo() };
            Verifier = new(Keys);
            Clock = new(Now);
        }
        internal JsonObject Payload(long revision = 1) => new()
        {
            ["schemaVersion"] = 1,
            ["purpose"] = "wisp-feature-controls",
            ["revision"] = revision,
            ["issuedAt"] = Stamp(Now),
            ["expiresAt"] = Stamp(Now.AddHours(2)),
            ["rules"] = new JsonArray(new JsonObject
            {
                ["id"] = "recording-pause",
                ["features"] = new JsonArray("clip-recording"),
                ["reason"] = "Recording is temporarily paused.",
                ["minVersion"] = null,
                ["maxVersion"] = null,
                ["channels"] = new JsonArray("stable"),
                ["startsAt"] = Stamp(Now),
                ["expiresAt"] = Stamp(Now.AddHours(1)),
            }),
        };
        internal byte[] Sign(JsonObject value)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                keyId = "fixture",
                payload = Convert.ToBase64String(bytes),
                signature = Convert.ToBase64String(_key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            });
        }
        internal FeatureControlStore Store() => new(Verifier, Directory);
        internal FeatureControlClient Client(Transport transport, Action<FeatureControlState> changed,
            Func<TimeSpan, CancellationToken, Task>? delay = null) =>
            new(new(new Uri("https://example.invalid/")), Store(), "2.6.8", "stable", changed, Clock, transport, () => 1, delay);
        public void Dispose()
        {
            _key.Dispose();
            var path = Path.GetFullPath(Directory);
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "WispFeatureControlTests")) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected fixture path.");
            if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, true);
        }
    }
}
