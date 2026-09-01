from contextlib import redirect_stdout
import copy
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import struct
import sys
import tempfile
import unittest
from unittest import mock


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import compatibility_audit as audit


OPTIONAL = 0x98
SECTION_TABLE = 0x188
BUNDLED_PACK_PATH = Path(__file__).resolve().parents[2] / "src/Wisp.App/NativeCompatibility/fh6-6.430.771.0.json"
FIELDS = {
    "sourceProvider": 0x7740, "sourceCarOrdinal": 0x740C, "providerRpm": 0x1B0,
    "providerSimRedlineAngularVelocity": 0x248,
    "providerTachometerMaximumAngularVelocity": 0x24C,
    "localPlayerFlag": 0x1464, "localPlayerProviderFlag": 0xC330,
    "stmState": 0x1430, "absState": 0x1434, "stmAvailable": 0x17B4,
    "tcrAvailable": 0x17B5, "absAvailable": 0x17B6, "lcAvailable": 0x17B7,
    "lcPrimary": 0x14EC, "lcMode": 0x1F7C, "lcSecondary": 0xC220,
    "tcrSecondary": 0xC220, "tcrPrimary": 0xC224, "tcrTertiary": 0xC228,
    "tcrWheelValues": 0xC2C8, "firstWheelPointer": 0xBA0,
    "secondWheelPointer": 0xBA8, "thirdWheelPointer": 0xBB0, "wheelId": 0x5A0,
}


def getter(field):
    return b"\xF3\x0F\x10\x81" + struct.pack("<I", field) + b"\xC3"


SIGNATURES = {0x680: getter(0x248), 0x670: getter(0x24C)}


def fixture(image_base=0x140000000):
    data = bytearray(0x2800)
    data[:2] = b"MZ"
    struct.pack_into("<I", data, 0x3C, 0x80)
    data[0x80:0x84] = b"PE\0\0"
    struct.pack_into("<HHIIIHH", data, 0x84, 0x8664, 3, 0, 0, 0, 0xF0, 0x22)
    struct.pack_into("<H", data, OPTIONAL, 0x20B)
    struct.pack_into("<Q", data, OPTIONAL + 24, image_base)
    struct.pack_into("<II", data, OPTIONAL + 32, 0x1000, 0x200)
    struct.pack_into("<II", data, OPTIONAL + 56, 0x9000, 0x200)
    struct.pack_into("<I", data, OPTIONAL + 108, 16)
    sections = [
        (b".data", 0x400, 0x4000, 0x200, 0x600, audit.READ | audit.WRITE),
        (b".text", 0x400, 0x2000, 0x400, 0x200, audit.READ | audit.EXECUTE),
        (b".rdata", 0x2000, 0x6000, 0x2000, 0x800, audit.READ),
    ]
    for index, (name, virtual_size, rva, raw_size, file_offset, flags) in enumerate(sections):
        entry = SECTION_TABLE + index * 40
        data[entry:entry + len(name)] = name
        struct.pack_into("<IIII", data, entry + 8, virtual_size, rva, raw_size, file_offset)
        struct.pack_into("<I", data, entry + 36, flags)
    struct.pack_into("<I", data, 0x900, 0x3DCCCCCD)
    slots = []
    for index, offset in enumerate(audit.SLOTS):
        rva = 0x2050 + index * 0x20
        slots.append({"offset": offset, "targetRva": rva})
        struct.pack_into("<Q", data, 0xA00 + offset, image_base + rva)
        data[0x200 + rva - 0x2000] = 0xC3
        if offset in SIGNATURES:
            code_offset = 0x200 + rva - 0x2000
            data[code_offset:code_offset + 9] = SIGNATURES[offset]
        if offset == 0x210:
            # This slot is scaled redline, not the +0x24C maximum getter.
            code_offset = 0x200 + rva - 0x2000
            scaled_redline = getter(0x248)[:-1] + b"\xF3\x0F\x59\x05"
            scaled_redline += struct.pack("<i", 0x6100 - rva - 16) + b"\xC3"
            data[code_offset:code_offset + len(scaled_redline)] = scaled_redline
    struct.pack_into("<Q", data, 0xA00 + 0x670, image_base + 0x2250)
    data[0x450:0x459] = SIGNATURES[0x670]
    # A three-level RT_VERSION tree in .rdata, separate from the vtable.
    resource = 0x2000
    struct.pack_into("<II", data, OPTIONAL + 112 + 2 * 8, 0x7800, 0x100)
    for offset in (0, 0x20, 0x40):
        struct.pack_into("<HH", data, resource + offset + 12, 0, 1)
    struct.pack_into("<II", data, resource + 0x10, 16, 0x80000020)
    struct.pack_into("<II", data, resource + 0x30, 1, 0x80000040)
    struct.pack_into("<II", data, resource + 0x50, 0x409, 0x60)
    key = "VS_VERSION_INFO\0".encode("utf-16le")
    value_offset = (6 + len(key) + 3) & ~3
    blob = bytearray(value_offset + 52)
    struct.pack_into("<HHH", blob, 0, len(blob), 52, 0)
    blob[6:6 + len(key)] = key
    struct.pack_into("<13I", blob, value_offset, 0xFEEF04BD, 0x10000,
                     (6 << 16) | 430, 771 << 16, 0, 0, 0, 0, 0, 0, 0, 0, 0)
    struct.pack_into("<IIII", data, resource + 0x60, 0x7880, len(blob), 0, 0)
    data[resource + 0x80:resource + 0x80 + len(blob)] = blob
    pack = {
        "schemaVersion": 1, "readerVersion": 1, "id": "synthetic", "revision": 1,
        "gameVersion": "6.430.771.0", "executableLength": len(data),
        "executableSha256": hashlib.sha256(data).hexdigest().upper(),
        "imageSize": 0x9000, "sourceVectorRva": 0x4300, "thresholdRva": 0x6100,
        "leadVtableRva": 0x6200, "expectedThresholdBits": 0x3DCCCCCD,
        "fields": dict(FIELDS), "requiredVtableSlots": slots,
    }
    return bytes(data), pack


def rehash(pack, data):
    result = copy.deepcopy(pack)
    result["executableLength"] = len(data)
    result["executableSha256"] = hashlib.sha256(data).hexdigest().upper()
    return result


def check(report, name):
    return next(item for item in report["checks"] if item["name"] == name)


