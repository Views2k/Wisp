namespace Wisp.Core;

public sealed record CalibrationOptions
{
    public const int DefaultMinimumSamples = 12;
    public const int DefaultReplacementMinimumSamples = 96;
    public const int DefaultReplacementWindowSamples = 192;
    public const int DefaultBaselineConfirmationSamples = 24;

    public int MinimumSamples { get; init; } = DefaultMinimumSamples;
    public int MaximumSamples { get; init; } = 121;
    public double MinimumGroundSpeedMetersPerSecond { get; init; } = 3.0;
    public double MaximumGroundSpeedMetersPerSecond { get; init; } = 125.0;
    public double MinimumWheelSpeedRadiansPerSecond { get; init; } = 8.0;
    // Forza reports a normalized grip signal: zero is full grip and values
    // above one indicate grip loss. These limits admit normal near-zero noise
    // while remaining far below the loss-of-grip region.
    public double MaximumAbsoluteSlipRatio { get; init; } = 0.12;
    public int MaximumAbsoluteSteeringInput { get; init; } = 16;
    public double MaximumAbsoluteLateralAccelerationMetersPerSecondSquared { get; init; } = 1.5;
    public double MaximumAbsoluteLongitudinalAccelerationMetersPerSecondSquared { get; init; } = 3.5;
    public double MaximumDecelerationMetersPerSecondSquared { get; init; } = 0.75;
    public byte MaximumBrakeInput { get; init; } = 0;
    public double MinimumLoadedSuspensionTravel { get; init; } = 0.03;
    public double MaximumWheelDisagreementFraction { get; init; } = 0.10;
    public double MaximumCandidateRadiusSpreadFraction { get; init; } = 0.015;
    public double MinimumCandidateConsensusFraction { get; init; } = 0.60;
    public int ReplacementMinimumSamples { get; init; } = DefaultReplacementMinimumSamples;
    public int ReplacementWindowSamples { get; init; } = DefaultReplacementWindowSamples;
    public double ReplacementMinimumConsensusFraction { get; init; } = 0.90;
    public double ReplacementMaximumRadiusSpreadFraction { get; init; } = 0.003;
    public double ReplacementMeaningfulDifferenceFraction { get; init; } = 0.0025;
    public int BaselineConfirmationSamples { get; init; } = DefaultBaselineConfirmationSamples;
    public double ReplacementMaximumAbsoluteSlipRatio { get; init; } = 0.02;
    public int ReplacementMaximumAbsoluteSteeringInput { get; init; } = 5;
    public double ReplacementMaximumAbsoluteLateralAccelerationMetersPerSecondSquared { get; init; } = 0.75;
    public double ReplacementMaximumAbsoluteLongitudinalAccelerationMetersPerSecondSquared { get; init; } = 1.0;
    public byte ReplacementMaximumBrakeInput { get; init; } = 0;
    public double ReplacementMaximumWheelDisagreementFraction { get; init; } = 0.015;
}

public sealed record CalibrationSnapshot(
    int CarOrdinal,
    double RadiusMeters,
    int SampleCount,
    DrivetrainType? Drivetrain = null,
    int CalibrationRevision = 0,
    double? FrontRadiusMeters = null,
    double? RearRadiusMeters = null);

public readonly record struct CalibrationResult(
    double? RadiusMeters,
    double? ProvisionalRadiusMeters,
    double Confidence,
    int AcceptedSamples,
    bool SampleAccepted,
    string RejectionReason,
    bool IsTrusted,
    RollingRadii? TrustedRadii = null,
    RollingRadii? ProvisionalRadii = null)
{
    public bool IsCalibrated => IsTrusted;
}

public sealed class RollingRadiusEstimator
{
    public const int CurrentCalibrationRevision = 3;
    public const double MinimumPlausibleRadiusMeters = 0.15;
    public const double MaximumPlausibleRadiusMeters = 0.75;

    private readonly CalibrationOptions _options;
    private readonly Dictionary<int, Profile> _profiles = new();
    private int? _activeCarOrdinal;

    public RollingRadiusEstimator(CalibrationOptions? options = null)
    {
        _options = options ?? new CalibrationOptions();
    }

