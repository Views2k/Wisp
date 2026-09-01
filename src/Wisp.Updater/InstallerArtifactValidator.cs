using System.Security.Cryptography;
using Wisp.Update;

namespace Wisp.Updater;

internal sealed class ValidatedInstaller : IDisposable
{
    private IDisposable? _lease;

    internal ValidatedInstaller(string path, IDisposable? lease = null)
    {
        Path = path;
        _lease = lease;
    }

    internal string Path { get; }

    public void Dispose()
    {
        Interlocked.Exchange(ref _lease, null)?.Dispose();
    }
}

internal interface IInstallerArtifactValidator
{
    ValidatedInstaller Validate(ValidatedUpdateRequest request);
}

internal sealed class InstallerArtifactValidator : IInstallerArtifactValidator
{
    private readonly IPortableExecutableInspector _executableInspector;

    internal InstallerArtifactValidator(IPortableExecutableInspector executableInspector)
    {
        _executableInspector = executableInspector;
    }

    public ValidatedInstaller Validate(ValidatedUpdateRequest request)
    {
        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                request.StagedInstallerPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.SequentialScan);

            if (stream.Length != request.ExpectedSizeBytes)
            {
                throw ValidationFailure(
                    "UPDATE_INSTALLER_SIZE_MISMATCH",
                    "The staged installer size did not match the immutable release metadata.");
            }

            var actualHash = SHA256.HashData(stream);
            var expectedHash = Convert.FromHexString(request.ExpectedSha256);
            if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
            {
                throw ValidationFailure(
                    "UPDATE_INSTALLER_HASH_MISMATCH",
                    "The staged installer did not match the immutable release metadata.");
            }

            var identity = _executableInspector.Inspect(stream, request.StagedInstallerPath);
            if (!identity.IsExecutable)
            {
                throw ValidationFailure(
                    "UPDATE_INSTALLER_NOT_PE",
                    "The staged update is not a valid Windows executable.");
            }

            var productName = VersionResourceText.Normalize(identity.ProductName);
            var fileDescription = VersionResourceText.Normalize(identity.FileDescription);
            if (!string.Equals(productName, UpdaterConstants.ExpectedProductName, StringComparison.Ordinal)
                || !string.Equals(
                    fileDescription,
                    UpdaterConstants.ExpectedInstallerDescription,
                    StringComparison.Ordinal))
            {
                throw ValidationFailure(
                    "UPDATE_INSTALLER_PRODUCT_MISMATCH",
                    "The staged executable is not a Wisp installer.");
            }

            if (!request.TargetVersion.MatchesVersionResource(identity.ProductVersion)
                || !request.TargetVersion.MatchesVersionResource(identity.FileVersion))
            {
                throw ValidationFailure(
                    "UPDATE_INSTALLER_VERSION_MISMATCH",
                    "The staged installer version did not match the requested update.");
            }

            var validatedInstaller = new ValidatedInstaller(request.StagedInstallerPath, stream);
            stream = null;
            return validatedInstaller;
        }
        catch (UpdateFailureException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            throw ValidationFailure(
                "UPDATE_INSTALLER_READ_FAILED",
                "The staged installer could not be verified.",
                exception);
        }
        finally
        {
            stream?.Dispose();
        }
    }

    private static UpdateFailureException ValidationFailure(
        string errorCode,
        string safeMessage,
        Exception? innerException = null) =>
        new(UpdaterExitCode.InstallerValidationFailure, errorCode, safeMessage, innerException);
}