class PeTests(unittest.TestCase):
    def setUp(self):
        self.data, self.pack = fixture()
        self.image = audit.PEImage(self.data)

    def test_valid_fingerprint_and_file_version(self):
        fingerprint = self.image.fingerprint()
        for key in ("gameVersion", "executableLength", "executableSha256", "imageSize"):
            self.assertEqual(self.pack[key], fingerprint[key])
        self.assertEqual(0x140000000, fingerprint["preferredImageBase"])

    def test_file_offsets_and_rvas_are_distinct(self):
        span = self.image.span(0x6100, 4)
        self.assertEqual(0x6100, span["imageRva"])
        self.assertEqual(0x900, span["fileOffset"])
        self.assertEqual(struct.pack("<I", 0x3DCCCCCD), self.image.read_rva(0x6100, 4))
        with self.assertRaises(audit.AuditError):
            self.image.read_rva(0x900, 4)

    def test_code_section_need_not_be_first(self):
        self.assertEqual(".data", self.image.sections[0].name)
        self.assertEqual(".text", self.image.executable_span(0x2050)["section"])
        with self.assertRaises(audit.AuditError):
            self.image.executable_span(0x4000)

    def test_zero_fill_is_mapped_but_not_file_readable(self):
        location = self.image.span(0x4300, 24)
        self.assertFalse(location["fileBacked"])
        self.assertIsNone(location["fileOffset"])
        with self.assertRaises(audit.AuditError):
            self.image.read_rva(0x4300, 24)

    def test_span_crossing_raw_end_is_not_complete_file_backing(self):
        location = self.image.span(0x41F8, 24)
        self.assertEqual(8, location["fileBytesAvailable"])
        self.assertFalse(location["fileBacked"])
        with self.assertRaises(audit.AuditError):
            self.image.read_rva(0x41F8, 24)

    def test_section_end_image_end_and_negative_ranges_fail(self):
        for rva, size in ((0x43F8, 24), (0x9000, 1), (0x8FFF, 2), (-1, 1), (0x2000, 0)):
            with self.subTest(rva=rva, size=size), self.assertRaises(audit.AuditError):
                self.image.span(rva, size)

    def test_truncated_header_and_raw_section_fail(self):
        for length in (0, 1, 0x3F, 0x100, 0x1FF, len(self.data) - 1):
            with self.subTest(length=length), self.assertRaises(audit.AuditError):
                audit.PEImage(self.data[:length])

    def test_corrupt_pe_offset_and_architecture_fail(self):
        for offset, fmt, value in ((0x3C, "<I", 0xFFFFFF00), (0x84, "<H", 0x14C),
                                   (OPTIONAL, "<H", 0x10B)):
            data = bytearray(self.data)
            struct.pack_into(fmt, data, offset, value)
            with self.subTest(offset=offset), self.assertRaises(audit.AuditError):
                audit.PEImage(bytes(data))

    def test_raw_and_virtual_overlaps_fail(self):
        for offset, value in ((SECTION_TABLE + 20, 0x200), (SECTION_TABLE + 12, 0x2000)):
            data = bytearray(self.data)
            struct.pack_into("<I", data, offset, value)
            with self.subTest(offset=offset), self.assertRaises(audit.AuditError):
                audit.PEImage(bytes(data))

    def test_missing_version_is_not_inferred_from_pack(self):
        data = bytearray(self.data)
        struct.pack_into("<II", data, OPTIONAL + 112 + 2 * 8, 0, 0)
        image = audit.PEImage(bytes(data))
        self.assertIsNone(image.file_version())
        self.assertFalse(audit.verify_image(image, rehash(self.pack, data))["fingerprintMatched"])

    def test_version_resource_cycle_and_bad_span_fail(self):
        for offset, value in ((0x2034, 0x80000020), (0x2060, 0x8FFF)):
            data = bytearray(self.data)
            struct.pack_into("<I", data, offset, value)
            with self.subTest(offset=offset), self.assertRaises(audit.AuditError):
                audit.PEImage(bytes(data)).file_version()

    def test_version_resource_traversal_is_bounded(self):
        with mock.patch.object(audit, "MAX_RESOURCE_NODES", 1):
            with self.assertRaises(audit.AuditError) as caught:
                self.image.file_version()
        self.assertIn("budget", caught.exception.message)

    def test_preferred_base_may_change_without_rva_changes(self):
        data, pack = fixture(0x180000000)
        report = audit.verify_image(audit.PEImage(data), pack)
        self.assertTrue(report["offlineChecksPassed"])
        slot = check(report, "vtableSlot0210")
        self.assertEqual(0x180002050, slot["actualPreferredVa"])
        self.assertEqual(0x2050, slot["actualTargetRva"])


