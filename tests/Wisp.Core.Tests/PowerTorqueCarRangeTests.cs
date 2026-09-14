using System.IO;
using System.Text.Json;
using Wisp.App;
using Xunit;

namespace Wisp.Core.Tests;

public sealed class PowerTorqueCarRangeTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"PowerTorqueGaugeRanges\":null}")]
    public void OldOrNullRangeSettingsNormalizeToAnEmptyDictionary(string json)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(json)!;
        settings.MigrateSettings();
        Assert.Empty(settings.PowerTorqueGaugeRanges);
        Assert.Equal(1000, settings.PowerGaugeMaximum);
        Assert.Equal(1200, settings.TorqueGaugeMaximumNm);
    }

    [Fact]
    public void MalformedEntriesAreRemovedOrNormalizedWithoutChangingValidCars()
    {
        var retained = new PowerTorqueGaugeRange(673.2, 759);
        var settings = new AppSettings
        {
            OverlayOpacity = 0.7,
            PowerTorqueGaugeRanges = new()
            {
                [0] = new(500, 500),
                [-1] = new(500, 500),
                [1] = null!,
                [2] = new(double.NaN, double.PositiveInfinity),
                [3] = new(-400, 50000),
                [4] = new(6000, -5),
                [5] = retained
            }
        };
        settings.MigrateSettings();
        Assert.Equal(4, settings.PowerTorqueGaugeRanges.Count);
        Assert.False(settings.PowerTorqueGaugeRanges.ContainsKey(0));
        Assert.False(settings.PowerTorqueGaugeRanges.ContainsKey(-1));
        Assert.False(settings.PowerTorqueGaugeRanges.ContainsKey(1));
        Assert.Equal(new PowerTorqueGaugeRange(1000, 1200), settings.PowerTorqueGaugeRanges[2]);
        Assert.Equal(new PowerTorqueGaugeRange(100, 10000), settings.PowerTorqueGaugeRanges[3]);
        Assert.Equal(new PowerTorqueGaugeRange(5000, 100), settings.PowerTorqueGaugeRanges[4]);
        Assert.Same(retained, settings.PowerTorqueGaugeRanges[5]);
        Assert.Equal(0.7, settings.OverlayOpacity);
    }

    [Fact]
    public void SaveLoadRoundTripPreservesEachCarsPhysicalRangeAndNormalizesInvalidValues()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Wisp.Tests", Guid.NewGuid().ToString("N"));
        var service = new SettingsService(Path.Combine(directory, "settings.json"));
        try
        {
            var settings = new AppSettings
            {
                PowerGaugeMaximum = 1400,
                TorqueGaugeMaximumNm = 2000,
                TorqueUnit = TorqueUnit.PoundFeet,
                PowerTorqueGaugeRanges = new()
                {
                    [101] = new(673.2, 759),
                    [202] = new(1100, 1650),
                    [303] = new(double.NegativeInfinity, double.NaN),
                    [-1] = new(500, 500)
                }
            };
            service.Save(settings);
            var restored = service.Load();
            Assert.Equal(3, restored.PowerTorqueGaugeRanges.Count);
            Assert.Equal(new PowerTorqueGaugeRange(673.2, 759), restored.PowerTorqueGaugeRanges[101]);
            Assert.Equal(new PowerTorqueGaugeRange(1100, 1650), restored.PowerTorqueGaugeRanges[202]);
            Assert.Equal(new PowerTorqueGaugeRange(1000, 1200), restored.PowerTorqueGaugeRanges[303]);
            Assert.Equal(1400, restored.PowerGaugeMaximum);
            Assert.Equal(2000, restored.TorqueGaugeMaximumNm);
            Assert.Equal(TorqueUnit.PoundFeet, restored.TorqueUnit);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
