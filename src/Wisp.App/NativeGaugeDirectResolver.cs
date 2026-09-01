using System.Buffers.Binary;
using System.Diagnostics;
using Wisp.Core;

namespace Wisp.App;

public enum NativeGaugeDirectState
{
    Unavailable,
    Resolved,
    Cached
}

public readonly record struct NativeGaugeDirectResult
{
    internal NativeGaugeDirectResult(
        NativeGaugeDirectState state,
        uint mode,
        bool isElectric,
        bool hasNeedlePair,
        bool hasHeadlightState,
        bool areHeadlightsOn,
        float needleAngleDegrees,
        float needleBlurAmount,
        float powerFillAmount,
        float regenFillAmount,
        float regenPowerRatio,
        float tachometerMaximum,
        float electricMaximumSpeed,
        NativeElectricGearState electricGearState,
        NativeDisplayedSpeedState displayedSpeedState,
        long observedTimestamp)
    {
        State = state;
        Mode = mode;
        IsElectric = isElectric;
        HasNeedlePair = hasNeedlePair;
        HasHeadlightState = hasHeadlightState;
        AreHeadlightsOn = areHeadlightsOn;
        NeedleAngleDegrees = needleAngleDegrees;
        NeedleBlurAmount = needleBlurAmount;
        PowerFillAmount = powerFillAmount;
        RegenFillAmount = regenFillAmount;
        RegenPowerRatio = regenPowerRatio;
        TachometerMaximum = tachometerMaximum;
        ElectricMaximumSpeed = electricMaximumSpeed;
        ElectricGearState = electricGearState;
        DisplayedSpeedState = displayedSpeedState;
        ObservedTimestamp = observedTimestamp;
    }

    public NativeGaugeDirectState State { get; }
    public bool IsAvailable => State != NativeGaugeDirectState.Unavailable;
    public uint Mode { get; }
    public bool IsElectric { get; }
    public bool HasNeedlePair { get; }
    public bool HasHeadlightState { get; }
    public bool AreHeadlightsOn { get; }
    public float NeedleAngleDegrees { get; }
    public float NeedleBlurAmount { get; }
    public float PowerFillAmount { get; }
    public float RegenFillAmount { get; }
    public float RegenPowerRatio { get; }
    public float TachometerMaximum { get; }
    public float ElectricMaximumSpeed { get; }
    public NativeElectricGearState ElectricGearState { get; }
    public NativeDisplayedSpeedState DisplayedSpeedState { get; }
    public long ObservedTimestamp { get; }
}

/// <summary>
/// Resolves the native HUD gauge through the bounded global/hud registry path.
/// It never scans process memory. Volatile gauge fields are captured atomically
/// between two validations of the stable ownership chain.
/// </summary>
public sealed class NativeGaugeDirectResolver
{
    private const ulong MaximumRegistryBucketCount = 65_536;
    private const ulong MaximumRegistryEntryCount = 65_536;
    private const int MaximumRegistryCollisionHops = 64;
    private const ulong MaximumUserAddress = 0x00007FFFFFFFFFFF;

    private readonly NativeHudCompatibilityPack _pack;
    private readonly NativeGaugeLayout? _layout;
    private readonly byte[] _slotZeroPrologue;
    private readonly byte[] _structuralBlock = new byte[NativeHudCompatibilityPack.MaximumFieldBytes];
    private readonly ulong _gaugeBlockStartOffset;
    private readonly byte[] _gaugeBlock;
    private readonly ulong _childlessElectricBlockStartOffset;
    private readonly byte[] _childlessElectricBlock = [];
    private CacheEntry? _cache;

    public NativeGaugeDirectResolver(NativeHudCompatibilityPack? pack = null)
    {
        _pack = pack ?? NativeHudBuildContract.BuiltIn;
        _layout = _pack.NativeGauge;
        _slotZeroPrologue = _layout is null
            ? []
            : Convert.FromHexString(_layout.HudSubobjectSlotZeroPrologueHex);
        if (_layout is null)
        {
            _gaugeBlock = [];
            return;
        }

        _gaugeBlockStartOffset = new[]
        {
            _layout.ChildModeOffset,
            _layout.ChildAngleOffset,
            _layout.ChildBlurOffset,
            _layout.ChildSpeedDigitOneOffset,
            _layout.ChildSpeedDigitTenOffset,
            _layout.ChildSpeedDigitHundredOffset,
            _layout.ChildSpeedLessOrEqualOneOffset,
            _layout.ChildSpeedLessTenOffset,
            _layout.ChildSpeedLessHundredOffset,
            _layout.ChildSpeedUnitObjectOffset,
            _layout.ChildHeadlightsOnOffset,
            _layout.ChildPowerOffset,
            _layout.ChildRegenOffset,
            _layout.ChildRatioOffset,
            _layout.ChildGearOffset,
            _layout.ChildGearNextOffset,
            _layout.ChildGearPreviousOffset,
            _layout.ChildGearGaugeStateOffset,
            _layout.ChildUseDriveFor1Offset,
            _layout.ChildMaximumTachometerOffset,
            _layout.ChildElectricMaximumSpeedOffset
        }.Min();
        var blockEnd = new[]
        {
            _layout.ChildModeOffset,
            _layout.ChildAngleOffset,
            _layout.ChildBlurOffset,
            _layout.ChildSpeedDigitOneOffset,
            _layout.ChildSpeedDigitTenOffset,
            _layout.ChildSpeedDigitHundredOffset,
            _layout.ChildSpeedLessOrEqualOneOffset,
            _layout.ChildSpeedLessTenOffset,
            _layout.ChildSpeedLessHundredOffset,
            _layout.ChildSpeedUnitObjectOffset,
            _layout.ChildHeadlightsOnOffset,
            _layout.ChildPowerOffset,
            _layout.ChildRegenOffset,
            _layout.ChildRatioOffset,
            _layout.ChildGearOffset,
            _layout.ChildGearNextOffset,
            _layout.ChildGearPreviousOffset,
            _layout.ChildGearGaugeStateOffset,
            _layout.ChildUseDriveFor1Offset,
            _layout.ChildMaximumTachometerOffset,
            _layout.ChildElectricMaximumSpeedOffset
        }.Max() + sizeof(uint);
        _gaugeBlock = new byte[checked((int)(blockEnd - _gaugeBlockStartOffset))];

        _childlessElectricBlockStartOffset = Math.Min(
            _layout.OuterPowerFillOffset,
            _layout.OuterRegenFillOffset);
        var childlessBlockEnd = Math.Max(
            _layout.OuterPowerFillOffset,
            _layout.OuterRegenFillOffset) + sizeof(uint);
        _childlessElectricBlock = new byte[checked((int)(childlessBlockEnd - _childlessElectricBlockStartOffset))];
    }

