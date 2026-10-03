using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Wisp.App.Clips;

internal sealed record RecorderTarget(uint ProcessId, ulong Window, ulong CreationFileTime);
internal sealed record RecorderLosslessBuffer(long Duration100ns, long PayloadBytes, long BudgetBytes, bool SizeLimited);
internal sealed record RecorderStateUpdate(string State, string Reason, RecorderLosslessBuffer? LosslessBuffer = null, bool BufferReady = false);
internal sealed record RecorderReply(long Request, bool Ok, string Reason, Guid? ClipId = null, FinalizedClipMedia? Media = null);
internal sealed class RecorderClientException(string reason) : IOException("The clip recorder could not complete the operation.")
{
    internal string Reason { get; } = reason;
    internal string? StorageStage { get; init; }
    internal int? StorageHResult { get; init; }
    internal bool NativeRequestRejected { get; init; }
    internal bool SaveCompletedWithoutMedia { get; init; }
}

internal static class RecorderProtocol
{
    internal const int Version = 2;
    internal const int MaximumLineBytes = 16 * 1024;
    internal const int MaximumStderrBytes = 16 * 1024;
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    internal static readonly IReadOnlySet<string> Reasons = new HashSet<string>(StringComparer.Ordinal)
    {
        "none", "waiting_for_game", "target_exited", "target_changed", "window_closed", "window_minimized",
        "window_resized", "focus_lost", "fullscreen_required", "unsupported_os", "unsupported_gpu", "unsupported_format", "capture_failed",
        "encoder_failed", "audio_failed", "audio_capture_failed", "audio_unavailable", "buffer_full", "no_keyframe",
        "not_ready", "save_in_progress", "storage_failed", "mux_failed", "protocol_error", "cancelled", "stopped", "parent_closed",
        "capture_stale", "capture_reconnecting", "encoder_reconnecting", "audio_reconnecting", "scheduler_late", "cleanup_failed", "lossless_storage_low", "lossless_encoder_unsupported", "hdr_encoder_unsupported"
    };
    private static readonly HashSet<string> States = new(StringComparer.Ordinal) { "waiting", "reconnecting", "paused", "buffering", "saving", "stopped", "error" };

    internal static Dictionary<string, object> Command(Guid session, long request, string command) => new()
    { ["v"] = Version, ["session"] = session.ToString("N"), ["request"] = request, ["command"] = command };

