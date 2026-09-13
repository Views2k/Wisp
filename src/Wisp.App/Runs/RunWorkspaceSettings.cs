namespace Wisp.App.Runs;

public enum RunWorkspaceWidth { Compact, Wide, Full }
// Retain the retired preset IDs so existing settings still deserialize correctly.
public enum RunWorkspacePreset { Overview = 0, Acceleration = 1, Drifting = 2, Engine = 3, TiresAndHandling = 4 }
public enum RunWorkspaceComparisonMode { Overlay, SideBySide }
public enum RunStatisticsView { Cards, Table }

public sealed record RunWorkspacePresetOption(RunWorkspacePreset Id, string Title);
public sealed record RunWorkspaceComparisonOption(RunWorkspaceComparisonMode Id, string Title);
public sealed record RunWorkspaceDefinition(string Id, string Title, string Description, RunWorkspaceWidth DefaultWidth);

public static class RunWorkspaceCatalog
{
    public const int MaximumModules = 11;
    public static IReadOnlyList<RunWorkspaceDefinition> Modules { get; } = Array.AsReadOnly<RunWorkspaceDefinition>(
    [
        new("speed", "Speed", "Ground speed and driven-wheel speed over time.", RunWorkspaceWidth.Wide),
        new("inputs", "Driver inputs", "Throttle, braking and steering over time.", RunWorkspaceWidth.Wide),
        new("rpm", "Engine speed", "RPM over time, including shifts and limiter bounces.", RunWorkspaceWidth.Compact),
        new("power", "Engine power", "Recorded engine power over time.", RunWorkspaceWidth.Compact),
        new("torque", "Engine torque", "Recorded engine torque over time.", RunWorkspaceWidth.Compact),
        new("boost", "Boost pressure", "Boost pressure for cars with positive boost recorded.", RunWorkspaceWidth.Compact),
        new("gforce-time", "G-force over time", "Cornering, acceleration and braking load over time.", RunWorkspaceWidth.Wide),
        new("tires", "Tire temperatures", "Front and rear axle average temperatures over time.", RunWorkspaceWidth.Wide),
        new("power-rpm", "Power and torque by RPM", "Recorded engine output at each RPM. Throttle and gear filters apply here.", RunWorkspaceWidth.Full),
        new("gforce", "G-force plot", "Cornering load against acceleration and braking load.", RunWorkspaceWidth.Compact),
        new("tire-change", "Tire temperature change", "Compare axle temperatures at the start and end of the selected period.", RunWorkspaceWidth.Compact)
    ]);
    public static IReadOnlyList<RunWorkspacePresetOption> Presets { get; } = Array.AsReadOnly<RunWorkspacePresetOption>(
    [
        new(RunWorkspacePreset.Overview, "Overview"),
        new(RunWorkspacePreset.Engine, "Engine"),
        new(RunWorkspacePreset.TiresAndHandling, "Tires & handling")
    ]);
    public static IReadOnlyList<RunWorkspaceComparisonOption> ComparisonModes { get; } = Array.AsReadOnly<RunWorkspaceComparisonOption>(
    [new(RunWorkspaceComparisonMode.Overlay, "Overlay"), new(RunWorkspaceComparisonMode.SideBySide, "Side by side")]);

    public static RunWorkspaceDefinition? Find(string? id) => Modules.FirstOrDefault(module => module.Id == id);

    public static string[] PresetModules(RunWorkspacePreset preset) => preset switch
    {
        RunWorkspacePreset.Engine => ["rpm", "power", "torque", "boost", "power-rpm"],
        RunWorkspacePreset.TiresAndHandling => ["tires", "tire-change", "gforce-time", "gforce", "inputs"],
        _ => ["speed", "inputs", "rpm", "gforce-time", "power", "torque", "boost", "gforce", "tires"]
    };

    public static List<RunWorkspacePanelSettings> CreatePanels(RunWorkspacePreset preset)
    {
        var visible = PresetModules(preset);
        return visible.Select(id => Find(id)!).Concat(Modules.Where(module => !visible.Contains(module.Id)))
            .Select(module => new RunWorkspacePanelSettings
            { Id = module.Id, Width = module.DefaultWidth, IsVisible = visible.Contains(module.Id) }).ToList();
    }
}

public sealed class RunWorkspacePanelSettings
{
    public string Id { get; set; } = "";
    public RunWorkspaceWidth Width { get; set; } = RunWorkspaceWidth.Compact;
    public bool IsVisible { get; set; } = true;
    public RunWorkspacePanelSettings Clone() => (RunWorkspacePanelSettings)MemberwiseClone();
}

public sealed class RunWorkspaceSettings
{
    public RunWorkspacePreset Preset { get; set; } = RunWorkspacePreset.Overview;
    public RunWorkspaceComparisonMode ComparisonMode { get; set; }
    public List<RunWorkspacePanelSettings> Panels { get; set; } = RunWorkspaceCatalog.CreatePanels(RunWorkspacePreset.Overview);

    public RunWorkspaceSettings Clone()
    {
        var copy = new RunWorkspaceSettings
        { Preset = Preset, ComparisonMode = ComparisonMode, Panels = Panels?.Where(panel => panel is not null).Select(panel => panel.Clone()).ToList()! };
        copy.Normalize();
        return copy;
    }

    public void Normalize()
    {
        // Only migrate the category. Saved graph visibility, order and widths remain authoritative.
        if (!Enum.IsDefined(Preset) || Preset is RunWorkspacePreset.Acceleration or RunWorkspacePreset.Drifting)
            Preset = RunWorkspacePreset.Overview;
        if (!Enum.IsDefined(ComparisonMode)) ComparisonMode = RunWorkspaceComparisonMode.Overlay;
        if (Panels is null) Panels = RunWorkspaceCatalog.CreatePanels(Preset);
        var clean = new List<RunWorkspacePanelSettings>(RunWorkspaceCatalog.MaximumModules);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var panel in Panels)
        {
            if (panel is null || RunWorkspaceCatalog.Find(panel.Id) is not { } definition || !ids.Add(panel.Id)) continue;
            clean.Add(new RunWorkspacePanelSettings
            {
                Id = definition.Id,
                IsVisible = panel.IsVisible,
                Width = Enum.IsDefined(panel.Width) ? panel.Width : definition.DefaultWidth
            });
            if (clean.Count == RunWorkspaceCatalog.MaximumModules) break;
        }
        // Missing modules remain available without silently adding graphs to an existing layout.
        clean.AddRange(RunWorkspaceCatalog.Modules.Where(module => ids.Add(module.Id)).Select(module =>
            new RunWorkspacePanelSettings { Id = module.Id, Width = module.DefaultWidth, IsVisible = false }));
        Panels = clean;
    }
}