    public NativeGaugeDirectResult Read(
        IReadOnlyProcessMemory memory,
        ulong moduleBase,
        ulong expectedSource,
        bool expectedElectric,
        bool forceStructuralValidation = true)
    {
        ArgumentNullException.ThrowIfNull(memory);
        if (_layout is null ||
            !IsObjectPointer(moduleBase) ||
            !IsAddressRange(moduleBase, _pack.ImageSize) ||
            !IsObjectPointer(expectedSource))
        {
            Reset();
            return Unavailable();
        }

        if (_cache is { } cached &&
            cached.ModuleBase == moduleBase &&
            cached.ExpectedSource == expectedSource &&
            cached.ExpectedElectric == expectedElectric)
        {
            GaugeRead cachedRead = default;
            bool valid;
            if (forceStructuralValidation)
            {
                valid = TryValidateCached(memory, cached) &&
                        TryReadChainGauge(memory, cached.Chain, expectedElectric, out cachedRead) &&
                        TryValidateCached(memory, cached);
            }
            else if (cached.Chain.Child == 0)
            {
                valid = TryValidateCachedChildless(memory, cached) &&
                        TryReadChainGauge(memory, cached.Chain, expectedElectric, out cachedRead) &&
                        TryValidateCachedChildless(memory, cached);
            }
            else
            {
                valid = HasVtable(memory, cached.Chain.Child, moduleBase + _layout.ChildVtableRva) &&
                        TryReadGauge(memory, cached.Chain.Child, expectedElectric, out cachedRead) &&
                        HasVtable(memory, cached.Chain.Child, moduleBase + _layout.ChildVtableRva);
            }
            if (valid)
            {
                return cachedRead.ToResult(NativeGaugeDirectState.Cached);
            }

            _cache = null;
        }
        else
        {
            _cache = null;
        }

        if (!TryResolveChain(memory, moduleBase, expectedSource, expectedElectric, out var firstChain) ||
            !TryReadChainGauge(memory, firstChain, expectedElectric, out var read) ||
            !TryResolveChain(memory, moduleBase, expectedSource, expectedElectric, out var secondChain) ||
            firstChain != secondChain)
        {
            return Unavailable();
        }

        _cache = new CacheEntry(moduleBase, expectedSource, expectedElectric, firstChain);
        return read.ToResult(NativeGaugeDirectState.Resolved);
    }

    public void Reset() => _cache = null;

    private bool TryResolveChain(
        IReadOnlyProcessMemory memory,
        ulong moduleBase,
        ulong expectedSource,
        bool expectedElectric,
        out ChainSnapshot chain)
    {
        chain = default;
        if (!TryResolveRegistry(memory, moduleBase, out var registry) ||
            !TryValidateHud(memory, moduleBase, registry.Hud, registry.HudControl, out var subobject) ||
            !TryReadVector(memory, subobject, out var vector) ||
            !TryFindTypeEntry(memory, moduleBase, vector, out var typeEntry) ||
            !TryReadUniqueInstance(memory, typeEntry, out var instances, out var outer, out var outerControl) ||
            !TryValidateOuter(
                memory,
                moduleBase,
                registry.Hud,
                registry.HudControl,
                outer,
                outerControl,
                expectedSource,
                out var child))
        {
            return false;
        }

        ulong provider = 0;
        if (child == 0 &&
            (!expectedElectric ||
             !TryResolveChildlessProvider(memory, moduleBase, expectedSource, out provider)))
        {
            return false;
        }

        chain = new ChainSnapshot(
            registry,
            subobject,
            vector,
            typeEntry,
            instances,
            outer,
            outerControl,
            child,
            provider);
        return true;
    }

    private bool TryReadChainGauge(
        IReadOnlyProcessMemory memory,
        ChainSnapshot chain,
        bool expectedElectric,
        out GaugeRead read)
    {
        read = default;
        if (chain.Child != 0)
        {
            return TryReadGauge(memory, chain.Child, expectedElectric, out read);
        }

        return expectedElectric && TryReadChildlessElectricGauge(memory, chain.Provider, out read);
    }

    private bool TryValidateCachedChildless(
        IReadOnlyProcessMemory memory,
        CacheEntry cached)
    {
        var layout = _layout!;
        return cached.ExpectedElectric &&
               cached.Chain.Child == 0 &&
               cached.Chain.Provider != 0 &&
               TryReadField(memory, cached.ExpectedSource, _pack.Fields.SourceProvider, out var provider) &&
               provider == cached.Chain.Provider &&
               HasVtable(memory, provider, cached.ModuleBase + _pack.LeadVtableRva) &&
               TryReadField(memory, cached.Chain.Outer, layout.OuterChildOffset, out var child) &&
               child == 0;
    }

