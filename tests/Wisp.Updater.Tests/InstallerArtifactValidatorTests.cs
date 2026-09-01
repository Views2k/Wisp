using System.Security.Cryptography;
using Wisp.Update;
using Xunit;

namespace Wisp.Updater.Tests;

public sealed class InstallerArtifactValidatorTests
{
    private static readonly PortableExecutableIdentity ValidIdentity = new(
        true,
        "Wisp",
        "Wisp installer",
        "1.2.3.0",
        "1.2.3.0");

    [Fact]
    public void AcceptsExactSizeHashPeProductAndVersionAndLocksArtifact()
    {
        using var layout = new TestLayout();
        var request = layout.CreateReader().Read(layout.RequestPath);
        var validator = new InstallerArtifactValidator(new StubInspector(ValidIdentity));

        using (var installer = validator.Validate(request))
        {
            Assert.Equal(layout.InstallerPath, installer.Path);
            Assert.Throws<IOException>(() =>
            {
                using var ignored = new FileStream(
                    layout.InstallerPath,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.ReadWrite);
            });
        }

        using var writable = new FileStream(
            layout.InstallerPath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite);
        Assert.True(writable.CanWrite);
    }

    [Fact]
    public void AcceptsOnlyTrailingNulAndSpaceVersionResourcePadding()
    {
        using var layout = new TestLayout();
        var request = layout.CreateReader().Read(layout.RequestPath);
        var paddedIdentity = ValidIdentity with
        {
            ProductName = "Wisp\0  ",
            FileDescription = "Wisp installer  \0",
            ProductVersion = "1.2.3.0\0  ",
            FileVersion = "1.2.3.0  \0"
        };

        using var installer = new InstallerArtifactValidator(new StubInspector(paddedIdentity))
            .Validate(request);

        Assert.Equal(layout.InstallerPath, installer.Path);
    }

    [Fact]
    public void RejectsSizeMismatchBeforeMetadataInspection()
    {
        using var layout = new TestLayout();
        var validRequest = layout.CreateReader().Read(layout.RequestPath);
        var request = validRequest with { ExpectedSizeBytes = validRequest.ExpectedSizeBytes + 1 };
        var inspector = new StubInspector(ValidIdentity);

        var exception = Assert.Throws<UpdateFailureException>(
            () => new InstallerArtifactValidator(inspector).Validate(request));

        Assert.Equal("UPDATE_INSTALLER_SIZE_MISMATCH", exception.ErrorCode);
        Assert.Equal(0, inspector.CallCount);
    }

    [Fact]
    public void RejectsHashMismatchBeforeMetadataInspection()
    {
        using var layout = new TestLayout();
        var validRequest = layout.CreateReader().Read(layout.RequestPath);
        var request = validRequest with { ExpectedSha256 = new string('0', 64) };
        var inspector = new StubInspector(ValidIdentity);

        var exception = Assert.Throws<UpdateFailureException>(
            () => new InstallerArtifactValidator(inspector).Validate(request));

        Assert.Equal("UPDATE_INSTALLER_HASH_MISMATCH", exception.ErrorCode);
        Assert.Equal(0, inspector.CallCount);
    }

    [Theory]
    [InlineData("not-pe", "UPDATE_INSTALLER_NOT_PE")]
    [InlineData("product", "UPDATE_INSTALLER_PRODUCT_MISMATCH")]
    [InlineData("description", "UPDATE_INSTALLER_PRODUCT_MISMATCH")]
    [InlineData("product-version", "UPDATE_INSTALLER_VERSION_MISMATCH")]
    [InlineData("file-version", "UPDATE_INSTALLER_VERSION_MISMATCH")]
    public void RejectsInvalidPeIdentity(string mutation, string expectedErrorCode)
    {
        using var layout = new TestLayout();
        var request = layout.CreateReader().Read(layout.RequestPath);
        var identity = mutation switch
        {
            "not-pe" => ValidIdentity with { IsExecutable = false },
            "product" => ValidIdentity with { ProductName = "Other" },
            "description" => ValidIdentity with { FileDescription = "Other installer" },
            "product-version" => ValidIdentity with { ProductVersion = "1.2.4.0" },
            "file-version" => ValidIdentity with { FileVersion = "1.2.4.0" },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };

        var exception = Assert.Throws<UpdateFailureException>(
            () => new InstallerArtifactValidator(new StubInspector(identity)).Validate(request));

        Assert.Equal(expectedErrorCode, exception.ErrorCode);
        Assert.Equal(UpdaterExitCode.InstallerValidationFailure, exception.ExitCode);
    }

