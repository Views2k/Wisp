using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Wisp.App.Clips;

namespace Wisp.UiReview;

internal enum ReportedPlaybackProfile { Baseline, NullAudio, NoCachePause, HalfSpeed, OutputFrameDrop, CompatiblePreview, CompatiblePreviewEightThreads, ProductPlaybackCopy }

// Exercises the production transport and native host,
// not ClipsPage, recording, decoded pixel accuracy, or displayed HDR color.
internal static class HdrPlayerReview
{
    internal static int Run(string sourcePath, string fixtureSha, string output, bool reportedClip = false,
        ReportedPlaybackProfile profile = ReportedPlaybackProfile.Baseline)
    {
        var halfSpeed = reportedClip && profile == ReportedPlaybackProfile.HalfSpeed;
        var compatiblePreview = reportedClip && profile is ReportedPlaybackProfile.CompatiblePreview or ReportedPlaybackProfile.CompatiblePreviewEightThreads;
        var productPlaybackCopy = reportedClip && profile == ReportedPlaybackProfile.ProductPlaybackCopy;
        var previewDecoderThreads = profile == ReportedPlaybackProfile.CompatiblePreviewEightThreads ? 8 : 2;
        using var watchdog = new System.Threading.Timer(_ => Environment.Exit(124), null,
            TimeSpan.FromSeconds(compatiblePreview || productPlaybackCopy ? 190 : reportedClip ? halfSpeed ? 40 : 30 : 80), Timeout.InfiniteTimeSpan);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(compatiblePreview || productPlaybackCopy ? 180 : reportedClip ? halfSpeed ? 35 : 23 : 70));
        var clock = Stopwatch.StartNew();
        var foreground = GetForegroundWindow();
        var previousContext = SynchronizationContext.Current;
        var report = new Report { ReportedClip = reportedClip, Profile = profile.ToString() };
        var originalOptions = LosslessMpvNative.Options.ToArray();
        var checkedApps = 0L;
        FileStream? source = null, index = null;
        byte[]? indexHash = null;
        byte[]? previewHash = null;
        string? ownedCopy = null;
        string? cachedPlaybackPath = null;
        Application? application = null;
        Window? window = null;
        LosslessVideoHost? host = null;
        LosslessClipPlayer? player = null;
        Task<LosslessAudioStatistics>? pendingAudio = null;
        var nextAudioSample = TimeSpan.Zero;
        try
        {
            Guard();
            Stage("fixture-validation");
            var checkout = Checkout(output);
            var full = Path.GetFullPath(sourcePath);
            var fixtureRoot = Path.Combine(checkout, "work", "clips-hdr-20261003") + Path.DirectorySeparatorChar;
            var reportedPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Wisp", "Clips", "8599d1e158ac4f60a2408f9d740df782.mp4");
            Need(reportedClip ? full.Equals(reportedPath, StringComparison.OrdinalIgnoreCase)
                : full.StartsWith(fixtureRoot, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetExtension(full).Equals(".mp4", StringComparison.OrdinalIgnoreCase) &&
                    Regex.IsMatch(fixtureSha, "^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant), "explicit-fixture-required");
            ClipLibrary.CheckPath(full);
            source = new(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            ClipEntry clip;
            string playbackPath;
            if (reportedClip)
            {
                Need(source.Length == 5_504_205_245, "reported-size");
                var indexPath = Path.Combine(Path.GetDirectoryName(full)!, ClipLibrary.IndexFileName);
                ClipLibrary.CheckPath(indexPath);
                index = new(indexPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                Need(index.Length is > 0 and <= ClipLibrary.MaximumIndexBytes, "index-size");
                indexHash = SHA256.HashData(index); index.Position = 0;
                using var document = JsonDocument.Parse(index, new JsonDocumentOptions { MaxDepth = 12 });
                var root = document.RootElement;
                Need(root.GetProperty("format").GetString() == "wisp.clip-library" && root.GetProperty("version").GetInt32() == 2,
                    "index-format");
                var id = Guid.ParseExact(Path.GetFileNameWithoutExtension(full), "N");
                var entry = root.GetProperty("clips").EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == id);
                clip = entry.Deserialize<ClipEntry>(new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
                    MaxDepth = 12
                }) ?? throw new ProbeFailure("missing-entry");
                Need(clip.Media.FileBytes == source.Length && clip.Media.HdrVideo && clip.Media.LosslessVideo && clip.Media.HasAudio &&
                    clip.Media.Width == 3840 && clip.Media.Height == 2160 && clip.Media.FrameRate == 60 &&
                    Math.Abs(clip.DurationSeconds - 9.55) < .001, "reported-metadata");
                report.Lossless = true;
                report.ReportedMedia = clip.Media;
                report.OriginalHeldReadOnly = true;
                playbackPath = full;
                if (compatiblePreview)
                {
                    Stage("prepare-compatible-preview");
                    report.PreviewDecoderThreads = previewDecoderThreads;
                    var previewId = Guid.NewGuid();
                    ownedCopy = Path.Combine(output, previewId.ToString("N") + ".mp4");
                    ClipLibrary.CheckPath(ownedCopy);
                    report.OwnedCopyName = Path.GetFileName(ownedCopy);
                    Await(PrepareCompatiblePreview(clip, full, ownedCopy));
                    Guard();
                    report.PreviewProbe = CompatibleClipExporter.Inspect(ownedCopy, deadline.Token);
                    CompatibleClipExporter.Validate(clip, report.PreviewProbe, compatible: true, target: CompatibleClipTarget.PreserveHdr);
                    using (var preview = new FileStream(ownedCopy, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        Need(preview.Length is > 0 and <= ClipLibrary.MaximumMediaBytes, "preview-size");
                        previewHash = SHA256.HashData(preview);
                        report.PreviewBytes = preview.Length;
                        clip = clip with
                        {
                            Id = previewId,
                            Recording = clip.Recording with { LosslessVideo = false },
                            Media = clip.Media with { FileBytes = preview.Length, LosslessVideo = false }
                        };
                    }
                    report.PlaybackMedia = clip.Media;
                    Check(LosslessClipPlayer.MatchesDecodedFormat(clip.Media, report.PreviewProbe.Codec,
                        report.PreviewProbe.PixelFormat, report.PreviewProbe.Gamma, report.PreviewProbe.Matrix,
                        report.PreviewProbe.Range, report.PreviewProbe.Primaries), "compatible-hdr-player-contract");
                    report.PreviewPrepared = true;
                    playbackPath = ownedCopy;
                }
            }
            else
            {
                Need(source.Length is > 0 and <= 256L * 1024 * 1024, "fixture-size");
                report.FixtureSha256 = Convert.ToHexString(SHA256.HashData(source));
                Check(report.FixtureSha256.Equals(fixtureSha, StringComparison.OrdinalIgnoreCase), "fixture-hash");
                source.Position = 0;
                Stage("source-inspection");
                var probe = CompatibleClipExporter.Inspect(full, deadline.Token);
                Need(probe.Duration is > .1 and <= 30 && double.IsFinite(probe.Duration) &&
                    probe.Width == 1920 && probe.Height == 1080 &&
                    double.IsFinite(probe.FrameRate) && Math.Abs(probe.FrameRate - 60) < .01, "bounded-generated-format");
                var lossless = LosslessClipPlayer.IsHdr444PixelFormat(probe.PixelFormat);
                var media = new FinalizedClipMedia(source.Length, probe.Width, probe.Height, 60, 0,
                    checked((long)Math.Round(probe.Duration * 10_000_000)), probe.HasAudio, lossless, HdrVideo: true);
                Check(LosslessClipPlayer.MatchesDecodedFormat(media, probe.Codec, probe.PixelFormat,
                    probe.Gamma, probe.Matrix, probe.Range, probe.Primaries), "strict-hdr-metadata");
                report.Source = probe;
                report.Lossless = lossless;
                var id = Guid.NewGuid();
                ownedCopy = Path.Combine(output, id.ToString("N") + ".mp4");
                report.OwnedCopyName = Path.GetFileName(ownedCopy);
                Stage("owned-copy");
                using (var copy = new FileStream(ownedCopy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { source.CopyTo(copy); copy.Flush(true); }
                using (var copy = new FileStream(ownedCopy, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Check(Convert.ToHexString(SHA256.HashData(copy)) == report.FixtureSha256, "owned-copy-hash");
                clip = new ClipEntry(id, DateTimeOffset.UtcNow, new(60, 1080, 60, 75, probe.HasAudio, lossless), media);
                playbackPath = ownedCopy;
            }
            Guard();
            Need(reportedClip || profile == ReportedPlaybackProfile.Baseline, "profile-requires-reported-clip");
            for (var option = 0; option < LosslessMpvNative.Options.Length; option++)
            {
                var (name, value) = LosslessMpvNative.Options[option];
                if (profile == ReportedPlaybackProfile.NullAudio && name == "ao") value = "null";
                if (profile == ReportedPlaybackProfile.NoCachePause && name == "cache-pause") value = "no";
                if (profile == ReportedPlaybackProfile.OutputFrameDrop && name == "framedrop") value = "vo";
                LosslessMpvNative.Options[option] = (name, value);
                if (name is "ao" or "vo" or "hwdec" or "cache" or "cache-pause" or "cache-pause-initial" or
                    "cache-pause-wait" or "cache-secs" or "demuxer-max-bytes" or "framedrop")
                    report.RequestedOptions[name] = value;
            }
            report.RequestedOptions["speed"] = halfSpeed ? "0.5" : "1";
            application = new ReviewApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var viewport = new Grid { Background = Brushes.Black };
            host = new LosslessVideoHost(viewport);
            viewport.Children.Add(host);
            window = new Window
            {
                Title = "Wisp HDR player check",
                Width = 640,
                Height = 400,
                ShowActivated = false,
                ShowInTaskbar = false,
                Focusable = false,
                IsHitTestVisible = false,
                WindowStyle = WindowStyle.ToolWindow,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = Brushes.Black,
                Content = viewport
            };
            window.SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                const int index = -20, styles = 0x08000080;
                _ = SetWindowLong(hwnd, index, GetWindowLong(hwnd, index) | styles);
                Check((GetWindowLong(hwnd, index) & styles) == styles, "passive-window-style");
                HwndSource.FromHwnd(hwnd)?.AddHook(BlockActivation);
            };
            ClipPlaybackCache? playbackCache = null;
            if (reportedClip)
            {
                // The original library is read only even when the product
                // prepares a copy. Cold and warm runs share only this workspace cache.
                var cacheDirectory = Path.Combine(checkout, "work", "clips-playback-queue-20261003", "product-playback-cache");
                Need(Path.GetFullPath(cacheDirectory).StartsWith(checkout + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                    "workspace-cache-required");
                ClipLibrary.CheckPath(cacheDirectory);
                playbackCache = new ClipPlaybackCache(Path.GetDirectoryName(full)!, cacheDirectory: cacheDirectory);
                report.WorkspaceCache = true;
            }
            player = new LosslessClipPlayer(host, 0, playbackCache);
            window.Show();
            Guard();
            Need(AppsClosed(), "apps-must-be-closed-before-preparation");
            player.Open(clip, playbackPath);
            Await(Scenario());
            report.TransportCompleted = true;

            async Task Scenario()
            {
                Stage("ready-paused");
                await Until(() => player.Snapshot.Ready, productPlaybackCopy ? 150 : 18);
                if (productPlaybackCopy)
                {
                    Check(player.Snapshot.IsPlaybackCopy, "product-uses-playback-copy");
                    report.PlaybackCopyCacheHit = player.PlaybackCopyCacheHit;
                    report.PlaybackMedia = player.PlaybackCopyMedia;
                    report.PlaybackCopyReadySeconds = clock.Elapsed.TotalSeconds;
                    cachedPlaybackPath = player.PlaybackCopyPath;
                    Need(cachedPlaybackPath is not null && report.PlaybackMedia is { HdrVideo: true, LosslessVideo: false },
                        "product-compatible-media");
                }
                Check(!player.Snapshot.Buffering && !player.Snapshot.Ended && player.Snapshot.Position <= 1d / 60,
                    "ready-at-zero");
                await Silenced("ready-volume-zero", requireMute: true);
                await StablePaused("ready-stays-paused");
                if (reportedClip)
                {
                    report.ActualSpeed = await ReadOrSetDiagnosticSpeed();
                    Check(report.ActualSpeed == (halfSpeed ? .5 : 1), "requested-speed-applied");
                }

                if (!reportedClip)
                {
                    Stage("paused-seek");
                    var target = player.Snapshot.Duration / 2;
                    player.Seek(target);
                    Check(player.Snapshot.Buffering, "seek-reports-loading");
                    await Until(() => !player.Snapshot.Buffering && Math.Abs(player.Snapshot.Position - target) <= 1d / 60 + .002, 18);
                    Check(player.DiagnosticStage == "paused" && !player.Snapshot.Ended, "seek-keeps-pause");
                    await Silenced("seek-volume-zero", requireMute: true);
                    await StablePaused("seek-stays-paused");

                    Stage("rewind-paused");
                    player.Seek(0);
                    await Until(() => !player.Snapshot.Buffering && player.Snapshot.Position <= 1d / 60, 18);
                    Check(player.DiagnosticStage == "paused" && !player.Snapshot.Ended, "rewind-keeps-pause");
                }
                Stage("explicit-play");
                var playStarted = clock.Elapsed;
                var rewoundPosition = player.Snapshot.Position;
                player.SetPaused(false);
                await Until(() => player.DiagnosticStage == "playing" && player.Snapshot.Position > rewoundPosition + .001, 10);
                Check(!player.Snapshot.Ended && player.DiagnosticStage == "playing", "play-clock-advances-before-eof");
                report.FirstPlayingPosition = player.Snapshot.Position;
                await Silenced("playing-volume-zero", requireMute: false);
                await Until(() => player.Snapshot.Ended, clip.DurationSeconds / (halfSpeed ? .5 : 1) + 10);
                Check(Math.Abs(player.Snapshot.Position - player.Snapshot.Duration) <= .002, "play-reaches-eof");
                report.EndPosition = player.Snapshot.Position;
                report.PlaybackSeconds = (clock.Elapsed - playStarted).TotalSeconds;
                await Silenced("ended-volume-zero", requireMute: false);
                if (compatiblePreview || productPlaybackCopy)
                {
                    Stage("paused-seek");
                    player.SetPaused(true);
                    await Until(() => player.DiagnosticStage == "paused", 10);
                    var target = player.Snapshot.Duration / 2;
                    player.Seek(target);
                    Check(player.Snapshot.Buffering, "preview-seek-reports-loading");
                    await Until(() => !player.Snapshot.Buffering && Math.Abs(player.Snapshot.Position - target) <= 1d / 60 + .002, 18);
                    Check(player.DiagnosticStage == "paused" && !player.Snapshot.Ended, "preview-seek-keeps-pause");
                    await Silenced("preview-seek-volume-zero", requireMute: true);
                    await StablePaused("preview-seek-stays-paused");
                    Stage("rewind-paused");
                    player.Seek(0);
                    await Until(() => !player.Snapshot.Buffering && player.Snapshot.Position <= 1d / 60, 18);
                    await StablePaused("preview-rewind-stays-paused");
                    Stage("replay");
                    var replayStarted = clock.Elapsed;
                    player.SetPaused(false);
                    await Until(() => player.DiagnosticStage == "playing" && player.Snapshot.Position > .001, 10);
                    Check(!player.Snapshot.Ended, "preview-replay-clock-advances");
                    await Silenced("preview-replay-volume-zero", requireMute: false);
                    await Until(() => player.Snapshot.Ended, clip.DurationSeconds + 10);
                    Check(Math.Abs(player.Snapshot.Position - player.Snapshot.Duration) <= .002, "preview-replay-reaches-eof");
                    report.ReplaySeconds = (clock.Elapsed - replayStarted).TotalSeconds;
                    report.ReplayCompleted = true;
                }
            }

            async Task PrepareCompatiblePreview(ClipEntry original, string originalPath, string destination)
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                var conversionStarted = clock.Elapsed;
                var percent = 0;
                var lastWritten = -1;
                var progress = new SynchronousProgress(value => Interlocked.Exchange(ref percent, (int)Math.Clamp(value, 0, 100)));
                using var staged = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
                var conversion = new CompatibleClipExporter(previewDecoderThreads, target: CompatibleClipTarget.PreserveHdr)
                    .ExportAsync(original, originalPath, destination, progress, cancellation.Token);
                try
                {
                    while (!conversion.IsCompleted)
                    {
                        Guard();
                        var current = Volatile.Read(ref percent);
                        if (current != lastWritten)
                        {
                            lastWritten = current;
                            File.WriteAllText(Path.Combine(output, "hdr-preview-progress.json"), JsonSerializer.Serialize(new
                            { stage = "compatible-export", percent = current, seconds = clock.Elapsed.TotalSeconds }));
                        }
                        await Task.WhenAny(conversion, Task.Delay(50));
                    }
                    await conversion;
                    report.ConversionCompleted = true;
                }
                catch
                {
                    cancellation.Cancel();
                    try { await conversion; } catch (Exception) { }
                    throw;
                }
                finally
                {
                    report.ConversionSeconds = (clock.Elapsed - conversionStarted).TotalSeconds;
                    report.ConversionCleanup = conversion.IsCompleted;
                    report.ConversionProgress = Volatile.Read(ref percent);
                }
            }

            Task<double?> ReadOrSetDiagnosticSpeed()
            {
                // Keep this one-off experiment out of the product API. Use the
                // existing owner queue; never access libmpv from the UI thread.
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var queue = typeof(LosslessClipPlayer).GetMethod("Queue", flags)
                    ?? throw new ProbeFailure("missing-player-queue");
                var nativeField = typeof(LosslessClipPlayer).GetField("_native", flags)
                    ?? throw new ProbeFailure("missing-player-native");
                var completion = new TaskCompletionSource<double?>(TaskCreationOptions.RunContinuationsAsynchronously);
                Func<Task> operation = () =>
                {
                    try
                    {
                        var native = nativeField.GetValue(player) as LosslessMpvNative
                            ?? throw new ProbeFailure("missing-native-player");
                        Need(native.Flag("pause") == true, "diagnostic-speed-requires-paused");
                        if (halfSpeed) native.Set("speed", "0.5");
                        completion.TrySetResult(native.Number("speed"));
                    }
                    catch (Exception error) { completion.TrySetException(error); }
                    return Task.CompletedTask;
                };
                queue.Invoke(player, [operation]);
                return completion.Task.WaitAsync(deadline.Token);
            }

            async Task StablePaused(string name)
            {
                var position = player.Snapshot.Position;
                var until = clock.Elapsed + TimeSpan.FromMilliseconds(350);
                do
                {
                    await Tick();
                    Need(!player.Snapshot.Buffering && !player.Snapshot.Ended &&
                        Math.Abs(player.Snapshot.Position - position) <= .002, name);
                } while (clock.Elapsed < until);
                Check(true, name);
            }

            async Task Silenced(string name, bool requireMute)
            {
                var stats = await player.ReadAudioStatisticsAsync(deadline.Token);
                Guard();
                AddAudio(name, stats);
                Check(stats.Volume == 0 && (!requireMute || stats.Mute == true), name);
            }

            void AddAudio(string stage, LosslessAudioStatistics stats) => report.Audio.Add(new(stage, stats.Volume, stats.Mute,
                stats.Position, stats.Codec, clock.Elapsed.TotalSeconds, stats.VideoPosition, stats.AudioVideoDifference,
                stats.PausedForCache, stats.CacheBufferingState));

            async Task Until(Func<bool> condition, double seconds)
            {
                var until = clock.Elapsed + TimeSpan.FromSeconds(seconds);
                while (!condition())
                {
                    Need(clock.Elapsed < until, "stage-timeout");
                    await Tick();
                }
                Guard();
                Need(player.Snapshot.Failure is null, "player-failure");
            }

            async Task Tick()
            {
                Guard();
                if (productPlaybackCopy && player.Snapshot.PreparingPlaybackCopy)
                {
                    report.CopyPreparationObserved = true;
                    var percent = player.Snapshot.PreparationProgress;
                    if (percent is { } value && (report.LastPreparationProgress is null || value > report.LastPreparationProgress))
                    {
                        report.LastPreparationProgress = value;
                        File.WriteAllText(Path.Combine(output, "hdr-preview-progress.json"), JsonSerializer.Serialize(new
                        { stage = "product-playback-copy", percent = value, seconds = clock.Elapsed.TotalSeconds }));
                    }
                }
                if (reportedClip && report.Stage is "explicit-play" or "replay")
                {
                    if (pendingAudio?.IsCompletedSuccessfully == true)
                    {
                        AddAudio("playing-sample", pendingAudio.Result);
                        pendingAudio = null;
                    }
                    if (pendingAudio is null && clock.Elapsed >= nextAudioSample && report.Audio.Count < 256)
                    {
                        pendingAudio = player.ReadAudioStatisticsAsync(deadline.Token);
                        nextAudioSample = clock.Elapsed + TimeSpan.FromMilliseconds(100);
                    }
                }
                player.RequestPoll();
                await Task.Delay(5, deadline.Token);
                Guard();
                report.LastPosition = player.Snapshot.Position;
                report.PlayerStage = player.DiagnosticStage;
                Need(player.Snapshot.Failure is null, "player-failure");
            }
        }
        catch (Exception error)
        {
            report.FailureStage = report.Stage;
            report.Failure = error is ProbeFailure known ? known.Code : error is OperationCanceledException ? "deadline" : "diagnostic-failed";
            report.HResult = error.HResult;
            report.NativeCode = error is LosslessMpvException native ? native.Code : null;
            report.ExportStage = error.Data["wisp-export-stage"] as string;
            report.ExportReason = error.Data["wisp-export-reason"] as string;
            report.PlayerFailurePresent = player?.Snapshot.Failure is not null;
            report.PlayerFailure = player?.Snapshot.Failure;
            report.PacketQueueDiagnostic = player?.PacketQueueDiagnostic;
        }
        finally
        {
            report.Stage = "cleanup";
            originalOptions.CopyTo(LosslessMpvNative.Options, 0);
            report.OptionsRestored = originalOptions.SequenceEqual(LosslessMpvNative.Options);
            report.DecoderDiagnostic = player?.DecoderDiagnostic;
            try
            {
                if (player is not null) report.PlayerCleanup = AwaitResult(player.CloseAsync());
                report.RuntimeCleanup = AwaitResult(LosslessMpvRuntime.ShutdownAsync());
                report.PlayerCleanupStatus = player?.CleanupStatus;
                host?.Dispose();
                window?.Close();
                report.WindowClosed = window?.IsVisible != true;
                application?.Shutdown();
                if ((player is null || report.PlayerCleanup) && report.RuntimeCleanup && ownedCopy is not null)
                {
                    using var exclusive = new FileStream(ownedCopy, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    report.ExclusiveReopen = true;
                    report.CopyUnchanged = compatiblePreview
                        ? previewHash is not null && previewHash.SequenceEqual(SHA256.HashData(exclusive))
                        : Convert.ToHexString(SHA256.HashData(exclusive)) == report.FixtureSha256;
                }
                if (index is not null && indexHash is not null)
                {
                    index.Position = 0;
                    report.IndexUnchanged = indexHash.SequenceEqual(SHA256.HashData(index));
                    report.OriginalLengthUnchanged = source?.Length == report.ReportedMedia?.FileBytes;
                }
                if (productPlaybackCopy && cachedPlaybackPath is not null && report.PlayerCleanup && report.RuntimeCleanup)
                {
                    using var exclusive = new FileStream(cachedPlaybackPath, FileMode.Open, FileAccess.Read, FileShare.None);
                    report.PlaybackCopyReleased = exclusive.Length == report.PlaybackMedia?.FileBytes;
                }
            }
            catch (Exception error) { report.Failure ??= "cleanup-failed"; report.HResult ??= error.HResult; }
            source?.Dispose(); index?.Dispose();
            SynchronizationContext.SetSynchronizationContext(previousContext);
            report.ForegroundPreserved = foreground != 0 && GetForegroundWindow() == foreground;
            try { report.AppsClosedAtEnd = AppsClosed(); } catch { report.AppsClosedAtEnd = false; }
            report.ElapsedSeconds = clock.Elapsed.TotalSeconds;
            report.Completed = report.TransportCompleted && report.Failure is null && report.PlayerCleanup && report.RuntimeCleanup &&
                report.WindowClosed && report.OptionsRestored && (!compatiblePreview || report.PreviewPrepared && report.ConversionCleanup &&
                    report.CopyUnchanged && report.ExclusiveReopen && report.ReplayCompleted) &&
                (!productPlaybackCopy || report.PlaybackCopyReleased && report.ReplayCompleted) &&
                (reportedClip ? report.IndexUnchanged && report.OriginalLengthUnchanged
                    : report.ExclusiveReopen && report.CopyUnchanged) && report.ForegroundPreserved && report.AppsClosedAtEnd;
            using var file = new FileStream(Path.Combine(output, "hdr-player-review.json"), FileMode.CreateNew, FileAccess.Write);
            JsonSerializer.Serialize(file, report, new JsonSerializerOptions { WriteIndented = true });
        }
        return report.Completed ? 0 : 1;

        void Stage(string stage)
        {
            report.Stage = stage;
            File.WriteAllText(Path.Combine(output, "hdr-player-progress.json"), JsonSerializer.Serialize(new { stage, seconds = clock.Elapsed.TotalSeconds }));
        }
        void Check(bool value, string name) { report.Checks.Add(new(name, value, clock.Elapsed.TotalSeconds)); Need(value, name); }
        void Guard()
        {
            deadline.Token.ThrowIfCancellationRequested();
            Need(foreground != 0 && GetForegroundWindow() == foreground, "foreground-changed");
            if (checkedApps != 0 && Stopwatch.GetElapsedTime(checkedApps) < TimeSpan.FromMilliseconds(250)) return;
            checkedApps = Stopwatch.GetTimestamp();
            Need(AppsClosed(), "apps-must-be-closed");
        }
    }

    private static bool AppsClosed()
    {
        foreach (var name in new[] { "Wisp", "Wisp.Recorder", "ForzaHorizon6", "ForzaHorizon5", "ForzaHorizon4", "ForzaMotorsport" })
        {
            var processes = Process.GetProcessesByName(name);
            try { if (processes.Length != 0) return false; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        return true;
    }
    private static string Checkout(string output)
    {
        for (DirectoryInfo? directory = new(Path.GetFullPath(output)); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Wisp.sln"))) return directory.FullName;
        throw new ProbeFailure("workspace-output-required");
    }
    private static void Await(Task task)
    {
        if (!task.IsCompleted)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            _ = task.ContinueWith(_ => dispatcher.InvokeAsync(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }
    private static T AwaitResult<T>(Task<T> task) { Await(task); return task.GetAwaiter().GetResult(); }
    private static void Need(bool value, string code) { if (!value) throw new ProbeFailure(code); }
    private sealed class ProbeFailure(string code) : Exception { internal string Code { get; } = code; }
    private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
    { public void Report(double value) => report(value); }
    private sealed class ReviewApplication : Application { protected override void OnStartup(StartupEventArgs e) { } }
    private sealed record CheckResult(string Name, bool Passed, double Seconds);
    private sealed record AudioSample(string Stage, double? Volume, bool? Mute, double? Position, string? Codec,
        double Seconds, double? VideoPosition, double? AudioVideoDifference, bool? PausedForCache, double? CacheBufferingState);
    private sealed class Report
    {
        public int Schema => 1;
        public bool Completed { get; set; }
        public bool ReportedClip { get; set; }
        public string Profile { get; set; } = "Baseline";
        public Dictionary<string, string> RequestedOptions { get; } = new(StringComparer.Ordinal);
        public bool OptionsRestored { get; set; }
        public double? ActualSpeed { get; set; }
        public bool PreviewPrepared { get; set; }
        public bool WorkspaceCache { get; set; }
        public bool? PlaybackCopyCacheHit { get; set; }
        public bool CopyPreparationObserved { get; set; }
        public double? LastPreparationProgress { get; set; }
        public double? PlaybackCopyReadySeconds { get; set; }
        public bool PlaybackCopyReleased { get; set; }
        public int? PreviewDecoderThreads { get; set; }
        public bool ConversionCompleted { get; set; }
        public bool ConversionCleanup { get; set; }
        public double? ConversionSeconds { get; set; }
        public int ConversionProgress { get; set; }
        public long? PreviewBytes { get; set; }
        public CompatibleClipExporter.Probe? PreviewProbe { get; set; }
        public FinalizedClipMedia? PlaybackMedia { get; set; }
        public double? PlaybackSeconds { get; set; }
        public double? ReplaySeconds { get; set; }
        public bool ReplayCompleted { get; set; }
        public FinalizedClipMedia? ReportedMedia { get; set; }
        public bool OriginalHeldReadOnly { get; set; }
        public bool OriginalLengthUnchanged { get; set; }
        public bool IndexUnchanged { get; set; }
        public string Stage { get; set; } = "not-started";
        public string? FailureStage { get; set; }
        public string? Failure { get; set; }
        public int? HResult { get; set; }
        public string? NativeCode { get; set; }
        public string? ExportStage { get; set; }
        public string? ExportReason { get; set; }
        public string? FixtureSha256 { get; set; }
        public string? OwnedCopyName { get; set; }
        public CompatibleClipExporter.Probe? Source { get; set; }
        public bool Lossless { get; set; }
        public bool TransportCompleted { get; set; }
        public bool PlayerFailurePresent { get; set; }
        public string? PlayerFailure { get; set; }
        public LosslessPacketQueueDiagnostic? PacketQueueDiagnostic { get; set; }
        public LosslessDecoderDiagnostic? DecoderDiagnostic { get; set; }
        public string? PlayerStage { get; set; }
        public double? LastPosition { get; set; }
        public double? FirstPlayingPosition { get; set; }
        public double? EndPosition { get; set; }
        public bool PlayerCleanup { get; set; }
        public bool RuntimeCleanup { get; set; }
        public string? PlayerCleanupStatus { get; set; }
        public bool WindowClosed { get; set; }
        public bool ExclusiveReopen { get; set; }
        public bool CopyUnchanged { get; set; }
        public bool ForegroundPreserved { get; set; }
        public bool AppsClosedAtEnd { get; set; }
        public double ElapsedSeconds { get; set; }
        public List<CheckResult> Checks { get; } = [];
        public List<AudioSample> Audio { get; } = [];
        public string Scope => ReportedClip
            ? "Exact reported HDR library clip held read-only; production LosslessClipPlayer and LosslessVideoHost transport. No original copy, full-file hash or library mutation."
            : "Pinned generated HDR fixture; production LosslessClipPlayer and LosslessVideoHost transport. Owned GUID copy retained for root cleanup.";
        public bool ClipsPageVerified => false;
        public bool HdrAppearanceVerified => false;
        public bool DecodedPixelsVerified => false;
        public bool CaptureStarted => false;
        public bool GameplayVerified => false;
    }
    private static nint BlockActivation(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    { if (message == 0x0021) { handled = true; return new nint(3); } return 0; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(nint hwnd, int index, int value);
}
