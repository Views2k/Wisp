using System.Windows.Media;
using Wisp.App.NativeRendering;

namespace Wisp.App.Laps;

internal static class LapColors
{
    internal const string Ahead = "#FF7CF2C9", Behind = "#FFFF647C", Track = "#FFE5EAF2",
        Car = "#FF7CF2C9", Background = "#AF080C11";
    internal static Color Resolve(string? value, string fallback) =>
        ColorCustomization.TryParse(value, out var color) ? color : (Color)ColorConverter.ConvertFromString(fallback);
    internal static AnalogHudColor Tint(string? value, string fallback)
    {
        var color = Resolve(value, fallback);
        return new(color.R, color.G, color.B, color.A);
    }
}
