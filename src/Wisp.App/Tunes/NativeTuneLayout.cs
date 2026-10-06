using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using Wisp.Core.Tunes;

namespace Wisp.App.Tunes;

// Called after exact-catalog or runtime structural admission. Normal tune reads
// recheck the exact admitted code ranges; discovery never runs on this path.
internal sealed partial class NativeTuneLayout
{
    private readonly IReadOnlyDictionary<ulong, ulong> _rvas;
    private readonly CodeGuard[] _guards;
    private readonly IReadOnlyDictionary<ulong, ulong> _providerSlots;
    private readonly string? _packFingerprint;

    private NativeTuneLayout(TunePlatform platform, string id, string digest,
        IReadOnlyDictionary<ulong, ulong> rvas, CodeGuard[] guards, IReadOnlyDictionary<ulong, ulong> providerSlots,
        NativeTuneCompatibilityLayout? descriptor = null, string? packFingerprint = null)
    {
        Platform = platform;
        Id = id;
        Digest = digest;
        _rvas = rvas;
        _guards = guards;
        _providerSlots = providerSlots;
        Descriptor = descriptor;
        _packFingerprint = packFingerprint;
    }

    internal TunePlatform Platform { get; }
    internal string Id { get; }
    internal string Digest { get; }
    internal NativeTuneCompatibilityLayout? Descriptor { get; }
    internal ulong Rva(ulong steamRva) => _rvas.TryGetValue(steamRva, out var rva)
        ? rva : throw new InvalidDataException("The tune layout is incomplete.");

    internal static NativeTuneLayout Resolve(INativeHudProcessMemory memory, CancellationToken cancellationToken)
    {
        var pack = memory.CompatibilityPack;
        if (pack.Tune is { } descriptor)
        {
            if (descriptor.ProfileId != TuneVerificationProfiles.DescriptorProfile ||
                descriptor.SemanticsVersion != TuneVerificationProfiles.DescriptorSemanticsVersion)
                throw new TuneLayoutException(TuneLayoutFailure.DescriptorProfile);
            var described = new NativeTuneLayout(pack.StoreIdentity is null ? TunePlatform.Steam : TunePlatform.MicrosoftStore,
                descriptor.ProfileId, descriptor.Fingerprint, descriptor.Rvas,
                descriptor.CodeGuards.Select(guard => new CodeGuard(guard.Rva, guard.Length, guard.Sha256)).ToArray(),
                descriptor.ProviderSlots, descriptor, pack.Fingerprint);
            described.Verify(memory, cancellationToken);
            return described;
        }
        var layout = pack.StoreIdentity is null ? Steam : Store;
        // Legacy packs have no Tune declaration, so only the original exact
        // identities may use the built-in fallback addresses and expectations.
        if (!NativeTuneCapture.Supports(pack))
            throw new TuneLayoutException(TuneLayoutFailure.UnsupportedIdentity);
        layout.Verify(memory, cancellationToken);
        return layout;
    }

    internal TuneVerification Provenance(NativeHudCompatibilityPack pack) => new(Platform, Id, Digest,
        pack.Id, pack.Revision, pack.StoreIdentity?.PackageFullName)
    {
        Method = pack.IsRuntimeValidated ? TuneVerificationMethod.RuntimeValidatedLayout :
            Descriptor is null ? TuneVerificationMethod.KnownProfile : TuneVerificationMethod.AuthenticatedCompatibilityDescriptor,
        SemanticsVersion = Descriptor?.SemanticsVersion ?? 0,
        CompatibilityPackSha256 = _packFingerprint
    };

    internal void Verify(INativeHudProcessMemory memory, CancellationToken cancellationToken)
    {
        if ((memory.CompatibilityPack.StoreIdentity is null) != (Platform == TunePlatform.Steam))
            throw new TuneLayoutException(TuneLayoutFailure.PlatformMismatch);
        if (Descriptor is not null && (_packFingerprint != memory.CompatibilityPack.Fingerprint ||
            Descriptor.Fingerprint != memory.CompatibilityPack.Tune?.Fingerprint))
            throw new TuneLayoutException(TuneLayoutFailure.DescriptorChanged);
        VerifyGuards(memory, _guards, cancellationToken);
        var read = new NativeTuneRead(memory, cancellationToken, 1024);
        foreach (var (slot, target) in _providerSlots)
        {
            var table = memory.CompatibilityPack.LeadVtableRva;
            var size = memory.CompatibilityPack.ImageSize;
            if (table >= size || slot > size - table || 8 > size - table - slot || target < 4096 || target >= size)
                throw new TuneLayoutException(TuneLayoutFailure.ProviderSlotBounds);
            if (read.UInt64(memory.ModuleBase + memory.CompatibilityPack.LeadVtableRva + slot) != memory.ModuleBase + target)
                throw new TuneLayoutException(TuneLayoutFailure.ProviderSlotMismatch);
        }
    }

