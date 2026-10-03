using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wisp.App;
using Wisp.App.Tunes;
using Wisp.Core;
using Wisp.Core.Tunes;
using Xunit;

namespace Wisp.App.Tests;

// These bounded memory fixtures exercise extraction, not live-build admission.
public sealed class NativeTuneReadBoundaryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public void StableLocalCandidateCountsHaveSpecificFailure(bool store, int count)
    {
        var memory = new Memory(store);
        memory.Cars(Math.Max(1, count), local: count != 0);
        var error = Assert.Throws<TuneCarSelectionException>(() => NativeTuneCapture.ReadLocalCar(memory, memory.Reader()));
        Assert.Equal(count, error.CandidateCount);
        Assert.Equal(count == 0 ? TuneCarSelectionFailure.NoCar : TuneCarSelectionFailure.AmbiguousCar, error.Failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidEmptyVectorReportsNoCarWithoutReadingAnEmptySpan(bool store)
    {
        var memory = new Memory(store);
        memory.Cars(0);
        var error = Assert.Throws<TuneCarSelectionException>(() => NativeTuneCapture.ReadLocalCar(memory, memory.Reader()));
        Assert.Equal(TuneCarSelectionFailure.NoCar, error.Failure);
        Assert.Equal(2, memory.Reads); // Initial vector plus coherence check.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StableAllNullVectorReportsNoCarWithoutDereferencingNull(bool store)
    {
        var memory = new Memory(store);
        memory.U64(memory.VectorAddress, 0);
        memory.U64(memory.VectorAddress + 8, 0);
        memory.U64(memory.VectorAddress + 16, 0);
        var error = Assert.Throws<TuneCarSelectionException>(() => NativeTuneCapture.ReadLocalCar(memory, memory.Reader()));
        Assert.Equal(TuneCarSelectionFailure.NoCar, error.Failure);
        Assert.Equal(2, memory.Reads);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public void PartiallyNullVectorsRemainMalformed(bool store, int shape)
    {
        var memory = new Memory(store);
        memory.U64(memory.VectorAddress, shape == 1 ? Memory.Vector : 0);
        memory.U64(memory.VectorAddress + 8, shape == 0 ? 0 : Memory.Vector);
        memory.U64(memory.VectorAddress + 16, shape == 1 ? 0 : Memory.Vector);
        Assert.Throws<InvalidDataException>(() => NativeTuneCapture.ReadLocalCar(memory, memory.Reader()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OneVerifiedLocalCarRetainsItsActorAndProvider(bool store)
    {
        var memory = new Memory(store);
        memory.Cars(1);
        Assert.Equal((Memory.Actor, Memory.Provider), NativeTuneCapture.ReadLocalCar(memory, memory.Reader()));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    [InlineData(false, 4)]
    [InlineData(true, 4)]
    [InlineData(false, 5)]
    [InlineData(true, 5)]
    public void MalformedOrUnreadableCandidatesNeverSuggestDrivingWillFixThem(bool store, int fault)
    {
        var memory = new Memory(store);
        memory.Cars(1);
        switch (fault)
        {
            case 0: memory.U64(memory.VectorAddress + 8, Memory.Vector - 8); break;
            case 1: memory.U64(memory.VectorAddress, Memory.Vector + 1); break;
            case 2: memory.U64(Memory.Vector, 0); break;
            case 3: memory.U64(Memory.Actor + memory.CompatibilityPack.Fields.SourceProvider, 0); break;
            case 4: memory.Missing = Memory.Provider + memory.CompatibilityPack.Fields.LocalPlayerFlag; break;
            case 5:
                var slot = memory.CompatibilityPack.RequiredVtableSlots.First();
                memory.U64(memory.ModuleBase + memory.CompatibilityPack.LeadVtableRva + slot.Key, 0);
                break;
        }
        Assert.Throws<InvalidDataException>(() => NativeTuneCapture.ReadLocalCar(memory, memory.Reader()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangingEmptyVectorReportsChangedInsteadOfNoCar(bool store)
    {
        var memory = new Memory(store);
        memory.Cars(0);
        memory.ChangeVectorOnRepeat = true;
        Assert.Throws<TuneChangedException>(() => NativeTuneCapture.ReadLocalCar(memory, memory.Reader()));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void IndividualUnitOverrideUsesTheVerifiedTableAndKeepsOtherFields(bool store, bool callback)
    {
        var memory = new Memory(store);
        memory.Units();
        var defaults = NativeTuneCapture.ReadUnitConversions(memory, memory.Reader(), memory.Layout, 1);
        Assert.Equal(54, defaults[TuneQuantity.SpringRate].UnitId);
        memory.I32(Memory.UnitOwner + 0x14D0 + 27 * 4, 3);
        memory.I32(Memory.OverrideTable + 3 * 20 + 4, callback ? 54 : 56);
        memory.Conversion(callback ? 54 : 56, callback ? 1 : .10197161961595322, callback ? memory.ModuleBase + 4096 : 0);
        var actual = NativeTuneCapture.ReadUnitConversions(memory, memory.Reader(), memory.Layout, 1);
        Assert.Equal(defaults[TuneQuantity.Pressure], actual[TuneQuantity.Pressure]);
        Assert.Equal(callback ? 54 : 56, actual[TuneQuantity.SpringRate].UnitId);
        Assert.Equal(callback, actual[TuneQuantity.SpringRate].HasCallback);
        Assert.True(TuneDecoder.TryDecode(NativeTuneCaptureTests.Input() with { Conversions = actual }, out var snapshot, out _));
        Assert.NotNull(snapshot);
        Assert.Equal(callback ? TuneFieldStatus.UnsupportedConversion : TuneFieldStatus.UnsupportedUnit,
            snapshot.Fields.Single(field => field.Id == TuneFieldId.FrontSprings).Status);
        Assert.Equal(TuneFieldStatus.Available, snapshot.Fields.Single(field => field.Id == TuneFieldId.FrontTirePressure).Status);
        Assert.False(snapshot.IsComplete);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    [InlineData(false, 4)]
    [InlineData(true, 4)]
    public void InvalidUnitOverridesStillRejectTheRead(bool store, int fault)
    {
        var memory = new Memory(store);
        memory.Units();
        memory.I32(Memory.UnitOwner + 0x14D0 + 27 * 4, 3);
        memory.I32(Memory.OverrideTable + 3 * 20 + 4, 54);
        switch (fault)
        {
            case 0: memory.I32(Memory.UnitOwner + 0x14D0 + 27 * 4, 256); break;
            case 1: memory.U64(Memory.UnitOwner + 0x1568, 0); break;
            case 2: memory.Missing = Memory.OverrideTable + 3 * 20 + 4; break;
            case 3: memory.Conversion(54, double.NaN, 0); break;
            case 4: memory.Conversion(54, 1, memory.ModuleBase + memory.CompatibilityPack.ImageSize); break;
        }
        Assert.Throws<InvalidDataException>(() => NativeTuneCapture.ReadUnitConversions(memory, memory.Reader(), memory.Layout, 1));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public void StableActiveTuneDecodesHistoricalValuesDespiteDivergentDescriptor(bool store, int descriptorPattern)
    {
        var memory = new Memory(store);
        var expected = MiataInput();
        var metadata = memory.CompleteCar(expected);
        if (descriptorPattern == 1)
            memory.I32(Memory.OtherDescriptor + 0x1A0 + 0x40, unchecked((int)(expected.NormalizedCopies[1][0x40 / 4] ^ 1)));
        else if (descriptorPattern == 2)
            foreach (var descriptor in new[] { Memory.Descriptor, Memory.OtherDescriptor })
                for (var word = 0; word < TuneDecoder.NormalizedWordCount; word++)
                    memory.I32(descriptor + 0x1A0 + (ulong)word * 4, BitConverter.SingleToInt32Bits(-1));
        var actual = NativeTuneCapture.Read(memory, metadata, Token, memory.Layout);
        Assert.True(TuneDecoder.TryDecode(actual, out var snapshot, out var failure), failure.ToString());
        Assert.True(TuneDecoder.TryDecode(expected, out var reference, out _));
        Assert.True(reference!.Fields.SequenceEqual(snapshot!.Fields));
        Assert.False(actual.ActiveNormalizedWords.IsDefault);
        Assert.Empty(actual.NormalizedCopies);
        Assert.Equal(33, snapshot.Fields.Count(field => field.Status == TuneFieldStatus.Available));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActiveTuneChangeDuringCaptureStillRejectsTheWholeRead(bool store)
    {
        var memory = new Memory(store);
        var metadata = memory.CompleteCar(MiataInput());
        memory.ChangeActiveOnRepeat = true;
        Assert.Throws<TuneChangedException>(() => NativeTuneCapture.Read(memory, metadata, Token, memory.Layout));
    }

    private static TuneDecodeInput MiataInput()
    {
        using var resource = typeof(NativeTuneReadBoundaryTests).Assembly.GetManifestResourceStream("Wisp.TuneFixtures.miata.json");
        Assert.NotNull(resource);
        return JsonSerializer.Deserialize<TuneDecodeInput>(resource, new JsonSerializerOptions
        { Converters = { new JsonStringEnumConverter() }, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })!;
    }

    private sealed class Memory(bool store) : INativeHudProcessMemory
    {
        internal const ulong Vector = 0x30000, Actor = 0x40000, Provider = 0x100000,
            UnitOwner = 0x200000, OverrideTable = 0x300000, Descriptor = 0x500000, OtherDescriptor = 0x510000;
        private readonly Dictionary<ulong, byte> _bytes = [];
        private int _vectorReads;
        private int _activeReads;
        internal ulong? Missing { get; set; }
        internal bool ChangeVectorOnRepeat { get; set; }
        internal bool ChangeActiveOnRepeat { get; set; }
        internal int Reads { get; private set; }
        public NativeHudCompatibilityPack CompatibilityPack => store ? NativeHudBuildContract.StoreBuiltIn : NativeHudBuildContract.BuiltIn;
        internal NativeTuneLayout Layout => store ? NativeTuneLayout.Store : NativeTuneLayout.Steam;
        public ulong ModuleBase => 0x140000000;
        internal ulong VectorAddress => ModuleBase + CompatibilityPack.SourceVectorRva;
        internal NativeTuneRead Reader() => new(this, Token);
        internal void Cars(int count, bool local = true)
        {
            U64(VectorAddress, Vector);
            U64(VectorAddress + 8, Vector + (ulong)count * 8);
            U64(VectorAddress + 16, Vector + (ulong)count * 8);
            for (var i = 0; i < count; i++)
            {
                var actor = Actor + (ulong)i * 0x10000;
                var provider = Provider + (ulong)i * 0x10000;
                U64(Vector + (ulong)i * 8, actor);
                U64(actor + CompatibilityPack.Fields.SourceProvider, provider);
                U64(provider, ModuleBase + CompatibilityPack.LeadVtableRva);
                foreach (var slot in CompatibilityPack.RequiredVtableSlots)
                    U64(ModuleBase + CompatibilityPack.LeadVtableRva + slot.Key, ModuleBase + slot.Value);
                Put(provider + CompatibilityPack.Fields.LocalPlayerFlag, [(byte)(local ? 1 : 0)]);
                Put(provider + CompatibilityPack.Fields.LocalPlayerProviderFlag, [1]);
            }
        }
        internal void Units(int preference = 1)
        {
            U64(ModuleBase + Layout.Rva(0xA862058), UnitOwner);
            Put(ModuleBase + Layout.Rva(0xA861342), [1]);
            U64(UnitOwner + 0x1568, OverrideTable);
            foreach (var (category, id) in new (ulong, int)[] { (0, 0), (1, 1), (8, 14), (20, 41), (22, 47), (27, 54), (30, 60) })
            {
                I32(UnitOwner + 0x14D0 + category * 4, -1);
                I32(UnitOwner + ((ulong)preference * 0x26 + category) * 20 + 12, id);
                Conversion(id, 1, 0);
            }
        }
        internal void Conversion(int id, double factor, ulong callback)
        {
            var address = ModuleBase + Layout.Rva(0xA861470) + (ulong)id * 40;
            I32(address, id);
            U64(address + 0x10, unchecked((ulong)BitConverter.DoubleToInt64Bits(factor)));
            U64(address + 0x18, callback);
        }
        internal TuneAssetMetadata CompleteCar(TuneDecodeInput input)
        {
            Cars(1);
            I32(Actor + CompatibilityPack.Fields.SourceCarOrdinal, input.CarOrdinal);
            I32(Provider + 0xB9C, (int)input.Drivetrain);
            I32(Provider + 0x24, input.ObservedGearEntryCount);
            Put(Provider + 0x28, new byte[input.ObservedGearEntryCount * 20]);
            for (var index = 0; index < input.ObservedGearEntryCount; index++)
                Single(Provider + 0x28 + (ulong)index * 20, index == 0 ? -3 : 4f / index);
            Single(Provider + 0xB68, 3.5f);
            Single(Provider - 0x570 + 8, input.SpringScale);
            var wheels = new[] { CompatibilityPack.Fields.FirstWheelPointer, CompatibilityPack.Fields.SecondWheelPointer,
                CompatibilityPack.Fields.ThirdWheelPointer, CompatibilityPack.Fields.ThirdWheelPointer + 8 };
            for (var index = 0; index < 4; index++)
            {
                var wheel = 0x600000UL + (ulong)index * 0x1000;
                U64(Provider + wheels[index], wheel);
                I32(wheel + CompatibilityPack.Fields.WheelId, index);
            }
            U64(Actor + 0x2E0, Descriptor); U64(Actor + 0x2D8, OtherDescriptor);
            I32(Descriptor, input.CarOrdinal);
            var copies = new[] { Actor + 0x71F4, OtherDescriptor + 0x1A0, Descriptor + 0x1A0 };
            for (var copy = 0; copy < 3; copy++)
                for (var word = 0; word < 46; word++)
                    I32(copies[copy] + (ulong)word * 4, unchecked((int)input.NormalizedCopies[copy][word]));
            var global = 0x700000UL;
            U64(ModuleBase + Layout.Rva(0xA7DB9E8), global);
            var bounds = new[] { input.Bounds.FinalDrive, input.Bounds.GearRatio, input.Bounds.TirePressure,
                input.Bounds.CamberDegrees, input.Bounds.ToeDegrees, input.Bounds.CasterDegrees };
            var offsets = new ulong[] { 0x43C, 0x444, 0x47C, 0x484, 0x48C, 0x494 };
            for (var index = 0; index < bounds.Length; index++)
            { Single(global + offsets[index], bounds[index].Minimum); Single(global + offsets[index] + 4, bounds[index].Maximum); }
            foreach (var (id, low, high) in new (TuneFieldId, ulong, ulong)[]
            {
                (TuneFieldId.FrontDownforce, 0x3A4, 0x3AC), (TuneFieldId.RearDownforce, 0x404, 0x40C),
                (TuneFieldId.FrontRideHeight, 0x534, 0x538), (TuneFieldId.RearRideHeight, 0x688, 0x68C),
                (TuneFieldId.FrontSprings, 0x554, 0x558), (TuneFieldId.RearSprings, 0x6A8, 0x6AC),
                (TuneFieldId.FrontBump, 0x560, 0x564), (TuneFieldId.RearBump, 0x6B4, 0x6B8),
                (TuneFieldId.FrontRebound, 0x584, 0x588), (TuneFieldId.RearRebound, 0x6D8, 0x6DC),
                (TuneFieldId.FrontAntiroll, 0x5F0, 0x5F4), (TuneFieldId.RearAntiroll, 0x744, 0x748)
            })
            { Single(Actor + 0x310 + low, input.CarRanges[id].Minimum); Single(Actor + 0x310 + high, input.CarRanges[id].Maximum); }
            var locale = 0x710000UL;
            U64(ModuleBase + Layout.Rva(0xA862060), locale);
            I32(locale + 0x34, input.UnitPreference);
            Units(input.UnitPreference);
            foreach (var conversion in input.Conversions.Values) Conversion(conversion.UnitId, conversion.Factor, 0);
            Single(ModuleBase + Layout.Rva(0x64BA6E8), input.Format.DegreesToRadians);
            Double(ModuleBase + Layout.Rva(0x6446D18), input.Format.PositiveHalf);
            Double(ModuleBase + Layout.Rva(0x65AC730), input.Format.NegativeHalf);
            Double(ModuleBase + Layout.Rva(0x64FBE80), input.Format.OneTenth);
            Double(ModuleBase + Layout.Rva(0x640EE78), input.Format.Ten);
            var indices = 0x720000UL;
            U64(ModuleBase + Layout.Rva(0x8F12A78), indices);
            var parts = new (TunePartId, ulong)[]
            {
                (TunePartId.Engine, 0), (TunePartId.Drivetrain, 1), (TunePartId.CarBody, 2), (TunePartId.Motor, 3),
                (TunePartId.Brakes, 4), (TunePartId.SpringDamper, 5), (TunePartId.FrontAntiroll, 6), (TunePartId.RearAntiroll, 7),
                (TunePartId.RearAero, 9), (TunePartId.Transmission, 0x1F), (TunePartId.Differential, 0x21), (TunePartId.FrontAero, 0x22)
            };
            for (var index = 0; index < parts.Length; index++)
            {
                I32(indices + parts[index].Item2 * 4, index);
                I32(Descriptor + 0x10 + (ulong)index * 4, input.Parts.Single(part => part.Kind == parts[index].Item1).InstalledId);
            }
            return new TuneAssetMetadata(input.Parts.Where(part => part.InstalledId >= 0)
                .Select(part => new TuneAssetRow(part.Kind, input.CarOrdinal, part.InstalledId, part.Level, input.CarOrdinal)));
        }
        private void Single(ulong address, float value) => I32(address, BitConverter.SingleToInt32Bits(value));
        private void Double(ulong address, double value) => U64(address, unchecked((ulong)BitConverter.DoubleToInt64Bits(value)));
        internal void U64(ulong address, ulong value) { Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(bytes, value); Put(address, bytes); }
        internal void I32(ulong address, int value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, value); Put(address, bytes); }
        private void Put(ulong address, ReadOnlySpan<byte> values)
        { for (var i = 0; i < values.Length; i++) _bytes[address + (ulong)i] = values[i]; }
        public bool TryReadBytes(ulong address, Span<byte> destination)
        {
            Reads++;
            for (var i = 0; i < destination.Length; i++)
                if (address + (ulong)i == Missing || !_bytes.TryGetValue(address + (ulong)i, out destination[i])) return false;
            if (address == VectorAddress && ++_vectorReads > 1 && ChangeVectorOnRepeat) destination[0] ^= 8;
            if (address == Actor + 0x71F4 && ++_activeReads > 1 && ChangeActiveOnRepeat) destination[0x40] ^= 1;
            return true;
        }
        public bool TryReadByte(ulong address, out byte value) { value = 0; return false; }
        public bool TryReadUInt32(ulong address, out uint value) { value = 0; return false; }
        public bool TryReadUInt64(ulong address, out ulong value) { value = 0; return false; }
        public bool TryReadSingle(ulong address, out float value) { value = 0; return false; }
        public void Dispose() { }
    }
}
