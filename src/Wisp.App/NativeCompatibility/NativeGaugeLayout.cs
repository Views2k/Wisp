namespace Wisp.App;

public sealed class NativeGaugeLayout
{
    internal NativeGaugeLayout(
        IReadOnlyDictionary<string, ulong> values,
        string hudSubobjectSlotZeroPrologueHex,
        IReadOnlyDictionary<ulong, ulong> requiredProviderVtableSlots)
    {
        RegistryGlobalRva = values["registryGlobalRva"];
        RegistryKeyHash = values["registryKeyHash"];
        RegistryWrapperVtableRva = values["registryWrapperVtableRva"];
        RegistryContextVtableRva = values["registryContextVtableRva"];
        RegistryContextControlVtableRva = values["registryContextControlVtableRva"];
        RegistryContextOffset = values["registryContextOffset"];
        RegistryContextControlOffset = values["registryContextControlOffset"];
        RegistrySentinelOffset = values["registrySentinelOffset"];
        RegistryCountOffset = values["registryCountOffset"];
        RegistryBucketsOffset = values["registryBucketsOffset"];
        RegistryBucketsEndOffset = values["registryBucketsEndOffset"];
        RegistryBucketsCapacityOffset = values["registryBucketsCapacityOffset"];
        RegistryMaskOffset = values["registryMaskOffset"];
        RegistryBucketCountOffset = values["registryBucketCountOffset"];
        RegistryBucketStride = values["registryBucketStride"];
        RegistryBucketBoundaryOffset = values["registryBucketBoundaryOffset"];
        RegistryBucketNodeOffset = values["registryBucketNodeOffset"];
        RegistryNodeNextOffset = values["registryNodeNextOffset"];
        RegistryNodeHashOffset = values["registryNodeHashOffset"];
        RegistryNodeObjectOffset = values["registryNodeObjectOffset"];
        RegistryNodeControlOffset = values["registryNodeControlOffset"];
        SharedControlObjectOffset = values["sharedControlObjectOffset"];
        HudVtableRva = values["hudVtableRva"];
        HudControlVtableRva = values["hudControlVtableRva"];
        HudSubobjectOffset = values["hudSubobjectOffset"];
        HudSubobjectPointerOffset = values["hudSubobjectPointerOffset"];
        HudSubobjectVtableRva = values["hudSubobjectVtableRva"];
        HudSubobjectSlotZeroTargetRva = values["hudSubobjectSlotZeroTargetRva"];
        HudSubobjectSlotZeroPrologueHex = hudSubobjectSlotZeroPrologueHex;
        HudTypeVectorOffset = values["hudTypeVectorOffset"];
        HudTypeVectorMaximumCount = values["hudTypeVectorMaximumCount"];
        HudTypeVectorBeginOffset = values["hudTypeVectorBeginOffset"];
        HudTypeVectorEndOffset = values["hudTypeVectorEndOffset"];
        HudTypeVectorCapacityOffset = values["hudTypeVectorCapacityOffset"];
        HudTypeVectorEntryStride = values["hudTypeVectorEntryStride"];
        HudTypeTokenRva = values["hudTypeTokenRva"];
        HudTypeTokenOffset = values["hudTypeTokenOffset"];
        HudTypeInstancesBeginOffset = values["hudTypeInstancesBeginOffset"];
        HudTypeInstancesEndOffset = values["hudTypeInstancesEndOffset"];
        HudTypeInstancesCapacityOffset = values["hudTypeInstancesCapacityOffset"];
        HudTypeInstanceCount = values["hudTypeInstanceCount"];
        HudTypeInstanceStride = values["hudTypeInstanceStride"];
        HudTypeInstanceObjectOffset = values["hudTypeInstanceObjectOffset"];
        HudTypeInstanceControlOffset = values["hudTypeInstanceControlOffset"];
        OuterControlVtableRva = values["outerControlVtableRva"];
        OuterPrimaryVtableRva = values["outerPrimaryVtableRva"];
        OuterSecondaryVtableRva = values["outerSecondaryVtableRva"];
        OuterSecondaryOffset = values["outerSecondaryOffset"];
        OuterHudBackReferenceOffset = values["outerHudBackReferenceOffset"];
        OuterHudControlBackReferenceOffset = values["outerHudControlBackReferenceOffset"];
        OuterChildOffset = values["outerChildOffset"];
        OuterSourceOffset = values["outerSourceOffset"];
        OuterPowerFillOffset = values["outerPowerFillOffset"];
        OuterRegenFillOffset = values["outerRegenFillOffset"];
        ChildlessRegenPowerRatioBits = checked((uint)values["childlessRegenPowerRatioBits"]);
        ChildVtableRva = values["childVtableRva"];
        ChildModeOffset = values["childModeOffset"];
        ChildAngleOffset = values["childAngleOffset"];
        ChildBlurOffset = values["childBlurOffset"];
        ChildSpeedDigitOneOffset = values["childSpeedDigitOneOffset"];
        ChildSpeedDigitTenOffset = values["childSpeedDigitTenOffset"];
        ChildSpeedDigitHundredOffset = values["childSpeedDigitHundredOffset"];
        ChildSpeedLessOrEqualOneOffset = values["childSpeedLessOrEqualOneOffset"];
        ChildSpeedLessTenOffset = values["childSpeedLessTenOffset"];
        ChildSpeedLessHundredOffset = values["childSpeedLessHundredOffset"];
        ChildSpeedUnitObjectOffset = values["childSpeedUnitObjectOffset"];
        SpeedUnitEnumOffset = values["speedUnitEnumOffset"];
        SpeedUnitMphValue = checked((uint)values["speedUnitMphValue"]);
        SpeedUnitKphValue = checked((uint)values["speedUnitKphValue"]);
        ChildHeadlightsOnOffset = values["childHeadlightsOnOffset"];
        ChildPowerOffset = values["childPowerOffset"];
        ChildRegenOffset = values["childRegenOffset"];
        ChildRatioOffset = values["childRatioOffset"];
        ChildGearOffset = values["childGearOffset"];
        ChildGearNextOffset = values["childGearNextOffset"];
        ChildGearPreviousOffset = values["childGearPreviousOffset"];
        ChildGearGaugeStateOffset = values["childGearGaugeStateOffset"];
        ChildUseDriveFor1Offset = values["childUseDriveFor1Offset"];
        ChildMaximumTachometerOffset = values["childMaximumTachometerOffset"];
        ChildElectricMaximumSpeedOffset = values["childElectricMaximumSpeedOffset"];
        ProviderPowerLimitFirstOffset = values["providerPowerLimitFirstOffset"];
        ProviderPowerLimitSecondOffset = values["providerPowerLimitSecondOffset"];
        ProviderPowerNumeratorOffset = values["providerPowerNumeratorOffset"];
        ProviderPowerDenominatorOffset = values["providerPowerDenominatorOffset"];
        ProviderRegenTargetOffset = values["providerRegenTargetOffset"];
        ProviderPowerDenominatorScaleBits = checked((uint)values["providerPowerDenominatorScaleBits"]);
        ProviderRegenScaleBits = checked((uint)values["providerRegenScaleBits"]);
        ProviderRegenUpperBaseBits = checked((uint)values["providerRegenUpperBaseBits"]);
        ProviderElectricSpeedOffset = values["providerElectricSpeedOffset"];
        RequiredProviderVtableSlots = requiredProviderVtableSlots;
    }

