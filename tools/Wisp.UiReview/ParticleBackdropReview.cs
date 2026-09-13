using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wisp.App;

namespace Wisp.UiReview;

internal static class ParticleBackdropReview
{
    private const int WarmingFrames = 120;
    private const int MeasuredFrames = 600;
    private static readonly Color Accent = Color.FromRgb(99, 216, 212);
    private static readonly SolidColorBrush PreviewBackground = FrozenBrush(Color.FromRgb(13, 25, 27));

    internal static int Run(string output)
    {
        var results = new List<object>();
        var captures = new List<object>();
        foreach (var (width, height, dpiScale) in new[]
        {
            (1280, 720, 1d), (1920, 1080, 1d), (3840, 2160, 1d), (1920, 1080, 2d)
        })
        {
            var viewport = new Size(width, height);
            var identifier = $"{width}x{height}-dpi{(int)(dpiScale * 96)}";
            var baseline = BenchmarkBaseline(viewport);
            var current = BenchmarkSprites(viewport, dpiScale);
            results.Add(new
            {
                identifier,
                viewportWidthDips = width,
                viewportHeightDips = height,
                dpi = dpiScale * 96,
                physicalWidth = (int)(width * dpiScale),
                physicalHeight = (int)(height * dpiScale),
                baseline,
                current
            });

            foreach (var (name, color) in new[]
            {
                ("accent", Accent), ("pink", Colors.HotPink), ("off", Colors.Transparent)
            })
            {
                var frame = RenderSprites(viewport, dpiScale, color);
                captures.Add(SaveCapture(output, $"particles-{identifier}-{name}", frame));
            }
            captures.Add(SaveCapture(output, $"particles-{identifier}-baseline-accent",
                RenderBaseline(viewport, dpiScale)));
        }

        File.WriteAllText(Path.Combine(output, "particle-review.json"), JsonSerializer.Serialize(new
        {
            measurement = "Synchronous UI-thread preparation only: scene update plus CPU raster/WritePixels call for the former path, or cached sprite lookup plus DrawingContext command recording for the current path. Excludes Windows composition, GPU execution/completion, displayed frame cadence and gameplay. No live window, telemetry or wall-clock frame scheduling is used.",
            coldFrame = "First prepared frame after constructing each renderer. Includes its initially empty sprite/material caches; the first case can also include JIT work. Cold and warming results are reported separately, not omitted.",
            warmingFrames = WarmingFrames,
            measuredFrames = MeasuredFrames,
            sceneTime = "Each path advances at its intended cadence: 20 Hz baseline or 60 Hz current. The 600 measured current frames represent 10 seconds of simulated animation, and the baseline frames represent 30 seconds. Preparation milliseconds per simulated second is a CPU-work estimate, not a frame-delivery measurement.",
            baselineMaximumPixels = AmbientBackdropRasterizer.MaximumParticlePixels,
            maximumCachedSpriteCount = AmbientParticleSprites.MaximumCachedSprites,
            maximumCachedSpritePixelBytes = AmbientParticleSprites.MaximumCachedPixelBytes,
            imageNotes = "PNG exports use native physical dimensions and the same fixed dark background solely for review. The renderer itself draws transparent particles only. Crop exports are untouched physical pixels at 1:1 scale; they are not enlarged.",
            results,
            captures
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Particle preparation timings, native-resolution images and 1:1 crops saved. GPU/compositor performance and gameplay are not measured.");
        return 0;
    }

    private static object BenchmarkBaseline(Size viewport)
    {
        var (pixelWidth, pixelHeight) = BaselineSize(viewport);
        var scene = new AmbientBackdropScene();
        var rasterizer = new AmbientBackdropRasterizer(pixelWidth, pixelHeight, particlesOnly: true);
        var bitmap = new WriteableBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32, null);
        var visual = new DrawingVisual();
        void Step(double seconds)
        {
            scene.Update(pixelWidth, pixelHeight, seconds);
            rasterizer.Render(scene.Particles, 1, Accent);
            bitmap.WritePixels(new Int32Rect(0, 0, pixelWidth, pixelHeight),
                rasterizer.Pixels, pixelWidth * 4, 0);
            using var drawing = visual.RenderOpen();
            drawing.DrawImage(bitmap, new Rect(viewport));
        }

        return new
        {
            nominalFramesPerSecond = 20,
            pixelWidth,
            pixelHeight,
            cold = MeasurePhase(Step, static () => 0, 0, 1, 20),
            warming = MeasurePhase(Step, static () => 0, 1, WarmingFrames, 20),
            warm = MeasurePhase(Step, static () => 0, WarmingFrames + 1, MeasuredFrames, 20)
        };
    }

    private static object BenchmarkSprites(Size viewport, double dpiScale)
    {
        var scene = new AmbientBackdropScene();
        var sprites = new AmbientParticleSprites();
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.LowQuality);
        var drawCount = 0;
        void Step(double seconds)
        {
            scene.Update(viewport.Width, viewport.Height, seconds);
            using var drawing = visual.RenderOpen();
            drawCount = sprites.Draw(drawing, scene.Particles, viewport, Accent, 1,
                new DpiScale(dpiScale, dpiScale));
        }

        var cold = MeasurePhase(Step, () => sprites.CreatedSpriteCount, 0, 1, 60);
        var afterCold = CacheState(sprites);
        var warming = MeasurePhase(Step, () => sprites.CreatedSpriteCount, 1, WarmingFrames, 60);
        var afterWarming = CacheState(sprites);
        var warm = MeasurePhase(Step, () => sprites.CreatedSpriteCount, WarmingFrames + 1, MeasuredFrames, 60);
        return new
        {
            nominalFramesPerSecond = 60,
            cold,
            afterCold,
            warming,
            afterWarming,
            warm,
            afterWarm = CacheState(sprites),
            particlesDrawnInFinalFrame = drawCount
        };
    }

    private static object CacheState(AmbientParticleSprites sprites) => new
    {
        cachedSprites = sprites.CachedSpriteCount,
        cachedSpritePixelBytes = sprites.CachedPixelBytes,
        cachedCoverageBytes = sprites.CachedCoverageBytes,
        spritesCreatedSinceConstruction = sprites.CreatedSpriteCount
    };

    private static object MeasurePhase(Action<double> step, Func<int> createdSprites,
        int firstFrame, int frameCount, int framesPerSecond)
    {
        var milliseconds = new double[frameCount];
        var allocatedBytes = new long[frameCount];
        var creations = new int[frameCount];
        for (var index = 0; index < frameCount; index++)
        {
            var beforeCreations = createdSprites();
            var beforeAllocated = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            step((firstFrame + index) / (double)framesPerSecond);
            milliseconds[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            allocatedBytes[index] = GC.GetAllocatedBytesForCurrentThread() - beforeAllocated;
            creations[index] = createdSprites() - beforeCreations;
        }
        Array.Sort(milliseconds);
        Array.Sort(allocatedBytes);
        var p95 = Math.Clamp((int)Math.Ceiling(frameCount * 0.95) - 1, 0, frameCount - 1);
        return new
        {
            frames = frameCount,
            simulatedSeconds = frameCount / (double)framesPerSecond,
            meanMilliseconds = milliseconds.Average(),
            p95Milliseconds = milliseconds[p95],
            maximumMilliseconds = milliseconds[^1],
            estimatedPreparationMillisecondsPerSimulatedSecond = milliseconds.Average() * framesPerSecond,
            meanAllocatedBytesPerFrame = allocatedBytes.Average(),
            p95AllocatedBytesPerFrame = allocatedBytes[p95],
            maximumAllocatedBytesPerFrame = allocatedBytes[^1],
            newSprites = creations.Sum(),
            framesCreatingSprites = creations.Count(value => value > 0),
            maximumNewSpritesInAFrame = creations.Max()
        };
    }

    private static RenderTargetBitmap RenderSprites(Size viewport, double dpiScale, Color color)
    {
        var scene = new AmbientBackdropScene();
        scene.Update(viewport.Width, viewport.Height, 6);
        var sprites = new AmbientParticleSprites();
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.LowQuality);
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(PreviewBackground, null, new Rect(viewport));
            sprites.Draw(drawing, scene.Particles, viewport, color, 1, new DpiScale(dpiScale, dpiScale));
        }
        return Render(visual, viewport, dpiScale);
    }

