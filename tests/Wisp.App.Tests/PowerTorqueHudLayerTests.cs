using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using Wisp.App.NativeRendering;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

internal static class PowerTorqueHudLayerTests
{
    internal static void AssertOnCurrentDispatcher()
    {
        ArtworkIsReusedWhileValuesChangeAndRebuiltForStyles();
        PowerAndTorqueKeepTheirArtworkGeometryAndUnits();
        AppendedCommandsDoNotScaleEarlierLayers();
        NativeFramesAdvanceWithoutUiRenderingOrAdditionalSmoothing();
    }

    private static void AppendedCommandsDoNotScaleEarlierLayers()
    {
        var gauge = Gauge();
        gauge.Width = 210;
        gauge.Height = 70;
        gauge.Measure(new(210, 70));
        gauge.Arrange(new(0, 0, 210, 70));
        gauge.UpdateLayout();
        gauge.Display = new(true, 500, 1000, 600, 1100);
        var snapshot = PowerTorqueHudLayer.Capture(gauge, null);
        var playback = snapshot.CreatePlayback();
        playback.Update(snapshot, Ticks(1000));
        var expected = playback.Build(Ticks(1000));
        var sentinel = AnalogHudScene.Quad(999, new(13, 17, 19, 23), opacity: .3);
        var commands = new List<DirectCompositionDrawCommand> { sentinel };
        playback.AppendCommands(commands, Ticks(1000));
        Assert.Equal(sentinel, commands[0]);
        Assert.Equal(expected, commands.Skip(1));
    }

    private static void ArtworkIsReusedWhileValuesChangeAndRebuiltForStyles()
    {
        var gauge = Gauge();
        gauge.Display = new(true, 250, 300, 500, 600);
        var first = PowerTorqueHudLayer.Capture(gauge, null);
        Assert.Same(first, PowerTorqueHudLayer.Capture(gauge, null));
        gauge.Display = gauge.Display with { PowerBhp = 500 };
        var changed = PowerTorqueHudLayer.Capture(gauge, null);
        Assert.NotSame(first, changed);
        Assert.Same(first.Textures, changed.Textures);
        Assert.Equal(first.CompatibilityKey, changed.CompatibilityKey);
        gauge.Maximum = 2_000;
        var range = PowerTorqueHudLayer.Capture(gauge, null);
        Assert.NotSame(changed.Textures, range.Textures);
        Assert.NotEqual(changed.CompatibilityKey, range.CompatibilityKey);
        gauge.LowBrush = Brushes.Orange;
        var color = PowerTorqueHudLayer.Capture(gauge, null);
        Assert.NotSame(range.Textures, color.Textures);
        gauge.AccentBrush = Brushes.Lime;
        var accent = PowerTorqueHudLayer.Capture(gauge, null);
        Assert.NotSame(color.Textures, accent.Textures);
        Assert.All(accent.Textures, texture =>
        {
            Assert.InRange(texture.Id, 20_000u, 24_999u);
            Assert.Equal(texture.Stride * texture.Height, texture.Pixels.Length);
        });
    }

    private static void PowerAndTorqueKeepTheirArtworkGeometryAndUnits()
    {
        var power = Gauge();
        power.Display = new(true, 500, 1_000, 600, 1_100) { ReadoutPowerBhp = 123, ReadoutTorqueNm = 200 };
        var snapshot = PowerTorqueHudLayer.Capture(power, null);
        var playback = snapshot.CreatePlayback();
        playback.Update(snapshot, Ticks(1_000));
        var commands = playback.Build(Ticks(1_000));
        var needle = Assert.Single(commands, command => command.Shader == DirectCompositionShader.Needle);
        Assert.InRange(Angle(needle), 269.999, 270.001);
        Assert.Equal(110 * 140d / 288 * .8, Math.Sqrt(needle.AxisXX * needle.AxisXX + needle.AxisXY * needle.AxisXY), 4);
        var arc = Assert.Single(commands, command => command.Shader == DirectCompositionShader.ImageSector);
        Assert.Equal(135 * Math.PI / 180, arc.ParameterX, 6);
        Assert.Equal(135 * Math.PI / 180, arc.ParameterY, 6);
        Assert.Equal(new uint[] { 20_011, 20_012, 20_013 }, commands.Where(command => command.TextureId is >= 20_010 and <= 20_019)
            .Select(command => command.TextureId));

        var torque = Gauge();
        torque.IsTorque = true;
        torque.IsElectricMaterial = true;
        torque.TorqueUnit = TorqueUnit.PoundFeet;
        torque.Maximum = 1_000;
        torque.Display = power.Display;
        var torqueSnapshot = PowerTorqueHudLayer.Capture(torque, null);
        var torquePlayback = torqueSnapshot.CreatePlayback();
        torquePlayback.Update(torqueSnapshot, Ticks(1_000));
        var torqueCommands = torquePlayback.Build(Ticks(1_000));
        var electric = Assert.Single(torqueCommands, command => command.Shader == DirectCompositionShader.ElectricNeedle);
        Assert.InRange(Angle(electric), 334.140, 334.144);
        Assert.Empty(snapshot.Textures.Select(texture => texture.Id).Intersect(torqueSnapshot.Textures.Select(texture => texture.Id)));
        Assert.Equal(new uint[] { 25_011, 25_014, 25_018 }, torqueCommands.Where(command => command.TextureId is >= 25_010 and <= 25_019)
            .Select(command => command.TextureId));
        torque.Display = PowerTorqueDisplay.Unavailable;
        var unavailable = PowerTorqueHudLayer.Capture(torque, null);
        torquePlayback.Update(unavailable, Ticks(1_016));
        Assert.DoesNotContain(torquePlayback.Build(Ticks(1_016)), command => command.Shader is
            DirectCompositionShader.Needle or DirectCompositionShader.ElectricNeedle or DirectCompositionShader.ImageSector);
    }

