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

    public static double AnalogMaximum(BoostPressureUnit unit) =>
        unit == BoostPressureUnit.Bar ? 5 : 70;

    public static double AnalogMinimum(BoostPressureUnit unit, bool showVacuum) =>
        showVacuum ? (unit == BoostPressureUnit.Bar ? -1 : -20) : 0;

    public static double GaugeFraction(double pressurePsi, BoostPressureUnit unit, bool showVacuum)
    {
        var minimum = AnalogMinimum(unit, showVacuum);
        return Math.Clamp((FromPsi(pressurePsi, unit) - minimum) / (AnalogMaximum(unit) - minimum), 0, 1);
    }

    public static double AnalogMaximumPsi(BoostPressureUnit unit) =>
        unit == BoostPressureUnit.Bar ? AnalogMaximum(unit) * PsiPerBar : AnalogMaximum(unit);
}
