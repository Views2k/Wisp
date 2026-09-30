using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Wisp.App.Clips;
using Xunit;

namespace Wisp.App.Tests;

public sealed class RecorderProcessClientTests
{
    private static readonly ClipRecordingSpec Recording = new(60, 1080, 60, 75);
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ConstructionAndDisabledDisposalNeverLaunchAHelper()
    {
        var calls = 0;
        await using (var client = new RecorderProcessClient(@"C:\Wisp\Wisp.Recorder.exe", _ => { calls++; throw new InvalidOperationException(); }))
        { await client.StopAsync(TestToken); }
        Assert.Equal(0, calls);
    }

    [Fact]
    public void LaunchUsesOneExactExecutableAndPrivateRedirectedPipes()
    {
        var start = RecorderProcessClient.StartInfo(@"C:\Wisp\Wisp.Recorder.exe");
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.True(start.RedirectStandardInput && start.RedirectStandardOutput && start.RedirectStandardError);
        Assert.Equal("--stdio-protocol", Assert.Single(start.ArgumentList));
        Assert.Empty(start.Arguments);
        Assert.Equal(@"C:\Wisp\Wisp.Recorder.exe", start.FileName);
    }

    [Fact]
    public async Task ConfigAndStartAcknowledgeWithoutFabricatingBuffering()
    {
        using var fixture = new Fixture();
        var child = new FakeChild();
        await using var client = new RecorderProcessClient(fixture.Helper, _ => child, fixture.BufferRoot);
        var observed = new List<RecorderStateUpdate>();
        client.StateChanged += (_, state) => observed.Add(state);
        await client.OpenAsync(Recording, fixture.Directory, TestToken);
        await client.StartAsync(new(42, 123, 456), TestToken);
        Assert.Empty(observed);
        Assert.Equal(new[] { "config", "start" }, child.Commands.Select(command => command.GetProperty("command").GetString()));
        var config = child.Commands[0];
        Assert.Equal(Path.Combine(fixture.BufferRoot, client.Session.ToString("N"), $".wisp-recorder-{client.Session:N}"), config.GetProperty("spoolDirectory").GetString());
        Assert.False(System.IO.Directory.Exists(config.GetProperty("spoolDirectory").GetString()));
        Assert.True(config.GetProperty("gameAudio").GetBoolean());
        Assert.False(config.GetProperty("borderlessAllowed").GetBoolean());
        Assert.Equal("123", child.Commands[1].GetProperty("window").GetString());
        await client.StopAsync(TestToken);
        Assert.True(child.Exited);
        Assert.Equal(0, child.Kills);
    }

    [Fact]
    public async Task ValidSilentSaveIsExplicitAndPreservesActualBounds()
    {
        using var fixture = new Fixture();
        var child = new FakeChild { SilentSave = true };
        await using var client = new RecorderProcessClient(fixture.Helper, _ => child, fixture.BufferRoot);
        await client.OpenAsync(Recording, fixture.Directory, TestToken);
        var id = Guid.NewGuid();
        var media = await client.SaveAsync(new(id, DateTimeOffset.UtcNow, Recording, Path.Combine(fixture.Directory, $"{id:N}.mp4")), TestToken);
        Assert.False(media.HasAudio);
        Assert.Equal(10_000_000, media.ActualStart100ns);
        Assert.Equal(20_000_000, media.ActualEnd100ns);
        Assert.Equal(1920, media.Width);
        Assert.Equal(60, media.FrameRate);
        Assert.Equal(new byte[1024], await File.ReadAllBytesAsync(Path.Combine(fixture.Directory, $"{id:N}.mp4"), TestToken));
        Assert.StartsWith(fixture.BufferRoot, child.Commands.Last().GetProperty("destination").GetString(), StringComparison.OrdinalIgnoreCase);
        await client.StopAsync(TestToken);
    }

