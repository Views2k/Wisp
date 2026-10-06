using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wisp.App;

// Profiles are release-owned resources, never downloaded or accepted from a user.
// Only address operands may move; opcodes, member offsets and constants must match.
internal sealed class NativeAdaptiveResolver
{
    private readonly NativeAdaptiveImage _image;
    private readonly JsonElement _profile;
    private readonly Dictionary<string, Signature> _signatures;
    private readonly Dictionary<(string Id, int Limit), uint[]> _locations = [];
    private readonly Dictionary<string, uint> _resolved = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, uint> _relationships = [];
    private readonly Dictionary<string, uint> _matched = new(StringComparer.Ordinal);
    private readonly HashSet<string>? _activePaths;
    private readonly HashSet<string>? _capabilities;
    private readonly JsonElement? _gaugeFieldProfile;
    private readonly NativeGaugeLayout? _referenceGauge;
    internal bool HasTransientCapabilityFailure { get; private set; }

    internal NativeAdaptiveResolver(NativeAdaptiveImage image, JsonElement profile, string[]? capabilities = null,
        JsonElement? gaugeFieldProfile = null, NativeGaugeLayout? referenceGauge = null)
    {
        _image = image;
        _profile = profile;
        _gaugeFieldProfile = gaugeFieldProfile;
        _referenceGauge = referenceGauge;
        if (gaugeFieldProfile.HasValue != (referenceGauge is not null) ||
            gaugeFieldProfile is { } fields && (profile.GetProperty("platform").GetString() != "steam" ||
                fields.GetProperty("platform").GetString() != "store"))
            throw new InvalidDataException("Adaptive field profile identity");
        if (capabilities is not null)
        {
            _capabilities = capabilities.ToHashSet(StringComparer.Ordinal);
            _activePaths = capabilities.SelectMany(c => profile.GetProperty("capabilities").GetProperty(c)
                .GetProperty("requiredPaths").EnumerateArray().Select(p => p.GetString()!)).ToHashSet(StringComparer.Ordinal);
        }
        if (profile.GetProperty("schemaVersion").GetInt32() != 1 ||
            profile.GetProperty("signatures").GetArrayLength() is < 1 or > 2048 ||
            profile.GetProperty("bindings").GetArrayLength() is < 1 or > 4096)
            throw new InvalidDataException("Adaptive profile bounds");
        _signatures = profile.GetProperty("signatures").EnumerateArray().Select(s => new Signature(s))
            .ToDictionary(s => s.Id, StringComparer.Ordinal);
    }

    internal IReadOnlyDictionary<string, uint> Resolve()
    {
        var groups = _profile.GetProperty("bindings").EnumerateArray()
            .Where(b => Active(b.GetProperty("path").GetString()!))
            .GroupBy(b => b.GetProperty("path").GetString()!, StringComparer.Ordinal).ToArray();
        for (var pass = 0; pass < 4; pass++)
        {
            var progress = false;
            foreach (var group in groups)
            {
                if (_resolved.ContainsKey(group.Key)) continue;
                foreach (var binding in group.OrderBy(BindingPriority))
                {
                    _image.CheckBudget();
                    if (!TryBind(binding, out var value)) continue;
                    _resolved.Add(group.Key, value);
                    progress = true;
                    break;
                }
            }
            if (!progress) break;
        }
        var missing = groups.FirstOrDefault(g => !_resolved.ContainsKey(g.Key));
        if (missing is not null) throw new InvalidDataException("Adaptive reader role unavailable: " + missing.Key);
        foreach (var check in _profile.GetProperty("checks").EnumerateArray())
        {
            var path = check.GetProperty("path").GetString()!;
            if (!Active(path)) continue;
            if (check.GetProperty("kind").GetString() == "region" && _resolved.TryGetValue(path, out var region))
            {
                if (_image.Kind(region) != check.GetProperty("section").GetString() ||
                    check.TryGetProperty("unwindEntry", out var unwind) && unwind.GetBoolean() && !_image.IsUnwindEntry(region))
                    throw new InvalidDataException("Adaptive reader region changed: " + path);
                continue;
            }
            if (check.GetProperty("kind").GetString() != "bytes" || !_resolved.TryGetValue(path, out var rva) ||
                !Match(_signatures[check.GetProperty("signature").GetString()!], rva, remember: true))
                throw new InvalidDataException("Adaptive reader guard changed: " + path);
        }
        // A table found through a constructor/reference must still pass its full
        // slot and inheritance checks, just as a table found by a method anchor.
        foreach (var group in groups)
            foreach (var binding in group.Where(b => b.GetProperty("kind").GetString() == "table"))
                if (!CheckTable(binding, _resolved[group.Key], remember: true))
                    throw new InvalidDataException("Adaptive table changed: " + group.Key);
        ValidateFields();
        ValidateRelationships();
        return _resolved;
    }