    private bool TryValidateCached(
        IReadOnlyProcessMemory memory,
        CacheEntry cached)
    {
        var expected = cached.Chain;
        var registry = expected.Registry;
        var layout = _layout!;
        if (!TryImageAddress(cached.ModuleBase, layout.RegistryGlobalRva, 8, out var global) ||
            !memory.TryReadUInt64(global, out var wrapper) ||
            wrapper != registry.Wrapper)
        {
            return false;
        }

        var wrapperEnd = Math.Max(
            8UL,
            Math.Max(layout.RegistryContextOffset + 8, layout.RegistryContextControlOffset + 8));
        if (!TryReadStructuralBlock(memory, registry.Wrapper, wrapperEnd) ||
            !StructuralUInt64(0, out var wrapperVtable) ||
            wrapperVtable != cached.ModuleBase + layout.RegistryWrapperVtableRva ||
            !StructuralUInt64(layout.RegistryContextOffset, out var context) ||
            context != registry.Context ||
            !StructuralUInt64(layout.RegistryContextControlOffset, out var contextControl) ||
            contextControl != registry.ContextControl)
        {
            return false;
        }

        if (!HasInlineObject(registry.ContextControl, layout.SharedControlObjectOffset, registry.Context) ||
            !TryReadStructuralBlock(memory, registry.ContextControl, 8) ||
            !StructuralUInt64(0, out var contextControlVtable) ||
            contextControlVtable != cached.ModuleBase + layout.RegistryContextControlVtableRva)
        {
            return false;
        }

        var contextEnd = Math.Max(
            8UL,
            Math.Max(
                layout.RegistryBucketCountOffset + 8,
                Math.Max(
                    layout.RegistryBucketsCapacityOffset + 8,
                    Math.Max(layout.RegistrySentinelOffset + 8, layout.RegistryCountOffset + 8))));
        if (!TryReadStructuralBlock(memory, registry.Context, contextEnd) ||
            !StructuralUInt64(0, out var contextVtable) ||
            contextVtable != cached.ModuleBase + layout.RegistryContextVtableRva ||
            !StructuralUInt64(layout.RegistrySentinelOffset, out var sentinel) ||
            sentinel != registry.Sentinel ||
            !StructuralUInt64(layout.RegistryCountOffset, out var entryCount) ||
            entryCount is 0 or > MaximumRegistryEntryCount ||
            entryCount > registry.BucketCount ||
            !StructuralUInt64(layout.RegistryBucketsOffset, out var buckets) ||
            buckets != registry.Buckets ||
            !StructuralUInt64(layout.RegistryBucketsEndOffset, out var bucketsEnd) ||
            bucketsEnd != registry.BucketsEnd ||
            !StructuralUInt64(layout.RegistryBucketsCapacityOffset, out var bucketsCapacity) ||
            bucketsCapacity != registry.BucketsCapacity ||
            !StructuralUInt64(layout.RegistryMaskOffset, out var mask) ||
            mask != registry.Mask ||
            !StructuralUInt64(layout.RegistryBucketCountOffset, out var bucketCount) ||
            bucketCount != registry.BucketCount)
        {
            return false;
        }

        if (!TryReadStructuralBlock(memory, registry.Bucket, layout.RegistryBucketStride) ||
            !StructuralUInt64(layout.RegistryBucketBoundaryOffset, out var boundary) ||
            boundary != registry.Boundary ||
            !StructuralUInt64(layout.RegistryBucketNodeOffset, out var node) ||
            node != registry.Node)
        {
            return false;
        }

        var nodeEnd = Math.Max(
            layout.RegistryNodeHashOffset + 8,
            Math.Max(layout.RegistryNodeObjectOffset + 8, layout.RegistryNodeControlOffset + 8));
        if (!TryReadStructuralBlock(memory, registry.Node, nodeEnd) ||
            !StructuralUInt64(layout.RegistryNodeHashOffset, out var hash) ||
            hash != layout.RegistryKeyHash ||
            !StructuralUInt64(layout.RegistryNodeObjectOffset, out var hud) ||
            hud != registry.Hud ||
            !StructuralUInt64(layout.RegistryNodeControlOffset, out var hudControl) ||
            hudControl != registry.HudControl)
        {
            return false;
        }

        var hudEnd = Math.Max(
            layout.HudSubobjectPointerOffset + 8,
            Math.Max(8UL, layout.HudSubobjectOffset + 8));
        if (!TryReadStructuralBlock(memory, registry.Hud, hudEnd) ||
            !StructuralUInt64(0, out var hudVtable) ||
            hudVtable != cached.ModuleBase + layout.HudVtableRva ||
            !StructuralUInt64(layout.HudSubobjectPointerOffset, out var subobject) ||
            subobject != expected.Subobject ||
            !StructuralUInt64(layout.HudSubobjectOffset, out var subobjectVtable) ||
            subobjectVtable != cached.ModuleBase + layout.HudSubobjectVtableRva)
        {
            return false;
        }

        if (!HasInlineObject(registry.HudControl, layout.SharedControlObjectOffset, registry.Hud) ||
            !TryReadStructuralBlock(memory, registry.HudControl, 8) ||
            !StructuralUInt64(0, out var hudControlVtable) ||
            hudControlVtable != cached.ModuleBase + layout.HudControlVtableRva ||
            !memory.TryReadUInt64(cached.ModuleBase + layout.HudSubobjectVtableRva, out var slotZero) ||
            slotZero != cached.ModuleBase + layout.HudSubobjectSlotZeroTargetRva ||
            !memory.TryReadBytes(slotZero, _structuralBlock.AsSpan(0, _slotZeroPrologue.Length)) ||
            !_structuralBlock.AsSpan(0, _slotZeroPrologue.Length).SequenceEqual(_slotZeroPrologue))
        {
            return false;
        }

        var vectorAddress = expected.Vector.Address;
        var vectorEnd = Math.Max(
            layout.HudTypeVectorBeginOffset + 8,
            Math.Max(layout.HudTypeVectorEndOffset + 8, layout.HudTypeVectorCapacityOffset + 8));
        if (!TryReadStructuralBlock(memory, vectorAddress, vectorEnd) ||
            !StructuralUInt64(layout.HudTypeVectorBeginOffset, out var vectorBegin) ||
            vectorBegin != expected.Vector.Begin ||
            !StructuralUInt64(layout.HudTypeVectorEndOffset, out var liveVectorEnd) ||
            liveVectorEnd != expected.Vector.End ||
            !StructuralUInt64(layout.HudTypeVectorCapacityOffset, out var vectorCapacity) ||
            vectorCapacity != expected.Vector.Capacity)
        {
            return false;
        }

        var typeEnd = Math.Max(
            layout.HudTypeTokenOffset + 8,
            Math.Max(
                layout.HudTypeInstancesBeginOffset + 8,
                Math.Max(layout.HudTypeInstancesEndOffset + 8, layout.HudTypeInstancesCapacityOffset + 8)));
        if (!TryReadStructuralBlock(memory, expected.TypeEntry, typeEnd) ||
            !StructuralUInt64(layout.HudTypeTokenOffset, out var token) ||
            token != cached.ModuleBase + layout.HudTypeTokenRva ||
            !StructuralUInt64(layout.HudTypeInstancesBeginOffset, out var instancesBegin) ||
            instancesBegin != expected.Instances.Begin ||
            !StructuralUInt64(layout.HudTypeInstancesEndOffset, out var instancesEnd) ||
            instancesEnd != expected.Instances.End ||
            !StructuralUInt64(layout.HudTypeInstancesCapacityOffset, out var instancesCapacity) ||
            instancesCapacity != expected.Instances.Capacity)
        {
            return false;
        }

        var instanceEnd = Math.Max(
            layout.HudTypeInstanceObjectOffset + 8,
            layout.HudTypeInstanceControlOffset + 8);
        if (!TryReadStructuralBlock(memory, expected.Instances.Begin, instanceEnd) ||
            !StructuralUInt64(layout.HudTypeInstanceObjectOffset, out var outer) ||
            outer != expected.Outer ||
            !StructuralUInt64(layout.HudTypeInstanceControlOffset, out var outerControl) ||
            outerControl != expected.OuterControl)
        {
            return false;
        }

        var outerControlEnd = Math.Max(8UL, layout.SharedControlObjectOffset + 8);
        if (!TryReadStructuralBlock(memory, expected.OuterControl, outerControlEnd) ||
            !StructuralUInt64(0, out var outerControlVtable) ||
            outerControlVtable != cached.ModuleBase + layout.OuterControlVtableRva ||
            !StructuralUInt64(layout.SharedControlObjectOffset, out var controlledOuter) ||
            controlledOuter != expected.Outer)
        {
            return false;
        }

        var outerEnd = Math.Max(
            layout.OuterSourceOffset + 8,
            Math.Max(
                layout.OuterChildOffset + 8,
                Math.Max(
                    layout.OuterHudControlBackReferenceOffset + 8,
                    Math.Max(layout.OuterHudBackReferenceOffset + 8, layout.OuterSecondaryOffset + 8))));
        if (!TryReadStructuralBlock(memory, expected.Outer, outerEnd) ||
            !StructuralUInt64(0, out var outerVtable) ||
            outerVtable != cached.ModuleBase + layout.OuterPrimaryVtableRva ||
            !StructuralUInt64(layout.OuterSecondaryOffset, out var secondaryVtable) ||
            secondaryVtable != cached.ModuleBase + layout.OuterSecondaryVtableRva ||
            !StructuralUInt64(layout.OuterHudBackReferenceOffset, out var hudBackReference) ||
            hudBackReference != registry.Hud ||
            !StructuralUInt64(layout.OuterHudControlBackReferenceOffset, out var hudControlBackReference) ||
            hudControlBackReference != registry.HudControl ||
            !StructuralUInt64(layout.OuterSourceOffset, out var source) ||
            source != cached.ExpectedSource ||
            !StructuralUInt64(layout.OuterChildOffset, out var child) ||
            child != expected.Child)
        {
            return false;
        }

        if (expected.Child == 0)
        {
            return cached.ExpectedElectric &&
                   TryResolveChildlessProvider(
                       memory,
                       cached.ModuleBase,
                       cached.ExpectedSource,
                       out var provider) &&
                   provider == expected.Provider;
        }

        if (!memory.TryReadUInt64(expected.Child, out var childVtable) ||
            childVtable != cached.ModuleBase + layout.ChildVtableRva)
        {
            return false;
        }

        return true;
    }

