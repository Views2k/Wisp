using System.Text.RegularExpressions;
using Xunit;

namespace Wisp.App.Tests;

public sealed class UiReviewWizardContractTests
{
    [Fact]
    public void WizardAddsItsOwnBoundedMatrixWithoutChangingTheMainMatrix()
    {
        var program = ToolSource("Program.cs");

        Assert.Matches(@"\[\(""baseline"", 980, 750\), \(""compact"", 720, 440\), \(""wide"", 1280, 900\), \(""fullscreen"", 2560, 1440\)\]", program);
        Assert.Matches(@"\[\(""baseline"", 800, 730\), \(""compact"", 540, 440\), \(""wide"", 840, 760\), \(""launch"", 900, 780\)\]", program);
        Assert.Contains("""["welcome", "connection", "display", "appearance"]""", program, StringComparison.Ordinal);
        Assert.Contains("""wizard && !values.ContainsKey("--dpi") ? [96, 144]""", program, StringComparison.Ordinal);
        Assert.Contains("""fixtures = [Fixture.All[0]];""", program, StringComparison.Ordinal);
    }

    [Fact]
    public void WizardDoesNotStartRuntimeListenersOrInjectTelemetryOrCompletionEvidence()
    {
        var program = ToolSource("Program.cs");
        var start = program.IndexOf("private static void CaptureWizard(", StringComparison.Ordinal);
        var end = program.IndexOf("private static void PresentSurface(", start, StringComparison.Ordinal);
        var wizard = program[start..end];

        Assert.DoesNotContain("fixture.Apply(", wizard, StringComparison.Ordinal);
        Assert.DoesNotContain(".StartAsync(", wizard, StringComparison.Ordinal);
        Assert.DoesNotContain(".RestartListenerAsync(", wizard, StringComparison.Ordinal);
        Assert.DoesNotContain(".RunAsync(", wizard, StringComparison.Ordinal);
        Assert.DoesNotContain(".CompleteSetup(", wizard, StringComparison.Ordinal);
        Assert.DoesNotContain(".Show(", wizard, StringComparison.Ordinal);
        Assert.DoesNotContain(".ShowDialog(", wizard, StringComparison.Ordinal);
        Assert.Contains("DetachSurface(window, window.DataContext)", wizard, StringComparison.Ordinal);
        Assert.Contains("new SettingsService(settingsPath)", wizard, StringComparison.Ordinal);
        Assert.Contains("controller.SetupTelemetry.SuccessfulEvidence is not null", wizard, StringComparison.Ordinal);
        Assert.Contains("controller.Settings.RequiresSetup", wizard, StringComparison.Ordinal);
    }

    [Fact]
    public void StepSelectionUsesOnlyExistingPrivateDisplayStateInsideTheTool()
    {
        var program = ToolSource("Program.cs");
        var start = program.IndexOf("private static void SelectWizardStep(", StringComparison.Ordinal);
        var end = program.IndexOf("private static void PresentSurface(", start, StringComparison.Ordinal);
        var selection = program[start..end];

        Assert.Contains("""GetField("_step", flags)""", selection, StringComparison.Ordinal);
        Assert.Contains("""GetMethod("UpdateStep", flags""", selection, StringComparison.Ordinal);
        Assert.Contains("stepField.SetValue(window, step)", selection, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(selection, @"\.SetValue\(").Cast<Match>());
        Assert.DoesNotContain("SetupCompletion =", selection, StringComparison.Ordinal);
        Assert.DoesNotContain("IsChecked =", selection, StringComparison.Ordinal);
    }

    [Fact]
    public void OffscreenWizardRequiresNoSourceHandleOrPresentationSourceAndNeverOverwritesCaptures()
    {
        var program = ToolSource("Program.cs");
        var start = program.IndexOf("private static void CaptureWizard(", StringComparison.Ordinal);
        var end = program.IndexOf("private static void SelectWizardStep(", start, StringComparison.Ordinal);
        var capture = program[start..end];

        Assert.Contains("new WindowInteropHelper(window).Handle != IntPtr.Zero", capture, StringComparison.Ordinal);
        Assert.Contains("PresentationSource.FromVisual(surface) is not null", capture, StringComparison.Ordinal);
        Assert.Contains("SetOffscreenDpi(surface, dpi)", capture, StringComparison.Ordinal);
        Assert.Contains("FileMode.CreateNew", capture, StringComparison.Ordinal);
        Assert.Contains("Directory.Delete(stateDirectory, recursive: false)", capture, StringComparison.Ordinal);
        Assert.Contains("controller?.DisposeAsync()", capture, StringComparison.Ordinal);
    }

