using Xunit;

namespace Wisp.Updater.Tests;

public sealed class ApplyRequestReaderTests
{
    [Fact]
    public void ReadsSharedApplyRequestAndCanonicalizesItsPaths()
    {
        using var layout = new TestLayout();

        var request = layout.CreateReader().Read(layout.RequestPath);

        Assert.Equal(Path.GetFullPath(layout.RequestPath), request.RequestPath);
        Assert.Equal(Path.GetFullPath(layout.InstallerPath), request.StagedInstallerPath);
        Assert.Equal(new StableVersion(1, 2, 3), request.TargetVersion);
        Assert.Equal(new StableVersion(1, 0, 0), request.SourceVersion);
        Assert.Equal(layout.ParentProcessId, request.ParentProcessId);
        Assert.Equal(Path.GetFullPath(layout.ApplicationPath), request.AppExecutablePath);
        Assert.Equal(TestLayout.InstallerBytes.LongLength, request.ExpectedSizeBytes);
        Assert.Equal(64, request.ExpectedSha256.Length);
        Assert.Equal(request.ExpectedSha256.ToLowerInvariant(), request.ExpectedSha256);
        Assert.True(Wisp.Update.UpdateApplyContract.IsValidReadyEventName(request.ReadyEventName));
    }

    [Fact]
    public void RejectsRequestOutsideStagingRoot()
    {
        using var layout = new TestLayout();
        var outsidePath = Path.Combine(layout.InstallDirectory, "apply.json");
        File.Copy(layout.RequestPath, outsidePath);

        var exception = Assert.Throws<UpdateFailureException>(() => layout.CreateReader().Read(outsidePath));

        Assert.Equal(UpdaterExitCode.InvalidRequest, exception.ExitCode);
        Assert.Equal("UPDATE_REQUEST_PATH", exception.ErrorCode);
    }

    [Fact]
    public void RejectsInstallerOutsideRequestDirectory()
    {
        using var layout = new TestLayout();
        var adjacentDirectory = Path.Combine(layout.StagingRoot, "other");
        Directory.CreateDirectory(adjacentDirectory);
        var adjacentInstaller = Path.Combine(adjacentDirectory, "Wisp-Setup-1.2.3.exe");
        File.Copy(layout.InstallerPath, adjacentInstaller);
        layout.WriteRequest(layout.CreateRequest(installerPath: adjacentInstaller));

        var exception = Assert.Throws<UpdateFailureException>(
            () => layout.CreateReader().Read(layout.RequestPath));

        Assert.Equal("UPDATE_INSTALLER_PATH", exception.ErrorCode);
    }

    [Fact]
    public void RejectsApplicationInsideUpdatesStagingRoot()
    {
        using var layout = new TestLayout();
        var otherApplication = Path.Combine(layout.StagingRoot, "Wisp.exe");
        File.WriteAllBytes(otherApplication, [0x4D, 0x5A]);
        layout.WriteRequest(layout.CreateRequest(applicationPath: otherApplication));

        var exception = Assert.Throws<UpdateFailureException>(
            () => layout.CreateReader().Read(layout.RequestPath));

        Assert.Equal("UPDATE_APPLICATION_PATH", exception.ErrorCode);
    }

    [Fact]
    public void RejectsHelperOutsideRequestDirectory()
    {
        using var layout = new TestLayout();
        var outsideHelper = Path.Combine(layout.InstallDirectory, "Wisp.Updater.exe");
        File.WriteAllBytes(outsideHelper, [0x4D, 0x5A]);
        var reader = new ApplyRequestReader(layout.StagingRoot, outsideHelper, layout.CurrentProcessId);

        var exception = Assert.Throws<UpdateFailureException>(() => reader.Read(layout.RequestPath));

        Assert.Equal("UPDATE_HELPER_PATH", exception.ErrorCode);
    }

    [Fact]
    public void AllowsInstalledApplicationOutsideStagingWhenParentWillBindItsIdentity()
    {
        using var layout = new TestLayout();

        var request = layout.CreateReader().Read(layout.RequestPath);

        Assert.Equal(layout.ApplicationPath, request.AppExecutablePath);
        Assert.False(UpdatePathSafety.IsContainedBy(request.AppExecutablePath, layout.StagingRoot));
    }

    [Theory]
    [InlineData("1.2", "UPDATE_TARGET_VERSION")]
    [InlineData("v1.2.3", "UPDATE_TARGET_VERSION")]
    [InlineData("01.2.3", "UPDATE_TARGET_VERSION")]
    public void RejectsNonStableTargetVersion(string targetVersion, string expectedErrorCode)
    {
        using var layout = new TestLayout();
        layout.WriteRequest(layout.CreateRequest(targetVersion: targetVersion));

        var exception = Assert.Throws<UpdateFailureException>(
            () => layout.CreateReader().Read(layout.RequestPath));

        Assert.Equal(expectedErrorCode, exception.ErrorCode);
    }

