using System.Collections.Immutable;
using System.Text.Json;
using Wisp.App.DebugLogging;

namespace Wisp.App.Supplementary;

internal sealed record SupplementaryDiagnosticEnvironment(DateTimeOffset At, SupplementaryEnvironment Environment);
internal sealed record SupplementaryDiagnosticSettings(int UdpPort, double BoostMaximumPsi, int BoostPressureUnit,
    int SpeedSource, int WheelAggregation, bool OverlayLocked, bool GForceAttached, bool BoostAttached,
    bool TireTemperatureAttached, bool PowerAttached, bool TorqueAttached, bool ShowBoostVacuum,
    double BoostScale, double TireTemperatureScale, double PowerScale, double TorqueScale,
    double PowerMaximum, double TorqueMaximumNm, double PowerTorqueSmoothingMs,
    bool DriftEnabled, double DriftScale, double LapMapScale, double LapDeltaScale,
    bool AutomaticUpdates, bool LegacyInterface)
{
    internal static SupplementaryDiagnosticSettings Capture(AppSettings s) => new(s.UdpPort,
        s.BoostMaximumPsi, (int)s.BoostPressureUnit, (int)s.SpeedSource, (int)s.AggregationMode,
        s.OverlayLocked, s.GForceAttached, s.BoostGaugeAttached, s.TireTemperatureGaugeAttached,
        s.PowerGaugeAttached, s.TorqueGaugeAttached, s.ShowBoostVacuum, s.BoostGaugeScale,
        s.TireTemperatureGaugeScale, s.PowerGaugeScale, s.TorqueGaugeScale, s.PowerGaugeMaximum,
        s.TorqueGaugeMaximumNm, s.PowerTorqueSmoothingMilliseconds, s.DriftGaugeEnabled,
        s.DriftGaugeScale, s.LapMapScale, s.LapDeltaScale, s.AutomaticApplicationUpdateChecks, s.UseLegacyInterface);
    internal bool IsValid => UdpPort is > 0 and <= 65535 && BoostMaximumPsi is >= 20 and <= 200 &&
        BoostPressureUnit is 0 or 1 && SpeedSource is >= 0 and <= 10 && WheelAggregation is >= 0 and <= 10 &&
        new[] { BoostScale, TireTemperatureScale, PowerScale, TorqueScale, PowerMaximum, TorqueMaximumNm,
            PowerTorqueSmoothingMs, DriftScale, LapMapScale, LapDeltaScale }.All(n => double.IsFinite(n) && n is >= 0 and <= 1_000_000);
}
internal sealed record SupplementaryDiagnosticConfiguration(DateTimeOffset At, SupplementaryDiagnosticSettings Settings);

internal sealed record SupplementaryDiagnosticDetail(ImmutableArray<HealthContextSample> Health,
    ImmutableArray<HealthBreadcrumb> Breadcrumbs, ImmutableArray<ComponentDiagnosticReport> Components,
    ImmutableArray<SupplementaryDiagnosticEnvironment> Environments,
    ImmutableArray<SupplementaryDiagnosticConfiguration> Configurations, int Omitted, long DroppedBreadcrumbs)
{
    internal const int MaximumBytes = 1100 * 1024;
    internal bool IsValid(DateTimeOffset from, DateTimeOffset to)
    {
        bool Within(DateTimeOffset at) => at.Offset == TimeSpan.Zero && at >= from && at <= to;
        return Omitted >= 0 && DroppedBreadcrumbs >= 0 &&
        !Health.IsDefault && Health.Length <= 601 && !Breadcrumbs.IsDefault && Breadcrumbs.Length <= 512 &&
        !Components.IsDefault && Components.Length <= 8 && !Environments.IsDefault && Environments.Length <= 32 &&
        !Configurations.IsDefault && Configurations.Length <= 64 &&
        Health.All(s => Within(s.TimestampUtc) && HealthContextRecorder.IsValidSnapshot(new(to, [s], []))) &&
        Breadcrumbs.All(b => Within(b.TimestampUtc) && HealthContextRecorder.IsValidSnapshot(new(to, [], [b]))) &&
        Components.All(c => Within(c.TimeUtc) && Enum.IsDefined(c.Component) && c.Details.Length is > 0 and <= 16384) &&
        Environments.All(e => Within(e.At) && e.Environment.IsValid(e.At)) &&
        Configurations.All(c => Within(c.At) && c.Settings.IsValid) &&
        JsonSerializer.SerializeToUtf8Bytes(this, SupplementarySchema.Json).Length <= MaximumBytes;
    }
}