    public CalibrationResult Observe(VehicleState state, bool isFresh = true)
    {
        if (!_profiles.TryGetValue(state.CarOrdinal, out var profile))
        {
            profile = new Profile(
                _options.MaximumSamples,
                _options.ReplacementWindowSamples);
            _profiles.Add(state.CarOrdinal, profile);
        }

        ActivateProfile(state, profile);

        var rejection = Validate(
            state,
            isFresh,
            out var angularSpeeds,
            out var wheelDisagreement,
            out var maximumSlipRatio);
        if (rejection is not null)
        {
            if (!isFresh || !state.IsRaceOn)
            {
                profile.ClearReplacementEvidence();
            }

            return Result(profile, false, rejection);
        }

        var candidateRadii = CandidateRadii(state, angularSpeeds);
        if (!candidateRadii.IsPlausible)
        {
            // An anomalous frame must not hide a speed that already has a
            // trusted tire profile. Fail closed only while no trusted geometry
            // exists; the speed model separately holds its last valid value if
            // the instantaneous wheel rotation itself is implausible.
            return Result(
                profile,
                false,
                "Implausible radius",
                forceUnavailable: !profile.TrustedRadii.HasValue);
        }

        if (profile.TrustedRadii is { } baseline)
        {
            // A trusted radius is eligible for replacement only at the start
            // of a telemetry session. Learning the first profile never opens a
            // second calibration pass in that same session.
            if (!profile.ReplacementEligible)
            {
                return Result(profile, true, string.Empty);
            }

            if (!IsUltraCleanReplacementSample(
                    state,
                    wheelDisagreement,
                    maximumSlipRatio))
            {
                return Result(profile, false, "Trusted replacement sample not ultra-clean");
            }

            var relativeDifference = RelativeDifference(
                candidateRadii,
                baseline,
                state.Drivetrain);
            if (relativeDifference < _options.ReplacementMeaningfulDifferenceFraction)
            {
                profile.ObserveBaselineMatch(_options.BaselineConfirmationSamples);
                return Result(profile, true, string.Empty);
            }

            profile.ResetBaselineMatches();
            var replacementConsensusReached = profile.AddReplacementCandidate(
                candidateRadii,
                _options.ReplacementMaximumRadiusSpreadFraction,
                _options.ReplacementMinimumSamples,
                _options.ReplacementWindowSamples,
                _options.ReplacementMinimumConsensusFraction,
                out var replacementCandidateAccepted,
                out var replacementRadii,
                out var replacementWindowExhausted);
            if (replacementConsensusReached)
            {
                profile.SetTrusted(replacementRadii);
                return Result(profile, true, string.Empty);
            }

            if (replacementWindowExhausted)
            {
                profile.EndReplacementAttempt();
                return Result(
                    profile,
                    replacementCandidateAccepted,
                    "Trusted replacement window exhausted");
            }

            return Result(
                profile,
                replacementCandidateAccepted,
                "Trusted replacement consensus pending");
        }

        var consensusReached = profile.AddInitialCandidate(
            candidateRadii,
            _options.MaximumCandidateRadiusSpreadFraction,
            _options.MinimumSamples,
            _options.MinimumCandidateConsensusFraction,
            out var initialCandidateAccepted,
            out var learnedRadii);
        if (!consensusReached)
        {
            return Result(
                profile,
                initialCandidateAccepted,
                initialCandidateAccepted ? "Stable tire consensus pending" : "Candidate radius outlier");
        }

        profile.SetTrusted(learnedRadii);

        return Result(profile, true, string.Empty);
    }

    public CalibrationResult Get(int carOrdinal)
    {
        return _profiles.TryGetValue(carOrdinal, out var profile)
            ? Result(profile, false, string.Empty)
            : new CalibrationResult(null, null, 0, 0, false, string.Empty, false);
    }

    public bool ResetProfile(int carOrdinal)
    {
        if (_activeCarOrdinal == carOrdinal)
        {
            _activeCarOrdinal = null;
        }

        return _profiles.Remove(carOrdinal);
    }

    public void EndTelemetrySession()
    {
        _activeCarOrdinal = null;
    }

