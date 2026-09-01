using Xunit;

namespace Wisp.Update.Tests;

public sealed class InstallerArtifactVerifierIntegrationTests
{
    [Fact]
    public void ConfiguredInstallerPassesRuntimeVerification()
    {
        var path = Environment.GetEnvironmentVariable("WISP_TEST_INSTALLER_PATH");
        var versionText = Environment.GetEnvironmentVariable("WISP_TEST_INSTALLER_VERSION");
        if (path is null && versionText is null)
        {
            return;
        }

        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.False(string.IsNullOrWhiteSpace(versionText));
        Assert.True(Path.IsPathFullyQualified(path));
        Assert.True(File.Exists(path));

        var version = SemanticVersion.Parse(versionText);
        var expectedSize = new FileInfo(path).Length;

        InstallerArtifactVerifier.Verify(path, version, expectedSize);
    }
}