    internal bool TryEnableCapability(string capability, out string reason)
    {
        if (_capabilities is null || _activePaths is null || !_capabilities.Contains("core") || _resolved.Count == 0)
            throw new InvalidOperationException("Resolve core compatibility before optional readers");
        var definition = _profile.GetProperty("capabilities").GetProperty(capability);
        reason = "The " + capability + " reader layout could not be verified";
        if (!definition.GetProperty("fieldWitnessComplete").GetBoolean() &&
            (capability != "gauge" || _gaugeFieldProfile is null)) return false;
        if (_capabilities.Contains(capability)) return true;
        var resolved = _resolved.ToArray();
        var relationships = _relationships.ToArray();
        var matched = _matched.ToArray();
        var active = _activePaths.ToArray();
        var proof = _image.SaveProof();
        _capabilities.Add(capability);
        foreach (var path in definition.GetProperty("requiredPaths").EnumerateArray()) _activePaths.Add(path.GetString()!);
        try
        {
            Resolve();
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or OverflowException)
        {
            HasTransientCapabilityFailure |= exception is IOException;
            _capabilities.Remove(capability);
            _activePaths.Clear();
            _activePaths.UnionWith(active);
            _resolved.Clear();
            foreach (var entry in resolved) _resolved.Add(entry.Key, entry.Value);
            _relationships.Clear();
            foreach (var entry in relationships) _relationships.Add(entry.Key, entry.Value);
            _matched.Clear();
            foreach (var entry in matched) _matched.Add(entry.Key, entry.Value);
            _image.RestoreProof(proof);
            return false;
        }
    }

