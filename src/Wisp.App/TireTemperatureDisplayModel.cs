using Wisp.Core;

namespace Wisp.App;

public readonly record struct TireTemperatureDisplay(
    bool IsAvailable,
    double FrontFahrenheit,
    double RearFahrenheit,
    double FrontFraction,
    double RearFraction)
{
    public const double MinimumFahrenheit = 50;
    public const double MaximumFahrenheit = 350;

    public static TireTemperatureDisplay Unavailable => new(false, 0, 0, 0, 0);

    public double Front(TireTemperatureUnit unit) => ConvertForReadout(FrontFahrenheit, unit);
    public double Rear(TireTemperatureUnit unit) => ConvertForReadout(RearFahrenheit, unit);

    public static double ConvertForReadout(double fahrenheit, TireTemperatureUnit unit) =>
        Convert(Math.Min(fahrenheit, MaximumFahrenheit), unit);

    public static double Convert(double fahrenheit, TireTemperatureUnit unit) =>
        unit == TireTemperatureUnit.Celsius
            ? (fahrenheit - 32) * 5 / 9
            : fahrenheit;

    public static string UnitSymbol(TireTemperatureUnit unit) =>
        unit == TireTemperatureUnit.Celsius ? "°C" : "°F";
}

public sealed class TireTemperatureDisplayModel
{
    private const double SaturationReleaseFahrenheit = 345;
    private int _carOrdinal;
    private bool _frontSaturated;
    private bool _rearSaturated;

    public TireTemperatureDisplay Calculate(int carOrdinal, WheelValues temperaturesFahrenheit)
    {
        if (carOrdinal <= 0 || !temperaturesFahrenheit.AreFinite() ||
            temperaturesFahrenheit.MaximumAbsolute() < 0.001f)
        {
            if (carOrdinal <= 0)
            {
                ResetSaturation();
            }
            return TireTemperatureDisplay.Unavailable;
        }

        if (_carOrdinal != carOrdinal)
        {
            ResetSaturation();
            _carOrdinal = carOrdinal;
        }

        var front = ((double)temperaturesFahrenheit.FrontLeft + temperaturesFahrenheit.FrontRight) / 2;
        var rear = ((double)temperaturesFahrenheit.RearLeft + temperaturesFahrenheit.RearRight) / 2;
        return new TireTemperatureDisplay(
            true,
            front,
            rear,
            StableFraction(front, ref _frontSaturated),
            StableFraction(rear, ref _rearSaturated));
    }

    private static double Fraction(double fahrenheit) => Math.Clamp(
        (fahrenheit - TireTemperatureDisplay.MinimumFahrenheit) /
        (TireTemperatureDisplay.MaximumFahrenheit - TireTemperatureDisplay.MinimumFahrenheit),
        0,
        1);

    private static double StableFraction(double fahrenheit, ref bool saturated)
    {
        if (fahrenheit >= TireTemperatureDisplay.MaximumFahrenheit)
        {
            saturated = true;
        }
        else if (fahrenheit <= SaturationReleaseFahrenheit)
        {
            saturated = false;
        }

        return saturated ? 1 : Fraction(fahrenheit);
    }

    private void ResetSaturation()
    {
        _carOrdinal = 0;
        _frontSaturated = false;
        _rearSaturated = false;
    }

}