    private bool TryResolveRegistry(
        IReadOnlyProcessMemory memory,
        ulong moduleBase,
        out RegistrySnapshot registry)
    {
        registry = default;
        var layout = _layout!;
        if (!TryImageAddress(moduleBase, layout.RegistryGlobalRva, 8, out var global) ||
            !memory.TryReadUInt64(global, out var wrapper) ||
            !IsObjectPointer(wrapper) ||
            !HasVtable(memory, wrapper, moduleBase + layout.RegistryWrapperVtableRva) ||
            !TryReadPointerField(memory, wrapper, layout.RegistryContextOffset, out var context) ||
            !HasVtable(memory, context, moduleBase + layout.RegistryContextVtableRva) ||
            !TryReadPointerField(memory, wrapper, layout.RegistryContextControlOffset, out var contextControl) ||
            !HasVtable(memory, contextControl, moduleBase + layout.RegistryContextControlVtableRva) ||
            !HasInlineObject(contextControl, layout.SharedControlObjectOffset, context) ||
            !TryReadPointerField(memory, context, layout.RegistrySentinelOffset, out var sentinel) ||
            !TryReadField(memory, context, layout.RegistryCountOffset, out var entryCount) ||
            !TryReadPointerField(memory, context, layout.RegistryBucketsOffset, out var buckets) ||
            !TryReadPointerField(memory, context, layout.RegistryBucketsEndOffset, out var bucketsEnd, true) ||
            !TryReadPointerField(memory, context, layout.RegistryBucketsCapacityOffset, out var bucketsCapacity, true) ||
            !TryReadField(memory, context, layout.RegistryMaskOffset, out var mask) ||
            !TryReadField(memory, context, layout.RegistryBucketCountOffset, out var bucketCount) ||
            entryCount is 0 or > MaximumRegistryEntryCount ||
            bucketCount is 0 or > MaximumRegistryBucketCount ||
            entryCount > bucketCount ||
            (bucketCount & (bucketCount - 1)) != 0 ||
            mask != bucketCount - 1 ||
            !TryMultiply(bucketCount, layout.RegistryBucketStride, out var bucketBytes) ||
            !TryAdd(buckets, bucketBytes, out var expectedBucketsEnd) ||
            bucketsEnd != expectedBucketsEnd || bucketsCapacity < bucketsEnd ||
            (bucketsCapacity - buckets) % layout.RegistryBucketStride != 0)
        {
            return false;
        }

        var bucketIndex = layout.RegistryKeyHash & mask;
        if (!TryMultiply(bucketIndex, layout.RegistryBucketStride, out var bucketOffset) ||
            !TryAdd(buckets, bucketOffset, out var bucket) ||
            !TryAdd(bucket, layout.RegistryBucketStride, out var bucketLimit) ||
            bucket < buckets || bucketLimit > bucketsEnd ||
            !TryReadPointerField(memory, bucket, layout.RegistryBucketBoundaryOffset, out var boundary) ||
            !TryReadPointerField(memory, bucket, layout.RegistryBucketNodeOffset, out var node))
        {
            return false;
        }

        Span<ulong> visited = stackalloc ulong[MaximumRegistryCollisionHops];
        var visitedCount = 0;
        var maximumHops = (int)Math.Min(entryCount, (ulong)MaximumRegistryCollisionHops);
        for (var hop = 0; hop < maximumHops; hop++)
        {
            if (node == sentinel)
            {
                return false;
            }

            if (!IsObjectPointer(node) || WasVisited(visited, visitedCount, node) ||
                !TryReadField(memory, node, layout.RegistryNodeHashOffset, out var hash) ||
                (hash & mask) != bucketIndex)
            {
                return false;
            }

            visited[visitedCount++] = node;

            if (hash == layout.RegistryKeyHash)
            {
                if (!TryReadPointerField(memory, node, layout.RegistryNodeObjectOffset, out var hud) ||
                    !TryReadPointerField(memory, node, layout.RegistryNodeControlOffset, out var hudControl) ||
                    !HasVtable(memory, hud, moduleBase + layout.HudVtableRva) ||
                    !HasVtable(memory, hudControl, moduleBase + layout.HudControlVtableRva) ||
                    !HasInlineObject(hudControl, layout.SharedControlObjectOffset, hud))
                {
                    return false;
                }

                registry = new RegistrySnapshot(
                    wrapper,
                    context,
                    contextControl,
                    sentinel,
                    buckets,
                    bucketsEnd,
                    bucketsCapacity,
                    mask,
                    bucketCount,
                    bucket,
                    boundary,
                    node,
                    hud,
                    hudControl);
                return true;
            }

            // bucket+0 is the inclusive last node. Its next pointer belongs to
            // the global intrusive list and can leave this bucket.
            if (node == boundary)
            {
                return false;
            }

            if (!TryReadPointerField(memory, node, layout.RegistryNodeNextOffset, out node))
            {
                return false;
            }
        }

        return false;
    }

    private bool TryValidateHud(
        IReadOnlyProcessMemory memory,
        ulong moduleBase,
        ulong hud,
        ulong hudControl,
        out ulong subobject)
    {
        subobject = 0;
        var layout = _layout!;
        if (!HasVtable(memory, hud, moduleBase + layout.HudVtableRva) ||
            !HasVtable(memory, hudControl, moduleBase + layout.HudControlVtableRva) ||
            !TryAdd(hud, layout.HudSubobjectOffset, out var expectedSubobject) ||
            !TryReadPointerField(memory, hud, layout.HudSubobjectPointerOffset, out subobject) ||
            subobject != expectedSubobject ||
            !HasVtable(memory, subobject, moduleBase + layout.HudSubobjectVtableRva) ||
            !memory.TryReadUInt64(moduleBase + layout.HudSubobjectVtableRva, out var slotZero) ||
            slotZero != moduleBase + layout.HudSubobjectSlotZeroTargetRva)
        {
            return false;
        }

        for (var index = 0; index < _slotZeroPrologue.Length; index++)
        {
            if (!memory.TryReadByte(slotZero + (ulong)index, out var value) ||
                value != _slotZeroPrologue[index])
            {
                return false;
            }
        }

        return true;
    }