class PackTests(unittest.TestCase):
    def setUp(self):
        self.data, self.pack = fixture()

    def test_exact_schema_round_trip(self):
        self.assertEqual(self.pack, audit.parse_pack(json.dumps(self.pack).encode()))

    def test_embedded_pack_uses_the_same_offline_schema(self):
        pack = audit.parse_pack(BUNDLED_PACK_PATH.read_bytes())
        self.assertEqual((3, 3), (pack["schemaVersion"], pack["readerVersion"]))
        self.assertEqual(5, pack["revision"])
        self.assertEqual("6.430.771.0", pack["gameVersion"])
        self.assertEqual(188293120, pack["imageSize"])
        self.assertEqual(FIELDS, pack["fields"])
        self.assertEqual(audit.NATIVE_GAUGE_KEYS, set(pack["nativeGauge"]))

    def test_duplicate_json_keys_rejected(self):
        with self.assertRaises(audit.AuditError):
            audit.parse_pack(b'{"schemaVersion":1,"schemaVersion":1}')

    def test_wrong_field_name_extra_key_and_bool_integer_rejected(self):
        variants = []
        pack = copy.deepcopy(self.pack)
        pack["fields"]["sourceProviderOffset"] = pack["fields"].pop("sourceProvider")
        variants.append(pack)
        pack = copy.deepcopy(self.pack)
        pack["approved"] = True
        variants.append(pack)
        pack = copy.deepcopy(self.pack)
        pack["revision"] = True
        variants.append(pack)
        for pack in variants:
            with self.subTest(keys=list(pack)), self.assertRaises(audit.AuditError):
                audit.parse_pack(json.dumps(pack).encode())

    def test_duplicate_slot_unknown_slot_and_out_of_bounds_target_rejected(self):
        for key, value in (("offset", 0x210), ("offset", 0x218), ("targetRva", 0x9000)):
            pack = copy.deepcopy(self.pack)
            pack["requiredVtableSlots"][1][key] = value
            with self.subTest(key=key, value=value), self.assertRaises(audit.AuditError):
                audit.parse_pack(json.dumps(pack).encode())

    def test_misaligned_out_of_bounds_and_signed_rvas_rejected(self):
        for key, value in (("sourceVectorRva", 0x8FF8), ("leadVtableRva", 0x6201),
                           ("thresholdRva", -4), ("imageSize", 1 << 32)):
            pack = copy.deepcopy(self.pack)
            pack[key] = value
            with self.subTest(key=key), self.assertRaises(audit.AuditError):
                audit.parse_pack(json.dumps(pack).encode())

    def test_runtime_id_rules_match_reserved_names_lengths_and_dots(self):
        invalid = ("CON", "con.any", "PRN", "aux.txt", "NUL", "COM1", "lpt9.json",
                   "a..b", "trailing.", "_leading", "a" * 81, "has space")
        for identifier in invalid:
            pack = copy.deepcopy(self.pack)
            pack["id"] = identifier
            with self.subTest(identifier=identifier), self.assertRaises(audit.AuditError):
                audit.parse_pack(json.dumps(pack).encode())
        for identifier in ("a" * 80, "COM0", "LPT10", "fh6-6.430.771.0", "A_b-c.d"):
            pack = copy.deepcopy(self.pack)
            pack["id"] = identifier
            with self.subTest(identifier=identifier):
                self.assertEqual(identifier, audit.parse_pack(json.dumps(pack).encode())["id"])

    def test_runtime_executable_and_image_caps_match(self):
        for key, value in (("executableLength", 4095), ("executableLength", 2**31 + 1),
                           ("imageSize", 4095), ("imageSize", 2**30 + 1)):
            pack = copy.deepcopy(self.pack)
            pack[key] = value
            with self.subTest(key=key, value=value), self.assertRaises(audit.AuditError):
                audit.parse_pack(json.dumps(pack).encode())
        pack = copy.deepcopy(self.pack)
        pack["executableLength"] = 2**31
        pack["imageSize"] = 2**30
        self.assertEqual(pack, audit.parse_pack(json.dumps(pack).encode()))

    def test_every_field_limit_includes_its_complete_width(self):
        for key, (_, width, alignment) in audit.FIELD_DEFINITIONS.items():
            pack = copy.deepcopy(self.pack)
            pack["fields"][key] = 65536 - width
            with self.subTest(key=key, boundary="last-valid"):
                self.assertEqual(pack, audit.parse_pack(json.dumps(pack).encode()))
            pack["fields"][key] += alignment
            with self.subTest(key=key, boundary="too-wide"), self.assertRaises(audit.AuditError):
                audit.parse_pack(json.dumps(pack).encode())

    def test_same_owner_overlaps_and_provider_header_are_rejected(self):
        for key, value in (("sourceCarOrdinal", 0x7744), ("sourceProvider", 0x7408),
                           ("providerRpm", 0x248), ("localPlayerFlag", 0xBA4),
                           ("tcrWheelValues", 0xC218), ("lcSecondary", 0xC224),
                           ("providerRpm", 0), ("localPlayerFlag", 7)):
            pack = copy.deepcopy(self.pack)
            pack["fields"][key] = value
            with self.subTest(key=key, value=value), self.assertRaises(audit.AuditError):
                audit.parse_pack(json.dumps(pack).encode())

    def test_cross_owner_reuse_and_exact_secondary_alias_are_allowed(self):
        pack = copy.deepcopy(self.pack)
        pack["fields"].update(sourceProvider=0xBA0, sourceCarOrdinal=0x1B0, wheelId=0x1B0,
                              lcSecondary=0xC300, tcrSecondary=0xC300)
        self.assertEqual(pack, audit.parse_pack(json.dumps(pack).encode()))

    def test_bom_negative_zero_and_nonfinite_json_are_rejected(self):
        data = json.dumps(self.pack).encode()
        variants = (b"\xEF\xBB\xBF" + data,
                    data.replace(b'"sourceProvider": 30528', b'"sourceProvider": -0'),
                    data.replace(b'"providerRpm": 432', b'"providerRpm": NaN'))
        for value in variants:
            with self.subTest(prefix=value[:20]), self.assertRaises(audit.AuditError):
                audit.parse_pack(value)


