using Xunit;

namespace Wisp.Updater.Tests;

public sealed class InstalledApplicationValidatorTests
{
    [Fact]
    public void AcceptsInstalledWispWithTargetVersion()
    {
        using var layout = new TestLayout();
        var inspector = new FixedInspector(new PortableExecutableIdentity(
            true,
            "Wisp",
            "Wisp",
            "1.2.3",
            "1.2.3.0"));

        new InstalledApplicationValidator(inspector).Validate(
            layout.ApplicationPath,
            new StableVersion(1, 2, 3));
    }

    [Fact]
    public void RejectsInstalledApplicationWithWrongVersion()
    {
        using var layout = new TestLayout();
        var inspector = new FixedInspector(new PortableExecutableIdentity(
            true,
            "Wisp",
            "Wisp",
            "1.2.4",
            "1.2.4.0"));

        var exception = Assert.Throws<UpdateFailureException>(() =>
            new InstalledApplicationValidator(inspector).Validate(
                layout.ApplicationPath,
                new StableVersion(1, 2, 3)));

        Assert.Equal(UpdaterExitCode.RestartFailure, exception.ExitCode);
        Assert.Equal("UPDATE_INSTALLED_APP_INVALID", exception.ErrorCode);
    }

    [Fact]
    public void RecoveryAcceptsOnlyAnExactKnownVersion()
    {
        using var layout = new TestLayout();
        var validator = new InstalledApplicationValidator(new FixedInspector(new PortableExecutableIdentity(
            true,
            "Wisp",
            "Wisp",
            "1.0.0",
            "1.0.0.0")));

        Assert.True(validator.IsValid(layout.ApplicationPath, new StableVersion(1, 0, 0)));
        Assert.False(validator.IsValid(layout.ApplicationPath, new StableVersion(1, 2, 3)));
    }

    [Fact]
    public void RejectsInstalledApplicationWithWrongDescription()
    {
        using var layout = new TestLayout();
        var validator = new InstalledApplicationValidator(new FixedInspector(new PortableExecutableIdentity(
            true,
            "Wisp",
            "Wisp Update Helper",
            "1.2.3",
            "1.2.3.0")));

        Assert.False(validator.IsValid(layout.ApplicationPath, new StableVersion(1, 2, 3)));
    }

    [Fact]
    public void RegistrationMustResolveToTheExactRequestedWispExecutable()
    {
        using var layout = new TestLayout();
        var validator = new InstalledApplicationRegistrationValidator(
            new FixedRegistrationReader(layout.InstallDirectory));

        validator.Validate(layout.ApplicationPath);

        var otherPath = Path.Combine(Path.GetDirectoryName(layout.InstallDirectory)!, "Other", "Wisp.exe");
        var exception = Assert.Throws<UpdateFailureException>(() => validator.Validate(otherPath));
        Assert.Equal("UPDATE_INSTALL_REGISTRATION", exception.ErrorCode);
    }

    private sealed class FixedInspector(PortableExecutableIdentity identity) : IPortableExecutableInspector
    {
        public PortableExecutableIdentity Inspect(Stream stream, string path) => identity;
    }

    private sealed class FixedRegistrationReader(string? installLocation) : IInstallationRegistrationReader
    {
        public string? ReadInstallLocation() => installLocation;
    }
}