    // Exact whole-range hashes include member offsets and relative references.
    // No pattern scanning or heuristic fallback is performed during normal reads.
    internal static void VerifyGuards(INativeHudProcessMemory memory, IReadOnlyList<CodeGuard> guards,
        CancellationToken cancellationToken)
    {
        var imageSize = memory.CompatibilityPack.ImageSize;
        if (!NativeHudProcessMemory.IsValidModuleRange(memory.ModuleBase, imageSize) ||
            guards.Count is < 1 or > 96 || guards.Sum(guard => (long)guard.Length) > 48 * 1024)
            throw new TuneLayoutException(TuneLayoutFailure.GuardManifest);
        var read = new NativeTuneRead(memory, cancellationToken, 64 * 1024);
        var header = read.Bytes(memory.ModuleBase, 4096, remember: false);
        var pe = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0x3C));
        if (BinaryPrimitives.ReadUInt16LittleEndian(header) != 0x5A4D || pe is < 64 or > 2048 ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(pe)) != 0x4550 ||
            BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(pe + 4)) != 0x8664 ||
            BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(pe + 24)) != 0x20B ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(pe + 80)) != imageSize)
            throw new TuneLayoutException(TuneLayoutFailure.ImageHeader);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(pe + 6));
        var optional = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(pe + 20));
        var start = pe + 24 + optional;
        if (count is < 1 or > 32 || optional < 112 || start + count * 40 > header.Length)
            throw new TuneLayoutException(TuneLayoutFailure.ImageSections);
        foreach (var guard in guards)
        {
            if (guard.Length is < 1 or > 8192 || guard.Rva < 4096 || guard.Rva >= imageSize ||
                (ulong)guard.Length > imageSize - guard.Rva)
                throw new TuneLayoutException(TuneLayoutFailure.CodeGuardBounds);
            var matches = 0;
            for (var index = 0; index < count; index++)
            {
                var section = header.AsSpan(start + index * 40);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(section[8..]);
                var rva = BinaryPrimitives.ReadUInt32LittleEndian(section[12..]);
                var flags = BinaryPrimitives.ReadUInt32LittleEndian(section[36..]);
                if (rva >= 4096 && rva < imageSize && size <= imageSize - rva &&
                    guard.Rva >= rva && guard.Rva - rva <= size && (ulong)guard.Length <= size - (guard.Rva - rva) &&
                    (flags & 0x20000000) != 0 && (flags & 0x80000000) == 0) matches++;
            }
            if (matches != 1) throw new TuneLayoutException(TuneLayoutFailure.CodeGuardSection);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (var offset = 0; offset < guard.Length; offset += 4096)
                hash.AppendData(read.Bytes(memory.ModuleBase + guard.Rva + (ulong)offset,
                    Math.Min(4096, guard.Length - offset), remember: false));
            if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), guard.Sha256, StringComparison.Ordinal))
                throw new TuneLayoutException(TuneLayoutFailure.CodeGuardHash);
        }
    }

    internal sealed record CodeGuard(ulong Rva, int Length, string Sha256);
}

internal enum TuneLayoutFailure
{
    Unspecified, DescriptorProfile, UnsupportedIdentity, PlatformMismatch, DescriptorChanged,
    ProviderSlotBounds, ProviderSlotMismatch, GuardManifest, ImageHeader, ImageSections,
    CodeGuardBounds, CodeGuardSection, CodeGuardHash
}

internal sealed class TuneLayoutException(TuneLayoutFailure failure = TuneLayoutFailure.Unspecified)
    : IOException(TuneCaptureService.LayoutUnavailableMessage)
{
    internal TuneLayoutFailure Failure { get; } = failure;
}
