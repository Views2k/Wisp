using System.Windows;
using Wisp.App;
using Xunit;

namespace Wisp.App.Tests;

public sealed class GeometryTests
{
    [Fact]
    public void HudDefaultsFullyInsideTheTopRightWorkArea()
    {
        var workArea = new Rect(0, 0, 1920, 1040);
        var subject = new Size(420, 190);

        var position = OverlayPlacementGeometry.PlaceTopRight(workArea, subject);
        var placed = new Rect(position, subject);

        Assert.Equal(1476, position.X);
        Assert.Equal(24, position.Y);
        Assert.True(workArea.Contains(placed));
    }

    [Fact]
    public void HudTopRightDefaultSupportsNegativeOriginMonitors()
    {
        var workArea = new Rect(-1920, -200, 1920, 1080);
        var subject = new Size(420, 190);

        var position = OverlayPlacementGeometry.PlaceTopRight(workArea, subject);
        var placed = new Rect(position, subject);

        Assert.Equal(-444, position.X);
        Assert.Equal(-176, position.Y);
        Assert.True(workArea.Contains(placed));
    }

    [Fact]
    public void NativeHudDefaultsAtTheMeasuredStockPositionOnFullMonitorBounds()
    {
        var monitorBounds = new Rect(0, 0, 1920, 1080);
        var subject = new Size(327.5, 160);
        var nativeAnchor = OverlayPlacementGeometry.NativeContentAnchorBounds(
            NativeGaugeMode.Digital, false, 1, 1);

        var position = OverlayPlacementGeometry.PlaceNativeBottomRight(monitorBounds, subject, nativeAnchor, 1);

        Assert.Equal(new Point(1550, 878), position);
        Assert.True(monitorBounds.Contains(new Rect(position, subject)));
    }

    [Fact]
    public void NativeReferenceScalePreservesGameLogicalSizeAt4KWithDisplayDpi()
    {
        Assert.Equal(
            8d / 7,
            OverlayPlacementGeometry.NativeReferenceScale(2160, 1.75),
            6);
    }

    [Fact]
    public void NativeBottomRightPlacementUsesTheAuthoredBoxWithinTheDefaultSafeFrame()
    {
        const double dpi = 1.75;
        var scale = 8d / 7;
        var monitorBounds = new Rect(0, 0, 3840 / dpi, 2160 / dpi);
        var subject = new Size(327.5 * scale, 160 * scale);
        var nativeAnchor = OverlayPlacementGeometry.NativeContentAnchorBounds(
            NativeGaugeMode.Digital, false, scale, scale);

        var result = OverlayPlacementGeometry.PlaceNativeBottomRight(
            monitorBounds, subject, nativeAnchor, scale, dpi, dpi);

        Assert.Equal(monitorBounds.Right - (50 * scale) - nativeAnchor.Right, result.X, 6);
        Assert.Equal(monitorBounds.Bottom - (50 * scale) - nativeAnchor.Bottom, result.Y, 6);
    }

    [Fact]
    public void Native4KPlacementPreservesTheDefaultSafeFrameAt175PercentDpi()
    {
        const double dpi = 1.75;
        var scale = OverlayPlacementGeometry.NativeReferenceScale(2160, dpi);
        var monitorBounds = new Rect(0, 0, 3840 / dpi, 2160 / dpi);
        var subject = new Size(327.5 * scale, 160 * scale);
        var nativeAnchor = OverlayPlacementGeometry.NativeContentAnchorBounds(
            NativeGaugeMode.Digital, false, scale, scale);

        var position = OverlayPlacementGeometry.PlaceNativeBottomRight(
            monitorBounds, subject, nativeAnchor, scale, dpi, dpi);

        Assert.Equal(100, (monitorBounds.Right - position.X - nativeAnchor.Right) * dpi, 6);
        Assert.Equal(100, (monitorBounds.Bottom - position.Y - nativeAnchor.Bottom) * dpi, 6);
        Assert.Equal(85, (monitorBounds.Right - position.X - subject.Width) * dpi, 6);
        Assert.Equal(85, (monitorBounds.Bottom - position.Y - subject.Height) * dpi, 6);
    }

