using System.Buffers.Binary;
using System.Collections.Immutable;
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
        var error = Assert.Throws<TuneAssetValidationException>(() => TuneAssetCapture.ValidateHeader(bytes));
        Assert.Equal(offset == 28 ? TuneAssetFailureCode.HeaderPageCount : TuneAssetFailureCode.HeaderFormat, error.FailureCode);
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
    public void ArbitrarySchemaIsRejectedButMutableRowsNoLongerRequireAWholeTableHash()
    {
        Assert.Throws<InvalidDataException>(() => TuneAssetContract.ValidateSchema(TunePartId.Engine, "CREATE TABLE List_UpgradeEngine(Id INTEGER, Ordinal INTEGER, Level INTEGER)"));
        var rows = Enumerable.Range(0, 3017).Select(id => new TuneAssetRow(TunePartId.Engine, 1, id, 0, null)).ToArray();
        TuneAssetContract.ValidateRows(TunePartId.Engine, rows);
        rows[0] = rows[0] with { Level = 1 };
        TuneAssetContract.ValidateRows(TunePartId.Engine, rows);
        TuneAssetContract.ValidateRows(TunePartId.Engine, [.. rows, new(TunePartId.Engine, 1, 3017, 2, null)]);
    }

    [Fact]
    public void DescriptorRowCountsProvideASanityFloorWithoutPinningMutableLengthsPagesOrRows()
    {
        var rows = new[] { new TuneAssetRow(TunePartId.Engine, 1, 2, 3, null) };
        var parts = new Dictionary<TunePartId, NativeTuneCompatibilityLayout.PartExpectation>
        {
            [TunePartId.Engine] = new(1, TuneAssetContract.HashRows(TunePartId.Engine, rows))
        };
        var asset = new NativeTuneCompatibilityLayout.AssetContract(TuneAssetCapture.ExpectedLength,
            TuneAssetCapture.ExpectedLength + 1024 * 1024, 15409, parts);
        TuneAssetContract.ValidateRows(TunePartId.Engine, rows, asset);
        Assert.Throws<InvalidDataException>(() => TuneAssetContract.ValidateRows(TunePartId.Engine, rows));
        TuneAssetContract.ValidateRows(TunePartId.Engine, [rows[0] with { Level = 4 }], asset);
        Assert.Throws<InvalidDataException>(() => TuneAssetContract.ValidateRows(TunePartId.Brakes, [], asset));
        var header = Header(TuneAssetCapture.MinimumLength);
        TuneAssetCapture.ValidateHeader(header, asset);
        header[18] = 2;
        Assert.Throws<TuneAssetValidationException>(() => TuneAssetCapture.ValidateHeader(header, asset));
        Assert.Throws<TuneAssetValidationException>(() => TuneAssetCapture.ValidateHeader(new byte[64], asset with { MinimumLength = 1 }));
        TuneAssetCapture.ValidateStreamShape(0, 1024, TuneAssetCapture.MinimumLength, TuneAssetCapture.MinimumLength, 8192, asset);
        Assert.Throws<TuneAssetStreamValidationException>(() =>
            TuneAssetCapture.ValidateStreamShape(0, 1024, 1024, 1024, 8));
        Assert.Throws<TuneAssetStreamValidationException>(() => TuneAssetCapture.ValidateStreamShape(0, 1024,
            (uint)TuneAssetCapture.MaximumLength + 1024, (ulong)TuneAssetCapture.MaximumLength + 1024, 8,
            asset with { MaximumLength = int.MaxValue }));
    }

    [Theory]
    [InlineData(TuneAssetCapture.MinimumLength, 1U)]
    [InlineData(TuneAssetCapture.MinimumLength, 1024U)]
    [InlineData(TuneAssetCapture.ExpectedLength, 15410U)]
    [InlineData(TuneAssetCapture.MaximumLength, 65536U)]
    public void MutablePageCountMustFitTheBoundedCopiedStream(int length, uint pageCount)
    {
        var bytes = Header(length);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(28), pageCount);
        TuneAssetCapture.ValidateHeader(bytes);
    }

    [Theory]
    [InlineData(0U)]
    [InlineData(1025U)]
    [InlineData(uint.MaxValue)]
    public void ZeroOrOutOfStreamPageCountsAreRejectedWithNumericDetails(uint pageCount)
    {
        var bytes = Header(TuneAssetCapture.MinimumLength);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(28), pageCount);
        var error = Assert.Throws<TuneAssetValidationException>(() => TuneAssetCapture.ValidateHeader(bytes));
        Assert.Equal(TuneAssetFailureCode.HeaderPageCount, error.FailureCode);
        Assert.Equal((ulong)bytes.Length, error.ActualSizeBytes);
        Assert.Equal(TuneAssetCapture.MaximumLength, error.MaximumSizeBytes);
        Assert.Equal(pageCount, error.ActualPageCount);
    }

    [Theory]
    [InlineData(TuneAssetCapture.MinimumLength - 1024)]
    [InlineData(TuneAssetCapture.MinimumLength + 1)]
    [InlineData(TuneAssetCapture.MaximumLength + 1024)]
    public void HeaderAndDecoderShareTheHardLengthBounds(int length)
    {
        var bytes = new byte[length];
        var header = Assert.Throws<TuneAssetValidationException>(() => TuneAssetCapture.ValidateHeader(bytes));
        var decode = Assert.Throws<TuneAssetValidationException>(() =>
            TuneAssetCapture.Decode(bytes, [], [], TestContext.Current.CancellationToken));
        Assert.Equal(TuneAssetFailureCode.HeaderLength, header.FailureCode);
        Assert.Equal(TuneAssetFailureCode.DecodeLength, decode.FailureCode);
        Assert.Equal((ulong)length, header.ActualSizeBytes);
        Assert.Equal((ulong)length, decode.ActualSizeBytes);
    }

    [Theory]
    [InlineData(31, null)]
    [InlineData(32, 123U)]
    public void InvalidHeaderLengthRetainsOnlyAnAvailablePageCount(int length, uint? pageCount)
    {
        var bytes = new byte[length];
        if (pageCount.HasValue) BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(28), pageCount.Value);
        var error = Assert.Throws<TuneAssetValidationException>(() => TuneAssetCapture.ValidateHeader(bytes));
        Assert.Equal(TuneAssetFailureCode.HeaderLength, error.FailureCode);
        Assert.Equal((ulong)length, error.ActualSizeBytes);
        Assert.Equal(pageCount, error.ActualPageCount);
    }

    [Fact]
    public void AtLeastHalfOfExpectedRowsAreRequiredAndIdentitiesStillMustBeUnique()
    {
        // The retained Motor projection has 33 rows: the minimum is 17, not 16.
        var rows = Enumerable.Range(0, 17).Select(id => new TuneAssetRow(TunePartId.Motor, 1, id, 0, null)).ToArray();
        TuneAssetContract.ValidateRows(TunePartId.Motor, rows);
        Assert.Throws<InvalidDataException>(() => TuneAssetContract.ValidateRows(TunePartId.Motor, rows[..16]));
        Assert.Throws<InvalidDataException>(() => TuneAssetContract.ValidateRows(TunePartId.Motor, [.. rows, rows[0]]));
        rows[0] = rows[0] with { Kind = TunePartId.Engine };
        Assert.Throws<InvalidDataException>(() => TuneAssetContract.ValidateRows(TunePartId.Motor, rows));
    }

    [Fact]
    public void DescriptorFloorAndHardRowLimitStillApply()
    {
        var parts = new Dictionary<TunePartId, NativeTuneCompatibilityLayout.PartExpectation>
        {
            [TunePartId.Motor] = new(35, new string('0', 64))
        };
        var asset = new NativeTuneCompatibilityLayout.AssetContract(1024, 2048, 1, parts);
        var rows = Enumerable.Range(0, 18).Select(id => new TuneAssetRow(TunePartId.Motor, 1, id, 0, null)).ToArray();
        TuneAssetContract.ValidateRows(TunePartId.Motor, rows, asset);
        Assert.Throws<InvalidDataException>(() => TuneAssetContract.ValidateRows(TunePartId.Motor, rows[..17], asset));
        var excessive = Enumerable.Range(0, 250001).Select(id => new TuneAssetRow(TunePartId.Motor, 1, id, 0, null)).ToArray();
        Assert.Throws<InvalidDataException>(() => TuneAssetContract.ValidateRows(TunePartId.Motor, excessive, asset));
    }

    [Fact]
    public void MutableRowsDoNotMakeMissingOrInvalidSelectedCarPartsResolvable()
    {
        var parts = Enum.GetValues<TunePartId>().Select(kind => new TunePart(kind, 7, null)).ToImmutableArray();
        var rows = parts.Select(part => new TuneAssetRow(part.Kind,
            part.Kind is TunePartId.Transmission or TunePartId.Differential ? 77 : part.Kind == TunePartId.FrontAero ? 88 : 10,
            part.InstalledId, 1, part.Kind == TunePartId.Drivetrain ? 77 : part.Kind == TunePartId.CarBody ? 88 : null)).ToArray();
        Assert.All(new TuneAssetMetadata(rows).Resolve(10, parts), part => Assert.Equal(1, part.Level));
        var missing = new TuneAssetMetadata(rows.Where(row => row.Kind != TunePartId.Engine)).Resolve(10, parts);
        Assert.Null(Assert.Single(missing, part => part.Kind == TunePartId.Engine).Level);
        var invalid = new TuneAssetMetadata(rows.Select(row => row.Kind == TunePartId.Brakes ? row with { Level = 101 } : row))
            .Resolve(10, parts);
        Assert.Null(Assert.Single(invalid, part => part.Kind == TunePartId.Brakes).Level);
        Assert.All(new TuneAssetMetadata(rows).Resolve(11, parts), part => Assert.Null(part.Level));
    }

    private static byte[] Header(int length = TuneAssetCapture.ExpectedLength)
    {
        var bytes = new byte[length];
        "SQLite format 3\0"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(16), 1024);
        bytes[18] = bytes[19] = 1;
        bytes[21] = 64; bytes[22] = bytes[23] = 32;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(28), Math.Min(15409U, (uint)(length / 1024)));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(44), 4);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(56), 1);
        return bytes;
    }
}
