using System.IO;
using System.Text.Json;
using Wisp.App;
using Wisp.App.Runs;
using Xunit;

namespace Wisp.Core.Tests;

public sealed class RunWorkspaceSettingsTests
{
    [Fact]
    public void CatalogHasElevenDistinctModulesAndEveryPresetUsesKnownModules()
    {
        Assert.Equal(11, RunWorkspaceCatalog.Modules.Count);
        Assert.Equal(11, RunWorkspaceCatalog.Modules.Select(module => module.Id).Distinct().Count());
        Assert.Equal(new[] { RunWorkspacePreset.Overview, RunWorkspacePreset.Engine, RunWorkspacePreset.TiresAndHandling },
            RunWorkspaceCatalog.Presets.Select(preset => preset.Id));
        foreach (var preset in RunWorkspaceCatalog.Presets)
        {
            var panels = RunWorkspaceCatalog.CreatePanels(preset.Id);
            Assert.Equal(11, panels.Count);
            Assert.Contains(panels, panel => panel.IsVisible);
            Assert.All(panels, panel => Assert.NotNull(RunWorkspaceCatalog.Find(panel.Id)));
            Assert.Equal(RunWorkspaceCatalog.PresetModules(preset.Id), panels.Where(panel => panel.IsVisible).Select(panel => panel.Id));
        }
    }

    [Fact]
    public void OverviewIncludesEachFormerDrivingGraphOnceAndKeepsSpecialistPresets()
    {
        string[] formerOverview = ["speed", "inputs", "rpm", "gforce-time"];
        string[] formerAcceleration = ["speed", "inputs", "rpm", "power", "torque", "boost"];
        string[] formerDrifting = ["speed", "inputs", "rpm", "gforce-time", "gforce", "tires"];
        var expected = formerOverview.Concat(formerAcceleration).Concat(formerDrifting).Distinct().ToArray();
        Assert.Equal(expected, RunWorkspaceCatalog.PresetModules(RunWorkspacePreset.Overview));
        Assert.Equal(expected, RunWorkspaceCatalog.PresetModules(RunWorkspacePreset.Acceleration));
        Assert.Equal(expected, RunWorkspaceCatalog.PresetModules(RunWorkspacePreset.Drifting));
        Assert.Equal(new[] { "rpm", "power", "torque", "boost", "power-rpm" }, RunWorkspaceCatalog.PresetModules(RunWorkspacePreset.Engine));
        Assert.Equal(new[] { "tires", "tire-change", "gforce-time", "gforce", "inputs" }, RunWorkspaceCatalog.PresetModules(RunWorkspacePreset.TiresAndHandling));
        Assert.Equal(3, (int)RunWorkspacePreset.Engine);
        Assert.Equal(4, (int)RunWorkspacePreset.TiresAndHandling);
    }

    [Theory]
    [InlineData("\"Overview\"")]
    [InlineData("\"Acceleration\"")]
    [InlineData("\"Drifting\"")]
    [InlineData("1")]
    [InlineData("2")]
    public void ExistingDrivingWorkspacesMigrateWithoutReplacingCustomizedPanels(string storedPreset)
    {
        WithSettings(path =>
        {
            File.WriteAllText(path, """
                {"SettingsRevision":9,"RunPurpose":"Drifting","RunWorkspace":{
                    "Preset":PRESET,"ComparisonMode":"SideBySide","Panels":[
                        {"Id":"gforce","Width":"Full","IsVisible":true},
                        {"Id":"inputs","Width":"Compact","IsVisible":false},
                        {"Id":"speed","Width":"Compact","IsVisible":true}]}}
                """.Replace("PRESET", storedPreset, StringComparison.Ordinal));
            var service = new SettingsService(path);
            var loaded = service.Load();
            var workspace = loaded.RunWorkspace;
            Assert.Equal(RunWorkspacePreset.Overview, workspace.Preset);
            Assert.Equal(RunWorkspaceComparisonMode.SideBySide, workspace.ComparisonMode);
            Assert.Equal(Wisp.Core.Runs.RunPurpose.Drifting, loaded.RunPurpose);
            Assert.Equal(new[] { "gforce", "inputs", "speed" }, workspace.Panels.Take(3).Select(panel => panel.Id));
            Assert.Equal(new[] { "gforce", "speed" }, workspace.Panels.Where(panel => panel.IsVisible).Select(panel => panel.Id));
            Assert.Equal(RunWorkspaceWidth.Full, workspace.Panels[0].Width);
            Assert.Equal(RunWorkspaceWidth.Compact, workspace.Panels[1].Width);
            Assert.Equal(RunWorkspaceWidth.Compact, workspace.Panels[2].Width);
            var snapshot = JsonSerializer.Serialize(workspace);
            Assert.Equal(snapshot, JsonSerializer.Serialize(workspace.Clone()));
            service.Save(loaded);
            Assert.Equal(snapshot, JsonSerializer.Serialize(service.Load().RunWorkspace));
        });
    }

