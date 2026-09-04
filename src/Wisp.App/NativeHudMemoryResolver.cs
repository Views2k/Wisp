using Wisp.Core;

namespace Wisp.App;

public interface IReadOnlyProcessMemory
{
    bool TryReadByte(ulong address, out byte value);
    bool TryReadUInt32(ulong address, out uint value);
    bool TryReadUInt64(ulong address, out ulong value);
    bool TryReadSingle(ulong address, out float value);

    bool TryReadBytes(ulong address, Span<byte> destination)
    {
        destination.Clear();
        return false;
    }
}

public sealed class NativeHudMemoryResolver
{
    private const int MaximumSourceCount = 128;
    private ulong SourceProviderOffset => _pack.Fields.SourceProvider;
    private ulong SourceCarOrdinalOffset => _pack.Fields.SourceCarOrdinal;
    private ulong ProviderRpmOffset => _pack.Fields.ProviderRpm;
    private ulong ProviderSimRedlineAngularVelocityOffset => _pack.Fields.ProviderSimRedlineAngularVelocity;
    private ulong ProviderTachometerMaximumAngularVelocityOffset => _pack.Fields.ProviderTachometerMaximumAngularVelocity;
    private ulong LocalPlayerFlagOffset => _pack.Fields.LocalPlayerFlag;
    private ulong LocalPlayerProviderFlagOffset => _pack.Fields.LocalPlayerProviderFlag;
    // Semantic labels were established with isolated LC, ABS, TCR, and STM
    // transitions on the supported build; do not reorder them in a renderer.
    private ulong StmStateOffset => _pack.Fields.StmState;
    private ulong AbsStateOffset => _pack.Fields.AbsState;
    private ulong StmAvailableOffset => _pack.Fields.StmAvailable;
    private ulong TcrAvailableOffset => _pack.Fields.TcrAvailable;
    private ulong AbsAvailableOffset => _pack.Fields.AbsAvailable;
    private ulong LcAvailableOffset => _pack.Fields.LcAvailable;
    private ulong LcPrimaryOffset => _pack.Fields.LcPrimary;
    private ulong LcModeOffset => _pack.Fields.LcMode;
    private ulong LcSecondaryOffset => _pack.Fields.LcSecondary;
    private ulong TcrSecondaryOffset => _pack.Fields.TcrSecondary;
    private ulong TcrPrimaryOffset => _pack.Fields.TcrPrimary;
    private ulong TcrTertiaryOffset => _pack.Fields.TcrTertiary;
    private ulong TcrWheelValuesOffset => _pack.Fields.TcrWheelValues;
    private ulong FirstWheelPointerOffset => _pack.Fields.FirstWheelPointer;
    private ulong SecondWheelPointerOffset => _pack.Fields.SecondWheelPointer;
    private ulong ThirdWheelPointerOffset => _pack.Fields.ThirdWheelPointer;
    private ulong WheelIdOffset => _pack.Fields.WheelId;

    private ulong _cachedSource;
    private readonly NativeHudCompatibilityPack _pack;
    private readonly NativeGaugeDirectResolver _nativeGaugeResolver;

    public NativeHudMemoryResolver(NativeHudCompatibilityPack? pack = null)
    {
        _pack = pack ?? NativeHudBuildContract.BuiltIn;
        _nativeGaugeResolver = new NativeGaugeDirectResolver(_pack);
    }

