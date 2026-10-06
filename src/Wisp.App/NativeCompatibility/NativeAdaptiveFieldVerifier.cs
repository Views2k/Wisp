using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Wisp.App;

// Checks field code against a separate Store reference namespace. The caller
// owns capability rollback and the final live recheck through FinishProof.
internal static class NativeAdaptiveFieldVerifier
{
    private sealed record FieldSpec(string Path, string Name, ulong Offset, int InstructionOffset, byte[] Prefix);
    private sealed record Relocation(int Offset, int Width, int Next, string Kind, uint ReferenceTarget,
        string Section, uint? InternalOffset);
    private sealed record Selected(Signature Signature, uint Rva);
    private sealed record Candidate(uint Initializer, uint Getter, uint Name);
    private sealed record TypeIdentity(uint Entry, uint Canonical, bool HasAlias)
    {
        internal bool Accepts(uint target) => target == Entry || HasAlias && target == Canonical;
    }

    internal static void Validate(NativeAdaptiveImage image, JsonElement storeProfile,
        NativeGaugeLayout expectedLayout, uint childVtableRva)
    {
        using var typeBody = ReadTypeBodyTemplate();
        Validate(image, storeProfile, expectedLayout, childVtableRva, typeBody.RootElement);
    }

    // Allows synthetic instruction fixtures without weakening the release-owned
    // type body used by production callers.
    internal static void Validate(NativeAdaptiveImage image, JsonElement storeProfile,
        NativeGaugeLayout expectedLayout, uint childVtableRva, JsonElement typeBodyTemplate)
    {
        try
        {
            ValidateCore(image, storeProfile, expectedLayout, childVtableRva, new Signature(typeBodyTemplate));
        }
        catch (Exception error) when (error is OverflowException or ArgumentException or InvalidOperationException
            or KeyNotFoundException or FormatException or JsonException)
        {
            throw new InvalidDataException("Adaptive field template is invalid", error);
        }
    }

