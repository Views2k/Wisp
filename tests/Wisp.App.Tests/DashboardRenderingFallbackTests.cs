using System.Globalization;
using System.IO;
using Wisp.App.CrashDiagnostics;
using Xunit;

namespace Wisp.App.Tests;

public sealed class DashboardRenderingFallbackTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wisp-fallback-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ARecentRequestDrawsTheDashboardInSoftware()
    {
        Assert.True(DashboardRenderingFallback.TryRequest(_directory, Now - TimeSpan.FromDays(13)));
        Assert.True(DashboardRenderingFallback.IsRequested(_directory, Now));
        Assert.True(File.Exists(DashboardRenderingFallback.MarkerPath(_directory)));
    }

    [Fact]
    public void ARequestLapsesAfterTwoWeeksAndItsMarkerIsRemoved()
    {
        Assert.True(DashboardRenderingFallback.TryRequest(_directory, Now - DashboardRenderingFallback.Lifetime));
        Assert.False(DashboardRenderingFallback.IsRequested(_directory, Now));
        Assert.False(File.Exists(DashboardRenderingFallback.MarkerPath(_directory)));
    }

    [Fact]
    public void AMarkerWithoutAReadableTimeDoesNotKeepSoftwareDrawingOn()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(DashboardRenderingFallback.MarkerPath(_directory), "not a time");
        Assert.False(DashboardRenderingFallback.IsRequested(_directory, Now));
        Assert.False(File.Exists(DashboardRenderingFallback.MarkerPath(_directory)));
    }

    [Fact]
    public void TheMarkerHoldsTheRequestTimeInUniversalIsoForm()
    {
        Assert.True(DashboardRenderingFallback.TryRequest(_directory, new DateTimeOffset(2026, 10, 24, 14, 30, 0, TimeSpan.FromHours(2))));
        var written = File.ReadAllText(DashboardRenderingFallback.MarkerPath(_directory));
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 12, 30, 0, TimeSpan.Zero),
            DateTimeOffset.ParseExact(written, "O", CultureInfo.InvariantCulture));
        Assert.False(DashboardRenderingFallback.IsRequested(_directory, Now));
    }

    [Fact]
    public void AMissingMarkerIsNoRequest() => Assert.False(DashboardRenderingFallback.IsRequested(_directory, Now));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }
}