class GameplayVisibilityPackTests(unittest.TestCase):
    def setUp(self):
        self.pack = json.loads(BUNDLED_PACK_PATH.read_bytes())

    def parse(self, pack):
        return audit.parse_pack(json.dumps(pack).encode())

    def legacy_pack(self):
        pack = copy.deepcopy(self.pack)
        pack["schemaVersion"] = pack["readerVersion"] = 1
        del pack["gameplayVisibility"]
        del pack["nativeGauge"]
        return pack

    def version_two_pack(self):
        pack = copy.deepcopy(self.pack)
        pack["schemaVersion"] = pack["readerVersion"] = 2
        del pack["nativeGauge"]
        return pack

    def test_schema_three_preserves_the_actual_bundled_descriptor(self):
        self.assertEqual(self.pack, self.parse(self.pack))
        self.assertEqual(12, len(self.pack["gameplayVisibility"]))
        self.assertEqual((3, 3), (self.pack["schemaVersion"], self.pack["readerVersion"]))

    def test_schema_two_remains_supported_without_native_gauge(self):
        pack = self.version_two_pack()
        self.assertEqual(pack, self.parse(pack))
        self.assertNotIn("nativeGauge", self.parse(pack))

    def test_legacy_support_does_not_claim_a_visibility_capability(self):
        legacy = self.legacy_pack()
        self.assertEqual(legacy, self.parse(legacy))
        self.assertNotIn("gameplayVisibility", self.parse(legacy))
        for descriptor in (None, self.pack["gameplayVisibility"]):
            pack = copy.deepcopy(legacy)
            pack["gameplayVisibility"] = descriptor
            with self.subTest(descriptor=type(descriptor).__name__), self.assertRaises(audit.AuditError):
                self.parse(pack)

    def test_only_matching_supported_integer_version_pairs_are_accepted(self):
        for schema, reader in ((0, 0), (4, 4), (1, 2), (2, 1), (3, 2), (True, 1),
                               (3, True), (3.0, 3), (3, 3.0), ("3", 3), (3, None)):
            pack = self.legacy_pack() if schema == 1 else copy.deepcopy(self.pack)
            pack["schemaVersion"], pack["readerVersion"] = schema, reader
            with self.subTest(schema=schema, reader=reader), self.assertRaises(audit.AuditError):
                self.parse(pack)

    def test_root_properties_are_exact_in_every_schema(self):
        for original in (self.legacy_pack(), self.version_two_pack(), self.pack):
            for key in original:
                pack = copy.deepcopy(original)
                del pack[key]
                with self.subTest(schema=original["schemaVersion"], missing=key), self.assertRaises(audit.AuditError):
                    self.parse(pack)
            for key in ("extra", "SchemaVersion", "carOrdinals"):
                pack = copy.deepcopy(original)
                pack[key] = []
                with self.subTest(schema=original["schemaVersion"], extra=key), self.assertRaises(audit.AuditError):
                    self.parse(pack)

    def test_visibility_object_and_every_exact_property_are_required(self):
        for value in (None, [], True, 1, "layout"):
            pack = copy.deepcopy(self.pack)
            pack["gameplayVisibility"] = value
            with self.subTest(value=value), self.assertRaises(audit.AuditError):
                self.parse(pack)
        for key in self.pack["gameplayVisibility"]:
            pack = copy.deepcopy(self.pack)
            del pack["gameplayVisibility"][key]
            with self.subTest(missing=key), self.assertRaises(audit.AuditError):
                self.parse(pack)
        for key in ("settledState", "visibleValue", "UiServiceRva", "extra"):
            pack = copy.deepcopy(self.pack)
            pack["gameplayVisibility"][key] = 6
            with self.subTest(extra=key), self.assertRaises(audit.AuditError):
                self.parse(pack)

    def test_duplicates_are_rejected_at_every_object_level_in_every_schema(self):
        for pack in (self.legacy_pack(), self.version_two_pack(), self.pack):
            raw = json.dumps(pack, separators=(",", ":"))
            duplicates = [
                ("{", "schemaVersion", str(pack["schemaVersion"])),
                ("{", r"\u0069d", '"duplicate-id"'),
                ('"fields":{', "sourceProvider", "30528"),
                ('"requiredVtableSlots":[{', "offset", "528"),
            ]
            if pack["schemaVersion"] >= 2:
                duplicates.extend([
                    ("{", "gameplayVisibility", "{}"),
                    ('"gameplayVisibility":{', "uiServiceRva", "8"),
                    ('"gameplayVisibility":{', r"pageUiVisible\u004Fffset", "964"),
                ])
            if pack["schemaVersion"] == 3:
                duplicates.extend([
                    ("{", "nativeGauge", "{}"),
                    ('"nativeGauge":{', "registryGlobalRva", "8"),
                    ('"nativeGauge":{', r"childSpeedDigitOne\u004Fffset", "252"),
                    ('"requiredProviderVtableSlots":[{', "offset", "664"),
                ])
            for marker, key, value in duplicates:
                duplicate = raw.replace(marker, marker + f'"{key}":{value},', 1)
                with self.subTest(schema=pack["schemaVersion"], duplicate=key):
                    self.assertNotEqual(raw, duplicate)
                    with self.assertRaises(audit.AuditError) as caught:
                        audit.parse_pack(duplicate.encode())
                    self.assertEqual("pack_duplicate_key", caught.exception.code)

    def test_every_visibility_value_requires_an_unsigned_integer(self):
        for key in self.pack["gameplayVisibility"]:
            for value in (-1, 1 << 64, None, True, False, 8.0, 1.5, "64", [], {}):
                pack = copy.deepcopy(self.pack)
                pack["gameplayVisibility"][key] = value
                with self.subTest(key=key, value=value), self.assertRaises(audit.AuditError):
                    self.parse(pack)

    def test_visibility_rvas_need_aligned_complete_image_spans(self):
        image_size = self.pack["imageSize"]
        for key in audit.GAMEPLAY_VISIBILITY_RVA_KEYS:
            for value in (0, 1, image_size - 4, image_size, audit.UINT64_MAX):
                pack = copy.deepcopy(self.pack)
                pack["gameplayVisibility"][key] = value
                with self.subTest(key=key, value=value), self.assertRaises(audit.AuditError):
                    self.parse(pack)
            pack = copy.deepcopy(self.pack)
            pack["gameplayVisibility"][key] = image_size - 8
            with self.subTest(key=key, boundary="last-valid"):
                self.assertEqual(pack, self.parse(pack))

    def test_visibility_rvas_cannot_alias_different_object_identities(self):
        for key in audit.GAMEPLAY_VISIBILITY_RVA_KEYS:
            pack = copy.deepcopy(self.pack)
            other = "uiServiceVtableRva" if key == "uiServiceRva" else "uiServiceRva"
            pack["gameplayVisibility"][key] = pack["gameplayVisibility"][other]
            with self.subTest(key=key), self.assertRaises(audit.AuditError) as caught:
                self.parse(pack)
            self.assertEqual("pack_gameplay_rva_alias", caught.exception.code)

    def test_visibility_fields_protect_object_vtables_and_bounds(self):
        for key in audit.GAMEPLAY_VISIBILITY_FIELD_DEFINITIONS:
            for value in (0, 4, 7, audit.MAX_FIELD_BYTES, audit.UINT64_MAX):
                pack = copy.deepcopy(self.pack)
                pack["gameplayVisibility"][key] = value
                with self.subTest(key=key, value=value), self.assertRaises(audit.AuditError):
                    self.parse(pack)

    def test_visibility_field_alignment_matches_the_scalar_type(self):
        for key, value in (("serviceDependencyOffset", 0xA4), ("rootTransitionManagerOffset", 0x3C),
                           ("managerOwnerOffset", 0xC4), ("managerCurrentPageOffset", 0x94),
                           ("managerStateOffset", 0x69), ("pageTransitionManagerOffset", 0x294)):
            pack = copy.deepcopy(self.pack)
            pack["gameplayVisibility"][key] = value
            with self.subTest(key=key), self.assertRaises(audit.AuditError) as caught:
                self.parse(pack)
            self.assertEqual("pack_gameplay_field_alignment", caught.exception.code)

    def test_same_object_visibility_fields_cannot_overlap(self):
        layout = self.pack["gameplayVisibility"]
        for key, value in (
            ("managerOwnerOffset", layout["managerCurrentPageOffset"]),
            ("managerCurrentPageOffset", layout["managerOwnerOffset"]),
            ("managerStateOffset", layout["managerCurrentPageOffset"]),
            ("managerStateOffset", layout["managerCurrentPageOffset"] + 4),
            ("managerOwnerOffset", layout["managerStateOffset"]),
            ("pageUiVisibleOffset", layout["pageTransitionManagerOffset"]),
            ("pageUiVisibleOffset", layout["pageTransitionManagerOffset"] + 7),
            ("pageTransitionManagerOffset", layout["pageUiVisibleOffset"] & ~7),
        ):
            pack = copy.deepcopy(self.pack)
            pack["gameplayVisibility"][key] = value
            with self.subTest(key=key, value=value), self.assertRaises(audit.AuditError) as caught:
                self.parse(pack)
            self.assertEqual("pack_gameplay_field_overlap", caught.exception.code)

    def test_separate_objects_may_reuse_offsets_and_visibility_has_byte_alignment(self):
        pack = copy.deepcopy(self.pack)
        shared = pack["gameplayVisibility"]["managerOwnerOffset"]
        pack["gameplayVisibility"].update(serviceDependencyOffset=shared, rootTransitionManagerOffset=shared,
                                          pageTransitionManagerOffset=shared, pageUiVisibleOffset=shared + 9)
        self.assertEqual(pack, self.parse(pack))

    def test_complete_field_widths_include_the_inline_manager_base(self):
        layout = self.pack["gameplayVisibility"]
        root_offset = layout["rootTransitionManagerOffset"]
        manager_width = max(layout[key] + width for key, width in (
            ("managerOwnerOffset", 8), ("managerCurrentPageOffset", 8), ("managerStateOffset", 4)))
        boundaries = (
            ("serviceDependencyOffset", 65528, 65536),
            ("rootTransitionManagerOffset", 65536 - manager_width, 65536 - manager_width + 8),
            ("managerOwnerOffset", 65536 - root_offset - 8, 65536 - root_offset),
            ("managerCurrentPageOffset", 65536 - root_offset - 8, 65536 - root_offset),
            ("managerStateOffset", 65536 - root_offset - 4, 65536 - root_offset),
            ("pageTransitionManagerOffset", 65528, 65536),
            ("pageUiVisibleOffset", 65535, 65536),
        )
        for key, last_valid, overflow in boundaries:
            pack = copy.deepcopy(self.pack)
            pack["gameplayVisibility"][key] = last_valid
            with self.subTest(key=key, boundary="last-valid"):
                self.assertEqual(pack, self.parse(pack))
            pack["gameplayVisibility"][key] = overflow
            with self.subTest(key=key, boundary="too-wide"), self.assertRaises(audit.AuditError):
                self.parse(pack)

    def test_root_and_manager_offsets_share_one_object_budget(self):
        pack = copy.deepcopy(self.pack)
        pack["gameplayVisibility"].update(rootTransitionManagerOffset=0x8000, managerOwnerOffset=0x7FF8)
        self.assertEqual(pack, self.parse(pack))
        pack["gameplayVisibility"]["managerOwnerOffset"] = 0x8000
        with self.assertRaises(audit.AuditError) as caught:
            self.parse(pack)
        self.assertEqual("pack_gameplay_inline_bounds", caught.exception.code)

    def test_existing_identity_field_and_slot_guards_are_unchanged_in_every_schema(self):
        for original in (self.legacy_pack(), self.version_two_pack(), self.pack):
            for path, value in (
                (("revision",), True), (("gameVersion",), "6.430.771"),
                (("executableSha256",), "invalid"), (("executableLength",), 4095),
                (("expectedThresholdBits",), 0), (("sourceVectorRva",), original["imageSize"] - 16),
                (("leadVtableRva",), 1), (("fields", "providerRpm"), original["fields"]["providerSimRedlineAngularVelocity"]),
                (("fields", "localPlayerFlag"), 7), (("requiredVtableSlots", 1, "offset"), 0x210),
                (("requiredVtableSlots", 0, "targetRva"), original["imageSize"]),
            ):
                pack = copy.deepcopy(original)
                target = pack
                for part in path[:-1]:
                    target = target[part]
                target[path[-1]] = value
                with self.subTest(schema=original["schemaVersion"], path=path), self.assertRaises(audit.AuditError):
                    self.parse(pack)


