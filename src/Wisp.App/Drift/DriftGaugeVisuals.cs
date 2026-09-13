using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wisp.App.NativeRendering;
using Wisp.Core;

namespace Wisp.App.Drift;

internal sealed record DriftGaugePresentation(int Width, int Height, float DpiScaleX, float DpiScaleY,
    float Opacity, bool Active, double TargetDegrees, double ToleranceDegrees, bool DarkMode = false,
    DriftGaugeGuidanceMode GuidanceMode = DriftGaugeGuidanceMode.CustomTarget, DriftZoneScoringProfile? ZoneProfile = null,
    bool BackgroundEnabled = false, double BackgroundOpacity = .5);

internal static class DriftGaugeVisuals
{
    internal const double DesignWidth = 620;
    internal const double DesignHeight = 108;
    internal const int MaximumCommands = 64;
    private const double Left = 24, Right = 596;
    private const uint WhiteTexture = 1, GaugeTexture = 2, GlyphTexture = 3, GaugeHaloTexture = 4, GlyphHaloTexture = 5, MarkerHaloTexture = 6;
    private const uint ZoneGaugeTexture = 7, ZoneGaugeHaloTexture = 8;
    private const uint ZoneGuideTexture = 9;
    private const uint BonusCaptionTexture = 10, BonusCaptionHaloTexture = 11;
    private const uint BackgroundTexture = 12;
    private const int BonusCaptionWidth = 116, BonusCaptionHeight = 15;
    private const double BonusCaptionX = 480, BonusCaptionY = 91, BonusY = 58;
    internal const double AngleFontSize = 40, AngleY = 50, BonusFontSize = 28, GuidanceY = 87;
    private const int CellWidth = 36, CellHeight = 56, AtlasColumns = 16;
    private const int HaloPadding = 4, HaloCellWidth = CellWidth + HaloPadding * 2, HaloCellHeight = CellHeight + HaloPadding * 2;
    private const string Glyphs = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ +-−°.%";
    private static readonly Typeface Typeface = new(new FontFamily("Bahnschrift SemiCondensed, Bahnschrift, Segoe UI"),
        FontStyles.Italic, FontWeights.SemiBold, FontStretches.Condensed);
    private static readonly AnalogHudColor White = new(255, 255, 255);
    private static readonly AnalogHudColor Pink = new(255, 0, 136);
    private static readonly AnalogHudColor Red = new(255, 90, 95);
    private static readonly AnalogHudColor Yellow = new(255, 205, 76);
    private static readonly AnalogHudColor Green = new(124, 242, 201);
    private static readonly AnalogHudColor Shade = new(8, 12, 17);
    private static readonly AnalogHudColor Black = new(0, 0, 0);
    private static readonly string[] DegreeLabels = Enumerable.Range(-180, 361)
        .Select(value => (value < 0 ? "−" : value > 0 ? "+" : "") + Math.Abs(value).ToString(CultureInfo.InvariantCulture) + "°").ToArray();
    private static readonly string[] MagnitudeLabels = Enumerable.Range(0, 1801)
        .Select(value => (value / 10d).ToString("F1", CultureInfo.InvariantCulture) + "°").ToArray();
    private static readonly string[] BonusLabels = Enumerable.Range(0, 101)
        .Select(value => value.ToString(CultureInfo.InvariantCulture) + "%").ToArray();
    private static readonly DriftZoneScoringProfile? ZoneArtworkProfile =
        DriftZoneProfileCatalog.ForBuild(NativeHudBuildContract.BuiltIn);
    private static readonly Dictionary<char, (Rect Bounds, Rect HaloBounds, double Advance)> GlyphBounds = [];
    private static IReadOnlyList<AnalogHudTexture>? _textures;
    private static IReadOnlyDictionary<uint, BitmapSource>? _images;