    internal static byte[] Encode(Dictionary<string, object> command)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(command);
        if (bytes.Length > MaximumLineBytes) throw new RecorderClientException("protocol_error");
        return bytes;
    }

    internal static object Decode(ReadOnlyMemory<byte> line, Guid session)
    {
        try
        {
            if (line.Length is 0 or > MaximumLineBytes) throw new FormatException();
            using var document = JsonDocument.Parse(line, new() { MaxDepth = 6 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject()) if (!names.Add(property.Name)) throw new FormatException();
            if (root.GetProperty("v").GetInt32() != Version || !Guid.TryParseExact(root.GetProperty("session").GetString(), "N", out var actual) || actual != session)
                throw new FormatException();
            var request = root.GetProperty("request").GetInt64();
            var reason = root.GetProperty("reason").GetString() ?? "";
            if (!Reasons.Contains(reason)) throw new FormatException();
            var type = root.GetProperty("type").GetString();
            if (type == "state")
            {
                var hasBuffer = names.Contains("losslessBuffer");
                var state = root.GetProperty("state").GetString() ?? "";
                var expected = new List<string> { "v", "session", "request", "type", "state", "reason" };
                if (hasBuffer) expected.Add("losslessBuffer");
                if (state == "paused") expected.Add("bufferReady");
                RequireMembers(names, expected.ToArray());
                var bufferReady = state == "paused" && root.GetProperty("bufferReady").GetBoolean();
                if (request != 0 || !States.Contains(state)) throw new FormatException();
                if (state == "paused" && reason is not ("window_minimized" or "focus_lost" or "fullscreen_required") || state == "reconnecting" && reason is not
                    ("target_exited" or "target_changed" or "window_closed" or "window_resized" or "capture_reconnecting" or
                     "encoder_reconnecting" or "audio_reconnecting" or "scheduler_late")) throw new FormatException();
                RecorderLosslessBuffer? buffer = null;
                if (hasBuffer)
                {
                    if (state != "buffering" && !(state == "paused" && bufferReady)) throw new FormatException();
                    var value = root.GetProperty("losslessBuffer");
                    var fields = value.EnumerateObject().Select(property => property.Name).ToArray();
                    if (fields.Length != 4) throw new FormatException();
                    RequireMembers(new HashSet<string>(fields, StringComparer.Ordinal), "duration100ns", "payloadBytes", "budgetBytes", "sizeLimited");
                    buffer = new(value.GetProperty("duration100ns").GetInt64(), value.GetProperty("payloadBytes").GetInt64(),
                        value.GetProperty("budgetBytes").GetInt64(), value.GetProperty("sizeLimited").GetBoolean());
                    if (buffer.Duration100ns is <= 0 or > 3_000_000_000L || buffer.PayloadBytes <= 0 ||
                        buffer.PayloadBytes > buffer.BudgetBytes || buffer.BudgetBytes > 12L * 1024 * 1024 * 1024 + 16L * 1024 * 1024)
                        throw new FormatException();
                }
                return new RecorderStateUpdate(state, reason, buffer, bufferReady);
            }
            if (type != "result" || request <= 0) throw new FormatException();
            var ok = root.GetProperty("ok").GetBoolean();
            if (!names.Contains("clipId"))
            {
                RequireMembers(names, "v", "session", "request", "type", "ok", "reason");
                if (ok ? reason != "none" : reason == "none") throw new FormatException();
                return new RecorderReply(request, ok, reason);
            }
            var hdrVideo = names.Remove("hdrVideo") && root.GetProperty("hdrVideo").GetBoolean();
            RequireMembers(names, "v", "session", "request", "type", "ok", "reason", "clipId", "fileBytes", "width", "height", "frameRate", "start100ns", "end100ns", "hasAudio", "losslessVideo", "sizeLimited");
            if (!ok || !Guid.TryParseExact(root.GetProperty("clipId").GetString(), "N", out var id) || id == Guid.Empty) throw new FormatException();
            var media = new FinalizedClipMedia(root.GetProperty("fileBytes").GetInt64(), root.GetProperty("width").GetInt32(),
                root.GetProperty("height").GetInt32(), root.GetProperty("frameRate").GetInt32(),
                root.GetProperty("start100ns").GetInt64(), root.GetProperty("end100ns").GetInt64(), root.GetProperty("hasAudio").GetBoolean(),
                root.GetProperty("losslessVideo").GetBoolean(), root.GetProperty("sizeLimited").GetBoolean(), hdrVideo);
            if (media.SizeLimited && !media.LosslessVideo) throw new FormatException();
            if (media.HasAudio ? reason != "none" : reason != "audio_unavailable") throw new FormatException();
            if (media.FileBytes is <= 0 or > ClipLibrary.MaximumMediaBytes || !ClipsSettings.ResolutionChoices.Contains(media.Height) ||
                media.Width != (media.Height == 480 ? 854 : media.Height * 16 / 9) || !ClipsSettings.FrameRateChoices.Contains(media.FrameRate) ||
                media.ActualStart100ns < 0 || media.ActualEnd100ns <= media.ActualStart100ns || media.ActualEnd100ns - media.ActualStart100ns > 3_000_000_000L)
                throw new FormatException();
            return new RecorderReply(request, true, reason, id, media);
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or OverflowException)
        { throw new RecorderClientException("protocol_error"); }
    }

    private static void RequireMembers(HashSet<string> actual, params string[] expected)
    {
        if (!actual.SetEquals(expected)) throw new FormatException();
    }

    internal static void ValidateRecording(ClipRecordingSpec recording)
    {
        ArgumentNullException.ThrowIfNull(recording);
        if (!ClipsSettings.LengthChoices.Contains(recording.LengthSeconds) || !ClipsSettings.ResolutionChoices.Contains(recording.ResolutionHeight) ||
            !ClipsSettings.FrameRateChoices.Contains(recording.FrameRate) || recording.Quality is < 10 or > 100)
            throw new ArgumentException("Choose supported clip recording settings.", nameof(recording));
    }
}

