using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeCompatibilityStatusTextTests
{
    private const string Installed = "The available compatibility packs are installed.";

    [Fact]
    public void AlreadyInstalledAppearsOnceWhenCatalogAndCheckAgree()
    {
        Assert.Equal(Installed, NativeCompatibilityStatusText.Compose(Installed, null, Installed));
    }

    [Fact]
    public void CatalogDiagnosticIsKeptWithoutRepeatingInstalledStatus()
    {
        const string diagnostic = "A cached pack could not be loaded.";
        Assert.Equal($"{Installed} {diagnostic}",
            NativeCompatibilityStatusText.Compose(Installed, diagnostic, Installed));
    }

    [Theory]
    [InlineData("The compatibility update could not be downloaded.")]
    [InlineData("The selected compatibility pack could not be read. The existing catalog is unchanged.")]
    [InlineData("Offline: no update publisher configured.")]
    public void DistinctOperationStatusRemainsVisible(string operationStatus)
    {
        Assert.Equal($"{Installed} {operationStatus}",
            NativeCompatibilityStatusText.Compose(Installed, null, operationStatus));
    }

    [Fact]
    public void EmptyMessagesAndOuterWhitespaceDoNotAddDuplicatesOrGaps()
    {
        Assert.Equal(Installed, NativeCompatibilityStatusText.Compose($" {Installed} ", "\r\n", Installed));
        Assert.Equal(Installed, NativeCompatibilityStatusText.Compose(null, null, Installed));
        Assert.Equal(string.Empty, NativeCompatibilityStatusText.Compose(null, " ", null));
    }

    [Fact]
    public void DistinctMessagesKeepTheirOriginalOrderAndContents()
    {
        Assert.Equal("Catalog status. Catalog warning. Update status.",
            NativeCompatibilityStatusText.Compose("Catalog status.", "Catalog warning.", "Update status."));
    }

    [Fact]
    public void InstalledPacksDoNotImplyTheCurrentUnsupportedBuildIsSupported()
    {
        Assert.Equal($"The current Forza build is not supported by the installed compatibility packs. {Installed}",
            NativeCompatibilityStatusText.Compose(Installed, null, Installed, NativeAssistProviderStatus.UnsupportedBuild));
    }

    [Fact]
    public void UnsupportedBuildExplanationDoesNotHideUpdateFailure()
    {
        const string failure = "The compatibility update is unavailable; the offline catalog is unchanged.";
        Assert.Equal($"The current Forza build is not supported by the installed compatibility packs. {Installed} {failure}",
            NativeCompatibilityStatusText.Compose(Installed, null, failure, NativeAssistProviderStatus.UnsupportedBuild));
    }

    [Theory]
    [InlineData(NativeAssistProviderStatus.GameNotRunning)]
    [InlineData(NativeAssistProviderStatus.Unavailable)]
    [InlineData(NativeAssistProviderStatus.Ready)]
    [InlineData(NativeAssistProviderStatus.AccessDenied)]
    public void OtherNativeStatesAreNotDescribedAsAnUnsupportedBuild(NativeAssistProviderStatus status)
    {
        Assert.Equal(Installed, NativeCompatibilityStatusText.Compose(Installed, null, Installed, status));
    }
}
