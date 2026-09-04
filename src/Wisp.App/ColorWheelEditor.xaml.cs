using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Wisp.App;

public partial class ColorWheelEditor : UserControl
{
    private const int WheelBitmapSize = 192;
    private static readonly ImageSource SharedWheelImage = CreateWheelImage();
    private bool _updating;
    private bool _publishingColor;
    private bool _draggingWheel;
    private double _hue;
    private double _saturation;
    private double _wheelHue;
    private double _wheelSaturation;
    private double _brightness = 1;
    private double _opacity = 1;

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(ColorWheelEditor), new PropertyMetadata("Color"));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(ColorWheelEditor), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SelectedColorProperty = DependencyProperty.Register(
        nameof(SelectedColor), typeof(Color), typeof(ColorWheelEditor),
        new FrameworkPropertyMetadata(Colors.White, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedColorChanged));

    public static readonly DependencyProperty SelectedBrushProperty = DependencyProperty.Register(
        nameof(SelectedBrush), typeof(Brush), typeof(ColorWheelEditor),
        new PropertyMetadata(Brushes.White));

    public static readonly DependencyProperty MinimumOpacityProperty = DependencyProperty.Register(
        nameof(MinimumOpacity), typeof(double), typeof(ColorWheelEditor),
        new PropertyMetadata(0d, OnBoundsChanged, CoerceUnitInterval));

    public static readonly DependencyProperty MaximumBrightnessProperty = DependencyProperty.Register(
        nameof(MaximumBrightness), typeof(double), typeof(ColorWheelEditor),
        new PropertyMetadata(1d, OnBoundsChanged, CoerceBrightness));

    public ColorWheelEditor()
    {
        InitializeComponent();
        WheelBrush.ImageSource = SharedWheelImage;
        Loaded += (_, _) => SyncFromColor(SelectedColor);
    }

    public event RoutedPropertyChangedEventHandler<Color>? SelectedColorChanged;

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public Color SelectedColor
    {
        get => (Color)GetValue(SelectedColorProperty);
        set => SetValue(SelectedColorProperty, value);
    }

    public Brush SelectedBrush
    {
        get => (Brush)GetValue(SelectedBrushProperty);
        private set => SetValue(SelectedBrushProperty, value);
    }

    public double MinimumOpacity
    {
        get => (double)GetValue(MinimumOpacityProperty);
        set => SetValue(MinimumOpacityProperty, value);
    }

    public double MaximumBrightness
    {
        get => (double)GetValue(MaximumBrightnessProperty);
        set => SetValue(MaximumBrightnessProperty, value);
    }

    private static void OnSelectedColorChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var editor = (ColorWheelEditor)sender;
        var previous = (Color)args.OldValue;
        var current = (Color)args.NewValue;
        if (editor._publishingColor)
        {
            editor.UpdateVisuals(current);
        }
        else
        {
            editor.SyncFromColor(current);
        }

        editor.SelectedColorChanged?.Invoke(editor, new RoutedPropertyChangedEventArgs<Color>(previous, current));
    }

    private static void OnBoundsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var editor = (ColorWheelEditor)sender;
        editor.SyncFromColor(editor.SelectedColor);
    }

    private static object CoerceUnitInterval(DependencyObject sender, object value) =>
        double.IsFinite((double)value) ? Math.Clamp((double)value, 0, 1) : 0d;

    private static object CoerceBrightness(DependencyObject sender, object value)
    {
        var brightness = double.IsFinite((double)value) ? (double)value : 1d;
        return Math.Clamp(brightness, 0.02, 1);
    }

    private void SyncFromColor(Color color)
    {
        if (!IsInitialized || _updating)
        {
            return;
        }

        _updating = true;
        try
        {
            var hsv = ColorCustomization.ToHsv(color);
            _hue = hsv.Hue;
            _saturation = hsv.Saturation;
            _wheelHue = hsv.Hue;
            _wheelSaturation = hsv.Saturation;
            _brightness = Math.Clamp(hsv.Value, 0.02, MaximumBrightness);
            _opacity = Math.Clamp(hsv.Opacity, MinimumOpacity, 1);
            SaturationSlider.Value = _saturation;
            BrightnessSlider.Value = _brightness;
            OpacitySlider.Value = _opacity;
            UpdateVisuals(color);
        }
        finally
        {
            _updating = false;
        }
    }

    private void ComponentSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || !IsInitialized)
        {
            return;
        }

        _saturation = SaturationSlider.Value;
        _brightness = BrightnessSlider.Value;
        _opacity = OpacitySlider.Value;
        PublishColor();
    }

    private void WheelSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _draggingWheel = true;
        WheelSurface.CaptureMouse();
        UpdateWheelSelection(e.GetPosition(WheelSurface));
        e.Handled = true;
    }

    private void WheelSurface_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingWheel && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateWheelSelection(e.GetPosition(WheelSurface));
        }
    }

    private void WheelSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_draggingWheel)
        {
            return;
        }

        UpdateWheelSelection(e.GetPosition(WheelSurface));
        _draggingWheel = false;
        WheelSurface.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void UpdateWheelSelection(Point point)
    {
        var radius = WheelSurface.ActualWidth / 2;
        if (radius <= 0)
        {
            return;
        }

        var selection = SelectionFromPoint(point, radius);
        _hue = selection.Hue;
        _saturation = selection.Saturation;
        _wheelHue = selection.Hue;
        _wheelSaturation = selection.Saturation;

        _updating = true;
        try
        {
            SaturationSlider.Value = _saturation;
        }
        finally
        {
            _updating = false;
        }

        PublishColor();
    }

    private void PublishColor()
    {
        var color = ColorCustomization.FromHsv(_hue, _saturation, _brightness, _opacity);
        if (SelectedColor != color)
        {
            _publishingColor = true;
            try
            {
                SelectedColor = color;
            }
            finally
            {
                _publishingColor = false;
            }
        }
        else
        {
            UpdateVisuals(color);
        }
    }

    private void UpdateVisuals(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        SelectedBrush = brush;
        SaturationValue.Text = $"{_saturation:P0}";
        BrightnessValue.Text = $"{_brightness:P0}";
        OpacityValue.Text = $"{_opacity:P0}";
        HexInput.Text = ColorCustomization.ToHex(color);

        var radius = WheelSurface.ActualWidth > 0 ? WheelSurface.ActualWidth / 2 : 76;
        var position = WheelMarkerPosition(
            _wheelHue,
            _wheelSaturation,
            radius,
            WheelMarker.Width,
            WheelMarker.Height);
        Canvas.SetLeft(WheelMarker, position.X);
        Canvas.SetTop(WheelMarker, position.Y);
    }

    internal static (double Hue, double Saturation) SelectionFromPoint(Point point, double radius)
    {
        var deltaX = point.X - radius;
        var deltaY = point.Y - radius;
        var hue = Math.Atan2(deltaY, deltaX) * 180 / Math.PI;
        return (
            hue < 0 ? hue + 360 : hue,
            radius > 0 ? Math.Clamp(Math.Sqrt(deltaX * deltaX + deltaY * deltaY) / radius, 0, 1) : 0);
    }

    internal static Point WheelMarkerPosition(
        double hue,
        double saturation,
        double radius,
        double markerWidth,
        double markerHeight)
    {
        var radians = hue * Math.PI / 180;
        var maximumMarkerRadius = Math.Max(0, radius - Math.Max(markerWidth, markerHeight) / 2 - 1);
        var markerRadius = Math.Min(radius * Math.Clamp(saturation, 0, 1), maximumMarkerRadius);
        return new Point(
            radius + Math.Cos(radians) * markerRadius - markerWidth / 2,
            radius + Math.Sin(radians) * markerRadius - markerHeight / 2);
    }

    private void HexInput_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        ApplyHexInput();

    private void HexInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        ApplyHexInput();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void ApplyHexInput()
    {
        if (!ColorCustomization.TryParse(HexInput.Text, out var color))
        {
            HexInput.Text = ColorCustomization.ToHex(SelectedColor);
            return;
        }

        var hsv = ColorCustomization.ToHsv(color);
        SelectedColor = ColorCustomization.FromHsv(
            hsv.Hue,
            hsv.Saturation,
            Math.Clamp(hsv.Value, 0.02, MaximumBrightness),
            Math.Clamp(hsv.Opacity, MinimumOpacity, 1));
    }

    private static ImageSource CreateWheelImage()
    {
        var pixels = new byte[WheelBitmapSize * WheelBitmapSize * 4];
        var radius = (WheelBitmapSize - 1) / 2d;
        for (var y = 0; y < WheelBitmapSize; y++)
        {
            for (var x = 0; x < WheelBitmapSize; x++)
            {
                var deltaX = x - radius;
                var deltaY = y - radius;
                var saturation = Math.Sqrt(deltaX * deltaX + deltaY * deltaY) / radius;
                if (saturation > 1)
                {
                    continue;
                }

                var hue = Math.Atan2(deltaY, deltaX) * 180 / Math.PI;
                if (hue < 0)
                {
                    hue += 360;
                }

                var color = ColorCustomization.FromHsv(hue, saturation, 1);
                var offset = (y * WheelBitmapSize + x) * 4;
                pixels[offset] = color.B;
                pixels[offset + 1] = color.G;
                pixels[offset + 2] = color.R;
                pixels[offset + 3] = 0xFF;
            }
        }

        var bitmap = BitmapSource.Create(
            WheelBitmapSize,
            WheelBitmapSize,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            WheelBitmapSize * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