    private bool TryReadVector(
        IReadOnlyProcessMemory memory,
        ulong subobject,
        out VectorSnapshot vector)
    {
        vector = default;
        var layout = _layout!;
        if (!TryAdd(subobject, layout.HudTypeVectorOffset, out var vectorAddress) ||
            !TryReadPointerField(memory, vectorAddress, layout.HudTypeVectorBeginOffset, out var begin) ||
            !TryReadPointerField(memory, vectorAddress, layout.HudTypeVectorEndOffset, out var end, true) ||
            !TryReadPointerField(memory, vectorAddress, layout.HudTypeVectorCapacityOffset, out var capacity, true) ||
            begin > end || end > capacity ||
            (end - begin) % layout.HudTypeVectorEntryStride != 0 ||
            (capacity - begin) % layout.HudTypeVectorEntryStride != 0)
        {
            return false;
        }

        var count = (end - begin) / layout.HudTypeVectorEntryStride;
        if (count is 0 || count > layout.HudTypeVectorMaximumCount)
        {
            return false;
        }

        vector = new VectorSnapshot(vectorAddress, begin, end, capacity, count);
        return true;
    }

    private bool TryFindTypeEntry(
        IReadOnlyProcessMemory memory,
        ulong moduleBase,
        VectorSnapshot vector,
        out ulong typeEntry)
    {
        typeEntry = 0;
        var layout = _layout!;
        var expectedToken = moduleBase + layout.HudTypeTokenRva;
        for (ulong index = 0; index < vector.Count; index++)
        {
            if (!TryMultiply(index, layout.HudTypeVectorEntryStride, out var offset) ||
                !TryAdd(vector.Begin, offset, out var entry) ||
                !TryReadField(memory, entry, layout.HudTypeTokenOffset, out var token))
            {
                return false;
            }

            if (token == expectedToken)
            {
                if (typeEntry != 0)
                {
                    return false;
                }

                typeEntry = entry;
            }
        }

        return typeEntry != 0;
    }

    private bool TryReadUniqueInstance(
        IReadOnlyProcessMemory memory,
        ulong typeEntry,
        out InstanceVectorSnapshot instances,
        out ulong outer,
        out ulong outerControl)
    {
        instances = default;
        outer = 0;
        outerControl = 0;
        var layout = _layout!;
        if (!TryReadPointerField(memory, typeEntry, layout.HudTypeInstancesBeginOffset, out var begin) ||
            !TryReadPointerField(memory, typeEntry, layout.HudTypeInstancesEndOffset, out var end, true) ||
            !TryReadPointerField(memory, typeEntry, layout.HudTypeInstancesCapacityOffset, out var capacity, true) ||
            begin > end || end > capacity ||
            !TryMultiply(layout.HudTypeInstanceCount, layout.HudTypeInstanceStride, out var exactBytes) ||
            end - begin != exactBytes ||
            (capacity - begin) % layout.HudTypeInstanceStride != 0 ||
            !TryReadPointerField(memory, begin, layout.HudTypeInstanceObjectOffset, out outer) ||
            !TryReadPointerField(memory, begin, layout.HudTypeInstanceControlOffset, out outerControl))
        {
            return false;
        }

        instances = new InstanceVectorSnapshot(begin, end, capacity);
        return true;
    }

    private bool TryValidateCachedTypeAndInstance(
        IReadOnlyProcessMemory memory,
        ulong moduleBase,
        ulong typeEntry,
        InstanceVectorSnapshot expectedInstances,
        ulong expectedOuter,
        ulong expectedOuterControl)
    {
        var layout = _layout!;
        if (!TryReadField(memory, typeEntry, layout.HudTypeTokenOffset, out var token) ||
            token != moduleBase + layout.HudTypeTokenRva ||
            !TryReadUniqueInstance(memory, typeEntry, out var instances, out var outer, out var outerControl))
        {
            return false;
        }

        return instances == expectedInstances &&
               outer == expectedOuter &&
               outerControl == expectedOuterControl;
    }

    private bool TryValidateOuter(
        IReadOnlyProcessMemory memory,
        ulong moduleBase,
        ulong hud,
        ulong hudControl,
        ulong outer,
        ulong outerControl,
        ulong expectedSource,
        out ulong child)
    {
        child = 0;
        var layout = _layout!;
        return HasVtable(memory, outerControl, moduleBase + layout.OuterControlVtableRva) &&
               HasManagedObject(memory, outerControl, layout.SharedControlObjectOffset, outer) &&
               HasVtable(memory, outer, moduleBase + layout.OuterPrimaryVtableRva) &&
               TryReadField(memory, outer, layout.OuterSecondaryOffset, out var secondaryVtable) &&
               secondaryVtable == moduleBase + layout.OuterSecondaryVtableRva &&
               TryReadField(memory, outer, layout.OuterHudBackReferenceOffset, out var hudBackReference) &&
               hudBackReference == hud &&
               TryReadField(memory, outer, layout.OuterHudControlBackReferenceOffset, out var hudControlBackReference) &&
               hudControlBackReference == hudControl &&
               TryReadField(memory, outer, layout.OuterSourceOffset, out var source) &&
               source == expectedSource &&
               TryReadField(memory, outer, layout.OuterChildOffset, out child) &&
               (child == 0 || HasVtable(memory, child, moduleBase + layout.ChildVtableRva));
    }

    private bool TryResolveChildlessProvider(
        IReadOnlyProcessMemory memory,
        ulong moduleBase,
        ulong expectedSource,
        out ulong provider)
    {
        provider = 0;
        if (!TryReadPointerField(
                memory,
                expectedSource,
                _pack.Fields.SourceProvider,
                out provider) ||
            !TryImageAddress(moduleBase, _pack.LeadVtableRva, 8, out var expectedVtable) ||
            !memory.TryReadUInt64(provider, out var vtable) ||
            vtable != expectedVtable ||
            !HasRequiredVtableSlots(memory, moduleBase, vtable, _pack.RequiredVtableSlots) ||
            !HasRequiredVtableSlots(memory, moduleBase, vtable, _layout!.RequiredProviderVtableSlots))
        {
            provider = 0;
            return false;
        }

        return true;
    }

    private bool HasRequiredVtableSlots(
        IReadOnlyProcessMemory memory,
        ulong moduleBase,
        ulong vtable,
        IReadOnlyDictionary<ulong, ulong> slots)
    {
        foreach (var slot in slots)
        {
            if (!TryAdd(vtable, slot.Key, out var slotAddress) ||
                !TryImageAddress(moduleBase, slot.Value, 1, out var expectedTarget) ||
                !memory.TryReadUInt64(slotAddress, out var target) ||
                target != expectedTarget)
            {
                return false;
            }
        }

        return true;
    }

