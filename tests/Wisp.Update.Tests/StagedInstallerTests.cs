using System.Text;
using Xunit;

namespace Wisp.Update.Tests;

public sealed class StagedInstallerTests
{
    [Fact]
    public void MatchingStagedInstallerIsVerifiedForReuse()
    {
        // After the update helper could not start the installer and restarted Wisp, one PC on 2.7.3
        // downloaded the same 95 MB installer again twice.
        var installer = File.ReadAllBytes(ReleaseTestData.FixtureExecutablePath());
        using var directory = new TemporaryDirectory();
        var release = Release(installer.LongLength, ReleaseTestData.Sha256(installer));
        var path = Path.Combine(directory.Path, release.FileName);
        File.WriteAllBytes(path, installer);

        var verified = Assert.IsType<VerifiedInstaller>(WispUpdateClient.TryVerifyStagedInstaller(release, path));

        Assert.Equal(path, verified.StagedPath);
        Assert.Equal(release.Version, verified.Version);
        Assert.Equal(installer.LongLength, verified.Size);
        Assert.Equal(release.Sha256, verified.Sha256);
    }

    [Fact]
    public void ChangedTruncatedOrMisnamedInstallerIsNotReused()
    {
        var installer = File.ReadAllBytes(ReleaseTestData.FixtureExecutablePath());
        using var directory = new TemporaryDirectory();
        var release = Release(installer.LongLength, ReleaseTestData.Sha256(installer));
        var path = Path.Combine(directory.Path, release.FileName);

        var changed = installer.ToArray();
        changed[^1] ^= 0xff;
        File.WriteAllBytes(path, changed);
        Assert.Null(WispUpdateClient.TryVerifyStagedInstaller(release, path));

        File.WriteAllBytes(path, installer[..^1]);
        Assert.Null(WispUpdateClient.TryVerifyStagedInstaller(release, path));

        var misnamed = Path.Combine(directory.Path, "Wisp-Setup-9.8.6.exe");
        File.WriteAllBytes(misnamed, installer);
        Assert.Null(WispUpdateClient.TryVerifyStagedInstaller(release, misnamed));

        Assert.Null(WispUpdateClient.TryVerifyStagedInstaller(release, Path.Combine(directory.Path, "missing", release.FileName)));
    }

    [Fact]
    public void MatchingHashOfAFileThatIsNotTheInstallerIsNotReused()
    {
        var content = Encoding.ASCII.GetBytes("not an installer");
        using var directory = new TemporaryDirectory();
        var release = Release(content.LongLength, ReleaseTestData.Sha256(content));
        var path = Path.Combine(directory.Path, release.FileName);
        File.WriteAllBytes(path, content);

        Assert.Null(WispUpdateClient.TryVerifyStagedInstaller(release, path));
    }

    private static UpdateRelease Release(long size, string sha256) =>
        GitHubReleaseParser.Parse(Encoding.UTF8.GetBytes(ReleaseTestData.CreateJson(size: size, sha256: sha256)));
}
