using System.Globalization;

namespace Wisp.App;

public static class BoostPressureUnits
{
    public const double PsiPerBar = 14.503773773;

    public static double FromPsi(double pressurePsi, BoostPressureUnit unit) =>
        unit == BoostPressureUnit.Bar ? pressurePsi / PsiPerBar : pressurePsi;

    public static string Symbol(BoostPressureUnit unit) =>
        unit == BoostPressureUnit.Bar ? "BAR" : "PSI";

    public static string FormatValue(double pressurePsi, BoostPressureUnit unit, bool padPsi = false)
    {
        var pressure = FromPsi(pressurePsi, unit);
        var value = pressure.ToString(
            unit == BoostPressureUnit.Bar ? "0.0" : padPsi && pressure >= 0 ? "00" : "0",
            CultureInfo.InvariantCulture);
        return value is "-0" or "-0.0"
            ? unit == BoostPressureUnit.Bar ? "0.0" : padPsi ? "00" : "0"
            : value;
    }

    public static double NormalizeMaximumPsi(double value) =>
        double.IsFinite(value) ? Math.Clamp(Math.Round(value / 10) * 10, 20, 200) : 70;

    public static double AnalogMaximum(BoostPressureUnit unit, double maximumPsi = 70) =>
        unit == BoostPressureUnit.Bar ? Math.Ceiling(NormalizeMaximumPsi(maximumPsi) / PsiPerBar) : NormalizeMaximumPsi(maximumPsi);

    public static IReadOnlyList<double> MajorTicks(BoostPressureUnit unit, bool showVacuum, double maximumPsi = 70)
    {
        var maximum = AnalogMaximum(unit, maximumPsi);
        var minimum = AnalogMinimum(unit, showVacuum);
        var interval = unit == BoostPressureUnit.Bar ? maximum <= 8 ? 1 : 2
            : maximum <= 80 ? 10 : maximum <= 160 ? 20 : 40;
        var ticks = new List<double>();
        if (minimum < 0) ticks.Add(minimum);
        for (var value = 0d; value < maximum; value += interval) ticks.Add(value);
        ticks.Add(maximum);
        return ticks;
    }

    public static double AnalogMinimum(BoostPressureUnit unit, bool showVacuum) =>
        showVacuum ? (unit == BoostPressureUnit.Bar ? -1 : -20) : 0;

    public static double GaugeFraction(double pressurePsi, BoostPressureUnit unit, bool showVacuum, double maximumPsi = 70)
    {
        var minimum = AnalogMinimum(unit, showVacuum);
        return Math.Clamp((FromPsi(pressurePsi, unit) - minimum) / (AnalogMaximum(unit, maximumPsi) - minimum), 0, 1);
    }

    public static double AnalogMaximumPsi(BoostPressureUnit unit, double maximumPsi = 70) =>
        unit == BoostPressureUnit.Bar ? AnalogMaximum(unit, maximumPsi) * PsiPerBar : AnalogMaximum(unit, maximumPsi);
}
