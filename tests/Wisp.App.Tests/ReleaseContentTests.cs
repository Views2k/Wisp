using System.Security.Cryptography;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ReleaseContentTests
{
    [Fact]
    public void NativeAssetManifestCoversEveryPackagedPngWithItsExactHash()
    {
        var appDirectory = Path.Combine(RepositoryRoot(), "src", "Wisp.App");
        var nativeDirectory = Path.Combine(appDirectory, "Assets", "Native");
        var manifestPath = Path.Combine(nativeDirectory, "ASSET-MANIFEST.csv");
        var rows = File.ReadLines(manifestPath).Skip(1).ToArray();
        var assets = Directory.EnumerateFiles(nativeDirectory, "*.png", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(nativeDirectory, path).Replace('\\', '/'),
                StringComparer.OrdinalIgnoreCase);

        Assert.Equal(assets.Count, rows.Length);
        Assert.Equal(240, assets.Count);
        var recorded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var fields = row.Trim('"').Split("\",\"");
            Assert.True(fields.Length >= 8, $"Malformed Native asset manifest row: {row}");
            Assert.True(recorded.Add(fields[0]), $"Duplicate Native asset manifest entry: {fields[0]}");
            Assert.True(assets.TryGetValue(fields[0], out var assetPath), $"Missing Native asset: {fields[0]}");
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assetPath!))).ToLowerInvariant();
            Assert.Equal(fields[5], hash);
        }
    }

    [Fact]
    public void ReleasePackagesNativeNoticeAndHasNoExternalDecryptDependency()
    {
        var root = RepositoryRoot();
        var appDirectory = Path.Combine(root, "src", "Wisp.App");
        var project = File.ReadAllText(Path.Combine(appDirectory, "Wisp.App.csproj"));
        var notice = File.ReadAllText(Path.Combine(
            appDirectory,
            "Assets",
            "Native",
            "THIRD-PARTY-NOTICE.txt"));

        Assert.Contains("ASSET-MANIFEST.csv", project, StringComparison.Ordinal);
        Assert.Contains("THIRD-PARTY-NOTICE.txt", project, StringComparison.Ordinal);
        Assert.Contains("Forza Horizon 6 © Microsoft Corporation.", notice, StringComparison.Ordinal);
        var shippedText = Directory.EnumerateFiles(Path.Combine(root, "src"), "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".cs" or ".xaml" or ".csproj")
            .Select(File.ReadAllText)
            .Append(File.ReadAllText(Path.Combine(root, "README.md")))
            .ToArray();
        Assert.DoesNotContain(shippedText, text =>
            text.Contains("Forza Crypto Tool", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ForzaCryptoTool", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("C_ProfileData", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Career_Garage", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NativeHudProviderIsReadOnlyFailClosedAndDoesNotAddTelemetry()
    {
        var appDirectory = Path.Combine(RepositoryRoot(), "src", "Wisp.App");
        var service = File.ReadAllText(Path.Combine(appDirectory, "NativeHudProcessService.cs")) +
                      File.ReadAllText(Path.Combine(appDirectory, "NativeHudProcessMemory.cs"));
        var resolver = File.ReadAllText(Path.Combine(appDirectory, "NativeHudMemoryResolver.cs"));
        var contract = NativeHudBuildContract.BuiltIn;
        var digital = File.ReadAllText(Path.Combine(appDirectory, "NativeDigitalSpeedometer.xaml.cs"));
        var analogue = File.ReadAllText(Path.Combine(appDirectory, "NativeAnalogSpeedometer.xaml.cs"));
        var assistSelector = File.ReadAllText(Path.Combine(appDirectory, "NativeAssistAssetSelector.cs"));

        Assert.Equal(0x0010U | 0x1000U, NativeHudProcessMemory.RequiredProcessAccess);
        Assert.Contains("OpenProcess(RequiredProcessAccess, false, processId)", service, StringComparison.Ordinal);
        Assert.Contains("ReadProcessMemory", service, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteProcessMemory", service, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CreateRemoteThread", service, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("VirtualAllocEx", service, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UdpClient", service, StringComparison.Ordinal);
        Assert.Contains("matches.Count != 1", resolver, StringComparison.Ordinal);
        Assert.Contains("source + SourceProviderOffset", resolver, StringComparison.Ordinal);
        Assert.Equal(0x0248UL, contract.Fields.ProviderSimRedlineAngularVelocity);
        Assert.Equal(0x024CUL, contract.Fields.ProviderTachometerMaximumAngularVelocity);
        Assert.Contains("TryValidateTachometerState", resolver, StringComparison.Ordinal);
        Assert.Equal(0x01F15590UL, contract.RequiredVtableSlots[0x0210]);
        Assert.Equal(0x01F15580UL, contract.RequiredVtableSlots[0x0680]);
        Assert.Contains("NativeAssistAssetSelector.FileName", digital, StringComparison.Ordinal);
        Assert.Contains("NativeAssistAssetSelector.FileName", analogue, StringComparison.Ordinal);
        Assert.Contains("\"On_glow\"", assistSelector, StringComparison.Ordinal);
        Assert.Contains("snapshot.HeadlightStateAvailable && snapshot.AreHeadlightsOn", assistSelector, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Wisp.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Wisp.sln from the test output directory.");
    }
}