    public ulong RegistryGlobalRva { get; }
    public ulong RegistryKeyHash { get; }
    public ulong RegistryWrapperVtableRva { get; }
    public ulong RegistryContextVtableRva { get; }
    public ulong RegistryContextControlVtableRva { get; }
    public ulong RegistryContextOffset { get; }
    public ulong RegistryContextControlOffset { get; }
    public ulong RegistrySentinelOffset { get; }
    public ulong RegistryCountOffset { get; }
    public ulong RegistryBucketsOffset { get; }
    public ulong RegistryBucketsEndOffset { get; }
    public ulong RegistryBucketsCapacityOffset { get; }
    public ulong RegistryMaskOffset { get; }
    public ulong RegistryBucketCountOffset { get; }
    public ulong RegistryBucketStride { get; }
    public ulong RegistryBucketBoundaryOffset { get; }
    public ulong RegistryBucketNodeOffset { get; }
    public ulong RegistryNodeNextOffset { get; }
    public ulong RegistryNodeHashOffset { get; }
    public ulong RegistryNodeObjectOffset { get; }
    public ulong RegistryNodeControlOffset { get; }
    public ulong SharedControlObjectOffset { get; }
    public ulong HudVtableRva { get; }
    public ulong HudControlVtableRva { get; }
    public ulong HudSubobjectOffset { get; }
    public ulong HudSubobjectPointerOffset { get; }
    public ulong HudSubobjectVtableRva { get; }
    public ulong HudSubobjectSlotZeroTargetRva { get; }
    public string HudSubobjectSlotZeroPrologueHex { get; }
    public ulong HudTypeVectorOffset { get; }
    public ulong HudTypeVectorMaximumCount { get; }
    public ulong HudTypeVectorBeginOffset { get; }
    public ulong HudTypeVectorEndOffset { get; }
    public ulong HudTypeVectorCapacityOffset { get; }
    public ulong HudTypeVectorEntryStride { get; }
    public ulong HudTypeTokenRva { get; }
    public ulong HudTypeTokenOffset { get; }
    public ulong HudTypeInstancesBeginOffset { get; }
    public ulong HudTypeInstancesEndOffset { get; }
    public ulong HudTypeInstancesCapacityOffset { get; }
    public ulong HudTypeInstanceCount { get; }
    public ulong HudTypeInstanceStride { get; }
    public ulong HudTypeInstanceObjectOffset { get; }
    public ulong HudTypeInstanceControlOffset { get; }
    public ulong OuterControlVtableRva { get; }
    public ulong OuterPrimaryVtableRva { get; }
    public ulong OuterSecondaryVtableRva { get; }
    public ulong OuterSecondaryOffset { get; }
    public ulong OuterHudBackReferenceOffset { get; }
    public ulong OuterHudControlBackReferenceOffset { get; }
    public ulong OuterChildOffset { get; }
    public ulong OuterSourceOffset { get; }
    public ulong OuterPowerFillOffset { get; }
    public ulong OuterRegenFillOffset { get; }
    public uint ChildlessRegenPowerRatioBits { get; }
    public ulong ChildVtableRva { get; }
    public ulong ChildModeOffset { get; }
    public ulong ChildAngleOffset { get; }
    public ulong ChildBlurOffset { get; }
    public ulong ChildSpeedDigitOneOffset { get; }
    public ulong ChildSpeedDigitTenOffset { get; }
    public ulong ChildSpeedDigitHundredOffset { get; }
    public ulong ChildSpeedLessOrEqualOneOffset { get; }
    public ulong ChildSpeedLessTenOffset { get; }
    public ulong ChildSpeedLessHundredOffset { get; }
    public ulong ChildSpeedUnitObjectOffset { get; }
    public ulong SpeedUnitEnumOffset { get; }
    public uint SpeedUnitMphValue { get; }
    public uint SpeedUnitKphValue { get; }
    public ulong ChildHeadlightsOnOffset { get; }
    public ulong ChildPowerOffset { get; }
    public ulong ChildRegenOffset { get; }
    public ulong ChildRatioOffset { get; }
    public ulong ChildGearOffset { get; }
    public ulong ChildGearNextOffset { get; }
    public ulong ChildGearPreviousOffset { get; }
    public ulong ChildGearGaugeStateOffset { get; }
    public ulong ChildUseDriveFor1Offset { get; }
    public ulong ChildMaximumTachometerOffset { get; }
    public ulong ChildElectricMaximumSpeedOffset { get; }
    public ulong ProviderPowerLimitFirstOffset { get; }
    public ulong ProviderPowerLimitSecondOffset { get; }
    public ulong ProviderPowerNumeratorOffset { get; }
    public ulong ProviderPowerDenominatorOffset { get; }
    public ulong ProviderRegenTargetOffset { get; }
    public uint ProviderPowerDenominatorScaleBits { get; }
    public uint ProviderRegenScaleBits { get; }
    public uint ProviderRegenUpperBaseBits { get; }
    public ulong ProviderElectricSpeedOffset { get; }
    public IReadOnlyDictionary<ulong, ulong> RequiredProviderVtableSlots { get; }
}