    private static void NativeFramesAdvanceWithoutUiRenderingOrAdditionalSmoothing()
    {
        var model = new DiagnosticsViewModel(new AppSettings
        {
            PowerGaugeEnabled = true,
            PowerTorqueSmoothingMilliseconds = 0
        });
        var gauge = Gauge();
        var start = Stopwatch.GetTimestamp() - Ticks(500);
        var now = start;
        Update(model, 1_000, start, 0);
        var initial = PowerTorqueHudLayer.Capture(gauge, model);
        var playback = (HudLayerPlayback)Activator.CreateInstance(
            typeof(PowerTorqueHudLayer).GetNestedType("Playback", BindingFlags.NonPublic)!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: new object[] { (Func<long>)(() => now) }, culture: null)!;
        playback.Update(initial, start);
        now = start + Ticks(16);
        Update(model, 1_000, now, 100);
        playback.Update(PowerTorqueHudLayer.Capture(gauge, model), now);
        now = start + Ticks(32);
        Update(model, 1_016, now, 200);
        playback.Update(PowerTorqueHudLayer.Capture(gauge, model), now);
        Assert.InRange(model.NativePowerTorqueInput.Display.PowerBhp, 199.99, 200.01);
        // Scene sampling occurs on a worker with no WPF rendering callback.
        var samples = Task.Run(() => (playback.Build(start + Ticks(44)), playback.Build(start + Ticks(48))))
            .GetAwaiter().GetResult();
        var before = Assert.Single(samples.Item1, command => command.Shader == DirectCompositionShader.Needle);
        var after = Assert.Single(samples.Item2, command => command.Shader == DirectCompositionShader.Needle);
        Assert.InRange(Angle(before), 141.749, 141.751);
        Assert.InRange(Angle(after), 148.499, 148.501);
        var beforeArc = Assert.Single(samples.Item1, command => command.Shader == DirectCompositionShader.ImageSector);
        var afterArc = Assert.Single(samples.Item2, command => command.Shader == DirectCompositionShader.ImageSector);
        Assert.True(afterArc.ParameterY > beforeArc.ParameterY);
        Assert.Equal(Angle(after) - 135, afterArc.ParameterY * 180 / Math.PI, 4);
        Assert.Equal(samples.Item1.Where(command => command.TextureId is >= 20_010 and <= 20_019),
            samples.Item2.Where(command => command.TextureId is >= 20_010 and <= 20_019));
    }

    private static PowerTorqueGaugeView Gauge()
    {
        var control = new PowerTorqueGaugeView { Width = 140, Height = 140, Maximum = 1_000 };
        control.Measure(new Size(140, 140));
        control.Arrange(new Rect(0, 0, 140, 140));
        return control;
    }

    private static double Angle(DirectCompositionDrawCommand command)
    {
        var value = Math.Atan2(command.AxisXY, command.AxisXX) * 180 / Math.PI;
        return value < 0 ? value + 360 : value;
    }

    private static long Ticks(double milliseconds) => (long)(milliseconds * Stopwatch.Frequency / 1_000);

    private static void Update(DiagnosticsViewModel model, uint gameTime, long received, double horsepower)
    {
        var state = new VehicleState
        {
            IsRaceOn = true,
            GameTimestampMilliseconds = gameTime,
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            ReceivedTimestamp = received,
            CarOrdinal = 1,
            Drivetrain = DrivetrainType.RearWheelDrive,
            GroundSpeedMetersPerSecond = 30,
            WheelRotationRadiansPerSecond = new(100, 100, 100, 100),
            TireSlipRatio = default,
            TireSlipAngle = default,
            NormalizedSuspensionTravel = new(.5f, .5f, .5f, .5f),
            LateralAccelerationMetersPerSecondSquared = 0,
            LongitudinalAccelerationMetersPerSecondSquared = 0,
            EngineRpm = 4_000,
            EngineMaximumRpm = 8_000,
            Gear = TransmissionGear.Second,
            Steering = 0,
            Accelerator = 255,
            Brake = 0,
            PowerWatts = (float)(horsepower * 745.69987158227022),
            TorqueNm = (float)(horsepower * 2)
        };
        model.Update(state, new IndicatedSpeed(0, 30, true, false, "Rear"),
            new CalibrationResult(null, .3, .2, 0, true, string.Empty, false),
            default, TimeSpan.Zero, SpeedUnit.MilesPerHour, 60, refreshDiagnostics: false, updateGForce: false);
    }
}