class NativeGaugePackTests(unittest.TestCase):
    def setUp(self):
        self.pack = json.loads(BUNDLED_PACK_PATH.read_bytes())

    def parse(self, pack):
        return audit.parse_pack(json.dumps(pack).encode())

    def test_native_gauge_object_and_every_exact_property_are_required(self):
        for value in (None, [], True, 1, "layout"):
            pack = copy.deepcopy(self.pack)
            pack["nativeGauge"] = value
            with self.subTest(value=value), self.assertRaises(audit.AuditError):
                self.parse(pack)
        for key in self.pack["nativeGauge"]:
            pack = copy.deepcopy(self.pack)
            del pack["nativeGauge"][key]
            with self.subTest(missing=key), self.assertRaises(audit.AuditError):
                self.parse(pack)
        for key in ("extra", "RegistryGlobalRva", "childSpeedDigitFourOffset"):
            pack = copy.deepcopy(self.pack)
            pack["nativeGauge"][key] = 8
            with self.subTest(extra=key), self.assertRaises(audit.AuditError):
                self.parse(pack)

    def test_native_identity_rvas_require_unique_aligned_image_spans(self):
        image_size = self.pack["imageSize"]
        for key in audit.NATIVE_GAUGE_RVA_KEYS:
            for value in (0, 1, image_size - 4, image_size, audit.UINT64_MAX, True):
                pack = copy.deepcopy(self.pack)
                pack["nativeGauge"][key] = value
                with self.subTest(key=key, value=value), self.assertRaises(audit.AuditError):
                    self.parse(pack)
            pack = copy.deepcopy(self.pack)
            pack["nativeGauge"][key] = image_size - 8
            self.assertEqual(pack, self.parse(pack))

        layout = self.pack["nativeGauge"]
        for key in audit.NATIVE_GAUGE_RVA_KEYS:
            pack = copy.deepcopy(self.pack)
            other = "registryWrapperVtableRva" if key == "registryGlobalRva" else "registryGlobalRva"
            pack["nativeGauge"][key] = layout[other]
            with self.subTest(alias=key), self.assertRaises(audit.AuditError) as caught:
                self.parse(pack)
            self.assertEqual("pack_native_rva_alias", caught.exception.code)

    def test_native_function_rva_is_byte_aligned_but_cannot_alias_identity(self):
        pack = copy.deepcopy(self.pack)
        pack["nativeGauge"]["hudSubobjectSlotZeroTargetRva"] = pack["imageSize"] - 1
        self.assertEqual(pack, self.parse(pack))
        for value in (0, pack["imageSize"], audit.UINT64_MAX, True,
                      self.pack["nativeGauge"]["hudVtableRva"]):
            pack = copy.deepcopy(self.pack)
            pack["nativeGauge"]["hudSubobjectSlotZeroTargetRva"] = value
            with self.subTest(value=value), self.assertRaises(audit.AuditError):
                self.parse(pack)

    def test_native_fields_enforce_width_alignment_minimum_and_no_same_owner_overlap(self):
        composite_fields = {
            "registryBucketBoundaryOffset", "registryBucketNodeOffset",
            "hudSubobjectOffset", "hudTypeVectorOffset", "hudTypeTokenOffset",
            "hudTypeInstancesCapacityOffset", "hudTypeVectorCapacityOffset",
            "hudTypeInstanceObjectOffset", "hudTypeInstanceControlOffset",
        }
        for key, (owner, width, alignment, minimum) in audit.NATIVE_GAUGE_FIELD_DEFINITIONS.items():
            pack = copy.deepcopy(self.pack)
            pack["nativeGauge"][key] = audit.MAX_FIELD_BYTES - width
            if key not in composite_fields:
                self.assertEqual(pack, self.parse(pack), key)
            for value in ({-1, audit.MAX_FIELD_BYTES - width + alignment, audit.UINT64_MAX, True} |
                          ({minimum - 1} if minimum else set())):
                pack = copy.deepcopy(self.pack)
                pack["nativeGauge"][key] = value
                with self.subTest(key=key, value=value), self.assertRaises(audit.AuditError):
                    self.parse(pack)

        groups = {}
        for key, definition in audit.NATIVE_GAUGE_FIELD_DEFINITIONS.items():
            groups.setdefault(definition[0], []).append(key)
        for owner, keys in groups.items():
            if len(keys) < 2:
                continue
            first, second = keys[:2]
            pack = copy.deepcopy(self.pack)
            pack["nativeGauge"][second] = pack["nativeGauge"][first]
            with self.subTest(owner=owner), self.assertRaises(audit.AuditError) as caught:
                self.parse(pack)
            self.assertEqual("pack_native_field_overlap", caught.exception.code)

    def test_native_scalar_contracts_reject_wrong_types_ranges_and_nonfinite_bits(self):
        layout = self.pack["nativeGauge"]
        for key in ("registryKeyHash", "hudTypeVectorMaximumCount", "hudTypeInstanceCount",
                    "speedUnitMphValue", "speedUnitKphValue"):
            for value in (None, True, -1, 1 << 64, 1.0, "1", [], {}):
                pack = copy.deepcopy(self.pack)
                pack["nativeGauge"][key] = value
                with self.subTest(key=key, value=value), self.assertRaises(audit.AuditError):
                    self.parse(pack)
        for key, values in (
            ("childlessRegenPowerRatioBits", (0xBF800000, 0x40000000, 0x7FC00000, 0x7F800000)),
            ("providerPowerDenominatorScaleBits", (0, 0x80000000, 0xBF800000, 0x7FC00000)),
            ("providerRegenScaleBits", (0, 0x3F800000, 0x7FC00000, 0xFF800000)),
            ("providerRegenUpperBaseBits", (0xBF800000, 0x40000000, 0x7FC00000, 0x7F800000)),
        ):
            for value in values:
                pack = copy.deepcopy(self.pack)
                pack["nativeGauge"][key] = value
                with self.subTest(key=key, value=value), self.assertRaises(audit.AuditError):
                    self.parse(pack)
        pack = copy.deepcopy(self.pack)
        pack["nativeGauge"]["speedUnitKphValue"] = layout["speedUnitMphValue"]
        with self.assertRaises(audit.AuditError):
            self.parse(pack)

    def test_native_stride_count_prologue_and_containing_bounds_match_runtime(self):
        variants = (
            ("registryBucketStride", 8), ("registryBucketStride", 4097),
            ("hudTypeVectorEntryStride", 24), ("hudTypeVectorEntryStride", 4097),
            ("hudTypeInstanceStride", 8), ("hudTypeInstanceStride", 4097),
            ("hudTypeVectorMaximumCount", 0), ("hudTypeVectorMaximumCount", 1025),
            ("hudTypeInstanceCount", 0), ("hudTypeInstanceCount", 2),
            ("hudSubobjectSlotZeroPrologueHex", "488D4170"),
            ("hudSubobjectSlotZeroPrologueHex", "488D4170CZ"),
        )
        for key, value in variants:
            pack = copy.deepcopy(self.pack)
            pack["nativeGauge"][key] = value
            with self.subTest(key=key, value=value), self.assertRaises(audit.AuditError):
                self.parse(pack)

        width = (self.pack["nativeGauge"]["hudTypeVectorOffset"] +
                 self.pack["nativeGauge"]["hudTypeVectorCapacityOffset"] + 8)
        pack = copy.deepcopy(self.pack)
        pack["nativeGauge"]["hudSubobjectOffset"] = audit.MAX_FIELD_BYTES - width
        self.assertEqual(pack, self.parse(pack))
        pack["nativeGauge"]["hudSubobjectOffset"] += 8
        with self.assertRaises(audit.AuditError) as caught:
            self.parse(pack)
        self.assertEqual("pack_native_containing_bounds", caught.exception.code)

    def test_native_provider_slots_are_exact_unique_bounded_and_non_aliasing(self):
        for index, slot in enumerate(self.pack["nativeGauge"]["requiredProviderVtableSlots"]):
            for key, value in (("offset", audit.NATIVE_GAUGE_PROVIDER_SLOTS[(index + 1) % 6]),
                               ("offset", 0x300), ("targetRva", 0),
                               ("targetRva", self.pack["imageSize"]),
                               ("targetRva", self.pack["nativeGauge"]["childVtableRva"])):
                pack = copy.deepcopy(self.pack)
                pack["nativeGauge"]["requiredProviderVtableSlots"][index][key] = value
                with self.subTest(index=index, key=key, value=value), self.assertRaises(audit.AuditError):
                    self.parse(pack)
        for value in (None, [], self.pack["nativeGauge"]["requiredProviderVtableSlots"][:-1]):
            pack = copy.deepcopy(self.pack)
            pack["nativeGauge"]["requiredProviderVtableSlots"] = value
            with self.subTest(value=type(value).__name__), self.assertRaises(audit.AuditError):
                self.parse(pack)