    private bool TryReadGauge(
        IReadOnlyProcessMemory memory,
        ulong child,
        bool expectedElectric,
        out GaugeRead read)
    {
        read = default;
        var layout = _layout!;
        if (_gaugeBlock.Length == 0 ||
            !TryAdd(child, _gaugeBlockStartOffset, out var blockAddress) ||
            !IsAddressRange(blockAddress, (ulong)_gaugeBlock.Length) ||
            !memory.TryReadBytes(blockAddress, _gaugeBlock))
        {
            return false;
        }

        // This timestamp belongs to the one block copy containing angle and blur.
        // Do not replace it with a later resolver/service publication timestamp.
        var observedTimestamp = Stopwatch.GetTimestamp();
        if (!TryReadBlockUInt32(layout.ChildModeOffset, out var mode) ||
            mode > 4)
        {
            return false;
        }

        var hasHeadlightState = TryReadBlockBoolean(
            layout.ChildHeadlightsOnOffset,
            out var areHeadlightsOn);

        if (!expectedElectric)
        {
            if (mode > 2 ||
                !TryReadBlockSingleBits(layout.ChildAngleOffset, 0f, 720f, out var angle) ||
                !TryReadBlockSingleBits(layout.ChildBlurOffset, -0.65f, 0.65f, out var blur) ||
                !TryReadBlockSingleBits(layout.ChildMaximumTachometerOffset, float.Epsilon, 100_000f, out var maximum))
            {
                return false;
            }

            read = GaugeRead.Combustion(
                mode,
                hasHeadlightState,
                areHeadlightsOn,
                angle,
                blur,
                maximum,
                observedTimestamp);
            return true;
        }

        var displayedSpeedState = NativeDisplayedSpeedState.Unavailable;
        if (mode is 3 or 4)
        {
            TryReadDisplayedSpeedState(memory, out displayedSpeedState);
        }

        if (mode is 1 or 2 ||
            !TryReadBlockSingleBits(layout.ChildPowerOffset, 0f, 1f, out var power) ||
            !TryReadBlockSingleBits(layout.ChildRegenOffset, 0f, 1f, out var regen) ||
            !TryReadBlockSingleBits(layout.ChildRatioOffset, 0f, 1f, out var ratio) ||
            !TryReadBlockInt32(layout.ChildGearOffset, out var gear) ||
            !TryReadBlockInt32(layout.ChildGearNextOffset, out var gearNext) ||
            !TryReadBlockInt32(layout.ChildGearPreviousOffset, out var gearPrevious) ||
            !TryReadBlockInt32(layout.ChildGearGaugeStateOffset, out var gearGaugeState) ||
            !TryReadBlockBoolean(layout.ChildUseDriveFor1Offset, out var useDriveFor1) ||
            !TryReadBlockSingleBits(layout.ChildElectricMaximumSpeedOffset, float.Epsilon, 100_000f, out var maximumSpeed))
        {
            return false;
        }

        var electricGearState = new NativeElectricGearState(
            true,
            gear,
            gearNext,
            gearPrevious,
            gearGaugeState,
            useDriveFor1);

        if (mode == 3)
        {
            if (!TryReadBlockSingleBits(layout.ChildAngleOffset, 150f, 390f, out var angle) ||
                !TryReadBlockSingleBits(layout.ChildBlurOffset, -0.65f, 0.65f, out var blur))
            {
                return false;
            }

            read = GaugeRead.ElectricAnalog(
                mode,
                hasHeadlightState,
                areHeadlightsOn,
                angle,
                blur,
                power,
                regen,
                ratio,
                maximumSpeed,
                electricGearState,
                displayedSpeedState,
                observedTimestamp);
            return true;
        }

        read = GaugeRead.ElectricWithoutNeedle(
            mode,
            hasHeadlightState,
            areHeadlightsOn,
            power,
            regen,
            ratio,
            maximumSpeed,
            electricGearState,
            displayedSpeedState,
            observedTimestamp);
        return true;
    }

    private bool TryReadChildlessElectricGauge(
        IReadOnlyProcessMemory memory,
        ulong provider,
        out GaugeRead read)
    {
        read = default;
        var layout = _layout!;
        if (!TryReadFiniteSingleField(memory, provider, layout.ProviderRegenTargetOffset, out var powerTarget) ||
            !TryReadFiniteSingleField(memory, provider, layout.ProviderPowerNumeratorOffset, out var regenNumerator) ||
            !TryReadFiniteSingleField(memory, provider, layout.ProviderPowerDenominatorOffset, out var rawDenominator) ||
            !TryReadFiniteSingleField(memory, provider, layout.ProviderPowerLimitFirstOffset, out var firstLimit) ||
            !TryReadFiniteSingleField(memory, provider, layout.ProviderPowerLimitSecondOffset, out var secondLimit))
        {
            return false;
        }

        var denominatorScale = BitConverter.UInt32BitsToSingle(layout.ProviderPowerDenominatorScaleBits);
        var regenScale = BitConverter.UInt32BitsToSingle(layout.ProviderRegenScaleBits);
        var upperBase = BitConverter.UInt32BitsToSingle(layout.ProviderRegenUpperBaseBits);
        var denominator = rawDenominator * denominatorScale;
        if (!float.IsFinite(denominator) || denominator <= 0 ||
            !float.IsFinite(denominatorScale) || !float.IsFinite(regenScale) ||
            !float.IsFinite(upperBase) || upperBase is < 0 or > 1)
        {
            return false;
        }

        // Mirrors FH6 6.430.771.0 at 0x4B027C9 and 0x4B0281D: the first
        // provider getter supplies power directly; regeneration is the scaled
        // provider ratio clamped by the authored two-value operating limit.
        var power = Math.Clamp(powerTarget, 0f, 1f);
        var regenUpper = Math.Clamp(
            ((1f - upperBase) * Math.Min(firstLimit, secondLimit)) + upperBase,
            0f,
            1f);
        var regen = Math.Clamp((regenScale / denominator) * regenNumerator, 0f, regenUpper);

        var observedTimestamp = Stopwatch.GetTimestamp();
        read = GaugeRead.ElectricWithoutNeedle(
            uint.MaxValue,
            false,
            false,
            BitConverter.SingleToUInt32Bits(power),
            BitConverter.SingleToUInt32Bits(regen),
            layout.ChildlessRegenPowerRatioBits,
            BitConverter.SingleToUInt32Bits(float.NaN),
            NativeElectricGearState.Unavailable,
            NativeDisplayedSpeedState.Unavailable,
            observedTimestamp);
        return true;
    }

    private static bool TryReadFiniteSingleField(
        IReadOnlyProcessMemory memory,
        ulong instance,
        ulong offset,
        out float value)
    {
        value = 0;
        return IsObjectPointer(instance) &&
               TryAdd(instance, offset, out var address) &&
               IsAddressRange(address, sizeof(float)) &&
               memory.TryReadSingle(address, out value) &&
               float.IsFinite(value);
    }

