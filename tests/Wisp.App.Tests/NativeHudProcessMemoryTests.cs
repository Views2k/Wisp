using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wisp.App;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeHudProcessMemoryTests
{
    private const string SyntheticPath = @"C:\Games\ForzaHorizon6.exe";
    private const ulong ModuleBase = 0x140000000UL;

    [Fact]
    public void StableProcessFingerprintIsHashedOnlyOnce()
    {
        var files = new FakeFingerprintFiles();
        var cache = new NativeHudFingerprintCache(files);

        for (var attempt = 0; attempt < 20; attempt++)
        {
            Assert.True(cache.TryGet(Identity(), out var fingerprint, out var status));
            Assert.Equal(NativeAssistProviderStatus.Ready, status);
            Assert.Equal(NativeHudBuildContract.BuiltIn.ExecutableSha256, fingerprint.Sha256);
        }

        Assert.Equal(1, files.HashCount);
        Assert.Equal(21, files.MetadataReadCount);
        Assert.Equal(1, cache.EntryCount);
    }

    [Fact]
    public async Task ConcurrentFingerprintRequestsShareOneBoundedCacheEntry()
    {
        var files = new FakeFingerprintFiles();
        var cache = new NativeHudFingerprintCache(files);
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(attempt => Task.Run(() =>
            cache.TryGet(Identity(), out _, out _))));

        Assert.All(results, result => Assert.True(result));
        Assert.Equal(1, files.HashCount);
        Assert.Equal(1, cache.EntryCount);
    }

    [Fact]
    public void UnknownBuildsAreAlsoHashedOnlyOnce()
    {
        var files = new FakeFingerprintFiles { Hash = new string('A', 64) };
        var factory = Factory(files, out var cache);

        for (var attempt = 0; attempt < 20; attempt++)
        {
            Assert.False(factory.TrySelectCompatibility(Identity(), out var pack, out _, out var status));
            Assert.Null(pack);
            Assert.Equal(NativeAssistProviderStatus.UnsupportedBuild, status);
        }

        Assert.Equal(1, files.HashCount);
        Assert.Equal(1, cache.EntryCount);
        Assert.Contains("AAAAAAAAAAAA", factory.CompatibilityStatus);
        Assert.DoesNotContain(SyntheticPath, factory.CompatibilityStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CachedUnknownFingerprintCanBeSelectedByAnUpdatedTrustedCatalog()
    {
        var files = new FakeFingerprintFiles { Hash = new string('A', 64) };
        var cache = new NativeHudFingerprintCache(files);
        var oldFactory = new NativeHudProcessMemoryFactory(Catalog(), cache);
        Assert.False(oldFactory.TrySelectCompatibility(Identity(), out _, out _, out _));

        var approvedPack = PackWithHash(files.Hash);
        var updatedCatalog = Catalog(approvedPack);
        var updatedFactory = new NativeHudProcessMemoryFactory(updatedCatalog, cache);
        Assert.True(updatedFactory.TrySelectCompatibility(Identity(), out var selected, out _, out var status));
        Assert.Same(approvedPack, selected);
        Assert.Equal(NativeAssistProviderStatus.Ready, status);
        Assert.Equal(updatedCatalog.Generation, updatedFactory.CompatibilityGeneration);
        Assert.Equal(1, files.HashCount);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("created")]
    [InlineData("modified")]
    [InlineData("volume")]
    [InlineData("file-id")]
    [InlineData("version")]
    public void AnyFileIdentityChangeBlocksThatProcessGenerationUntilRestart(string changed)
    {
        var files = new FakeFingerprintFiles();
        var originalMetadata = files.Metadata;
        var cache = new NativeHudFingerprintCache(files);
        Assert.True(cache.TryGet(Identity(), out _, out _));
        files.Metadata = changed switch
        {
            "length" => files.Metadata with { Length = files.Metadata.Length + 1 },
            "created" => files.Metadata with { CreationTimeUtcTicks = files.Metadata.CreationTimeUtcTicks + 1 },
            "modified" => files.Metadata with { LastWriteTimeUtcTicks = files.Metadata.LastWriteTimeUtcTicks + 1 },
            "volume" => files.Metadata with { VolumeSerialNumber = files.Metadata.VolumeSerialNumber + 1 },
            "file-id" => files.Metadata with { FileIndex = files.Metadata.FileIndex + 1 },
            "version" => files.Metadata with { Version = "6.431.0.0" },
            _ => throw new ArgumentOutOfRangeException(nameof(changed))
        };

        var changedMetadata = files.Metadata;
        Assert.False(cache.TryGet(Identity(), out var fingerprint, out var status));
        Assert.Equal(default, fingerprint);
        Assert.Equal(NativeAssistProviderStatus.UnsupportedBuild, status);
        Assert.Equal(1, files.HashCount);
        Assert.Equal(0, cache.CachedFingerprintCount);

        files.Metadata = originalMetadata;
        Assert.False(cache.TryGet(Identity(), out _, out _));
        Assert.Equal(1, files.HashCount);

        files.Metadata = changedMetadata;
        Assert.True(cache.TryGet(Identity() with { StartTimeUtcTicks = 101 }, out fingerprint, out _));
        Assert.Equal(changedMetadata, fingerprint.Metadata);
        Assert.Equal(2, files.HashCount);
    }

    [Theory]
    [InlineData("pid")]
    [InlineData("start-time")]
    public void NewProcessGenerationCannotReuseAnOldFingerprint(string changed)
    {
        var files = new FakeFingerprintFiles();
        var cache = new NativeHudFingerprintCache(files);
        var original = Identity();
        Assert.True(cache.TryGet(original, out _, out _));
        var next = changed switch
        {
            "pid" => original with { ProcessId = original.ProcessId + 1 },
            "start-time" => original with { StartTimeUtcTicks = original.StartTimeUtcTicks + 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(changed))
        };

        Assert.True(cache.TryGet(next, out _, out _));
        Assert.Equal(2, files.HashCount);
    }

    [Theory]
    [InlineData("path")]
    [InlineData("module-base")]
    [InlineData("image-size")]
    public void ChangedMainModuleIdentityCannotBypassTheProcessLifetimePin(string changed)
    {
        var files = new FakeFingerprintFiles();
        var cache = new NativeHudFingerprintCache(files);
        var original = Identity();
        Assert.True(cache.TryGet(original, out _, out _));
        var changedIdentity = changed switch
        {
            "path" => original with { ExecutablePath = @"C:\Games\Updated\ForzaHorizon6.exe" },
            "module-base" => original with { ModuleBase = original.ModuleBase + 0x10000000UL },
            "image-size" => original with { ImageSize = original.ImageSize + 4096 },
            _ => throw new ArgumentOutOfRangeException(nameof(changed))
        };

        Assert.False(cache.TryGet(changedIdentity, out _, out var status));
        Assert.Equal(NativeAssistProviderStatus.UnsupportedBuild, status);
        Assert.False(cache.TryGet(original, out _, out _));
        Assert.Equal(1, files.HashCount);

        Assert.True(cache.TryGet(changedIdentity with { StartTimeUtcTicks = 101 }, out _, out _));
        Assert.Equal(2, files.HashCount);
    }

    [Fact]
    public void ReplacementExecutableCannotAuthorizeANewPackForTheStillRunningOldImage()
    {
        var files = new FakeFingerprintFiles();
        var factory = Factory(files, out var cache);
        Assert.True(factory.TrySelectCompatibility(Identity(), out _, out _, out _));

        files.Hash = new string('A', 64);
        files.Metadata = files.Metadata with { FileIndex = files.Metadata.FileIndex + 1 };
        var replacementPack = PackWithHash(files.Hash);
        var updatedFactory = new NativeHudProcessMemoryFactory(Catalog(replacementPack), cache);

        Assert.False(updatedFactory.TrySelectCompatibility(Identity(), out var rejectedPack, out _, out var status));
        Assert.Null(rejectedPack);
        Assert.Equal(NativeAssistProviderStatus.UnsupportedBuild, status);
        Assert.Equal(1, files.HashCount);
        Assert.Equal(Identity().ImageSize, replacementPack.ImageSize);

        Assert.True(updatedFactory.TrySelectCompatibility(
            Identity() with { StartTimeUtcTicks = 101 }, out var selectedPack, out _, out _));
        Assert.Same(replacementPack, selectedPack);
        Assert.Equal(2, files.HashCount);
    }

    [Theory]
    [InlineData("created")]
    [InlineData("modified")]
    public void FileTimestampsClearlyAfterProcessStartRequireAProcessRestart(string changed)
    {
        var files = new FakeFingerprintFiles();
        var originalMetadata = files.Metadata;
        var later = Identity().StartTimeUtcTicks + NativeHudFingerprintCache.FileTimestampToleranceTicks + 1;
        files.Metadata = changed == "created"
            ? files.Metadata with { CreationTimeUtcTicks = later }
            : files.Metadata with { LastWriteTimeUtcTicks = later };
        var factory = Factory(files, out var cache);

        Assert.False(factory.TrySelectCompatibility(Identity(), out _, out _, out var status));
        Assert.Equal(NativeAssistProviderStatus.UnsupportedBuild, status);
        Assert.Equal(0, files.HashCount);
        Assert.Equal(0, cache.CachedFingerprintCount);

        files.Metadata = originalMetadata;
        Assert.False(factory.TrySelectCompatibility(Identity(), out _, out _, out _));
        Assert.Equal(0, files.HashCount);
        Assert.True(factory.TrySelectCompatibility(
            Identity() with { StartTimeUtcTicks = later + 1 }, out _, out _, out _));
        Assert.Equal(1, files.HashCount);
    }

    [Theory]
    [InlineData("created")]
    [InlineData("modified")]
    public void FileTimestampGranularityToleranceIsBoundedAndInclusive(string changed)
    {
        var files = new FakeFingerprintFiles();
        var rounded = Identity().StartTimeUtcTicks + NativeHudFingerprintCache.FileTimestampToleranceTicks;
        files.Metadata = changed == "created"
            ? files.Metadata with { CreationTimeUtcTicks = rounded }
            : files.Metadata with { LastWriteTimeUtcTicks = rounded };
        var cache = new NativeHudFingerprintCache(files);

        Assert.True(cache.TryGet(Identity(), out _, out _));
        Assert.Equal(1, files.HashCount);
        Assert.Equal(2 * TimeSpan.TicksPerSecond, NativeHudFingerprintCache.FileTimestampToleranceTicks);
    }

    [Fact]
    public void HashFailureCannotPermitAdoptingAReplacementOnRetry()
    {
        var files = new FakeFingerprintFiles { HashFailure = new IOException("Synthetic interrupted hash") };
        var cache = new NativeHudFingerprintCache(files);
        Assert.False(cache.TryGet(Identity(), out _, out var firstStatus));
        Assert.Equal(NativeAssistProviderStatus.ReadFailure, firstStatus);

        files.HashFailure = null;
        files.Metadata = files.Metadata with { FileIndex = files.Metadata.FileIndex + 1 };
        Assert.False(cache.TryGet(Identity(), out _, out var replacementStatus));
        Assert.Equal(NativeAssistProviderStatus.UnsupportedBuild, replacementStatus);
        Assert.Equal(1, files.HashCount);
        Assert.Equal(0, cache.CachedFingerprintCount);

        Assert.True(cache.TryGet(Identity() with { StartTimeUtcTicks = 101 }, out _, out _));
        Assert.Equal(2, files.HashCount);
    }

    [Fact]
    public void WindowsPathCaseAndLongPathPrefixDoNotCauseRepeatedHashing()
    {
        var files = new FakeFingerprintFiles();
        var cache = new NativeHudFingerprintCache(files);
        Assert.True(cache.TryGet(Identity(), out _, out _));
        Assert.True(cache.TryGet(Identity() with { ExecutablePath = SyntheticPath.ToLowerInvariant() }, out _, out _));
        Assert.True(cache.TryGet(Identity() with { ExecutablePath = @"\\?\" + SyntheticPath }, out _, out _));

        Assert.Equal(1, files.HashCount);
        Assert.All(files.HashedPaths, path => Assert.Equal(SyntheticPath.ToUpperInvariant(), path));
    }

    [Fact]
    public void MetadataChangedDuringHashingBlocksSameProcessRetriesUntilRestart()
    {
        var files = new FakeFingerprintFiles();
        files.DuringHash = () => files.Metadata = files.Metadata with { LastWriteTimeUtcTicks = 99 };
        var cache = new NativeHudFingerprintCache(files);

        Assert.False(cache.TryGet(Identity(), out var rejected, out var status));
        Assert.Equal(default, rejected);
        Assert.Equal(NativeAssistProviderStatus.ReadFailure, status);
        Assert.Equal(1, cache.EntryCount);
        Assert.Equal(0, cache.CachedFingerprintCount);

        files.DuringHash = null;
        Assert.False(cache.TryGet(Identity(), out _, out _));
        Assert.Equal(1, files.HashCount);
        Assert.True(cache.TryGet(Identity() with { StartTimeUtcTicks = 101 }, out var accepted, out _));
        Assert.Equal(99, accepted.Metadata.LastWriteTimeUtcTicks);
        Assert.Equal(2, files.HashCount);
        Assert.Equal(2, cache.EntryCount);
        Assert.Equal(1, cache.CachedFingerprintCount);
    }

    [Fact]
    public void FileReplacementWithUnchangedLengthAndTimestampsIsNotCachedDuringHashing()
    {
        var files = new FakeFingerprintFiles();
        files.DuringHash = () => files.Metadata = files.Metadata with { FileIndex = 777 };
        var cache = new NativeHudFingerprintCache(files);

        Assert.False(cache.TryGet(Identity(), out _, out var status));
        Assert.Equal(NativeAssistProviderStatus.ReadFailure, status);
        Assert.Equal(1, cache.EntryCount);
        Assert.Equal(0, cache.CachedFingerprintCount);
        files.DuringHash = null;
        Assert.False(cache.TryGet(Identity(), out _, out _));
        Assert.Equal(1, files.HashCount);
    }

    [Fact]
    public void AttachmentMetadataIsRecheckedWithoutRehashing()
    {
        var files = new FakeFingerprintFiles();
        var originalMetadata = files.Metadata;
        var cache = new NativeHudFingerprintCache(files);
        Assert.True(cache.TryGet(Identity(), out var fingerprint, out _));
        Assert.True(cache.IsCurrent(Identity(), fingerprint));

        files.Metadata = files.Metadata with { FileIndex = files.Metadata.FileIndex + 1 };
        Assert.False(cache.IsCurrent(Identity(), fingerprint));
        files.Metadata = originalMetadata;
        Assert.False(cache.IsCurrent(Identity(), fingerprint));
        Assert.False(cache.TryGet(Identity(), out _, out _));
        Assert.Equal(1, files.HashCount);
    }

    [Fact]
    public void BoundedCacheEvictionNeverReadoptsAnOlderRunningProcessGeneration()
    {
        var files = new FakeFingerprintFiles();
        var cache = new NativeHudFingerprintCache(files, capacity: 2);
        for (var processId = 1; processId <= 3; processId++)
        {
            Assert.True(cache.TryGet(Identity() with { ProcessId = processId }, out _, out _));
        }

        Assert.Equal(2, cache.EntryCount);
        Assert.Equal(3, files.HashCount);
        Assert.False(cache.TryGet(Identity() with { ProcessId = 1 }, out _, out _));
        Assert.Equal(3, files.HashCount);
        Assert.True(cache.TryGet(Identity() with { ProcessId = 1, StartTimeUtcTicks = 101 }, out _, out _));
        Assert.Equal(4, files.HashCount);
        Assert.Equal(2, cache.EntryCount);
    }

    [Fact]
    public void EvictionCannotClearAStickyReplacementRejection()
    {
        var files = new FakeFingerprintFiles();
        var originalMetadata = files.Metadata;
        var cache = new NativeHudFingerprintCache(files, capacity: 1);
        Assert.True(cache.TryGet(Identity(), out _, out _));
        files.Metadata = files.Metadata with { FileIndex = files.Metadata.FileIndex + 1 };
        Assert.False(cache.TryGet(Identity(), out _, out _));
        Assert.True(cache.TryGet(Identity() with { ProcessId = 1235, StartTimeUtcTicks = 101 }, out _, out _));

        files.Metadata = originalMetadata;
        Assert.False(cache.TryGet(Identity(), out _, out _));
        Assert.Equal(2, files.HashCount);
        Assert.Equal(1, cache.EntryCount);
        Assert.True(cache.TryGet(Identity() with { StartTimeUtcTicks = 102 }, out _, out _));
        Assert.Equal(3, files.HashCount);
        Assert.Equal(1, cache.EntryCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(129)]
    [InlineData(int.MaxValue)]
    public void CacheCapacityCannotBeUnbounded(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeHudFingerprintCache(new FakeFingerprintFiles(), capacity));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(4095L)]
    [InlineData(-1L)]
    [InlineData(2_147_483_649L)]
    [InlineData(long.MaxValue)]
    public void InvalidExecutableLengthsAreRejectedBeforeHashing(long length)
    {
        var files = new FakeFingerprintFiles();
        files.Metadata = files.Metadata with { Length = length };
        var factory = Factory(files, out var cache);

        Assert.False(factory.TrySelectCompatibility(Identity(), out _, out _, out var status));
        Assert.Equal(NativeAssistProviderStatus.UnsupportedBuild, status);
        Assert.Equal(0, files.HashCount);
        Assert.Equal(1, cache.EntryCount);
        Assert.Equal(0, cache.CachedFingerprintCount);
    }

    [Theory]
    [InlineData(4096L)]
    [InlineData(2_147_483_648L)]
    public void ExecutableLengthBoundsAreInclusive(long length)
    {
        var files = new FakeFingerprintFiles();
        files.Metadata = files.Metadata with { Length = length };
        var cache = new NativeHudFingerprintCache(files);

        Assert.True(cache.TryGet(Identity(), out _, out _));
        Assert.Equal(1, files.HashCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("6.430.771")]
    [InlineData("6.430.771.0.extra")]
    [InlineData("6.430.771.65536")]
    [InlineData("6.430.771.-1")]
    [InlineData("6.430.771.0\nprivate-data")]
    [InlineData(" 6.430.771.0 ")]
    public void NonNumericOrUnboundedFileVersionsNeverReachDiagnosticsOrHashing(string version)
    {
        var files = new FakeFingerprintFiles();
        files.Metadata = files.Metadata with { Version = version };
        var factory = Factory(files, out _);

        Assert.False(factory.TrySelectCompatibility(Identity(), out _, out _, out var status));
        Assert.Equal(NativeAssistProviderStatus.UnsupportedBuild, status);
        Assert.Equal(0, files.HashCount);
        Assert.Equal("FH6 executable identity could not be verified or changed during validation", factory.CompatibilityStatus);
    }

    [Theory]
    [InlineData("ForzaHorizon6.exe")]
    [InlineData(@"C:ForzaHorizon6.exe")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\?\GLOBALROOT\Device\Synthetic\ForzaHorizon6.exe")]
    public void RelativeOrDevicePathsAreRejectedWithoutFileAccess(string path)
    {
        var files = new FakeFingerprintFiles();
        var cache = new NativeHudFingerprintCache(files);

        Assert.False(cache.TryGet(Identity() with { ExecutablePath = path }, out _, out _));
        Assert.Equal(0, files.MetadataReadCount);
        Assert.Equal(0, files.HashCount);
    }

    [Theory]
    [InlineData(0, 100L)]
    [InlineData(-1, 100L)]
    [InlineData(1234, 0L)]
    [InlineData(1234, -1L)]
    public void IncompleteProcessGenerationIsRejectedWithoutFileAccess(int processId, long startTime)
    {
        var files = new FakeFingerprintFiles();
        var cache = new NativeHudFingerprintCache(files);

        Assert.False(cache.TryGet(Identity() with { ProcessId = processId, StartTimeUtcTicks = startTime }, out _, out _));
        Assert.Equal(0, files.MetadataReadCount);
        Assert.Equal(0, files.HashCount);
    }

    [Theory]
    [InlineData(0, 'A')]
    [InlineData(63, 'A')]
    [InlineData(65, 'A')]
    [InlineData(64, 'G')]
    [InlineData(64, '\n')]
    public void InvalidComputedHashesAreNeverCached(int length, char character)
    {
        var files = new FakeFingerprintFiles { Hash = new string(character, length) };
        var cache = new NativeHudFingerprintCache(files);

        Assert.False(cache.TryGet(Identity(), out _, out var status));
        Assert.Equal(NativeAssistProviderStatus.ReadFailure, status);
        Assert.Equal(0, cache.CachedFingerprintCount);
    }

    [Fact]
    public void LowercaseHashesAreNormalizedBeforeExactCatalogSelection()
    {
        var files = new FakeFingerprintFiles { Hash = NativeHudBuildContract.BuiltIn.ExecutableSha256.ToLowerInvariant() };
        var factory = Factory(files, out _);

        Assert.True(factory.TrySelectCompatibility(Identity(), out var pack, out var fingerprint, out _));
        Assert.Same(NativeHudBuildContract.BuiltIn, pack);
        Assert.Equal(NativeHudBuildContract.BuiltIn.ExecutableSha256, fingerprint.Sha256);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("hash")]
    public void FileReadFailuresFailClosedWithoutCaching(string stage)
    {
        var files = new FakeFingerprintFiles();
        if (stage == "read")
        {
            files.MetadataFailure = new IOException("Synthetic read failure");
        }
        else
        {
            files.HashFailure = new CryptographicException("Synthetic hash failure");
        }

        var factory = Factory(files, out var cache);
        Assert.False(factory.TrySelectCompatibility(Identity(), out _, out _, out var status));
        Assert.Equal(NativeAssistProviderStatus.ReadFailure, status);
        Assert.Equal(0, cache.CachedFingerprintCount);
        Assert.DoesNotContain("Synthetic", factory.CompatibilityStatus, StringComparison.Ordinal);

        files.MetadataFailure = null;
        files.HashFailure = null;
        Assert.True(factory.TrySelectCompatibility(Identity(), out _, out _, out _));
    }

    [Fact]
    public void DeniedFileReadsAreReportedWithoutExceptionDetails()
    {
        var files = new FakeFingerprintFiles { MetadataFailure = new UnauthorizedAccessException("Synthetic private path") };
        var factory = Factory(files, out _);

        Assert.False(factory.TrySelectCompatibility(Identity(), out _, out _, out var status));
        Assert.Equal(NativeAssistProviderStatus.AccessDenied, status);
        Assert.DoesNotContain("Synthetic", factory.CompatibilityStatus, StringComparison.Ordinal);
        Assert.True(NativeHudFingerprintCache.IsExpectedFailure(new Win32Exception(5)));
    }

    [Fact]
    public void ExactBuildSelectionUsesVersionLengthHashAndActualModuleSize()
    {
        var files = new FakeFingerprintFiles();
        var factory = Factory(files, out _);

        Assert.True(factory.TrySelectCompatibility(Identity(), out var pack, out _, out var status));
        Assert.Same(NativeHudBuildContract.BuiltIn, pack);
        Assert.Equal(NativeAssistProviderStatus.Ready, status);
        Assert.Contains(pack!.GameVersion, factory.CompatibilityStatus);
        Assert.Contains(pack.ExecutableSha256[..12], factory.CompatibilityStatus);
        Assert.Contains(pack.Id, factory.CompatibilityStatus);
        Assert.DoesNotContain(SyntheticPath, factory.CompatibilityStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("length")]
    [InlineData("hash")]
    [InlineData("image-size")]
    public void AnyExactBuildMismatchPreventsAttachment(string changed)
    {
        var files = new FakeFingerprintFiles();
        var identity = Identity();
        switch (changed)
        {
            case "version":
                files.Metadata = files.Metadata with { Version = "6.431.0.0" };
                break;
            case "length":
                files.Metadata = files.Metadata with { Length = files.Metadata.Length + 1 };
                break;
            case "hash":
                files.Hash = new string('A', 64);
                break;
            case "image-size":
                identity = identity with { ImageSize = identity.ImageSize + 4096 };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(changed));
        }

        var factory = Factory(files, out _);
        Assert.False(factory.TrySelectCompatibility(identity, out var pack, out _, out var status));
        Assert.Null(pack);
        Assert.Equal(NativeAssistProviderStatus.UnsupportedBuild, status);
    }

    [Theory]
    [InlineData(0UL, 188293120U)]
    [InlineData(0xFFFFUL, 188293120U)]
    [InlineData(ModuleBase + 1, 188293120U)]
    [InlineData(0x0000800000000000UL, 188293120U)]
    [InlineData(0x00007FFFFFFFF000UL, 188293120U)]
    [InlineData(ulong.MaxValue - 7, 188293120U)]
    [InlineData(ModuleBase, 0U)]
    [InlineData(ModuleBase, 4095U)]
    [InlineData(ModuleBase, 1_073_741_825U)]
    public void InvalidActualModuleRangesFailBeforeHashing(ulong moduleBase, uint imageSize)
    {
        var files = new FakeFingerprintFiles();
        var factory = Factory(files, out _);

        Assert.False(factory.TrySelectCompatibility(
            Identity() with { ModuleBase = moduleBase, ImageSize = imageSize }, out _, out _, out var status));
        Assert.Equal(NativeAssistProviderStatus.UnsupportedBuild, status);
        Assert.Equal(0, files.MetadataReadCount);
        Assert.Equal(0, files.HashCount);
    }

    [Theory]
    [InlineData(0x10000UL, 1UL, true)]
    [InlineData(0xFFFFUL, 1UL, false)]
    [InlineData(ModuleBase, 0UL, false)]
    [InlineData(0x00007FFFFFFFFFFFUL, 1UL, true)]
    [InlineData(0x00007FFFFFFFFFFFUL, 2UL, false)]
    [InlineData(0x00007FFFFFFFFFF8UL, 8UL, true)]
    [InlineData(0x00007FFFFFFFFFFCUL, 8UL, false)]
    [InlineData(0x0000800000000000UL, 1UL, false)]
    [InlineData(ulong.MaxValue, 8UL, false)]
    [InlineData(ModuleBase, ulong.MaxValue, false)]
    public void ReadBoundsCoverTheEntireReadWidthWithoutOverflow(ulong address, ulong width, bool valid)
    {
        Assert.Equal(valid, NativeHudProcessMemory.IsValidReadSpan(address, width));
    }

    [Fact]
    public void ProcessRightsRemainReadOnlyAndLimitedQuery()
    {
        Assert.Equal(0x1010U, NativeHudProcessMemory.RequiredProcessAccess);
        Assert.Equal(0U, NativeHudProcessMemory.RequiredProcessAccess & 0x0008U);
        Assert.Equal(0U, NativeHudProcessMemory.RequiredProcessAccess & 0x0020U);
        Assert.Equal(0U, NativeHudProcessMemory.RequiredProcessAccess & 0x0400U);
    }

    private static NativeHudProcessIdentity Identity() =>
        new(1234, 100, SyntheticPath, ModuleBase, NativeHudBuildContract.BuiltIn.ImageSize);

    private static NativeCompatibilityCatalog Catalog(NativeHudCompatibilityPack? builtIn = null) =>
        new(builtIn ?? NativeHudBuildContract.BuiltIn, null, new Dictionary<string, byte[]>());

    private static NativeHudProcessMemoryFactory Factory(FakeFingerprintFiles files, out NativeHudFingerprintCache cache)
    {
        cache = new NativeHudFingerprintCache(files);
        return new NativeHudProcessMemoryFactory(Catalog(), cache);
    }

    private static NativeHudCompatibilityPack PackWithHash(string hash)
    {
        using var stream = typeof(NativeHudBuildContract).Assembly.GetManifestResourceStream(
            "Wisp.NativeCompatibility.BuiltIn.json")!;
        var document = JsonNode.Parse(stream)!.AsObject();
        document["id"] = "fh6-synthetic-approved-build";
        document["executableSha256"] = hash;
        return NativeHudCompatibilityPack.Parse(JsonSerializer.SerializeToUtf8Bytes(document));
    }

    private sealed class FakeFingerprintFiles : INativeHudFingerprintFileSystem
    {
        public NativeHudFileMetadata Metadata { get; set; } = new(
            NativeHudBuildContract.BuiltIn.ExecutableLength, 1, 2, 3, 4, NativeHudBuildContract.BuiltIn.GameVersion);
        public string Hash { get; set; } = NativeHudBuildContract.BuiltIn.ExecutableSha256;
        public int MetadataReadCount { get; private set; }
        public int HashCount { get; private set; }
        public List<string> HashedPaths { get; } = [];
        public Action? DuringHash { get; set; }
        public Exception? MetadataFailure { get; set; }
        public Exception? HashFailure { get; set; }

        public NativeHudFileMetadata ReadMetadata(string path)
        {
            MetadataReadCount++;
            if (MetadataFailure is not null)
            {
                throw MetadataFailure;
            }

            return Metadata;
        }

        public string ComputeSha256(string path)
        {
            HashCount++;
            HashedPaths.Add(path);
            if (HashFailure is not null)
            {
                throw HashFailure;
            }

            DuringHash?.Invoke();
            return Hash;
        }
    }
}