    [Theory]
    [InlineData(1920, 1080, 1)]
    [InlineData(1920, 1080, 1.25)]
    [InlineData(1920, 1080, 1.5)]
    [InlineData(1920, 1080, 1.75)]
    [InlineData(1920, 1080, 2)]
    [InlineData(2560, 1440, 1)]
    [InlineData(2560, 1440, 1.25)]
    [InlineData(2560, 1440, 1.5)]
    [InlineData(2560, 1440, 2)]
    [InlineData(3840, 2160, 1)]
    [InlineData(3840, 2160, 1.5)]
    [InlineData(3840, 2160, 1.75)]
    [InlineData(3840, 2160, 2)]
    public void NativeDigitalOverflowDoesNotMoveItsReferenceDefault(
        double pixelWidth,
        double pixelHeight,
        double dpi)
    {
        var scale = OverlayPlacementGeometry.NativeReferenceScale(pixelHeight, dpi);
        var monitorBounds = new Rect(0, 0, pixelWidth / dpi, pixelHeight / dpi);
        var renderSize = new Size(327.5 * scale, 160 * scale);
        var nativeAnchor = OverlayPlacementGeometry.NativeContentAnchorBounds(
            NativeGaugeMode.Digital, false, scale, scale);

        var position = OverlayPlacementGeometry.PlaceNativeBottomRight(
            monitorBounds, renderSize, nativeAnchor, scale, dpi, dpi);

        var physicalScale = pixelHeight / 1080;
        Assert.Equal(Math.Round(1550 * physicalScale, MidpointRounding.AwayFromZero), position.X * dpi, 6);
        Assert.Equal(Math.Round(877.5 * physicalScale, MidpointRounding.AwayFromZero), position.Y * dpi, 6);
        Assert.True(monitorBounds.Contains(new Rect(position, renderSize)));
        Assert.Equal(
            position,
            OverlayPlacementGeometry.ClampNativeInside(monitorBounds, renderSize, position));
    }

    [Theory]
    [InlineData(-1920, 0, 1920, 1080, 1)]
    [InlineData(-2560, -240, 2560, 1440, 1.25)]
    [InlineData(-3840, -2160, 3840, 2160, 1.75)]
    public void NativeDigitalReferenceDefaultSupportsNegativeOriginMonitors(
        double pixelLeft,
        double pixelTop,
        double pixelWidth,
        double pixelHeight,
        double dpi)
    {
        var scale = OverlayPlacementGeometry.NativeReferenceScale(pixelHeight, dpi);
        var monitorBounds = new Rect(pixelLeft / dpi, pixelTop / dpi, pixelWidth / dpi, pixelHeight / dpi);
        var renderSize = new Size(327.5 * scale, 160 * scale);
        var nativeAnchor = OverlayPlacementGeometry.NativeContentAnchorBounds(
            NativeGaugeMode.Digital, false, scale, scale);

        var position = OverlayPlacementGeometry.PlaceNativeBottomRight(
            monitorBounds, renderSize, nativeAnchor, scale, dpi, dpi);

        var physicalScale = pixelHeight / 1080;
        Assert.Equal(Math.Round(pixelLeft + (1550 * physicalScale), MidpointRounding.AwayFromZero), position.X * dpi, 6);
        Assert.Equal(Math.Round(pixelTop + (877.5 * physicalScale), MidpointRounding.AwayFromZero), position.Y * dpi, 6);
        Assert.True(monitorBounds.Contains(new Rect(position, renderSize)));
    }

