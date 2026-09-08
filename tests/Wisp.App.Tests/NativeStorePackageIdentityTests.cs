using Microsoft.Win32.SafeHandles;
using Wisp.App;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeStorePackageIdentityTests
{
    private const string KnownPackageFullName = "Microsoft.ForteBaseGame_3.430.771.0_x64__8wekyb3d8bbwe";
    private const string PackagePath = @"C:\Games\Forza\Content";
    private const string ExecutablePath = PackagePath + @"\ForzaHorizon6.exe";

    [Fact]
    public void ExactStorePackageAndExecutableMatch()
    {
        Assert.True(Validate(ExecutablePath, out var identity));
        Assert.Equal(KnownPackageFullName, identity.PackageFullName);
        Assert.Equal(ExecutablePath.ToUpperInvariant(), identity.ExecutablePath);
    }

    [Theory]
    [InlineData(@"c:\games\forza\content\FORZAHORIZON6.EXE")]
    [InlineData(@"\\?\C:\Games\Forza\Content\ForzaHorizon6.exe")]
    [InlineData(@"C:\Games\Forza\Content\.\ForzaHorizon6.exe")]
    public void EquivalentAbsoluteExecutablePathsAreNormalized(string path) => Assert.True(Validate(path, out _));

    [Theory]
    [InlineData(@"C:\Games\Forza\ContentOther\ForzaHorizon6.exe")]
    [InlineData(@"C:\Games\Forza\Content\..\ForzaHorizon6.exe")]
    [InlineData(@"C:\Games\Forza\Content\Other\ForzaHorizon6.exe")]
    [InlineData(@"C:\Games\Forza\Content\Other.exe")]
    [InlineData(@"C:\Games\Forza\Content\ForzaHorizon6.exe:stream")]
    [InlineData(@"C:\Games\Forza\Content\ForzaHorizon6.exe.bak")]
    [InlineData(@"Content\ForzaHorizon6.exe")]
    [InlineData(@"\\.\C:\Games\Forza\Content\ForzaHorizon6.exe")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("C:\\Games\\Forza\\Content\\ForzaHorizon6.exe\0ignored")]
    public void WrongOrMalformedExecutablePathIsRejected(string? path)
    {
        Assert.False(Validate(path, out var identity));
        Assert.Equal(default, identity);
    }

    [Theory]
    [InlineData("Microsoft.ForteBaseGame_3.430.772_x64__8wekyb3d8bbwe")]
    [InlineData("Microsoft.ForteBaseGame_3.430.772.65536_x64__8wekyb3d8bbwe")]
    [InlineData("Microsoft.ForteBaseGame_3.430.772.-1_x64__8wekyb3d8bbwe")]
    [InlineData("Microsoft.ForteBaseGame_3.430.772.0.1_x64__8wekyb3d8bbwe")]
    [InlineData("Microsoft.ForteBaseGame_3.430.772.0 _x64__8wekyb3d8bbwe")]
    [InlineData("Microsoft.ForteBaseGame_3.430.772.0_x64__8wekyb3d8bbwe_extra")]
    [InlineData("Microsoft.ForteBaseGame_3.430.771.0_x86__8wekyb3d8bbwe")]
    [InlineData("Microsoft.ForteBaseGame_3.430.771.0_x64__otherpublisher")]
    [InlineData("Microsoft.OtherGame_3.430.771.0_x64__8wekyb3d8bbwe")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherPackageOrMalformedVersionIsRejected(string? name) => Assert.False(
        NativeStorePackageIdentity.TryValidate(name, 3, PackagePath, PackagePath, ExecutablePath, out _));

    [Theory]
    [InlineData("Microsoft.ForteBaseGame_3.440.853.0_x64__8wekyb3d8bbwe")]
    [InlineData("Microsoft.ForteBaseGame_6.430.771.0_x64__8wekyb3d8bbwe")]
    public void NewPackageVersionStillRequiresItsOwnTrustedBuildContract(string fullName)
    {
        using var handle = new SafeProcessHandle(new IntPtr(42), ownsHandle: false);
        var api = new FakePackageApi { FullName = fullName };
        Assert.True(NativeStorePackageIdentity.TryRead(handle, ExecutablePath, api, out var identity));
        Assert.Equal(fullName, identity.PackageFullName);

        var catalog = new NativeCompatibilityCatalog(NativeHudBuildContract.BuiltIn, null,
            new Dictionary<string, byte[]>(), NativeHudBuildContract.AdditionalBuiltIns);
        Assert.Null(catalog.FindStore(identity.PackageFullName, NativeHudBuildContract.StoreBuiltIn.ImageSize));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void NonStoreOriginsAreRejected(int origin) => Assert.False(
        NativeStorePackageIdentity.TryValidate(KnownPackageFullName,
            origin, PackagePath, PackagePath, ExecutablePath, out _));

    [Theory]
    [InlineData(null, PackagePath)]
    [InlineData(PackagePath, null)]
    [InlineData("", PackagePath)]
    [InlineData(PackagePath, "relative")]
    [InlineData(@"C:\Games\Other\Content", PackagePath)]
    [InlineData(PackagePath, @"C:\Games\Other\Content")]
    public void UnavailableOrUnverifiedPackagePathsAreRejected(string? original, string? effective) => Assert.False(
        NativeStorePackageIdentity.TryValidate(KnownPackageFullName,
            3, original, effective, ExecutablePath, out _));

    [Fact]
    public void NativeQueriesUseOriginalAndEffectivePathsWithoutMutablePath()
    {
        using var handle = new SafeProcessHandle(new IntPtr(42), ownsHandle: false);
        var api = new FakePackageApi();
        Assert.True(NativeStorePackageIdentity.TryRead(handle, ExecutablePath, api, out var identity));
        Assert.Equal(KnownPackageFullName, identity.PackageFullName);
        Assert.Equal(new[] { 0, 0, 2, 2 }, api.PathTypes);
        Assert.Equal(2, api.NameCalls);
    }

    [Theory]
    [InlineData("no-package")]
    [InlineData("name-first-success")]
    [InlineData("name-small")]
    [InlineData("name-large")]
    [InlineData("name-second-error")]
    [InlineData("name-growth")]
    [InlineData("name-no-terminator")]
    [InlineData("name-interior-null")]
    [InlineData("origin-error")]
    [InlineData("original-error")]
    [InlineData("effective-error")]
    [InlineData("path-large")]
    [InlineData("path-growth")]
    [InlineData("missing-api")]
    public void NativeErrorsAndMalformedBufferResultsFailClosed(string fault)
    {
        using var handle = new SafeProcessHandle(new IntPtr(42), ownsHandle: false);
        var api = new FakePackageApi { Fault = fault };
        Assert.False(NativeStorePackageIdentity.TryRead(handle, ExecutablePath, api, out var identity));
        Assert.Equal(default, identity);
        Assert.InRange(api.NameCalls, 1, 2);
        Assert.InRange(api.PathTypes.Count, 0, 4);
    }

    [Fact]
    public void InvalidOrClosedHandleDoesNotCallWindows()
    {
        var api = new FakePackageApi();
        using var invalid = new SafeProcessHandle(IntPtr.Zero, ownsHandle: false);
        Assert.False(NativeStorePackageIdentity.TryRead(invalid, ExecutablePath, api, out _));
        using var closed = new SafeProcessHandle(new IntPtr(42), ownsHandle: false);
        closed.Dispose();
        Assert.False(NativeStorePackageIdentity.TryRead(closed, ExecutablePath, api, out _));
        Assert.Equal(0, api.NameCalls);
    }

    private static bool Validate(string? path, out NativeStorePackageIdentity identity) =>
        NativeStorePackageIdentity.TryValidate(KnownPackageFullName,
            3, PackagePath, PackagePath, path, out identity);

    [Fact]
    public void DifferentPathsNeedTheSameLive128BitFileIdentity()
    {
        var api = new FakeFileAliasApi();
        Assert.True(NativeStorePackageIdentity.MatchesExecutableFileAlias(
            ExecutablePath, @"C:\Projected\ForzaHorizon6.exe", api));
        Assert.Equal(2, api.Handles.Count);
        Assert.Equal(2, api.ReadCount);
        Assert.All(api.Handles, handle => Assert.True(handle.IsClosed));
    }

    [Theory]
    [InlineData("volume")]
    [InlineData("id-low")]
    [InlineData("id-high")]
    [InlineData("size")]
    [InlineData("created")]
    [InlineData("modified")]
    public void AliasMetadataMismatchIsRejected(string changed)
    {
        var expected = FakeFileAliasApi.ValidIdentity;
        var observed = changed switch
        {
            "volume" => expected with { Volume = expected.Volume + 1 },
            "id-low" => expected with { FileIdLow = expected.FileIdLow + 1 },
            "id-high" => expected with { FileIdHigh = expected.FileIdHigh + 1 },
            "size" => expected with { Length = expected.Length + 1 },
            "created" => expected with { CreationTime = expected.CreationTime + 1 },
            "modified" => expected with { LastWriteTime = expected.LastWriteTime + 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(changed))
        };
        Assert.False(NativeStorePackageIdentity.MatchesFileIdentity(expected, observed));
        Assert.False(NativeStorePackageIdentity.MatchesFileIdentity(observed, expected));
    }

    [Theory]
    [InlineData("zero-volume")]
    [InlineData("zero-id")]
    [InlineData("short-file")]
    [InlineData("huge-file")]
    [InlineData("zero-created")]
    [InlineData("zero-modified")]
    [InlineData("directory")]
    [InlineData("device")]
    public void EqualButInvalidFileMetadataCannotMatch(string changed)
    {
        var valid = FakeFileAliasApi.ValidIdentity;
        var invalid = changed switch
        {
            "zero-volume" => valid with { Volume = 0 },
            "zero-id" => valid with { FileIdLow = 0, FileIdHigh = 0 },
            "short-file" => valid with { Length = 4095 },
            "huge-file" => valid with { Length = 2147483649 },
            "zero-created" => valid with { CreationTime = 0 },
            "zero-modified" => valid with { LastWriteTime = 0 },
            "directory" => valid with { Attributes = 0x10 },
            "device" => valid with { Attributes = 0x40 },
            _ => throw new ArgumentOutOfRangeException(nameof(changed))
        };
        Assert.False(NativeStorePackageIdentity.MatchesFileIdentity(invalid, invalid));
        Assert.False(NativeStorePackageIdentity.MatchesFileIdentity(default, default));
    }

    [Fact]
    public void HighHalfOnlyFileIdentifierIsNotTreatedAsZero()
    {
        var identity = FakeFileAliasApi.ValidIdentity with { FileIdLow = 0, FileIdHigh = 12 };
        Assert.True(NativeStorePackageIdentity.MatchesFileIdentity(identity, identity));
    }

    [Theory]
    [InlineData("open-first")]
    [InlineData("open-second")]
    [InlineData("read-first")]
    [InlineData("read-second")]
    [InlineData("access-denied")]
    [InlineData("missing-api")]
    public void AttributeHandleErrorsFailClosedAndReleaseHandles(string fault)
    {
        var api = new FakeFileAliasApi { Fault = fault };
        Assert.False(NativeStorePackageIdentity.MatchesExecutableFileAlias(
            ExecutablePath, @"C:\Projected\ForzaHorizon6.exe", api));
        Assert.All(api.Handles, handle => Assert.True(handle.IsClosed));
    }

    [Theory]
    [InlineData(@"C:\Projected\NotForza.exe")]
    [InlineData(@"C:\Projected\ForzaHorizon6.exe:stream")]
    [InlineData(@"C:\Projected\ForzaHorizon6.exe.bak")]
    [InlineData(@"\\unavailable\share\ForzaHorizon6.exe")]
    [InlineData(@"\\.\C:\Projected\ForzaHorizon6.exe")]
    [InlineData(@"relative\ForzaHorizon6.exe")]
    [InlineData("")]
    public void InvalidAliasPathDoesNotOpenAnyFile(string invalidPath)
    {
        var api = new FakeFileAliasApi();
        Assert.False(NativeStorePackageIdentity.MatchesExecutableFileAlias(ExecutablePath, invalidPath, api));
        Assert.False(NativeStorePackageIdentity.MatchesExecutableFileAlias(invalidPath, ExecutablePath, api));
        Assert.Empty(api.Handles);
    }

    private sealed class FakeFileAliasApi : NativeStorePackageIdentity.IFileAliasApi
    {
        internal static readonly NativeStorePackageIdentity.FileAliasIdentity ValidIdentity = new(11, 12, 13, 50000, 100, 200, 0x80);
        internal string Fault { get; init; } = "";
        internal List<SafeFileHandle> Handles { get; } = [];
        internal int ReadCount { get; private set; }

        public SafeFileHandle OpenAttributes(string path)
        {
            if (Fault == "access-denied") throw new UnauthorizedAccessException();
            if (Fault == "missing-api") throw new EntryPointNotFoundException();
            var invalid = Fault == "open-first" && Handles.Count == 0 || Fault == "open-second" && Handles.Count == 1;
            var handle = new SafeFileHandle(invalid ? new IntPtr(-1) : new IntPtr(41 + Handles.Count), ownsHandle: false);
            Handles.Add(handle);
            return handle;
        }

        public bool TryReadIdentity(SafeFileHandle handle, out NativeStorePackageIdentity.FileAliasIdentity identity)
        {
            Assert.Equal(2, Handles.Count);
            Assert.All(Handles, existing => Assert.False(existing.IsClosed));
            ReadCount++;
            identity = ValidIdentity;
            return !(Fault == "read-first" && ReadCount == 1 || Fault == "read-second" && ReadCount == 2);
        }
    }

    private sealed class FakePackageApi : NativeStorePackageIdentity.IPackageApi
    {
        internal string FullName { get; init; } = KnownPackageFullName;
        internal string Fault { get; init; } = "";
        internal int NameCalls { get; private set; }
        internal List<int> PathTypes { get; } = [];

        public int GetFullName(SafeProcessHandle handle, ref uint length, char[]? buffer)
        {
            NameCalls++;
            if (Fault == "missing-api") throw new EntryPointNotFoundException();
            if (Fault == "no-package") return 15700;
            if (buffer is null)
            {
                if (Fault == "name-first-success") return 0;
                if (Fault == "name-small") { length = 1; return 122; }
                if (Fault == "name-large") { length = uint.MaxValue; return 122; }
            }
            else
            {
                if (Fault == "name-second-error") return 122;
                if (Fault == "name-growth") { length++; return 0; }
            }

            var result = Copy(FullName, ref length, buffer);
            if (buffer is not null && Fault == "name-no-terminator") buffer[^1] = 'x';
            if (buffer is not null && Fault == "name-interior-null") buffer[3] = '\0';
            return result;
        }

        public int GetOrigin(string fullName, out int origin)
        {
            Assert.Equal(FullName, fullName);
            origin = 3;
            return Fault == "origin-error" ? 5 : 0;
        }

        public int GetPath(string fullName, int pathType, ref uint length, char[]? buffer)
        {
            Assert.Equal(FullName, fullName);
            PathTypes.Add(pathType);
            if (Fault == "original-error" && pathType == 0 || Fault == "effective-error" && pathType == 2) return 15707;
            if (Fault == "path-large" && buffer is null) { length = uint.MaxValue; return 122; }
            if (Fault == "path-growth" && buffer is not null) { length++; return 0; }
            return Copy(PackagePath, ref length, buffer);
        }

        private static int Copy(string value, ref uint length, char[]? buffer)
        {
            length = (uint)value.Length + 1;
            if (buffer is null) return 122;
            value.CopyTo(0, buffer, 0, value.Length);
            buffer[value.Length] = '\0';
            return 0;
        }
    }
}
