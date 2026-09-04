using Xunit;

namespace Wisp.App.Tests;

public sealed class DashboardPowerDisplayModelTests
{
    private const double WattsPerHorsepower = 745.69987158227022;

    [Theory]
    [InlineData(113, "+113 HP")]
    [InlineData(-113, "-113 HP")]
    [InlineData(0, "0 HP")]
    public void FirstSamplePublishesImmediatelyAsWholeHorsepower(
        double horsepower,
        string expected)
    {
        var model = new DashboardPowerDisplayModel();

        var display = model.Observe(1, 1_000, horsepower * WattsPerHorsepower);

        Assert.Equal(expected, display);
    }

    [Fact]
    public void RapidSamplesAreSmoothedAndRateLimited()
    {
        var model = new DashboardPowerDisplayModel();

        Assert.Equal(
            "+100 HP",
            model.Observe(1, 1_000, 100 * WattsPerHorsepower));

        Assert.Equal(
            "+100 HP",
            model.Observe(1, 1_050, 1_000 * WattsPerHorsepower));

        Assert.Equal(
            "+454 HP",
            model.Observe(1, 1_125, 1_000 * WattsPerHorsepower));
    }

    [Fact]
    public void NewCarAndExplicitResetPublishTheNewValueImmediately()
    {
        var model = new DashboardPowerDisplayModel();

        _ = model.Observe(1, 1_000, 100 * WattsPerHorsepower);
        _ = model.Observe(1, 1_125, 1_000 * WattsPerHorsepower);

        Assert.Equal(
            "+250 HP",
            model.Observe(2, 1_130, 250 * WattsPerHorsepower));

        model.Reset();

        Assert.Equal(
            "-100 HP",
            model.Observe(2, 1_131, -100 * WattsPerHorsepower));
    }
}
