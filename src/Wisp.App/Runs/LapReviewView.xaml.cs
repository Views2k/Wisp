using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Wisp.App.Runs;

public partial class LapReviewView : UserControl
{
    public LapReviewView()
    {
        InitializeComponent();
        Track.PointChosen += Pick;
        Track3D.PointChosen += Pick;
        Trace.PointChosen += Pick;
        InitializeMapWorkspace();
        InitializeScrubbing();
    }
    private void Pick(int index) { if (DataContext is LapReviewViewModel model) model.Cursor = index; }
    private void ResetMapView_Click(object sender, RoutedEventArgs e) => Track3D.ResetView();
    private void ZoomMapIn_Click(object sender, RoutedEventArgs e) => Track3D.ZoomBy(1.25);
    private void ZoomMapOut_Click(object sender, RoutedEventArgs e) => Track3D.ZoomBy(.8);
    private bool _exporting;

    private async void SaveMapPng_Click(object sender, RoutedEventArgs e)
    {
        if (_exporting || DataContext is not LapReviewViewModel { HasLap: true } model) return;
        if (model.Is3D && !Track3D.IsReady) { model.MapStatus = "Wait for the 3D map to finish preparing, then save the image."; return; }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save the current lap map",
            Filter = "PNG image (*.png)|*.png",
            DefaultExt = ".png",
            FileName = $"wisp-lap-{(model.Is3D ? "3d" : "2d")}-{DateTime.Now:yyyyMMdd-HHmmss}",
            AddExtension = true,
            OverwritePrompt = false
        };
        dialog.FileOk += (_, cancel) =>
        {
            if (Path.GetExtension(dialog.FileName).Equals(".png", StringComparison.OrdinalIgnoreCase) && !File.Exists(dialog.FileName)) return;
            cancel.Cancel = true;
            MessageBox.Show(Window.GetWindow(this), "Choose a new filename ending in .png to keep existing files.", "Choose a PNG filename", MessageBoxButton.OK, MessageBoxImage.Information);
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        _exporting = true;
        SaveMapPng.IsEnabled = false;
        model.MapStatus = "Saving map image…";
        try
        {
            var bitmap = CaptureMapImage();
            await RunImageExporter.WriteAsync(bitmap, dialog.FileName);
            model.MapStatus = "Map image saved.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            model.MapStatus = "The map image could not be saved. Check the destination and try again.";
            DebugLogging.HealthContextRecorder.Current.RecordBreadcrumb(DebugLogging.HealthEventCode.LapReviewFailed, error.HResult);
        }
        finally { _exporting = false; SaveMapPng.ClearValue(IsEnabledProperty); }
    }

    internal BitmapSource CaptureMapImage()
    {
        if (DataContext is not LapReviewViewModel { HasLap: true } model || model.Is3D && !Track3D.IsReady)
            throw new InvalidOperationException("The lap map is not ready.");
        MapExportSurface.UpdateLayout();
        var size = MapExportSurface.RenderSize;
        if (size.Width <= 0 || size.Height <= 0 || !double.IsFinite(size.Width) || !double.IsFinite(size.Height))
            throw new InvalidOperationException("The lap map has no layout.");
        // Bound export memory while keeping small windows readable in shared images.
        var scale = Math.Min(2, Math.Min(3840 / size.Width, 2160 / size.Height));
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        // Render in local coordinates; directly rendering a scrolled child also
        // applies its layout offset and can crop the legend off the saved image.
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(MapExportSurface.Background, null,
                new Rect(0, 0, bitmap.PixelWidth / scale, bitmap.PixelHeight / scale));
            var brush = new VisualBrush(MapExportSurface)
            {
                Stretch = Stretch.Fill,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect((Point)VisualTreeHelper.GetOffset(MapExportSurface), size)
            };
            drawing.DrawRectangle(brush, null, new Rect(size));
        }
        bitmap.Render(visual); bitmap.Freeze();
        return bitmap;
    }
}

public sealed class LapReviewCompactViewportConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double height && double.IsFinite(height) && height > 0 && height < 420;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class LapReviewMapHeightConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length > 1 && values[1] is double requested && double.IsFinite(requested))
            return Math.Clamp(requested, 180, 1400);
        var overhead = 235d;
        if (values.Length >= 6 && values[2] is double toolbar && values[3] is double scrub &&
            values[4] is double readout && values[5] is bool compact)
            overhead = 110 + toolbar + scrub + (compact ? 0 : readout + 16);
        return values.Length > 0 && values[0] is double height && double.IsFinite(height) && height > 0
            ? Math.Clamp(height - overhead, 70, 640) : 480d;
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        targetTypes.Select(_ => Binding.DoNothing).ToArray();
}
