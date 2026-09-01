using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;

namespace Wisp.App;

public sealed class NativeHudCompatibilityPack
{
    public const int MaximumJsonBytes = 64 * 1024;

    internal const ulong MaximumFieldBytes = 64 * 1024;
    private const uint MaximumImageSize = 1024 * 1024 * 1024;
    private const long MaximumExecutableLength = 2L * 1024 * 1024 * 1024;
    private const uint ReaderThresholdBits = 0x3DCCCCCD;

    private static readonly FrozenSet<string> LegacyPackProperties = new[]
    {
        "schemaVersion", "readerVersion", "id", "revision", "gameVersion",
        "executableLength", "executableSha256", "imageSize", "sourceVectorRva",
        "thresholdRva", "leadVtableRva", "expectedThresholdBits", "fields",
        "requiredVtableSlots"
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> VersionTwoPackProperties = LegacyPackProperties
        .Append("gameplayVisibility")
        .ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> VersionThreePackProperties = VersionTwoPackProperties
        .Append("nativeGauge")
        .ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> SlotProperties = new[]
    {
        "offset", "targetRva"
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<ulong> RequiredSlotOffsets = new ulong[]
    {
        0x0210, 0x02A8, 0x02B0, 0x02B8, 0x0680, 0x1058, 0x1060, 0x1068, 0x1078
    }.ToFrozenSet();

    private static readonly FrozenSet<ulong> RequiredNativeGaugeProviderSlotOffsets = new ulong[]
    {
        0x0298, 0x0358, 0x05B8, 0x0720, 0x0798, 0x0E28
    }.ToFrozenSet();

    private static readonly FieldDefinition[] FieldDefinitions =
    [
        new("sourceProvider", FieldOwner.Source, 8, 8),
        new("sourceCarOrdinal", FieldOwner.Source, 4, 4),
        new("providerRpm", FieldOwner.Provider, 4, 4),
        new("providerSimRedlineAngularVelocity", FieldOwner.Provider, 4, 4),
        new("providerTachometerMaximumAngularVelocity", FieldOwner.Provider, 4, 4),
        new("localPlayerFlag", FieldOwner.Provider, 1, 1),
        new("localPlayerProviderFlag", FieldOwner.Provider, 1, 1),
        new("stmState", FieldOwner.Provider, 4, 4),
        new("absState", FieldOwner.Provider, 4, 4),
        new("stmAvailable", FieldOwner.Provider, 1, 1),
        new("tcrAvailable", FieldOwner.Provider, 1, 1),
        new("absAvailable", FieldOwner.Provider, 1, 1),
        new("lcAvailable", FieldOwner.Provider, 1, 1),
        new("lcPrimary", FieldOwner.Provider, 4, 4),
        new("lcMode", FieldOwner.Provider, 4, 4),
        new("lcSecondary", FieldOwner.Provider, 4, 4),
        new("tcrSecondary", FieldOwner.Provider, 4, 4),
        new("tcrPrimary", FieldOwner.Provider, 4, 4),
        new("tcrTertiary", FieldOwner.Provider, 4, 4),
        new("tcrWheelValues", FieldOwner.Provider, 16, 4),
        new("firstWheelPointer", FieldOwner.Provider, 8, 8),
        new("secondWheelPointer", FieldOwner.Provider, 8, 8),
        new("thirdWheelPointer", FieldOwner.Provider, 8, 8),
        new("wheelId", FieldOwner.Wheel, 4, 4)
    ];

    private static readonly FrozenSet<string> FieldProperties = FieldDefinitions
        .Select(definition => definition.Name)
        .ToFrozenSet(StringComparer.Ordinal);

    private static readonly string[] GameplayVisibilityRvaProperties =
    [
        "uiServiceRva", "uiServiceVtableRva", "dependencyVtableRva",
        "transitionManagerVtableRva", "hudPageVtableRva"
    ];

    private static readonly FieldDefinition[] GameplayVisibilityFieldDefinitions =
    [
        new("serviceDependencyOffset", FieldOwner.UiService, 8, 8),
        new("rootTransitionManagerOffset", FieldOwner.UiDependency, 8, 8),
        new("managerOwnerOffset", FieldOwner.TransitionManager, 8, 8),
        new("managerCurrentPageOffset", FieldOwner.TransitionManager, 8, 8),
        new("managerStateOffset", FieldOwner.TransitionManager, 4, 4),
        new("pageTransitionManagerOffset", FieldOwner.UiPage, 8, 8),
        new("pageUiVisibleOffset", FieldOwner.UiPage, 1, 1)
    ];

    private static readonly FrozenSet<string> GameplayVisibilityProperties = GameplayVisibilityRvaProperties
        .Concat(GameplayVisibilityFieldDefinitions.Select(definition => definition.Name))
        .ToFrozenSet(StringComparer.Ordinal);

    private static readonly string[] NativeGaugeRvaProperties =
    [
        "registryGlobalRva", "registryWrapperVtableRva", "registryContextVtableRva",
        "registryContextControlVtableRva", "hudVtableRva", "hudControlVtableRva",
        "hudSubobjectVtableRva", "hudTypeTokenRva", "outerControlVtableRva",
        "outerPrimaryVtableRva", "outerSecondaryVtableRva", "childVtableRva"
    ];

    private static readonly string[] NativeGaugeFunctionRvaProperties =
    [
        "hudSubobjectSlotZeroTargetRva"
    ];

    private static readonly NativeGaugeFieldDefinition[] NativeGaugeFieldDefinitions =
    [
        new("registryContextOffset", FieldOwner.RegistryWrapper, 8, 8, 8),
        new("registryContextControlOffset", FieldOwner.RegistryWrapper, 8, 8, 8),
        new("registrySentinelOffset", FieldOwner.RegistryContext, 8, 8, 8),
        new("registryCountOffset", FieldOwner.RegistryContext, 8, 8, 8),
        new("registryBucketsOffset", FieldOwner.RegistryContext, 8, 8, 8),
        new("registryBucketsEndOffset", FieldOwner.RegistryContext, 8, 8, 8),
        new("registryBucketsCapacityOffset", FieldOwner.RegistryContext, 8, 8, 8),
        new("registryMaskOffset", FieldOwner.RegistryContext, 8, 8, 8),
        new("registryBucketCountOffset", FieldOwner.RegistryContext, 8, 8, 8),
        new("registryBucketBoundaryOffset", FieldOwner.RegistryBucket, 8, 8, 0),
        new("registryBucketNodeOffset", FieldOwner.RegistryBucket, 8, 8, 0),
        new("registryNodeNextOffset", FieldOwner.RegistryNode, 8, 8, 0),
        new("registryNodeHashOffset", FieldOwner.RegistryNode, 8, 8, 0),
        new("registryNodeObjectOffset", FieldOwner.RegistryNode, 8, 8, 0),
        new("registryNodeControlOffset", FieldOwner.RegistryNode, 8, 8, 0),
        new("sharedControlObjectOffset", FieldOwner.SharedControl, 8, 8, 8),
        new("hudSubobjectOffset", FieldOwner.Hud, 8, 8, 8),
        new("hudSubobjectPointerOffset", FieldOwner.Hud, 8, 8, 8),
        new("hudTypeVectorOffset", FieldOwner.HudSubobject, 24, 8, 8),
        new("hudTypeVectorBeginOffset", FieldOwner.HudVector, 8, 8, 0),
        new("hudTypeVectorEndOffset", FieldOwner.HudVector, 8, 8, 0),
        new("hudTypeVectorCapacityOffset", FieldOwner.HudVector, 8, 8, 0),
        new("hudTypeTokenOffset", FieldOwner.HudVectorEntry, 8, 8, 0),
        new("hudTypeInstancesBeginOffset", FieldOwner.HudVectorEntry, 8, 8, 0),
        new("hudTypeInstancesEndOffset", FieldOwner.HudVectorEntry, 8, 8, 0),
        new("hudTypeInstancesCapacityOffset", FieldOwner.HudVectorEntry, 8, 8, 0),
        new("hudTypeInstanceObjectOffset", FieldOwner.HudTypeInstance, 8, 8, 0),
        new("hudTypeInstanceControlOffset", FieldOwner.HudTypeInstance, 8, 8, 0),
        new("outerSecondaryOffset", FieldOwner.Outer, 8, 8, 8),
        new("outerHudBackReferenceOffset", FieldOwner.Outer, 8, 8, 8),
        new("outerHudControlBackReferenceOffset", FieldOwner.Outer, 8, 8, 8),
        new("outerChildOffset", FieldOwner.Outer, 8, 8, 8),
        new("outerSourceOffset", FieldOwner.Outer, 8, 8, 8),
        new("outerPowerFillOffset", FieldOwner.Outer, 4, 4, 8),
        new("outerRegenFillOffset", FieldOwner.Outer, 4, 4, 8),
        new("childModeOffset", FieldOwner.Child, 4, 4, 8),
        new("childAngleOffset", FieldOwner.Child, 4, 4, 8),
        new("childBlurOffset", FieldOwner.Child, 4, 4, 8),
        new("childSpeedDigitOneOffset", FieldOwner.Child, 4, 4, 8),
        new("childSpeedDigitTenOffset", FieldOwner.Child, 4, 4, 8),
        new("childSpeedDigitHundredOffset", FieldOwner.Child, 4, 4, 8),
        new("childSpeedLessOrEqualOneOffset", FieldOwner.Child, 1, 1, 8),
        new("childSpeedLessTenOffset", FieldOwner.Child, 1, 1, 8),
        new("childSpeedLessHundredOffset", FieldOwner.Child, 1, 1, 8),
        new("childSpeedUnitObjectOffset", FieldOwner.Child, 8, 8, 8),
        new("speedUnitEnumOffset", FieldOwner.SpeedUnit, 4, 4, 0),
        new("childHeadlightsOnOffset", FieldOwner.Child, 1, 1, 8),
        new("childPowerOffset", FieldOwner.Child, 4, 4, 8),
        new("childRegenOffset", FieldOwner.Child, 4, 4, 8),
        new("childRatioOffset", FieldOwner.Child, 4, 4, 8),
        new("childGearOffset", FieldOwner.Child, 4, 4, 8),
        new("childGearNextOffset", FieldOwner.Child, 4, 4, 8),
        new("childGearPreviousOffset", FieldOwner.Child, 4, 4, 8),
        new("childGearGaugeStateOffset", FieldOwner.Child, 4, 4, 8),
        new("childUseDriveFor1Offset", FieldOwner.Child, 1, 1, 8),
        new("childMaximumTachometerOffset", FieldOwner.Child, 4, 4, 8),
        new("childElectricMaximumSpeedOffset", FieldOwner.Child, 4, 4, 8),
        new("providerPowerLimitFirstOffset", FieldOwner.Provider, 4, 4, 8),
        new("providerPowerLimitSecondOffset", FieldOwner.Provider, 4, 4, 8),
        new("providerPowerNumeratorOffset", FieldOwner.Provider, 4, 4, 8),
        new("providerPowerDenominatorOffset", FieldOwner.Provider, 4, 4, 8),
        new("providerRegenTargetOffset", FieldOwner.Provider, 4, 4, 8),
        new("providerElectricSpeedOffset", FieldOwner.Provider, 4, 4, 8)
    ];

    private static readonly FrozenSet<string> NativeGaugeProperties = NativeGaugeRvaProperties
        .Concat(NativeGaugeFunctionRvaProperties)
        .Concat(NativeGaugeFieldDefinitions.Select(definition => definition.Name))
        .Concat(new[]
        {
            "registryKeyHash", "registryBucketStride", "hudSubobjectSlotZeroPrologueHex",
            "hudTypeVectorMaximumCount", "hudTypeVectorEntryStride", "hudTypeInstanceCount",
            "hudTypeInstanceStride", "childlessRegenPowerRatioBits",
            "providerPowerDenominatorScaleBits", "providerRegenScaleBits",
            "providerRegenUpperBaseBits", "speedUnitMphValue", "speedUnitKphValue",
            "requiredProviderVtableSlots"
        })
        .ToFrozenSet(StringComparer.Ordinal);

    private NativeHudCompatibilityPack(JsonElement root)
    {
        var properties = ReadPackObject(root);
        SchemaVersion = ReadInt32(properties["schemaVersion"]);
        ReaderVersion = ReadInt32(properties["readerVersion"]);
        if (ReaderVersion != SchemaVersion)
        {
            throw new FormatException("The compatibility pack schema or reader version is unsupported.");
        }

        Id = ReadString(properties["id"]);
        if (!IsSafeId(Id))
        {
            throw new FormatException("The compatibility pack id is invalid.");
        }

        Revision = ReadInt32(properties["revision"]);
        if (Revision <= 0)
        {
            throw new FormatException("The compatibility pack revision must be positive.");
        }

        GameVersion = ReadString(properties["gameVersion"]);
        if (!IsGameVersion(GameVersion))
        {
            throw new FormatException("The game version must contain four numeric version parts.");
        }

        ExecutableLength = ReadInt64(properties["executableLength"]);
        if (ExecutableLength is < 4096 or > MaximumExecutableLength)
        {
            throw new FormatException("The executable length is outside the supported bounds.");
        }

        var hash = ReadString(properties["executableSha256"]);
        if (hash.Length != 64 || !hash.All(char.IsAsciiHexDigit))
        {
            throw new FormatException("The executable SHA-256 must contain exactly 64 hexadecimal digits.");
        }

        ExecutableSha256 = hash.ToUpperInvariant();
        ImageSize = ReadUInt32(properties["imageSize"]);
        if (ImageSize is < 4096 or > MaximumImageSize)
        {
            throw new FormatException("The image size is outside the supported bounds.");
        }

        SourceVectorRva = ReadUInt64(properties["sourceVectorRva"]);
        ThresholdRva = ReadUInt64(properties["thresholdRva"]);
        LeadVtableRva = ReadUInt64(properties["leadVtableRva"]);
        ValidateImageSpan(SourceVectorRva, 24, 8, ImageSize);
        ValidateImageSpan(ThresholdRva, 4, 4, ImageSize);
        ValidateImageSpan(LeadVtableRva, RequiredSlotOffsets.Max() + 8, 8, ImageSize);

        ExpectedThresholdBits = ReadUInt32(properties["expectedThresholdBits"]);
        if (ExpectedThresholdBits != ReaderThresholdBits)
        {
            throw new FormatException("The threshold does not match this reader's semantics.");
        }

        Fields = ReadFields(properties["fields"]);
        RequiredVtableSlots = ReadSlots(properties["requiredVtableSlots"], ImageSize);
        GameplayVisibility = SchemaVersion >= 2
            ? ReadGameplayVisibility(properties["gameplayVisibility"], ImageSize)
            : null;
        NativeGauge = SchemaVersion == 3
            ? ReadNativeGauge(properties["nativeGauge"], ImageSize)
            : null;
    }

    public int SchemaVersion { get; }
    public int ReaderVersion { get; }
    public string Id { get; }
    public int Revision { get; }
    public string GameVersion { get; }
    public long ExecutableLength { get; }
    public string ExecutableSha256 { get; }
    public uint ImageSize { get; }
    public ulong SourceVectorRva { get; }
    public ulong ThresholdRva { get; }
    public ulong LeadVtableRva { get; }
    public uint ExpectedThresholdBits { get; }
    public NativeHudFieldLayout Fields { get; }
    public IReadOnlyDictionary<ulong, ulong> RequiredVtableSlots { get; }
    public NativeGameplayVisibilityLayout? GameplayVisibility { get; }
    public NativeGaugeLayout? NativeGauge { get; }

    public static NativeHudCompatibilityPack Parse(ReadOnlySpan<byte> json)
    {
        if (json.IsEmpty || json.Length > MaximumJsonBytes)
        {
            throw new FormatException("The compatibility pack is empty or exceeds the size limit.");
        }

        try
        {
            using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4
            });
            return new NativeHudCompatibilityPack(document.RootElement);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new FormatException("The compatibility pack is not valid JSON.", exception);
        }
    }

