using Xunit;

namespace Wisp.Updater.Tests;

public sealed class UpdaterWorkflowTests
{
    [Fact]
    public void PreparesBeforeExitThenRunsFixedInstallerAndRestartsVerifiedApp()
    {
        using var layout = new TestLayout();
        var request = layout.CreateReader().Read(layout.RequestPath);
        var events = new List<string>();
        var installerValidator = new RecordingInstallerValidator(events);
        var appValidator = new RecordingApplicationValidator(events);
        var processHost = new RecordingProcessHost(events);
        var workflow = new UpdaterWorkflow(
            installerValidator,
            appValidator,
            new InstalledApplicationRegistrationValidator(
                new RecordingRegistrationReader(events, layout.InstallDirectory)),
            processHost);

        using (var installer = workflow.Prepare(request))
        {
            events.Add("signal-ready");
            workflow.Apply(request, installer, () => events.Add("parent-exited"));
            workflow.Restart(request);
        }

        Assert.Equal(
            [
                "read-registration", "validate-app-1.0.0", "validate-parent", "validate-installer", "signal-ready",
                "wait-parent", "parent-exited", "run-installer", "validate-app-1.2.3",
                "restart-app", "release-installer"
            ],
            events);
        Assert.Equal(layout.ParentProcessId, processHost.ParentProcessId);
        Assert.Equal(layout.ApplicationPath, processHost.ExpectedParentPath);
        Assert.Equal(UpdaterConstants.ParentExitTimeout, processHost.ParentTimeout);
        Assert.Equal(layout.InstallerPath, processHost.InstallerPath);
        Assert.Equal(UpdaterConstants.InstallerArguments, processHost.InstallerArguments);
        Assert.Equal(UpdaterConstants.InstallerExitTimeout, processHost.InstallerTimeout);
        Assert.Equal(
            [new StableVersion(1, 0, 0), new StableVersion(1, 2, 3)],
            appValidator.ValidatedVersions);
        Assert.Equal(layout.ApplicationPath, processHost.StartedApplicationPath);
    }

    [Fact]
    public void InstallerStartInfoUsesOnlySafeFixedArguments()
    {
        using var layout = new TestLayout();

        var startInfo = WindowsUpdaterProcessHost.CreateInstallerStartInfo(
            layout.InstallerPath,
            UpdaterConstants.InstallerArguments);

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Equal(layout.InstallerPath, startInfo.FileName);
        Assert.Equal(layout.RequestDirectory, startInfo.WorkingDirectory);
        Assert.Equal(
            [
                "/VERYSILENT",
                "/SUPPRESSMSGBOXES",
                "/NORESTART",
                "/SP-",
                "/CURRENTUSER",
                "/CLOSEAPPLICATIONS",
                "/WISPUPDATE"
            ],
            startInfo.ArgumentList);
        Assert.DoesNotContain("/ALLUSERS", startInfo.ArgumentList);
    }

    [Fact]
    public void TimedOutInstallerIsStoppedBeforeRecoveryIsAllowed()
    {
        var commandProcessor = Environment.GetEnvironmentVariable("ComSpec");
        Assert.False(string.IsNullOrWhiteSpace(commandProcessor));
        var exception = Assert.Throws<UpdateFailureException>(() =>
            new WindowsUpdaterProcessHost().RunInstaller(
                commandProcessor!,
                ["/d", "/s", "/c", "ping 127.0.0.1 -n 30 >nul"],
                TimeSpan.FromMilliseconds(100)));

        Assert.Equal("UPDATE_INSTALLER_TIMEOUT", exception.ErrorCode);
        Assert.True(exception.RecoveryIsSafe);
    }

    [Theory]
    [InlineData("1.0.0", true)]
    [InlineData("1.2.3", true)]
    [InlineData("9.9.9", false)]
    public void RecoveryPermitsOnlyTheVerifiedSourceOrTargetVersion(
        string installedVersion,
        bool expected)
    {
        using var layout = new TestLayout();
        var request = layout.CreateReader().Read(layout.RequestPath);
        Assert.True(StableVersion.TryParse(installedVersion, out var parsedVersion));
        var appValidator = new VersionSelectiveApplicationValidator(parsedVersion);
        var workflow = new UpdaterWorkflow(
            new RecordingInstallerValidator([]),
            appValidator,
            new InstalledApplicationRegistrationValidator(
                new RecordingRegistrationReader([], layout.InstallDirectory)),
            new RecordingProcessHost([]));

        Assert.Equal(expected, workflow.CanRestart(request));
    }

    private sealed class RecordingInstallerValidator(List<string> events) : IInstallerArtifactValidator
    {
        public ValidatedInstaller Validate(ValidatedUpdateRequest request)
        {
            events.Add("validate-installer");
            return new ValidatedInstaller(request.StagedInstallerPath, new CallbackDisposable(
                () => events.Add("release-installer")));
        }
    }

    private sealed class RecordingApplicationValidator(List<string> events) : IInstalledApplicationValidator
    {
        internal List<StableVersion> ValidatedVersions { get; } = [];

        public void Validate(string applicationPath, StableVersion targetVersion)
        {
            events.Add($"validate-app-{targetVersion}");
            ValidatedVersions.Add(targetVersion);
        }

        public bool IsValid(string applicationPath, StableVersion version) => true;
    }

    private sealed class VersionSelectiveApplicationValidator(StableVersion installedVersion)
        : IInstalledApplicationValidator
    {
        public void Validate(string applicationPath, StableVersion targetVersion) =>
            throw new NotSupportedException();

        public bool IsValid(string applicationPath, StableVersion version) => version == installedVersion;
    }

    private sealed class RecordingRegistrationReader(List<string> events, string installDirectory)
        : IInstallationRegistrationReader
    {
        public string? ReadInstallLocation()
        {
            events.Add("read-registration");
            return installDirectory;
        }
    }

    private sealed class RecordingProcessHost(List<string> events) : IUpdaterProcessHost
    {
        internal int ParentProcessId { get; private set; }

        internal string? ExpectedParentPath { get; private set; }

        internal TimeSpan ParentTimeout { get; private set; }

        internal string? InstallerPath { get; private set; }

        internal IReadOnlyList<string>? InstallerArguments { get; private set; }

        internal TimeSpan InstallerTimeout { get; private set; }

        internal string? StartedApplicationPath { get; private set; }

        public void ValidateParentIdentity(int parentProcessId, string expectedApplicationPath)
        {
            events.Add("validate-parent");
            ParentProcessId = parentProcessId;
            ExpectedParentPath = expectedApplicationPath;
        }

        public void WaitForParentExit(int parentProcessId, string expectedApplicationPath, TimeSpan timeout)
        {
            events.Add("wait-parent");
            ParentProcessId = parentProcessId;
            ExpectedParentPath = expectedApplicationPath;
            ParentTimeout = timeout;
        }

        public void RunInstaller(string installerPath, IReadOnlyList<string> arguments, TimeSpan timeout)
        {
            events.Add("run-installer");
            InstallerPath = installerPath;
            InstallerArguments = arguments;
            InstallerTimeout = timeout;
        }

        public void StartApplication(string applicationPath)
        {
            events.Add("restart-app");
            StartedApplicationPath = applicationPath;
        }
    }

    private sealed class CallbackDisposable(Action callback) : IDisposable
    {
        public void Dispose() => callback();
    }
}