    [Theory]
    [InlineData(" Wisp", "Wisp installer", "1.2.3.0", "1.2.3.0", "UPDATE_INSTALLER_PRODUCT_MISMATCH")]
    [InlineData("Wisp\t", "Wisp installer", "1.2.3.0", "1.2.3.0", "UPDATE_INSTALLER_PRODUCT_MISMATCH")]
    [InlineData("Wisp", "Wisp installer\n", "1.2.3.0", "1.2.3.0", "UPDATE_INSTALLER_PRODUCT_MISMATCH")]
    [InlineData("Wisp", "Wisp installer", " 1.2.3.0", "1.2.3.0", "UPDATE_INSTALLER_VERSION_MISMATCH")]
    [InlineData("Wisp", "Wisp installer", "1.2.3.0\t", "1.2.3.0", "UPDATE_INSTALLER_VERSION_MISMATCH")]
    [InlineData("Wisp", "Wisp installer", "1.2.3.0", "1.2.3.0\n", "UPDATE_INSTALLER_VERSION_MISMATCH")]
    [InlineData("Wi\0sp", "Wisp installer", "1.2.3.0", "1.2.3.0", "UPDATE_INSTALLER_PRODUCT_MISMATCH")]
    public void RejectsUnsupportedIdentityPadding(
        string productName,
        string fileDescription,
        string productVersion,
        string fileVersion,
        string expectedErrorCode)
    {
        using var layout = new TestLayout();
        var request = layout.CreateReader().Read(layout.RequestPath);
        var identity = new PortableExecutableIdentity(
            true, productName, fileDescription, productVersion, fileVersion);

        var exception = Assert.Throws<UpdateFailureException>(
            () => new InstallerArtifactValidator(new StubInspector(identity)).Validate(request));

        Assert.Equal(expectedErrorCode, exception.ErrorCode);
    }

    [Fact]
    public void ConfiguredInstallerPassesRealRuntimeValidation()
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
        Assert.True(StableVersion.TryParse(versionText, out var version));

        var file = new FileInfo(path);
        using var hashStream = file.OpenRead();
        var hash = Convert.ToHexString(SHA256.HashData(hashStream)).ToLowerInvariant();
        var request = new ValidatedUpdateRequest(
            Path.Combine(file.DirectoryName!, "apply.json"),
            path,
            version,
            new StableVersion(0, 0, 0),
            1,
            Path.Combine(file.DirectoryName!, UpdaterConstants.ApplicationFileName),
            hash,
            file.Length,
            UpdateApplyContract.CreateReadyEventName(new string('a', 64)));

        using var installer = new InstallerArtifactValidator(new WindowsPortableExecutableInspector())
            .Validate(request);
        Assert.Equal(path, installer.Path);
    }

    [Fact]
    public void RealInspectorRejectsNonPeFile()
    {
        using var layout = new TestLayout();
        using var stream = File.OpenRead(layout.InstallerPath);

        var identity = new WindowsPortableExecutableInspector().Inspect(stream, layout.InstallerPath);

        Assert.False(identity.IsExecutable);
    }

    [Fact]
    public void RealInspectorRejectsDllImage()
    {
        var sourcePath = Path.Combine(AppContext.BaseDirectory, "Wisp.Updater.exe");
        Assert.True(File.Exists(sourcePath), "The updater app host was not copied to the test output.");
        var path = Path.Combine(Path.GetTempPath(), $"Wisp.Updater.DllImage.{Guid.NewGuid():N}.exe");
        try
        {
            var bytes = File.ReadAllBytes(sourcePath);
            var peOffset = BitConverter.ToInt32(bytes, 0x3c);
            var characteristicsOffset = peOffset + 22;
            var characteristics = BitConverter.ToUInt16(bytes, characteristicsOffset);
            BitConverter.GetBytes((ushort)(characteristics | 0x2000))
                .CopyTo(bytes, characteristicsOffset);
            File.WriteAllBytes(path, bytes);
            using var stream = File.OpenRead(path);

            var identity = new WindowsPortableExecutableInspector().Inspect(stream, path);

            Assert.False(identity.IsExecutable);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RealInspectorAcceptsUpdaterAppHostWithExactIdentity()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Wisp.Updater.exe");
        Assert.True(File.Exists(path), "The updater app host was not copied to the test output.");
        using var stream = File.OpenRead(path);

        var identity = new WindowsPortableExecutableInspector().Inspect(stream, path);
        var assemblyVersion = typeof(UpdaterApplication).Assembly.GetName().Version
            ?? throw new InvalidOperationException("The updater assembly version is unavailable.");
        var expectedProductVersion = $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";

        Assert.True(identity.IsExecutable);
        Assert.Equal("Wisp", identity.ProductName);
        Assert.Equal("Wisp Update Helper", identity.FileDescription);
        Assert.Equal(expectedProductVersion, identity.ProductVersion);
        Assert.Equal($"{expectedProductVersion}.0", identity.FileVersion);
    }

    private sealed class StubInspector(PortableExecutableIdentity identity) : IPortableExecutableInspector
    {
        internal int CallCount { get; private set; }

        public PortableExecutableIdentity Inspect(Stream stream, string path)
        {
            CallCount++;
            return identity;
        }
    }
}