    private bool TryReadChildlessBlockSingleBits(ulong fieldOffset, out uint bits)
    {
        bits = 0;
        if (fieldOffset < _childlessElectricBlockStartOffset)
        {
            return false;
        }

        var candidate = fieldOffset - _childlessElectricBlockStartOffset;
        if (candidate > (ulong)_childlessElectricBlock.Length - sizeof(uint))
        {
            return false;
        }

        var candidateBits = BinaryPrimitives.ReadUInt32LittleEndian(
            _childlessElectricBlock.AsSpan((int)candidate, sizeof(uint)));
        var value = BitConverter.UInt32BitsToSingle(candidateBits);
        if (!float.IsFinite(value) || value is < 0 or > 1)
        {
            return false;
        }

        bits = candidateBits;
        return true;
    }

    private bool TryReadBlockUInt32(ulong fieldOffset, out uint value)
    {
        value = 0;
        if (!TryGetBlockRange(fieldOffset, sizeof(uint), out var relative))
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(
            _gaugeBlock.AsSpan(relative, sizeof(uint)));
        return true;
    }

    private bool TryReadBlockUInt64(ulong fieldOffset, out ulong value)
    {
        value = 0;
        if (!TryGetBlockRange(fieldOffset, sizeof(ulong), out var relative))
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(
            _gaugeBlock.AsSpan(relative, sizeof(ulong)));
        return true;
    }

    private bool TryReadSpeedUnit(
        IReadOnlyProcessMemory memory,
        ulong speedUnitObject,
        out SpeedUnit speedUnit)
    {
        speedUnit = default;
        var layout = _layout!;
        if (!IsObjectPointer(speedUnitObject) ||
            !TryAdd(speedUnitObject, layout.SpeedUnitEnumOffset, out var enumAddress) ||
            !IsAddressRange(enumAddress, sizeof(uint)) ||
            !memory.TryReadUInt32(enumAddress, out var nativeValue))
        {
            return false;
        }

        if (nativeValue == layout.SpeedUnitMphValue)
        {
            speedUnit = SpeedUnit.MilesPerHour;
            return true;
        }

        if (nativeValue == layout.SpeedUnitKphValue)
        {
            speedUnit = SpeedUnit.KilometersPerHour;
            return true;
        }

        return false;
    }

    private bool TryReadDisplayedSpeedState(
        IReadOnlyProcessMemory memory,
        out NativeDisplayedSpeedState state)
    {
        state = NativeDisplayedSpeedState.Unavailable;
        var layout = _layout!;
        if (!TryReadBlockInt32(layout.ChildSpeedDigitOneOffset, out var speedDigitOne) ||
            speedDigitOne is < 0 or > 9 ||
            !TryReadBlockInt32(layout.ChildSpeedDigitTenOffset, out var speedDigitTen) ||
            speedDigitTen is < 0 or > 9 ||
            !TryReadBlockInt32(layout.ChildSpeedDigitHundredOffset, out var speedDigitHundred) ||
            speedDigitHundred is < 0 or > 9 ||
            !TryReadBlockBoolean(layout.ChildSpeedLessOrEqualOneOffset, out var speedLessOrEqualOne) ||
            !TryReadBlockBoolean(layout.ChildSpeedLessTenOffset, out var speedLessTen) ||
            !TryReadBlockBoolean(layout.ChildSpeedLessHundredOffset, out var speedLessHundred) ||
            !TryReadBlockUInt64(layout.ChildSpeedUnitObjectOffset, out var speedUnitObject) ||
            !TryReadSpeedUnit(memory, speedUnitObject, out var speedUnit))
        {
            return false;
        }

        state = new NativeDisplayedSpeedState(
            true,
            speedDigitHundred,
            speedDigitTen,
            speedDigitOne,
            speedLessOrEqualOne,
            speedLessTen,
            speedLessHundred,
            speedUnit);
        return true;
    }

    private bool TryReadBlockInt32(ulong fieldOffset, out int value)
    {
        value = 0;
        if (!TryReadBlockUInt32(fieldOffset, out var raw))
        {
            return false;
        }

        value = unchecked((int)raw);
        return true;
    }

    private bool TryReadBlockBoolean(ulong fieldOffset, out bool value)
    {
        value = false;
        if (!TryGetBlockRange(fieldOffset, sizeof(byte), out var relative) ||
            _gaugeBlock[relative] > 1)
        {
            return false;
        }

        value = _gaugeBlock[relative] != 0;
        return true;
    }

    private bool TryReadBlockSingleBits(
        ulong fieldOffset,
        float minimum,
        float maximum,
        out uint bits)
    {
        bits = 0;
        if (!TryReadBlockUInt32(fieldOffset, out var candidateBits))
        {
            return false;
        }

        var candidate = BitConverter.UInt32BitsToSingle(candidateBits);
        if (!float.IsFinite(candidate) || candidate < minimum || candidate > maximum)
        {
            return false;
        }

        bits = candidateBits;
        return true;
    }

    private bool TryGetBlockRange(ulong fieldOffset, int width, out int relative)
    {
        relative = 0;
        if (fieldOffset < _gaugeBlockStartOffset)
        {
            return false;
        }

        var candidate = fieldOffset - _gaugeBlockStartOffset;
        if (candidate > (ulong)_gaugeBlock.Length ||
            (ulong)width > (ulong)_gaugeBlock.Length - candidate)
        {
            return false;
        }

        relative = (int)candidate;
        return true;
    }

    private bool TryReadStructuralBlock(
        IReadOnlyProcessMemory memory,
        ulong address,
        ulong length)
    {
        if (length is 0 or > NativeHudCompatibilityPack.MaximumFieldBytes ||
            !IsAddressRange(address, length))
        {
            return false;
        }

        return memory.TryReadBytes(address, _structuralBlock.AsSpan(0, (int)length));
    }

    private bool StructuralUInt64(ulong offset, out ulong value)
    {
        value = 0;
        if (offset > (ulong)_structuralBlock.Length - sizeof(ulong))
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(
            _structuralBlock.AsSpan((int)offset, sizeof(ulong)));
        return true;
    }

    private bool TryImageAddress(ulong moduleBase, ulong rva, ulong width, out ulong address)
    {
        address = 0;
        if (rva >= _pack.ImageSize || width > _pack.ImageSize - rva ||
            !TryAdd(moduleBase, rva, out address))
        {
            return false;
        }

        return IsAddressRange(address, width);
    }

    private static bool HasVtable(IReadOnlyProcessMemory memory, ulong instance, ulong expectedVtable) =>
        IsObjectPointer(instance) &&
        memory.TryReadUInt64(instance, out var vtable) &&
        vtable == expectedVtable;

    private bool HasManagedObject(
        IReadOnlyProcessMemory memory,
        ulong control,
        ulong objectOffset,
        ulong expectedObject) =>
        TryReadField(memory, control, objectOffset, out var managedObject) &&
        managedObject == expectedObject;

