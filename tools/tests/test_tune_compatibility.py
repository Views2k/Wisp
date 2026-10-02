import copy
import hashlib
import json
from pathlib import Path
import struct
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import compatibility_audit as audit
import tune_compatibility as tune
from test_compatibility_audit import fixture, SECTION_TABLE


DIRECTORY = Path(__file__).resolve().parents[2] / "src/Wisp.App/NativeCompatibility"


def current(store=False):
    return json.loads((DIRECTORY / ("fh6-store-3.440.853.0.json" if store else "fh6-6.440.853.0.json")).read_bytes())


class TunePackTests(unittest.TestCase):
    def parse(self, pack):
        return audit.parse_pack(json.dumps(pack).encode())

    def test_current_both_platforms_are_complete_and_old_versions_remain_supported(self):
        for store in (False, True):
            pack = current(store)
            self.assertEqual(pack, self.parse(pack))
            self.assertEqual(19, len(pack["tune"]["rvas"]))
            self.assertEqual(65 if store else 58, len(pack["tune"]["codeGuards"]))
            pack["tune"] = None
            self.assertEqual(pack, self.parse(pack))
            pack["schemaVersion"] = pack["readerVersion"] = 4 if store else 3
            with self.assertRaises(audit.AuditError):
                self.parse(pack)
            del pack["tune"]
            self.assertEqual(pack, self.parse(pack))

    def test_closed_roles_sizes_ranges_and_data_expectations_fail_closed(self):
        mutations = [
            lambda p: p["tune"].update(semanticsVersion=2),
            lambda p: p["tune"].update(profileId="unknown"),
            lambda p: p["tune"]["rvas"].pop("0640EE78"),
            lambda p: p["tune"]["rvas"].update(unreviewed=4096),
            lambda p: p["tune"]["rvas"].update({"08F75670": p["imageSize"] - 4}),
            lambda p: p["tune"]["codeGuards"].pop(),
            lambda p: p["tune"]["codeGuards"][0].update(role="unknown"),
            lambda p: p["tune"]["codeGuards"][0].update(role=p["tune"]["codeGuards"][1]["role"]),
            lambda p: p["tune"]["codeGuards"][0].update(rva=p["tune"]["codeGuards"][1]["rva"]),
            lambda p: p["tune"]["codeGuards"][0].update(length=8193),
            lambda p: p["tune"]["codeGuards"][0].update(sha256="G" * 64),
            lambda p: p["tune"]["codeGuards"][0].update(mask="wildcard"),
            lambda p: p["tune"]["providerSlots"][0].update(targetRva=p["tune"]["codeGuards"][0]["rva"]),
            lambda p: p["tune"]["asset"].update(maximumLength=tune.MAX_ASSET + 1024),
            lambda p: p["tune"]["asset"].update(headerPageCount=17000),
            lambda p: p["tune"]["asset"].update(sql="SELECT anything"),
            lambda p: p["tune"]["asset"]["parts"].pop("Engine"),
            lambda p: p["tune"]["asset"]["parts"]["Engine"].update(count=True),
            lambda p: p["tune"].update(rvas=None),
        ]
        for index, mutate in enumerate(mutations):
            with self.subTest(mutation=index):
                pack = current()
                mutate(pack)
                with self.assertRaises(audit.AuditError):
                    self.parse(pack)

    def test_aggregate_guard_and_row_budgets_are_not_per_entry_budgets(self):
        pack = current()
        for index, guard in enumerate(pack["tune"]["codeGuards"]):
            guard.update(rva=4096 + index * 16384, length=8192)
        with self.assertRaises(audit.AuditError):
            self.parse(pack)
        pack = current()
        for part in pack["tune"]["asset"]["parts"].values():
            part["count"] = 250000
        with self.assertRaises(audit.AuditError):
            self.parse(pack)

    def test_store_identity_has_no_executable_hash_substitute(self):
        pack = current(True)
        for mutate in (
            lambda p: p.update(executableSha256="A" * 64),
            lambda p: p["storeIdentity"].update(packageFullName="wrong"),
            lambda p: p["storeIdentity"].update(timeDateStamp=0),
            lambda p: p["storeIdentity"]["codeGuards"][0].update(rva=p["storeIdentity"]["codeGuards"][1]["rva"]),
        ):
            variant = copy.deepcopy(pack)
            mutate(variant)
            with self.assertRaises(audit.AuditError):
                self.parse(variant)

    def test_store_offline_guards_check_timestamp_hash_and_nonwritable_code_without_package_claim(self):
        data, pack = fixture()
        data = bytearray(data)
        struct.pack_into("<I", data, 0x88, 1)
        pack.pop("executableLength")
        pack.pop("executableSha256")
        pack["storeIdentity"] = {"packageFullName": "synthetic", "timeDateStamp": 1,
                                 "codeGuards": [{"rva": 0x2300, "length": 32,
                                                 "sha256": hashlib.sha256(data[0x500:0x520]).hexdigest()}]}
        report = audit.verify_image(audit.PEImage(bytes(data)), pack)
        self.assertTrue(report["offlineChecksPassed"])
        self.assertEqual("not_observable_offline", report["packageProvenance"])
        self.assertTrue(report["reviewOnly"])
        self.assertEqual("not_granted", report["runtimeApproval"])
        pack["storeIdentity"]["timeDateStamp"] = 2
        self.assertFalse(audit.verify_image(audit.PEImage(bytes(data)), pack)["offlineChecksPassed"])
        pack["storeIdentity"]["timeDateStamp"] = 1
        data[0x500] ^= 1
        self.assertFalse(audit.verify_image(audit.PEImage(bytes(data)), pack)["offlineChecksPassed"])
        data[0x500] ^= 1
        struct.pack_into("<I", data, SECTION_TABLE + 40 + 36, audit.READ | audit.WRITE | audit.EXECUTE)
        self.assertFalse(audit.verify_image(audit.PEImage(bytes(data)), pack)["offlineChecksPassed"])

    def test_tune_guard_bytes_and_provider_slots_are_checked_offline(self):
        data, pack = fixture()
        data = bytearray(data)
        struct.pack_into("<Q", data, 0xA00, 0x140002300)
        guard = {"role": "synthetic-range", "rva": 0x2300, "length": 32,
                 "sha256": hashlib.sha256(data[0x500:0x520]).hexdigest()}
        pack["executableSha256"] = hashlib.sha256(data).hexdigest()
        pack["tune"] = {"codeGuards": [guard], "providerSlots": [{"offset": 0, "targetRva": 0x2300}]}
        report = audit.verify_image(audit.PEImage(bytes(data)), pack)
        self.assertTrue(report["offlineChecksPassed"])
        self.assertEqual("not_observable_in_executable_copy", report["tuneAssetProjections"])
        pack["tune"]["providerSlots"][0]["targetRva"] = 0x2310
        self.assertFalse(audit.verify_image(audit.PEImage(bytes(data)), pack)["offlineChecksPassed"])
        pack["tune"]["providerSlots"][0]["targetRva"] = 0x2300
        guard["sha256"] = "A" * 64
        self.assertFalse(audit.verify_image(audit.PEImage(bytes(data)), pack)["offlineChecksPassed"])


if __name__ == "__main__":
    unittest.main()
