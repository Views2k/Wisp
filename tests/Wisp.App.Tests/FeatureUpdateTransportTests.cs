using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using Wisp.Update;
using Xunit;

namespace Wisp.App.Tests;

public sealed class FeatureUpdateTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PausingCancelsAnInstallerStreamAndRestoreDoesNotResumeIt(bool restoreBeforeCompletion)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Wisp.App.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var controller = Controller();
            using var stream = new PausedDownloadStream();
            using var handler = new DownloadHandler(stream);
            using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using var client = (WispUpdateClient)Activator.CreateInstance(typeof(WispUpdateClient),
                BindingFlags.Instance | BindingFlags.NonPublic, null, [http, null, null, null], null)!;
            var version = new SemanticVersion(9, 8, 7);
            var release = (UpdateRelease)Activator.CreateInstance(typeof(UpdateRelease),
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                [version, "v9.8.7", "Wisp-Setup-9.8.7.exe", 65536L, new string('a', 64),
                    new Uri("https://github.com/Views2k/Wisp/releases/download/v9.8.7/Wisp-Setup-9.8.7.exe"), "Fixture"], null)!;
            var permission = controller.ApplicationUpdateFeatureToken;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(permission, TestContext.Current.CancellationToken);
            var download = client.DownloadInstallerAsync(release, directory, cancellationToken: cancellation.Token);
            if (await Task.WhenAny(stream.Waiting.Task, download).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken) == download)
                await download;
            await stream.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Single(Directory.GetFiles(directory, "*.partial"));

            await PauseAsync(controller);
            if (restoreBeforeCompletion) await RestoreAsync(controller);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);

            Assert.True(stream.ReadCancelled);
            Assert.Empty(Directory.GetFileSystemEntries(directory));
            Assert.False(controller.CanBeginApplicationUpdate(permission));
            Assert.Equal(1, handler.Requests);
            Assert.False(controller.SetupTelemetry.IsRunning);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PausingRevokesAWaitingHandoffEvenIfTheReadyEventIsSignalled(bool alreadyPaused)
    {
        await using var controller = Controller();
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            Arguments = "-NoLogo -NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        })!;
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset);
        using var handoff = new ApplicationUpdateHandoff(process, ready);
        try
        {
            var permission = controller.ApplicationUpdateFeatureToken;
            if (alreadyPaused)
            {
                await PauseAsync(controller);
                ready.Set();
            }
            var waiting = handoff.WaitUntilReadyAsync(TimeSpan.FromSeconds(20), permission);
            if (!alreadyPaused) await PauseAsync(controller);
            await RestoreAsync(controller);
            Assert.Equal(ApplicationUpdateHandoffState.Cancelled,
                await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.False(controller.CanBeginApplicationUpdate(permission));
            Assert.True(controller.CanBeginApplicationUpdate(controller.ApplicationUpdateFeatureToken));
            handoff.StopIfRunning();
            Assert.True(process.HasExited);
        }
        finally { handoff.StopIfRunning(); }
    }

    private static AppController Controller() => new(new AppSettings
    {
        StartWithWindows = false,
        StartWithForza = false,
        AutomaticApplicationUpdateChecks = false
    }, _ => { }, new NoStartup(), checkForApplicationUpdate: (_, _) => Task.FromResult<UpdateRelease?>(null));
    private static Task PauseAsync(AppController controller) => controller.ApplyFeatureControlsAsync(
        new(1, ImmutableDictionary<string, string>.Empty.Add("updates", "Update maintenance.")));
    private static Task RestoreAsync(AppController controller) => controller.ApplyFeatureControlsAsync(new(2, ImmutableDictionary<string, string>.Empty));
    private sealed class NoStartup : IStartupRegistrationService
    {
        public void Apply(bool startWithWindows, bool startWithForza) { }
    }
    private sealed class DownloadHandler(Stream stream) : HttpMessageHandler
    {
        internal int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StreamContent(stream) };
            response.Content.Headers.ContentLength = 65536;
            return Task.FromResult(response);
        }
    }
    private sealed class PausedDownloadStream : Stream
    {
        private bool _first = true;
        internal TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool ReadCancelled { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 65536;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_first)
            {
                _first = false;
                buffer.Span[..4096].Clear();
                return 4096;
            }
            Waiting.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { ReadCancelled = true; throw; }
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