    internal static IReadOnlyList<AnalogHudTexture> LoadOnUiThread()
    {
        Application.Current?.Dispatcher.VerifyAccess();
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Drift gauge artwork must be prepared on the UI thread.");
        if (_textures is not null) return _textures;
        var glyphRows = (Glyphs.Length + AtlasColumns - 1) / AtlasColumns;
        var glyphAtlas = Rasterize(AtlasColumns * CellWidth, glyphRows * CellHeight, 1, drawing =>
        {
            for (var index = 0; index < Glyphs.Length; index++)
            {
                var x = index % AtlasColumns * CellWidth;
                var y = index / AtlasColumns * CellHeight;
                var text = Text(Glyphs[index].ToString(), 40, Brushes.White);
                drawing.DrawText(text, new Point(x + 2, y));
                GlyphBounds[Glyphs[index]] = (new Rect(x, y, CellWidth, CellHeight),
                    new Rect(index % AtlasColumns * HaloCellWidth, index / AtlasColumns * HaloCellHeight, HaloCellWidth, HaloCellHeight),
                    Math.Min(CellWidth - 2, text.WidthIncludingTrailingWhitespace));
            }
        });
        var gauge = Rasterize((int)DesignWidth, (int)DesignHeight, 2, drawing => DrawScale(drawing, 90, false));
        var zoneGauge = Rasterize((int)DesignWidth, (int)DesignHeight, 2, drawing => DrawScale(drawing, 90, true));
        var zoneGuide = Rasterize((int)DesignWidth, (int)DesignHeight, 2, drawing => DrawScale(drawing, 90, true, ZoneArtworkProfile));
        var bonusCaption = Rasterize(BonusCaptionWidth, BonusCaptionHeight, 2,
            drawing => drawing.DrawText(Text("MAX ANGLE BONUS", 12, Brushes.White), new Point(0, 0)));
        var bonusCaptionHalo = Rasterize(BonusCaptionWidth + 8, BonusCaptionHeight + 8, 2,
            drawing => drawing.DrawText(Text("MAX ANGLE BONUS", 12, Brushes.White), new Point(4, 4)));
        var background = Rasterize((int)DesignWidth, (int)DesignHeight, 2,
            drawing => drawing.DrawRoundedRectangle(Brushes.White, null, new Rect(2, 2, DesignWidth - 4, DesignHeight - 4), 12, 12));
        var glyphHaloSource = Rasterize(AtlasColumns * HaloCellWidth, glyphRows * HaloCellHeight, 1, drawing =>
        {
            for (var index = 0; index < Glyphs.Length; index++)
                drawing.DrawText(Text(Glyphs[index].ToString(), 40, Brushes.White),
                    new Point(index % AtlasColumns * HaloCellWidth + HaloPadding + 2, index / AtlasColumns * HaloCellHeight + HaloPadding));
        });
        var markerHaloSource = Rasterize(16, 44, 2, drawing =>
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(6.5, 6, 3, 34));
            drawing.DrawRectangle(Brushes.White, null, new Rect(4, 4, 8, 3));
        });
        var white = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Pbgra32, null, new byte[] { 255, 255, 255, 255 }, 4);
        white.Freeze();
        _images = new Dictionary<uint, BitmapSource>
        {
            [WhiteTexture] = white,
            [GaugeTexture] = gauge,
            [GlyphTexture] = glyphAtlas,
            [GaugeHaloTexture] = CreateHalo(gauge, 2),
            [GlyphHaloTexture] = CreateHalo(glyphHaloSource, 1),
            [MarkerHaloTexture] = CreateHalo(markerHaloSource, 2),
            [ZoneGaugeTexture] = zoneGauge,
            [ZoneGaugeHaloTexture] = CreateHalo(zoneGauge, 2),
            [ZoneGuideTexture] = zoneGuide,
            [BonusCaptionTexture] = bonusCaption,
            [BonusCaptionHaloTexture] = CreateHalo(bonusCaptionHalo, 2),
            [BackgroundTexture] = background
        };
        _textures = _images.Select(item =>
        {
            var stride = item.Value.PixelWidth * 4;
            var pixels = new byte[stride * item.Value.PixelHeight];
            item.Value.CopyPixels(pixels, stride, 0);
            return new AnalogHudTexture(item.Key, item.Value.PixelWidth, item.Value.PixelHeight, stride, pixels);
        }).ToArray();
        return _textures;
    }

    // CPU scene preparation only: the worker uses cached textures and a reused command array.
    internal static int Build(DirectCompositionDrawCommand[] commands, DriftGaugePresentation presentation, DriftAngleReading reading)
    {
        if (commands.Length < MaximumCommands) throw new ArgumentException("Drift command buffer is too small.", nameof(commands));
        if (reading.State is DriftGuidanceState.LowSpeed or DriftGuidanceState.Unavailable or DriftGuidanceState.Reversing)
            reading = reading with { SignedDegrees = null, MagnitudeDegrees = null };
        var zone = presentation.GuidanceMode == DriftGaugeGuidanceMode.DriftZoneAngleBonus;
        const int range = 90;
        var target = double.IsFinite(presentation.TargetDegrees) ? Math.Clamp(presentation.TargetDegrees, 10, 75) : 40;
        var tolerance = double.IsFinite(presentation.ToleranceDegrees) ? Math.Clamp(presentation.ToleranceDegrees, 2, 15) : 10;
        var low = Math.Max(0, target - tolerance);
        var high = Math.Min(90, target + tolerance);
        var showBand = !zone;
        // The cached colored scale belongs to this verified scoring profile only.
        // It is one sprite, not a separate draw call for each tick.
        var verifiedGuide = zone && presentation.ZoneProfile is { IsValid: true } &&
            presentation.ZoneProfile == ZoneArtworkProfile;
        var count = 0;
        var backgroundOpacity = double.IsFinite(presentation.BackgroundOpacity) ? Math.Clamp(presentation.BackgroundOpacity, 0, 1) : .5;
        if (presentation.BackgroundEnabled && backgroundOpacity > 0)
            Add(BackgroundTexture, new Rect(0, 0, DesignWidth, DesignHeight), Black, backgroundOpacity);
        if (presentation.DarkMode)
            Add(zone ? ZoneGaugeHaloTexture : GaugeHaloTexture, new Rect(0, 0, DesignWidth, DesignHeight), Shade);
        var highlighted = !zone && reading.State == DriftGuidanceState.OnTarget;
        if (showBand)
        {
            Add(WhiteTexture, new Rect(X(-high, range), 12, X(-low, range) - X(-high, range), 24), Green, highlighted ? .26 : .12);
            Add(WhiteTexture, new Rect(X(low, range), 12, X(high, range) - X(low, range), 24), Green, highlighted ? .26 : .12);
        }
        Add(zone ? verifiedGuide ? ZoneGuideTexture : ZoneGaugeTexture : GaugeTexture,
            new Rect(0, 0, DesignWidth, DesignHeight), White);
        var tooHigh = zone ? reading.State == DriftGuidanceState.AboveScoringAngle : reading.State == DriftGuidanceState.AboveTarget;
        var hasScoringState = reading.State is DriftGuidanceState.BelowScoringAngle or
            DriftGuidanceState.AngleBonusIncreasing or DriftGuidanceState.MaximumAngleBonus or DriftGuidanceState.AboveScoringAngle;
        var color = zone ? verifiedGuide && hasScoringState &&
            reading.MagnitudeDegrees is { } measuredAngle
                ? ZoneColor(measuredAngle, presentation.ZoneProfile!) : White
            : tooHigh ? Pink : highlighted ? Green : White;
        if (reading.SignedDegrees is { } angle && double.IsFinite(angle))
        {
            var marker = X(Math.Clamp(angle, -range, range), range);
            if (presentation.DarkMode) Add(MarkerHaloTexture, new Rect(marker - 8, 1, 16, 44), Shade);
            Add(WhiteTexture, new Rect(marker - 1.5, 7, 3, 34), color);
            Add(WhiteTexture, new Rect(marker - 4, 5, 8, 3), color);
        }
        var value = zone ? reading.MagnitudeDegrees : reading.SignedDegrees;
        if (value is { } degrees && double.IsFinite(degrees))
        {
            var rounded = (int)Math.Round(Math.Clamp(degrees, zone ? 0 : -180, 180) * (zone ? 10 : 1), MidpointRounding.AwayFromZero);
            AddText(zone ? MagnitudeLabels[rounded] : DegreeLabels[rounded + 180], DesignWidth / 2, AngleY, AngleFontSize, color, centered: true);
        }
        else AddText("−−°", DesignWidth / 2, AngleY, AngleFontSize, White, centered: true);
        if (verifiedGuide && (reading.State is DriftGuidanceState.AngleBonusIncreasing or DriftGuidanceState.MaximumAngleBonus) &&
            AngleBonusPercent(reading.MagnitudeDegrees, presentation.ZoneProfile) is { } percent)
        {
            // Cache the fixed caption as one sprite instead of drawing its letters each frame.
            AddText(BonusLabels[percent], Right, BonusY, BonusFontSize, color, rightAligned: true);
            if (presentation.DarkMode)
            {
                var padded = new Rect(BonusCaptionX - 4, BonusCaptionY - 4, BonusCaptionWidth + 8, BonusCaptionHeight + 8);
                var visible = Rect.Intersect(padded, new Rect(0, 0, DesignWidth, DesignHeight));
                Add(BonusCaptionHaloTexture, visible, Shade);
                commands[count - 1].UvBottom = (float)(visible.Height / padded.Height);
            }
            Add(BonusCaptionTexture, new Rect(BonusCaptionX, BonusCaptionY, BonusCaptionWidth, BonusCaptionHeight), color);
        }
        var guidance = reading.State switch
        {
            DriftGuidanceState.LowSpeed => "LOW SPEED",
            DriftGuidanceState.Reversing => "REVERSING",
            DriftGuidanceState.BelowTarget => "BELOW TARGET",
            DriftGuidanceState.OnTarget => "ON TARGET",
            DriftGuidanceState.AboveTarget => "OVER TARGET",
            DriftGuidanceState.ProfileUnavailable => "UNVERIFIED BUILD",
            DriftGuidanceState.BelowScoringAngle => "BELOW SCORING ANGLE",
            DriftGuidanceState.AngleBonusIncreasing => "SCORING ANGLE",
            DriftGuidanceState.MaximumAngleBonus => verifiedGuide && reading.MagnitudeDegrees >
                CeilingTick(presentation.ZoneProfile!) ? "NO EXTRA BONUS" : "FULL ANGLE BONUS",
            DriftGuidanceState.AboveScoringAngle => "ANGLE TOO HIGH",
            _ => "NO DATA"
        };
        AddText(guidance, Left, GuidanceY, 13, color);
        var scale = Math.Min(presentation.Width / DesignWidth, presentation.Height / DesignHeight);
        var offsetX = (presentation.Width - DesignWidth * scale) / 2;
        var offsetY = (presentation.Height - DesignHeight * scale) / 2;
        for (var index = 0; index < count; index++)
        {
            ref var command = ref commands[index];
            command.OriginX = (float)(offsetX + command.OriginX * scale);
            command.OriginY = (float)(offsetY + command.OriginY * scale);
            command.AxisXX *= (float)scale; command.AxisYY *= (float)scale;
        }
        return count;

        void Add(uint texture, Rect rectangle, AnalogHudColor tint, double opacity = 1)
        {
            commands[count++] = AnalogHudScene.Quad(texture,
                new AnalogHudRect(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height), opacity: opacity, color: tint);
        }
        void AddText(string value, double x, double y, double size, AnalogHudColor tint, bool centered = false, bool rightAligned = false)
        {
            var factor = size / 40;
            var width = 0d;
            foreach (var character in value) width += GlyphBounds[character].Advance * factor;
            if (centered) x -= width / 2;
            else if (rightAligned) x -= width;
            var glyphRows = (Glyphs.Length + AtlasColumns - 1) / AtlasColumns;
            var atlasHeight = glyphRows * CellHeight;
            for (var pass = presentation.DarkMode ? 0 : 1; pass < 2; pass++)
            {
                var glyphX = x;
                foreach (var character in value)
                {
                    var glyph = GlyphBounds[character];
                    if (character != ' ' && pass == 0)
                    {
                        var padded = new Rect(glyphX - HaloPadding * factor, y - HaloPadding * factor,
                            HaloCellWidth * factor, HaloCellHeight * factor);
                        var visible = Rect.Intersect(padded, new Rect(0, 0, DesignWidth, DesignHeight));
                        // Keep the atlas padding inside the gauge without stretching its mask.
                        Add(GlyphHaloTexture, visible, Shade);
                        ref var halo = ref commands[count - 1];
                        halo.UvLeft = (float)((glyph.HaloBounds.Left + (visible.Left - padded.Left) / factor) / (AtlasColumns * HaloCellWidth));
                        halo.UvRight = (float)((glyph.HaloBounds.Right - (padded.Right - visible.Right) / factor) / (AtlasColumns * HaloCellWidth));
                        halo.UvTop = (float)((glyph.HaloBounds.Top + (visible.Top - padded.Top) / factor) / (glyphRows * HaloCellHeight));
                        halo.UvBottom = (float)((glyph.HaloBounds.Bottom - (padded.Bottom - visible.Bottom) / factor) / (glyphRows * HaloCellHeight));
                    }
                    else if (character != ' ')
                    {
                        Add(GlyphTexture, new Rect(glyphX, y, CellWidth * factor, CellHeight * factor), tint);
                        ref var command = ref commands[count - 1];
                        command.UvLeft = (float)(glyph.Bounds.Left / (AtlasColumns * CellWidth));
                        command.UvRight = (float)(glyph.Bounds.Right / (AtlasColumns * CellWidth));
                        command.UvTop = (float)(glyph.Bounds.Top / atlasHeight);
                        command.UvBottom = (float)(glyph.Bounds.Bottom / atlasHeight);
                    }
                    glyphX += glyph.Advance * factor;
                }
            }
        }
    }

    internal static void Draw(DrawingContext drawing, Size size, DriftAngleReading reading, double targetDegrees, double toleranceDegrees, bool darkMode = false,
        DriftGaugeGuidanceMode guidanceMode = DriftGaugeGuidanceMode.CustomTarget, DriftZoneScoringProfile? zoneProfile = null,
        bool backgroundEnabled = false, double backgroundOpacity = .5)
    {
        LoadOnUiThread();
        var commands = new DirectCompositionDrawCommand[MaximumCommands];
        var presentation = new DriftGaugePresentation((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 1, 1, 1, true, targetDegrees, toleranceDegrees, darkMode, guidanceMode, zoneProfile,
            backgroundEnabled, backgroundOpacity);
        var count = Build(commands, presentation, reading);
        for (var index = 0; index < count; index++)
        {
            var command = commands[index];
            var source = _images![command.TextureId];
            var crop = new CroppedBitmap(source, new Int32Rect(
                (int)Math.Round(command.UvLeft * source.PixelWidth), (int)Math.Round(command.UvTop * source.PixelHeight),
                (int)Math.Round((command.UvRight - command.UvLeft) * source.PixelWidth), (int)Math.Round((command.UvBottom - command.UvTop) * source.PixelHeight)));
            crop.Freeze();
            var rectangle = new Rect(command.OriginX, command.OriginY, command.AxisXX, command.AxisYY);
            if (command.TextureId is GaugeTexture or ZoneGaugeTexture or ZoneGuideTexture) drawing.DrawImage(crop, rectangle);
            else
            {
                var mask = new ImageBrush(crop) { Stretch = Stretch.Fill }; mask.Freeze();
                drawing.PushOpacityMask(mask);
                drawing.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)Math.Round(command.TintA * 255),
                    (byte)Math.Round(command.TintR * 255), (byte)Math.Round(command.TintG * 255), (byte)Math.Round(command.TintB * 255))), null, rectangle);
                drawing.Pop();
            }
        }
    }

    private static double X(double degrees, double range = 90) => Left + (degrees + range) / (2 * range) * (Right - Left);
    internal static int? AngleBonusPercent(double? degrees, DriftZoneScoringProfile? profile)
    {
        if (degrees is not { } angle || profile is not { IsValid: true } || !double.IsFinite(angle) ||
            angle < profile.MinimumAngleDegrees || angle > profile.MaximumAngleDegrees) return null;
        var multiplier = DriftZoneScoring.EvaluateAngleMultiplier(angle, profile)!.Value;
        return angle >= profile.SaturationAngleDegrees ? 100 :
            Math.Clamp((int)Math.Floor(100 * multiplier / profile.MaximumAngleMultiplier), 0, 99);
    }

    // The nearest upper scale tick locates the ceiling; it is not another scoring threshold.
    private static double CeilingTick(DriftZoneScoringProfile profile) => Math.Ceiling(profile.SaturationAngleDegrees / 5) * 5;

    private static AnalogHudColor ZoneColor(double angle, DriftZoneScoringProfile profile)
    {
        if (!double.IsFinite(angle)) return White;
        if (angle < profile.MinimumAngleDegrees || angle > profile.MaximumAngleDegrees) return Red;
        if (angle > CeilingTick(profile)) return Yellow;
        var fraction = Math.Clamp((angle - profile.MinimumAngleDegrees) /
            (profile.SaturationAngleDegrees - profile.MinimumAngleDegrees), 0, 1);
        return new((byte)Math.Round(Yellow.R + (Green.R - Yellow.R) * fraction),
            (byte)Math.Round(Yellow.G + (Green.G - Yellow.G) * fraction),
            (byte)Math.Round(Yellow.B + (Green.B - Yellow.B) * fraction));
    }

    private static void DrawScale(DrawingContext drawing, int range, bool zone, DriftZoneScoringProfile? guide = null)
    {
        var major = FrozenBrush(White, .65);
        var minor = FrozenBrush(White, .35);
        for (var degrees = -range; degrees <= range; degrees += 5)
        {
            var tall = degrees % 10 == 0;
            var tickBrush = guide is { IsValid: true } ? FrozenBrush(ZoneColor(Math.Abs(degrees), guide), tall ? .95 : .7) : tall ? major : minor;
            drawing.DrawRectangle(tickBrush, null, new Rect(X(degrees, range) - .75, tall ? 16 : 23, 1.5, tall ? 17 : 10));
            if (degrees % 30 != 0 && !(zone && Math.Abs(degrees) == 10)) continue;
            var text = Text((degrees < 0 ? "−" : degrees > 0 ? "+" : "") + Math.Abs(degrees), 14, major);
            drawing.DrawText(text, new Point(X(degrees, range) - text.Width / 2, 39));
        }
        drawing.DrawText(Text(zone ? "DRIFT ZONE" : "DRIFT ANGLE", 15, major), new Point(Left, 65));
    }
    private static FormattedText Text(string text, double size, Brush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface, size, brush, 1);
    private static Brush FrozenBrush(AnalogHudColor color, double opacity)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * opacity), color.R, color.G, color.B)); brush.Freeze(); return brush;
    }
    // Prepare a small soft outline once. Transparent areas between the artwork stay clear;
    // dark mode only adds cached alpha masks, never a panel or per-frame blur effect.
    private static BitmapSource CreateHalo(BitmapSource source, int scale)
    {
        var width = source.PixelWidth; var height = source.PixelHeight; var stride = width * 4;
        var pixels = new byte[stride * height]; source.CopyPixels(pixels, stride, 0);
        var spread = new byte[width * height];
        var horizontal = new byte[spread.Length];
        var radius = 2 * scale;
        var weight = (radius + 1) * (radius + 1);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var alpha = 0;
                for (var dy = -scale; dy <= scale; dy++)
                    for (var dx = -scale; dx <= scale; dx++)
                        if (x + dx >= 0 && x + dx < width && y + dy >= 0 && y + dy < height)
                            alpha = Math.Max(alpha, Math.Min(255, pixels[((y + dy) * width + x + dx) * 4 + 3] * 3));
                spread[y * width + x] = (byte)alpha;
            }
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var alpha = 0;
                for (var dx = -radius; dx <= radius; dx++)
                    if (x + dx >= 0 && x + dx < width)
                        alpha += spread[y * width + x + dx] * (radius + 1 - Math.Abs(dx));
                horizontal[y * width + x] = (byte)(alpha / weight);
            }
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var alpha = 0;
                for (var dy = -radius; dy <= radius; dy++)
                    if (y + dy >= 0 && y + dy < height)
                        alpha += horizontal[(y + dy) * width + x] * (radius + 1 - Math.Abs(dy));
                var value = (byte)Math.Round(alpha / (double)weight * .92);
                var offset = (y * width + x) * 4;
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = pixels[offset + 3] = value;
            }
        var bitmap = BitmapSource.Create(width, height, source.DpiX, source.DpiY, PixelFormats.Pbgra32, null, pixels, stride);
        bitmap.Freeze(); return bitmap;
    }
    private static BitmapSource Rasterize(int width, int height, int scale, Action<DrawingContext> paint)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) paint(drawing);
        var bitmap = new RenderTargetBitmap(width * scale, height * scale, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }
}
