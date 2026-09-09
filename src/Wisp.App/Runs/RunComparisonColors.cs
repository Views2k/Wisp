using System.Windows.Media;

namespace Wisp.App.Runs;

public static class RunComparisonColors
{
    // Stable data colors keep the runs distinct even with a custom app accent.
    public static Brush RunA { get; } = Frozen(0x63, 0xD8, 0xD4);
    public static Brush RunB { get; } = Frozen(0xFF, 0xB8, 0x6B);
    private static readonly Brush[] Baseline = [RunA, Frozen(0x91, 0xC5, 0xFF), Frozen(0xC5, 0xB3, 0xFF)];
    private static readonly Brush[] Comparison = [RunB, Frozen(0xF6, 0xD8, 0x86), Frozen(0xFF, 0x9D, 0xBC)];

    internal static Brush Series(bool comparison, int channel) =>
        (comparison ? Comparison : Baseline)[channel is >= 0 and < 3 ? channel : 0];

    private static Brush Frozen(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
