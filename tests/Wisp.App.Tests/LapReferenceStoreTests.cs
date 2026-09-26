using Wisp.App.Laps;
using Wisp.App.NativeRendering;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class LapReferenceStoreTests
{
    private static LapReferenceSession Session() => new(LapTimingMode.TimeAttack, 42, 2,
        new(61.5f, true, Enumerable.Range(0, 100).Select(i => (float)i).ToArray()), null);

    [Fact]
    public void KeptLapsReturnOnlyForTheSameRunOfTheGame()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wisp-laps-{Guid.NewGuid():N}.json");
        var run = "100:1";
        var store = new LapReferenceStore(path, () => run);
        store.Save(Session());
        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.Circuit);
        Assert.Equal(61.5f, loaded.Best!.Duration);
        Assert.Equal(Session().Best!.Points, loaded.Best.Points);
        run = "200:2";
        Assert.Null(store.Load());
        run = "100:1";
        store.Save(null);
        Assert.False(File.Exists(path));
        Assert.Null(store.Load());
    }

    [Theory]
    [InlineData(33.034, "0:33.034")]
    [InlineData(61.5, "1:01.500")]
    [InlineData(119.9996, "2:00.000")]
    public void HeaderShowsTheReferenceLapTime(double seconds, string expected) =>
        Assert.Equal(expected, LapDeltaHudLayer.LapTime(seconds));
}
