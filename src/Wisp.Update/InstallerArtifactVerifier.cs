using System.Buffers.Binary;
using System.Diagnostics;

namespace Wisp.Update;

internal static class InstallerArtifactVerifier
{
    private const string ExpectedProductName = "Wisp";

    internal static void Verify(string path, SemanticVersion expectedVersion, long expectedSize)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length != expectedSize)
        {
            throw new UpdateSecurityException("The staged installer size changed before PE validation.");
        }

        try
        {
            VerifyPortableExecutable(path, expectedSize);
        }
        catch (EndOfStreamException exception)
        {
            throw new UpdateSecurityException("The staged installer PE headers are truncated.", exception);
        }

        FileVersionInfo versionInfo;
        try
        {
            versionInfo = FileVersionInfo.GetVersionInfo(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new UpdateSecurityException("The staged installer version resource could not be read.", exception);
        }

        if (!string.Equals(
                VersionResourceText.Normalize(versionInfo.ProductName),
                ExpectedProductName,
                StringComparison.Ordinal))
        {
            throw new UpdateSecurityException("The staged installer ProductName is not Wisp.");
        }

        if (!MatchesFileVersion(versionInfo, expectedVersion) || !MatchesProductVersion(versionInfo, expectedVersion))
        {
            throw new UpdateSecurityException("The staged installer version resource does not match the release version.");
        }
    }

    private static void VerifyPortableExecutable(string path, long expectedSize)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, FileOptions.RandomAccess);
        Span<byte> header = stackalloc byte[64];
        stream.ReadExactly(header);
        if (header[0] != (byte)'M' || header[1] != (byte)'Z')
        {
            throw new UpdateSecurityException("The staged installer does not have an MZ header.");
        }

        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(header[0x3c..]);
        if (peOffset < header.Length || peOffset > expectedSize - 24)
        {
            throw new UpdateSecurityException("The staged installer PE header offset is invalid.");
        }

        stream.Position = peOffset;
        Span<byte> coffHeader = stackalloc byte[24];
        stream.ReadExactly(coffHeader);
        if (BinaryPrimitives.ReadUInt32LittleEndian(coffHeader) != 0x00004550)
        {
            throw new UpdateSecurityException("The staged installer PE signature is invalid.");
        }

        const ushort executableImage = 0x0002;
        const ushort dynamicLinkLibrary = 0x2000;
        var characteristics = BinaryPrimitives.ReadUInt16LittleEndian(coffHeader[22..]);
        if ((characteristics & executableImage) == 0 || (characteristics & dynamicLinkLibrary) != 0)
        {
            throw new UpdateSecurityException("The staged PE is not an executable installer image.");
        }
    }

    private static bool MatchesFileVersion(FileVersionInfo info, SemanticVersion expected) =>
        info.FileMajorPart == expected.Major &&
        info.FileMinorPart == expected.Minor &&
        info.FileBuildPart == expected.Patch &&
        info.FilePrivatePart == 0 &&
        string.Equals(
            VersionResourceText.Normalize(info.FileVersion),
            $"{expected}.0",
            StringComparison.Ordinal);

    private static bool MatchesProductVersion(FileVersionInfo info, SemanticVersion expected) =>
        info.ProductMajorPart == expected.Major &&
        info.ProductMinorPart == expected.Minor &&
        info.ProductBuildPart == expected.Patch &&
        info.ProductPrivatePart == 0 &&
        string.Equals(
            VersionResourceText.Normalize(info.ProductVersion),
            $"{expected}.0",
            StringComparison.Ordinal);
}