    [Fact]
    public void InvalidAndDuplicatePreferencesCannotCreateExtraOrUnknownModules()
    {
        var preferences = new RunWorkspaceSettings
        {
            Preset = (RunWorkspacePreset)123,
            ComparisonMode = (RunWorkspaceComparisonMode)123,
            Panels = [null!, new() { Id = null! }, new() { Id = "unknown" },
                new() { Id = "boost", Width = (RunWorkspaceWidth)123 },
                new() { Id = "boost", Width = RunWorkspaceWidth.Full },
                new() { Id = "speed", Width = RunWorkspaceWidth.Full, IsVisible = false }]
        };
        preferences.Normalize();
        Assert.Equal(RunWorkspacePreset.Overview, preferences.Preset);
        Assert.Equal(RunWorkspaceComparisonMode.Overlay, preferences.ComparisonMode);
        Assert.Equal(11, preferences.Panels.Count);
        Assert.Equal(11, preferences.Panels.Select(panel => panel.Id).Distinct().Count());
        Assert.Equal("boost", preferences.Panels[0].Id);
        Assert.Equal(RunWorkspaceWidth.Compact, preferences.Panels[0].Width);
        Assert.Equal("boost", Assert.Single(preferences.Panels, panel => panel.IsVisible).Id);
        Assert.Equal(RunWorkspaceWidth.Full, preferences.Panels.Single(panel => panel.Id == "speed").Width);
        var normalized = JsonSerializer.Serialize(preferences);
        preferences.Normalize();
        Assert.Equal(normalized, JsonSerializer.Serialize(preferences));
    }

    [Fact]
    public void EmptyLayoutStaysEmptyWhileMissingLayoutGetsItsSelectedPreset()
    {
        var empty = new RunWorkspaceSettings { Panels = [] };
        empty.Normalize();
        Assert.Equal(11, empty.Panels.Count);
        Assert.DoesNotContain(empty.Panels, panel => panel.IsVisible);
        var missing = new RunWorkspaceSettings { Preset = RunWorkspacePreset.Engine, Panels = null! };
        missing.Normalize();
        Assert.Equal(RunWorkspaceCatalog.PresetModules(RunWorkspacePreset.Engine), missing.Panels.Where(panel => panel.IsVisible).Select(panel => panel.Id));
    }

    [Fact]
    public void CloningDoesNotShareMutablePanelPreferences()
    {
        var original = new RunWorkspaceSettings();
        var clone = original.Clone();
        clone.Panels[0].IsVisible = false;
        clone.Panels[0].Width = RunWorkspaceWidth.Full;
        Assert.True(original.Panels[0].IsVisible);
        Assert.Equal(RunWorkspaceWidth.Wide, original.Panels[0].Width);
        Assert.NotSame(original.Panels, clone.Panels);
    }

    [Fact]
    public void WorkspaceRoundTripPreservesOrderWidthsAndOtherPreferencesWithoutSchemaChange()
    {
        WithSettings(path =>
        {
            var service = new SettingsService(path);
            var settings = new AppSettings
            {
                SettingsRevision = 9,
                ColorTheme = "Plum",
                CpuRenderingEnabled = true,
                RunPurpose = Wisp.Core.Runs.RunPurpose.Drifting,
                RecordingCountdownSeconds = 5,
                RunStatisticsView = RunStatisticsView.Table,
                RunWorkspace = new()
                {
                    Preset = RunWorkspacePreset.Engine,
                    ComparisonMode = RunWorkspaceComparisonMode.SideBySide,
                    Panels = [new() { Id = "torque", Width = RunWorkspaceWidth.Full }, new() { Id = "speed", Width = RunWorkspaceWidth.Compact }]
                }
            };
            service.Save(settings);
            var loaded = service.Load();
            Assert.Equal(9, loaded.SettingsRevision);
            Assert.Equal("Plum", loaded.ColorTheme);
            Assert.True(loaded.CpuRenderingEnabled);
            Assert.Equal(Wisp.Core.Runs.RunPurpose.Drifting, loaded.RunPurpose);
            Assert.Equal(5, loaded.RecordingCountdownSeconds);
            Assert.Equal(RunStatisticsView.Table, loaded.RunStatisticsView);
            Assert.Equal(RunWorkspaceComparisonMode.SideBySide, loaded.RunWorkspace.ComparisonMode);
            Assert.Equal(new[] { "torque", "speed" }, loaded.RunWorkspace.Panels.Where(panel => panel.IsVisible).Select(panel => panel.Id));
            Assert.Equal(RunWorkspaceWidth.Full, loaded.RunWorkspace.Panels[0].Width);
            Assert.Equal(RunWorkspaceWidth.Compact, loaded.RunWorkspace.Panels[1].Width);
            Assert.Equal(11, loaded.RunWorkspace.Panels.Count);
        });
    }

    [Fact]
    public void ExistingSettingsWithoutWorkspaceOrWithNullWorkspaceKeepOtherValues()
    {
        WithSettings(path =>
        {
            var service = new SettingsService(path);
            foreach (var json in new[]
            {
                """{"SettingsRevision":9,"ColorTheme":"Plum","RecordingCountdownSeconds":5}""",
                """{"SettingsRevision":9,"ColorTheme":"Plum","RecordingCountdownSeconds":5,"RunWorkspace":null,"RunStatisticsView":99}"""
            })
            {
                File.WriteAllText(path, json);
                var settings = service.Load();
                Assert.Equal("Plum", settings.ColorTheme);
                Assert.Equal(5, settings.RecordingCountdownSeconds);
                Assert.Equal(RunStatisticsView.Cards, settings.RunStatisticsView);
                Assert.Equal(RunWorkspaceCatalog.PresetModules(RunWorkspacePreset.Overview), settings.RunWorkspace.Panels.Where(panel => panel.IsVisible).Select(panel => panel.Id));
            }
        });
    }

    private static void WithSettings(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "wisp-workspace-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { action(Path.Combine(directory, "settings.json")); }
        finally { Directory.Delete(directory, true); }
    }
}