    [Fact]
    public void WizardReportsActualForegroundEstimatedContrastAndRequiredFooterBounds()
    {
        var wizard = ToolSource("WizardReview.cs");
        var diagnostics = ToolSource("ReviewDiagnostics.cs");

        Assert.Contains("text.Foreground is SolidColorBrush", wizard, StringComparison.Ordinal);
        Assert.Contains("BlackForegroundCount", wizard, StringComparison.Ordinal);
        Assert.Contains("LowContrastCount", wizard, StringComparison.Ordinal);
        Assert.Contains("UnexpectedClippingCount", wizard, StringComparison.Ordinal);
        Assert.Contains("""new[] { "BackButton", "NextButton" }""", wizard, StringComparison.Ordinal);
        Assert.Contains("control.ClippedByAncestor", wizard, StringComparison.Ordinal);
        Assert.Contains("not ambient/image pixels", wizard, StringComparison.Ordinal);
        Assert.Contains("PreviewStateReport? previewState = null", diagnostics, StringComparison.Ordinal);
        Assert.Contains("else if (wizard is null)", diagnostics, StringComparison.Ordinal);
        Assert.Contains("WizardReview.Inspect(surface, elements, wizard)", diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public void WizardPresentationRemainsASeparateInputBlockedBoundedHost()
    {
        var program = ToolSource("Program.cs");
        var start = program.IndexOf("private static void PresentSurface(", StringComparison.Ordinal);
        var end = program.IndexOf("private static FrameworkElement DetachSurface(", start, StringComparison.Ordinal);
        var presentation = program[start..end];

        Assert.Contains("var host = new Window", presentation, StringComparison.Ordinal);
        Assert.Contains("surface.IsHitTestVisible = false", presentation, StringComparison.Ordinal);
        Assert.Contains("KeyboardNavigationMode.None", presentation, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(presentation.AutoCloseSeconds)", presentation, StringComparison.Ordinal);
        Assert.Contains("new WindowInteropHelper(sourceWindow).Handle != IntPtr.Zero", presentation, StringComparison.Ordinal);
        Assert.DoesNotContain("sourceWindow.Show", presentation, StringComparison.Ordinal);
        Assert.DoesNotContain("sourceWindow.Activate", presentation, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryOffscreenPassRefreshesDpiBeforeMeasuring()
    {
        var program = ToolSource("Program.cs");
        var mainStart = program.IndexOf("private static void CaptureFixture(", StringComparison.Ordinal);
        var wizardStart = program.IndexOf("private static void CaptureWizard(", StringComparison.Ordinal);
        var wizardEnd = program.IndexOf("private static void SelectWizardStep(", StringComparison.Ordinal);

        Assert.All(new[] { program[mainStart..wizardStart], program[wizardStart..wizardEnd] }, capture =>
        {
            Assert.Matches(@"for \(var pass = 0; pass < 2; pass\+\+\)\s*\{\s*SetOffscreenDpi\(surface, dpi\);\s*surface.Measure\(size\);", capture);
            Assert.DoesNotContain("VisualTreeHelper.SetRootDpi", capture, StringComparison.Ordinal);
        });
        Assert.Single(Regex.Matches(program, @"VisualTreeHelper\.SetRootDpi\(").Cast<Match>());
    }

    [Fact]
    public void DpiRefreshInvalidatesBoundedDescendantsIncludingCollapsedSteps()
    {
        var program = ToolSource("Program.cs");
        var start = program.IndexOf("private static void SetOffscreenDpi(", StringComparison.Ordinal);
        var end = program.IndexOf("private static FrameworkElement DetachSurface(", start, StringComparison.Ordinal);
        var refresh = program[start..end];

        Assert.Contains("VisualTreeHelper.SetRootDpi(surface, new DpiScale(dpi / 96d, dpi / 96d))", refresh, StringComparison.Ordinal);
        Assert.Contains("const int maximumVisualNodes = 4096", refresh, StringComparison.Ordinal);
        Assert.Contains("pending.Enqueue(surface)", refresh, StringComparison.Ordinal);
        Assert.Contains("visited + pending.Count >= maximumVisualNodes", refresh, StringComparison.Ordinal);
        Assert.Contains("pending.Enqueue(VisualTreeHelper.GetChild(current, index))", refresh, StringComparison.Ordinal);
        Assert.Contains("current is UIElement element", refresh, StringComparison.Ordinal);
        Assert.Matches(@"for \(var index = elements.Count - 1; index >= 0; index--\)\s*\{\s*elements\[index\].InvalidateMeasure\(\);", refresh);
        Assert.DoesNotContain("IsVisible", refresh, StringComparison.Ordinal);
        Assert.DoesNotContain("VisibleWithin", refresh, StringComparison.Ordinal);
        Assert.DoesNotContain(".FontSize =", refresh, StringComparison.Ordinal);
        Assert.DoesNotContain(".Padding =", refresh, StringComparison.Ordinal);
        Assert.DoesNotContain(".Text =", refresh, StringComparison.Ordinal);
    }

    [Fact]
    public void DpiRefreshDoesNotWeakenExistingOverflowChecks()
    {
        var diagnostics = ToolSource("ReviewDiagnostics.cs");

        Assert.Contains("formatted.MaxTextWidth = width;", diagnostics, StringComparison.Ordinal);
        Assert.Contains("VisualTreeHelper.GetDpi(text).PixelsPerDip", diagnostics, StringComparison.Ordinal);
        Assert.Contains("formatted.Width <= width + 1.5 && formatted.Height <= height + 1.5", diagnostics, StringComparison.Ordinal);
    }

    private static string ToolSource(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wisp.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName, "tools", "Wisp.UiReview", fileName));
    }
}
