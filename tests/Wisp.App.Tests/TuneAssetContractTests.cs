using System.Buffers.Binary;
using System.IO;
using Wisp.App;
using Wisp.App.Tunes;
using Wisp.Core.Tunes;
using Xunit;

namespace Wisp.App.Tests;

public sealed class TuneAssetContractTests
{
    [Fact]
    public void HeaderCountersAndAdditionalWholePagesAreNotMetadataIdentity()
    {
        var bytes = Header(TuneAssetCapture.ExpectedLength + 1024);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(24), 3167);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(40), 482);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(92), 3134);
        TuneAssetCapture.ValidateHeader(bytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(23)]
    [InlineData(28)]
    [InlineData(44)]
    [InlineData(56)]
    public void RequiredHeaderFormatChangesAreRejected(int offset)
    {
        var bytes = Header();
        bytes[offset] ^= 1;
        Assert.Throws<InvalidDataException>(() => TuneAssetCapture.ValidateHeader(bytes));
    }

    [Fact]
    public void ValidLookingHeaderDoesNotAcceptAnUnverifiedDatabase()
    {
        var bytes = Header();
        var error = Assert.Throws<InvalidDataException>(() =>
            TuneAssetSqlite.Extract(bytes, TestContext.Current.CancellationToken));
        Assert.Contains("Local tuning metadata", error.Message);
    }

    [Fact]
    public void CancelledExtractionStopsBeforeOpeningSQLite()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => TuneAssetSqlite.Extract([], cancelled.Token));
    }

    [Fact]
    public void ProjectionHashIgnoresRowOrderButKeepsEveryNumericFieldAndNullState()
    {
        TuneAssetRow[] rows = [new(TunePartId.Drivetrain, 4, 8, null, 0), new(TunePartId.Drivetrain, 2, 8, 3, 9)];
        var original = TuneAssetContract.HashRows(TunePartId.Drivetrain, rows);
        Assert.Equal(original, TuneAssetContract.HashRows(TunePartId.Drivetrain, rows.Reverse().ToArray()));
        foreach (var changed in new[]
        {
            rows[0] with { Parent = 5 }, rows[0] with { Id = 9 },
            rows[0] with { Level = 0 }, rows[0] with { ChildParent = null }
        })
            Assert.NotEqual(original, TuneAssetContract.HashRows(TunePartId.Drivetrain, [changed, rows[1]]));
    }

    [Fact]
    public void DuplicateIdentitiesAndWrongPartKindsAreRejected()
    {
        var row = new TuneAssetRow(TunePartId.Engine, 1, 2, 3, null);
        Assert.Throws<InvalidDataException>(() => TuneAssetContract.HashRows(TunePartId.Engine, [row, row with { Level = 4 }]));
        Assert.Throws<InvalidDataException>(() => TuneAssetContract.HashRows(TunePartId.Motor, [row]));
    }

    [Fact]
    public void ArbitrarySchemaAndPlausibleSizedValuesCannotReplacePinnedMetadata()
    {
        Assert.Throws<InvalidDataException>(() => TuneAssetContract.ValidateSchema(TunePartId.Engine, "CREATE TABLE List_UpgradeEngine(Id INTEGER, Ordinal INTEGER, Level INTEGER)"));
        var rows = Enumerable.Range(0, 3017).Select(id => new TuneAssetRow(TunePartId.Engine, 1, id, 0, null)).ToArray();
        Assert.Throws<InvalidDataException>(() => TuneAssetContract.ValidateRows(TunePartId.Engine, rows));
    }

    [Fact]
    public void DescriptorChangesOnlyTheDeclaredAssetExpectations()
    {
        var rows = new[] { new TuneAssetRow(TunePartId.Engine, 1, 2, 3, null) };
        var parts = new Dictionary<TunePartId, NativeTuneCompatibilityLayout.PartExpectation>
        {
            [TunePartId.Engine] = new(1, TuneAssetContract.HashRows(TunePartId.Engine, rows))
        };
        var asset = new NativeTuneCompatibilityLayout.AssetContract(1024, 2048, 1, parts);
        TuneAssetContract.ValidateRows(TunePartId.Engine, rows, asset);
        Assert.Throws<InvalidDataException>(() => TuneAssetContract.ValidateRows(TunePartId.Engine, rows));
        Assert.Throws<InvalidDataException>(() => TuneAssetContract.ValidateRows(TunePartId.Engine,
            [rows[0] with { Level = 4 }], asset));
        Assert.Throws<InvalidDataException>(() => TuneAssetContract.ValidateRows(TunePartId.Brakes, [], asset));
        var header = Header(1024);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(28), 1);
        TuneAssetCapture.ValidateHeader(header, asset);
        header[18] = 2;
        Assert.Throws<InvalidDataException>(() => TuneAssetCapture.ValidateHeader(header, asset));
        Assert.Throws<InvalidDataException>(() => TuneAssetCapture.ValidateHeader(new byte[64], asset with { MinimumLength = 1 }));
        TuneAssetCapture.ValidateStreamShape(0, 1024, 1024, 1024, 8, asset);
        Assert.Throws<TuneAssetStreamValidationException>(() =>
            TuneAssetCapture.ValidateStreamShape(0, 1024, 1024, 1024, 8));
        Assert.Throws<TuneAssetStreamValidationException>(() => TuneAssetCapture.ValidateStreamShape(0, 1024,
            (uint)TuneAssetCapture.MaximumLength + 1024, (ulong)TuneAssetCapture.MaximumLength + 1024, 8,
            asset with { MaximumLength = int.MaxValue }));
    }

    private static byte[] Header(int length = TuneAssetCapture.ExpectedLength)
    {
        var bytes = new byte[length];
        "SQLite format 3\0"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(16), 1024);
        bytes[18] = bytes[19] = 1;
        bytes[21] = 64; bytes[22] = bytes[23] = 32;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(28), 15409);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(44), 4);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(56), 1);
        return bytes;
    }
}
