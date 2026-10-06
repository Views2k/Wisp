using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wisp.App;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeAdaptiveRelatedTableTests
{
    [Theory]
    [InlineData(0x3b00)]
    [InlineData(0x3e40)]
    public void SecondaryTableIsFoundThroughItsVerifiedParentWithoutFixedSpacing(int secondaryTable)
    {
        using var fixture = new Fixture((uint)secondaryTable);
        var image = fixture.Image(TestContext.Current.CancellationToken);

        var resolved = new NativeAdaptiveResolver(image, fixture.Profile.RootElement).Resolve();

        Assert.Equal(Fixture.ParentTable, resolved[Fixture.ParentPath]);
        Assert.Equal((uint)secondaryTable, resolved[Fixture.SecondaryPath]);
        Assert.True(image.FinishProof().Matches(fixture.Read, Fixture.Module,
            Fixture.Budget(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public void SharedGetterCodeDoesNotReplaceOrInvalidateTheParentTypeRelationship()
    {
        using var fixture = new Fixture();
        fixture.Bytes.AsSpan((int)Fixture.SecondaryGetter, 5).CopyTo(fixture.Bytes.AsSpan(0x1580));

        Assert.Equal(fixture.SecondaryTable, fixture.Resolve(TestContext.Current.CancellationToken)[Fixture.SecondaryPath]);
    }

    [Theory]
    [InlineData(12, Fixture.OtherTypeDescriptor)]
    [InlineData(16, Fixture.OtherHierarchy)]
    public void EqualLookingButDifferentTypeOrHierarchyPointersAreRejected(int locatorField, uint replacement)
    {
        using var fixture = new Fixture();
        fixture.Write32(Fixture.SecondaryLocator + (uint)locatorField, replacement);

        Assert.Throws<InvalidDataException>(() => fixture.Resolve(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(4, 40U)]
    [InlineData(8, 4U)]
    public void SecondaryObjectOffsetAndConstructionDisplacementMustStillMatch(int locatorField, uint replacement)
    {
        using var fixture = new Fixture();
        fixture.Write32(Fixture.SecondaryLocator + (uint)locatorField, replacement);

        Assert.Throws<InvalidDataException>(() => fixture.Resolve(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TwoCompleteSecondaryTablesWithTheSameParentIdentityAreAmbiguous()
    {
        using var fixture = new Fixture();
        fixture.AddSecondaryTable(0x3d80, 0x31c0);

        Assert.Throws<InvalidDataException>(() => fixture.Resolve(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void RelatedIdentityCannotAdmitAChangedCompleteSlotGuard(int changedByte)
    {
        using var fixture = new Fixture();
        fixture.Bytes[Fixture.SecondaryGetter + changedByte] ^= 1;

        Assert.Throws<InvalidDataException>(() => fixture.Resolve(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AChangedInheritanceMemberStillRejectsTheRelatedTable()
    {
        using var fixture = new Fixture();
        fixture.Write32(Fixture.BaseDescriptor + 8, 8);

        Assert.Throws<InvalidDataException>(() => fixture.Resolve(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(0)] // Pointer from the parent table to its complete object locator.
    [InlineData(12)] // Type descriptor pointer within that locator.
    [InlineData(16)] // Class hierarchy pointer within that locator.
    public void ParentIdentityChangesAreCaughtByTheFinalAndCachedProof(int changedPointer)
    {
        using var fixture = new Fixture();
        var image = fixture.Image(TestContext.Current.CancellationToken);
        new NativeAdaptiveResolver(image, fixture.Profile.RootElement).Resolve();
        var proof = image.FinishProof();

        if (changedPointer == 0)
            fixture.WritePointer(Fixture.ParentTable - 8, Fixture.Module + Fixture.OtherParentLocator);
        else
            fixture.Write32(Fixture.ParentLocator + (uint)changedPointer,
                changedPointer == 12 ? Fixture.OtherTypeDescriptor : Fixture.OtherHierarchy);

        Assert.False(proof.Matches(fixture.Read, Fixture.Module,
            Fixture.Budget(TestContext.Current.CancellationToken)));
        Assert.Throws<IOException>(() => image.FinishProof());
    }

    private sealed class Fixture : IDisposable
    {
        internal const ulong Module = 0x140000000;
        internal const uint ParentTable = 0x3380;
        internal const uint ParentLocator = 0x3100;
        internal const uint SecondaryLocator = 0x3140;
        internal const uint OtherParentLocator = 0x3180;
        internal const uint SecondaryGetter = 0x1480;
        internal const uint OtherTypeDescriptor = 0x4400;
        internal const uint OtherHierarchy = 0x3840;
        internal const uint BaseDescriptor = 0x3980;
        internal const string ParentPath = "/nativeGauge/hudVtableRva";
        internal const string SecondaryPath = "/nativeGauge/hudSubobjectVtableRva";
        private const uint TypeDescriptor = 0x4300;
        private const uint Hierarchy = 0x3800;
        private const uint BaseArray = 0x3900;
        private const uint ParentGetter = 0x1280;
        private static readonly byte[] TypeName = Encoding.ASCII.GetBytes(".?AVAdaptiveHud@@\0");

        internal byte[] Bytes { get; } = new byte[0x8000];
        internal JsonDocument Profile { get; }
        internal uint SecondaryTable { get; }

        internal Fixture(uint secondaryTable = 0x3b00)
        {
            SecondaryTable = secondaryTable;
            Write16(0, 0x5a4d); Write32(0x3c, 0x80); Write32(0x80, 0x4550);
            Write16(0x84, 0x8664); Write16(0x86, 3); Write16(0x94, 0xf0);
            Write16(0x98, 0x20b); Write32(0xd0, (uint)Bytes.Length);
            Section(0, ".text", 0x1000, 0x2000, 0x60000020);
            Section(1, ".rdata", 0x3000, 0x2000, 0x40000040);
            Section(2, ".data", 0x5000, 0x2000, 0xc0000040);

            var reference = Convert.FromHexString("55488D0500000000C3");
            reference.CopyTo(Bytes, 0x1180);
            Write32(0x1184, ParentTable - 0x1188);
            var parentGetter = Convert.FromHexString("8B8120000000C3");
            parentGetter.CopyTo(Bytes, (int)ParentGetter);
            var secondaryGetter = Convert.FromHexString("8B410890C3");
            secondaryGetter.CopyTo(Bytes, (int)SecondaryGetter);

            WriteLocator(ParentLocator, 0);
            WriteLocator(OtherParentLocator, 0);
            WritePointer(ParentTable - 8, Module + ParentLocator);
            WritePointer(ParentTable, Module + ParentGetter);
            AddSecondaryTable(secondaryTable, SecondaryLocator);
            Write32(Hierarchy + 8, 1);
            Write32(Hierarchy + 12, BaseArray);
            Bytes.AsSpan((int)Hierarchy, 16).CopyTo(Bytes.AsSpan((int)OtherHierarchy));
            Write32(BaseArray, BaseDescriptor);
            Write32(BaseDescriptor, TypeDescriptor);
            Write32(BaseDescriptor + 12, uint.MaxValue);
            Write32(BaseDescriptor + 24, Hierarchy);
            TypeName.CopyTo(Bytes, (int)TypeDescriptor + 16);
            TypeName.CopyTo(Bytes, (int)OtherTypeDescriptor + 16);

            Profile = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                signatures = new object[]
                {
                    new
                    {
                        id = "parent-reference", referenceRva = 0x1100, length = reference.Length,
                        normalizedSha256 = Convert.ToHexString(SHA256.HashData(reference)),
                        anchorOffset = 0, anchorHex = "55488D05", section = "code",
                        relocations = new[] { new { offset = 4, width = 4, next = 8, kind = "relative",
                            targetRva = 0x3300, expectedTargetSection = "readonly" } }
                    },
                    Signature("parent-getter", 0x1200, parentGetter),
                    Signature("secondary-getter", 0x1400, secondaryGetter)
                },
                bindings = new object[]
                {
                    // The secondary is deliberately listed first: the parent
                    // must resolve before this relationship can be admitted.
                    Table(SecondaryPath, "secondary-getter", 48, ParentPath),
                    new { path = ParentPath, kind = "reference", signature = "parent-reference", operandOffset = 4 },
                    Table(ParentPath, "parent-getter", 0)
                },
                checks = Array.Empty<object>()
            }));
        }

        private static object Signature(string id, uint referenceRva, byte[] code) => new
        {
            id,
            referenceRva,
            length = code.Length,
            normalizedSha256 = Convert.ToHexString(SHA256.HashData(code)),
            anchorOffset = 0,
            anchorHex = Convert.ToHexString(code),
            section = "code",
            relocations = Array.Empty<object>()
        };

        private static Dictionary<string, object> Table(string path, string signature, uint offset, string? parent = null)
        {
            var table = new Dictionary<string, object>
            {
                ["path"] = path,
                ["kind"] = "table",
                ["minimumAnchors"] = 1,
                ["anchors"] = new[] { 0 },
                ["slots"] = new[] { new { offset = 0, signature, required = true } },
                ["scalarSlots"] = Array.Empty<object>(),
                ["topology"] = new
                {
                    offset,
                    constructionDisplacement = 0,
                    hierarchyFlags = new[] { 0, 0, 1 },
                    basePmdAndAttributes = new[] { new uint[] { 0, 0, uint.MaxValue, 0, 0 } },
                    baseTypeNames = new[] { new { kind = "named", hex = Convert.ToHexString(TypeName) } }
                }
            };
            if (parent is not null) table["relatedTypeTablePath"] = parent;
            return table;
        }

        internal void AddSecondaryTable(uint table, uint locator)
        {
            WriteLocator(locator, 48);
            WritePointer(table - 8, Module + locator);
            WritePointer(table, Module + SecondaryGetter);
        }

        private void WriteLocator(uint locator, uint offset)
        {
            Write32(locator, 1);
            Write32(locator + 4, offset);
            Write32(locator + 12, TypeDescriptor);
            Write32(locator + 16, Hierarchy);
            Write32(locator + 20, locator);
        }

        internal bool Read(ulong address, Span<byte> destination)
        {
            if (address < Module || address - Module >= (ulong)Bytes.Length ||
                (ulong)destination.Length > (ulong)Bytes.Length - (address - Module)) return false;
            Bytes.AsSpan((int)(address - Module), destination.Length).CopyTo(destination);
            return true;
        }

        internal static NativeAdaptiveBudget Budget(CancellationToken cancellationToken) =>
            new(TimeSpan.FromSeconds(5), cancellationToken);
        internal NativeAdaptiveImage Image(CancellationToken cancellationToken) =>
            new(Read, Module, (uint)Bytes.Length, false, Budget(cancellationToken));
        internal IReadOnlyDictionary<string, uint> Resolve(CancellationToken cancellationToken) =>
            new NativeAdaptiveResolver(Image(cancellationToken), Profile.RootElement).Resolve();
        internal void WritePointer(uint offset, ulong value) =>
            BinaryPrimitives.WriteUInt64LittleEndian(Bytes.AsSpan((int)offset), value);
        internal void Write32(uint offset, uint value) =>
            BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan((int)offset), value);
        private void Write16(int offset, ushort value) =>
            BinaryPrimitives.WriteUInt16LittleEndian(Bytes.AsSpan(offset), value);
        private void Section(int index, string name, uint rva, uint size, uint flags)
        {
            var offset = (uint)(0x188 + 40 * index);
            Encoding.ASCII.GetBytes(name).CopyTo(Bytes, (int)offset);
            Write32(offset + 8, size); Write32(offset + 12, rva); Write32(offset + 36, flags);
        }

        public void Dispose() => Profile.Dispose();
    }
}