    internal JsonObject CreatePack(byte[] referenceJson, string version, long executableLength,
        string? executableHash, string? packageName)
    {
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(referenceJson)),
                _profile.GetProperty("referencePackSha256").GetString(), StringComparison.Ordinal))
            throw new InvalidDataException("Adaptive reference identity");
        var pack = JsonNode.Parse(referenceJson)?.AsObject() ?? throw new InvalidDataException("Adaptive reference JSON");
        var fieldProfile = _gaugeFieldProfile ?? _profile;
        if (_capabilities?.Contains("gauge") != false && fieldProfile.TryGetProperty("fieldWitnesses", out var witnesses))
            foreach (var field in witnesses.EnumerateArray())
            {
                var path = field.GetProperty("path").GetString()!;
                var property = path.Split('/');
                if (property.Length != 3 || property[1] != "nativeGauge" ||
                    pack["nativeGauge"]?[property[2]]?.GetValue<uint>() != field.GetProperty("value").GetUInt32())
                    throw new InvalidDataException("Adaptive field differs from the reader contract");
            }
        foreach (var (path, value) in _resolved) Set(pack, path, JsonValue.Create(value));
        if (_capabilities is not null && !_capabilities.Contains("gauge")) pack["nativeGauge"] = null;
        if (_capabilities is not null && !_capabilities.Contains("tune")) pack["tune"] = null;
        pack["gameVersion"] = version;
        pack["imageSize"] = _image.ImageSize;
        pack["id"] = "fh6-runtime-" + _profile.GetProperty("platform").GetString() + "-v1";
        pack["revision"] = 1;
        if (packageName is null)
        {
            pack["executableLength"] = executableLength;
            pack["executableSha256"] = executableHash;
        }
        else
        {
            pack["storeIdentity"]!["packageFullName"] = packageName;
            pack["storeIdentity"]!["timeDateStamp"] = _image.TimeDateStamp;
            var guards = pack["storeIdentity"]!["codeGuards"]!.AsArray();
            for (var index = guards.Count - 1; index >= 0; index--)
                if (!Active("/storeIdentity/codeGuards/" + index + "/rva")) guards.RemoveAt(index);
            Rehash(guards);
        }
        if (pack["tune"] is { } tune) Rehash(tune["codeGuards"]!.AsArray());
        return pack;
    }

    private void Rehash(JsonArray guards)
    {
        foreach (var guard in guards)
        {
            var rva = guard!["rva"]!.GetValue<uint>();
            var length = guard["length"]!.GetValue<int>();
            guard["sha256"] = Convert.ToHexString(SHA256.HashData(_image.Bytes(rva, length, proof: true)));
        }
    }

    private static int BindingPriority(JsonElement b) => b.GetProperty("kind").GetString() switch
    {
        "ownership" => 0,
        "slot" => 1,
        "signature" => 2,
        "reference" => 3,
        "table" => 4,
        _ => 5
    };

    private bool Active(string path) => _activePaths is null || _activePaths.Contains(path);

    private bool TryBind(JsonElement binding, out uint value)
    {
        value = 0;
        var kind = binding.GetProperty("kind").GetString();
        if (kind == "ownership")
            return binding.GetProperty("path").GetString() == "/nativeGauge/hudTypeTokenRva";
        if (kind == "slot")
        {
            if (!_resolved.TryGetValue(binding.GetProperty("tablePath").GetString()!, out var table)) return false;
            value = _image.Pointer(checked(table + binding.GetProperty("slotOffset").GetUInt32()), proof: true);
            return binding.TryGetProperty("signature", out var slotSignature) && slotSignature.ValueKind == JsonValueKind.String
                ? Match(_signatures[slotSignature.GetString()!], value, remember: true)
                : _image.Kind(value) == "code";
        }
        if (kind == "table") return TryTable(binding, out value);
        if (kind is not ("signature" or "reference")) throw new InvalidDataException("Adaptive binding kind");
        var signature = _signatures[binding.GetProperty("signature").GetString()!];
        var found = Find(signature);
        if (found.Length != 1) return false;
        if (!Match(signature, found[0], remember: true)) return false;
        value = kind == "signature"
            ? checked(found[0] + (binding.TryGetProperty("resultOffset", out var offset) ? offset.GetUInt32() : 0))
            : Target(signature, found[0], signature.Relocations.Single(r => r.Offset == binding.GetProperty("operandOffset").GetInt32()));
        return true;
    }

    private uint[] Find(Signature signature, int maximumMatches = 64)
    {
        var key = (signature.Id, maximumMatches);
        if (_locations.TryGetValue(key, out var cached)) return cached;
        if (!signature.CanDiscover) return _locations[key] = [];
        var found = new List<uint>();
        var hits = 0;
        foreach (var section in _image.Sections.Where(s => s.Kind == signature.Section && s.Bytes is not null))
        {
            _image.CheckBudget();
            var bytes = section.Bytes!;
            for (var start = 0; start < bytes.Length;)
            {
                var index = bytes.AsSpan(start).IndexOf(signature.Anchor);
                if (index < 0) break;
                if (++hits > 131072) break; // A ubiquitous sequence is not an identity.
                _image.CheckBudget();
                index += start;
                var candidate = index - signature.AnchorOffset;
                if (candidate >= 0 && candidate <= bytes.Length - signature.Length &&
                    Match(signature, checked(section.Rva + (uint)candidate), remember: false))
                {
                    found.Add(checked(section.Rva + (uint)candidate));
                    if (found.Count > maximumMatches) break;
                }
                start = index + 1;
            }
            if (hits > 131072 || found.Count > maximumMatches) return _locations[key] = [];
        }
        return _locations[key] = found.ToArray();
    }

    private bool Match(Signature signature, uint rva, bool remember)
    {
        if (_image.Kind(rva, signature.Length) != signature.Section) return false;
        var bytes = _image.Bytes(rva, signature.Length);
        var normalized = bytes.ToArray();
        foreach (var relocation in signature.Relocations)
        {
            uint target;
            try { target = Target(signature, rva, relocation); }
            catch (Exception e) when (e is InvalidDataException or OverflowException) { return false; }
            if (_image.Kind(target) != relocation.Section ||
                relocation.InternalOffset is { } inner && target != checked(rva + inner)) return false;
            normalized.AsSpan(relocation.Offset, relocation.Width).Clear();
        }
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(normalized), signature.Hash)) return false;
        if (remember)
        {
            _image.Remember(rva, bytes);
            if (_matched.TryGetValue(signature.Id, out var previous) && previous != rva)
                throw new InvalidDataException("Adaptive signature identity is ambiguous");
            _matched[signature.Id] = rva;
            BindRelationship(signature.ReferenceRva, rva);
            foreach (var relocation in signature.Relocations)
                BindRelationship(relocation.TargetRva, Target(signature, rva, relocation));
        }
        return true;
    }

    private void BindRelationship(uint previous, uint current)
    {
        if (_relationships.TryGetValue(previous, out var existing) && existing != current)
            throw new InvalidDataException("Adaptive references disagree");
        _relationships[previous] = current;
    }

    private void ValidateRelationships()
    {
        // References to the middle of a guarded instruction range must move with
        // that range. A same-section target is not sufficient to establish this.
        foreach (var (oldTarget, newTarget) in _relationships)
            foreach (var (id, start) in _matched)
            {
                var signature = _signatures[id];
                if (oldTarget >= signature.ReferenceRva && oldTarget - signature.ReferenceRva < signature.Length &&
                    newTarget != checked(start + oldTarget - signature.ReferenceRva))
                    throw new InvalidDataException("Adaptive interior reference changed");
            }
        if (!_profile.TryGetProperty("dependencyRelationships", out var dependencies)) return;
        foreach (var dependency in dependencies.EnumerateArray())
        {
            var id = dependency.GetProperty("signature").GetString()!;
            if (!_matched.TryGetValue(id, out var start)) continue;
            var signature = _signatures[id];
            var operand = signature.Relocations.Single(r => r.Offset == dependency.GetProperty("operandOffset").GetInt32());
            var target = Target(signature, start, operand);
            foreach (var path in dependency.GetProperty("targetPaths").EnumerateArray())
                if (Active(path.GetString()!) && (!_resolved.TryGetValue(path.GetString()!, out var expected) || target != expected))
                    throw new InvalidDataException("Adaptive reader reference changed");
            if (dependency.TryGetProperty("ranges", out var ranges))
                foreach (var range in ranges.EnumerateArray())
                {
                    var targetId = range.GetProperty("signature").GetString()!;
                    if (_matched.TryGetValue(targetId, out var expected) && target != checked(expected + range.GetProperty("offset").GetUInt32()))
                        throw new InvalidDataException("Adaptive instruction reference changed");
                }
            var length = dependency.TryGetProperty("accessBytes", out var bytes) ? bytes.GetInt32() : 0;
            if (length > 0 && operand.Section != "image" && _image.Kind(target, length) != operand.Section)
                throw new InvalidDataException("Adaptive referenced span changed");
        }
    }

    private uint Target(Signature signature, uint rva, Relocation relocation)
    {
        var bytes = _image.Bytes(rva, signature.Length).Slice(relocation.Offset, relocation.Width);
        long target = relocation.Kind switch
        {
            "relative" => (long)rva + relocation.Next + (relocation.Width == 1 ? (sbyte)bytes[0] : BinaryPrimitives.ReadInt32LittleEndian(bytes)),
            "image" => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            "absolute" => checked((long)(BinaryPrimitives.ReadUInt64LittleEndian(bytes) - _image.ModuleBase)),
            _ => throw new InvalidDataException("Adaptive operand kind")
        };
        if (target < 0 || target >= _image.ImageSize) throw new InvalidDataException("Adaptive operand bounds");
        return (uint)target;
    }

    private bool TryTable(JsonElement binding, out uint result)
    {
        result = 0;
        if (binding.TryGetProperty("relatedTypeTablePath", out var parentPath))
            return TryRelatedTable(binding, parentPath.GetString()!, out result);
        if (binding.TryGetProperty("typeNameSignature", out var typeName))
        {
            var signature = _signatures[typeName.GetString()!];
            var names = Find(signature);
            if (names.Length == 1)
            {
                var descriptor = checked((uint)((long)names[0] + binding.GetProperty("typeDescriptorOffset").GetInt32()));
                var candidates = new HashSet<uint>();
                var descriptorBytes = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(descriptorBytes, descriptor);
                foreach (var reference in FindLiteral(descriptorBytes, 4))
                {
                    if (reference < 12) continue;
                    var col = reference - 12;
                    if (_image.Kind(col, 24) != "readonly" || _image.UInt32(col) != 1 || _image.UInt32(col + 20) != col) continue;
                    var pointer = new byte[8];
                    BinaryPrimitives.WriteUInt64LittleEndian(pointer, _image.ModuleBase + col);
                    foreach (var location in FindLiteral(pointer, 8))
                        if (CheckTable(binding, checked(location + 8), remember: false) &&
                            CheckTableIdentity(binding, location + 8, fromReference: false))
                            candidates.Add(location + 8);
                }
                if (candidates.Count == 1)
                {
                    result = candidates.Single();
                    return CheckTable(binding, result, remember: true);
                }
            }
        }
        var slots = binding.GetProperty("slots").EnumerateArray().ToArray();
        foreach (var anchorOffset in binding.GetProperty("anchors").EnumerateArray().Select(a => a.GetUInt32()))
        {
            var slot = slots.Single(s => s.GetProperty("offset").GetUInt32() == anchorOffset);
            var id = slot.TryGetProperty("anchorSignature", out var anchor) && anchor.ValueKind == JsonValueKind.String
                ? anchor.GetString()! : slot.GetProperty("signature").GetString()!;
            var functions = Find(_signatures[id]);
            if (functions.Length != 1) continue;
            var pointer = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(pointer, _image.ModuleBase + functions[0]);
            var candidates = new HashSet<uint>();
            foreach (var section in _image.Sections.Where(s => s.Kind == "readonly" && s.Bytes is not null))
            {
                for (var start = 0; start < section.Bytes!.Length;)
                {
                    _image.CheckBudget();
                    var index = section.Bytes.AsSpan(start).IndexOf(pointer);
                    if (index < 0) break;
                    index += start;
                    var address = section.Rva + (uint)index;
                    if (address % 8 == 0 && address >= anchorOffset && CheckTable(binding, address - anchorOffset, remember: false) &&
                        CheckTableIdentity(binding, address - anchorOffset, fromReference: false))
                        candidates.Add(address - anchorOffset);
                    start = index + 1;
                }
            }
            if (candidates.Count != 1) continue;
            result = candidates.Single();
            return CheckTable(binding, result, remember: true);
        }
        return false;
    }

    private bool TryRelatedTable(JsonElement binding, string parentPath, out uint result)
    {
        result = 0;
        if (!_resolved.TryGetValue(parentPath, out var parent) || !TryReadLocator(parent, false, out var parentCol)) return false;
        var descriptor = _image.UInt32(parentCol + 12);
        var hierarchy = _image.UInt32(parentCol + 16);
        var name = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(name, descriptor);
        var candidates = new HashSet<uint>();
        foreach (var reference in FindLiteral(name, 4))
        {
            if (reference < 12) continue;
            var col = reference - 12;
            if (_image.Kind(col, 24) != "readonly" || _image.UInt32(col) != 1 ||
                _image.UInt32(col + 20) != col || _image.UInt32(col + 16) != hierarchy) continue;
            var pointer = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(pointer, _image.ModuleBase + col);
            foreach (var location in FindLiteral(pointer, 8))
            {
                var table = checked(location + 8);
                if (table != parent && CheckTable(binding, table, remember: false) &&
                    CheckTableIdentity(binding, table, fromReference: false)) candidates.Add(table);
            }
        }
        if (candidates.Count != 1) return false;
        result = candidates.Single();
        return CheckTable(binding, result, remember: true);
    }

    private bool TryReadLocator(uint table, bool proof, out uint locator)
    {
        locator = 0;
        if (table < 8 || table % 8 != 0 || _image.Kind(table - 8, 16) != "readonly") return false;
        locator = _image.Pointer(table - 8, proof);
        if (_image.Kind(locator, 24) != "readonly" || _image.UInt32(locator, proof) != 1 ||
            _image.UInt32(locator + 20, proof) != locator) return false;
        return _image.Kind(_image.UInt32(locator + 12, proof), 16) is "readonly" or "data" &&
            _image.Kind(_image.UInt32(locator + 16, proof), 16) == "readonly";
    }

    private IEnumerable<uint> FindLiteral(byte[] value, int alignment)
    {
        var hits = 0;
        foreach (var section in _image.Sections.Where(s => s.Kind == "readonly" && s.Bytes is not null))
            for (var start = 0; start < section.Bytes!.Length;)
            {
                _image.CheckBudget();
                var index = section.Bytes.AsSpan(start).IndexOf(value);
                if (index < 0) break;
                if (++hits > 8192) throw new InvalidDataException("Adaptive table identity is ambiguous");
                index += start;
                var rva = checked(section.Rva + (uint)index);
                if (rva % alignment == 0) yield return rva;
                start = index + 1;
            }
    }

    private bool CheckTable(JsonElement binding, uint rva, bool remember)
    {
        try
        {
            if (_image.Kind(rva, 8) != "readonly" || rva % 8 != 0) return false;
            foreach (var slot in binding.GetProperty("slots").EnumerateArray())
            {
                if (slot.TryGetProperty("required", out var required) && !required.GetBoolean()) continue;
                if (slot.TryGetProperty("requiredBy", out var owners) && !owners.EnumerateArray().Any(p => Active(p.GetString()!))) continue;
                var address = checked(rva + slot.GetProperty("offset").GetUInt32());
                if (_image.Kind(address, 8) != "readonly") return false;
                var target = _image.Pointer(address, proof: remember);
                if (!Match(_signatures[slot.GetProperty("signature").GetString()!], target, remember)) return false;
            }
            foreach (var slot in binding.GetProperty("scalarSlots").EnumerateArray())
            {
                if (slot.TryGetProperty("requiredBy", out var owners) && !owners.EnumerateArray().Any(p => Active(p.GetString()!))) continue;
                var address = checked(rva + slot.GetProperty("offset").GetUInt32());
                if (BinaryPrimitives.ReadUInt64LittleEndian(_image.Bytes(address, 8, proof: remember)) != slot.GetProperty("value").GetUInt64()) return false;
            }
            if (binding.TryGetProperty("codeRegionSlots", out var codeSlots))
                foreach (var slot in codeSlots.EnumerateArray())
                    if (_image.Kind(_image.Pointer(checked(rva + slot.GetUInt32()), proof: remember)) != "code") return false;
            if (binding.TryGetProperty("topology", out var topology) && topology.ValueKind != JsonValueKind.Null &&
                !CheckTopology(rva, topology, remember)) return false;
            return !remember || CheckTableIdentity(binding, rva, fromReference: _resolved.ContainsKey(binding.GetProperty("path").GetString()!), remember: true);
        }
        catch (Exception e) when (e is InvalidDataException or OverflowException)
        {
            if (remember) throw;
            return false;
        }
    }

    private bool CheckTableIdentity(JsonElement binding, uint table, bool fromReference, bool remember = false)
    {
        if (binding.TryGetProperty("relatedTypeTablePath", out var parentPath))
        {
            // Multiple-inheritance tables name the same complete-object type
            // and hierarchy. Their spacing and numeric RTTI labels may move.
            // Ownership plus the full topology and slot guards establish identity.
            return _resolved.TryGetValue(parentPath.GetString()!, out var parent) && parent != table &&
                binding.TryGetProperty("topology", out var topology) && topology.ValueKind == JsonValueKind.Object &&
                binding.GetProperty("slots").EnumerateArray().Any(s => s.GetProperty("required").GetBoolean() &&
                    (!s.TryGetProperty("requiredBy", out var owners) || owners.EnumerateArray().Any(p => Active(p.GetString()!)))) &&
                TryReadLocator(parent, remember, out var parentCol) && TryReadLocator(table, remember, out var col) &&
                _image.UInt32(parentCol + 12, remember) == _image.UInt32(col + 12, remember) &&
                _image.UInt32(parentCol + 16, remember) == _image.UInt32(col + 16, remember);
        }
        if (!binding.TryGetProperty("minimumAnchors", out var minimum)) return true;
        var required = minimum.GetInt32();
        var named = false;
        if (binding.TryGetProperty("typeNameSignature", out var typeName))
        {
            var signature = _signatures[typeName.GetString()!];
            var names = Find(signature);
            if (names.Length != 1) return false;
            var col = _image.Pointer(checked(table - 8), proof: remember);
            var descriptor = _image.UInt32(checked(col + 12), remember);
            named = (long)names[0] + binding.GetProperty("typeDescriptorOffset").GetInt32() == descriptor &&
                Match(signature, names[0], remember);
            if (!named) return false;
        }
        if (required is < 0 or > 4 || required <= 1 && !fromReference && !named) return false;
        var matched = 0;
        var slots = binding.GetProperty("slots").EnumerateArray().ToArray();
        foreach (var offset in binding.GetProperty("anchors").EnumerateArray().Select(x => x.GetUInt32()))
        {
            var slot = slots.Single(s => s.GetProperty("offset").GetUInt32() == offset);
            var id = slot.TryGetProperty("anchorSignature", out var anchor) && anchor.ValueKind == JsonValueKind.String
                ? anchor.GetString()! : slot.GetProperty("signature").GetString()!;
            var signature = _signatures[id];
            var locations = Find(signature);
            if (locations.Length != 1) continue;
            if (_image.Pointer(checked(table + offset), proof: remember) != locations[0] || !Match(signature, locations[0], remember)) return false;
            matched++;
        }
        return matched >= required;
    }

    private bool CheckTopology(uint table, JsonElement expected, bool proof)
    {
        var col = _image.Pointer(checked(table - 8), proof);
        if (_image.Kind(col, 24) != "readonly" || _image.UInt32(col, proof) != 1 ||
            _image.UInt32(col + 4, proof) != expected.GetProperty("offset").GetUInt32() ||
            _image.UInt32(col + 8, proof) != expected.GetProperty("constructionDisplacement").GetUInt32() ||
            _image.UInt32(col + 20, proof) != col) return false;
        var hierarchy = _image.UInt32(col + 16, proof);
        if (_image.Kind(hierarchy, 16) != "readonly") return false;
        var flags = expected.GetProperty("hierarchyFlags").EnumerateArray().Select(x => x.GetUInt32()).ToArray();
        if (flags.Length != 3 || flags[2] is < 1 or > 128) return false;
        for (var i = 0; i < 3; i++) if (_image.UInt32(hierarchy + (uint)(i * 4), proof) != flags[i]) return false;
        var array = _image.UInt32(hierarchy + 12, proof);
        if (_image.Kind(array, checked((int)flags[2] * 4)) != "readonly") return false;
        var bases = expected.GetProperty("basePmdAndAttributes").EnumerateArray().ToArray();
        if (bases.Length != flags[2]) return false;
        var names = expected.TryGetProperty("baseTypeNames", out var nameList) ? nameList.EnumerateArray().ToArray() : null;
        if (names is not null && names.Length != bases.Length) return false;
        for (var i = 0; i < bases.Length; i++)
        {
            var descriptor = _image.UInt32(array + (uint)(i * 4), proof);
            if (_image.Kind(descriptor, 28) != "readonly") return false;
            var values = bases[i].EnumerateArray().Select(x => x.GetUInt32()).ToArray();
            if (values.Length != 5) return false;
            for (var j = 0; j < 5; j++) if (_image.UInt32(descriptor + 4 + (uint)(j * 4), proof) != values[j]) return false;
            if (names is not null && !CheckTypeName(_image.UInt32(descriptor, proof), names[i], proof)) return false;
        }
        return true;
    }

    private bool CheckTypeName(uint descriptor, JsonElement expected, bool proof)
    {
        var start = checked(descriptor + 16);
        if (expected.GetProperty("kind").GetString() == "named")
        {
            var name = Convert.FromHexString(expected.GetProperty("hex").GetString()!);
            return name.Length is > 1 and <= 512 && name[^1] == 0 && _image.Bytes(start, name.Length, proof).SequenceEqual(name);
        }
        // Numeric RTTI labels change between builds. Their padding is only an
        // extra discriminator after method identity and full inheritance checks.
        var bytes = _image.Bytes(start, 512);
        var length = bytes.IndexOf((byte)0);
        if (length is < 1 or >= 512) return false;
        var value = bytes[..length];
        var trimmed = value.Trim((byte)' ');
        if (trimmed.IsEmpty) return false;
        foreach (var digit in trimmed) if (digit is < (byte)'0' or > (byte)'9') return false;
        if (length != expected.GetProperty("width").GetInt32() &&
            (trimmed.Length != length || expected.GetProperty("padded").GetBoolean())) return false;
        if (proof) _image.Remember(start, bytes[..(length + 1)]);
        return true;
    }

    private void ValidateFields()
    {
        if (_capabilities is not null && !_capabilities.Contains("gauge") || !_profile.TryGetProperty("fieldWitnesses", out var fields)) return;
        var capability = _profile.GetProperty("capabilities").GetProperty("gauge");
        if (!capability.GetProperty("fieldWitnessComplete").GetBoolean())
        {
            if (_gaugeFieldProfile is { } alternate && _referenceGauge is { } contract)
            {
                NativeAdaptiveFieldVerifier.Validate(_image, alternate, contract, _resolved["/nativeGauge/childVtableRva"]);
                return;
            }
            throw new InvalidDataException("Adaptive native gauge fields could not be verified");
        }
        var required = capability.GetProperty("requiredFieldWitnessPaths").EnumerateArray().Select(p => p.GetString()!).ToHashSet(StringComparer.Ordinal);
        foreach (var field in fields.EnumerateArray())
        {
            var path = field.GetProperty("path").GetString()!;
            if (!required.Remove(path)) throw new InvalidDataException("Adaptive field witness is invalid");
            var initializer = _signatures[field.GetProperty("initializerSignature").GetString()!];
            var getter = _signatures[field.GetProperty("getterSignature").GetString()!];
            var name = Convert.FromHexString(field.GetProperty("nameHex").GetString()!);
            if (name.Length is < 2 or > 512 || name[^1] != 0) throw new InvalidDataException("Adaptive property name bounds");
            var getterOperand = initializer.Relocations.Single(r => r.Offset == field.GetProperty("getterOperandOffset").GetInt32());
            var nameOperand = initializer.Relocations.Single(r => r.Offset == field.GetProperty("nameOperandOffset").GetInt32());
            var typeOperand = initializer.Relocations.Single(r => r.Offset == field.GetProperty("typeGetterOperandOffset").GetInt32());
            var typeSlot = checked(_resolved["/nativeGauge/childVtableRva"] + field.GetProperty("childTypeGetterSlotOffset").GetUInt32());
            var childType = _image.Pointer(typeSlot);
            var accepted = new List<uint>();
            foreach (var start in Find(initializer, 8192))
            {
                var nameRva = Target(initializer, start, nameOperand);
                if (_image.Kind(nameRva, name.Length) != "readonly" || !_image.Bytes(nameRva, name.Length).SequenceEqual(name) ||
                    Target(initializer, start, typeOperand) != childType ||
                    !Match(getter, Target(initializer, start, getterOperand), remember: false)) continue;
                accepted.Add(start);
            }
            if (accepted.Count != 1) throw new InvalidDataException("Adaptive native gauge field changed: " + path);
            var selected = accepted[0];
            Match(initializer, selected, remember: true);
            Match(getter, Target(initializer, selected, getterOperand), remember: true);
            _image.Remember(Target(initializer, selected, nameOperand), name);
            _image.Pointer(typeSlot, proof: true);
        }
        if (required.Count != 0) throw new InvalidDataException("Adaptive field witness is missing");
    }

    private static void Set(JsonObject root, string path, JsonNode? value)
    {
        var parts = path.Split('/');
        JsonNode node = root;
        for (var i = 1; i < parts.Length - 1; i++)
            node = node is JsonArray a ? a[int.Parse(parts[i], System.Globalization.CultureInfo.InvariantCulture)]! : node[parts[i]]!;
        if (node is JsonArray array) array[int.Parse(parts[^1], System.Globalization.CultureInfo.InvariantCulture)] = value;
        else node[parts[^1]] = value;
    }

    private sealed class Signature
    {
        internal Signature(JsonElement value)
        {
            Id = value.GetProperty("id").GetString()!;
            ReferenceRva = value.GetProperty("referenceRva").GetUInt32();
            Length = value.GetProperty("length").GetInt32();
            Hash = Convert.FromHexString(value.GetProperty("normalizedSha256").GetString()!);
            Anchor = Convert.FromHexString(value.GetProperty("anchorHex").GetString()!);
            AnchorOffset = value.GetProperty("anchorOffset").GetInt32();
            Section = value.GetProperty("section").GetString()!;
            CanDiscover = !value.TryGetProperty("canDiscover", out var discover) || discover.GetBoolean();
            Relocations = value.GetProperty("relocations").EnumerateArray().Select(r => new Relocation(
                r.GetProperty("offset").GetInt32(), r.GetProperty("width").GetInt32(), r.GetProperty("next").GetInt32(),
                r.GetProperty("kind").GetString()!, r.GetProperty("targetRva").GetUInt32(), r.GetProperty("expectedTargetSection").GetString()!,
                r.TryGetProperty("internalOffset", out var inner) && inner.ValueKind == JsonValueKind.Number ? inner.GetUInt32() : null)).ToArray();
            if (Length is < 1 or > 8192 || Hash.Length != 32 || AnchorOffset < 0 || AnchorOffset > Length - Anchor.Length ||
                CanDiscover && Anchor.Length < 4 || Relocations.Any(r => r.Width is not (1 or 4 or 8) || r.Offset < 0 ||
                    r.Offset > Length - r.Width || r.Kind == "relative" && (r.Next < r.Offset + r.Width || r.Next > Length) ||
                    r.Kind == "absolute" && r.Width != 8 || r.Kind == "image" && r.Width != 4 || r.Kind == "relative" && r.Width == 8))
                throw new InvalidDataException("Adaptive signature bounds");
            var occupied = new bool[Length];
            foreach (var relocation in Relocations)
                for (var i = relocation.Offset; i < relocation.Offset + relocation.Width; i++)
                {
                    if (occupied[i] || i >= AnchorOffset && i < AnchorOffset + Anchor.Length)
                        throw new InvalidDataException("Adaptive signature overlap");
                    occupied[i] = true;
                }
        }
        internal string Id { get; }
        internal uint ReferenceRva { get; }
        internal int Length { get; }
        internal byte[] Hash { get; }
        internal byte[] Anchor { get; }
        internal int AnchorOffset { get; }
        internal string Section { get; }
        internal bool CanDiscover { get; }
        internal Relocation[] Relocations { get; }
    }
    private sealed record Relocation(int Offset, int Width, int Next, string Kind, uint TargetRva, string Section, uint? InternalOffset);
}