// Fixed buffers ensure a child cannot force ReadLineAsync to accumulate an unbounded string.
internal sealed class RecorderLineReader(Stream stream)
{
    private readonly byte[] _read = new byte[1024];
    private readonly byte[] _line = new byte[RecorderProtocol.MaximumLineBytes];
    private int _offset, _available, _length;
    internal async Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_offset == _available)
            {
                _available = await stream.ReadAsync(_read, cancellationToken).ConfigureAwait(false);
                _offset = 0;
                if (_available == 0)
                {
                    if (_length != 0) throw new RecorderClientException("protocol_error");
                    return null;
                }
            }
            var value = _read[_offset++];
            if (value == (byte)'\n')
            {
                if (_length > 0 && _line[_length - 1] == (byte)'\r') _length--;
                if (_length == 0) throw new RecorderClientException("protocol_error");
                var complete = _line.AsSpan(0, _length).ToArray();
                _length = 0;
                return complete;
            }
            if (_length == _line.Length) throw new RecorderClientException("protocol_error");
            _line[_length++] = value;
        }
    }
}

internal interface IRecorderChild : IDisposable
{
    Stream Input { get; }
    Stream Output { get; }
    Stream Error { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken);
    void Kill();
}

internal sealed class RecorderProcessChild : IRecorderChild
{
    private readonly Process _process;
    internal RecorderProcessChild(ProcessStartInfo start)
    {
        _process = new() { StartInfo = start };
        try { if (!_process.Start()) throw new RecorderClientException("helper_start_failed"); }
        catch { _process.Dispose(); throw; }
    }
    public Stream Input => _process.StandardInput.BaseStream;
    public Stream Output => _process.StandardOutput.BaseStream;
    public Stream Error => _process.StandardError.BaseStream;
    public Task WaitForExitAsync(CancellationToken cancellationToken) => _process.WaitForExitAsync(cancellationToken);
    public void Kill() { try { if (!_process.HasExited) _process.Kill(); } catch (InvalidOperationException) { } }
    public void Dispose() => _process.Dispose();
}

// One owned helper, serialized command writes and bounded pending replies. Pause
// and resume remain responsive while a save is writing; media never crosses IPC.
// The controller adapts observed native states to IClipRecorder; acknowledgments
// here intentionally do not fabricate buffering or a completed saved clip.
internal sealed class RecorderProcessClient : IAsyncDisposable
{
    private readonly string _helperPath;
    private readonly Func<ProcessStartInfo, IRecorderChild> _launch;
    private readonly string? _bufferRoot;
    private readonly SemaphoreSlim _commands = new(1, 1), _shutdown = new(1, 1), _saves = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _token;
    private readonly object _sync = new();
    private IRecorderChild? _child;
    private Task? _output, _error;
    private readonly Dictionary<long, TaskCompletionSource<RecorderReply>> _pending = new();
    private long _nextRequest;
    private bool _stopping, _configured;
    private int _opened, _disposed, _lifetimeDisposed;
    private string? _terminalReason, _storage;
    private ClipRecordingSpec? _recording;
    private RecorderFailureDiagnostic? _failureDiagnostic;
    private ClipBufferStore? _buffer;
    private bool _recovering;
    internal Guid Session { get; } = Guid.NewGuid();
    internal bool BorderlessAllowed { get; set; }
    internal ClipBufferCleanup? StartupCleanup { get; private set; }
    internal RecorderFailureDiagnostic? FailureDiagnostic => Volatile.Read(ref _failureDiagnostic);
    internal event EventHandler<RecorderStateUpdate>? StateChanged;

