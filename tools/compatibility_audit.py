"""Offline inspection of explicitly supplied executable copies.

inspect/verify need only Python's standard library. discover requires Capstone.
No process, executable-loader, installation-discovery, network, or approval API
is used. Reports are review-only evidence, never loadable compatibility packs.
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass
import hashlib
import importlib
import json
import math
from pathlib import Path
import re
import struct
import sys
from typing import Any


MAX_EXE_BYTES = 512 * 1024 * 1024
MAX_PACK_BYTES = 64 * 1024
MAX_REPORT_BYTES = 256 * 1024
MAX_CANDIDATES = 16
MAX_POINTERS = 8_000_000
MAX_RESOURCE_NODES = 256
MAX_EXECUTABLE_LENGTH = 2 * 1024 * 1024 * 1024
MAX_IMAGE_SIZE = 1024 * 1024 * 1024
MAX_FIELD_BYTES = 64 * 1024
UINT32_MAX = (1 << 32) - 1
UINT64_MAX = (1 << 64) - 1
READ, WRITE, EXECUTE = 0x40000000, 0x80000000, 0x20000000
SLOTS = (0x210, 0x2A8, 0x2B0, 0x2B8, 0x680, 0x1058, 0x1060, 0x1068, 0x1078)
# Both relationships were checked against the staged 6.430.771.0 executable.
# 0x670 is an additional offline anchor, not one of the nine runtime guards.
GETTER_FIELDS = {
    0x680: "providerSimRedlineAngularVelocity",
    0x670: "providerTachometerMaximumAngularVelocity",
}
FIELD_DEFINITIONS = {
    "sourceProvider": ("source", 8, 8),
    "sourceCarOrdinal": ("source", 4, 4),
    "providerRpm": ("provider", 4, 4),
    "providerSimRedlineAngularVelocity": ("provider", 4, 4),
    "providerTachometerMaximumAngularVelocity": ("provider", 4, 4),
    "localPlayerFlag": ("provider", 1, 1),
    "localPlayerProviderFlag": ("provider", 1, 1),
    "stmState": ("provider", 4, 4),
    "absState": ("provider", 4, 4),
    "stmAvailable": ("provider", 1, 1),
    "tcrAvailable": ("provider", 1, 1),
    "absAvailable": ("provider", 1, 1),
    "lcAvailable": ("provider", 1, 1),
    "lcPrimary": ("provider", 4, 4),
    "lcMode": ("provider", 4, 4),
    "lcSecondary": ("provider", 4, 4),
    "tcrSecondary": ("provider", 4, 4),
    "tcrPrimary": ("provider", 4, 4),
    "tcrTertiary": ("provider", 4, 4),
    "tcrWheelValues": ("provider", 16, 4),
    "firstWheelPointer": ("provider", 8, 8),
    "secondWheelPointer": ("provider", 8, 8),
    "thirdWheelPointer": ("provider", 8, 8),
    "wheelId": ("wheel", 4, 4),
}
FIELD_ALIGNMENT = {key: definition[2] for key, definition in FIELD_DEFINITIONS.items()}
LEGACY_PACK_KEYS = {
    "schemaVersion", "readerVersion", "id", "revision", "gameVersion",
    "executableLength", "executableSha256", "imageSize", "sourceVectorRva",
    "thresholdRva", "leadVtableRva", "expectedThresholdBits", "fields",
    "requiredVtableSlots",
}
PACK_KEYS = LEGACY_PACK_KEYS | {"gameplayVisibility"}
PACK_V3_KEYS = PACK_KEYS | {"nativeGauge"}
GAMEPLAY_VISIBILITY_RVA_KEYS = (
    "uiServiceRva", "uiServiceVtableRva", "dependencyVtableRva",
    "transitionManagerVtableRva", "hudPageVtableRva",
)
GAMEPLAY_VISIBILITY_FIELD_DEFINITIONS = {
    "serviceDependencyOffset": ("ui_service", 8, 8),
    "rootTransitionManagerOffset": ("ui_dependency", 8, 8),
    "managerOwnerOffset": ("transition_manager", 8, 8),
    "managerCurrentPageOffset": ("transition_manager", 8, 8),
    "managerStateOffset": ("transition_manager", 4, 4),
    "pageTransitionManagerOffset": ("ui_page", 8, 8),
    "pageUiVisibleOffset": ("ui_page", 1, 1),
}
GAMEPLAY_VISIBILITY_KEYS = set(GAMEPLAY_VISIBILITY_RVA_KEYS) | set(GAMEPLAY_VISIBILITY_FIELD_DEFINITIONS)
NATIVE_GAUGE_RVA_KEYS = (
    "registryGlobalRva", "registryWrapperVtableRva", "registryContextVtableRva",
    "registryContextControlVtableRva", "hudVtableRva", "hudControlVtableRva",
    "hudSubobjectVtableRva", "hudTypeTokenRva", "outerControlVtableRva",
    "outerPrimaryVtableRva", "outerSecondaryVtableRva", "childVtableRva",
)
NATIVE_GAUGE_FUNCTION_RVA_KEYS = ("hudSubobjectSlotZeroTargetRva",)
NATIVE_GAUGE_PROVIDER_SLOTS = (0x298, 0x358, 0x5B8, 0x720, 0x798, 0xE28)
NATIVE_GAUGE_FIELD_DEFINITIONS = {
    "registryContextOffset": ("registry_wrapper", 8, 8, 8),
    "registryContextControlOffset": ("registry_wrapper", 8, 8, 8),
    "registrySentinelOffset": ("registry_context", 8, 8, 8),
    "registryCountOffset": ("registry_context", 8, 8, 8),
    "registryBucketsOffset": ("registry_context", 8, 8, 8),
    "registryBucketsEndOffset": ("registry_context", 8, 8, 8),
    "registryBucketsCapacityOffset": ("registry_context", 8, 8, 8),
    "registryMaskOffset": ("registry_context", 8, 8, 8),
    "registryBucketCountOffset": ("registry_context", 8, 8, 8),
    "registryBucketBoundaryOffset": ("registry_bucket", 8, 8, 0),
    "registryBucketNodeOffset": ("registry_bucket", 8, 8, 0),
    "registryNodeNextOffset": ("registry_node", 8, 8, 0),
    "registryNodeHashOffset": ("registry_node", 8, 8, 0),
    "registryNodeObjectOffset": ("registry_node", 8, 8, 0),
    "registryNodeControlOffset": ("registry_node", 8, 8, 0),
    "sharedControlObjectOffset": ("shared_control", 8, 8, 8),
    "hudSubobjectOffset": ("hud", 8, 8, 8),
    "hudSubobjectPointerOffset": ("hud", 8, 8, 8),
    "hudTypeVectorOffset": ("hud_subobject", 24, 8, 8),
    "hudTypeVectorBeginOffset": ("hud_vector", 8, 8, 0),
    "hudTypeVectorEndOffset": ("hud_vector", 8, 8, 0),
    "hudTypeVectorCapacityOffset": ("hud_vector", 8, 8, 0),
    "hudTypeTokenOffset": ("hud_vector_entry", 8, 8, 0),
    "hudTypeInstancesBeginOffset": ("hud_vector_entry", 8, 8, 0),
    "hudTypeInstancesEndOffset": ("hud_vector_entry", 8, 8, 0),
    "hudTypeInstancesCapacityOffset": ("hud_vector_entry", 8, 8, 0),
    "hudTypeInstanceObjectOffset": ("hud_type_instance", 8, 8, 0),
    "hudTypeInstanceControlOffset": ("hud_type_instance", 8, 8, 0),
    "outerSecondaryOffset": ("outer", 8, 8, 8),
    "outerHudBackReferenceOffset": ("outer", 8, 8, 8),
    "outerHudControlBackReferenceOffset": ("outer", 8, 8, 8),
    "outerChildOffset": ("outer", 8, 8, 8),
    "outerSourceOffset": ("outer", 8, 8, 8),
    "outerPowerFillOffset": ("outer", 4, 4, 8),
    "outerRegenFillOffset": ("outer", 4, 4, 8),
    "childModeOffset": ("child", 4, 4, 8),
    "childAngleOffset": ("child", 4, 4, 8),
    "childBlurOffset": ("child", 4, 4, 8),
    "childSpeedDigitOneOffset": ("child", 4, 4, 8),
    "childSpeedDigitTenOffset": ("child", 4, 4, 8),
    "childSpeedDigitHundredOffset": ("child", 4, 4, 8),
    "childSpeedLessOrEqualOneOffset": ("child", 1, 1, 8),
    "childSpeedLessTenOffset": ("child", 1, 1, 8),
    "childSpeedLessHundredOffset": ("child", 1, 1, 8),
    "childSpeedUnitObjectOffset": ("child", 8, 8, 8),
    "speedUnitEnumOffset": ("speed_unit", 4, 4, 0),
    "childHeadlightsOnOffset": ("child", 1, 1, 8),
    "childPowerOffset": ("child", 4, 4, 8),
    "childRegenOffset": ("child", 4, 4, 8),
    "childRatioOffset": ("child", 4, 4, 8),
    "childGearOffset": ("child", 4, 4, 8),
    "childGearNextOffset": ("child", 4, 4, 8),
    "childGearPreviousOffset": ("child", 4, 4, 8),
    "childGearGaugeStateOffset": ("child", 4, 4, 8),
    "childUseDriveFor1Offset": ("child", 1, 1, 8),
    "childMaximumTachometerOffset": ("child", 4, 4, 8),
    "childElectricMaximumSpeedOffset": ("child", 4, 4, 8),
    "providerPowerLimitFirstOffset": ("provider", 4, 4, 8),
    "providerPowerLimitSecondOffset": ("provider", 4, 4, 8),
    "providerPowerNumeratorOffset": ("provider", 4, 4, 8),
    "providerPowerDenominatorOffset": ("provider", 4, 4, 8),
    "providerRegenTargetOffset": ("provider", 4, 4, 8),
    "providerElectricSpeedOffset": ("provider", 4, 4, 8),
}
NATIVE_GAUGE_VALUE_KEYS = {
    "registryKeyHash", "registryBucketStride", "hudSubobjectSlotZeroPrologueHex",
    "hudTypeVectorMaximumCount", "hudTypeVectorEntryStride", "hudTypeInstanceCount",
    "hudTypeInstanceStride", "childlessRegenPowerRatioBits",
    "providerPowerDenominatorScaleBits", "providerRegenScaleBits",
    "providerRegenUpperBaseBits", "speedUnitMphValue", "speedUnitKphValue",
    "requiredProviderVtableSlots",
}
NATIVE_GAUGE_KEYS = (set(NATIVE_GAUGE_RVA_KEYS) |
                     set(NATIVE_GAUGE_FUNCTION_RVA_KEYS) |
                     set(NATIVE_GAUGE_FIELD_DEFINITIONS) |
                     NATIVE_GAUGE_VALUE_KEYS)
LIMITATIONS = [
    "Offline checks do not approve a build or establish runtime compatibility.",
    "Live source-vector pointers, local-player uniqueness, car identity, RPM, "
    "tachometer maximum, and assist transitions are not observable here.",
    "Protected getter semantics, including the TCR getter, are offline-unverifiable; "
    "a readable vtable pointer does not verify the protected function.",
    "Gameplay visibility layout validation is structural only; live UI ownership, "
    "page transitions, and UI visibility are not observable from an executable copy.",
    "Signature matches are candidate evidence only. Unknown builds require separate "
    "review and validation before any compatibility pack is published.",
]


class AuditError(ValueError):
    def __init__(self, code: str, message: str):
        super().__init__(message)
        self.code, self.message = code, message


def fail(code: str, message: str) -> None:
    raise AuditError(code, message)


def read_input(path: Path, limit: int, label: str) -> bytes:
    try:
        if path.is_symlink() or not path.is_file():
            fail("input_not_regular_file", f"{label} must be a regular copied file.")
        with path.open("rb") as stream:
            data = stream.read(limit + 1)
    except OSError:
        fail("input_unreadable", f"Cannot read the supplied {label}.")
    if not data or len(data) > limit:
        fail("input_size_invalid", f"{label} is empty or exceeds the input size limit.")
    return data


def integer(value: Any, label: str, minimum: int, maximum: int) -> int:
    if type(value) is not int or not minimum <= value <= maximum:
        fail("pack_integer_invalid", f"{label} has an invalid integer value.")
    return value


def no_duplicate_keys(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            fail("pack_duplicate_key", "The pack contains a duplicate JSON key.")
        result[key] = value
    return result


def json_integer(token: str) -> int:
    value = int(token)
    if token.startswith("-") and value == 0:
        fail("pack_integer_invalid", "Negative zero is not a valid unsigned integer encoding.")
    return value


def no_nonfinite_json(token: str) -> None:
    fail("pack_json_invalid", "Non-finite numeric literals are not valid pack JSON.")


def safe_id(value: Any) -> bool:
    if (not isinstance(value, str) or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,79}", value)
            or value.endswith(".") or ".." in value):
        return False
    stem = value.split(".", 1)[0].upper()
    return stem not in {"CON", "PRN", "AUX", "NUL"} and not re.fullmatch(r"(?:COM|LPT)[1-9]", stem)


def parse_pack(data: bytes) -> dict[str, Any]:
    if len(data) > MAX_PACK_BYTES:
        fail("pack_too_large", "The pack exceeds the size limit.")
    try:
        pack = json.loads(data.decode("utf-8"), object_pairs_hook=no_duplicate_keys,
                          parse_int=json_integer, parse_constant=no_nonfinite_json)
    except AuditError:
        raise
    except (UnicodeError, ValueError, RecursionError):
        fail("pack_json_invalid", "The pack is not valid bounded UTF-8 JSON.")
    if not isinstance(pack, dict) or not {"schemaVersion", "readerVersion"} <= set(pack):
        fail("pack_keys_invalid", "The pack must contain its schema and reader versions.")
    schema = pack["schemaVersion"]
    if type(schema) is not int or schema not in (1, 2, 3):
        fail("pack_version_unsupported", "Only matching schema/reader pairs 1 through 3 are supported.")
    expected_keys = LEGACY_PACK_KEYS if schema == 1 else PACK_KEYS if schema == 2 else PACK_V3_KEYS
    if set(pack) != expected_keys:
        fail("pack_keys_invalid", "The pack does not have the exact keys for its schema.")
    if type(pack["readerVersion"]) is not int or pack["readerVersion"] != schema:
        fail("pack_version_unsupported", "Only matching schema/reader pairs 1 through 3 are supported.")
    if not safe_id(pack["id"]):
        fail("pack_id_invalid", "The pack id violates the runtime's bounded safe-filename rules.")
    integer(pack["revision"], "revision", 1, 2_147_483_647)
    version = pack["gameVersion"]
    if (not isinstance(version, str) or not re.fullmatch(r"\d{1,5}(?:\.\d{1,5}){3}", version, re.ASCII)
            or any(int(part) > 65535 for part in version.split("."))):
        fail("pack_game_version_invalid", "gameVersion must contain four bounded numeric components.")
    integer(pack["executableLength"], "executableLength", 4096, MAX_EXECUTABLE_LENGTH)
    if not isinstance(pack["executableSha256"], str) or not re.fullmatch(r"[0-9A-Fa-f]{64}", pack["executableSha256"]):
        fail("pack_hash_invalid", "executableSha256 must contain exactly 64 hexadecimal characters.")
    image_size = integer(pack["imageSize"], "imageSize", 4096, MAX_IMAGE_SIZE)
    for key, length, alignment in (
        ("sourceVectorRva", 24, 8), ("thresholdRva", 4, 4),
        ("leadVtableRva", max(SLOTS) + 8, 8),
    ):
        value = integer(pack[key], key, 1, UINT32_MAX)
        if value % alignment or value + length > image_size:
            fail("pack_rva_invalid", f"{key} is misaligned or outside the declared image.")
    if type(pack["expectedThresholdBits"]) is not int or pack["expectedThresholdBits"] != 0x3DCCCCCD:
        fail("pack_threshold_invalid", "The supported readers require threshold bits 0x3DCCCCCD.")
    fields = pack["fields"]
    if not isinstance(fields, dict) or set(fields) != set(FIELD_ALIGNMENT):
        fail("pack_fields_invalid", "fields must contain exactly the 24 shared reader field names.")
    for key, (owner, width, alignment) in FIELD_DEFINITIONS.items():
        value = integer(fields[key], f"fields.{key}", 0, MAX_FIELD_BYTES - width)
        if value % alignment:
            fail("pack_field_alignment", f"fields.{key} has invalid alignment.")
        if owner == "provider" and value < 8:
            fail("pack_field_vtable_overlap", f"fields.{key} overlaps the provider vtable pointer.")
    previous_fields: list[tuple[str, str, int, int]] = []
    for key, (owner, width, _) in FIELD_DEFINITIONS.items():
        offset = fields[key]
        for previous_key, previous_owner, previous_offset, previous_width in previous_fields:
            if owner != previous_owner or offset >= previous_offset + previous_width or previous_offset >= offset + width:
                continue
            if ({key, previous_key} == {"lcSecondary", "tcrSecondary"}
                    and offset == previous_offset):
                continue
            fail("pack_field_overlap", "Same-object fields overlap without the runtime's allowed secondary alias.")
        previous_fields.append((key, owner, offset, width))
    slots = pack["requiredVtableSlots"]
    if not isinstance(slots, list) or len(slots) != len(SLOTS):
        fail("pack_slots_invalid", "Exactly the nine shared reader vtable slots are required.")
    seen: set[int] = set()
    for slot in slots:
        if not isinstance(slot, dict) or set(slot) != {"offset", "targetRva"}:
            fail("pack_slots_invalid", "Each vtable slot needs only offset and targetRva.")
        offset = integer(slot["offset"], "slot.offset", 0, max(SLOTS))
        if offset not in SLOTS or offset in seen:
            fail("pack_slots_invalid", "The vtable slot set is incorrect or contains duplicates.")
        seen.add(offset)
        integer(slot["targetRva"], "slot.targetRva", 1, image_size - 1)
    if schema >= 2:
        validate_gameplay_visibility(pack["gameplayVisibility"], image_size)
    if schema == 3:
        validate_native_gauge(pack["nativeGauge"], image_size)
    return pack


def validate_gameplay_visibility(layout: Any, image_size: int) -> None:
    if not isinstance(layout, dict) or set(layout) != GAMEPLAY_VISIBILITY_KEYS:
        fail("pack_gameplay_keys_invalid", "gameplayVisibility must contain exactly the reader's layout properties.")
    seen_rvas: set[int] = set()
    for key in GAMEPLAY_VISIBILITY_RVA_KEYS:
        rva = integer(layout[key], f"gameplayVisibility.{key}", 1, UINT64_MAX)
        if rva % 8 or rva + 8 > image_size:
            fail("pack_gameplay_rva_invalid", f"gameplayVisibility.{key} is misaligned or outside the declared image.")
        if rva in seen_rvas:
            fail("pack_gameplay_rva_alias", "Gameplay visibility RVAs cannot alias different object identities.")
        seen_rvas.add(rva)
    previous_fields: list[tuple[str, int, int]] = []
    for key, (owner, width, alignment) in GAMEPLAY_VISIBILITY_FIELD_DEFINITIONS.items():
        offset = integer(layout[key], f"gameplayVisibility.{key}", 0, MAX_FIELD_BYTES - width)
        if offset < 8:
            fail("pack_gameplay_field_vtable_overlap", f"gameplayVisibility.{key} overlaps its object vtable pointer.")
        if offset % alignment:
            fail("pack_gameplay_field_alignment", f"gameplayVisibility.{key} has invalid alignment.")
        for previous_owner, previous_offset, previous_width in previous_fields:
            if owner == previous_owner and offset < previous_offset + previous_width and previous_offset < offset + width:
                fail("pack_gameplay_field_overlap", "Gameplay visibility fields overlap within the same object.")
        previous_fields.append((owner, offset, width))
    # The dependency owns the root manager inline. PageTransitionManagerOffset
    # is a pointer field instead, so its pointee has a separate object budget.
    manager_width = max(layout[key] + width
                        for key, (owner, width, _) in GAMEPLAY_VISIBILITY_FIELD_DEFINITIONS.items()
                        if owner == "transition_manager")
    if layout["rootTransitionManagerOffset"] + manager_width > MAX_FIELD_BYTES:
        fail("pack_gameplay_inline_bounds", "The inline root manager exceeds its containing object's bounds.")


def float_from_bits(value: Any, label: str) -> float:
    bits = integer(value, label, 0, UINT32_MAX)
    return struct.unpack("<f", struct.pack("<I", bits))[0]


def validate_stride(value: Any, first_width: int, second_width: int, label: str) -> int:
    stride = integer(value, label, 0, UINT64_MAX)
    if stride < max(first_width, second_width) or stride > 4096 or stride % 8:
        fail("pack_native_stride_invalid", f"{label} is invalid.")
    return stride


def validate_native_gauge(layout: Any, image_size: int) -> None:
    if not isinstance(layout, dict) or set(layout) != NATIVE_GAUGE_KEYS:
        fail("pack_native_keys_invalid", "nativeGauge must contain exactly the reader's layout properties.")

    identity_rvas: set[int] = set()
    for key in NATIVE_GAUGE_RVA_KEYS:
        rva = integer(layout[key], f"nativeGauge.{key}", 1, UINT64_MAX)
        if rva % 8 or rva + 8 > image_size:
            fail("pack_native_rva_invalid", f"nativeGauge.{key} is misaligned or outside the declared image.")
        if rva in identity_rvas:
            fail("pack_native_rva_alias", "Native gauge RVAs cannot alias different object identities.")
        identity_rvas.add(rva)
    for key in NATIVE_GAUGE_FUNCTION_RVA_KEYS:
        rva = integer(layout[key], f"nativeGauge.{key}", 1, UINT64_MAX)
        if rva >= image_size:
            fail("pack_native_rva_invalid", f"nativeGauge.{key} is outside the declared image.")
        if rva in identity_rvas:
            fail("pack_native_rva_alias", "Native gauge code and object RVAs cannot alias.")
        identity_rvas.add(rva)

    if integer(layout["registryKeyHash"], "nativeGauge.registryKeyHash", 1, UINT64_MAX) == 0:
        fail("pack_native_hash_invalid", "The native gauge registry key hash cannot be zero.")

    values: dict[str, int] = {}
    prior: list[tuple[str, str, int, int]] = []
    for key, (owner, width, alignment, minimum) in NATIVE_GAUGE_FIELD_DEFINITIONS.items():
        offset = integer(layout[key], f"nativeGauge.{key}", minimum, MAX_FIELD_BYTES - width)
        if offset % alignment:
            fail("pack_native_field_alignment", f"nativeGauge.{key} has invalid alignment.")
        for previous_key, previous_owner, previous_offset, previous_width in prior:
            if (owner == previous_owner and offset < previous_offset + previous_width
                    and previous_offset < offset + width):
                fail("pack_native_field_overlap",
                     f"nativeGauge.{key} overlaps nativeGauge.{previous_key} within the same object.")
        prior.append((key, owner, offset, width))
        values[key] = offset

    mph = integer(layout["speedUnitMphValue"], "nativeGauge.speedUnitMphValue", 0, UINT32_MAX)
    kph = integer(layout["speedUnitKphValue"], "nativeGauge.speedUnitKphValue", 0, UINT32_MAX)
    if mph > 2_147_483_647 or kph > 2_147_483_647 or mph == kph:
        fail("pack_native_speed_units_invalid", "The native speed-unit values are invalid.")

    ratio = float_from_bits(layout["childlessRegenPowerRatioBits"],
                            "nativeGauge.childlessRegenPowerRatioBits")
    denominator_scale = float_from_bits(layout["providerPowerDenominatorScaleBits"],
                                        "nativeGauge.providerPowerDenominatorScaleBits")
    regen_scale = float_from_bits(layout["providerRegenScaleBits"],
                                  "nativeGauge.providerRegenScaleBits")
    regen_upper = float_from_bits(layout["providerRegenUpperBaseBits"],
                                  "nativeGauge.providerRegenUpperBaseBits")
    if not math.isfinite(ratio) or not 0 <= ratio <= 1:
        fail("pack_native_float_invalid", "The childless electric regen/power ratio is invalid.")
    if not math.isfinite(denominator_scale) or not denominator_scale > 0:
        fail("pack_native_float_invalid", "The native power denominator scale is invalid.")
    if not math.isfinite(regen_scale) or not regen_scale < 0:
        fail("pack_native_float_invalid", "The native regeneration scale is invalid.")
    if not math.isfinite(regen_upper) or not 0 <= regen_upper <= 1:
        fail("pack_native_float_invalid", "The native regeneration upper base is invalid.")

    validate_stride(layout["registryBucketStride"],
                    values["registryBucketBoundaryOffset"] + 8,
                    values["registryBucketNodeOffset"] + 8,
                    "nativeGauge.registryBucketStride")
    vector_maximum = integer(layout["hudTypeVectorMaximumCount"],
                             "nativeGauge.hudTypeVectorMaximumCount", 1, 1024)
    if not 1 <= vector_maximum <= 1024:
        fail("pack_native_count_invalid", "The native gauge HUD vector count limit is invalid.")
    validate_stride(layout["hudTypeVectorEntryStride"], values["hudTypeTokenOffset"] + 8,
                    values["hudTypeInstancesCapacityOffset"] + 8,
                    "nativeGauge.hudTypeVectorEntryStride")
    if integer(layout["hudTypeInstanceCount"], "nativeGauge.hudTypeInstanceCount", 0, UINT64_MAX) != 1:
        fail("pack_native_count_invalid", "The native gauge HUD type must resolve to exactly one instance.")
    validate_stride(layout["hudTypeInstanceStride"], values["hudTypeInstanceObjectOffset"] + 8,
                    values["hudTypeInstanceControlOffset"] + 8,
                    "nativeGauge.hudTypeInstanceStride")

    prologue = layout["hudSubobjectSlotZeroPrologueHex"]
    if not isinstance(prologue, str) or not re.fullmatch(r"[0-9A-Fa-f]{10}", prologue):
        fail("pack_native_prologue_invalid", "The native gauge function prologue must contain five hexadecimal bytes.")

    slots = layout["requiredProviderVtableSlots"]
    if not isinstance(slots, list) or len(slots) != len(NATIVE_GAUGE_PROVIDER_SLOTS):
        fail("pack_native_slots_invalid", "Every native gauge provider vtable guard is required.")
    seen_slots: set[int] = set()
    for slot in slots:
        if not isinstance(slot, dict) or set(slot) != {"offset", "targetRva"}:
            fail("pack_native_slots_invalid", "Each native gauge provider slot needs only offset and targetRva.")
        offset = integer(slot["offset"], "nativeGauge.slot.offset", 0, max(NATIVE_GAUGE_PROVIDER_SLOTS))
        target = integer(slot["targetRva"], "nativeGauge.slot.targetRva", 1, image_size - 1)
        if offset not in NATIVE_GAUGE_PROVIDER_SLOTS or offset in seen_slots:
            fail("pack_native_slots_invalid", "The native gauge provider slot set is invalid or duplicated.")
        if target in identity_rvas:
            fail("pack_native_rva_alias", "A native gauge provider target aliases another guarded identity.")
        seen_slots.add(offset)
        identity_rvas.add(target)

    containing_width = values["hudTypeVectorOffset"] + values["hudTypeVectorCapacityOffset"] + 8
    if containing_width > MAX_FIELD_BYTES or values["hudSubobjectOffset"] > MAX_FIELD_BYTES - containing_width:
        fail("pack_native_containing_bounds", "The native gauge HUD subobject exceeds its containing object bounds.")


@dataclass(frozen=True)
class Section:
    name: str
    rva: int
    virtual_size: int
    file_offset: int
    raw_size: int
    characteristics: int

    @property
    def mapped_size(self) -> int:
        return max(self.virtual_size, self.raw_size)

    @property
    def readable(self) -> bool:
        return bool(self.characteristics & READ)

    @property
    def writable(self) -> bool:
        return bool(self.characteristics & WRITE)

    @property
    def executable(self) -> bool:
        return bool(self.characteristics & EXECUTE)

    def describe(self) -> dict[str, Any]:
        return {
            "name": self.name, "imageRva": self.rva, "virtualSize": self.virtual_size,
            "fileOffset": self.file_offset, "rawSize": self.raw_size,
            "readable": self.readable, "writable": self.writable, "executable": self.executable,
        }


class PEImage:
    def __init__(self, data: bytes):
        self.data = data
        if len(data) > MAX_EXE_BYTES or data[:2] != b"MZ":
            fail("pe_dos_header_invalid", "The copied input is not a bounded PE executable.")
        pe_offset = self.unpack("<I", 0x3C)[0]
        if pe_offset < 0x40 or self.slice_file(pe_offset, 4) != b"PE\0\0":
            fail("pe_signature_invalid", "The PE signature or offset is invalid.")
        machine, count, _, _, _, optional_size, _ = self.unpack("<HHIIIHH", pe_offset + 4)
        if machine != 0x8664 or not 1 <= count <= 96:
            fail("pe_architecture_invalid", "Only bounded AMD64 PE32+ section tables are supported.")
        optional = pe_offset + 24
        self.slice_file(optional, optional_size)
        if optional_size < 112 or self.unpack("<H", optional)[0] != 0x20B:
            fail("pe_optional_header_invalid", "The image must contain a PE32+ optional header.")
        self.image_base = self.unpack("<Q", optional + 24)[0]
        alignment, file_alignment = self.unpack("<II", optional + 32)
        self.image_size, self.header_size = self.unpack("<II", optional + 56)
        if (not alignment or alignment & (alignment - 1)
                or not file_alignment or file_alignment & (file_alignment - 1)
                or alignment < file_alignment or file_alignment > 65536
                or not self.image_size or self.image_size % alignment
                or not 0 < self.header_size <= min(len(data), self.image_size)
                or self.image_base + self.image_size > UINT64_MAX):
            fail("pe_image_bounds_invalid", "PE image, header, or alignment bounds are invalid.")
        directory_count = self.unpack("<I", optional + 108)[0]
        if directory_count > 16 or 112 + directory_count * 8 > optional_size:
            fail("pe_directories_invalid", "The PE data-directory table is invalid.")
        self.directories = [self.unpack("<II", optional + 112 + index * 8)
                            for index in range(directory_count)]
        table = optional + optional_size
        if table + count * 40 > self.header_size:
            fail("pe_sections_invalid", "The section table exceeds the file-backed headers.")
        self.sections: list[Section] = []
        for index in range(count):
            entry = table + index * 40
            raw_name = self.slice_file(entry, 8).split(b"\0", 1)[0]
            name = "".join(chr(value) if 32 <= value < 127 else "?" for value in raw_name)
            virtual_size, rva, raw_size, file_offset = self.unpack("<IIII", entry + 8)
            flags = self.unpack("<I", entry + 36)[0]
            section = Section(name, rva, virtual_size, file_offset, raw_size, flags)
            if (not section.mapped_size or rva < self.header_size or rva % alignment
                    or rva + section.mapped_size > self.image_size):
                fail("pe_section_virtual_bounds", "A section has invalid virtual image bounds.")
            if raw_size:
                if file_offset < self.header_size or file_offset % file_alignment:
                    fail("pe_section_file_bounds", "A section has invalid raw-file alignment or bounds.")
                self.slice_file(file_offset, raw_size)
            self.sections.append(section)
        for attribute, length_attribute in (("rva", "mapped_size"), ("file_offset", "raw_size")):
            spans = sorted((getattr(section, attribute), getattr(section, length_attribute))
                           for section in self.sections if getattr(section, length_attribute))
            if any(start + size > next_start for (start, size), (next_start, _) in zip(spans, spans[1:])):
                fail("pe_section_overlap", "PE sections overlap in file or image address space.")

    def slice_file(self, offset: int, size: int) -> bytes:
        if offset < 0 or size < 0 or offset > len(self.data) or size > len(self.data) - offset:
            fail("pe_file_truncated", "A requested PE span exceeds the copied file.")
        return self.data[offset:offset + size]

    def unpack(self, fmt: str, offset: int) -> tuple[Any, ...]:
        return struct.unpack(fmt, self.slice_file(offset, struct.calcsize(fmt)))

    def section_for_span(self, rva: int, size: int) -> Section:
        if (type(rva) is not int or type(size) is not int or rva < 0 or size <= 0
                or rva >= self.image_size or size > self.image_size - rva):
            fail("rva_out_of_image", "A requested RVA span is outside SizeOfImage.")
        for section in self.sections:
            delta = rva - section.rva
            if 0 <= delta and size <= section.mapped_size - delta:
                return section
        fail("rva_unmapped", "A requested RVA span is not contained in one mapped section.")

    def span(self, rva: int, size: int) -> dict[str, Any]:
        section = self.section_for_span(rva, size)
        delta = rva - section.rva
        available = max(0, min(size, section.raw_size - delta))
        return {
            "imageRva": rva, "byteCount": size, "section": section.name,
            "fileOffset": section.file_offset + delta if available else None,
            "fileBytesAvailable": available, "fileBacked": available == size,
            "readable": section.readable, "writable": section.writable,
            "executable": section.executable,
        }

    def read_rva(self, rva: int, size: int) -> bytes:
        location = self.span(rva, size)
        if not location["readable"]:
            fail("rva_not_readable", "A requested RVA span is not in a readable section.")
        if not location["fileBacked"]:
            fail("rva_not_file_backed", "A requested RVA span has no complete raw-file backing.")
        return self.slice_file(location["fileOffset"], size)

    def executable_span(self, rva: int, size: int = 1) -> dict[str, Any]:
        location = self.span(rva, size)
        if not location["executable"] or not location["readable"]:
            fail("target_not_code", "A function target is not in a readable executable section.")
        return location

    def directory(self, index: int) -> tuple[int, int]:
        return self.directories[index] if index < len(self.directories) else (0, 0)

    def file_version(self) -> str | None:
        resource_rva, resource_size = self.directory(2)
        if not resource_rva and not resource_size:
            return None
        if not resource_rva or not resource_size:
            fail("version_resource_invalid", "The resource directory has incomplete bounds.")
        resource_location = self.span(resource_rva, resource_size)
        if not resource_location["readable"] or not resource_location["fileBacked"]:
            fail("version_resource_invalid", "The resource directory is not completely file-backed and readable.")

        def resource(offset: int, size: int) -> bytes:
            if offset < 0 or size < 0 or offset + size > resource_size:
                fail("version_resource_invalid", "Version-resource metadata exceeds its directory.")
            return self.read_rva(resource_rva + offset, size)

        def entries(offset: int) -> list[tuple[int, int]]:
            header = resource(offset, 16)
            named, ids = struct.unpack_from("<HH", header, 12)
            if named + ids > 256:
                fail("version_resource_invalid", "A resource directory exceeds the entry limit.")
            raw = resource(offset + 16, (named + ids) * 8)
            return list(struct.iter_unpack("<II", raw))

        roots = [target for name, target in entries(0) if name == 16]
        versions: set[str] = set()
        leaves = 0
        nodes = 0

        def visit(target: int, depth: int, visited: frozenset[int]) -> None:
            nonlocal leaves, nodes
            nodes += 1
            if nodes > MAX_RESOURCE_NODES:
                fail("version_resource_invalid", "The version-resource traversal budget was exceeded.")
            offset = target & 0x7FFFFFFF
            if target & 0x80000000:
                if depth > 3 or offset in visited:
                    fail("version_resource_invalid", "Version-resource nesting is invalid.")
                for _, child in entries(offset):
                    visit(child, depth + 1, visited | {offset})
                return
            leaves += 1
            if leaves > 32:
                fail("version_resource_invalid", "The version-resource leaf limit was exceeded.")
            blob_rva, blob_size, _, _ = struct.unpack("<IIII", resource(offset, 16))
            if not 6 <= blob_size <= 65536:
                fail("version_resource_invalid", "A version-resource payload has invalid length.")
            blob = self.read_rva(blob_rva, blob_size)
            length, value_length, kind = struct.unpack_from("<HHH", blob)
            key = "VS_VERSION_INFO\0".encode("utf-16le")
            value_offset = (6 + len(key) + 3) & ~3
            if (length > len(blob) or kind != 0 or value_length < 52
                    or value_offset + value_length > length or blob[6:6 + len(key)] != key):
                fail("version_resource_invalid", "The fixed version-info payload is invalid.")
            fixed = struct.unpack_from("<13I", blob, value_offset)
            if fixed[0] != 0xFEEF04BD:
                fail("version_resource_invalid", "The fixed version-info signature is invalid.")
            versions.add(f"{fixed[2] >> 16}.{fixed[2] & 65535}.{fixed[3] >> 16}.{fixed[3] & 65535}")

        for root in roots:
            visit(root, 1, frozenset())
        if len(versions) > 1:
            fail("version_resource_ambiguous", "The PE contains conflicting file versions.")
        return next(iter(versions)) if versions else None

    def fingerprint(self) -> dict[str, Any]:
        return {
            "gameVersion": self.file_version(), "executableLength": len(self.data),
            "executableSha256": hashlib.sha256(self.data).hexdigest().upper(),
            "imageSize": self.image_size, "preferredImageBase": self.image_base,
            "architecture": "AMD64/PE32+",
        }


def report_base(operation: str) -> dict[str, Any]:
    return {
        "reportVersion": 1, "operation": operation, "reviewOnly": True,
        "runtimeApproval": "not_granted", "compatibilityEstablished": False,
        "limitations": list(LIMITATIONS),
    }


def inspect_image(image: PEImage) -> dict[str, Any]:
    report = report_base("inspect")
    report.update(inputLabel="executable", fingerprint=image.fingerprint(),
                  sections=[section.describe() for section in image.sections])
    return report


def check_result(name: str, action: Any) -> dict[str, Any]:
    try:
        return {"name": name, "status": "passed", **action()}
    except AuditError as error:
        return {"name": name, "status": "failed", "code": error.code, "message": error.message}


def vtable_target(image: PEImage, table_rva: int, slot: int) -> int:
    target_va = struct.unpack("<Q", image.read_rva(table_rva + slot, 8))[0]
    if not image.image_base <= target_va < image.image_base + image.image_size:
        fail("vtable_target_out_of_image", "A vtable pointer is outside the preferred image address range.")
    target_rva = target_va - image.image_base
    image.executable_span(target_rva)
    return target_rva


def verify_image(image: PEImage, pack: dict[str, Any]) -> dict[str, Any]:
    report = report_base("verify")
    actual = image.fingerprint()
    fingerprint_checks = []
    for key in ("gameVersion", "executableLength", "executableSha256", "imageSize"):
        expected = pack[key].upper() if key == "executableSha256" else pack[key]
        fingerprint_checks.append({
            "name": key, "status": "passed" if actual[key] == expected else "failed",
            "expected": expected, "actual": actual[key],
        })
    checks = []

    def vector() -> dict[str, Any]:
        location = image.span(pack["sourceVectorRva"], 24)
        if not location["readable"] or not location["writable"]:
            fail("vector_section_invalid", "The source vector is not in readable writable image data.")
        return {"location": location, "liveContents": "not_observable_offline"}

    checks.append(check_result("sourceVectorBounds", vector))

    def threshold() -> dict[str, Any]:
        rva = pack["thresholdRva"]
        bits = struct.unpack("<I", image.read_rva(rva, 4))[0]
        return {
            "status": "passed" if bits == pack["expectedThresholdBits"] else "failed",
            "location": image.span(rva, 4), "expectedBits": pack["expectedThresholdBits"],
            "actualBits": bits,
        }

    checks.append(check_result("thresholdBits", threshold))

    def vtable() -> dict[str, Any]:
        location = image.span(pack["leadVtableRva"], max(SLOTS) + 8)
        if not location["readable"] or not location["fileBacked"] or location["executable"]:
            fail("vtable_section_invalid", "The vtable span must be file-backed readable non-code data.")
        return {"location": location}

    checks.append(check_result("leadVtableBounds", vtable))
    for slot in sorted(pack["requiredVtableSlots"], key=lambda entry: entry["offset"]):
        def verify_slot(slot: dict[str, int] = slot) -> dict[str, Any]:
            rva = pack["leadVtableRva"] + slot["offset"]
            actual_va = struct.unpack("<Q", image.read_rva(rva, 8))[0]
            expected_va = image.image_base + slot["targetRva"]
            target_location = image.executable_span(slot["targetRva"])
            return {
                "status": "passed" if actual_va == expected_va else "failed",
                "slotOffset": slot["offset"], "location": image.span(rva, 8),
                "expectedTargetRva": slot["targetRva"], "expectedPreferredVa": expected_va,
                "actualPreferredVa": actual_va,
                "actualTargetRva": actual_va - image.image_base
                if image.image_base <= actual_va < image.image_base + image.image_size else None,
                "expectedTargetLocation": target_location,
            }

        checks.append(check_result(f"vtableSlot{slot['offset']:04X}", verify_slot))
    for offset, field in GETTER_FIELDS.items():
        def getter(offset: int = offset, field: str = field) -> dict[str, Any]:
            signature = b"\xF3\x0F\x10\x81" + struct.pack("<I", pack["fields"][field]) + b"\xC3"
            rva = vtable_target(image, pack["leadVtableRva"], offset)
            location = image.executable_span(rva, len(signature))
            matched = image.read_rva(rva, len(signature)) == signature
            return {
                "status": "passed" if matched else "failed", "location": location,
                "slotOffset": offset,
                "slotLocation": image.span(pack["leadVtableRva"] + offset, 8),
                "runtimeGuard": offset in SLOTS,
                "field": field, "fieldOffset": pack["fields"][field],
                "evidence": "Explicit-target movss/ret byte anchor only; not general semantic proof.",
            }

        checks.append(check_result(f"readableGetter{offset:04X}", getter))
    report.update(
        inputLabel="executable", packId=pack["id"], packRevision=pack["revision"],
        fingerprint=actual, fingerprintChecks=fingerprint_checks, checks=checks,
        fingerprintMatched=all(check["status"] == "passed" for check in fingerprint_checks),
    )
    report["offlineChecksPassed"] = report["fingerprintMatched"] and all(
        check["status"] == "passed" for check in checks)
    report["result"] = "offline_checks_passed_review_only" if report["offlineChecksPassed"] else "review_required"
    return report


def load_capstone() -> Any:
    try:
        return importlib.import_module("capstone")
    except ImportError:
        fail("discovery_dependency_missing",
             "discover requires Capstone in the developer environment; inspect and verify use only the standard library.")


def getter_signature(image: PEImage, rva: int, field: int, capstone: Any) -> bytes:
    raw = image.read_rva(rva, 9)
    image.executable_span(rva, len(raw))
    decoder = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    decoder.detail = True
    instructions = list(decoder.disasm(raw, image.image_base + rva, count=2))
    constants = importlib.import_module("capstone.x86_const")
    if len(instructions) != 2:
        fail("reference_getter_unverified", "The reference getter cannot be decoded as a bounded getter.")
    first, last = instructions
    operands = first.operands
    if (first.mnemonic != "movss" or last.mnemonic != "ret" or last.operands
            or len(operands) != 2 or operands[0].type != constants.X86_OP_REG
            or operands[0].reg != constants.X86_REG_XMM0 or operands[1].type != constants.X86_OP_MEM
            or operands[1].size != 4 or operands[1].mem.base != constants.X86_REG_RCX
            or operands[1].mem.index != 0 or operands[1].mem.disp != field
            or first.size + last.size != len(raw)):
        fail("reference_getter_unverified", "The reference getter is not the expected field-preserving movss/ret shape.")
    return raw


def pointer_anchored_candidates(
    candidate: PEImage, signatures: dict[int, bytes],
) -> tuple[list[dict[str, Any]], int, bool]:
    """Entry-boundary evidence is aligned data pointers, not arbitrary byte regexes."""
    results = []
    seen: set[int] = set()
    pointers = 0
    complete = True
    code_sections = [section for section in candidate.sections if section.executable and section.readable]
    code_min = min((section.rva for section in code_sections), default=candidate.image_size)
    code_max = max((section.rva + section.mapped_size for section in code_sections), default=0)
    for section in candidate.sections:
        if not section.readable or section.writable or section.executable or not section.raw_size:
            continue
        start = (-section.rva) % 8
        length = ((section.raw_size - start) // 8) * 8
        if length <= 0:
            continue
        raw = memoryview(candidate.data)[section.file_offset + start:section.file_offset + start + length]
        for index, (target_va,) in enumerate(struct.iter_unpack("<Q", raw)):
            pointers += 1
            if pointers > MAX_POINTERS:
                return results, pointers - 1, False
            target = target_va - candidate.image_base
            if not code_min <= target < code_max:
                continue
            pointer_rva = section.rva + start + index * 8
            table = pointer_rva - 0x680
            if table <= 0 or table in seen:
                continue
            try:
                candidate.executable_span(target, len(signatures[0x680]))
                if candidate.read_rva(target, len(signatures[0x680])) != signatures[0x680]:
                    continue
                location = candidate.span(table, max(SLOTS) + 8)
                if not location["fileBacked"] or location["executable"] or location["writable"]:
                    continue
                slots = []
                code_anchors = []
                for offset in sorted(set(SLOTS) | set(signatures)):
                    slot_rva = table + offset
                    function_va = struct.unpack("<Q", candidate.read_rva(slot_rva, 8))[0]
                    function_rva = function_va - candidate.image_base
                    function_location = candidate.executable_span(function_rva)
                    if offset in signatures:
                        if candidate.read_rva(function_rva, len(signatures[offset])) != signatures[offset]:
                            fail("candidate_anchor_mismatch", "A candidate does not preserve both getter anchors.")
                        code_anchors.append({
                            "offset": offset, "targetRva": function_rva,
                            "field": GETTER_FIELDS[offset], "runtimeGuard": offset in SLOTS,
                        })
                    slot_evidence = {
                        "offset": offset, "targetRva": function_rva,
                        "slotFileOffset": candidate.span(slot_rva, 8)["fileOffset"],
                        "targetSection": function_location["section"],
                    }
                    if offset in SLOTS:
                        slots.append(slot_evidence)
                seen.add(table)
                if len(results) >= MAX_CANDIDATES:
                    complete = False
                    continue
                results.append({
                    "candidateLeadVtable": location, "candidateSlots": slots,
                    "candidateCodeAnchors": code_anchors,
                    "entryBoundaryEvidence": "Aligned candidate vtable pointers to byte-identical decoded reference getters.",
                    "reviewOnly": True,
                })
            except AuditError:
                continue
    return results, pointers, complete


def discover_images(reference: PEImage, pack: dict[str, Any], candidate: PEImage) -> dict[str, Any]:
    reference_report = verify_image(reference, pack)
    if not reference_report["offlineChecksPassed"]:
        fail("reference_verification_failed", "The reference copy does not pass the supplied pack's offline checks.")
    capstone = load_capstone()
    slots = {offset: vtable_target(reference, pack["leadVtableRva"], offset)
             for offset in GETTER_FIELDS}
    signatures = {
        offset: getter_signature(reference, slots[offset], pack["fields"][field], capstone)
        for offset, field in GETTER_FIELDS.items()
    }
    candidates, examined, complete = pointer_anchored_candidates(candidate, signatures)
    report = report_base("discover")
    report.update(
        inputLabel="candidate", referenceFingerprint=reference_report["fingerprint"],
        fingerprint=candidate.fingerprint(), result="review_required",
        candidates=candidates, reportedCandidateCount=len(candidates),
        referenceCodeAnchors=[
            {"offset": offset, "targetRva": slots[offset], "field": field,
             "fieldOffset": pack["fields"][field], "runtimeGuard": offset in SLOTS}
            for offset, field in GETTER_FIELDS.items()
        ],
        ambiguous=len(candidates) > 1 or not complete, scanComplete=complete,
        dataPointersExamined=examined, pointerLimit=MAX_POINTERS, candidateLimit=MAX_CANDIDATES,
        sourceVectorDiscovery="not_attempted_requires_initializer_destructor_evidence",
        thresholdDiscovery="not_attempted_constant_matches_are_not_identity_evidence",
        instructionEvidence="Only two simple getter bodies are compared, with member offsets retained.",
        candidateBoundaryLimit="Candidate entry boundaries are inferred from vtable-like pointers, not independently proven by call sites or unwind data.",
        absenceOfCandidates="Does not establish incompatibility; code generation or protection may have changed.",
    )
    return report


class SafeArgumentParser(argparse.ArgumentParser):
    def error(self, message: str) -> None:
        fail("arguments_invalid", "Invalid command arguments; use --help.")


def build_parser() -> argparse.ArgumentParser:
    parser = SafeArgumentParser(
        description=__doc__,
        epilog="Exit codes: 0 = inspection or offline checks passed (never approval); "
               "1 = invalid input/dependency; 2 = review required, including every discovery report.",
    )
    commands = parser.add_subparsers(dest="command", required=True, parser_class=SafeArgumentParser)
    inspect = commands.add_parser("inspect", help="Inspect an explicitly supplied executable copy.")
    inspect.add_argument("--exe-copy", required=True, type=Path)
    verify = commands.add_parser("verify", help="Check a copied executable against an unsigned local pack.")
    verify.add_argument("--exe-copy", required=True, type=Path)
    verify.add_argument("--pack", required=True, type=Path)
    discover = commands.add_parser("discover", help="Report review-only pointer-anchored candidates; requires Capstone.")
    discover.add_argument("--reference-exe-copy", required=True, type=Path)
    discover.add_argument("--reference-pack", required=True, type=Path)
    discover.add_argument("--candidate-exe-copy", required=True, type=Path)
    return parser


def main(argv: list[str] | None = None) -> int:
    operation = "unknown"
    try:
        args = build_parser().parse_args(argv)
        operation = args.command
        if operation == "discover":
            reference = PEImage(read_input(args.reference_exe_copy, MAX_EXE_BYTES, "reference copy"))
            pack = parse_pack(read_input(args.reference_pack, MAX_PACK_BYTES, "reference pack"))
            candidate = PEImage(read_input(args.candidate_exe_copy, MAX_EXE_BYTES, "candidate copy"))
            report = discover_images(reference, pack, candidate)
            code = 2
        else:
            image = PEImage(read_input(args.exe_copy, MAX_EXE_BYTES, "executable copy"))
            if operation == "inspect":
                report = inspect_image(image)
                code = 0
            else:
                pack = parse_pack(read_input(args.pack, MAX_PACK_BYTES, "pack"))
                report = verify_image(image, pack)
                code = 0 if report["offlineChecksPassed"] else 2
        output = json.dumps(report, indent=2, sort_keys=True, allow_nan=False)
        if len(output.encode("utf-8")) > MAX_REPORT_BYTES:
            fail("report_limit_exceeded", "The bounded report size limit was exceeded.")
    except (AuditError, OSError, ValueError, struct.error, OverflowError, RecursionError) as error:
        report = report_base(operation)
        report["error"] = {
            "code": error.code if isinstance(error, AuditError) else "input_processing_failed",
            "message": error.message if isinstance(error, AuditError) else "The supplied inputs could not be safely processed.",
        }
        output = json.dumps(report, indent=2, sort_keys=True)
        code = 1
    print(output)
    return code


if __name__ == "__main__":
    sys.exit(main())
