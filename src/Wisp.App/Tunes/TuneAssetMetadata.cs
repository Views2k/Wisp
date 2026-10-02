using System.Collections.Immutable;
using Wisp.Core.Tunes;

namespace Wisp.App.Tunes;

internal sealed record TuneAssetRow(TunePartId Kind, int Parent, int Id, int? Level, int? ChildParent);
internal sealed record TuneCarNameKey(int CarOrdinal, int Year, ulong ModelToken, ulong MakeToken);

internal sealed class TuneAssetMetadata(IEnumerable<TuneAssetRow> rows, IEnumerable<TuneCarNameKey>? cars = null)
{
    private readonly ImmutableDictionary<(TunePartId Kind, int Parent, int Id), TuneAssetRow> _rows =
        rows.ToImmutableDictionary(row => (row.Kind, row.Parent, row.Id));
    private readonly ImmutableDictionary<int, TuneCarNameKey> _cars =
        (cars ?? []).ToImmutableDictionary(car => car.CarOrdinal);

    internal TuneCarNameKey? CarNameKey(int ordinal) => _cars.GetValueOrDefault(ordinal);

    internal ImmutableArray<TunePart> Resolve(int carOrdinal, ImmutableArray<TunePart> parts)
    {
        var byKind = parts.ToDictionary(part => part.Kind);
        var result = ImmutableArray.CreateBuilder<TunePart>(parts.Length);
        foreach (var part in parts)
        {
            if (part.InstalledId < 0)
            {
                result.Add(part with { Level = -1 });
                continue;
            }
            int? parent = carOrdinal;
            if (part.Kind is TunePartId.Transmission or TunePartId.Differential or TunePartId.FrontAero)
            {
                var parentKind = part.Kind == TunePartId.FrontAero ? TunePartId.CarBody : TunePartId.Drivetrain;
                parent = byKind.TryGetValue(parentKind, out var installedParent) &&
                    _rows.TryGetValue((parentKind, carOrdinal, installedParent.InstalledId), out var parentRow)
                    ? parentRow.ChildParent : null;
            }
            var level = parent.HasValue && _rows.TryGetValue((part.Kind, parent.Value, part.InstalledId), out var row)
                && row.Level is >= 0 and <= 100 ? row.Level : null;
            result.Add(part with { Level = level });
        }
        return result.MoveToImmutable();
    }

    internal static readonly (TunePartId Kind, string Table, string Parent, string? ChildParent)[] Tables =
    [
        (TunePartId.Engine, "List_UpgradeEngine", "Ordinal", null),
        (TunePartId.Drivetrain, "List_UpgradeDrivetrain", "Ordinal", "DrivetrainId"),
        (TunePartId.CarBody, "List_UpgradeCarBody", "Ordinal", "CarBodyId"),
        (TunePartId.Motor, "List_UpgradeMotor", "Ordinal", null),
        (TunePartId.Brakes, "List_UpgradeBrakes", "Ordinal", null),
        (TunePartId.SpringDamper, "List_UpgradeSpringDamper", "Ordinal", null),
        (TunePartId.FrontAntiroll, "List_UpgradeAntiSwayFront", "Ordinal", null),
        (TunePartId.RearAntiroll, "List_UpgradeAntiSwayRear", "Ordinal", null),
        (TunePartId.RearAero, "List_UpgradeRearWing", "Ordinal", null),
        (TunePartId.Transmission, "List_UpgradeDrivetrainTransmission", "DrivetrainId", null),
        (TunePartId.Differential, "List_UpgradeDrivetrainDifferential", "DrivetrainId", null),
        (TunePartId.FrontAero, "List_UpgradeCarBodyFrontBumper", "CarBodyId", null)
    ];
}
