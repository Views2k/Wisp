using System.IO;
using System.Text.Json.Nodes;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class ShiftCalibrationStoreTests
{
    private const string Build = "calibration-store-build";

    [Fact]
    public void ReloadRecalculatesTargetsFromSavedCurveInsteadOfTrustingSuppliedTargets()
    {
        using var temp = new TempDirectory();
        var store = new ShiftCalibrationStore(temp.Path);
        var result = Calibration();
        var forged = result with
        {
            Gears = [result.Gears[0] with { EstimatedTargetRpm = 1234 }, result.Gears[1]]
        };

        Assert.True(store.Save(Build, forged, Commit));
        var restored = new ShiftCalibrationStore(temp.Path).Load(Build, result.Context);

        Assert.NotNull(restored);
        Assert.True(restored.Ready);
        Assert.Equal(AccelerationShiftStatus.EstimatedCrossover, restored.Gears[0].Status);
        Assert.Equal(20_000d / 3, restored.Gears[0].EstimatedTargetRpm!.Value, 6);
        Assert.Equal(AccelerationShiftStatus.NoNextGear, restored.Gears[1].Status);
        Assert.Equal(result.Profile!.Samples.ToArray(), restored.Profile!.Samples.ToArray());
        Assert.Equal(result.AcceptedSamples, restored.AcceptedSamples);
        Assert.Equal(result.ConfirmingUpshifts, restored.ConfirmingUpshifts);
        Assert.Null(restored.Profile.VerifiedOperatingCeilingRpm);
        Assert.Null(restored.EmpiricalUpperRpm);
        Assert.Single(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public void DifferentBuildOrFingerprintDoesNotReuseSavedCalibration()
    {
        using var temp = new TempDirectory();
        var store = new ShiftCalibrationStore(temp.Path);
        var result = Calibration();
        Assert.True(store.Save(Build, result, Commit));

        Assert.Null(store.Load("different-build", result.Context));
        Assert.Null(store.Load(Build, Context(fingerprint: "different-tune")));
        Assert.Null(store.Load(Build, Context(car: 101)));
        Assert.NotNull(store.Load(Build, result.Context));
        Assert.Single(Directory.GetFiles(temp.Path));
    }

    [Theory]
    [InlineData("Build")]
    [InlineData("Fingerprint")]
    [InlineData("CarOrdinal")]
    public void CorrectFileNameDoesNotOverrideMismatchedIdentityInsideTheFile(string field)
    {
        using var temp = new TempDirectory();
        var store = new ShiftCalibrationStore(temp.Path);
        var result = Calibration();
        Assert.True(store.Save(Build, result, Commit));
        var path = Assert.Single(Directory.GetFiles(temp.Path, "*.json"));
        var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        if (field == "CarOrdinal") document[field] = 101;
        else document[field] = "different-identity";
        File.WriteAllText(path, document.ToJsonString());

        Assert.Null(store.Load(Build, result.Context));
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("curve-gap")]
    [InlineData("nan")]
    [InlineData("oversize")]
    [InlineData("unconfirmed")]
    [InlineData("unsupported-schema")]
    public void InvalidStoredEvidenceNeverBecomesReady(string scenario)
    {
        using var temp = new TempDirectory();
        var store = new ShiftCalibrationStore(temp.Path);
        var result = Calibration();
        Assert.True(store.Save(Build, result, Commit));
        var path = Assert.Single(Directory.GetFiles(temp.Path, "*.json"));
        var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        switch (scenario)
        {
            case "malformed":
                File.WriteAllText(path, "{");
                break;
            case "curve-gap":
                var samples = document["Samples"]!.AsArray();
                samples.RemoveAt(11);
                samples.RemoveAt(10);
                File.WriteAllText(path, document.ToJsonString());
                break;
            case "nan":
                document["Samples"]![1]!["Torque"] = "NaN";
                File.WriteAllText(path, document.ToJsonString());
                break;
            case "oversize":
                document["Padding"] = new string('x', 512 * 1024);
                File.WriteAllText(path, document.ToJsonString());
                Assert.True(new FileInfo(path).Length > 512 * 1024);
                break;
            case "unconfirmed":
                document["ConfirmingUpshifts"] = 0;
                File.WriteAllText(path, document.ToJsonString());
                break;
            case "unsupported-schema":
                document["Schema"] = 0;
                File.WriteAllText(path, document.ToJsonString());
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        var invalidBytes = File.ReadAllBytes(path);
        Assert.Null(store.Load(Build, result.Context));
        Assert.Equal(invalidBytes, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public void CancelledCommitPreservesExistingCalibrationAndRemovesTemporaryFile()
    {
        using var temp = new TempDirectory();
        var store = new ShiftCalibrationStore(temp.Path);
        var original = Calibration();
        Assert.True(store.Save(Build, original, Commit));
        var path = Assert.Single(Directory.GetFiles(temp.Path, "*.json"));
        var originalBytes = File.ReadAllBytes(path);
        var replacement = Calibration(intercept: 1200, slope: .11);
        Assert.NotEqual(original.Gears[0].EstimatedTargetRpm, replacement.Gears[0].EstimatedTargetRpm);
        var commitAttempted = false;

        Assert.False(store.Save(Build, replacement, _ =>
        {
            commitAttempted = true;
            Assert.Single(Directory.GetFiles(temp.Path, "*.tmp"));
            Assert.Equal(originalBytes, File.ReadAllBytes(path));
            return false;
        }));

        Assert.True(commitAttempted);
        Assert.Equal(originalBytes, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
        Assert.Single(Directory.GetFiles(temp.Path));
        var restored = store.Load(Build, original.Context);
        Assert.NotNull(restored);
        Assert.Equal(original.Gears[0].EstimatedTargetRpm, restored.Gears[0].EstimatedTargetRpm);
    }

    private static ShiftCalibrationContext Context(int car = 100, string fingerprint = "calibrated-tune") =>
        new(car, fingerprint, [2, 1], configuredOperatingCeilingRpm: 9000);

    private static ShiftCalibrationResult Calibration(double intercept = 1000, double slope = .1)
    {
        var samples = Enumerable.Range(0, 76)
            .Select(i => 1000 + i * 100d)
            .Select(rpm => new AccelerationShiftSample(rpm, intercept - slope * rpm))
            .ToArray();
        Assert.True(ShiftCalibrationSession.TryRestore(Context(), samples,
            empiricalUpperRpm: null, confirmingUpshifts: 1, acceptedSamples: samples.Length * 3, out var result));
        Assert.NotNull(result);
        return result;
    }

    private static bool Commit(Action commit)
    {
        commit();
        return true;
    }

    private sealed class TempDirectory : IDisposable
    {
        internal string Path { get; } = Directory.CreateTempSubdirectory("WispShiftCalibrationTests-").FullName;

        public void Dispose()
        {
            var resolved = System.IO.Path.GetFullPath(Path);
            var parent = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetDirectoryName(resolved)!);
            var expected = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()));
            if (!string.Equals(parent, expected, StringComparison.OrdinalIgnoreCase) ||
                !System.IO.Path.GetFileName(resolved).StartsWith("WispShiftCalibrationTests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to remove a directory outside the test temporary root.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }
}