    public NativeHudSnapshot Resolve(
        IReadOnlyProcessMemory memory,
        ulong moduleBase,
        int carOrdinal,
        float currentEngineRpm,
        float maximumEngineRpm,
        ulong generation,
        bool forceSourceAudit = false,
        bool isElectric = false)
    {
        ArgumentNullException.ThrowIfNull(memory);
        if (carOrdinal <= 0 || !IsPointer(moduleBase) || !IsAddressRange(moduleBase, _pack.ImageSize))
        {
            Reset();
            return Unavailable(NativeAssistProviderStatus.InvalidProvider, generation, carOrdinal);
        }

        if (!TryReadThreshold(memory, moduleBase, out var threshold))
        {
            // A bad assist threshold must not invalidate independently verified tach data.
            threshold = float.NaN;
        }

        if (!forceSourceAudit && _cachedSource != 0 &&
            TryResolveSource(
                memory,
                moduleBase,
                _cachedSource,
                carOrdinal,
                currentEngineRpm,
                maximumEngineRpm,
                threshold,
                generation,
                isElectric,
                forceSourceAudit,
                out var cachedResult))
        {
            return cachedResult;
        }

        _cachedSource = 0;
        if (!TryReadSourceVector(memory, moduleBase, out var sources))
        {
            return Unavailable(NativeAssistProviderStatus.InvalidSourceVector, generation, carOrdinal);
        }

        var localPlayerCandidates = new List<(ulong Source, ulong Provider)>();
        foreach (var source in sources)
        {
            if (TryGetLocalPlayerProvider(memory, moduleBase, source, out var provider))
            {
                localPlayerCandidates.Add((source, provider));
            }
        }

        if (localPlayerCandidates.Count == 0)
        {
            return Unavailable(NativeAssistProviderStatus.PlayerNotUnique, generation, carOrdinal);
        }

        // FH6 can briefly retain more than one local-player HUD source across
        // menu/race transitions. Disambiguate those sources using the live Data
        // Out vehicle identity before deciding that the player is ambiguous.
        var matches = new List<(ulong Source, ulong Provider)>();
        var failureStatus = NativeAssistProviderStatus.TelemetryMismatch;
        foreach (var candidate in localPlayerCandidates)
        {
            if (TryMatchTelemetryIdentity(
                    memory,
                    candidate.Source,
                    candidate.Provider,
                    carOrdinal,
                    currentEngineRpm,
                    maximumEngineRpm,
                    out var candidateFailure))
            {
                matches.Add(candidate);
            }
            else if (candidateFailure == NativeAssistProviderStatus.ReadFailure ||
                     failureStatus == NativeAssistProviderStatus.TelemetryMismatch)
            {
                failureStatus = candidateFailure;
            }
        }

        if (matches.Count == 0)
        {
            return Unavailable(failureStatus, generation, carOrdinal);
        }

        if (matches.Count != 1)
        {
            return Unavailable(NativeAssistProviderStatus.PlayerNotUnique, generation, carOrdinal);
        }

        _cachedSource = matches[0].Source;
        return ResolveValidatedProvider(
            memory,
            moduleBase,
            matches[0].Source,
            matches[0].Provider,
            carOrdinal,
            currentEngineRpm,
            maximumEngineRpm,
            threshold,
            generation,
            isElectric,
            forceSourceAudit);
    }

    public void Reset()
    {
        _cachedSource = 0;
        _nativeGaugeResolver.Reset();
    }

    /// <summary>
    /// Refreshes only the volatile native gauge block from the already validated
    /// HUD ownership chain. The caller still schedules a complete source audit at
    /// the normal bounded interval.
    /// </summary>
    public NativeHudSnapshot RefreshNativeGauge(
        IReadOnlyProcessMemory memory,
        ulong moduleBase,
        NativeHudSnapshot baseline,
        ulong generation,
        bool isElectric,
        bool forceStructuralValidation = false)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(baseline);
        if (_cachedSource == 0 || baseline.CarOrdinal <= 0 ||
            !IsPointer(moduleBase) || !IsAddressRange(moduleBase, _pack.ImageSize))
        {
            return baseline;
        }

        var nativeGauge = _nativeGaugeResolver.Read(
            memory,
            moduleBase,
            _cachedSource,
            isElectric,
            forceStructuralValidation);
        if (!nativeGauge.IsAvailable)
        {
            return baseline;
        }