    [Fact]
    public async Task DestinationRemovedAfterEnableKeepsTheFinishedPrivateClip()
    {
        using var fixture = new Fixture();
        var child = new FakeChild();
        await using var client = new RecorderProcessClient(fixture.Helper, _ => child, fixture.BufferRoot);
        await client.OpenAsync(Recording, fixture.Directory, TestToken);
        var id = Guid.NewGuid();
        Directory.Delete(fixture.Directory);
        var error = await Assert.ThrowsAsync<RecorderClientException>(() => client.SaveAsync(
            new(id, DateTimeOffset.UtcNow, Recording, Path.Combine(fixture.Directory, $"{id:N}.mp4")), TestToken));
        Assert.Equal("clip_publish_failed", error.Reason);
        var nativeSave = Assert.Single(child.Commands, command => command.GetProperty("command").GetString() == "save");
        Assert.Equal(new byte[1024], await File.ReadAllBytesAsync(nativeSave.GetProperty("destination").GetString()!, TestToken));
        await client.StopAsync(TestToken);
    }

    [Fact]
    public async Task TerminalRecoveryDoesNotBecomeUnexpectedExitOrSendAStopCommand()
    {
        using var fixture = new Fixture();
        var child = new FakeChild();
        await using var client = new RecorderProcessClient(fixture.Helper, _ => child, fixture.BufferRoot) { BorderlessAllowed = true };
        var recovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var states = new List<RecorderStateUpdate>();
        client.StateChanged += (_, state) => { states.Add(state); if (state.State == "reconnecting") recovery.TrySetResult(); };
        await client.OpenAsync(Recording, fixture.Directory, TestToken);
        Assert.True(child.Commands[0].GetProperty("borderlessAllowed").GetBoolean());
        child.Reconnect(client.Session);
        await recovery.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        await client.StopAsync(TestToken);
        Assert.DoesNotContain(states, state => state.Reason == "helper_exited");
        Assert.DoesNotContain(child.Commands, command => command.GetProperty("command").GetString() == "stop");
        Assert.Equal(0, child.Kills);
    }

    [Fact]
    public async Task TerminalOutputKeepsDelayedFirstDiagnosticBeforeDisposingOwnedPipe()
    {
        using var fixture = new Fixture();
        var second = RecorderFailureDiagnosticTests.Fields();
        second["reason"] = "stopped";
        var child = new FakeChild
        {
            DelayDiagnosticUntilWait = true,
            ExitDiagnostic = RecorderFailureDiagnosticTests.Line().Append((byte)10)
                .Concat(JsonSerializer.SerializeToUtf8Bytes(second)).Append((byte)10).ToArray()
        };
        await using var client = new RecorderProcessClient(fixture.Helper, _ => child, fixture.BufferRoot);
        var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StateChanged += (_, state) => { if (state.Reason == "helper_exited") terminal.TrySetResult(); };
        await client.OpenAsync(Recording, fixture.Directory, TestToken);
        child.CompleteOutput();
        await terminal.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        Assert.Null(client.FailureDiagnostic);
        await Assert.ThrowsAsync<RecorderClientException>(() => client.StopAsync(TestToken));
        Assert.Equal("encoder_failed", client.FailureDiagnostic?.Reason);
        Assert.Equal("video_submit", client.FailureDiagnostic?.Stage);
        Assert.True(child.Exited);
        Assert.Equal(1, child.Disposes);
        await client.DisposeAsync();
        Assert.Equal("encoder_failed", client.FailureDiagnostic?.Reason);
    }

    [Fact]
    public async Task WrongReservationPathNeverSendsSave()
    {
        using var fixture = new Fixture();
        var child = new FakeChild();
        await using var client = new RecorderProcessClient(fixture.Helper, _ => child, fixture.BufferRoot);
        await client.OpenAsync(Recording, fixture.Directory, TestToken);
        await Assert.ThrowsAsync<ArgumentException>(() => client.SaveAsync(new(Guid.NewGuid(), DateTimeOffset.UtcNow, Recording,
            Path.Combine(fixture.Directory, "unowned.mp4")), TestToken));
        Assert.Single(child.Commands);
    }