class VerificationTests(unittest.TestCase):
    def setUp(self):
        self.data, self.pack = fixture()

    def test_pass_remains_review_only_with_protected_code_limitation(self):
        report = audit.verify_image(audit.PEImage(self.data), self.pack)
        self.assertTrue(report["offlineChecksPassed"])
        self.assertTrue(report["reviewOnly"])
        self.assertFalse(report["compatibilityEstablished"])
        self.assertEqual("not_granted", report["runtimeApproval"])
        self.assertIn("offline-unverifiable", " ".join(report["limitations"]))
        self.assertEqual(9, sum(item["name"].startswith("vtableSlot") for item in report["checks"]))
        self.assertFalse(check(report, "sourceVectorBounds")["location"]["fileBacked"])

    def test_unknown_fingerprint_never_becomes_approval_despite_all_anchors(self):
        pack = copy.deepcopy(self.pack)
        pack["executableSha256"] = "0" * 64
        report = audit.verify_image(audit.PEImage(self.data), pack)
        self.assertTrue(all(item["status"] == "passed" for item in report["checks"]))
        self.assertFalse(report["fingerprintMatched"])
        self.assertFalse(report["offlineChecksPassed"])
        self.assertFalse(report["compatibilityEstablished"])
        self.assertEqual("review_required", report["result"])

    def test_maximum_uses_observed_offline_slot_not_scaled_redline_slot(self):
        report = audit.verify_image(audit.PEImage(self.data), self.pack)
        self.assertTrue(report["offlineChecksPassed"])
        maximum = check(report, "readableGetter0670")
        self.assertEqual(0x2250, maximum["location"]["imageRva"])
        self.assertEqual("providerTachometerMaximumAngularVelocity", maximum["field"])
        self.assertFalse(maximum["runtimeGuard"])
        self.assertNotIn("readableGetter0210", [item["name"] for item in report["checks"]])

    def test_wrong_additional_maximum_anchor_is_reported(self):
        data = bytearray(self.data)
        struct.pack_into("<Q", data, 0xA00 + 0x670, 0x140002050)
        report = audit.verify_image(audit.PEImage(bytes(data)), rehash(self.pack, data))
        self.assertTrue(all(item["status"] == "passed" for item in report["checks"]
                            if item["name"].startswith("vtableSlot")))
        self.assertEqual("failed", check(report, "readableGetter0670")["status"])
        self.assertFalse(report["offlineChecksPassed"])

    def test_threshold_bit_change_fails_even_with_updated_fingerprint(self):
        data = bytearray(self.data)
        data[0x900] ^= 1
        report = audit.verify_image(audit.PEImage(bytes(data)), rehash(self.pack, data))
        self.assertEqual("failed", check(report, "thresholdBits")["status"])
        self.assertFalse(report["offlineChecksPassed"])

    def test_every_wrong_vtable_slot_fails(self):
        for offset in audit.SLOTS:
            data = bytearray(self.data)
            struct.pack_into("<Q", data, 0xA00 + offset, 0x140002010)
            report = audit.verify_image(audit.PEImage(bytes(data)), rehash(self.pack, data))
            with self.subTest(slot=offset):
                self.assertEqual("failed", check(report, f"vtableSlot{offset:04X}")["status"])
                self.assertFalse(report["offlineChecksPassed"])

    def test_raw_rva_in_vtable_is_not_a_preferred_va(self):
        data = bytearray(self.data)
        struct.pack_into("<Q", data, 0xA00 + 0x210, 0x2050)
        report = audit.verify_image(audit.PEImage(bytes(data)), rehash(self.pack, data))
        item = check(report, "vtableSlot0210")
        self.assertEqual("failed", item["status"])
        self.assertIsNone(item["actualTargetRva"])

    def test_function_target_in_data_is_not_code(self):
        data = bytearray(self.data)
        pack = copy.deepcopy(self.pack)
        pack["requiredVtableSlots"][0]["targetRva"] = 0x4100
        struct.pack_into("<Q", data, 0xA00 + 0x210, 0x140004100)
        report = audit.verify_image(audit.PEImage(bytes(data)), rehash(pack, data))
        self.assertEqual("target_not_code", check(report, "vtableSlot0210")["code"])

    def test_field_operand_change_is_not_normalized_away(self):
        data = bytearray(self.data)
        redline = next(slot["targetRva"] for slot in self.pack["requiredVtableSlots"] if slot["offset"] == 0x680)
        data[0x200 + redline - 0x2000 + 4] ^= 4
        report = audit.verify_image(audit.PEImage(bytes(data)), rehash(self.pack, data))
        self.assertEqual("failed", check(report, "readableGetter0680")["status"])

    def test_cross_section_vtable_span_fails(self):
        pack = copy.deepcopy(self.pack)
        pack["leadVtableRva"] = 0x7A00
        report = audit.verify_image(audit.PEImage(self.data), pack)
        self.assertEqual("failed", check(report, "leadVtableBounds")["status"])

    def test_report_is_not_a_loadable_pack(self):
        report = audit.verify_image(audit.PEImage(self.data), self.pack)
        with self.assertRaises(audit.AuditError):
            audit.parse_pack(json.dumps(report).encode())