    private static void ValidateCore(NativeAdaptiveImage image, JsonElement storeProfile,
        NativeGaugeLayout expectedLayout, uint childVtableRva, Signature typeBody)
    {
        image.CheckBudget();
        if (storeProfile.GetProperty("platform").GetString() != "store")
            throw new InvalidDataException("field-template-platform");
        var specs = Specifications(expectedLayout);
        var fields = storeProfile.GetProperty("fieldWitnesses").EnumerateArray().ToArray();
        if (fields.Length != 18 || fields.Select(f => f.GetProperty("path").GetString()).Distinct().Count() != 18)
            throw new InvalidDataException("witness-count");
        var signatures = storeProfile.GetProperty("signatures").EnumerateArray()
            .ToDictionary(s => s.GetProperty("id").GetString()!, s => s, StringComparer.Ordinal);
        var cache = new Dictionary<string, uint[]>(StringComparer.Ordinal);
        var selected = new List<Selected>();
        var relationships = new Dictionary<uint, uint>();
        var typeReferences = new HashSet<uint>();
        var typeSlot = checked(childVtableRva + 24);
        if (typeSlot % 8 != 0 || image.Kind(typeSlot, 8) != "readonly")
            throw new InvalidDataException("type-slot");
        var typeEntry = image.Pointer(typeSlot);
        if (image.Kind(typeEntry, 5) != "code") throw new InvalidDataException("type-entry");
        var entry = image.Bytes(typeEntry, 5);
        var canonicalType = typeEntry;
        var hasAlias = entry[0] == 0xE9;
        if (hasAlias)
        {
            canonicalType = checked((uint)((long)typeEntry + 5 + BinaryPrimitives.ReadInt32LittleEndian(entry[1..])));
            if (canonicalType == typeEntry || image.Kind(canonicalType, typeBody.Length) != "code" ||
                image.Bytes(canonicalType, 1)[0] == 0xE9)
                throw new InvalidDataException("type-alias-chain");
        }
        var identity = new TypeIdentity(typeEntry, canonicalType, hasAlias);
        if (!Match(image, typeBody, canonicalType)) throw new InvalidDataException("type-body-template-changed");
        BindSignature(image, typeBody, canonicalType, identity, typeReferences, relationships, selected);
        image.Remember(typeSlot, image.Bytes(typeSlot, 8));
        if (hasAlias) image.Remember(typeEntry, entry);
        image.Remember(canonicalType, image.Bytes(canonicalType, typeBody.Length));

        foreach (var spec in specs)
        {
            image.CheckBudget();
            var field = fields.Single(f => f.GetProperty("path").GetString() == spec.Path);
            if (field.GetProperty("name").GetString() != spec.Name || field.GetProperty("value").GetUInt64() != spec.Offset ||
                field.GetProperty("childTypeGetterSlotOffset").GetInt32() != 24 || field.GetProperty("nameEncoding").GetString() != "ascii")
                throw new InvalidDataException("field-template-contract");
            var expectedName = Encoding.ASCII.GetBytes(spec.Name + "\0");
            if (!Convert.FromHexString(field.GetProperty("nameHex").GetString()!).AsSpan().SequenceEqual(expectedName))
                throw new InvalidDataException("field-template-name");
            var initializer = new Signature(signatures[field.GetProperty("initializerSignature").GetString()!]);
            var getter = new Signature(signatures[field.GetProperty("getterSignature").GetString()!]);
            var getterOperand = Operand(initializer, field.GetProperty("getterOperandOffset").GetInt32());
            var nameOperand = Operand(initializer, field.GetProperty("nameOperandOffset").GetInt32());
            var typeOperand = Operand(initializer, field.GetProperty("typeGetterOperandOffset").GetInt32());
            if (getterOperand.ReferenceTarget != getter.ReferenceRva) throw new InvalidDataException("getter-template-link");
            var getterTypes = getter.Relocations.Where(r => r.ReferenceTarget == typeOperand.ReferenceTarget).ToArray();
            if (getterTypes.Length != 1 || getterTypes[0].Offset != 24 ||
                typeOperand.Kind != "relative" || typeOperand.Width != 4 || typeOperand.Next != typeOperand.Offset + 4 ||
                getterTypes[0].Kind != "relative" || getterTypes[0].Width != 4 || getterTypes[0].Next != 28)
                throw new InvalidDataException("getter-type-template");
            typeReferences.Add(typeOperand.ReferenceTarget);
            var candidates = new List<Candidate>();
            foreach (var at in Find(image, initializer, cache))
            {
                var name = Target(image, initializer, at, nameOperand);
                if (image.Kind(name, expectedName.Length) != "readonly" || !image.Bytes(name, expectedName.Length).SequenceEqual(expectedName) ||
                    !identity.Accepts(Target(image, initializer, at, typeOperand))) continue;
                var actualGetter = Target(image, initializer, at, getterOperand);
                if (!Match(image, getter, actualGetter) || !identity.Accepts(Target(image, getter, actualGetter, getterTypes[0])) ||
                    !CheckMember(image.Bytes(actualGetter, getter.Length), getter, spec)) continue;
                if (typeOperand.Offset < 1 || image.Bytes(checked(at + (uint)typeOperand.Offset - 1), 1)[0] != 0xE8 ||
                    image.Bytes(checked(actualGetter + 23), 1)[0] != 0xE8) continue;
                candidates.Add(new Candidate(at, actualGetter, name));
                if (candidates.Count > 1) break;
            }
            if (candidates.Count != 1)
                throw new InvalidDataException(candidates.Count == 0 ? "no-complete-typed-match" : "ambiguous-complete-typed-match");
            var choice = candidates[0];
            BindSignature(image, initializer, choice.Initializer, identity, typeReferences, relationships, selected);
            BindSignature(image, getter, choice.Getter, identity, typeReferences, relationships, selected);
            image.Remember(choice.Initializer, image.Bytes(choice.Initializer, initializer.Length));
            image.Remember(choice.Getter, image.Bytes(choice.Getter, getter.Length));
            image.Remember(choice.Name, image.Bytes(choice.Name, expectedName.Length));
        }
        foreach (var (previous, current) in relationships)
            foreach (var range in selected)
                if (previous >= range.Signature.ReferenceRva && previous - range.Signature.ReferenceRva < range.Signature.Length &&
                    current != checked(range.Rva + previous - range.Signature.ReferenceRva))
                    throw new InvalidDataException("template-interior-reference-conflict");
        image.CheckBudget();
    }