    internal RecorderProcessClient(string installedHelperPath, Func<ProcessStartInfo, IRecorderChild>? launch = null, string? bufferRoot = null)
    {
        if (!Path.IsPathFullyQualified(installedHelperPath) || !string.Equals(Path.GetExtension(installedHelperPath), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The installed recorder helper path is invalid.", nameof(installedHelperPath));
        _helperPath = Path.GetFullPath(installedHelperPath);
        _launch = launch ?? (start => new RecorderProcessChild(start));
        _bufferRoot = bufferRoot;
        _token = _lifetime.Token;
    }

    internal static ProcessStartInfo StartInfo(string path)
    {
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = RecorderProtocol.Utf8,
            StandardOutputEncoding = RecorderProtocol.Utf8,
            StandardErrorEncoding = RecorderProtocol.Utf8,
            WorkingDirectory = Path.GetDirectoryName(path)!
        };
        start.ArgumentList.Add("--stdio-protocol");
        return start;
    }

    internal async Task OpenAsync(ClipRecordingSpec recording, string selectedStorage, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        RecorderProtocol.ValidateRecording(recording);
        if (!ClipsSettings.TryNormalizeStorageDirectory(selectedStorage, out var storage) || !Directory.Exists(storage))
            throw new ArgumentException("Choose an existing clip library folder.", nameof(selectedStorage));
        ClipLibrary.CheckPath(storage);
        if (Interlocked.Exchange(ref _opened, 1) != 0) throw new InvalidOperationException("The recorder client cannot be reopened.");
        _storage = storage; _recording = recording;
        try
        {
            _buffer = await ClipBufferStore.OpenAsync(Session, _bufferRoot, cancellationToken).ConfigureAwait(false);
            StartupCleanup = _buffer.StartupCleanup;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                _child = _launch(StartInfo(_helperPath));
                _output = ReadOutputAsync(_child.Output);
                _error = DrainErrorAsync(_child.Error);
            }
            await SendAsync("config", new()
            {
                ["durationSeconds"] = recording.LengthSeconds,
                ["height"] = recording.ResolutionHeight,
                ["frameRate"] = recording.FrameRate,
                ["quality"] = recording.Quality,
                ["losslessVideo"] = recording.LosslessVideo,
                ["preserveHdrRecording"] = recording.PreserveHdrRecording,
                ["gameAudio"] = true,
                ["systemAudio"] = recording.CaptureSystemAudio,
                ["spoolDirectory"] = _buffer.SpoolDirectory,
                ["borderlessAllowed"] = BorderlessAllowed
            }, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                if (_stopping || Volatile.Read(ref _disposed) != 0 || _child is null) throw new RecorderClientException("stopped");
                _configured = true;
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            await ShutdownAsync().ConfigureAwait(false);
            if (error is RecorderClientException or OperationCanceledException) throw;
            throw new RecorderClientException("helper_start_failed");
        }
    }

    internal Task StartAsync(RecorderTarget target, CancellationToken cancellationToken) => SendTargetAsync("start", target, cancellationToken);

    internal Task ResumeAsync(RecorderTarget target, CancellationToken cancellationToken) => SendTargetAsync("resume", target, cancellationToken);

    internal Task PauseAsync(CancellationToken cancellationToken)
    {
        EnsureConfigured();
        return SendAsync("pause", new(), TimeSpan.FromSeconds(15), cancellationToken);
    }

    private Task SendTargetAsync(string command, RecorderTarget target, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        if (target.ProcessId == 0 || target.Window == 0 || target.CreationFileTime == 0) throw new ArgumentException("The game capture identity is invalid.", nameof(target));
        return SendAsync(command, new()
        {
            ["processId"] = target.ProcessId,
            ["window"] = target.Window.ToString(CultureInfo.InvariantCulture),
            ["creationFileTime"] = target.CreationFileTime.ToString(CultureInfo.InvariantCulture)
        }, TimeSpan.FromSeconds(15), cancellationToken);
    }

    internal async Task<FinalizedClipMedia> SaveAsync(ClipSaveTarget target, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        ArgumentNullException.ThrowIfNull(target);
        var expected = Path.Combine(_storage!, $"{target.Id:N}.mp4");
        if (target.Id == Guid.Empty || target.Recording != _recording || !string.Equals(target.MediaPath, expected, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The clip destination does not match its library reservation.", nameof(target));
        if (!await _saves.WaitAsync(0, cancellationToken).ConfigureAwait(false)) throw new RecorderClientException("save_in_progress");
        using var publication = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _token);
        publication.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            var buffer = _buffer ?? throw new RecorderClientException("buffer_storage_unavailable");
            RecorderReply result;
            try
            {
                result = await SendAsync("save", new() { ["clipId"] = target.Id.ToString("N"), ["destination"] = buffer.PrivateMediaPath(target.Id) },
                    TimeSpan.FromMinutes(5), publication.Token).ConfigureAwait(false);
            }
            catch (RecorderClientException error) when (error.NativeRequestRejected && error.Reason is not ("cancelled" or "stopped" or "parent_closed"))
            {
                var empty = false;
                try { empty = await buffer.IsEmptyAsync(target.Id, publication.Token).ConfigureAwait(false); }
                catch (Exception checkError) when (checkError is not OutOfMemoryException) { }
                throw new RecorderClientException(error.Reason) { NativeRequestRejected = true, SaveCompletedWithoutMedia = empty };
            }
            var media = result.Media;
            if (result.ClipId != target.Id || media is null || media.Height != _recording!.ResolutionHeight || media.FrameRate != _recording.FrameRate)
            {
                Fail("protocol_error");
                await ShutdownAsync().ConfigureAwait(false);
                throw new RecorderClientException("protocol_error");
            }
            return await buffer.PublishAsync(target, media, publication.Token).ConfigureAwait(false);
        }
        finally { _saves.Release(); }
    }

    internal async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _opened) == 0) return;
        lock (_sync) _stopping = true;
        try { if (_configured) await SendAsync("stop", [], TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); }
        finally { await ShutdownAsync().ConfigureAwait(false); }
    }

    private void EnsureConfigured()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_configured) throw new InvalidOperationException("The recorder session is not configured.");
    }

    private async Task<RecorderReply> SendAsync(string command, Dictionary<string, object> fields, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _token);
        operation.CancelAfter(timeout);
        var entered = false;
        try
        {
            await _commands.WaitAsync(operation.Token).ConfigureAwait(false); entered = true;
            TaskCompletionSource<RecorderReply> pending;
            IRecorderChild child;
            long request;
            lock (_sync)
            {
                if (_terminalReason is not null) throw new RecorderClientException(_terminalReason);
                if (_stopping && command != "stop") throw new RecorderClientException("stopped");
                child = _child ?? throw new RecorderClientException("helper_exited");
                request = checked(++_nextRequest);
                pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_pending.Count >= 3) throw new RecorderClientException("protocol_error");
                _pending.Add(request, pending);
            }
            var message = RecorderProtocol.Command(Session, request, command);
            foreach (var field in fields) message.Add(field.Key, field.Value);
            var bytes = RecorderProtocol.Encode(message);
            await child.Input.WriteAsync(bytes, operation.Token).ConfigureAwait(false);
            await child.Input.WriteAsync(new byte[] { (byte)'\n' }, operation.Token).ConfigureAwait(false);
            await child.Input.FlushAsync(operation.Token).ConfigureAwait(false);
            _commands.Release(); entered = false;
            var reply = await pending.Task.WaitAsync(operation.Token).ConfigureAwait(false);
            if (!reply.Ok) throw new RecorderClientException(reply.Reason) { NativeRequestRejected = true };
            if (command != "save" && reply.Media is not null) throw new RecorderClientException("protocol_error");
            return reply;
        }
        catch (OperationCanceledException)
        {
            Fail(cancellationToken.IsCancellationRequested ? "cancelled" : "helper_timeout");
            await ShutdownAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new RecorderClientException(_terminalReason ?? "helper_timeout");
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or OverflowException)
        {
            // A native refusal is explicit and may be recoverable (e.g. no keyframe).
            // Transport/protocol failures close the session; never retry the save.
            if (error is RecorderClientException native && RecorderProtocol.Reasons.Contains(native.Reason) && native.Reason != "protocol_error") throw;
            Fail(error is RecorderClientException known ? known.Reason : "protocol_error");
            await ShutdownAsync().ConfigureAwait(false);
            throw new RecorderClientException(_terminalReason ?? "protocol_error");
        }
        finally { if (entered) _commands.Release(); }
    }

    private async Task ReadOutputAsync(Stream output)
    {
        try
        {
            var lines = new RecorderLineReader(output);
            while (await lines.ReadAsync(_token).ConfigureAwait(false) is { } line)
            {
                var message = RecorderProtocol.Decode(line, Session);
                if (message is RecorderStateUpdate state)
                {
                    if (state.LosslessBuffer is not null && _recording?.LosslessVideo != true)
                        throw new RecorderClientException("protocol_error");
                    // Native sends this terminal transition only after closing the old media session.
                    // Preserve that reason when its following EOF arrives; the service owns recovery.
                    if (state.State == "reconnecting")
                    {
                        lock (_sync)
                        {
                            _recovering = true; _configured = false;
                            _terminalReason ??= state.Reason;
                            FailPending(state.Reason);
                        }
                    }
                    StateChanged?.Invoke(this, state); continue;
                }
                var reply = (RecorderReply)message;
                TaskCompletionSource<RecorderReply>? pending;
                lock (_sync)
                {
                    if (!_pending.Remove(reply.Request, out pending)) throw new RecorderClientException("protocol_error");
                }
                pending.TrySetResult(reply);
            }
            lock (_sync)
            {
                if (_recovering)
                {
                    FailPending("capture_reconnecting");
                    _configured = false;
                    return;
                }
                if (_stopping && _pending.Count == 0) return;
            }
            Fail("helper_exited");
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException) { Fail("protocol_error"); }
    }

    private async Task DrainErrorAsync(Stream error)
    {
        try
        {
            var bytes = new byte[1024];
            var line = new byte[RecorderFailureDiagnostic.MaximumBytes];
            var length = 0;
            var oversized = false;
            var total = 0;
            int count;
            // This owned pipe must drain after terminal stdout cancels normal work.
            // Helper exit and the existing bounded shutdown close it.
            while ((count = await error.ReadAsync(bytes).ConfigureAwait(false)) != 0)
            {
                total += count;
                if (total > RecorderProtocol.MaximumStderrBytes) { Fail("protocol_error"); return; }
                for (var index = 0; index < count; index++)
                {
                    var value = bytes[index];
                    if (value == (byte)'\n')
                    {
                        if (!oversized && RecorderFailureDiagnostic.Parse(line.AsMemory(0, length)) is { } diagnostic)
                            Interlocked.CompareExchange(ref _failureDiagnostic, diagnostic, null);
                        length = 0; oversized = false;
                    }
                    else if (length < line.Length) line[length++] = value;
                    else oversized = true;
                }
            }
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception errorRead) when (errorRead is not OutOfMemoryException) { Fail("protocol_error"); }
    }

    private void Fail(string reason)
    {
        IRecorderChild? child;
        lock (_sync)
        {
            if (_terminalReason is not null || (_stopping && _pending.Count == 0)) return;
            _terminalReason = reason;
            FailPending(reason);
            child = _child;
        }
        _lifetime.Cancel();
        try { child?.Kill(); } catch (Exception error) when (error is not OutOfMemoryException) { }
        StateChanged?.Invoke(this, new("error", reason));
    }

    // Caller owns _sync. Continuations always run outside this lock.
    private void FailPending(string reason)
    {
        foreach (var pending in _pending.Values) pending.TrySetException(new RecorderClientException(reason));
        _pending.Clear();
    }

    private async Task ShutdownAsync()
    {
        await _shutdown.WaitAsync().ConfigureAwait(false);
        try
        {
            IRecorderChild? child;
            lock (_sync)
            {
                _stopping = true; _configured = false; child = _child;
                FailPending("stopped");
            }
            if (child is null)
            {
                if (_buffer is { } unopened) { await unopened.DisposeAsync().ConfigureAwait(false); _buffer = null; }
                return;
            }
            try { child.Input.Close(); } catch (Exception error) when (error is IOException or ObjectDisposedException) { }
            using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var exited = false;
            try { await child.WaitForExitAsync(exit.Token).ConfigureAwait(false); exited = true; }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                try { child.Kill(); } catch (Exception killError) when (killError is not OutOfMemoryException) { }
                using var killed = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await child.WaitForExitAsync(killed.Token).ConfigureAwait(false); exited = true; }
                catch (Exception waitError) when (waitError is not OutOfMemoryException) { }
            }
            finally
            {
                _lifetime.Cancel();
                if (exited)
                {
                    lock (_sync) { if (ReferenceEquals(_child, child)) _child = null; }
                }
                var readers = Task.WhenAll(_output ?? Task.CompletedTask, _error ?? Task.CompletedTask);
                try { await readers.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (Exception error) when (error is not OutOfMemoryException) { }
                finally
                {
                    if (exited)
                    {
                        child.Dispose();
                        if (_buffer is { } finished) { await finished.DisposeAsync().ConfigureAwait(false); _buffer = null; }
                    }
                }
            }
            if (!exited) throw new RecorderClientException("helper_shutdown_failed");
        }
        finally { _shutdown.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        await ShutdownAsync().ConfigureAwait(false);
        if (Interlocked.Exchange(ref _lifetimeDisposed, 1) == 0) _lifetime.Dispose();
    }
}