    [Fact]
    public async Task MismatchedResponseFailsClosedAndDoesNotRestart()
    {
        using var fixture = new Fixture();
        var child = new FakeChild { WrongRequest = true };
        var launches = 0;
        await using var client = new RecorderProcessClient(fixture.Helper, _ => { launches++; return child; }, fixture.BufferRoot);
        var failure = await Assert.ThrowsAsync<RecorderClientException>(() => client.OpenAsync(Recording, fixture.Directory, TestToken));
        Assert.Equal("protocol_error", failure.Reason);
        Assert.True(child.Kills > 0);
        Assert.Equal(1, launches);
    }

    [Fact]
    public async Task CancellationStopsExactOwnedChildAndFailsPendingSave()
    {
        using var fixture = new Fixture();
        var child = new FakeChild { HoldSave = true };
        await using var client = new RecorderProcessClient(fixture.Helper, _ => child, fixture.BufferRoot);
        await client.OpenAsync(Recording, fixture.Directory, TestToken);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        var id = Guid.NewGuid();
        var save = client.SaveAsync(new(id, DateTimeOffset.UtcNow, Recording, Path.Combine(fixture.Directory, $"{id:N}.mp4")), cancelled.Token);
        await child.SaveArrived.Task.WaitAsync(TimeSpan.FromSeconds(3), TestToken);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save);
        Assert.True(child.Exited);
        Assert.True(child.Kills > 0);
        Assert.Single(child.Commands, command => command.GetProperty("command").GetString() == "save");
    }

    [Theory]
    [InlineData("blank")]
    [InlineData("partial")]
    [InlineData("oversized")]
    public async Task LineReaderRejectsUnboundedOrIncompleteFrames(string kind)
    {
        var input = kind switch
        {
            "blank" => new byte[] { 10 },
            "partial" => Encoding.UTF8.GetBytes("{}"),
            _ => Enumerable.Repeat((byte)'x', RecorderProtocol.MaximumLineBytes + 1).Append((byte)10).ToArray()
        };
        await using var stream = new MemoryStream(input);
        var reader = new RecorderLineReader(stream);
        await Assert.ThrowsAsync<RecorderClientException>(() => reader.ReadAsync(TestToken));
    }

    [Fact]
    public async Task NonCancellationWaitFailureStillTerminatesAndConfirmsOwnedChild()
    {
        using var fixture = new Fixture();
        var child = new FakeChild { IgnoreClose = true, WaitFailuresRemaining = 1 };
        await using var client = new RecorderProcessClient(fixture.Helper, _ => child, fixture.BufferRoot);
        await client.OpenAsync(Recording, fixture.Directory, TestToken);
        await client.StopAsync(TestToken);
        Assert.Equal(1, child.Kills);
        Assert.Equal(2, child.WaitCalls);
        Assert.Equal(1, child.Disposes);
        Assert.True(child.Exited);
    }

    [Fact]
    public async Task UnconfirmedExitRetainsChildForLaterExplicitCleanup()
    {
        using var fixture = new Fixture();
        var child = new FakeChild { IgnoreClose = true, IgnoreKill = true, WaitFailuresRemaining = 2 };
        await using var client = new RecorderProcessClient(fixture.Helper, _ => child, fixture.BufferRoot);
        await client.OpenAsync(Recording, fixture.Directory, TestToken);
        var failure = await Assert.ThrowsAsync<RecorderClientException>(() => client.StopAsync(TestToken));
        Assert.Equal("helper_shutdown_failed", failure.Reason);
        Assert.Equal(0, child.Disposes);
        Assert.False(child.Exited);
        child.ExitOnNextWait = true;
        await client.DisposeAsync();
        Assert.Equal(3, child.WaitCalls);
        Assert.Equal(1, child.Disposes);
        Assert.True(child.Exited);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("wrong-session")]
    [InlineData("wrong-version")]
    [InlineData("unknown-reason")]
    [InlineData("wrong-state-request")]
    public void ProtocolRejectsMalformedIdentityOrSchema(string kind)
    {
        var session = Guid.NewGuid();
        var fields = new Dictionary<string, object>
        { ["v"] = 1, ["session"] = session.ToString("N"), ["request"] = 1, ["type"] = "result", ["ok"] = true, ["reason"] = "none" };
        if (kind == "unknown") fields["privatePath"] = "not-allowed";
        if (kind == "wrong-session") fields["session"] = Guid.NewGuid().ToString("N");
        if (kind == "wrong-version") fields["v"] = 2;
        if (kind == "unknown-reason") fields["reason"] = "raw-error";
        if (kind == "wrong-state-request") { fields.Remove("ok"); fields["type"] = "state"; fields["state"] = "buffering"; }
        var json = JsonSerializer.Serialize(fields);
        if (kind == "duplicate") json = json[..^1] + ",\"v\":1}";
        Assert.Equal("protocol_error", Assert.Throws<RecorderClientException>(() => RecorderProtocol.Decode(Encoding.UTF8.GetBytes(json), session)).Reason);
    }

    [Fact]
    public void SilentSaveCannotClaimOrdinarySuccessReason()
    {
        var session = Guid.NewGuid();
        var result = FakeChild.Saved(session.ToString("N"), 1, Guid.NewGuid().ToString("N"), true);
        result["reason"] = "none";
        Assert.Throws<RecorderClientException>(() => RecorderProtocol.Decode(JsonSerializer.SerializeToUtf8Bytes(result), session));
    }

    [Theory]
    [InlineData("reconnecting", "none")]
    [InlineData("reconnecting", "window_minimized")]
    [InlineData("paused", "unsupported_format")]
    [InlineData("preparing", "none")]
    public void UnsupportedTerminalStatePairsCannotSuppressAnUnexpectedExit(string state, string reason)
    {
        var session = Guid.NewGuid();
        var line = JsonSerializer.SerializeToUtf8Bytes(new { v = 1, session = session.ToString("N"), request = 0, type = "state", state, reason });
        var error = Assert.Throws<RecorderClientException>(() => RecorderProtocol.Decode(line, session));
        Assert.Equal("protocol_error", error.Reason);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "WispRecorderClientTests", Guid.NewGuid().ToString("N"));
        internal string Directory => Path.Combine(_root, "clips");
        internal string Helper => Path.Combine(Directory, "Wisp.Recorder.exe");
        internal string BufferRoot => Path.Combine(_root, "private-buffer");
        internal Fixture() => System.IO.Directory.CreateDirectory(Directory);
        public void Dispose() => System.IO.Directory.Delete(_root, true);
    }

    private sealed class FakeChild : IRecorderChild
    {
        private readonly PipeStream _output = new(), _error;
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _waitObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<JsonElement> Commands = [];
        internal readonly TaskCompletionSource SaveArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool SilentSave { get; init; }
        internal bool WrongRequest { get; init; }
        internal bool HoldSave { get; init; }
        internal bool DelayDiagnosticUntilWait { get; init; }
        internal byte[]? ExitDiagnostic { get; init; }
        internal int Kills { get; private set; }
        internal int WaitCalls { get; private set; }
        internal int Disposes { get; private set; }
        internal int WaitFailuresRemaining { get; set; }
        internal bool IgnoreClose { get; init; }
        internal bool IgnoreKill { get; init; }
        internal bool ExitOnNextWait { get; set; }
        internal bool Exited => _exit.Task.IsCompleted;
        public Stream Input { get; }
        public Stream Output => _output;
        public Stream Error => _error;
        internal FakeChild()
        {
            _error = new(async token => { if (DelayDiagnosticUntilWait) await _waitObserved.Task.WaitAsync(token); });
            Input = new CommandStream(Receive, () => { if (!IgnoreClose) Finish(); });
        }
        internal void CompleteOutput() => _output.Complete();
        internal void Reconnect(Guid session)
        {
            _output.Push(JsonSerializer.SerializeToUtf8Bytes(new { v = 1, session = session.ToString("N"), request = 0, type = "state", state = "reconnecting", reason = "capture_reconnecting" }).Append((byte)10).ToArray());
            Finish();
        }
        private void Receive(byte[] bytes)
        {
            using var document = JsonDocument.Parse(bytes);
            var command = document.RootElement.Clone(); Commands.Add(command);
            var name = command.GetProperty("command").GetString();
            if (name == "save") { SaveArrived.TrySetResult(); if (HoldSave) return; }
            var request = command.GetProperty("request").GetInt64() + (WrongRequest ? 1 : 0);
            var session = command.GetProperty("session").GetString()!;
            if (name == "save") File.WriteAllBytes(command.GetProperty("destination").GetString()!, new byte[1024]);
            var reply = name == "save" ? Saved(session, request, command.GetProperty("clipId").GetString()!, SilentSave) :
                new Dictionary<string, object> { ["v"] = 1, ["session"] = session, ["request"] = request, ["type"] = "result", ["ok"] = true, ["reason"] = "none" };
            _output.Push(JsonSerializer.SerializeToUtf8Bytes(reply).Append((byte)10).ToArray());
        }
        internal static Dictionary<string, object> Saved(string session, long request, string id, bool silent) => new()
        {
            ["v"] = 1,
            ["session"] = session,
            ["request"] = request,
            ["type"] = "result",
            ["ok"] = true,
            ["reason"] = silent ? "audio_unavailable" : "none",
            ["clipId"] = id,
            ["fileBytes"] = 1024,
            ["width"] = 1920,
            ["height"] = 1080,
            ["frameRate"] = 60,
            ["start100ns"] = 10_000_000L,
            ["end100ns"] = 20_000_000L,
            ["hasAudio"] = !silent
        };
        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            WaitCalls++;
            _waitObserved.TrySetResult();
            if (WaitFailuresRemaining > 0) { WaitFailuresRemaining--; throw new InvalidOperationException("Synthetic wait failure"); }
            if (ExitOnNextWait) Finish();
            return _exit.Task.WaitAsync(cancellationToken);
        }
        public void Kill() { Kills++; if (!IgnoreKill) Finish(); }
        private void Finish()
        {
            _output.Complete();
            if (!Exited && ExitDiagnostic is { } diagnostic) _error.Push(diagnostic);
            _error.Complete(); _exit.TrySetResult();
        }
        public void Dispose() { Disposes++; Finish(); Input.Dispose(); _output.Dispose(); _error.Dispose(); }
    }

    private sealed class CommandStream(Action<byte[]> command, Action closed) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            foreach (var value in buffer.AsSpan(offset, count))
            {
                if (value == 10) { command(ToArray()); SetLength(0); Position = 0; }
                else base.WriteByte(value);
            }
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Write(buffer.ToArray(), 0, buffer.Length); return ValueTask.CompletedTask; }
        protected override void Dispose(bool disposing) { if (disposing) closed(); base.Dispose(disposing); }
    }

    private sealed class PipeStream(Func<CancellationToken, Task>? beforeRead = null) : Stream
    {
        private readonly Channel<byte[]> _data = Channel.CreateUnbounded<byte[]>();
        private byte[]? _current;
        private int _position;
        internal void Push(byte[] bytes) => _data.Writer.TryWrite(bytes);
        internal void Complete() => _data.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (beforeRead is not null) await beforeRead(cancellationToken);
            if (_current is null || _position == _current.Length)
            {
                try { _current = await _data.Reader.ReadAsync(cancellationToken); _position = 0; }
                catch (ChannelClosedException) { return 0; }
            }
            var count = Math.Min(buffer.Length, _current.Length - _position);
            _current.AsMemory(_position, count).CopyTo(buffer); _position += count; return count;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
