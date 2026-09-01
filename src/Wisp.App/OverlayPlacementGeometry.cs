using System.Windows;

namespace Wisp.App;

public static class OverlayPlacementGeometry
{
    public const double DefaultGap = 12;
    public const double WorkAreaMargin = 24;
    // Standard stock default; FH6's configured SafeFrameMargin is not read here.
    public const double NativeSafeFrameInset = 50;

    public static Point PlaceTopRight(Rect workArea, Size subject) =>
        ClampInside(
            workArea,
            subject,
            new Point(
                workArea.Right - WorkAreaMargin - subject.Width,
                workArea.Top + WorkAreaMargin));

    public static Point PlaceBottomRight(Rect workArea, Size subject) =>
        ClampInside(
            workArea,
            subject,
            new Point(
                workArea.Right - WorkAreaMargin - subject.Width,
                workArea.Bottom - WorkAreaMargin - subject.Height));

    public static Point PlaceNativeBottomRight(Rect workArea, Size subject, double referenceScale)
        => PlaceNativeBottomRight(workArea, subject, new Rect(subject), referenceScale);

    public static Rect NativeContentAnchorBounds(
        NativeGaugeMode gaugeMode,
        bool isElectric,
        double widthScale,
        double heightScale)
    {
        // HUDSpeedometerControl.xaml: native template size plus external margin.
        // Fixed Wisp hosts center the negative-margin Digital/EV roots, so their
        // content origin is included; the ICE Analogue root is top-left aligned.
        var anchor = gaugeMode == NativeGaugeMode.Analogue
            ? isElectric
                ? new Rect(10, 28, 325, 289)
                : new Rect(0, 0, 293, 282.5)
            : new Rect(0, 7.5, 320, 145);
        return new Rect(
            anchor.X * widthScale,
            anchor.Y * heightScale,
            anchor.Width * widthScale,
            anchor.Height * heightScale);
    }

    public static Point PreserveAnchorPosition(Point position, Rect previousAnchor, Rect nextAnchor) =>
        new(
            position.X + previousAnchor.Right - nextAnchor.Right,
            position.Y + previousAnchor.Bottom - nextAnchor.Bottom);

    public static Point PlaceNativeBottomRight(
        Rect monitorBounds,
        Size renderSize,
        Rect nativeAnchorBounds,
        double referenceScale,
        double dpiScaleX = 1,
        double dpiScaleY = 1)
    {
        referenceScale = double.IsFinite(referenceScale) && referenceScale > 0
            ? referenceScale
            : 1;

        // Anchor the authored content, not the transparent HWND's overflow padding.
        return ClampNativeInside(
            monitorBounds,
            renderSize,
            new Point(
                SnapToDevicePixel(
                    monitorBounds.Right - (NativeSafeFrameInset * referenceScale) - nativeAnchorBounds.Right,
                    dpiScaleX),
                SnapToDevicePixel(
                    monitorBounds.Bottom - (NativeSafeFrameInset * referenceScale) - nativeAnchorBounds.Bottom,
                    dpiScaleY)));
    }

    private static double SnapToDevicePixel(double value, double dpiScale)
    {
        dpiScale = double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1;
        return Math.Round(value * dpiScale, MidpointRounding.AwayFromZero) / dpiScale;
    }

    public static Point ClampNativeInside(Rect monitorBounds, Size subject, Point requested)
    {
        // Native insets are already reference-scaled. A second, fixed 24-DIP
        // margin moves the HUD inward at high display DPI, even when it fits.
        var maximumLeft = Math.Max(monitorBounds.Left, monitorBounds.Right - subject.Width);
        var maximumTop = Math.Max(monitorBounds.Top, monitorBounds.Bottom - subject.Height);
        return new Point(
            Math.Clamp(requested.X, monitorBounds.Left, maximumLeft),
            Math.Clamp(requested.Y, monitorBounds.Top, maximumTop));
    }

    public static double NativeReferenceScale(double monitorPixelHeight, double dpiScaleY)
    {
        if (!double.IsFinite(monitorPixelHeight) || monitorPixelHeight <= 0 ||
            !double.IsFinite(dpiScaleY) || dpiScaleY <= 0)
        {
            return 1;
        }

        return Math.Clamp(monitorPixelHeight / 1080 / dpiScaleY, 0.5, 2);
    }

    public static Point PlaceBelow(Rect workArea, Rect anchor, Size subject)
    {
        var left = anchor.Right - subject.Width;
        var top = anchor.Bottom + DefaultGap;
        if (top + subject.Height > workArea.Bottom - WorkAreaMargin)
        {
            top = anchor.Top - DefaultGap - subject.Height;
        }

        return ClampInside(workArea, subject, new Point(left, top));
    }

    public static Point PlaceAbove(Rect workArea, Rect anchor, Size subject)
    {
        var left = anchor.Right - subject.Width;
        var top = anchor.Top - DefaultGap - subject.Height;
        if (top < workArea.Top + WorkAreaMargin)
        {
            top = anchor.Bottom + DefaultGap;
        }

        return ClampInside(workArea, subject, new Point(left, top));
    }

    public static Point ClampInside(Rect workArea, Size subject, Point requested)
    {
        var minimumLeft = workArea.Left + WorkAreaMargin;
        var maximumLeft = Math.Max(minimumLeft, workArea.Right - WorkAreaMargin - subject.Width);
        var minimumTop = workArea.Top + WorkAreaMargin;
        var maximumTop = Math.Max(minimumTop, workArea.Bottom - WorkAreaMargin - subject.Height);
        return new Point(
            Math.Clamp(requested.X, minimumLeft, maximumLeft),
            Math.Clamp(requested.Y, minimumTop, maximumTop));
    }

    public static Point PlaceAdjacentHorizontally(Rect workArea, Rect anchor, Size subject)
    {
        var minimumLeft = workArea.Left + WorkAreaMargin;
        var maximumLeft = Math.Max(minimumLeft, workArea.Right - WorkAreaMargin - subject.Width);
        var left = anchor.Left - DefaultGap - subject.Width;
        if (left < minimumLeft)
        {
            left = anchor.Right + DefaultGap;
        }

        left = Math.Clamp(left, minimumLeft, maximumLeft);

        var minimumTop = workArea.Top + WorkAreaMargin;
        var maximumTop = Math.Max(minimumTop, workArea.Bottom - WorkAreaMargin - subject.Height);
        var top = anchor.Top + ((anchor.Height - subject.Height) / 2);
        top = Math.Clamp(top, minimumTop, maximumTop);

        return new Point(left, top);
    }
}
