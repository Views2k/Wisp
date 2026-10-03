using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using Wisp.App.Clips;

namespace Wisp.UiReview;

internal static class ClipExportReview
{
    internal static int LibraryCompatibleCopy(string sourcePath, string output)
    {
        var stage = "closed-app-guard";
        FileStream? source = null, index = null;
        byte[]? indexHash = null;
        var lastProgress = -1;
        var clock = Stopwatch.StartNew();
        try
        {
            foreach (var name in new[] { "Wisp", "Wisp.Recorder", "ForzaHorizon6", "ForzaHorizon5", "ForzaHorizon4", "ForzaMotorsport" })
            {
                var processes = Process.GetProcessesByName(name);
                try { if (processes.Length != 0) throw new InvalidOperationException("Close Wisp and Forza before this diagnostic."); }
                finally { foreach (var process in processes) process.Dispose(); }
            }
            stage = "library-entry";
            if (!Path.IsPathFullyQualified(sourcePath) || sourcePath.StartsWith(@"\\", StringComparison.Ordinal) ||
                !Path.GetExtension(sourcePath).Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(Path.GetFileNameWithoutExtension(sourcePath), "N", out var id))
                throw new ArgumentException("Choose the original local library clip.");
            ClipLibrary.CheckPath(sourcePath);
            var indexPath = Path.Combine(Path.GetDirectoryName(sourcePath)!, ClipLibrary.IndexFileName);
            ClipLibrary.CheckPath(indexPath);
            index = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (index.Length is <= 0 or > ClipLibrary.MaximumIndexBytes) throw new InvalidDataException("Invalid index size.");
            indexHash = SHA256.HashData(index); index.Position = 0;
            using var document = JsonDocument.Parse(index, new JsonDocumentOptions { MaxDepth = 12 });
            var root = document.RootElement;
            if (root.GetProperty("format").GetString() != "wisp.clip-library" || root.GetProperty("version").GetInt32() != 2)
                throw new InvalidDataException("A current library index is required.");
            var matches = root.GetProperty("clips").EnumerateArray()
                .Where(entry => entry.GetProperty("id").GetGuid() == id).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("The selected clip must have one exact library entry.");
            var clip = matches[0].Deserialize<ClipEntry>(new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
                MaxDepth = 12
            }) ?? throw new InvalidDataException("The selected library entry is missing.");
            source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (source.Length != clip.Media.FileBytes || source.Length is <= 0 or > ClipLibrary.MaximumMediaBytes ||
                !(clip.Media.LosslessVideo || clip.Media.HdrVideo))
                throw new InvalidDataException("The original differs from its saved entry.");
            Write("library-entry.json", new
            {
                clip.Recording,
                clip.Media,
                clip.DurationSeconds,
                sourceBytes = source.Length,
                indexSha256 = Convert.ToHexString(indexHash).ToLowerInvariant(),
                sourceHashComputed = false
            });
            using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(31));
            stage = "source-inspection";
            var before = CompatibleClipExporter.Inspect(sourcePath, lifetime.Token);
            Write("source.json", before);
            stage = "production-export";
            var destination = Path.Combine(output, "compatible.mp4");
            var progress = new SynchronousProgress(value =>
            {
                var percent = (int)Math.Clamp(value, 0, 100);
                if (percent <= lastProgress) return;
                lastProgress = percent;
                Write("progress.json", new { stage, percent, elapsedSeconds = clock.Elapsed.TotalSeconds });
            });
            using (var staged = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite))
                new CompatibleClipExporter().ExportAsync(clip, sourcePath, destination, progress, lifetime.Token).GetAwaiter().GetResult();
            stage = "output-inspection";
            var after = CompatibleClipExporter.Inspect(destination, lifetime.Token);
            index.Position = 0;
            var indexUnchanged = indexHash.SequenceEqual(SHA256.HashData(index));
            if (!indexUnchanged) throw new InvalidDataException("The source index changed.");
            Write("export.json", new
            {
                before,
                after,
                elapsedSeconds = clock.Elapsed.TotalSeconds,
                fileBytes = new FileInfo(destination).Length,
                originalBytes = source.Length,
                indexUnchanged,
                sourceHeldReadOnly = true,
                sourceHashComputed = false,
                libraryPublicationAttempted = false
            });
            return 0;
        }
        catch (Exception error)
        {
            Write("failure.json", new
            {
                exceptionType = error.GetType().Name,
                error.HResult,
                stage,
                lastProgressPercent = lastProgress,
                elapsedSeconds = clock.Elapsed.TotalSeconds,
                nativeCode = error is LosslessMpvException mpv ? mpv.Code : null,
                reason = error.Data["wisp-export-reason"] as string,
                probe = error.Data["wisp-export-probe"] as CompatibleClipExporter.Probe,
                sourceHeldReadOnly = source is not null,
                indexHeldReadOnly = index is not null,
                sourceHashComputed = false,
                libraryPublicationAttempted = false
            });
            return 1;
        }
        finally { source?.Dispose(); index?.Dispose(); }

        void Write(string name, object value) => File.WriteAllText(Path.Combine(output, name),
            JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static int CancelCompatibleCopy(string sourcePath, string output)
    {
        var stage = "source-inspection";
        try
        {
            using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var originalHash = SHA256.HashData(source);
            var before = CompatibleClipExporter.Inspect(sourcePath, lifetime.Token);
            var rate = checked((int)Math.Round(before.FrameRate));
            var hdrVideo = before.Codec == "hevc" && before.Gamma == "pq" && before.Primaries == "bt.2020";
            var lossless = before.Matrix == "rgb" && before.Range == "full" &&
                (hdrVideo ? LosslessClipPlayer.IsHdr444PixelFormat(before.PixelFormat) : before.PixelFormat == "gbrp");
            var clip = new ClipEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, new(60, before.Height, rate, 75, LosslessVideo: lossless),
                new(source.Length, before.Width, before.Height, rate, 0, checked((long)(before.Duration * 10_000_000)), before.HasAudio, LosslessVideo: lossless, HdrVideo: hdrVideo));
            var destination = Path.Combine(output, "cancelled-partial.mp4");
            var requested = false; var observed = false; double cancelledAt = 0;
            var progress = new SynchronousProgress(value =>
            {
                if (requested || value <= 0 || value >= 95) return;
                requested = true; cancelledAt = value; lifetime.Cancel();
            });
            stage = "cancel-during-encoding";
            using (var staged = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                try
                {
                    new CompatibleClipExporter().ExportAsync(clip, sourcePath, destination, progress, lifetime.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) when (requested) { observed = true; }
            }
            if (!observed) throw new InvalidOperationException("The fixture did not reach cancellation during encoding.");
            stage = "native-file-release";
            long partialBytes;
            using (var exclusive = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                partialBytes = exclusive.Length;
            File.Delete(destination);
            source.Position = 0;
            var originalUnchanged = originalHash.SequenceEqual(SHA256.HashData(source));
            if (!originalUnchanged || File.Exists(destination)) throw new InvalidOperationException("The cancellation fixture did not preserve file ownership.");
            File.WriteAllText(Path.Combine(output, "cancellation.json"), JsonSerializer.Serialize(new
            {
                cancelledAtPercent = cancelledAt,
                cancellationObserved = observed,
                nativeFileReleased = true,
                ownedPartialRemoved = true,
                originalUnchanged,
                partialBytes,
                originalBytes = source.Length
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(output, "failure.json"), JsonSerializer.Serialize(new
            {
                exceptionType = error.GetType().Name,
                error.HResult,
                stage,
                nativeCode = error is LosslessMpvException mpv ? mpv.Code : null,
                reason = error.Data["wisp-export-reason"] as string,
                probe = error.Data["wisp-export-probe"] as CompatibleClipExporter.Probe
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }

    private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    internal static int CompatibleCopy(string sourcePath, string output)
    {
        var stage = "source-inspection";
        try
        {
            using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var before = CompatibleClipExporter.Inspect(sourcePath, lifetime.Token);
            File.WriteAllText(Path.Combine(output, "source.json"), JsonSerializer.Serialize(before, new JsonSerializerOptions { WriteIndented = true }));
            stage = "compatible-encoding";
            var rate = checked((int)Math.Round(before.FrameRate));
            var hdrVideo = before.Codec == "hevc" && before.Gamma == "pq" && before.Primaries == "bt.2020";
            var lossless = before.Matrix == "rgb" && before.Range == "full" &&
                (hdrVideo ? LosslessClipPlayer.IsHdr444PixelFormat(before.PixelFormat) : before.PixelFormat == "gbrp");
            var clip = new ClipEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, new(60, before.Height, rate, 75, LosslessVideo: lossless),
                new(source.Length, before.Width, before.Height, rate, 0, checked((long)(before.Duration * 10_000_000)), before.HasAudio, LosslessVideo: lossless, HdrVideo: hdrVideo));
            var destination = Path.Combine(output, "compatible.mp4");
            using (var staged = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite))
                new CompatibleClipExporter().ExportAsync(clip, sourcePath, destination, null, lifetime.Token).GetAwaiter().GetResult();
            stage = "output-inspection";
            var after = CompatibleClipExporter.Inspect(destination, lifetime.Token);
            File.WriteAllText(Path.Combine(output, "export.json"), JsonSerializer.Serialize(new
            { before, after, fileBytes = new FileInfo(destination).Length, originalBytes = source.Length }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(output, "failure.json"), JsonSerializer.Serialize(new
            {
                exceptionType = error.GetType().Name,
                error.HResult,
                stage,
                nativeCode = error is LosslessMpvException mpv ? mpv.Code : null,
                reason = error.Data["wisp-export-reason"] as string,
                probe = error.Data["wisp-export-probe"] as CompatibleClipExporter.Probe
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }

    internal static int NativeFailureContext(string sourcePath, string output)
    {
        LosslessMpvNative? native = null;
        NativeExportEvents? events = null;
        FileStream? source = null, staged = null;
        Exception? failure = null;
        CompatibleClipExporter.Probe? probe = null;
        var stage = "closed-app-guard";
        var clock = Stopwatch.StartNew();
        var cleanup = false;
        var sourceUnchanged = false;
        var exclusiveReopen = false;
        var destination = Path.Combine(output, "context-output.mp4");
        long sourceBytes = 0, outputBytes = 0;
        try
        {
            foreach (var name in new[] { "Wisp", "Wisp.Recorder", "ForzaHorizon6", "ForzaHorizon5", "ForzaHorizon4", "ForzaMotorsport" })
            {
                var processes = Process.GetProcessesByName(name);
                try { if (processes.Length != 0) throw new InvalidOperationException("Close Wisp and Forza before this diagnostic."); }
                finally { foreach (var process in processes) process.Dispose(); }
            }
            if (!Path.IsPathFullyQualified(sourcePath) || sourcePath.StartsWith(@"\\", StringComparison.Ordinal) ||
                string.Equals(sourcePath, destination, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Choose a local generated fixture.");
            ClipLibrary.CheckPath(sourcePath);
            ClipLibrary.CheckPath(destination);
            stage = "source-inspection";
            source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            sourceBytes = source.Length;
            if (sourceBytes is <= 0 or > 256 * 1024 * 1024) throw new InvalidDataException("The diagnostic requires a small fixture.");
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            probe = CompatibleClipExporter.Inspect(sourcePath, lifetime.Token);
            if (probe.Duration is <= 0 or > 1 || probe.Width > 3840 || probe.Height > 2160)
                throw new InvalidDataException("The diagnostic requires a short fixture.");
            var hdr = probe.Codec == "hevc" && probe.Gamma == "pq" && probe.Primaries == "bt.2020";
            var lossless = probe.Matrix == "rgb" && probe.Range == "full" &&
                (hdr ? LosslessClipPlayer.IsHdr444PixelFormat(probe.PixelFormat) : probe.PixelFormat == "gbrp");
            var rate = checked((int)Math.Round(probe.FrameRate));
            var clip = new ClipEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, new(60, probe.Height, rate, 75, LosslessVideo: lossless),
                new(sourceBytes, probe.Width, probe.Height, rate, 0, checked((long)(probe.Duration * 10_000_000)), probe.HasAudio,
                    LosslessVideo: lossless, HdrVideo: hdr));
            CompatibleClipExporter.Validate(clip, probe, compatible: false);
            if (!hdr && !lossless) throw new InvalidDataException("The fixture does not need conversion.");
            var transfer = hdr ? "iec61966-2-1" : probe.Gamma == "srgb" ? "iec61966-2-1" : "bt709";
            staged = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
            stage = "native-initialize";
            native = new LosslessMpvNative();
            events = new NativeExportEvents(native);
            native.InitializeHeadless(CompatibleClipExporter.EncodingOptions(destination, rate, probe.HasAudio, transfer,
                hdrVideo: hdr, losslessVideo: lossless, width: probe.Width, height: probe.Height));
            stage = "encoding";
            native.Run("loadfile", sourcePath, "replace");
            var quit = false;
            while (!events.Shutdown)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                events.Drain();
                if ((events.EndReason.HasValue || events.HasError) && !quit)
                {
                    native.Run("quit");
                    quit = true;
                }
                if (staged.Length > 128 * 1024 * 1024) throw new InvalidDataException("The diagnostic output exceeded its bound.");
                lifetime.Token.WaitHandle.WaitOne(10);
            }
            // Keep the complete bounded event batch, including errors queued
            // after END_FILE. Production error handling is not changed.
            events.Drain();
            stage = "native-close";
        }
        catch (Exception error)
        {
            failure = error;
            try { events?.Drain(); } catch { }
        }
        finally
        {
            try { native?.Close(); cleanup = true; }
            catch (Exception error) { failure ??= error; }
            staged?.Dispose();
            if (source is not null) { sourceUnchanged = source.Length == sourceBytes; source.Dispose(); }
        }
        if (cleanup && File.Exists(destination))
        {
            try
            {
                using var released = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.None);
                outputBytes = released.Length;
                exclusiveReopen = true;
            }
            catch (Exception error) { failure ??= error; }
        }
        File.WriteAllText(Path.Combine(output, "native-context.json"), JsonSerializer.Serialize(new
        {
            stage,
            probe,
            sourceBytes,
            outputBytes,
            sourceHeldReadOnly = source is not null,
            sourceUnchanged,
            cleanup,
            exclusiveReopen,
            elapsedSeconds = clock.Elapsed.TotalSeconds,
            exceptionType = failure?.GetType().Name,
            hresult = failure?.HResult,
            nativeCode = failure is LosslessMpvException mpv ? mpv.Code : null,
            eventCount = events?.Count,
            events?.EndReason,
            events?.EndError,
            events?.Shutdown,
            events?.HasError,
            events?.LogLimitReached,
            logs = events?.Logs
        }, new JsonSerializerOptions { WriteIndented = true }));
        return failure is null && events is { EndReason: 0, EndError: >= 0, HasError: false } && cleanup && exclusiveReopen ? 0 : 1;
    }

    // Diagnostic-only access to the same pinned client. It changes neither
    // options nor native ownership; only authored reason labels leave memory.
    private sealed class NativeExportEvents
    {
        private readonly IntPtr _handle;
        private readonly WaitEvent _wait;
        internal int Count { get; private set; }
        internal int? EndReason { get; private set; }
        internal int? EndError { get; private set; }
        internal bool Shutdown { get; private set; }
        internal bool HasError { get; private set; }
        internal bool LogLimitReached { get; private set; }
        internal List<SafeNativeLog> Logs { get; } = [];

        internal NativeExportEvents(LosslessMpvNative native)
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            _handle = (IntPtr)(typeof(LosslessMpvNative).GetField("_handle", flags)?.GetValue(native) ?? throw new InvalidDataException("Missing diagnostic handle."));
            var module = (IntPtr)(typeof(LosslessMpvNative).GetField("_module", flags)?.GetValue(native) ?? throw new InvalidDataException("Missing diagnostic module."));
            _wait = Marshal.GetDelegateForFunctionPointer<WaitEvent>(NativeLibrary.GetExport(module, "mpv_wait_event"));
        }

        internal void Drain()
        {
            for (var batch = 0; batch < 128; batch++)
            {
                if (Count >= 2048) throw new InvalidDataException("The diagnostic event bound was reached.");
                var item = Marshal.PtrToStructure<NativeEvent>(_wait(_handle, 0));
                if (item.Id == 0) return;
                Count++;
                if (item.Id == 1) Shutdown = true;
                if (item.Id == 24) throw new InvalidDataException("The native event queue overflowed.");
                if (item.Id == 7 && item.Data != IntPtr.Zero)
                {
                    EndReason = Marshal.ReadInt32(item.Data);
                    EndError = Marshal.ReadInt32(item.Data, 4);
                }
                if (item.Id != 2 || item.Data == IntPtr.Zero) continue;
                var log = Marshal.PtrToStructure<NativeLog>(item.Data);
                HasError |= log.Level <= 20;
                if (Logs.Count >= 64) { LogLimitReached = true; continue; }
                Logs.Add(Classify(Text(log.Prefix, 256), Text(log.Text, 8192), log.Level));
            }
        }

        private static SafeNativeLog Classify(string prefix, string message, int level)
        {
            string[] components = ["ffmpeg", "lavfi", "vf", "vo/lavc", "ao/lavc", "encode-lavc", "vd", "demux", "cplayer"];
            var component = components.FirstOrDefault(value => prefix.StartsWith(value, StringComparison.Ordinal)) ?? "other";
            (string Text, string Reason)[] reasons =
            [
                ("Undefined constant or missing", "undefined-option-constant"),
                ("Unable to parse option value", "option-value-parse"),
                ("Error setting option", "option-setting"),
                ("Error applying option", "option-application"),
                ("Option not found", "unknown-option"),
                ("No such filter", "missing-filter"),
                ("Error parsing", "filter-or-expression-parse"),
                ("Error initializing", "filter-or-encoder-initialize"),
                ("Failed to configure", "filter-configure"),
                ("Impossible to convert", "filter-format-conversion"),
                ("Failed to initialize", "initialize-failed"),
                ("Could not open codec", "codec-open"),
                ("Could not open encoder", "encoder-open"),
                ("Could not find encoder", "encoder-missing"),
                ("could not set output type", "mf-output-type"),
                ("could not set input type", "mf-input-type"),
                ("format negotiation failed", "mf-format-negotiation"),
                ("hardware MFT is not async", "mf-hardware-not-async"),
                ("could not get async interface", "mf-async-interface"),
                ("could not set async unlock", "mf-async-unlock"),
                ("failed processing input", "mf-process-input"),
                ("failed processing output", "mf-process-output"),
                ("could not start streaming", "mf-start-streaming"),
                ("could not start stream", "mf-start-stream"),
                ("Failed to create DXGI device manager", "mf-device-manager"),
                ("Failed to reset device", "mf-reset-device"),
                ("Failed to set D3D manager", "mf-d3d-manager"),
                ("DLL mfplat.dll failed to open", "mf-library-load"),
                ("No suitable transform", "mf-transform-unavailable"),
                ("No MFT", "mf-transform-unavailable"),
                ("Too many packets", "packet-queue-full"),
                ("Invalid argument", "invalid-argument"),
                ("Conversion failed", "conversion-failed")
            ];
            var reason = reasons.FirstOrDefault(value => message.Contains(value.Text, StringComparison.OrdinalIgnoreCase)).Reason ?? "unmapped";
            string[] symbols = ["profile", "high", "hw_encoding", "rate_control", "quality", "color_trc", "color_primaries", "colorspace",
                "h264_mf", "h264_nvenc", "geq", "zscale", "gbrpf32le", "yuv420p", "st(", "ld("];
            var mentioned = symbols.Where(value => message.Contains(value, StringComparison.Ordinal)).ToArray();
            string? hresult = null;
            if (reason.StartsWith("mf-", StringComparison.Ordinal))
            {
                var match = System.Text.RegularExpressions.Regex.Match(message, @"\b0x[0-9a-fA-F]{8}\b",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20));
                if (match.Success) hresult = match.Value.ToUpperInvariant();
            }
            return new(component, level, reason, mentioned, hresult);
        }

        private static string Text(IntPtr value, int maximum)
        {
            if (value == IntPtr.Zero) return string.Empty;
            var size = 0;
            while (size < maximum && Marshal.ReadByte(value, size) != 0) size++;
            if (size == maximum) return string.Empty;
            var bytes = new byte[size];
            Marshal.Copy(value, bytes, 0, size);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        [StructLayout(LayoutKind.Sequential)] private struct NativeEvent { internal int Id, Error; internal ulong Reply; internal IntPtr Data; }
        [StructLayout(LayoutKind.Sequential)] private struct NativeLog { internal IntPtr Prefix, LevelName, Text; internal int Level; }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr WaitEvent(IntPtr handle, double timeout);
    }

    private sealed record SafeNativeLog(string Component, int Level, string Reason, string[] Symbols, string? HResult);

    internal static int Capabilities(string output)
    {
        LosslessMpvNative? native = null;
        try
        {
            native = new LosslessMpvNative();
            native.InitializeHeadless();
            var encoders = native.ReadEncoderNames();
            native.Close(); native = null;
            File.WriteAllText(Path.Combine(output, "capabilities.json"), JsonSerializer.Serialize(new
            {
                encoders,
                mediaLoaded = false,
                graphicsInitialized = false,
                encoderActivated = false
            }, new JsonSerializerOptions { WriteIndented = true }));
            return encoders.Contains("h264_nvenc") && encoders.Contains("hevc_nvenc") && encoders.Contains("aac") ? 0 : 1;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(output, "failure.json"), JsonSerializer.Serialize(new
            { exceptionType = error.GetType().Name, error.HResult }));
            return 1;
        }
        finally { native?.Close(); }
    }
}