// Copies the already collected two-second context on the background reporting cycle.
// It never reads game memory, enumerates hardware or enables verbose logging.
internal sealed class SupplementaryDiagnosticDetailBuffer
{
    private readonly object _gate = new();
    private readonly SortedDictionary<DateTimeOffset, HealthContextSample> _health = new();
    private readonly HashSet<HealthBreadcrumb> _breadcrumbs = [];
    private readonly List<ComponentDiagnosticReport> _components = [];
    private readonly List<SupplementaryDiagnosticEnvironment> _environments = [];
    private readonly List<SupplementaryDiagnosticConfiguration> _configurations = [];
    private readonly SortedDictionary<long, int> _omissions = new();
    private long _droppedBreadcrumbs;

    internal void Clear()
    {
        lock (_gate)
        {
            _health.Clear(); _breadcrumbs.Clear(); _components.Clear(); _environments.Clear();
            _configurations.Clear(); _omissions.Clear(); _droppedBreadcrumbs = 0;
        }
    }

    internal void Observe(DateTimeOffset now, HealthContextSnapshot context, ComponentDiagnosticReport[] components,
        SupplementaryEnvironment? environment = null, SupplementaryDiagnosticSettings? settings = null)
    {
        var from = now - TimeSpan.FromMinutes(20);
        lock (_gate)
        {
            Trim(from);
            foreach (var s in context.Samples)
                if (s.TimestampUtc >= from && s.TimestampUtc <= now) _health[s.TimestampUtc] = s;
            foreach (var b in context.Breadcrumbs)
                if (b.TimestampUtc >= from && b.TimestampUtc <= now) _breadcrumbs.Add(b);
            foreach (var c in components)
                if (c.TimeUtc >= from && c.TimeUtc <= now && !_components.Contains(c)) _components.Add(c);
            _droppedBreadcrumbs = Math.Max(_droppedBreadcrumbs, context.DroppedBreadcrumbs);
            if (environment is not null && (_environments.Count == 0 || _environments[^1].Environment != environment))
                _environments.Add(new(now, environment));
            if (settings is not null && (_configurations.Count == 0 || _configurations[^1].Settings != settings || now - _configurations[^1].At >= TimeSpan.FromMinutes(1)))
                _configurations.Add(new(now, settings));
            while (_health.Count > 601) { _health.Remove(_health.First().Key); Omit(now); }
            while (_breadcrumbs.Count > 512) { _breadcrumbs.Remove(_breadcrumbs.MinBy(b => b.TimestampUtc)!); Omit(now); }
            Limit(_components, 8, now); Limit(_environments, 32, now); Limit(_configurations, 64, now);
        }
    }

    internal SupplementaryDiagnosticDetail Snapshot(DateTimeOffset from, DateTimeOffset to)
    {
        SupplementaryDiagnosticDetail detail;
        lock (_gate)
        {
            Trim(from);
            detail = new(_health.Values.Where(s => s.TimestampUtc <= to).ToImmutableArray(),
                _breadcrumbs.Where(b => b.TimestampUtc <= to).OrderBy(b => b.TimestampUtc).ToImmutableArray(),
                _components.Where(c => c.TimeUtc <= to).ToImmutableArray(),
                _environments.Where(e => e.At <= to).ToImmutableArray(),
                _configurations.Where(c => c.At <= to).ToImmutableArray(), (int)Math.Min(int.MaxValue, _omissions.Values.Sum(n => (long)n)), _droppedBreadcrumbs);
        }
        // Attachment work runs only after consent, outside the collection lock.
        while (JsonSerializer.SerializeToUtf8Bytes(detail, SupplementarySchema.Json).Length > SupplementaryDiagnosticDetail.MaximumBytes)
        {
            if (detail.Health.Length > 0) detail = detail with { Health = detail.Health.RemoveRange(0, Math.Min(30, detail.Health.Length)), Omitted = detail.Omitted + Math.Min(30, detail.Health.Length) };
            else return detail with { Components = [], Environments = [], Configurations = [], Omitted = detail.Omitted + detail.Components.Length + detail.Environments.Length + detail.Configurations.Length };
        }
        return detail;
    }
    private void Omit(DateTimeOffset now)
    {
        var second = now.UtcTicks / TimeSpan.TicksPerSecond;
        _omissions.TryGetValue(second, out var count);
        _omissions[second] = (int)Math.Min(int.MaxValue, (long)count + 1);
    }
    private void Limit<T>(List<T> items, int max, DateTimeOffset now)
    { while (items.Count > max) { items.RemoveAt(0); Omit(now); } }
    private void Trim(DateTimeOffset from)
    {
        while (_health.Count > 0 && _health.First().Key < from) _health.Remove(_health.First().Key);
        _breadcrumbs.RemoveWhere(b => b.TimestampUtc < from);
        _components.RemoveAll(c => c.TimeUtc < from); _environments.RemoveAll(e => e.At < from);
        _configurations.RemoveAll(c => c.At < from);
        while (_omissions.Count > 0 && _omissions.First().Key < from.UtcTicks / TimeSpan.TicksPerSecond) _omissions.Remove(_omissions.First().Key);
    }
}
