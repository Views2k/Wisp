"""Closed Tune descriptor validation for the offline compatibility auditor."""

from __future__ import annotations

import json
import re
from typing import Any


PROFILE = "fh6-tune-imperial-v1"
MAX_JSON = 32 * 1024
MAX_ASSET = 17_269_760
RVA_ROLES = {
    "0640EE78": (8, 8), "06446D18": (8, 8), "064BA6E8": (4, 4),
    "064FBE80": (8, 8), "065AC730": (8, 8), "06C79E70": (8, 8),
    "06C7A570": (8, 8), "06C7B3D8": (8, 8), "06C7B9B0": (8, 8),
    "06C7BC10": (8, 8), "08F12A78": (8, 8), "08F75670": (1024, 4),
    "08F75A70": (256, 1), "0A7DB9E8": (8, 8), "0A861342": (1, 1),
    "0A861470": (5120, 8), "0A862058": (8, 8), "0A862060": (8, 8),
    "0A8AF088": (8, 8),
}
STEAM_RANGES = "7D4830 7D6F90 7E5770 8268A0 8F3790 8F37E0 A702F0 A72FF0 A74830 A74870 A74940 A74980 A74BF0 A74CD0 A74CF0 A75AF0 A76E40 A76F70 A782F0 A7A5E0 A7AAE0 A7AB00 A7AB20 A7AB40 1812E20 1812EAA 1812FB6 1813064 1813690 1813FA0 1814670 1816680 1819680 18196E0 1819750 2A9FBD0 2A9FBE0 2A9FC60 2BCEB40 3123480 31AC820 31ACBB0 34E5090 34EAB00 34EAFE0 34EAFED 34ECE40 34F0D30 34F3C50 34F8180 34F8260 34F89C0 34F8A20 34FB0D0 34FB190 34FB1E0 34FB210 34FB240"
STORE_RANGES = "4C1890 4C4140 75C530 75EC90 76D470 7AE5A0 87BC00 87BC50 9F86C0 9FB3C0 9FCC00 9FCC40 9FCD10 9FCD50 9FCFC0 9FD0A0 9FD0C0 9FDEC0 9FF210 9FF340 A006C0 A029B0 A02EB0 A02ED0 A02EF0 A02F10 179C280 179C30A 179C416 179C4C4 179CAF0 179D400 179DAD0 179FAE0 17A3100 17A3160 17A31D0 2A10E70 2A16030 2A22BC0 2A22BD0 2A22C50 2A29280 2B25E40 2B4D8B0 30A4750 312DAF0 312DE80 347DF20 3483990 3483E20 3483E70 3483E7D 3485CD0 3489BC0 348CAE0 3491010 34910F0 3491850 34918B0 3493F60 3494020 3494070 34940A0 34940D0"
PARTS = {"Engine", "Drivetrain", "CarBody", "Motor", "Brakes", "SpringDamper", "FrontAntiroll",
         "RearAntiroll", "RearAero", "Transmission", "Differential", "FrontAero"}


def require(condition: bool) -> None:
    if not condition:
        raise ValueError("The Tune descriptor is incomplete or outside the fixed reader contract.")


def integer(value: Any, minimum: int, maximum: int) -> int:
    require(type(value) is int and minimum <= value <= maximum)
    return value


def obj(value: Any, keys: set[str]) -> dict[str, Any]:
    require(isinstance(value, dict) and set(value) == keys)
    return value


def digest(value: Any) -> None:
    require(isinstance(value, str) and re.fullmatch(r"[0-9A-Fa-f]{64}", value) is not None)


def span(value: Any, width: int, alignment: int, image_size: int) -> int:
    rva = integer(value, 4096, image_size - 1)
    require(rva % alignment == 0 and width <= image_size - rva)
    return rva


def validate_tune(layout: Any, image_size: int, provider_table: int, store: bool) -> None:
    obj(layout, {"semanticsVersion", "profileId", "rvas", "codeGuards", "providerSlots", "asset"})
    require(len(json.dumps(layout, ensure_ascii=False, separators=(",", ":")).encode("utf-8")) <= MAX_JSON)
    require(integer(layout["semanticsVersion"], 1, 1) == 1 and layout["profileId"] == PROFILE)
    rvas = obj(layout["rvas"], set(RVA_ROLES))
    for role, (width, alignment) in RVA_ROLES.items():
        span(rvas[role], width, alignment, image_size)
    require(len(set(rvas.values())) == len(rvas))
    platform = "store" if store else "steam"
    roles = {f"{platform}-range-{value.zfill(8)}" for value in (STORE_RANGES if store else STEAM_RANGES).split()}
    guards = layout["codeGuards"]
    require(isinstance(guards, list) and len(guards) == len(roles))
    found: set[str] = set()
    ranges: list[tuple[int, int]] = []
    total = 0
    for guard in guards:
        obj(guard, {"role", "rva", "length", "sha256"})
        require(isinstance(guard["role"], str) and guard["role"] in roles and guard["role"] not in found)
        found.add(guard["role"])
        length = integer(guard["length"], 1, 8192)
        total += length
        require(total <= 48 * 1024)
        rva = span(guard["rva"], length, 1, image_size)
        require(all(rva >= end or start >= rva + length for start, end in ranges))
        ranges.append((rva, rva + length))
        digest(guard["sha256"])
    slots = layout["providerSlots"]
    require(isinstance(slots, list) and len(slots) == 2)
    found_slots: set[int] = set()
    for slot in slots:
        obj(slot, {"offset", "targetRva"})
        offset = integer(slot["offset"], 0, 0x12E8)
        require(offset in (0, 0x12E8) and offset not in found_slots)
        found_slots.add(offset)
        span(provider_table, offset + 8, 8, image_size)
        target = span(slot["targetRva"], 1, 1, image_size)
        original = ("030A4750" if offset == 0 else "0312DE80") if store else ("03123480" if offset == 0 else "031ACBB0")
        require(any(guard["role"] == f"{platform}-range-{original}" and guard["rva"] == target for guard in guards))
    asset = obj(layout["asset"], {"minimumLength", "maximumLength", "headerPageCount", "parts"})
    minimum = integer(asset["minimumLength"], 1024, MAX_ASSET)
    maximum = integer(asset["maximumLength"], minimum, MAX_ASSET)
    require(minimum % 1024 == maximum % 1024 == 0)
    integer(asset["headerPageCount"], 1, minimum // 1024)
    parts = obj(asset["parts"], PARTS)
    rows = 0
    for part in parts.values():
        obj(part, {"count", "rowsSha256"})
        rows += integer(part["count"], 1, 250_000)
        require(rows <= 250_000)
        digest(part["rowsSha256"])
