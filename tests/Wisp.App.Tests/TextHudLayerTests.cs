using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wisp.App.NativeRendering;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

internal static class TextHudLayerTests
{
    internal static void AssertOnCurrentDispatcher()
    {
        var shutdown = Application.Current.ShutdownMode;
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var controller = new AppController(new AppSettings
        {
            StartWithWindows = false,
            StartWithForza = false,
            AutomaticApplicationUpdateChecks = false,
            DebugLoggingEnabled = false,
            GForceEnabled = true,
            BoostGaugeEnabled = false,
            TireTemperatureGaugeEnabled = false,
            PowerGaugeEnabled = false,
            TorqueGaugeEnabled = false
        }, _ => { }, new NoStartupRegistration());
        var window = new OverlayWindow(controller);
        try
        {
            var root = (Grid)window.FindName("RootPanel");
            foreach (var name in new[] { "MinimalPanel", "BoxedSpeedPanel", "CombinedPanel" })
            {
                var panel = (FrameworkElement)window.FindName(name);
                panel.Visibility = Visibility.Visible;
                var text = Descendants(panel).OfType<TextBlock>().Single(element => element.FontSize == 104);
                BindingOperations.ClearBinding(text, TextBlock.TextProperty);
                text.Foreground = Brushes.White;
                var originalMask = panel.OpacityMask;
                var originalOpacity = text.Opacity;
                IReadOnlyList<AnalogHudTexture>? firstTextures = null;
                foreach (var speed in new[] { "1", "88", "137", "888", "—" })
                {
                    text.Text = speed;
                    Arrange(root);
                    // Real Viewbox children include WPF's internal visual container.
                    var snapshot = TextHudLayer.Capture(panel, null);
                    Assert.Same(originalMask, panel.OpacityMask);
                    Assert.Equal(originalOpacity, text.Opacity);
                    if (firstTextures is null) firstTextures = snapshot.Textures;
                    else Assert.Same(firstTextures, snapshot.Textures);
                    var playback = snapshot.CreatePlayback();
                    playback.Update(snapshot, 0);
                    var commands = playback.Build(0);
                    Assert.Equal(speed.Length + 1, commands.Length);
                    Assert.All(commands, command => Assert.Equal(DirectCompositionShader.Image, command.Shader));
                    var viewbox = Ancestors(text).OfType<Viewbox>().First();
                    var region = viewbox.TransformToAncestor(panel).TransformBounds(new Rect(viewbox.RenderSize));
                    var expected = InkBounds(Render(panel), region);
                    var actual = InkBounds(Render(commands, snapshot.Textures, panel.RenderSize), region);
                    Assert.False(expected.IsEmpty);
                    Assert.False(actual.IsEmpty);
                    AssertClose(expected.Left, actual.Left, name, speed);
                    AssertClose(expected.Top, actual.Top, name, speed);
                    AssertClose(expected.Right, actual.Right, name, speed);
                    AssertClose(expected.Bottom, actual.Bottom, name, speed);
                }
                // A changing readout reuses the decoded atlas; a border-theme change replaces the artwork.
                panel.Resources["HudBorderBrush"] = new SolidColorBrush(Colors.Coral);
                Arrange(root);
                var themed = TextHudLayer.Capture(panel, null);
                Assert.NotSame(firstTextures, themed.Textures);
                Assert.Equal(firstTextures![0].Id, themed.Textures[0].Id);
                Assert.All(themed.Textures, texture => Assert.Equal(texture.Stride * texture.Height, texture.Pixels.Length));
                panel.Visibility = Visibility.Collapsed;
            }
        }
        finally
        {
            window.Close();
            controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Application.Current.ShutdownMode = shutdown;
        }
    }

    private static void AssertClose(double expected, double actual, string panel, string speed) =>
        Assert.True(Math.Abs(expected - actual) <= 2, $"{panel} speed {speed} ink edge differs: WPF {expected}, native scene {actual}.");
    private static void Arrange(FrameworkElement root)
    {
        root.Measure(new Size(root.Width, root.Height));
        root.Arrange(new Rect(0, 0, root.Width, root.Height));
        root.UpdateLayout();
    }
    private static RenderTargetBitmap Render(FrameworkElement visual)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }
    private static RenderTargetBitmap Render(DirectCompositionDrawCommand[] commands, IReadOnlyList<AnalogHudTexture> textures, Size size)
    {
        var images = textures.ToDictionary(texture => texture.Id, texture => BitmapSource.Create(texture.Width, texture.Height,
            96, 96, PixelFormats.Pbgra32, null, texture.Pixels.ToArray(), texture.Stride));
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            foreach (var command in commands)
            {
                // These fixtures use white speed text. The original static panel texture
                // and per-glyph imagery are composed at the actual native-scene geometry.
                Assert.Equal(1f, command.TintR);
                Assert.Equal(1f, command.TintG);
                Assert.Equal(1f, command.TintB);
                drawing.PushOpacity(command.TintA);
                drawing.PushTransform(new MatrixTransform(command.AxisXX, command.AxisXY, command.AxisYX, command.AxisYY,
                    command.OriginX, command.OriginY));
                drawing.DrawImage(images[command.TextureId], new Rect(0, 0, 1, 1));
                drawing.Pop();
                drawing.Pop();
            }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }
    private static Rect InkBounds(BitmapSource bitmap, Rect region)
    {
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        var left = bitmap.PixelWidth; var top = bitmap.PixelHeight; var right = -1; var bottom = -1;
        for (var y = Math.Max(0, (int)Math.Floor(region.Top)); y < Math.Min(bitmap.PixelHeight, (int)Math.Ceiling(region.Bottom)); y++)
            for (var x = Math.Max(0, (int)Math.Floor(region.Left)); x < Math.Min(bitmap.PixelWidth, (int)Math.Ceiling(region.Right)); x++)
            {
                var offset = y * stride + x * 4;
                if (pixels[offset] < 180 || pixels[offset + 1] < 180 || pixels[offset + 2] < 180) continue;
                left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
            }
        return right < left ? Rect.Empty : new Rect(left, top, right - left + 1, bottom - top + 1);
    }
    private static IEnumerable<DependencyObject> Ancestors(DependencyObject element)
    {
        for (var current = VisualTreeHelper.GetParent(element); current is not null; current = VisualTreeHelper.GetParent(current)) yield return current;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private sealed class NoStartupRegistration : IStartupRegistrationService
    { public void Apply(bool startWithWindows, bool startWithForza) { } }
}