class DiscoveryTests(unittest.TestCase):
    def setUp(self):
        self.data, self.pack = fixture()

    def test_pointer_anchored_candidate_preserves_all_slot_relationships(self):
        candidates, examined, complete = audit.pointer_anchored_candidates(audit.PEImage(self.data), SIGNATURES)
        self.assertEqual(1, len(candidates))
        self.assertTrue(complete)
        self.assertGreater(examined, 0)
        self.assertEqual(0x6200, candidates[0]["candidateLeadVtable"]["imageRva"])
        self.assertEqual(0xA00, candidates[0]["candidateLeadVtable"]["fileOffset"])
        self.assertEqual(9, len(candidates[0]["candidateSlots"]))
        self.assertTrue(candidates[0]["reviewOnly"])

    def test_getter_bytes_without_a_complete_vtable_are_not_candidates(self):
        data = bytearray(self.data)
        struct.pack_into("<Q", data, 0xA00 + 0x2B8, 0)
        candidates, _, _ = audit.pointer_anchored_candidates(audit.PEImage(bytes(data)), SIGNATURES)
        self.assertEqual([], candidates)

    def test_same_bytes_in_non_executable_data_do_not_prove_a_getter(self):
        data = bytearray(self.data)
        data[0x650:0x659] = SIGNATURES[0x680]
        struct.pack_into("<Q", data, 0xA00 + 0x680, 0x140004050)
        candidates, _, _ = audit.pointer_anchored_candidates(audit.PEImage(bytes(data)), SIGNATURES)
        self.assertEqual([], candidates)

    def test_multiple_vtables_are_all_reported(self):
        data = bytearray(self.data)
        for slot in self.pack["requiredVtableSlots"]:
            struct.pack_into("<Q", data, 0xE00 + slot["offset"], 0x140000000 + slot["targetRva"])
        struct.pack_into("<Q", data, 0xE00 + 0x670, 0x140002250)
        candidates, _, complete = audit.pointer_anchored_candidates(audit.PEImage(bytes(data)), SIGNATURES)
        self.assertTrue(complete)
        self.assertEqual([0x6200, 0x6600], [item["candidateLeadVtable"]["imageRva"] for item in candidates])

    def test_scan_limit_is_explicit(self):
        with mock.patch.object(audit, "MAX_POINTERS", 1):
            _, examined, complete = audit.pointer_anchored_candidates(audit.PEImage(self.data), SIGNATURES)
        self.assertEqual(1, examined)
        self.assertFalse(complete)

    def test_candidate_report_limit_is_explicit(self):
        with mock.patch.object(audit, "MAX_CANDIDATES", 0):
            candidates, _, complete = audit.pointer_anchored_candidates(audit.PEImage(self.data), SIGNATURES)
        self.assertEqual([], candidates)
        self.assertFalse(complete)

    def test_missing_optional_dependency_has_clear_error(self):
        with mock.patch.object(audit.importlib, "import_module", side_effect=ImportError):
            with self.assertRaises(audit.AuditError) as caught:
                audit.load_capstone()
        self.assertEqual("discovery_dependency_missing", caught.exception.code)
        self.assertIn("standard library", caught.exception.message)

    @unittest.skipUnless(importlib.util.find_spec("capstone"), "Optional Capstone is not installed.")
    def test_real_decoder_requires_the_exact_member_operand(self):
        image = audit.PEImage(self.data)
        capstone = audit.load_capstone()
        slots = {offset: audit.vtable_target(image, self.pack["leadVtableRva"], offset)
                 for offset in SIGNATURES}
        for offset, field in ((0x680, 0x248), (0x670, 0x24C)):
            self.assertEqual(SIGNATURES[offset], audit.getter_signature(image, slots[offset], field, capstone))
            with self.assertRaises(audit.AuditError):
                audit.getter_signature(image, slots[offset], field + 4, capstone)

    @unittest.skipUnless(importlib.util.find_spec("capstone"), "Optional Capstone is not installed.")
    def test_real_discovery_remains_review_only(self):
        report = audit.discover_images(audit.PEImage(self.data), self.pack,
                                       audit.PEImage(self.data + b"changed-copy"))
        self.assertEqual(1, report["reportedCandidateCount"])
        self.assertTrue(report["reviewOnly"])
        self.assertFalse(report["compatibilityEstablished"])
        self.assertEqual("not_granted", report["runtimeApproval"])

    def test_discovery_never_promotes_an_unknown_candidate(self):
        candidate = audit.PEImage(self.data + b"changed-copy")
        with mock.patch.object(audit, "load_capstone", return_value=object()):
            with mock.patch.object(audit, "getter_signature", side_effect=lambda image, rva, field, cap: getter(field)):
                report = audit.discover_images(audit.PEImage(self.data), self.pack, candidate)
        self.assertEqual(1, report["reportedCandidateCount"])
        self.assertEqual("review_required", report["result"])
        self.assertTrue(report["reviewOnly"])
        self.assertFalse(report["compatibilityEstablished"])
        self.assertEqual("not_granted", report["runtimeApproval"])