    public IReadOnlyList<CalibrationSnapshot> ExportSnapshots() => _profiles
        .Where(pair => pair.Value.TrustedRadii.HasValue && pair.Value.Drivetrain.HasValue)
        .Select(pair => new CalibrationSnapshot(
            pair.Key,
            pair.Value.TrustedRadii!.Value.Representative(pair.Value.Drivetrain!.Value),
            pair.Value.Samples.Count,
            pair.Value.Drivetrain,
            CurrentCalibrationRevision,
            pair.Value.TrustedRadii.Value.FrontMeters,
            pair.Value.TrustedRadii.Value.RearMeters))
        .ToArray();

    public void ImportSnapshots(IEnumerable<CalibrationSnapshot> snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            if (snapshot.CarOrdinal <= 0 ||
                snapshot.CalibrationRevision != CurrentCalibrationRevision ||
                snapshot.Drivetrain is not { } drivetrain ||
                !Enum.IsDefined(drivetrain) ||
                snapshot.SampleCount < _options.MinimumSamples ||
                !TrySnapshotRadii(snapshot, drivetrain, out var radii))
            {
                continue;
            }

            var profile = new Profile(
                _options.MaximumSamples,
                _options.ReplacementWindowSamples)
            {
                TrustedRadii = radii,
                Drivetrain = drivetrain
            };
            for (var i = 0; i < Math.Clamp(snapshot.SampleCount, _options.MinimumSamples, _options.MaximumSamples); i++)
            {
                profile.AddHistorical(radii);
            }

            _profiles[snapshot.CarOrdinal] = profile;
        }
    }

    public static bool IsPlausibleRadius(double radiusMeters) =>
        double.IsFinite(radiusMeters) &&
        radiusMeters is >= MinimumPlausibleRadiusMeters and <= MaximumPlausibleRadiusMeters;

    public static bool TrySnapshotRadii(
        CalibrationSnapshot snapshot,
        DrivetrainType drivetrain,
        out RollingRadii radii)
    {
        if (snapshot.FrontRadiusMeters is { } front &&
            snapshot.RearRadiusMeters is { } rear)
        {
            radii = new RollingRadii(front, rear);
            return radii.IsPlausible;
        }

        // Older scalar profiles are exact for the driven axle on 2WD cars.
        // They cannot represent a staggered AWD setup and must be relearned.
        radii = RollingRadii.Uniform(snapshot.RadiusMeters);
        return drivetrain != DrivetrainType.AllWheelDrive && radii.IsPlausible;
    }

    private void ActivateProfile(VehicleState state, Profile profile)
    {
        if (_activeCarOrdinal == state.CarOrdinal && profile.Drivetrain == state.Drivetrain)
        {
            return;
        }

        profile.BeginSession(state.Drivetrain);
        _activeCarOrdinal = state.CarOrdinal;
    }

    private string? Validate(
        VehicleState state,
        bool isFresh,
        out AxleAngularSpeeds angularSpeeds,
        out double wheelDisagreement,
        out double maximumSlipRatio)
    {
        angularSpeeds = default;
        wheelDisagreement = double.PositiveInfinity;
        maximumSlipRatio = double.PositiveInfinity;
        if (!isFresh)
        {
            return "Stale telemetry";
        }

        if (!state.IsRaceOn || state.CarOrdinal <= 0)
        {
            return "Not driving";
        }

        var groundSpeed = Math.Abs((double)state.GroundSpeedMetersPerSecond);
        if (!double.IsFinite(groundSpeed) ||
            groundSpeed < _options.MinimumGroundSpeedMetersPerSecond ||
            groundSpeed > _options.MaximumGroundSpeedMetersPerSecond)
        {
            return "Ground speed outside calibration range";
        }

        if (!state.WheelRotationRadiansPerSecond.AreFinite() ||
            !state.TireSlipRatio.AreFinite() ||
            !state.TireSlipAngle.AreFinite())
        {
            return "Non-finite wheel telemetry";
        }

        GetDrivenWheelMetrics(
            state,
            out angularSpeeds,
            out wheelDisagreement,
            out maximumSlipRatio);

        if (maximumSlipRatio > _options.MaximumAbsoluteSlipRatio)
        {
            return "Tire slip ratio";
        }

        // TireSlipAngle is not a longitudinal wheelspin measurement. Aggressive
        // alignment can keep it high even while the tire is rolling cleanly,
        // so using its absolute value as a gate can deadlock calibration. Real
        // drifting remains excluded by longitudinal slip, steering, lateral
        // acceleration, acceleration/brake, axle load, and wheel agreement.
        if (Math.Abs((int)state.Steering) > _options.MaximumAbsoluteSteeringInput)
        {
            return "Steering input";
        }

        if (Math.Abs(state.LateralAccelerationMetersPerSecondSquared) >
            _options.MaximumAbsoluteLateralAccelerationMetersPerSecondSquared)
        {
            return "Cornering acceleration";
        }

        if (Math.Abs(state.LongitudinalAccelerationMetersPerSecondSquared) >
            _options.MaximumAbsoluteLongitudinalAccelerationMetersPerSecondSquared)
        {
            return "Longitudinal acceleration";
        }

        // Learning while the driven tires are being slowed makes
        // ground-speed / wheel-speed too large and permanently overstates the
        // rolling radius. Even light braking can create a 1-3 mph positive
        // display error, so initial calibration is limited to neutral or
        // accelerating longitudinal states.
        if (state.LongitudinalAccelerationMetersPerSecondSquared <
            -_options.MaximumDecelerationMetersPerSecondSquared)
        {
            return "Longitudinal deceleration";
        }

        if (state.Brake > _options.MaximumBrakeInput)
        {
            return "Braking input";
        }

        if (IsDrivenAxleUnloaded(state, _options.MinimumLoadedSuspensionTravel))
        {
            return "Driven axle unloaded";
        }

        if (MinimumDrivenAngularSpeed(state.Drivetrain, angularSpeeds) <
            _options.MinimumWheelSpeedRadiansPerSecond)
        {
            return "Wheel speed too low";
        }

        return wheelDisagreement > _options.MaximumWheelDisagreementFraction
            ? "Wheel speeds disagree"
            : null;
    }

    private bool IsUltraCleanReplacementSample(
        VehicleState state,
        double wheelDisagreement,
        double maximumSlipRatio) =>
        maximumSlipRatio <= _options.ReplacementMaximumAbsoluteSlipRatio &&
        Math.Abs((int)state.Steering) <= _options.ReplacementMaximumAbsoluteSteeringInput &&
        Math.Abs(state.LateralAccelerationMetersPerSecondSquared) <=
            _options.ReplacementMaximumAbsoluteLateralAccelerationMetersPerSecondSquared &&
        Math.Abs(state.LongitudinalAccelerationMetersPerSecondSquared) <=
            _options.ReplacementMaximumAbsoluteLongitudinalAccelerationMetersPerSecondSquared &&
        state.Brake <= _options.ReplacementMaximumBrakeInput &&
        wheelDisagreement <= _options.ReplacementMaximumWheelDisagreementFraction;

    private CalibrationResult Result(
        Profile profile,
        bool accepted,
        string reason,
        bool forceUnavailable = false)
    {
        var confidence = profile.TrustedRadii.HasValue
            ? 1.0
            : Math.Min(1.0, profile.Samples.Count / (double)(_options.MinimumSamples * 2));
        var trustedRadii = profile.TrustedRadii;
        var isTrusted = !forceUnavailable &&
                        trustedRadii is { IsPlausible: true };
        var provisionalRadii = isTrusted || forceUnavailable
            ? null
            : profile.EffectiveRadii();
        var drivetrain = profile.Drivetrain ?? DrivetrainType.RearWheelDrive;
        return new CalibrationResult(
            isTrusted ? trustedRadii!.Value.Representative(drivetrain) : null,
            provisionalRadii?.Representative(drivetrain),
            confidence,
            profile.Samples.Count,
            accepted,
            reason,
            isTrusted,
            isTrusted ? trustedRadii : null,
            provisionalRadii);
    }

    private static bool IsDrivenAxleUnloaded(VehicleState state, double minimumTravel)
    {
        var suspension = state.NormalizedSuspensionTravel;
        var frontUnloaded = suspension.FrontLeft < minimumTravel &&
                            suspension.FrontRight < minimumTravel;
        var rearUnloaded = suspension.RearLeft < minimumTravel &&
                           suspension.RearRight < minimumTravel;
        return state.Drivetrain switch
        {
            DrivetrainType.FrontWheelDrive => frontUnloaded,
            DrivetrainType.RearWheelDrive => rearUnloaded,
            DrivetrainType.AllWheelDrive => frontUnloaded || rearUnloaded,
            _ => true
        };
    }

    private static AxleAngularSpeeds DrivenAxleSpeeds(WheelValues wheels) => new(
        Math.Abs(((double)wheels.FrontLeft + wheels.FrontRight) * 0.5),
        Math.Abs(((double)wheels.RearLeft + wheels.RearRight) * 0.5));

    private static RollingRadii CandidateRadii(VehicleState state, AxleAngularSpeeds speeds)
    {
        var groundSpeed = Math.Abs((double)state.GroundSpeedMetersPerSecond);
        return state.Drivetrain switch
        {
            DrivetrainType.FrontWheelDrive => RollingRadii.Uniform(groundSpeed / speeds.Front),
            DrivetrainType.RearWheelDrive => RollingRadii.Uniform(groundSpeed / speeds.Rear),
            DrivetrainType.AllWheelDrive => new RollingRadii(
                groundSpeed / speeds.Front,
                groundSpeed / speeds.Rear),
            _ => new RollingRadii(double.NaN, double.NaN)
        };
    }

    private static double MinimumDrivenAngularSpeed(
        DrivetrainType drivetrain,
        AxleAngularSpeeds speeds) => drivetrain switch
        {
            DrivetrainType.FrontWheelDrive => speeds.Front,
            DrivetrainType.RearWheelDrive => speeds.Rear,
            DrivetrainType.AllWheelDrive => Math.Min(speeds.Front, speeds.Rear),
            _ => 0
        };

    private static double RelativeDifference(
        RollingRadii candidate,
        RollingRadii baseline,
        DrivetrainType drivetrain) => drivetrain switch
        {
            DrivetrainType.FrontWheelDrive => RelativeDifference(candidate.FrontMeters, baseline.FrontMeters),
            DrivetrainType.RearWheelDrive => RelativeDifference(candidate.RearMeters, baseline.RearMeters),
            DrivetrainType.AllWheelDrive => Math.Max(
                RelativeDifference(candidate.FrontMeters, baseline.FrontMeters),
                RelativeDifference(candidate.RearMeters, baseline.RearMeters)),
            _ => double.PositiveInfinity
        };

    private static double RelativeDifference(double first, double second) =>
        Math.Abs(first - second) / Math.Max(Math.Abs(second), double.Epsilon);

    private static void GetDrivenWheelMetrics(
        VehicleState state,
        out AxleAngularSpeeds angularSpeeds,
        out double wheelDisagreement,
        out double maximumSlipRatio)
    {
        var wheels = state.WheelRotationRadiansPerSecond;
        var slipRatios = state.TireSlipRatio;
        angularSpeeds = DrivenAxleSpeeds(wheels);

        if (state.Drivetrain == DrivetrainType.AllWheelDrive)
        {
            wheelDisagreement = Math.Max(
                PairDisagreement(wheels.FrontLeft, wheels.FrontRight),
                PairDisagreement(wheels.RearLeft, wheels.RearRight));
            maximumSlipRatio = slipRatios.MaximumAbsolute();
            return;
        }

        var useFrontWheels = state.Drivetrain == DrivetrainType.FrontWheelDrive;
        var leftSpeed = (double)(useFrontWheels ? wheels.FrontLeft : wheels.RearLeft);
        var rightSpeed = (double)(useFrontWheels ? wheels.FrontRight : wheels.RearRight);
        wheelDisagreement = PairDisagreement(leftSpeed, rightSpeed);
        maximumSlipRatio = Math.Max(
            Math.Abs((double)(useFrontWheels ? slipRatios.FrontLeft : slipRatios.RearLeft)),
            Math.Abs((double)(useFrontWheels ? slipRatios.FrontRight : slipRatios.RearRight)));
    }

    private static double PairDisagreement(double leftSpeed, double rightSpeed)
    {
        var average = Math.Abs((leftSpeed + rightSpeed) * 0.5);
        return average > 0
            ? Math.Abs(leftSpeed - rightSpeed) / average
            : double.PositiveInfinity;
    }

    private readonly record struct AxleAngularSpeeds(double Front, double Rear);

    private sealed class Profile
    {
        private readonly int _maximumSamples;
        private readonly RollingRadii[] _pointScratch;
        private readonly double[] _valueScratch;
        private readonly int[] _countScratch;

        public Profile(int maximumSamples, int replacementWindowSamples)
        {
            _maximumSamples = maximumSamples;
            var scratchCapacity = Math.Max(
                1,
                Math.Max(maximumSamples, replacementWindowSamples));
            _pointScratch = new RollingRadii[scratchCapacity];
            _valueScratch = new double[scratchCapacity];
            _countScratch = new int[scratchCapacity];
        }

        public List<RollingRadii> Samples { get; } = new();
        public List<RollingRadii> CandidateWindow { get; } = new();
        public List<RollingRadii> ReplacementCandidateWindow { get; } = new();
        public RollingRadii? TrustedRadii { get; set; }
        public DrivetrainType? Drivetrain { get; set; }
        public bool ReplacementEligible { get; private set; }
        private int BaselineMatchCount { get; set; }

        public void BeginSession(DrivetrainType drivetrain)
        {
            if (Drivetrain is { } knownDrivetrain && knownDrivetrain != drivetrain)
            {
                Reset();
            }

            Drivetrain = drivetrain;
            ClearReplacementEvidence();
            ReplacementEligible = TrustedRadii.HasValue;
            // A telemetry gap does not change trusted tire geometry. It does
            // begin a fresh, bounded opportunity to verify a changed setup.
        }

        public bool AddInitialCandidate(
            RollingRadii value,
            double maximumSpreadFraction,
            int minimumSamples,
            double minimumConsensusFraction,
            out bool candidateAccepted,
            out RollingRadii consensus)
        {
            return AddCandidate(
                CandidateWindow,
                value,
                maximumSpreadFraction,
                minimumSamples,
                minimumConsensusFraction,
                out candidateAccepted,
                out consensus,
                updateSamplesOnConsensusOnly: false);
        }

        public bool AddReplacementCandidate(
            RollingRadii value,
            double maximumSpreadFraction,
            int minimumSamples,
            int maximumWindowSamples,
            double minimumConsensusFraction,
            out bool candidateAccepted,
            out RollingRadii consensus,
            out bool windowExhausted)
        {
            if (ReplacementCandidateWindow.Count < maximumWindowSamples)
            {
                ReplacementCandidateWindow.Add(value);
            }

            var bestCluster = FindBestCluster(
                ReplacementCandidateWindow,
                maximumSpreadFraction);
            candidateAccepted = bestCluster.Contains(value);
            consensus = Median(ReplacementCandidateWindow, bestCluster);
            windowExhausted = ReplacementCandidateWindow.Count >= maximumWindowSamples;
            if (ReplacementCandidateWindow.Count < minimumSamples)
            {
                return false;
            }

            var requiredConsensus = Math.Max(
                minimumSamples,
                (int)Math.Ceiling(ReplacementCandidateWindow.Count * minimumConsensusFraction));
            if (bestCluster.Count < requiredConsensus)
            {
                return false;
            }

            CopyClusterToSamples(ReplacementCandidateWindow, bestCluster);
            return true;
        }

        public void ObserveBaselineMatch(int requiredSamples)
        {
            ReplacementCandidateWindow.Clear();
            BaselineMatchCount++;
            if (BaselineMatchCount >= requiredSamples)
            {
                CloseReplacementEligibility();
            }
        }

        public void ResetBaselineMatches() => BaselineMatchCount = 0;

        public void AddHistorical(RollingRadii value)
        {
            if (Samples.Count == _maximumSamples)
            {
                Samples.RemoveAt(0);
            }

            Samples.Add(value);
        }

        public RollingRadii? EffectiveRadii()
        {
            return TrustedRadii ?? (Samples.Count > 0 ? Median(Samples) : null);
        }

        public void SetTrusted(RollingRadii radii)
        {
            TrustedRadii = radii;
            CandidateWindow.Clear();
            CloseReplacementEligibility();
        }

        public void ClearReplacementEvidence()
        {
            ReplacementCandidateWindow.Clear();
            BaselineMatchCount = 0;
        }

        public void EndReplacementAttempt() => CloseReplacementEligibility();

        private void CloseReplacementEligibility()
        {
            ReplacementEligible = false;
            ClearReplacementEvidence();
        }

        private bool AddCandidate(
            List<RollingRadii> window,
            RollingRadii value,
            double maximumSpreadFraction,
            int minimumSamples,
            double minimumConsensusFraction,
            out bool candidateAccepted,
            out RollingRadii consensus,
            bool updateSamplesOnConsensusOnly)
        {
            var maximumWindow = Math.Min(
                _maximumSamples,
                Math.Max(minimumSamples, minimumSamples * 2));
            AddBounded(window, value, maximumWindow);
            var bestCluster = FindBestCluster(window, maximumSpreadFraction);
            candidateAccepted = bestCluster.Contains(value);
            var requiredConsensus = Math.Max(
                minimumSamples,
                (int)Math.Ceiling(window.Count * minimumConsensusFraction));
            var reached = bestCluster.Count >= requiredConsensus;

            if (!updateSamplesOnConsensusOnly || reached)
            {
                CopyClusterToSamples(window, bestCluster);
            }

            consensus = Median(window, bestCluster);
            return reached;
        }

        private ClusterBounds FindBestCluster(
            List<RollingRadii> values,
            double maximumSpreadFraction)
        {
            return Drivetrain == DrivetrainType.AllWheelDrive
                ? FindBestAwdCluster(values, maximumSpreadFraction)
                : FindBestUniformCluster(values, maximumSpreadFraction);
        }

        private ClusterBounds FindBestUniformCluster(
            List<RollingRadii> values,
            double maximumSpreadFraction)
        {
            for (var index = 0; index < values.Count; index++)
            {
                _valueScratch[index] = values[index].FrontMeters;
            }

            Array.Sort(_valueScratch, 0, values.Count);
            var bestStart = 0;
            var bestCount = 0;
            var start = 0;
            for (var end = 0; end < values.Count; end++)
            {
                while (start < end &&
                       !FitsSpread(
                           _valueScratch[start],
                           _valueScratch[end],
                           maximumSpreadFraction))
                {
                    start++;
                }

                var count = end - start + 1;
                if (count > bestCount)
                {
                    bestStart = start;
                    bestCount = count;
                }
            }

            var minimum = _valueScratch[bestStart];
            var maximum = _valueScratch[bestStart + bestCount - 1];
            return new ClusterBounds(minimum, maximum, minimum, maximum, bestCount);
        }

        private ClusterBounds FindBestAwdCluster(
            List<RollingRadii> values,
            double maximumSpreadFraction)
        {
            var count = values.Count;
            for (var index = 0; index < count; index++)
            {
                _pointScratch[index] = values[index];
                _valueScratch[index] = values[index].RearMeters;
            }

            Array.Sort(
                _pointScratch,
                0,
                count,
                RollingRadiiFrontComparer.Instance);
            Array.Sort(_valueScratch, 0, count);

            var uniqueRearCount = 0;
            for (var index = 0; index < count; index++)
            {
                if (uniqueRearCount == 0 ||
                    _valueScratch[index] != _valueScratch[uniqueRearCount - 1])
                {
                    _valueScratch[uniqueRearCount++] = _valueScratch[index];
                }
            }

            var best = ClusterBounds.Empty;
            var frontEnd = -1;
            for (var frontStart = 0; frontStart < count; frontStart++)
            {
                frontEnd = Math.Max(frontEnd, frontStart);
                while (frontEnd + 1 < count &&
                       FitsSpread(
                           _pointScratch[frontStart].FrontMeters,
                           _pointScratch[frontEnd + 1].FrontMeters,
                           maximumSpreadFraction))
                {
                    frontEnd++;
                }

                Array.Clear(_countScratch, 0, uniqueRearCount);
                for (var pointIndex = frontStart; pointIndex <= frontEnd; pointIndex++)
                {
                    var rearIndex = Array.BinarySearch(
                        _valueScratch,
                        0,
                        uniqueRearCount,
                        _pointScratch[pointIndex].RearMeters);
                    _countScratch[rearIndex]++;
                }

                var rearEnd = 0;
                var pointsInRearWindow = 0;
                for (var rearStart = 0; rearStart < uniqueRearCount; rearStart++)
                {
                    rearEnd = Math.Max(rearEnd, rearStart);
                    while (rearEnd < uniqueRearCount &&
                           FitsSpread(
                               _valueScratch[rearStart],
                               _valueScratch[rearEnd],
                               maximumSpreadFraction))
                    {
                        pointsInRearWindow += _countScratch[rearEnd];
                        rearEnd++;
                    }

                    if (pointsInRearWindow > best.Count)
                    {
                        best = new ClusterBounds(
                            _pointScratch[frontStart].FrontMeters,
                            _pointScratch[frontEnd].FrontMeters,
                            _valueScratch[rearStart],
                            _valueScratch[rearEnd - 1],
                            pointsInRearWindow);
                    }

                    pointsInRearWindow -= _countScratch[rearStart];
                }
            }

            return best;
        }

        private RollingRadii Median(List<RollingRadii> values) => new(
            Median(values, front: true),
            Median(values, front: false));

        private RollingRadii Median(
            List<RollingRadii> values,
            ClusterBounds cluster) => new(
                Median(values, cluster, front: true),
                Median(values, cluster, front: false));

        private double Median(List<RollingRadii> values, bool front)
        {
            for (var index = 0; index < values.Count; index++)
            {
                _valueScratch[index] = front
                    ? values[index].FrontMeters
                    : values[index].RearMeters;
            }

            return MedianScratch(values.Count);
        }

        private double Median(
            List<RollingRadii> values,
            ClusterBounds cluster,
            bool front)
        {
            var count = 0;
            foreach (var value in values)
            {
                if (cluster.Contains(value))
                {
                    _valueScratch[count++] = front
                        ? value.FrontMeters
                        : value.RearMeters;
                }
            }

            return MedianScratch(count);
        }

        private double MedianScratch(int count)
        {
            Array.Sort(_valueScratch, 0, count);
            var middle = count / 2;
            return count % 2 == 0
                ? (_valueScratch[middle - 1] + _valueScratch[middle]) * 0.5
                : _valueScratch[middle];
        }

        private void CopyClusterToSamples(
            List<RollingRadii> source,
            ClusterBounds cluster)
        {
            Samples.Clear();
            var matchingToSkip = Math.Max(0, cluster.Count - _maximumSamples);
            foreach (var value in source)
            {
                if (!cluster.Contains(value))
                {
                    continue;
                }

                if (matchingToSkip > 0)
                {
                    matchingToSkip--;
                    continue;
                }

                Samples.Add(value);
            }
        }

        private static void AddBounded(
            List<RollingRadii> values,
            RollingRadii value,
            int maximumCount)
        {
            if (values.Count >= maximumCount)
            {
                values.RemoveAt(0);
            }

            values.Add(value);
        }

        private static bool FitsSpread(
            double minimum,
            double maximum,
            double maximumSpreadFraction)
        {
            var center = (minimum + maximum) * 0.5;
            return center > 0 && (maximum - minimum) / center <= maximumSpreadFraction;
        }

        private readonly record struct ClusterBounds(
            double MinimumFront,
            double MaximumFront,
            double MinimumRear,
            double MaximumRear,
            int Count)
        {
            public static ClusterBounds Empty => new(
                double.PositiveInfinity,
                double.NegativeInfinity,
                double.PositiveInfinity,
                double.NegativeInfinity,
                0);

            public bool Contains(RollingRadii value) =>
                value.FrontMeters >= MinimumFront &&
                value.FrontMeters <= MaximumFront &&
                value.RearMeters >= MinimumRear &&
                value.RearMeters <= MaximumRear;
        }

        private sealed class RollingRadiiFrontComparer : IComparer<RollingRadii>
        {
            public static RollingRadiiFrontComparer Instance { get; } = new();

            public int Compare(RollingRadii left, RollingRadii right)
            {
                var frontComparison = left.FrontMeters.CompareTo(right.FrontMeters);
                return frontComparison != 0
                    ? frontComparison
                    : left.RearMeters.CompareTo(right.RearMeters);
            }
        }

        public void Reset()
        {
            Samples.Clear();
            CandidateWindow.Clear();
            ReplacementCandidateWindow.Clear();
            TrustedRadii = null;
            Drivetrain = null;
            ReplacementEligible = false;
            BaselineMatchCount = 0;
        }
    }
}