    private static bool HasInlineObject(
        ulong control,
        ulong objectOffset,
        ulong expectedObject) =>
        IsObjectPointer(control) &&
        TryAdd(control, objectOffset, out var managedObject) &&
        managedObject == expectedObject;

    private static bool TryReadPointerField(
        IReadOnlyProcessMemory memory,
        ulong instance,
        ulong offset,
        out ulong value,
        bool allowOnePastEnd = false)
    {
        value = 0;
        return TryReadField(memory, instance, offset, out value) &&
               (allowOnePastEnd ? IsAlignedUserAddress(value) : IsObjectPointer(value));
    }

    private static bool TryReadField(
        IReadOnlyProcessMemory memory,
        ulong instance,
        ulong offset,
        out ulong value)
    {
        value = 0;
        return IsObjectPointer(instance) &&
               TryAdd(instance, offset, out var address) &&
               IsAddressRange(address, 8) &&
               memory.TryReadUInt64(address, out value);
    }

    private static bool IsObjectPointer(ulong address) =>
        address >= 0x10000 && address <= MaximumUserAddress - 7 && (address & 7) == 0;

    private static bool IsAlignedUserAddress(ulong address) =>
        address >= 0x10000 && address <= MaximumUserAddress && (address & 7) == 0;

    private static bool IsAddressRange(ulong address, ulong width) =>
        width > 0 && address >= 0x10000 && address <= MaximumUserAddress &&
        width - 1 <= MaximumUserAddress - address;

    private static bool TryAdd(ulong first, ulong second, out ulong value)
    {
        value = first + second;
        return value >= first;
    }

    private static bool TryMultiply(ulong first, ulong second, out ulong value)
    {
        if (first != 0 && second > ulong.MaxValue / first)
        {
            value = 0;
            return false;
        }

        value = first * second;
        return true;
    }

    private static bool WasVisited(ReadOnlySpan<ulong> visited, int count, ulong node)
    {
        for (var index = 0; index < count; index++)
        {
            if (visited[index] == node)
            {
                return true;
            }
        }

        return false;
    }

    private static NativeGaugeDirectResult Unavailable() => new(
        NativeGaugeDirectState.Unavailable,
        uint.MaxValue,
        false,
        false,
        false,
        false,
        float.NaN,
        float.NaN,
        float.NaN,
        float.NaN,
        float.NaN,
        float.NaN,
        float.NaN,
        NativeElectricGearState.Unavailable,
        NativeDisplayedSpeedState.Unavailable,
        0L);

    private readonly record struct CacheEntry(
        ulong ModuleBase,
        ulong ExpectedSource,
        bool ExpectedElectric,
        ChainSnapshot Chain);

    private readonly record struct RegistrySnapshot(
        ulong Wrapper,
        ulong Context,
        ulong ContextControl,
        ulong Sentinel,
        ulong Buckets,
        ulong BucketsEnd,
        ulong BucketsCapacity,
        ulong Mask,
        ulong BucketCount,
        ulong Bucket,
        ulong Boundary,
        ulong Node,
        ulong Hud,
        ulong HudControl);

    private readonly record struct VectorSnapshot(
        ulong Address,
        ulong Begin,
        ulong End,
        ulong Capacity,
        ulong Count);

    private readonly record struct InstanceVectorSnapshot(ulong Begin, ulong End, ulong Capacity);

    private readonly record struct ChainSnapshot(
        RegistrySnapshot Registry,
        ulong Subobject,
        VectorSnapshot Vector,
        ulong TypeEntry,
        InstanceVectorSnapshot Instances,
        ulong Outer,
        ulong OuterControl,
        ulong Child,
        ulong Provider);

    private readonly record struct GaugeRead(
        uint Mode,
        bool IsElectric,
        bool HasNeedlePair,
        bool HasHeadlightState,
        bool AreHeadlightsOn,
        uint AngleBits,
        uint BlurBits,
        uint PowerBits,
        uint RegenBits,
        uint RatioBits,
        uint TachometerMaximumBits,
        uint ElectricMaximumSpeedBits,
        NativeElectricGearState ElectricGearState,
        NativeDisplayedSpeedState DisplayedSpeedState,
        long ObservedTimestamp)
    {
        public static GaugeRead Combustion(
            uint mode,
            bool hasHeadlightState,
            bool areHeadlightsOn,
            uint angle,
            uint blur,
            uint maximum,
            long observedTimestamp) =>
            new(mode, false, true, hasHeadlightState, areHeadlightsOn, angle, blur, 0, 0, 0, maximum, 0,
                NativeElectricGearState.Unavailable,
                NativeDisplayedSpeedState.Unavailable,
                observedTimestamp);

        public static GaugeRead ElectricAnalog(
            uint mode,
            bool hasHeadlightState,
            bool areHeadlightsOn,
            uint angle,
            uint blur,
            uint power,
            uint regen,
            uint ratio,
            uint maximumSpeed,
            NativeElectricGearState electricGearState,
            NativeDisplayedSpeedState displayedSpeedState,
            long observedTimestamp) =>
            new(
                mode,
                true,
                true,
                hasHeadlightState,
                areHeadlightsOn,
                angle,
                blur,
                power,
                regen,
                ratio,
                0,
                maximumSpeed,
                electricGearState,
                displayedSpeedState,
                observedTimestamp);

        public static GaugeRead ElectricWithoutNeedle(
            uint mode,
            bool hasHeadlightState,
            bool areHeadlightsOn,
            uint power,
            uint regen,
            uint ratio,
            uint maximumSpeed,
            NativeElectricGearState electricGearState,
            NativeDisplayedSpeedState displayedSpeedState,
            long observedTimestamp) =>
            new(mode, true, false, hasHeadlightState, areHeadlightsOn, 0, 0, power, regen, ratio, 0, maximumSpeed,
                electricGearState,
                displayedSpeedState,
                observedTimestamp);

        public NativeGaugeDirectResult ToResult(NativeGaugeDirectState state) => new(
            state,
            Mode,
            IsElectric,
            HasNeedlePair,
            HasHeadlightState,
            AreHeadlightsOn,
            HasNeedlePair ? BitConverter.UInt32BitsToSingle(AngleBits) : float.NaN,
            HasNeedlePair ? BitConverter.UInt32BitsToSingle(BlurBits) : float.NaN,
            IsElectric ? BitConverter.UInt32BitsToSingle(PowerBits) : float.NaN,
            IsElectric ? BitConverter.UInt32BitsToSingle(RegenBits) : float.NaN,
            IsElectric ? BitConverter.UInt32BitsToSingle(RatioBits) : float.NaN,
            IsElectric ? float.NaN : BitConverter.UInt32BitsToSingle(TachometerMaximumBits),
            IsElectric ? BitConverter.UInt32BitsToSingle(ElectricMaximumSpeedBits) : float.NaN,
            IsElectric ? ElectricGearState : NativeElectricGearState.Unavailable,
            IsElectric ? DisplayedSpeedState : NativeDisplayedSpeedState.Unavailable,
            ObservedTimestamp);
    }
}
