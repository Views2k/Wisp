using System.Globalization;

namespace Wisp.App;

public static class BoostPressureUnits
{
    public const double PsiPerBar = 14.503773773;

    public static double FromPsi(double pressurePsi, BoostPressureUnit unit) =>
        unit == BoostPressureUnit.Bar ? pressurePsi / PsiPerBar : pressurePsi;

    public static string Symbol(BoostPressureUnit unit) =>
        unit == BoostPressureUnit.Bar ? "BAR" : "PSI";

    public static string FormatValue(double pressurePsi, BoostPressureUnit unit) =>
        FromPsi(pressurePsi, unit).ToString(
            unit == BoostPressureUnit.Bar ? "0.0" : "0",
            CultureInfo.InvariantCulture);

    public static double AnalogMaximum(BoostPressureUnit unit) =>
        unit == BoostPressureUnit.Bar ? 5 : 70;

    public static double AnalogMaximumPsi(BoostPressureUnit unit) =>
        unit == BoostPressureUnit.Bar ? AnalogMaximum(unit) * PsiPerBar : AnalogMaximum(unit);
}
