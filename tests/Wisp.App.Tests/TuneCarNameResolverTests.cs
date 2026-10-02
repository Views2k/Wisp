using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using Wisp.App.Tunes;
using Xunit;

namespace Wisp.App.Tests;

public sealed class TuneCarNameResolverTests
{
    private const ulong ModelToken = 0x434455F200000017;
    private const ulong MakeToken = 0x788FB61100000009;

    [Fact]
    public void ReturnsOnlyTheSelectedNameWithoutExtractingTheTables()
    {
        using var fixture = new Fixture();
        fixture.Write(Models(), Makes());
        Assert.Equal("2008 Example Coupé", Resolve(fixture));
        Assert.Single(Directory.GetFiles(fixture.Directory));
        Assert.Empty(Directory.GetDirectories(fixture.Directory));
    }

    [Fact]
    public void PreservesAValidFutureModelYearFromTheGameMetadata()
    {
        using var fixture = new Fixture();
        fixture.Write(Models(), Makes());
        Assert.Equal("2554 Example Coupé", TuneCarNameResolver.TryResolve(fixture.Path, 2554,
            ModelToken, MakeToken, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void MissingArchiveTableOrTokenFallsBackWithoutGuessing()
    {
        using var fixture = new Fixture();
        Assert.Null(Resolve(fixture));
        fixture.Write(Models(), null);
        Assert.Null(Resolve(fixture));
        fixture.Write(Models(), Makes());
        Assert.Null(TuneCarNameResolver.TryResolve(fixture.Path, 2008, ModelToken + 1, MakeToken, TestContext.Current.CancellationToken));
        Assert.Null(TuneCarNameResolver.TryResolve(fixture.Path, 2008, ModelToken, MakeToken + 1, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(2008, 0x434455F300000017UL, MakeToken)]
    [InlineData(2008, ModelToken, 0x788FB61200000009UL)]
    [InlineData(0, ModelToken, MakeToken)]
    [InlineData(10000, ModelToken, MakeToken)]
    public void RejectsAnUnverifiedTokenNamespaceOrYear(int year, ulong model, ulong make)
    {
        using var fixture = new Fixture();
        fixture.Write(Models(), Makes());
        Assert.Null(TuneCarNameResolver.TryResolve(fixture.Path, year, model, make, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("magic")]
    [InlineData("name")]
    [InlineData("padding")]
    [InlineData("sections")]
    [InlineData("first-offset")]
    [InlineData("second-offset")]
    [InlineData("payload-size")]
    [InlineData("pool-size")]
    [InlineData("entry-count")]
    [InlineData("string-offset")]
    [InlineData("string-interior")]
    [InlineData("duplicate-id")]
    [InlineData("key-id")]
    [InlineData("key-offset")]
    [InlineData("unterminated")]
    [InlineData("utf8")]
    public void RejectsMalformedDisplayOrKeySections(string corruption)
    {
        using var fixture = new Fixture();
        var table = Models();
        var second = checked((int)U32(table, 136));
        var pool = 152 + checked((int)U32(table, 148)) * 8;
        switch (corruption)
        {
            case "magic": table[1] = 7; break;
            case "name": table[2] = (byte)'X'; break;
            case "padding": table[129] = 1; break;
            case "sections": table[130] = 3; break;
            case "first-offset": Put(table, 132, 144); break;
            case "second-offset": Put(table, 136, uint.MaxValue); break;
            case "payload-size": Put(table, 140, U32(table, 140) + 1); break;
            case "pool-size": Put(table, 144, uint.MaxValue); break;
            case "entry-count": Put(table, 148, 4097); break;
            case "string-offset": Put(table, 156, U32(table, 144)); break;
            case "string-interior": Put(table, 156, 1); break;
            case "duplicate-id": Put(table, 160, U32(table, 152)); break;
            case "key-id": Put(table, second + 12, 1); break;
            case "key-offset": Put(table, second + 16, U32(table, second + 4)); break;
            case "unterminated": table[^1] = (byte)'x'; break;
            case "utf8": table[pool] = 0xFF; break;
        }
        fixture.Write(table, Makes());
        Assert.Null(Resolve(fixture));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" Coupé")]
    [InlineData("Coupé ")]
    [InlineData("Coupé\n")]
    [InlineData("Coupé\u202E")]
    [InlineData("Cou\u2028pé")]
    [InlineData("Cou\u2029pé")]
    public void RejectsInvalidSelectedLabel(string label)
    {
        using var fixture = new Fixture();
        fixture.Write(Table("Data_Car", (23, label)), Makes());
        Assert.Null(Resolve(fixture));
    }

    [Fact]
    public void RejectsOversizedLabelsTablesAndArchives()
    {
        using var fixture = new Fixture();
        fixture.Write(Table("Data_Car", (23, new string('x', 161))), Makes());
        Assert.Null(Resolve(fixture));
        fixture.Write(new byte[TuneCarNameResolver.MaximumTableBytes + 1], Makes());
        Assert.Null(Resolve(fixture));
        using (var file = File.Create(fixture.Path)) file.SetLength(TuneCarNameResolver.MaximumArchiveBytes + 1L);
        Assert.Null(Resolve(fixture));
    }

    [Theory]
    [InlineData("Data_Car.str")]
    [InlineData("data_car.str")]
    public void RejectsDuplicateOrAmbiguousTableNames(string duplicate)
    {
        using var fixture = new Fixture();
        fixture.Write(Models(), Makes(), duplicate);
        Assert.Null(Resolve(fixture));
    }

    [Fact]
    public void RejectsDirectoryCountMismatchAndTruncatedZip()
    {
        using var fixture = new Fixture();
        fixture.Write(Models(), Makes());
        var bytes = File.ReadAllBytes(fixture.Path);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 22 + 8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 22 + 10), 1);
        File.WriteAllBytes(fixture.Path, bytes);
        Assert.Null(Resolve(fixture));
        File.WriteAllBytes(fixture.Path, bytes[..^1]);
        Assert.Null(Resolve(fixture));
    }

    [Fact]
    public void CancellationIsNotReportedAsMissingName()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => TuneCarNameResolver.TryResolve("unused", 2008,
            ModelToken, MakeToken, cancellation.Token));
    }

    [Theory]
    [InlineData(@"\\unreachable.invalid\share\EN.zip")]
    [InlineData("//unreachable.invalid/share/EN.zip")]
    [InlineData(@"/\unreachable.invalid\share\EN.zip")]
    [InlineData(@"\/unreachable.invalid/share/EN.zip")]
    [InlineData(@"\\?\C:\Example\EN.zip")]
    [InlineData(@"\\.\C:\Example\EN.zip")]
    [InlineData(@"Example\EN.zip")]
    [InlineData(@"C:Example\EN.zip")]
    public void RejectsNetworkDeviceAndRelativePathsWithoutOpeningThem(string path) =>
        Assert.Null(TuneCarNameResolver.NormalizeLocalArchivePath(path));

    [Fact]
    public void NormalizesAnAbsoluteLocalPathWithoutReadingIt() =>
        Assert.Equal(@"C:\Example\EN.zip", TuneCarNameResolver.NormalizeLocalArchivePath("C:/Example/EN.zip"));

    private static string? Resolve(Fixture fixture) => TuneCarNameResolver.TryResolve(fixture.Path, 2008,
        ModelToken, MakeToken, TestContext.Current.CancellationToken);
    private static byte[] Models() => Table("Data_Car", (71, "Unselected model"), (23, "Coupé"), (42, "Other model"));
    private static byte[] Makes() => Table("List_CarMake", (9, "Example"), (3, "Unselected make"));

    private static byte[] Table(string name, params (uint Id, string Text)[] rows)
    {
        var values = Section(rows);
        var keys = Section(rows.Select((row, index) => (row.Id, "Key" + index)).ToArray());
        var result = new byte[140 + values.Length + keys.Length];
        result[1] = 8;
        Encoding.ASCII.GetBytes(name).CopyTo(result, 2);
        result[130] = 2;
        Put(result, 132, 140);
        Put(result, 136, (uint)(140 + values.Length));
        values.CopyTo(result, 140);
        keys.CopyTo(result, 140 + values.Length);
        return result;
    }

    private static byte[] Section((uint Id, string Text)[] rows)
    {
        var strings = rows.Select(row => Encoding.UTF8.GetBytes(row.Text + "\0")).ToArray();
        var poolBytes = strings.Sum(value => value.Length);
        var result = new byte[12 + rows.Length * 8 + poolBytes];
        Put(result, 0, (uint)(result.Length - 12)); Put(result, 4, (uint)poolBytes); Put(result, 8, (uint)rows.Length);
        var offset = 0;
        for (var index = 0; index < rows.Length; index++)
        {
            Put(result, 12 + index * 8, rows[index].Id); Put(result, 16 + index * 8, (uint)offset);
            strings[index].CopyTo(result, 12 + rows.Length * 8 + offset);
            offset += strings[index].Length;
        }
        return result;
    }

    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static void Put(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);

    private sealed class Fixture : IDisposable
    {
        internal string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WispCarNameTests", Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(Directory, "EN.zip");
        internal Fixture() => System.IO.Directory.CreateDirectory(Directory);
        internal void Write(byte[] model, byte[]? make, string? duplicate = null)
        {
            using var file = File.Create(Path);
            using var zip = new ZipArchive(file, ZipArchiveMode.Create);
            WriteEntry(zip, "Data_Car.str", model);
            if (make is not null) WriteEntry(zip, "List_CarMake.str", make);
            if (duplicate is not null) WriteEntry(zip, duplicate, model);
        }
        private static void WriteEntry(ZipArchive zip, string name, byte[] bytes)
        {
            using var output = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
            output.Write(bytes);
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
