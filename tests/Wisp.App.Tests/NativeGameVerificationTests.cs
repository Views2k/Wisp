using System.Diagnostics;
using Wisp.App.DebugLogging;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeGameVerificationTests
{
    private const string StoreExecutable = @"C:\XboxGames\Forza Horizon 6\Content\ForzaHorizon6.exe";

    [Fact]
    public void DeclinedStorePackageIsOfficialAndReportsThePackageVersion()
    {
        using var process = Process.GetCurrentProcess();
        var package = new NativeStorePackageIdentity("Microsoft.ForteBaseGame_3.462.120.0_x64__8wekyb3d8bbwe", StoreExecutable);

        var declined = NativeGameVerification.DescribeRejected(process, StoreExecutable, package, "1.0.0.0");

        Assert.Equal(new NativeRejectedGame(DiagnosticGamePlatform.XboxStore, "3.462.120.0", NativeGameVerification.Store), declined);
        Assert.True(declined.Verification.IsValid);
    }

    [Fact]
    public void DeclinedProcessWithoutSteamOrStoreIdentityStaysUnverifiedAndKeepsItsVersion()
    {
        using var process = Process.GetCurrentProcess();

        var declined = NativeGameVerification.DescribeRejected(process, Environment.ProcessPath!, null, "6.462.120.0");

        Assert.Equal(new NativeRejectedGame(null, "6.462.120.0", NativeGameVerification.IdentityUnverified), declined);
        Assert.True(declined.Verification.IsValid);
    }

    [Fact]
    public void SteamApiInspectionWithoutTheModuleKeepsTheExistingUnverifiedResult()
    {
        using var process = Process.GetCurrentProcess();

        Assert.Equal(NativeGameVerification.SteamApiUnverified, NativeGameVerification.InspectSteamApi(process, Environment.ProcessPath!));
    }
}