    private static Relocation Operand(Signature signature, int offset) => signature.Relocations.Single(r => r.Offset == offset);

    private static void BindSignature(NativeAdaptiveImage image, Signature signature, uint rva, TypeIdentity identity,
        HashSet<uint> typeReferences, Dictionary<uint, uint> relationships, List<Selected> selected)
    {
        void Bind(uint previous, uint current)
        {
            if (typeReferences.Contains(previous))
            {
                if (!identity.Accepts(current)) throw new InvalidDataException("type-alias-reference-conflict");
                current = identity.Canonical;
            }
            if (relationships.TryGetValue(previous, out var existing) && existing != current)
                throw new InvalidDataException("template-shared-reference-conflict");
            relationships[previous] = current;
        }
        Bind(signature.ReferenceRva, rva);
        foreach (var relocation in signature.Relocations) Bind(relocation.ReferenceTarget, Target(image, signature, rva, relocation));
        selected.Add(new Selected(signature, rva));
    }

    private static bool CheckMember(ReadOnlySpan<byte> bytes, Signature getter, FieldSpec spec)
    {
        var displacement = spec.InstructionOffset + spec.Prefix.Length;
        if (bytes.Length < displacement + (spec.InstructionOffset == 57 ? 5 : 4) || !bytes.Slice(spec.InstructionOffset, spec.Prefix.Length).SequenceEqual(spec.Prefix) ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(displacement, 4)) != spec.Offset ||
            getter.Relocations.Any(r => r.Offset < displacement + 4 && displacement < r.Offset + r.Width)) return false;
        return spec.InstructionOffset != 57 || bytes[displacement + 4] == 0;
    }

    private static uint[] Find(NativeAdaptiveImage image, Signature signature, Dictionary<string, uint[]> cache)
    {
        if (cache.TryGetValue(signature.ScanKey, out var remembered)) return remembered;
        var result = new List<uint>();
        var hits = 0;
        foreach (var section in image.Sections.Where(s => s.Kind == "code" && s.Bytes is not null))
        {
            var bytes = section.Bytes!;
            for (var start = 0; start < bytes.Length;)
            {
                image.CheckBudget();
                var at = bytes.AsSpan(start).IndexOf(signature.Anchor);
                if (at < 0) break;
                at += start;
                if (++hits > 131072) throw new InvalidDataException("template-anchor-limit");
                var candidate = at - signature.AnchorOffset;
                if (candidate >= 0 && candidate <= bytes.Length - signature.Length &&
                    Match(image, signature, checked(section.Rva + (uint)candidate)))
                {
                    result.Add(checked(section.Rva + (uint)candidate));
                    if (result.Count > 8192) throw new InvalidDataException("template-match-limit");
                }
                start = at + 1;
            }
        }
        return cache[signature.ScanKey] = result.ToArray();
    }

    private static bool Match(NativeAdaptiveImage image, Signature signature, uint rva)
    {
        if (image.Kind(rva, signature.Length) != "code") return false;
        var normalized = image.Bytes(rva, signature.Length).ToArray();
        foreach (var relocation in signature.Relocations)
        {
            uint target;
            try { target = Target(image, signature, rva, relocation); }
            catch (Exception e) when (e is InvalidDataException or OverflowException) { return false; }
            if (image.Kind(target) != relocation.Section || relocation.InternalOffset is { } inner && target != checked(rva + inner)) return false;
            normalized.AsSpan(relocation.Offset, relocation.Width).Clear();
        }
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(normalized), signature.Hash);
    }

    private static uint Target(NativeAdaptiveImage image, Signature signature, uint rva, Relocation relocation)
    {
        var bytes = image.Bytes(rva, signature.Length).Slice(relocation.Offset, relocation.Width);
        long target = relocation.Kind switch
        {
            "relative" => (long)rva + relocation.Next + (relocation.Width == 1 ? (sbyte)bytes[0] : BinaryPrimitives.ReadInt32LittleEndian(bytes)),
            "image" => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            "absolute" => checked((long)(BinaryPrimitives.ReadUInt64LittleEndian(bytes) - image.ModuleBase)),
            _ => throw new InvalidDataException("template-relocation-kind")
        };
        if (target < 0 || target >= image.ImageSize) throw new InvalidDataException("template-target-bounds");
        return (uint)target;
    }

    private sealed class Signature
    {
        internal Signature(JsonElement value)
        {
            ReferenceRva = value.GetProperty("referenceRva").GetUInt32();
            Length = value.GetProperty("length").GetInt32();
            Hash = Convert.FromHexString(value.GetProperty("normalizedSha256").GetString()!);
            Anchor = Convert.FromHexString(value.GetProperty("anchorHex").GetString()!);
            AnchorOffset = value.GetProperty("anchorOffset").GetInt32();
            if (value.GetProperty("section").GetString() != "code") throw new InvalidDataException("template-section");
            Relocations = value.GetProperty("relocations").EnumerateArray().Select(r => new Relocation(
                r.GetProperty("offset").GetInt32(), r.GetProperty("width").GetInt32(), r.GetProperty("next").GetInt32(),
                r.GetProperty("kind").GetString()!, r.GetProperty("targetRva").GetUInt32(), r.GetProperty("expectedTargetSection").GetString()!,
                r.TryGetProperty("internalOffset", out var inner) && inner.ValueKind != JsonValueKind.Null ? inner.GetUInt32() : null)).ToArray();
            if (Length is < 1 or > 8192 || Hash.Length != 32 || Anchor.Length is < 4 or > 256 || AnchorOffset < 0 || AnchorOffset > Length - Anchor.Length)
                throw new InvalidDataException("template-signature-bounds");
            var occupied = new bool[Length];
            foreach (var r in Relocations)
            {
                if (r.Width is not (1 or 4 or 8) || r.Offset < 0 || r.Offset > Length - r.Width ||
                    r.Kind is not ("relative" or "absolute" or "image") || r.Kind == "relative" && (r.Width == 8 || r.Next < r.Offset + r.Width || r.Next > Length) ||
                    r.Kind == "absolute" && r.Width != 8 || r.Kind == "image" && r.Width != 4 || r.Section is not ("code" or "readonly" or "data" or "image"))
                    throw new InvalidDataException("template-relocation-bounds");
                for (var i = r.Offset; i < r.Offset + r.Width; i++)
                {
                    if (occupied[i] || i >= AnchorOffset && i < AnchorOffset + Anchor.Length) throw new InvalidDataException("template-relocation-overlap");
                    occupied[i] = true;
                }
            }
            ScanKey = Convert.ToHexString(Hash) + ":" + Length + ":" + AnchorOffset + ":" + Convert.ToHexString(Anchor) + ":" +
                string.Join(";", Relocations.Select(r => $"{r.Offset},{r.Width},{r.Next},{r.Kind},{r.Section},{r.InternalOffset}"));
        }
        internal uint ReferenceRva { get; }
        internal int Length { get; }
        internal byte[] Hash { get; }
        internal byte[] Anchor { get; }
        internal int AnchorOffset { get; }
        internal Relocation[] Relocations { get; }
        internal string ScanKey { get; }
    }

    private static FieldSpec[] Specifications(NativeGaugeLayout g)
    {
        static FieldSpec F(string name, string field, ulong offset, string kind) => new("/nativeGauge/" + field, name, offset,
            kind == "bool" ? 57 : 48, Convert.FromHexString(kind == "float" ? "F30F1087" : kind == "bool" ? "80BF" : "8B87"));
        return [F("NeedleAngle", "childAngleOffset", g.ChildAngleOffset, "float"), F("NeedleBlur", "childBlurOffset", g.ChildBlurOffset, "float"),
            F("SpeedDigitOne", "childSpeedDigitOneOffset", g.ChildSpeedDigitOneOffset, "int"), F("SpeedDigitTen", "childSpeedDigitTenOffset", g.ChildSpeedDigitTenOffset, "int"),
            F("SpeedDigitHundred", "childSpeedDigitHundredOffset", g.ChildSpeedDigitHundredOffset, "int"), F("SpeedLessOrEqualOne", "childSpeedLessOrEqualOneOffset", g.ChildSpeedLessOrEqualOneOffset, "bool"),
            F("SpeedLessTen", "childSpeedLessTenOffset", g.ChildSpeedLessTenOffset, "bool"), F("SpeedLessHundred", "childSpeedLessHundredOffset", g.ChildSpeedLessHundredOffset, "bool"),
            F("AreHeadlightsOn", "childHeadlightsOnOffset", g.ChildHeadlightsOnOffset, "bool"), F("Gear", "childGearOffset", g.ChildGearOffset, "int"),
            F("GearPrevious", "childGearPreviousOffset", g.ChildGearPreviousOffset, "int"), F("GearNext", "childGearNextOffset", g.ChildGearNextOffset, "int"),
            F("RegenFillAmount", "childRegenOffset", g.ChildRegenOffset, "float"), F("PowerFillAmount", "childPowerOffset", g.ChildPowerOffset, "float"),
            F("GearGaugeState", "childGearGaugeStateOffset", g.ChildGearGaugeStateOffset, "int"), F("UseDriveFor1", "childUseDriveFor1Offset", g.ChildUseDriveFor1Offset, "bool"),
            F("RegenPowerRatio", "childRatioOffset", g.ChildRatioOffset, "float"), F("SpeedometerOption", "childModeOffset", g.ChildModeOffset, "int")];
    }

    // Complete old Store type body; independently matched both current Store
    // and captured Steam bodies after declared relocation normalization.
    // All field and type templates share this isolated OLD Store namespace.
    private static JsonDocument ReadTypeBodyTemplate()
    {
        return JsonDocument.Parse(TypeBodyTemplate);
    }

    private const string TypeBodyTemplate = """
{"referenceRva":58668368,"length":505,"normalizedSha256":"96C92ABE8C4B1E06321A753EAC44A2BD52E4112C7BB873572313D42D91918641","anchorHex":"48895C24104889742418574881EC80","anchorOffset":0,"section":"code","relocations":[{"offset":20,"width":4,"next":24,"kind":"relative","targetRva":180139504,"expectedTargetSection":"data","internalOffset":null},{"offset":49,"width":4,"next":53,"kind":"relative","targetRva":177820000,"expectedTargetSection":"data","internalOffset":null},{"offset":55,"width":4,"next":59,"kind":"relative","targetRva":58668586,"expectedTargetSection":"code","internalOffset":218},{"offset":62,"width":4,"next":66,"kind":"relative","targetRva":177819824,"expectedTargetSection":"data","internalOffset":null},{"offset":91,"width":4,"next":95,"kind":"relative","targetRva":104645616,"expectedTargetSection":"readonly","internalOffset":null},{"offset":130,"width":4,"next":134,"kind":"relative","targetRva":98646732,"expectedTargetSection":"code","internalOffset":null},{"offset":146,"width":4,"next":150,"kind":"relative","targetRva":3864832,"expectedTargetSection":"code","internalOffset":null},{"offset":179,"width":4,"next":183,"kind":"relative","targetRva":177819824,"expectedTargetSection":"data","internalOffset":null},{"offset":184,"width":4,"next":188,"kind":"relative","targetRva":61564832,"expectedTargetSection":"code","internalOffset":null},{"offset":191,"width":4,"next":195,"kind":"relative","targetRva":103282304,"expectedTargetSection":"code","internalOffset":null},{"offset":196,"width":4,"next":200,"kind":"relative","targetRva":94177100,"expectedTargetSection":"code","internalOffset":null},{"offset":204,"width":4,"next":208,"kind":"relative","targetRva":177820000,"expectedTargetSection":"data","internalOffset":null},{"offset":209,"width":4,"next":213,"kind":"relative","targetRva":94177304,"expectedTargetSection":"code","internalOffset":null},{"offset":214,"width":4,"next":218,"kind":"relative","targetRva":58668427,"expectedTargetSection":"code","internalOffset":59},{"offset":221,"width":4,"next":225,"kind":"relative","targetRva":177820000,"expectedTargetSection":"data","internalOffset":null},{"offset":226,"width":4,"next":230,"kind":"relative","targetRva":94177412,"expectedTargetSection":"code","internalOffset":null},{"offset":232,"width":4,"next":237,"kind":"relative","targetRva":177820000,"expectedTargetSection":"data","internalOffset":null},{"offset":239,"width":4,"next":243,"kind":"relative","targetRva":58668427,"expectedTargetSection":"code","internalOffset":59},{"offset":246,"width":4,"next":250,"kind":"relative","targetRva":58144224,"expectedTargetSection":"code","internalOffset":null},{"offset":276,"width":4,"next":280,"kind":"relative","targetRva":3864640,"expectedTargetSection":"code","internalOffset":null},{"offset":289,"width":4,"next":293,"kind":"relative","targetRva":104645664,"expectedTargetSection":"readonly","internalOffset":null},{"offset":302,"width":4,"next":306,"kind":"relative","targetRva":114154208,"expectedTargetSection":"readonly","internalOffset":null},{"offset":311,"width":4,"next":315,"kind":"relative","targetRva":114154224,"expectedTargetSection":"readonly","internalOffset":null},{"offset":325,"width":4,"next":329,"kind":"relative","targetRva":177825984,"expectedTargetSection":"data","internalOffset":null},{"offset":333,"width":4,"next":337,"kind":"relative","targetRva":177826016,"expectedTargetSection":"data","internalOffset":null},{"offset":338,"width":1,"next":339,"kind":"relative","targetRva":58668766,"expectedTargetSection":"code","internalOffset":398},{"offset":342,"width":4,"next":346,"kind":"relative","targetRva":177826016,"expectedTargetSection":"data","internalOffset":null},{"offset":347,"width":4,"next":351,"kind":"relative","targetRva":94177412,"expectedTargetSection":"code","internalOffset":null},{"offset":353,"width":4,"next":358,"kind":"relative","targetRva":177826016,"expectedTargetSection":"data","internalOffset":null},{"offset":359,"width":1,"next":360,"kind":"relative","targetRva":58668766,"expectedTargetSection":"code","internalOffset":398},{"offset":369,"width":4,"next":373,"kind":"relative","targetRva":61733360,"expectedTargetSection":"code","internalOffset":null},{"offset":376,"width":4,"next":380,"kind":"relative","targetRva":103275760,"expectedTargetSection":"code","internalOffset":null},{"offset":381,"width":4,"next":385,"kind":"relative","targetRva":94177100,"expectedTargetSection":"code","internalOffset":null},{"offset":389,"width":4,"next":393,"kind":"relative","targetRva":177826016,"expectedTargetSection":"data","internalOffset":null},{"offset":394,"width":4,"next":398,"kind":"relative","targetRva":94177304,"expectedTargetSection":"code","internalOffset":null},{"offset":401,"width":4,"next":406,"kind":"relative","targetRva":177826008,"expectedTargetSection":"data","internalOffset":null},{"offset":410,"width":4,"next":414,"kind":"relative","targetRva":177825984,"expectedTargetSection":"data","internalOffset":null},{"offset":425,"width":4,"next":429,"kind":"relative","targetRva":58668455,"expectedTargetSection":"code","internalOffset":87},{"offset":448,"width":1,"next":449,"kind":"relative","targetRva":58668862,"expectedTargetSection":"code","internalOffset":494},{"offset":469,"width":1,"next":470,"kind":"relative","targetRva":58668862,"expectedTargetSection":"code","internalOffset":494},{"offset":489,"width":4,"next":493,"kind":"relative","targetRva":104291080,"expectedTargetSection":"readonly","internalOffset":null},{"offset":495,"width":4,"next":499,"kind":"relative","targetRva":3870928,"expectedTargetSection":"code","internalOffset":null},{"offset":500,"width":4,"next":504,"kind":"relative","targetRva":58668455,"expectedTargetSection":"code","internalOffset":87}]}
""";
}
