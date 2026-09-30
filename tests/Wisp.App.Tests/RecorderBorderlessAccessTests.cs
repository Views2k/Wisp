using System.Diagnostics;
using System.Text;
using Wisp.App.Clips;
using Xunit;

namespace Wisp.App.Tests;

public sealed class RecorderBorderlessAccessTests
{
    [Theory]
    [InlineData("allowed", ClipBorderlessAccessResult.Allowed)]
    [InlineData("denied", ClipBorderlessAccessResult.Denied)]
    [InlineData("unavailable", ClipBorderlessAccessResult.Unavailable)]
    public void ParsesOnlyThePermissionEnvelope(string status, ClipBorderlessAccessResult expected) =>
        Assert.Equal(expected, RecorderBorderlessAccess.Parse(Encoding.UTF8.GetBytes(
            $$"""{"v":1,"mode":"borderless_access","status":"{{status}}"}""")));

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"v\":1,\"mode\":\"borderless_access\",\"status\":\"allowed\",\"extra\":1}")]
    [InlineData("{\"v\":1,\"mode\":\"borderless_access\",\"status\":\"denied\",\"status\":\"allowed\"}")]
    [InlineData("{\"v\":true,\"mode\":\"borderless_access\",\"status\":\"allowed\"}")]
    [InlineData("{\"v\":2,\"mode\":\"borderless_access\",\"status\":\"allowed\"}")]
    [InlineData("{\"v\":1,\"mode\":\"recorder_failure\",\"status\":\"allowed\"}")]
    [InlineData("{\"v\":1,\"mode\":\"borderless_access\",\"status\":null}")]
    public void MalformedResponsesNeverGrantPermission(string response) =>
        Assert.Equal(ClipBorderlessAccessResult.Unavailable, RecorderBorderlessAccess.Parse(Encoding.UTF8.GetBytes(response)));

    [Fact]
    public async Task RequestUsesNoCaptureCommandAndWaitsForContainedChild()
    {
        var child = new Child();
        ProcessStartInfo? started = null;
        var request = new RecorderBorderlessAccess(@"C:\Wisp\Wisp.Recorder.exe", start => { started = start; return child; });
        var result = await request.RequestAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ClipBorderlessAccessResult.Allowed, result);
        Assert.NotNull(started);
        Assert.Equal(["--borderless-access-stdio"], started.ArgumentList);
        Assert.False(started.UseShellExecute);
        Assert.Equal("request-borderless-v1\n", Encoding.UTF8.GetString(child.Request.ToArray()));
        Assert.True(child.Disposed);
        Assert.Equal(0, child.Kills);
    }

    [Fact]
    public async Task UncontainedChildIsStoppedBeforeAnyPermissionRequest()
    {
        var child = new Child { IsReady = false };
        var request = new RecorderBorderlessAccess(@"C:\Wisp\Wisp.Recorder.exe", _ => child);
        Assert.Equal(ClipBorderlessAccessResult.Unavailable, await request.RequestAsync(TestContext.Current.CancellationToken));
        Assert.Empty(child.Request.ToArray());
        Assert.Equal(1, child.Kills);
        Assert.True(child.Disposed);
    }

    [Fact]
    public async Task ExplicitPermissionRequestIsCachedAndEnableNeverInvokesIt()
    {
        var calls = 0;
        await using var service = new ClipRecorderService(() => @"C:\Clips", () => throw new InvalidOperationException(), true,
            validateStorage: _ => Task.FromResult(@"C:\Clips"), requestBorderless: _ =>
            { calls++; return Task.FromResult(ClipBorderlessAccessResult.Denied); });
        await service.SetEnabledAsync(true, new(60, 1080, 60, 75), TestContext.Current.CancellationToken);
        Assert.Equal(0, calls);
        await service.SetEnabledAsync(false, new(60, 1080, 60, 75), TestContext.Current.CancellationToken);
        Assert.Equal(ClipBorderlessAccessResult.Denied, await service.RequestBorderlessAccessAsync(TestContext.Current.CancellationToken));
        Assert.Equal(ClipBorderlessAccessResult.Denied, await service.RequestBorderlessAccessAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
    }

    private sealed class Child : IThumbnailChild
    {
        internal System.IO.MemoryStream Request { get; } = new();
        public bool IsReady { get; init; } = true;
        public System.IO.Stream Input => Request;
        public System.IO.Stream Output { get; } = new System.IO.MemoryStream(Encoding.UTF8.GetBytes(
            "{\"v\":1,\"mode\":\"borderless_access\",\"status\":\"allowed\"}\n"));
        public System.IO.Stream Error { get; } = new System.IO.MemoryStream();
        public int ExitCode => 0;
        internal bool Disposed { get; private set; }
        internal int Kills { get; private set; }
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Kill() => Kills++;
        public void Dispose() { Disposed = true; Request.Dispose(); Output.Dispose(); Error.Dispose(); }
    }
}
