using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Wisp.App.NativeRendering;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class SceneCommandBaselineTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("analogue")]
    [InlineData("digital")]
    [InlineData("electric")]
    [InlineData("boost")]
    [InlineData("tire")]
    [InlineData("shared")]
    public void ReportDeterministicCommandBytesAndOwnedBuildAllocations(string kind)
    {
        for (var variant = 0; variant < 8; variant++)
        {
            var build = CreateBuilder(kind, variant);
            var commands = build();
            var bytes = MemoryMarshal.AsBytes(commands.AsSpan());
            output.WriteLine($"SCENE_BYTES {kind} {variant} {commands.Length} {Convert.ToHexString(SHA256.HashData(bytes))}");
            for (var warmup = 0; warmup < 32; warmup++) build();
            const int iterations = 256;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < iterations; index++) build();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            output.WriteLine($"SCENE_OWNED_ALLOC {kind} {variant} {iterations} {allocated}");
            Assert.NotEmpty(commands);
        }
    }

    private static Func<DirectCompositionDrawCommand[]> CreateBuilder(string kind, int variant)
    {
        var frame = Frame(variant);
        var traction = (variant & 2) != 0;
        var color = new AnalogHudColor(85, 230, 193);
        switch (kind)
        {
            case "analogue":
                return () => AnalogHudScene.Build(frame, 120 + variant * 30, -.3, variant != 7, traction, color);
            case "digital":
                frame = frame with { IsElectric = (variant & 4) != 0 };
                var digital = new DigitalHudSample(frame, frame.EngineRpm,
                    NativeElectricSpeedDisplaySelector.Resolve(frame, 0), Power(frame), true);
                return () => DigitalHudScene.Build(digital, traction, color);
            case "electric":
                frame = frame with { IsElectric = true };
                var electric = new ElectricHudSample(frame, 214.75, -.3, variant != 7, true, 0,
                    NativeElectricSpeedDisplaySelector.Resolve(frame, 0), Power(frame), null, 0, 40, 1, true, 1, 0);
                return () => ElectricHudScene.Build(electric, traction, color);
            case "boost":
                var boost = Boost(variant);
                return () => BoostHudLayer.Build(boost, boost.Display.PressurePsi, boost.Display.Fraction, 0);
            case "tire":
                var tire = Tire(variant);
                return () => TireHudLayer.Build(tire, tire.Display.FrontFraction, tire.Display.RearFraction);
            case "shared":
                var scene = new HudScenePlayback();
                scene.Update(Window(variant), 0);
                return () => scene.Build(0);
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    internal static HudWindowSnapshot Window(int variant) => new(800, 600, true,
    [
        new(1, Boost(variant), 37, 49, 0, 1.25f, -.75f, 0, .4f),
        new(2, Tire(variant), 190, 127, 1.5f, .25f, -.5f, 2, .7f)
    ]);

    internal static BoostHudLayer.Snapshot Boost(int variant)
    {
        var config = new BoostHudLayer.Configuration((variant & 1) != 0, 136, 136, 1,
            (variant & 2) == 0 ? BoostPressureUnit.Psi : BoostPressureUnit.Bar, (variant & 4) != 0,
            true, (variant & 2) != 0, (variant & 4) != 0, new(72, 217, 241), new(57, 127, 247), new(164, 92, 255));
        var pressure = (variant & 4) != 0 ? -5 : 35;
        return new(config, new(Array.Empty<AnalogHudTexture>(), Glyphs()),
            new(true, pressure, 70, .5, 70) { ScaleMinimumPsi = config.Vacuum ? -15 : 0 }, 1, 0, 0);
    }

    private static TireHudLayer.Snapshot Tire(int variant)
    {
        var config = new TireHudLayer.Configuration((variant & 1) != 0, 136, 136, 1,
            (variant & 2) == 0 ? TireTemperatureUnit.Fahrenheit : TireTemperatureUnit.Celsius,
            (variant & 4) != 0, true, new(72, 217, 241), new(57, 127, 247), new(164, 92, 255));
        return new(config, new(Array.Empty<AnalogHudTexture>(), Glyphs()), new(true, 200, 220, .3, .7), 1, 0, 0);
    }

    private static SupplementaryHudArt.GlyphSet Glyphs() => new(
        "0123456789.-FRONT EAC°".Distinct().Select((c, index) => (c, index)).ToDictionary(
            pair => pair.c, pair => new SupplementaryHudArt.Glyph((uint)(50000 + pair.index), 8, 16, 7)), 16);

    private static NativeElectricPowerGaugeDisplay Power(NativeGaugeFrame frame) =>
        new NativeElectricPowerGaugeModel().Update(frame.NativeRegenFillAmount, frame.NativePowerFillAmount, frame.NativeRegenPowerRatio);

    private static NativeGaugeFrame Frame(int variant) => new(true, variant * 137, variant * 1300, 9000,
        TransmissionGear.Third, (variant & 1) == 0 ? SpeedUnit.MilesPerHour : SpeedUnit.KilometersPerHour,
        ExactRedlineResult.Exact(8000 * Math.PI / 30), NativeAssistSnapshot.Unavailable() with
        {
            Available = (variant & 4) == 0,
            IsABSAvailable = true,
            IsABSOn = (variant & 1) == 0,
            ABSAngle = -34,
            IsTCRAvailable = true,
            IsTCROn = (variant & 1) != 0,
            TCRAngle = 23,
            IsLCAvailable = true,
            IsLCOn = (variant & 2) == 0,
            LCAngle = -12,
            IsSTMAvailable = true,
            IsSTMOn = (variant & 2) != 0,
            STMAngle = 48,
            HeadlightStateAvailable = true,
            AreHeadlightsOn = (variant & 2) != 0
        }, CarOrdinal: 1, NativeRegenFillAmount: .1, NativePowerFillAmount: .6, NativeRegenPowerRatio: .3,
        ElectricGearState: new(true, 2, 3, 1, 2, false));
}