    [Theory]
    [InlineData("1.0", "UPDATE_SOURCE_VERSION")]
    [InlineData("1.2.3", "UPDATE_SOURCE_VERSION")]
    [InlineData("2.0.0", "UPDATE_SOURCE_VERSION")]
    public void RejectsInvalidOrNonOlderSourceVersion(string sourceVersion, string expectedErrorCode)
    {
        using var layout = new TestLayout();
        layout.WriteRequest(layout.CreateRequest(sourceVersion: sourceVersion));

        var exception = Assert.Throws<UpdateFailureException>(
            () => layout.CreateReader().Read(layout.RequestPath));

        Assert.Equal(expectedErrorCode, exception.ErrorCode);
    }

    [Fact]
    public void RejectsBroadOrPredictableReadySignalName()
    {
        using var layout = new TestLayout();
        layout.WriteRequest(layout.CreateRequest(readyEventName: @"Local\Wisp.Update.Ready.shared"));

        var exception = Assert.Throws<UpdateFailureException>(
            () => layout.CreateReader().Read(layout.RequestPath));

        Assert.Equal("UPDATE_READY_EVENT", exception.ErrorCode);
    }

    [Fact]
    public void RejectsDuplicateJsonProperties()
    {
        using var layout = new TestLayout();
        var validJson = File.ReadAllText(layout.RequestPath);
        var duplicateJson = validJson.Replace(
            "\"targetVersion\":\"1.2.3\"",
            "\"targetVersion\":\"1.2.3\",\"targetVersion\":\"9.9.9\"",
            StringComparison.Ordinal);
        File.WriteAllText(layout.RequestPath, duplicateJson);

        var exception = Assert.Throws<UpdateFailureException>(
            () => layout.CreateReader().Read(layout.RequestPath));

        Assert.Equal("UPDATE_REQUEST_JSON", exception.ErrorCode);
    }

    [Fact]
    public void RejectsUnknownJsonProperty()
    {
        using var layout = new TestLayout();
        var validJson = File.ReadAllText(layout.RequestPath);
        var unknownPropertyJson = validJson.Replace("{", "{\"installerArguments\":[\"/ALLUSERS\"],", StringComparison.Ordinal);
        File.WriteAllText(layout.RequestPath, unknownPropertyJson);

        var exception = Assert.Throws<UpdateFailureException>(
            () => layout.CreateReader().Read(layout.RequestPath));

        Assert.Equal("UPDATE_REQUEST_JSON", exception.ErrorCode);
    }

    [Fact]
    public void RejectsWrongJsonPropertyType()
    {
        using var layout = new TestLayout();
        var validJson = File.ReadAllText(layout.RequestPath);
        var wrongTypeJson = validJson.Replace(
            $"\"parentProcessId\":{layout.ParentProcessId}",
            $"\"parentProcessId\":\"{layout.ParentProcessId}\"",
            StringComparison.Ordinal);
        Assert.NotEqual(validJson, wrongTypeJson);
        File.WriteAllText(layout.RequestPath, wrongTypeJson);

        var exception = Assert.Throws<UpdateFailureException>(
            () => layout.CreateReader().Read(layout.RequestPath));

        Assert.Equal("UPDATE_REQUEST_JSON", exception.ErrorCode);
    }

    [Fact]
    public void RejectsMissingRequiredJsonProperty()
    {
        using var layout = new TestLayout();
        var validJson = File.ReadAllText(layout.RequestPath);
        var missingPropertyJson = validJson.Replace(
            $",\"expectedSizeBytes\":{TestLayout.InstallerBytes.LongLength}",
            string.Empty,
            StringComparison.Ordinal);
        Assert.NotEqual(validJson, missingPropertyJson);
        File.WriteAllText(layout.RequestPath, missingPropertyJson);

        var exception = Assert.Throws<UpdateFailureException>(
            () => layout.CreateReader().Read(layout.RequestPath));

        Assert.Equal("UPDATE_REQUEST_JSON", exception.ErrorCode);
    }

    [Fact]
    public void RejectsWrongJsonPropertyCasing()
    {
        using var layout = new TestLayout();
        var validJson = File.ReadAllText(layout.RequestPath);
        var wrongCaseJson = validJson.Replace(
            "\"targetVersion\"",
            "\"TargetVersion\"",
            StringComparison.Ordinal);
        Assert.NotEqual(validJson, wrongCaseJson);
        File.WriteAllText(layout.RequestPath, wrongCaseJson);

        var exception = Assert.Throws<UpdateFailureException>(
            () => layout.CreateReader().Read(layout.RequestPath));

        Assert.Equal("UPDATE_REQUEST_JSON", exception.ErrorCode);
    }

    [Fact]
    public void RejectsCurrentProcessAsParent()
    {
        using var layout = new TestLayout();
        layout.WriteRequest(layout.CreateRequest(parentProcessId: layout.CurrentProcessId));

        var exception = Assert.Throws<UpdateFailureException>(
            () => layout.CreateReader().Read(layout.RequestPath));

        Assert.Equal("UPDATE_PARENT_PROCESS", exception.ErrorCode);
    }
}