    public bool Matches(string? version, long length, string? sha256) =>
        string.Equals(version?.Trim(), GameVersion, StringComparison.Ordinal) &&
        length == ExecutableLength &&
        string.Equals(sha256?.Trim(), ExecutableSha256, StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, JsonElement> ReadPackObject(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var schemaVersion))
        {
            throw new FormatException("The compatibility pack schema is missing or invalid.");
        }

        var expectedProperties = ReadInt32(schemaVersion) switch
        {
            1 => LegacyPackProperties,
            2 => VersionTwoPackProperties,
            3 => VersionThreePackProperties,
            _ => throw new FormatException("The compatibility pack schema or reader version is unsupported.")
        };
        return ReadObject(root, expectedProperties);
    }

    private static Dictionary<string, JsonElement> ReadObject(
        JsonElement element,
        IReadOnlySet<string> expectedProperties)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("A compatibility pack object has the wrong JSON type.");
        }

        var properties = new Dictionary<string, JsonElement>(expectedProperties.Count, StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expectedProperties.Contains(property.Name) ||
                !properties.TryAdd(property.Name, property.Value))
            {
                throw new FormatException("A compatibility pack object has unknown or duplicate properties.");
            }
        }

        if (properties.Count != expectedProperties.Count)
        {
            throw new FormatException("A compatibility pack object is missing required properties.");
        }

        return properties;
    }

    private static NativeHudFieldLayout ReadFields(JsonElement element)
    {
        var properties = ReadObject(element, FieldProperties);
        var offsets = new Dictionary<string, ulong>(FieldDefinitions.Length, StringComparer.Ordinal);
        foreach (var definition in FieldDefinitions)
        {
            var offset = ReadUInt64(properties[definition.Name]);
            if (offset % definition.Alignment != 0 ||
                offset > MaximumFieldBytes - definition.Width ||
                definition.Owner == FieldOwner.Provider && offset < 8)
            {
                throw new FormatException("A compatibility field is misaligned or outside its object bounds.");
            }

            offsets.Add(definition.Name, offset);
        }

        for (var index = 0; index < FieldDefinitions.Length; index++)
        {
            var current = FieldDefinitions[index];
            var currentOffset = offsets[current.Name];
            for (var previousIndex = 0; previousIndex < index; previousIndex++)
            {
                var previous = FieldDefinitions[previousIndex];
                var previousOffset = offsets[previous.Name];
                if (current.Owner == previous.Owner &&
                    currentOffset < previousOffset + previous.Width &&
                    previousOffset < currentOffset + current.Width &&
                    !IsSharedSecondary(current.Name, previous.Name, currentOffset, previousOffset))
                {
                    throw new FormatException("Compatibility fields overlap without an allowed semantic alias.");
                }
            }
        }

        return new NativeHudFieldLayout(offsets);
    }

    private static bool IsSharedSecondary(string first, string second, ulong firstOffset, ulong secondOffset) =>
        firstOffset == secondOffset &&
        (first == "lcSecondary" && second == "tcrSecondary" ||
         first == "tcrSecondary" && second == "lcSecondary");

    private static NativeGameplayVisibilityLayout ReadGameplayVisibility(JsonElement element, uint imageSize)
    {
        var properties = ReadObject(element, GameplayVisibilityProperties);
        var values = new Dictionary<string, ulong>(GameplayVisibilityProperties.Count, StringComparer.Ordinal);
        var rvas = new HashSet<ulong>();
        foreach (var name in GameplayVisibilityRvaProperties)
        {
            var rva = ReadUInt64(properties[name]);
            ValidateImageSpan(rva, 8, 8, imageSize);
            if (!rvas.Add(rva))
            {
                throw new FormatException("Gameplay visibility RVAs cannot alias different object identities.");
            }

            values.Add(name, rva);
        }

        foreach (var definition in GameplayVisibilityFieldDefinitions)
        {
            var offset = ReadUInt64(properties[definition.Name]);
            ValidateGameplayFieldSpan(offset, definition.Width, definition.Alignment);
            values.Add(definition.Name, offset);
        }

        for (var index = 0; index < GameplayVisibilityFieldDefinitions.Length; index++)
        {
            var current = GameplayVisibilityFieldDefinitions[index];
            var currentOffset = values[current.Name];
            for (var previousIndex = 0; previousIndex < index; previousIndex++)
            {
                var previous = GameplayVisibilityFieldDefinitions[previousIndex];
                var previousOffset = values[previous.Name];
                if (current.Owner == previous.Owner &&
                    currentOffset < previousOffset + previous.Width &&
                    previousOffset < currentOffset + current.Width)
                {
                    throw new FormatException("Gameplay visibility fields overlap within the same object.");
                }
            }
        }

        // The dependency owns this manager inline, not through a pointer. Its
        // complete read span must fit both the manager and its containing object.
        var managerWidth = GameplayVisibilityFieldDefinitions
            .Where(definition => definition.Owner == FieldOwner.TransitionManager)
            .Max(definition => values[definition.Name] + definition.Width);
        ValidateGameplayFieldSpan(values["rootTransitionManagerOffset"], managerWidth, 8);
        return new NativeGameplayVisibilityLayout(values);
    }

    private static NativeGaugeLayout ReadNativeGauge(JsonElement element, uint imageSize)
    {
        var properties = ReadObject(element, NativeGaugeProperties);
        var values = new Dictionary<string, ulong>(NativeGaugeProperties.Count, StringComparer.Ordinal);
        var identityRvas = new HashSet<ulong>();
        foreach (var name in NativeGaugeRvaProperties)
        {
            var rva = ReadUInt64(properties[name]);
            ValidateImageSpan(rva, 8, 8, imageSize);
            if (!identityRvas.Add(rva))
            {
                throw new FormatException("Native gauge RVAs cannot alias different object identities.");
            }

            values.Add(name, rva);
        }

        foreach (var name in NativeGaugeFunctionRvaProperties)
        {
            var rva = ReadUInt64(properties[name]);
            ValidateImageSpan(rva, 1, 1, imageSize);
            if (!identityRvas.Add(rva))
            {
                throw new FormatException("Native gauge code and object RVAs cannot alias.");
            }

            values.Add(name, rva);
        }

        var hash = ReadUInt64(properties["registryKeyHash"]);
        if (hash == 0)
        {
            throw new FormatException("The native gauge registry key hash cannot be zero.");
        }

        values.Add("registryKeyHash", hash);
        foreach (var definition in NativeGaugeFieldDefinitions)
        {
            var offset = ReadUInt64(properties[definition.Name]);
            ValidateNativeGaugeFieldSpan(offset, definition.Width, definition.Alignment, definition.MinimumOffset);
            values.Add(definition.Name, offset);
        }

        ValidateNativeGaugeFieldAliases(values);

        var speedUnitMphValue = ReadUInt32(properties["speedUnitMphValue"]);
        var speedUnitKphValue = ReadUInt32(properties["speedUnitKphValue"]);
        if (speedUnitMphValue > int.MaxValue || speedUnitKphValue > int.MaxValue ||
            speedUnitMphValue == speedUnitKphValue)
        {
            throw new FormatException("The native speed-unit values are invalid.");
        }

        values.Add("speedUnitMphValue", speedUnitMphValue);
        values.Add("speedUnitKphValue", speedUnitKphValue);

        var childlessRegenPowerRatioBits = ReadUInt32(properties["childlessRegenPowerRatioBits"]);
        var childlessRegenPowerRatio = BitConverter.UInt32BitsToSingle(childlessRegenPowerRatioBits);
        if (!float.IsFinite(childlessRegenPowerRatio) || childlessRegenPowerRatio is < 0 or > 1)
        {
            throw new FormatException("The childless electric regen/power ratio is invalid.");
        }

        values.Add("childlessRegenPowerRatioBits", childlessRegenPowerRatioBits);

        var powerDenominatorScaleBits = ReadUInt32(properties["providerPowerDenominatorScaleBits"]);
        var powerDenominatorScale = BitConverter.UInt32BitsToSingle(powerDenominatorScaleBits);
        if (!float.IsFinite(powerDenominatorScale) || powerDenominatorScale <= 0)
        {
            throw new FormatException("The native gauge power denominator scale is invalid.");
        }

        values.Add("providerPowerDenominatorScaleBits", powerDenominatorScaleBits);

        var regenScaleBits = ReadUInt32(properties["providerRegenScaleBits"]);
        var regenScale = BitConverter.UInt32BitsToSingle(regenScaleBits);
        if (!float.IsFinite(regenScale) || regenScale >= 0)
        {
            throw new FormatException("The native gauge regeneration scale is invalid.");
        }

        values.Add("providerRegenScaleBits", regenScaleBits);

        var regenUpperBaseBits = ReadUInt32(properties["providerRegenUpperBaseBits"]);
        var regenUpperBase = BitConverter.UInt32BitsToSingle(regenUpperBaseBits);
        if (!float.IsFinite(regenUpperBase) || regenUpperBase is < 0 or > 1)
        {
            throw new FormatException("The native gauge regeneration upper base is invalid.");
        }

        values.Add("providerRegenUpperBaseBits", regenUpperBaseBits);

        var registryBucketStride = ReadUInt64(properties["registryBucketStride"]);
        ValidateStride(
            registryBucketStride,
            values["registryBucketBoundaryOffset"] + 8,
            values["registryBucketNodeOffset"] + 8,
            "registry bucket");
        values.Add("registryBucketStride", registryBucketStride);

        var vectorMaximumCount = ReadUInt64(properties["hudTypeVectorMaximumCount"]);
        if (vectorMaximumCount is 0 or > 1024)
        {
            throw new FormatException("The native gauge HUD vector count limit is outside the supported bounds.");
        }

        values.Add("hudTypeVectorMaximumCount", vectorMaximumCount);
        var vectorEntryStride = ReadUInt64(properties["hudTypeVectorEntryStride"]);
        ValidateStride(
            vectorEntryStride,
            values["hudTypeTokenOffset"] + 8,
            values["hudTypeInstancesCapacityOffset"] + 8,
            "HUD type vector entry");
        values.Add("hudTypeVectorEntryStride", vectorEntryStride);

        var instanceCount = ReadUInt64(properties["hudTypeInstanceCount"]);
        if (instanceCount != 1)
        {
            throw new FormatException("The native gauge HUD type must resolve to exactly one shared instance.");
        }

        values.Add("hudTypeInstanceCount", instanceCount);
        var instanceStride = ReadUInt64(properties["hudTypeInstanceStride"]);
        ValidateStride(
            instanceStride,
            values["hudTypeInstanceObjectOffset"] + 8,
            values["hudTypeInstanceControlOffset"] + 8,
            "HUD type instance");
        values.Add("hudTypeInstanceStride", instanceStride);

        var prologue = ReadString(properties["hudSubobjectSlotZeroPrologueHex"]);
        if (prologue.Length != 10 || !prologue.All(char.IsAsciiHexDigit))
        {
            throw new FormatException("The native gauge function prologue must contain exactly five hexadecimal bytes.");
        }

        var slots = ReadRequiredSlots(
            properties["requiredProviderVtableSlots"],
            RequiredNativeGaugeProviderSlotOffsets,
            imageSize,
            "native gauge provider");
        if (slots.Values.Any(target => !identityRvas.Add(target)))
        {
            throw new FormatException("Native gauge provider targets cannot alias another guarded identity.");
        }

        ValidateContainingSpan(
            values["hudSubobjectOffset"],
            values["hudTypeVectorOffset"] + values["hudTypeVectorCapacityOffset"] + 8,
            "native gauge HUD subobject");

        return new NativeGaugeLayout(values, prologue.ToUpperInvariant(), slots);
    }

    private static void ValidateNativeGaugeFieldAliases(IReadOnlyDictionary<string, ulong> values)
    {
        for (var index = 0; index < NativeGaugeFieldDefinitions.Length; index++)
        {
            var current = NativeGaugeFieldDefinitions[index];
            var currentOffset = values[current.Name];
            for (var previousIndex = 0; previousIndex < index; previousIndex++)
            {
                var previous = NativeGaugeFieldDefinitions[previousIndex];
                var previousOffset = values[previous.Name];
                if (current.Owner == previous.Owner &&
                    currentOffset < previousOffset + previous.Width &&
                    previousOffset < currentOffset + current.Width)
                {
                    throw new FormatException("Native gauge fields overlap within the same object.");
                }
            }
        }
    }

    private static void ValidateNativeGaugeFieldSpan(ulong offset, ulong width, ulong alignment, ulong minimumOffset)
    {
        if (offset < minimumOffset || offset % alignment != 0 || width > MaximumFieldBytes ||
            offset > MaximumFieldBytes - width)
        {
            throw new FormatException("A native gauge field is misaligned or exceeds its object bounds.");
        }
    }

    private static void ValidateStride(ulong stride, ulong firstRequiredWidth, ulong secondRequiredWidth, string name)
    {
        var requiredWidth = Math.Max(firstRequiredWidth, secondRequiredWidth);
        if (stride < requiredWidth || stride > 4096 || stride % 8 != 0)
        {
            throw new FormatException($"The native gauge {name} stride is invalid.");
        }
    }

    private static void ValidateContainingSpan(ulong offset, ulong width, string name)
    {
        if (width > MaximumFieldBytes || offset > MaximumFieldBytes - width)
        {
            throw new FormatException($"The {name} exceeds its containing object bounds.");
        }
    }

    private static void ValidateGameplayFieldSpan(ulong offset, ulong width, ulong alignment)
    {
        if (offset < 8 || offset % alignment != 0 || width > MaximumFieldBytes ||
            offset > MaximumFieldBytes - width)
        {
            throw new FormatException("A gameplay visibility field overlaps its vtable, is misaligned, or exceeds its object bounds.");
        }
    }

    private static FrozenDictionary<ulong, ulong> ReadSlots(JsonElement element, uint imageSize) =>
        ReadRequiredSlots(element, RequiredSlotOffsets, imageSize, "compatibility pack");

    private static FrozenDictionary<ulong, ulong> ReadRequiredSlots(
        JsonElement element,
        IReadOnlySet<ulong> requiredOffsets,
        uint imageSize,
        string guardName)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != requiredOffsets.Count)
        {
            throw new FormatException($"The {guardName} must contain every required vtable guard.");
        }

        var slots = new Dictionary<ulong, ulong>(requiredOffsets.Count);
        foreach (var entry in element.EnumerateArray())
        {
            var properties = ReadObject(entry, SlotProperties);
            var offset = ReadUInt64(properties["offset"]);
            var targetRva = ReadUInt64(properties["targetRva"]);
            if (!requiredOffsets.Contains(offset) || !slots.TryAdd(offset, targetRva))
            {
                throw new FormatException("The vtable guards have an unsupported or duplicate slot offset.");
            }

            // Function entry points are not guaranteed to have data-pointer alignment.
            ValidateImageSpan(targetRva, 1, 1, imageSize);
        }

        return slots.ToFrozenDictionary();
    }

    private static void ValidateImageSpan(ulong rva, ulong width, ulong alignment, uint imageSize)
    {
        if (rva == 0 || rva % alignment != 0 || rva >= imageSize || width > imageSize - rva)
        {
            throw new FormatException("A compatibility RVA is misaligned or outside the executable image.");
        }
    }

    private static bool IsSafeId(string id)
    {
        if (id.Length is < 1 or > 80 || !char.IsAsciiLetterOrDigit(id[0]) || id[^1] == '.' ||
            id.Contains("..", StringComparison.Ordinal) ||
            id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
        {
            return false;
        }

        var stem = id.Split('.')[0];
        return !stem.Equals("CON", StringComparison.OrdinalIgnoreCase) &&
               !stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) &&
               !stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) &&
               !stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) &&
               !(stem.Length == 4 && stem[3] is >= '1' and <= '9' &&
                 (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                  stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsGameVersion(string version)
    {
        if (version.Length is < 7 or > 23)
        {
            return false;
        }

        var parts = version.Split('.');
        return parts.Length == 4 && parts.All(part =>
            part.Length is >= 1 and <= 5 &&
            part.All(char.IsAsciiDigit) &&
            ushort.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _));
    }

    private static string ReadString(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
            ? element.GetString()!
            : throw new FormatException("A compatibility value must be a JSON string.");

    private static int ReadInt32(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value)
            ? value
            : throw new FormatException("A compatibility value must be a 32-bit integer.");

    private static long ReadInt64(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var value)
            ? value
            : throw new FormatException("A compatibility value must be a 64-bit integer.");

    private static uint ReadUInt32(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetUInt32(out var value)
            ? value
            : throw new FormatException("A compatibility value must be an unsigned 32-bit integer.");

    private static ulong ReadUInt64(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetUInt64(out var value)
            ? value
            : throw new FormatException("A compatibility value must be an unsigned 64-bit integer.");

    private enum FieldOwner
    {
        Source,
        Provider,
        Wheel,
        UiService,
        UiDependency,
        TransitionManager,
        UiPage,
        RegistryWrapper,
        RegistryContext,
        RegistryBucket,
        RegistryNode,
        SharedControl,
        Hud,
        HudSubobject,
        HudVector,
        HudVectorEntry,
        HudTypeInstance,
        Outer,
        Child,
        SpeedUnit
    }

    private readonly record struct FieldDefinition(string Name, FieldOwner Owner, ulong Width, ulong Alignment);
    private readonly record struct NativeGaugeFieldDefinition(
        string Name,
        FieldOwner Owner,
        ulong Width,
        ulong Alignment,
        ulong MinimumOffset);
}

public sealed class NativeHudFieldLayout
{
    internal NativeHudFieldLayout(IReadOnlyDictionary<string, ulong> offsets)
    {
        SourceProvider = offsets["sourceProvider"];
        SourceCarOrdinal = offsets["sourceCarOrdinal"];
        ProviderRpm = offsets["providerRpm"];
        ProviderSimRedlineAngularVelocity = offsets["providerSimRedlineAngularVelocity"];
        ProviderTachometerMaximumAngularVelocity = offsets["providerTachometerMaximumAngularVelocity"];
        LocalPlayerFlag = offsets["localPlayerFlag"];
        LocalPlayerProviderFlag = offsets["localPlayerProviderFlag"];
        StmState = offsets["stmState"];
        AbsState = offsets["absState"];
        StmAvailable = offsets["stmAvailable"];
        TcrAvailable = offsets["tcrAvailable"];
        AbsAvailable = offsets["absAvailable"];
        LcAvailable = offsets["lcAvailable"];
        LcPrimary = offsets["lcPrimary"];
        LcMode = offsets["lcMode"];
        LcSecondary = offsets["lcSecondary"];
        TcrSecondary = offsets["tcrSecondary"];
        TcrPrimary = offsets["tcrPrimary"];
        TcrTertiary = offsets["tcrTertiary"];
        TcrWheelValues = offsets["tcrWheelValues"];
        FirstWheelPointer = offsets["firstWheelPointer"];
        SecondWheelPointer = offsets["secondWheelPointer"];
        ThirdWheelPointer = offsets["thirdWheelPointer"];
        WheelId = offsets["wheelId"];
    }

    public ulong SourceProvider { get; }
    public ulong SourceCarOrdinal { get; }
    public ulong ProviderRpm { get; }
    public ulong ProviderSimRedlineAngularVelocity { get; }
    public ulong ProviderTachometerMaximumAngularVelocity { get; }
    public ulong LocalPlayerFlag { get; }
    public ulong LocalPlayerProviderFlag { get; }
    public ulong StmState { get; }
    public ulong AbsState { get; }
    public ulong StmAvailable { get; }
    public ulong TcrAvailable { get; }
    public ulong AbsAvailable { get; }
    public ulong LcAvailable { get; }
    public ulong LcPrimary { get; }
    public ulong LcMode { get; }
    public ulong LcSecondary { get; }
    public ulong TcrSecondary { get; }
    public ulong TcrPrimary { get; }
    public ulong TcrTertiary { get; }
    public ulong TcrWheelValues { get; }
    public ulong FirstWheelPointer { get; }
    public ulong SecondWheelPointer { get; }
    public ulong ThirdWheelPointer { get; }
    public ulong WheelId { get; }
}