    [Fact]
    public void NativePlacementAnchorsTheContentRectangleIncludingItsOffset()
    {
        var position = OverlayPlacementGeometry.PlaceNativeBottomRight(
            new Rect(0, 0, 1920, 1080),
            new Size(340, 175),
            new Rect(5, 6, 320, 160),
            1);

        Assert.Equal(new Point(1545, 864), position);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void NativeContentPlacementRetainsInvalidReferenceScaleFallback(double scale)
    {
        var position = OverlayPlacementGeometry.PlaceNativeBottomRight(
            new Rect(0, 0, 1920, 1080),
            new Size(327.5, 160),
            OverlayPlacementGeometry.NativeContentAnchorBounds(NativeGaugeMode.Digital, false, 1, 1),
            scale);

        Assert.Equal(new Point(1550, 878), position);
    }

    [Fact]
    public void NativeSavedCustomPositionInsideTheMonitorIsNotMovedToTheDefault()
    {
        var monitorBounds = new Rect(0, 0, 2560, 1440);
        var renderSize = new Size(293 * 4d / 3, 293.5 * 4d / 3);
        var customPosition = new Point(2126, 1024.6666666666665);

        Assert.Equal(
            customPosition,
            OverlayPlacementGeometry.ClampNativeInside(monitorBounds, renderSize, customPosition));
    }

    [Fact]
    public void NativeSavedCustomPositionNearMonitorEdgeIsNotNudgedByWorkAreaMargin()
    {
        var monitorBounds = new Rect(-1920, -200, 1920, 1080);
        var renderSize = new Size(320, 160);
        var customPosition = new Point(-1910, -190);

        Assert.Equal(
            customPosition,
            OverlayPlacementGeometry.ClampNativeInside(monitorBounds, renderSize, customPosition));
    }

    [Fact]
    public void NativeSavedOutOfBoundsPositionStillClampsTheEntireRenderWindow()
    {
        var position = OverlayPlacementGeometry.ClampNativeInside(
            new Rect(-1920, -200, 1920, 1080),
            new Size(327.5, 160),
            new Point(10, -300));

        Assert.Equal(new Point(-327.5, -200), position);
    }

    [Theory]
    [InlineData(NativeGaugeMode.Digital, false, 0, 7.5, 320, 145)]
    [InlineData(NativeGaugeMode.Digital, true, 0, 7.5, 320, 145)]
    [InlineData(NativeGaugeMode.Analogue, false, 0, 0, 293, 282.5)]
    [InlineData(NativeGaugeMode.Analogue, true, 10, 28, 325, 289)]
    public void NativeAnchorsIncludeAuthoredMarginsAndFixedHostContentOrigins(
        NativeGaugeMode mode,
        bool isElectric,
        double x,
        double y,
        double width,
        double height)
    {
        Assert.Equal(
            new Rect(x, y, width, height),
            OverlayPlacementGeometry.NativeContentAnchorBounds(mode, isElectric, 1, 1));
        Assert.Equal(
            new Rect(x * 1.25, y * 1.5, width * 1.25, height * 1.5),
            OverlayPlacementGeometry.NativeContentAnchorBounds(mode, isElectric, 1.25, 1.5));
    }

    [Theory]
    [InlineData(1920, 1080, 1, false, 1577, 748)]
    [InlineData(2560, 1440, 1.25, false, 2103, 997)]
    [InlineData(3840, 2160, 1.5, false, 3154, 1495)]
    [InlineData(3840, 2160, 1.75, false, 3154, 1495)]
    [InlineData(1920, 1080, 1, true, 1535, 713)]
    [InlineData(2560, 1440, 1.25, true, 2047, 951)]
    [InlineData(3840, 2160, 1.5, true, 3070, 1426)]
    [InlineData(3840, 2160, 1.75, true, 3070, 1426)]
    public void NativeAnalogueDefaultsUseAuthoredMarginBoxesAtEachResolutionAndDpi(
        double pixelWidth,
        double pixelHeight,
        double dpi,
        bool isElectric,
        double expectedPixelX,
        double expectedPixelY)
    {
        var scale = OverlayPlacementGeometry.NativeReferenceScale(pixelHeight, dpi);
        var monitorBounds = new Rect(0, 0, pixelWidth / dpi, pixelHeight / dpi);
        var renderSize = isElectric
            ? new Size(345 * scale, 345 * scale)
            : new Size(293 * scale, 293.5 * scale);
        var anchor = OverlayPlacementGeometry.NativeContentAnchorBounds(
            NativeGaugeMode.Analogue, isElectric, scale, scale);

        var position = OverlayPlacementGeometry.PlaceNativeBottomRight(
            monitorBounds, renderSize, anchor, scale, dpi, dpi);

        Assert.Equal(expectedPixelX, position.X * dpi, 6);
        Assert.Equal(expectedPixelY, position.Y * dpi, 6);
        Assert.True(monitorBounds.Contains(new Rect(position, renderSize)));
    }

    [Fact]
    public void NativeAnalogueCurrentDisplayDefaultUsesNegativeBottomMargin()
    {
        const double scale = 4d / 3;
        var position = OverlayPlacementGeometry.PlaceNativeBottomRight(
            new Rect(0, 0, 2560, 1440),
            new Size(293 * scale, 293.5 * scale),
            OverlayPlacementGeometry.NativeContentAnchorBounds(NativeGaugeMode.Analogue, false, scale, scale),
            scale,
            1.5,
            1.5);

        Assert.Equal(2102.6666666666665, position.X, 6);
        Assert.Equal(996.6666666666666, position.Y, 6);
    }

    [Theory]
    [InlineData(NativeGaugeMode.Digital, true, -740, -405)]
    [InlineData(NativeGaugeMode.Analogue, false, -686, -665)]
    [InlineData(NativeGaugeMode.Analogue, true, -770, -734)]
    public void AllNativeTemplatesRetainTheirAnchorsOnANegativeOriginMonitor(
        NativeGaugeMode mode,
        bool isElectric,
        double expectedPixelX,
        double expectedPixelY)
    {
        const double dpi = 1.75;
        var scale = OverlayPlacementGeometry.NativeReferenceScale(2160, dpi);
        var monitorBounds = new Rect(-3840 / dpi, -2160 / dpi, 3840 / dpi, 2160 / dpi);
        var renderSize = mode == NativeGaugeMode.Digital
            ? new Size(327.5 * scale, 160 * scale)
            : isElectric
                ? new Size(345 * scale, 345 * scale)
                : new Size(293 * scale, 293.5 * scale);
        var anchor = OverlayPlacementGeometry.NativeContentAnchorBounds(mode, isElectric, scale, scale);

        var position = OverlayPlacementGeometry.PlaceNativeBottomRight(
            monitorBounds, renderSize, anchor, scale, dpi, dpi);

        Assert.Equal(expectedPixelX, position.X * dpi, 6);
        Assert.Equal(expectedPixelY, position.Y * dpi, 6);
        Assert.True(monitorBounds.Contains(new Rect(position, renderSize)));
    }

    [Theory]
    [InlineData(NativeGaugeMode.Digital)]
    [InlineData(NativeGaugeMode.Analogue)]
    public void FirstElectricFramePreservesTheSameDefaultSafeFrameAnchor(NativeGaugeMode mode)
    {
        const double scale = 4d / 3;
        const double dpi = 1.5;
        var monitorBounds = new Rect(0, 0, 2560, 1440);
        var previousAnchor = OverlayPlacementGeometry.NativeContentAnchorBounds(mode, false, scale, scale);
        var nextAnchor = OverlayPlacementGeometry.NativeContentAnchorBounds(mode, true, scale, scale);
        var previousRenderSize = mode == NativeGaugeMode.Digital
            ? new Size(327.5 * scale, 160 * scale)
            : new Size(293 * scale, 293.5 * scale);
        var nextRenderSize = mode == NativeGaugeMode.Digital
            ? previousRenderSize
            : new Size(345 * scale, 345 * scale);
        var initialPosition = OverlayPlacementGeometry.PlaceNativeBottomRight(
            monitorBounds, previousRenderSize, previousAnchor, scale, dpi, dpi);
        var expectedPosition = OverlayPlacementGeometry.PlaceNativeBottomRight(
            monitorBounds, nextRenderSize, nextAnchor, scale, dpi, dpi);

        var position = OverlayPlacementGeometry.PreserveAnchorPosition(initialPosition, previousAnchor, nextAnchor);
        var roundTrip = OverlayPlacementGeometry.PreserveAnchorPosition(position, nextAnchor, previousAnchor);

        Assert.Equal(expectedPosition.X, position.X, 6);
        Assert.Equal(expectedPosition.Y, position.Y, 6);
        Assert.Equal(initialPosition.X, roundTrip.X, 6);
        Assert.Equal(initialPosition.Y, roundTrip.Y, 6);
    }

    [Fact]
    public void ElectricResizePreservesCustomAnchorRatherThanApplyingDefaultPlacement()
    {
        const double scale = 4d / 3;
        var previousAnchor = OverlayPlacementGeometry.NativeContentAnchorBounds(
            NativeGaugeMode.Analogue, false, scale, scale);
        var nextAnchor = OverlayPlacementGeometry.NativeContentAnchorBounds(
            NativeGaugeMode.Analogue, true, scale, scale);
        var customPosition = new Point(2126, 1024.6666666666665);

        var position = OverlayPlacementGeometry.PreserveAnchorPosition(customPosition, previousAnchor, nextAnchor);

        Assert.Equal(customPosition.X + previousAnchor.Right, position.X + nextAnchor.Right, 6);
        Assert.Equal(customPosition.Y + previousAnchor.Bottom, position.Y + nextAnchor.Bottom, 6);
        Assert.NotEqual(2046.6666666666667, position.X);
        Assert.NotEqual(950.6666666666666, position.Y);
    }

    [Fact]
    public void SavedPlacementIsClampedFullyInsideItsWorkArea()
    {
        var workArea = new Rect(0, 0, 1920, 1040);
        var subject = new Size(420, 190);

        var position = OverlayPlacementGeometry.ClampInside(
            workArea,
            subject,
            new Point(1850, -80));

        Assert.Equal(1476, position.X);
        Assert.Equal(24, position.Y);
        Assert.True(workArea.Contains(new Rect(position, subject)));
    }

    [Fact]
    public void StandaloneMeterDefaultsBelowMinimalSpeedWithoutClipping()
    {
        var workArea = new Rect(0, 0, 1920, 1040);
        var anchor = new Rect(1780, 24, 116, 84);
        var subject = new Size(210, 150);

        var position = OverlayPlacementGeometry.PlaceBelow(workArea, anchor, subject);
        var placed = new Rect(position, subject);

        Assert.Equal(1686, position.X);
        Assert.Equal(120, position.Y);
        Assert.True(workArea.Contains(placed));
        Assert.False(placed.IntersectsWith(anchor));
    }

    [Fact]
    public void NativeGForceMeterDefaultsImmediatelyAboveSpeedometer()
    {
        var workArea = new Rect(0, 0, 1920, 1040);
        var anchor = new Rect(1576, 856, 320, 160);
        var subject = new Size(210, 150);

        var position = OverlayPlacementGeometry.PlaceAbove(workArea, anchor, subject);
        var placed = new Rect(position, subject);

        Assert.Equal(new Point(1686, 694), position);
        Assert.Equal(OverlayPlacementGeometry.DefaultGap, anchor.Top - placed.Bottom);
        Assert.True(workArea.Contains(placed));
        Assert.False(placed.IntersectsWith(anchor));
    }

    [Theory]
    [InlineData(1920, 1040, 980, 750)]
    [InlineData(960, 540, 936, 516)]
    [InlineData(700, 400, 676, 376)]
    public void ControlWindowFitsInsideWorkArea(
        double workAreaWidth,
        double workAreaHeight,
        double expectedWidth,
        double expectedHeight)
    {
        var fitted = ControlWindowGeometry.FitToWorkArea(
            new Size(980, 750),
            new Size(workAreaWidth, workAreaHeight));

        Assert.Equal(expectedWidth, fitted.Width);
        Assert.Equal(expectedHeight, fitted.Height);
    }

    [Fact]
    public void ControlWindowUsesPerMonitorDpiWhenFittingPhysicalWorkArea()
    {
        var fitted = ControlWindowGeometry.FitToPhysicalWorkArea(
            new Size(980, 750),
            new Size(1366, 720),
            dpiScaleX: 1.5,
            dpiScaleY: 1.5);

        Assert.Equal(886.6666666666666, fitted.Width, 10);
        Assert.Equal(456, fitted.Height);
    }

    [Fact]
    public void StandaloneMeterDefaultsImmediatelyLeftOfTwoBoxSpeedPanel()
    {
        var position = OverlayPlacementGeometry.PlaceAdjacentHorizontally(
            new Rect(0, 0, 1920, 1040),
            new Rect(1688, 866, 190, 150),
            new Size(210, 150));

        Assert.Equal(1466, position.X);
        Assert.Equal(866, position.Y);
    }

    [Fact]
    public void StandaloneMeterUsesRightSideWhenLeftSideDoesNotFit()
    {
        var position = OverlayPlacementGeometry.PlaceAdjacentHorizontally(
            new Rect(0, 0, 1920, 1040),
            new Rect(30, 500, 190, 150),
            new Size(210, 150));

        Assert.Equal(232, position.X);
        Assert.Equal(500, position.Y);
    }

    [Fact]
    public void StandaloneMeterStaysAdjacentInsideNegativeOriginAnchorMonitor()
    {
        var workArea = new Rect(-1920, 0, 1920, 1040);
        var anchor = new Rect(-232, 866, 190, 150);
        var subject = new Size(210, 150);

        var position = OverlayPlacementGeometry.PlaceAdjacentHorizontally(workArea, anchor, subject);
        var placed = new Rect(position, subject);

        Assert.Equal(-454, position.X);
        Assert.Equal(866, position.Y);
        Assert.Equal(OverlayPlacementGeometry.DefaultGap, anchor.Left - placed.Right);
        Assert.True(workArea.Contains(placed));
        Assert.False(placed.IntersectsWith(anchor));
    }

    [Fact]
    public void MatchingGForcePlacementForSelectedSpeedDisplayWins()
    {
        const string speedKey = "DISPLAY-A-2560x1440-SpeedV4-SeparateBoxes";
        const string matchingKey = "DISPLAY-A-2560x1440-GForceV2";
        var matchingPlacement = new OverlayPlacement(120, 240, 1.25, 1.1);
        var placements = new Dictionary<string, OverlayPlacement>
        {
            ["DISPLAY-B-1920x1080-GForceV2"] = new OverlayPlacement(10, 20, 1, 1),
            [matchingKey] = matchingPlacement
        };

        var placement = OverlayPlacementResolver.FindGForcePlacementForSpeedDisplay(
            placements,
            speedKey,
            out var resolvedKey);

        Assert.Same(matchingPlacement, placement);
        Assert.Equal(matchingKey, resolvedKey);
    }

    [Fact]
    public void AnotherDisplaysGForcePlacementDoesNotSuppressAdjacentFallback()
    {
        const string speedKey = "DISPLAY-A-2560x1440-SpeedV4-SeparateBoxes";
        var placements = new Dictionary<string, OverlayPlacement>
        {
            ["DISPLAY-B-1920x1080-GForceV2"] = new OverlayPlacement(10, 20, 1, 1)
        };

        var placement = OverlayPlacementResolver.FindGForcePlacementForSpeedDisplay(
            placements,
            speedKey,
            out var resolvedKey);

        Assert.Null(placement);
        Assert.Equal("DISPLAY-A-2560x1440-GForceV2", resolvedKey);
    }
}