        var assists = nativeGauge.HasHeadlightState &&
                      (!baseline.Assists.HeadlightStateAvailable ||
                       baseline.Assists.AreHeadlightsOn != nativeGauge.AreHeadlightsOn)
            ? baseline.Assists with
            {
                HeadlightStateAvailable = true,
                AreHeadlightsOn = nativeGauge.AreHeadlightsOn
            }
            : baseline.Assists;

        if (!isElectric)
        {
            return nativeGauge.HasNeedlePair
                ? baseline with
                {
                    Generation = generation,
                    Assists = assists,
                    TachometerMaximumRpm = nativeGauge.TachometerMaximum,
                    NativeNeedleAngleDegrees = nativeGauge.NeedleAngleDegrees,
                    NativeNeedleBlurAmount = nativeGauge.NeedleBlurAmount,
                    NativeGaugeObservedTimestamp = nativeGauge.ObservedTimestamp
                }
                : baseline;
        }

        return baseline with
        {
            Generation = generation,
            Assists = assists,
            NativeNeedleAngleDegrees = nativeGauge.HasNeedlePair
                ? nativeGauge.NeedleAngleDegrees
                : double.NaN,
            NativeNeedleBlurAmount = nativeGauge.HasNeedlePair
                ? nativeGauge.NeedleBlurAmount
                : double.NaN,
            NativeRegenFillAmount = nativeGauge.RegenFillAmount,
            NativePowerFillAmount = nativeGauge.PowerFillAmount,
            NativeRegenPowerRatio = nativeGauge.RegenPowerRatio,
            NativeElectricMaximumSpeed = nativeGauge.ElectricMaximumSpeed,
            NativeGaugeObservedTimestamp = nativeGauge.ObservedTimestamp,
            ElectricGearState = nativeGauge.ElectricGearState,
            DisplayedSpeedState = nativeGauge.DisplayedSpeedState
        };
    }

    private bool TryReadThreshold(
        IReadOnlyProcessMemory memory,
        ulong moduleBase,
        out float threshold)
    {
        if (!memory.TryReadSingle(moduleBase + _pack.ThresholdRva, out threshold))
        {
            return false;
        }

        return BitConverter.SingleToUInt32Bits(threshold) == _pack.ExpectedThresholdBits;
    }

    private bool TryReadSourceVector(
        IReadOnlyProcessMemory memory,
        ulong moduleBase,
        out IReadOnlyList<ulong> sources)
    {
        sources = Array.Empty<ulong>();
        var vector = moduleBase + _pack.SourceVectorRva;
        if (!memory.TryReadUInt64(vector, out var begin) ||
            !memory.TryReadUInt64(vector + 8, out var end) ||
            !memory.TryReadUInt64(vector + 16, out var capacity) ||
            !IsPointer(begin) || !IsPointer(end, allowOnePastEnd: true) ||
            !IsPointer(capacity, allowOnePastEnd: true) ||
            begin > end || end > capacity || (end - begin) % 8 != 0)
        {
            return false;
        }

        var count = (end - begin) / 8;
        if (count == 0 || count > MaximumSourceCount)
        {
            return false;
        }

        var result = new List<ulong>((int)count);
        for (ulong index = 0; index < count; index++)
        {
            if (!memory.TryReadUInt64(begin + (index * 8), out var source) || !IsPointer(source))
            {
                return false;
            }

            result.Add(source);
        }

        sources = result;
        return true;
    }

    private bool TryResolveSource(
        IReadOnlyProcessMemory memory,
        ulong moduleBase,
        ulong source,
        int carOrdinal,
        float currentEngineRpm,
        float maximumEngineRpm,
        float threshold,
        ulong generation,
        bool isElectric,
        bool forceStructuralValidation,
        out NativeHudSnapshot snapshot)
    {
        snapshot = NativeHudSnapshot.Unavailable();
        if (!TryGetLocalPlayerProvider(memory, moduleBase, source, out var provider))
        {
            return false;
        }

        snapshot = ResolveValidatedProvider(
            memory,
            moduleBase,
            source,
            provider,
            carOrdinal,
            currentEngineRpm,
            maximumEngineRpm,
            threshold,
            generation,
            isElectric,
            forceStructuralValidation);
        return snapshot.HasAvailableCapabilities;
    }

    private bool TryGetLocalPlayerProvider(
        IReadOnlyProcessMemory memory,
        ulong moduleBase,
        ulong source,
        out ulong provider)
    {
        provider = 0;
        return IsPointer(source) && IsAddressRange(source, NativeHudCompatibilityPack.MaximumFieldBytes) &&
               memory.TryReadUInt64(source + SourceProviderOffset, out provider) &&
               IsPointer(provider) && IsAddressRange(provider, NativeHudCompatibilityPack.MaximumFieldBytes) &&
               HasExactProviderContract(memory, moduleBase, provider) &&
               memory.TryReadByte(provider + LocalPlayerFlagOffset, out var localPlayer) &&
               memory.TryReadByte(provider + LocalPlayerProviderFlagOffset, out var localProvider) &&
               localPlayer == 1 && localProvider == 1;
    }

    private bool TryMatchTelemetryIdentity(
        IReadOnlyProcessMemory memory,
        ulong source,
        ulong provider,
        int carOrdinal,
        float currentEngineRpm,
        float maximumEngineRpm,
        out NativeAssistProviderStatus failureStatus)
    {
        failureStatus = NativeAssistProviderStatus.ReadFailure;
        if (!memory.TryReadUInt32(source + SourceCarOrdinalOffset, out var sourceCarOrdinal) ||
            !memory.TryReadSingle(provider + ProviderRpmOffset, out var angularVelocity) ||
            !memory.TryReadSingle(
                provider + ProviderTachometerMaximumAngularVelocityOffset,
                out var tachometerMaximumAngularVelocity))
        {
            return false;
        }

        failureStatus = NativeAssistProviderStatus.TelemetryMismatch;
        if (sourceCarOrdinal != (uint)carOrdinal ||
            !ProviderRpmMatches(angularVelocity, currentEngineRpm, maximumEngineRpm))
        {
            return false;
        }

        if (!TryValidateMaximum(
                tachometerMaximumAngularVelocity,
                maximumEngineRpm,
                out _,
                out failureStatus))
        {
            return false;
        }

        failureStatus = NativeAssistProviderStatus.Ready;
        return true;
    }

    private NativeHudSnapshot ResolveValidatedProvider(
        IReadOnlyProcessMemory memory,
        ulong moduleBase,
        ulong source,
        ulong provider,
        int carOrdinal,
        float currentEngineRpm,
        float maximumEngineRpm,
        float threshold,
        ulong generation,
        bool isElectric,
        bool forceStructuralValidation)
    {
        if (!memory.TryReadUInt32(source + SourceCarOrdinalOffset, out var sourceCarOrdinal) ||
            !memory.TryReadSingle(provider + ProviderRpmOffset, out var angularVelocity) ||
            !memory.TryReadSingle(
                provider + ProviderTachometerMaximumAngularVelocityOffset,
                out var tachometerMaximumAngularVelocity))
        {
            return Unavailable(NativeAssistProviderStatus.ReadFailure, generation, carOrdinal);
        }

        // These are shared identity guards, not optional capability checks.
        if (sourceCarOrdinal != (uint)carOrdinal ||
            !ProviderRpmMatches(angularVelocity, currentEngineRpm, maximumEngineRpm))
        {
            return Unavailable(NativeAssistProviderStatus.TelemetryMismatch, generation, carOrdinal);
        }

        if (!TryValidateMaximum(tachometerMaximumAngularVelocity, maximumEngineRpm, out _, out var maximumFailure))
        {
            return Unavailable(maximumFailure, generation, carOrdinal);
        }

        var exactRedline = ExactRedlineResult.Unavailable(ExactRedlineStatus.ReadFailure);
        var tachometerMaximumRpm = 0d;
        var validationFailure = NativeAssistProviderStatus.ReadFailure;
        var tachometerAvailable =
            memory.TryReadSingle(provider + ProviderSimRedlineAngularVelocityOffset, out var redline) &&
            TryValidateTachometerState(
                redline,
                tachometerMaximumAngularVelocity,
                maximumEngineRpm,
                out exactRedline,
                out tachometerMaximumRpm,
                out validationFailure);

        var assists = float.IsFinite(threshold) && TryReadRawState(memory, provider, out var raw)
            ? NativeAssistStateCalculator.Calculate(raw, threshold, generation, carOrdinal)
            : NativeAssistSnapshot.Unavailable(
                NativeAssistProviderStatus.ReadFailure, generation, carOrdinal);
        var snapshot = new NativeHudSnapshot(
            tachometerAvailable,
            generation,
            carOrdinal,
            tachometerAvailable ? NativeAssistProviderStatus.Ready : validationFailure,
            exactRedline,
            tachometerAvailable ? tachometerMaximumRpm : 0,
            assists);

        var nativeGauge = _nativeGaugeResolver.Read(
            memory,
            moduleBase,
            source,
            isElectric,
            forceStructuralValidation);
        if (!nativeGauge.IsAvailable)
        {
            return snapshot;
        }

        snapshot = snapshot with
        {
            NativeGaugeObservedTimestamp = nativeGauge.ObservedTimestamp
        };

        if (nativeGauge.HasHeadlightState)
        {
            snapshot = snapshot with
            {
                Assists = snapshot.Assists with
                {
                    HeadlightStateAvailable = true,
                    AreHeadlightsOn = nativeGauge.AreHeadlightsOn
                }
            };
        }

        if (!isElectric && nativeGauge.HasNeedlePair)
        {
            return snapshot with
            {
                TachometerMaximumRpm = nativeGauge.TachometerMaximum,
                NativeNeedleAngleDegrees = nativeGauge.NeedleAngleDegrees,
                NativeNeedleBlurAmount = nativeGauge.NeedleBlurAmount
            };
        }

        if (!isElectric)
        {
            return snapshot;
        }

        return snapshot with
        {
            NativeNeedleAngleDegrees = nativeGauge.HasNeedlePair
                ? nativeGauge.NeedleAngleDegrees
                : double.NaN,
            NativeNeedleBlurAmount = nativeGauge.HasNeedlePair
                ? nativeGauge.NeedleBlurAmount
                : double.NaN,
            NativeRegenFillAmount = nativeGauge.RegenFillAmount,
            NativePowerFillAmount = nativeGauge.PowerFillAmount,
            NativeRegenPowerRatio = nativeGauge.RegenPowerRatio,
            NativeElectricMaximumSpeed = nativeGauge.ElectricMaximumSpeed,
            ElectricGearState = nativeGauge.ElectricGearState,
            DisplayedSpeedState = nativeGauge.DisplayedSpeedState
        };
    }

    private bool HasExactProviderContract(
        IReadOnlyProcessMemory memory,
        ulong moduleBase,
        ulong provider)
    {
        if (!memory.TryReadUInt64(provider, out var vtable) ||
            vtable != moduleBase + _pack.LeadVtableRva)
        {
            return false;
        }

        foreach (var slot in _pack.RequiredVtableSlots)
        {
            if (!memory.TryReadUInt64(vtable + slot.Key, out var target) ||
                target != moduleBase + slot.Value)
            {
                return false;
            }
        }

        return true;
    }

    private bool TryReadRawState(
        IReadOnlyProcessMemory memory,
        ulong provider,
        out NativeAssistRawState raw)
    {
        raw = default;
        if (!memory.TryReadByte(provider + AbsAvailableOffset, out var absAvailable) ||
            !memory.TryReadByte(provider + TcrAvailableOffset, out var tcrAvailable) ||
            !memory.TryReadByte(provider + StmAvailableOffset, out var stmAvailable) ||
            !memory.TryReadByte(provider + LcAvailableOffset, out var lcAvailable) ||
            !memory.TryReadUInt32(provider + AbsStateOffset, out var absState) ||
            !memory.TryReadSingle(provider + TcrPrimaryOffset, out var tcrPrimary) ||
            !memory.TryReadSingle(provider + TcrSecondaryOffset, out var tcrSecondary) ||
            !memory.TryReadSingle(provider + TcrTertiaryOffset, out var tcrTertiary) ||
            !TryReadMappedTcrWheels(memory, provider, out var wheelValues) ||
            !memory.TryReadUInt32(provider + StmStateOffset, out var stmState) ||
            !memory.TryReadSingle(provider + LcPrimaryOffset, out var lcPrimary) ||
            !memory.TryReadUInt32(provider + LcModeOffset, out var lcMode) ||
            !memory.TryReadSingle(provider + LcSecondaryOffset, out var lcSecondary) ||
            IsInfinity(tcrPrimary, tcrSecondary, tcrTertiary, lcPrimary, lcSecondary) ||
            wheelValues.Any(float.IsInfinity))
        {
            return false;
        }

        raw = new NativeAssistRawState(
            absAvailable != 0,
            tcrAvailable != 0,
            stmAvailable != 0,
            lcAvailable != 0,
            absState,
            tcrPrimary,
            tcrSecondary,
            tcrTertiary,
            wheelValues,
            stmState,
            lcPrimary,
            lcMode,
            lcSecondary);
        return true;
    }

    private bool TryReadMappedTcrWheels(
        IReadOnlyProcessMemory memory,
        ulong provider,
        out IReadOnlyList<float> mappedValues)
    {
        mappedValues = Array.Empty<float>();
        if (!TryReadWheelId(memory, provider + FirstWheelPointerOffset, out var firstId) ||
            !TryReadWheelId(memory, provider + SecondWheelPointerOffset, out var secondId) ||
            !TryReadWheelId(memory, provider + ThirdWheelPointerOffset, out var thirdId))
        {
            return false;
        }

        var rawValues = new float[4];
        for (var index = 0; index < rawValues.Length; index++)
        {
            if (!memory.TryReadSingle(provider + TcrWheelValuesOffset + ((ulong)index * 4), out rawValues[index]))
            {
                return false;
            }
        }

        var result = new float[4];
        for (var wheelId = 0; wheelId < result.Length; wheelId++)
        {
            var index = NativeAssistStateCalculator.MapWheelIndex(wheelId, firstId, secondId, thirdId);
            result[wheelId] = rawValues[index];
        }

        mappedValues = result;
        return true;
    }

    private bool TryReadWheelId(
        IReadOnlyProcessMemory memory,
        ulong pointerAddress,
        out int wheelId)
    {
        wheelId = 0;
        return memory.TryReadUInt64(pointerAddress, out var wheel) &&
               IsPointer(wheel) && IsAddressRange(wheel, WheelIdOffset + 4) &&
               memory.TryReadUInt32(wheel + WheelIdOffset, out var value) &&
               value <= 3 &&
               (wheelId = (int)value) >= 0;
    }

    private static bool ProviderRpmMatches(
        float angularVelocity,
        float currentEngineRpm,
        float maximumEngineRpm)
    {
        if (!float.IsFinite(angularVelocity) || angularVelocity < 0 ||
            !float.IsFinite(currentEngineRpm) || currentEngineRpm < 0)
        {
            return false;
        }

        var providerRpm = angularVelocity * 60 / (2 * MathF.PI);
        var tolerance = Math.Max(
            750,
            float.IsFinite(maximumEngineRpm) && maximumEngineRpm > 0
                ? maximumEngineRpm * 0.125
                : 750);
        return Math.Abs(providerRpm - currentEngineRpm) <= tolerance;
    }

    private static bool TryValidateMaximum(
        float angularVelocity,
        float telemetryMaximumRpm,
        out double maximumRpm,
        out NativeAssistProviderStatus failure)
    {
        maximumRpm = 0;
        failure = NativeAssistProviderStatus.InvalidProvider;
        if (!float.IsFinite(angularVelocity) || angularVelocity <= 0 ||
            !float.IsFinite(telemetryMaximumRpm) || telemetryMaximumRpm <= 0)
        {
            return false;
        }

        maximumRpm = ExactRedlineResult.AngularVelocityToRpm(angularVelocity);
        failure = NativeAssistProviderStatus.TelemetryMismatch;
        return double.IsFinite(maximumRpm) &&
               Math.Abs(maximumRpm - telemetryMaximumRpm) <= Math.Max(2d, telemetryMaximumRpm * 0.00025d);
    }

    private static bool TryValidateTachometerState(
        float simRedlineAngularVelocity,
        float tachometerMaximumAngularVelocity,
        float telemetryMaximumRpm,
        out ExactRedlineResult exactRedline,
        out double tachometerMaximumRpm,
        out NativeAssistProviderStatus failureStatus)
    {
        exactRedline = ExactRedlineResult.Unavailable(ExactRedlineStatus.InvalidProvider);
        tachometerMaximumRpm = 0;
        failureStatus = NativeAssistProviderStatus.InvalidProvider;
        if (!float.IsFinite(simRedlineAngularVelocity) || simRedlineAngularVelocity <= 0 ||
            !float.IsFinite(tachometerMaximumAngularVelocity) || tachometerMaximumAngularVelocity <= 0 ||
            !float.IsFinite(telemetryMaximumRpm) || telemetryMaximumRpm <= 0)
        {
            return false;
        }

        tachometerMaximumRpm = ExactRedlineResult.AngularVelocityToRpm(
            tachometerMaximumAngularVelocity);
        var maximumToleranceRpm = Math.Max(2d, telemetryMaximumRpm * 0.00025d);
        if (!double.IsFinite(tachometerMaximumRpm) ||
            Math.Abs(tachometerMaximumRpm - telemetryMaximumRpm) > maximumToleranceRpm)
        {
            tachometerMaximumRpm = 0;
            failureStatus = NativeAssistProviderStatus.TelemetryMismatch;
            return false;
        }

        exactRedline = ExactRedlineResult.Exact(simRedlineAngularVelocity);
        if (!exactRedline.IsExact || exactRedline.Rpm < 100 ||
            exactRedline.Rpm > tachometerMaximumRpm + maximumToleranceRpm)
        {
            exactRedline = ExactRedlineResult.Unavailable(ExactRedlineStatus.InvalidProvider);
            tachometerMaximumRpm = 0;
            return false;
        }

        failureStatus = NativeAssistProviderStatus.Ready;
        return true;
    }

    private static bool IsPointer(ulong address, bool allowOnePastEnd = false) =>
        (allowOnePastEnd && address == 0) ||
        IsAddressRange(address, allowOnePastEnd ? 1UL : 8UL) && (address & 7) == 0;

    private static bool IsAddressRange(ulong address, ulong bytes) =>
        address is >= 0x10000 and <= 0x00007FFFFFFFFFFF && bytes > 0 &&
        bytes - 1 <= 0x00007FFFFFFFFFFF - address;

    private static bool IsInfinity(params float[] values) => values.Any(float.IsInfinity);

    private static NativeHudSnapshot Unavailable(
        NativeAssistProviderStatus status,
        ulong generation,
        int carOrdinal) =>
        NativeHudSnapshot.Unavailable(status, generation, carOrdinal);
}