    private static RenderTargetBitmap RenderBaseline(Size viewport, double dpiScale)
    {
        var (width, height) = BaselineSize(viewport);
        var scene = new AmbientBackdropScene();
        scene.Update(width, height, 6);
        var rasterizer = new AmbientBackdropRasterizer(width, height, particlesOnly: true);
        rasterizer.Render(scene.Particles, 1, Accent);
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32,
            null, rasterizer.Pixels, width * 4);
        source.Freeze();
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(PreviewBackground, null, new Rect(viewport));
            drawing.DrawImage(source, new Rect(viewport));
        }
        return Render(visual, viewport, dpiScale);
    }

    private static RenderTargetBitmap Render(DrawingVisual visual, Size viewport, double dpiScale)
    {
        var target = new RenderTargetBitmap((int)(viewport.Width * dpiScale), (int)(viewport.Height * dpiScale),
            dpiScale * 96, dpiScale * 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static object SaveCapture(string output, string name, BitmapSource bitmap)
    {
        var fullName = name + "-native.png";
        SavePng(Path.Combine(output, fullName), bitmap);
        var cropWidth = Math.Min(768, bitmap.PixelWidth);
        var cropHeight = Math.Min(512, bitmap.PixelHeight);
        var cropRect = new Int32Rect((bitmap.PixelWidth - cropWidth) / 3,
            (bitmap.PixelHeight - cropHeight) / 2, cropWidth, cropHeight);
        var crop = new CroppedBitmap(bitmap, cropRect);
        crop.Freeze();
        var cropName = name + "-crop-1to1.png";
        SavePng(Path.Combine(output, cropName), crop);
        return new
        {
            image = fullName,
            width = bitmap.PixelWidth,
            height = bitmap.PixelHeight,
            crop = cropName,
            cropX = cropRect.X,
            cropY = cropRect.Y,
            cropWidth = cropRect.Width,
            cropHeight = cropRect.Height
        };
    }

    private static void SavePng(string path, BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static (int Width, int Height) BaselineSize(Size viewport)
    {
        var scale = Math.Min(1, Math.Sqrt(AmbientBackdropRasterizer.MaximumParticlePixels /
            (viewport.Width * viewport.Height)));
        return ((int)Math.Floor(viewport.Width * scale), (int)Math.Floor(viewport.Height * scale));
    }

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