class CliTests(unittest.TestCase):
    def run_cli(self, args):
        output = io.StringIO()
        with redirect_stdout(output):
            code = audit.main(args)
        return code, json.loads(output.getvalue()), output.getvalue()

    def test_inspect_and_verify_do_not_change_input_or_expose_paths(self):
        data, pack = fixture()
        with tempfile.TemporaryDirectory() as directory:
            exe = Path(directory) / "private-input-name.exe"
            pack_path = Path(directory) / "private-pack-name.json"
            exe.write_bytes(data)
            pack_path.write_text(json.dumps(pack), encoding="utf-8")
            for args in (["inspect", "--exe-copy", str(exe)],
                         ["verify", "--exe-copy", str(exe), "--pack", str(pack_path)]):
                code, report, raw = self.run_cli(args)
                self.assertEqual(0, code)
                self.assertTrue(report["reviewOnly"])
                self.assertNotIn(directory, raw)
                self.assertNotIn(exe.name, raw)
                self.assertNotIn(pack_path.name, raw)
                self.assertEqual(data, exe.read_bytes())
                self.assertLess(len(raw.encode()), audit.MAX_REPORT_BYTES)

    def test_missing_input_and_invalid_arguments_are_redacted(self):
        for args in (["inspect", "--exe-copy", "private/path/missing.exe"],
                     ["inspect", "--private-user-path"]):
            code, report, raw = self.run_cli(args)
            self.assertEqual(1, code)
            self.assertIn("error", report)
            self.assertNotIn("private/path", raw)
            self.assertNotIn("private-user-path", raw)

    def test_unknown_fingerprint_cli_returns_review_exit_code(self):
        data, pack = fixture()
        pack["executableSha256"] = "0" * 64
        with tempfile.TemporaryDirectory() as directory:
            exe, pack_path = Path(directory) / "copy.exe", Path(directory) / "pack.json"
            exe.write_bytes(data)
            pack_path.write_text(json.dumps(pack), encoding="utf-8")
            code, report, _ = self.run_cli(["verify", "--exe-copy", str(exe), "--pack", str(pack_path)])
        self.assertEqual(2, code)
        self.assertFalse(report["offlineChecksPassed"])

    def test_runtime_invalid_layout_never_gets_an_offline_pass_report(self):
        data, original = fixture()
        with tempfile.TemporaryDirectory() as directory:
            exe, pack_path = Path(directory) / "copy.exe", Path(directory) / "pack.json"
            exe.write_bytes(data)
            for offset in (65536, 0x248):
                pack = copy.deepcopy(original)
                pack["fields"]["providerRpm"] = offset
                pack_path.write_text(json.dumps(pack), encoding="utf-8")
                code, report, _ = self.run_cli(["verify", "--exe-copy", str(exe), "--pack", str(pack_path)])
                with self.subTest(offset=offset):
                    self.assertEqual(1, code)
                    self.assertIn("error", report)
                    self.assertNotIn("offlineChecksPassed", report)
                    self.assertFalse(report["compatibilityEstablished"])

    def test_schema_three_is_structurally_accepted_and_invalid_visibility_is_rejected(self):
        data, _ = fixture()
        pack = json.loads(BUNDLED_PACK_PATH.read_bytes())
        with tempfile.TemporaryDirectory() as directory:
            exe, pack_path = Path(directory) / "copy.exe", Path(directory) / "pack.json"
            exe.write_bytes(data)
            pack_path.write_text(json.dumps(pack), encoding="utf-8")
            code, report, _ = self.run_cli(["verify", "--exe-copy", str(exe), "--pack", str(pack_path)])
            self.assertEqual(2, code)
            self.assertNotIn("error", report)
            self.assertFalse(report["fingerprintMatched"])
            self.assertFalse(report["compatibilityEstablished"])
            self.assertIn("live UI ownership", " ".join(report["limitations"]))

            pack["gameplayVisibility"]["pageUiVisibleOffset"] = 0
            pack_path.write_text(json.dumps(pack), encoding="utf-8")
            code, report, _ = self.run_cli(["verify", "--exe-copy", str(exe), "--pack", str(pack_path)])
            self.assertEqual(1, code)
            self.assertIn("error", report)
            self.assertNotIn("offlineChecksPassed", report)
            self.assertFalse(report["compatibilityEstablished"])


if __name__ == "__main__":
    unittest.main()
