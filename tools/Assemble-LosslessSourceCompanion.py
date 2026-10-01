"""Fetch pinned library sources and copy notices/recipes; never run source code."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import shutil
import struct
import tarfile
import time
import urllib.error
import urllib.parse
import urllib.request
import zipfile


REPO = Path(__file__).resolve().parent.parent
MANIFEST = REPO / "LICENSES/libvlc-3.0.24-source-manifest.json"
OUTPUT = REPO / "work/lossless-source-companion"
MAX_ARCHIVE = 64 * 1024 * 1024
MAX_TOTAL = 320 * 1024 * 1024
MAX_NOTICES = 16 * 1024 * 1024
COMMIT = "4896d0e06d19ac40b46802cf7dc167d432f3fd63"


def owned(path: Path) -> Path:
    path = path.absolute()
    if not path.is_relative_to(OUTPUT) or path == OUTPUT:
        raise ValueError("output_path_outside_companion")
    for parent in [path, *path.parents]:
        if parent.is_symlink() or getattr(parent, "is_junction", lambda: False)():
            raise ValueError("reparse_output_refused")
        if parent == REPO:
            break
    return path


def safe_url(url: str) -> str:
    parsed = urllib.parse.urlsplit(url)
    host = parsed.hostname or ""
    exact = {"github.com", "codeload.github.com", "release-assets.githubusercontent.com",
             "objects.githubusercontent.com", "raw.githubusercontent.com", "ffmpeg.org",
             "ftp.gnu.org", "www.gnupg.org", "downloads.xiph.org", "ftp.osuosl.org",
             "downloads.sourceforge.net", "sourceforge.net", "download.gnome.org"}
    if parsed.scheme != "https" or parsed.username or parsed.password or parsed.port not in (None, 443):
        raise ValueError("https_source_required")
    if host not in exact and not host.endswith((".videolan.org", ".dl.sourceforge.net")):
        raise ValueError("source_redirect_host_not_allowed")
    return url


class Redirects(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        safe_url(newurl)
        return super().redirect_request(req, fp, code, msg, headers, newurl)


def digest(path: Path, algorithm: str) -> str:
    value = hashlib.new(algorithm)
    with path.open("rb") as stream:
        while block := stream.read(1024 * 1024):
            value.update(block)
    return value.hexdigest()


def download(urls: list[str], destination: Path, algorithm: str, expected: str | None) -> dict:
    destination = owned(destination)
    if destination.exists():
        if not expected or digest(destination, algorithm) != expected:
            raise ValueError("existing_archive_requires_verified_identity")
        return {"downloaded": False, "url": urls[0], "bytes": destination.stat().st_size,
                "sha256": digest(destination, "sha256"), "verified": True}
    errors = []
    for url in urls[:2]:
        partial = owned(destination.with_name(destination.name + ".partial"))
        if partial.exists():
            raise ValueError("existing_partial_archive_refused")
        try:
            started = time.monotonic()
            request = urllib.request.Request(safe_url(url), headers={"User-Agent": "Wisp-source-companion/1"})
            with urllib.request.build_opener(Redirects()).open(request, timeout=10) as response, partial.open("xb") as stream:
                safe_url(response.url)
                length = response.headers.get("Content-Length")
                if length and int(length) > MAX_ARCHIVE:
                    raise ValueError("archive_declared_size_exceeded")
                count = 0
                while block := response.read(256 * 1024):
                    count += len(block)
                    if count > MAX_ARCHIVE or time.monotonic() - started > 45:
                        raise ValueError("archive_size_or_time_exceeded")
                    stream.write(block)
            actual = digest(partial, algorithm)
            if expected and actual != expected:
                raise ValueError("archive_checksum_mismatch")
            partial.rename(destination)
            return {"downloaded": True, "url": url, "bytes": count,
                    "sha256": digest(destination, "sha256"), "verified": expected is not None}
        except (OSError, ValueError, urllib.error.URLError) as error:
            if partial.exists():
                owned(partial).unlink()
            errors.append(type(error).__name__ + (":" + str(error.code) if isinstance(error, urllib.error.HTTPError) else ""))
            # A hash mismatch is evidence, not a reason to try another source.
            if isinstance(error, ValueError) and str(error) == "archive_checksum_mismatch":
                break
    raise RuntimeError("archive_fetch_failed:" + ",".join(errors))


def members(archive: tarfile.TarFile):
    for count, item in enumerate(archive):
        if count >= 80000:
            raise ValueError("archive_member_count_exceeded")
        path = PurePosixPath(item.name)
        if path.is_absolute() or ".." in path.parts or "\\" in item.name or ":" in item.name:
            raise ValueError("unsafe_archive_member")
        yield item, path


def write_owned(path: Path, data: bytes):
    target = owned(path)
    target.parent.mkdir(parents=True, exist_ok=True)
    if target.exists():
        if target.read_bytes() != data:
            raise ValueError("existing_companion_file_differs")
    else:
        target.write_bytes(data)


def extract_notices(path: Path, component: str) -> list[dict]:
    result = []
    total = 0
    with tarfile.open(path, "r:*") as archive:
        for item, member in members(archive):
            leaf = member.name.upper()
            # Top-level original notices; full nested notices remain in the archive.
            if not item.isfile() or len(member.parts) > 3 or not re.match(r"^(COPYING|LICENSE|LICENCE|NOTICE|COPYRIGHT|AUTHORS)([.\-_]|$)", leaf):
                continue
            if item.size > 2 * 1024 * 1024 or total + item.size > MAX_NOTICES:
                raise ValueError("notice_size_exceeded")
            data = archive.extractfile(item).read(item.size + 1)
            if len(data) != item.size:
                raise ValueError("notice_read_incomplete")
            relative = Path(component, *member.parts[1:])
            write_owned(OUTPUT / "notices" / relative, data)
            result.append({"path": relative.as_posix(), "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()})
            total += item.size
    return result


def preserve_build_materials(vlc: Path, components: set[str]) -> list[dict]:
    exact = {"contrib/bootstrap", "contrib/src/main.mak", "extras/package/win32/configure.sh",
             "extras/package/win32/package.mak", "configure", "Makefile.in"}
    result = []
    total = 0
    with tarfile.open(vlc, "r:*") as archive:
        for item, member in members(archive):
            relative = PurePosixPath(*member.parts[1:])
            selected = str(relative) in exact or (len(relative.parts) > 3 and relative.parts[:2] == ("contrib", "src") and relative.parts[2] in components)
            if not item.isfile() or not selected:
                continue
            if item.size > 4 * 1024 * 1024 or total + item.size > 24 * 1024 * 1024:
                raise ValueError("build_material_size_exceeded")
            data = archive.extractfile(item).read(item.size + 1)
            if len(data) != item.size:
                raise ValueError("build_material_read_incomplete")
            write_owned(OUTPUT / "build-materials" / str(relative), data)
            result.append({"path": str(relative), "sha256": hashlib.sha256(data).hexdigest()})
            total += item.size
    return result


def public_pe_constant(binary: bytes, export_name: bytes) -> str:
    """Read only a direct x64 constant getter; never map or execute a library."""
    def u32(at): return struct.unpack_from("<I", binary, at)[0]
    pe = u32(0x3C)
    if binary[pe:pe + 4] != b"PE\0\0" or struct.unpack_from("<H", binary, pe + 4)[0] != 0x8664:
        raise ValueError("native_metadata_not_x64_pe")
    optional = pe + 24
    sections = optional + struct.unpack_from("<H", binary, pe + 20)[0]
    count = struct.unpack_from("<H", binary, pe + 6)[0]
    if not 0 < count <= 96 or struct.unpack_from("<H", binary, optional)[0] != 0x20B:
        raise ValueError("native_metadata_pe_shape")
    def offset(rva):
        for index in range(count):
            at = sections + index * 40
            start, size, raw = u32(at + 12), u32(at + 16), u32(at + 20)
            if start <= rva < start + size:
                result = raw + rva - start
                if result >= len(binary): break
                return result
        raise ValueError("native_metadata_rva_invalid")
    def string(rva):
        at = offset(rva); end = binary.find(b"\0", at, min(at + 256, len(binary)))
        if end < 0: raise ValueError("native_metadata_constant_unbounded")
        return binary[at:end]
    directory = offset(u32(optional + 112))
    names, functions, ordinals = offset(u32(directory + 32)), offset(u32(directory + 28)), offset(u32(directory + 36))
    total = u32(directory + 24)
    if total > 10000: raise ValueError("native_metadata_export_count")
    for index in range(total):
        if string(u32(names + 4 * index)) != export_name: continue
        ordinal = struct.unpack_from("<H", binary, ordinals + 2 * index)[0]
        rva = u32(functions + 4 * ordinal); at = offset(rva)
        if binary[at:at + 3] != b"\x48\x8d\x05":
            raise ValueError("native_metadata_getter_not_direct")
        displacement = struct.unpack_from("<i", binary, at + 3)[0]
        return string(rva + 7 + displacement).decode("ascii")
    raise ValueError("native_metadata_export_missing")


def provenance(manifest: dict, vlc: Path) -> dict:
    package = REPO / "work/lossless-dependencies/videolan.libvlc.windows.3.0.24.nupkg"
    if digest(package, "sha256") != manifest["packages"][1]["sha256"]:
        raise ValueError("native_metadata_package_hash_mismatch")
    with zipfile.ZipFile(package) as archive:
        library = archive.read("build/x64/libvlc.dll")
        core = archive.read("build/x64/libvlccore.dll")
    version = public_pe_constant(library, b"libvlc_get_version")
    revision = public_pe_constant(library, b"libvlc_get_changeset")
    compiler = public_pe_constant(core, b"VLC_Compiler")
    if version != "3.0.24 Vetinari" or revision != "3.0.24-rc1-0-g6de05adcba" or compiler != "gcc version 6.4.0 (GCC)":
        raise ValueError("native_public_metadata_mismatch")
    with tarfile.open(vlc) as archive:
        source_revision = archive.extractfile("vlc-3.0.24/src/revision.txt").read(256).decode().strip()
    if source_revision != revision:
        raise ValueError("native_source_revision_mismatch")
    candidates = [value for value in re.findall(rb"[ -~]{20,}", core) if b"--enable-merge-ffmpeg" in value]
    if len(candidates) != 1: raise ValueError("native_configure_metadata_ambiguous")
    switches = sorted(set(value.decode() for value in re.findall(rb"--(?:enable|disable)-[a-zA-Z0-9_-]+", candidates[0])))
    report = {"method": "Static direct PE constant getters and allowlisted configure switches; no DLL loaded or executed.",
              "version": version, "binaryChangeset": revision, "sourceChangeset": source_revision,
              "sourceRevisionMatches": True, "compiler": compiler, "target": "x86_64-w64-mingw32",
              "configureFeatureSwitches": switches, "dependencyConfigureLinesVerified": False,
              "reproducibleBuildExecuted": False,
              "limits": ["Feature switches omit machine paths, environment values and service URLs.",
                         "The NuGet package does not carry a native dependency build manifest; preserved release recipes and sources are not a reproduced build."]}
    owned(OUTPUT / "native-build-provenance.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    return report


def main():
    started = time.monotonic()
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    owned(OUTPUT / "archives").mkdir(parents=True, exist_ok=True)
    report = {"schemaVersion": 1, "complete": False, "executedDownloadedCode": False, "archives": [], "failures": [], "notices": []}
    total = 0
    jobs = [(item["component"], item["archive"], item["url"], "sha512", item["sha512"], item) for item in manifest["upstreamSources"]]
    sharp = manifest["packages"][0]
    jobs.append(("libvlcsharp", f"libvlcsharp-{COMMIT}.tar.gz", sharp["sourceArchiveUrl"], "sha256", sharp["sourceArchiveSha256"], sharp))
    for component, name, url, algorithm, expected, entry in jobs:
        if time.monotonic() - started > 600:
            report["failures"].append({"component": component, "reason": "assembly_time_limit"}); break
        try:
            mirrors = {"mingw64": "winpthreads"}
            urls = [url, f"https://download.videolan.org/pub/contrib/{mirrors.get(component, component)}/{name}"] if component != "libvlcsharp" else [url]
            target = OUTPUT / "archives" / name
            fetched = download(urls, target, algorithm, expected)
            total += fetched["bytes"]
            if total > MAX_TOTAL:
                raise ValueError("companion_archive_total_exceeded")
            if component == "libvlcsharp":
                with tarfile.open(target) as archive:
                    roots = {path.parts[0] for _, path in members(archive)}
                    if roots != {"libvlcsharp-" + COMMIT}:
                        raise ValueError("managed_source_commit_root_mismatch")
                sharp["sourceArchiveSha256"] = fetched["sha256"]
                sharp["sourceArchiveVerification"] = "Fetched over HTTPS from the pinned official repository commit; exact archive root checked; SHA256 recorded."
            else:
                entry["fetchedAndVerified"] = True
                entry["archiveSha256"] = fetched["sha256"]
            report["notices"] += extract_notices(target, component)
            report["archives"].append({"component": component, "archive": name, **fetched})
            print(json.dumps({"component": component, "sourceVerified": True, "bytes": fetched["bytes"]}), flush=True)
        except (OSError, ValueError, RuntimeError, tarfile.TarError) as error:
            report["failures"].append({"component": component, "reason": str(error) if str(error).startswith(("archive_", "companion_", "managed_", "notice_", "unsafe_", "existing_")) else type(error).__name__})
            print(json.dumps({"component": component, "sourceVerified": False, "reason": type(error).__name__}), flush=True)
        MANIFEST.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
        (OUTPUT / "assembly-report.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")

    vlc = REPO / "work/lossless-dependencies/vlc-3.0.24.tar.xz"
    if digest(vlc, "sha256") != manifest["packages"][1]["sourceArchiveSha256"]:
        raise ValueError("vlc_source_identity_mismatch")
    copied = owned(OUTPUT / "archives" / vlc.name)
    if copied.exists():
        if digest(copied, "sha256") != digest(vlc, "sha256"):
            raise ValueError("existing_vlc_source_differs")
    else:
        shutil.copyfile(vlc, copied)
    report["archives"].append({"component": "vlc", "archive": vlc.name, "bytes": vlc.stat().st_size, "sha256": digest(vlc, "sha256"), "verified": True})
    report["notices"] += extract_notices(vlc, "vlc")
    report["buildMaterials"] = preserve_build_materials(vlc, {item["component"] for item in manifest["upstreamSources"]} | {"d3d11", "mingw12-fixes"})
    report["nativeBuildProvenance"] = provenance(manifest, vlc)
    header = REPO / "tools/Wisp.RecorderPrototype/LosslessProbe/nvEncodeAPI.h"
    expected_header = "4fe4094541ef0f8a13249d97a8692dc5f835a6e9dd42eeadb3e2f7321d54dc7e"
    if digest(header, "sha256") != expected_header:
        raise ValueError("nvenc_header_identity_mismatch")
    write_owned(OUTPUT / "headers/nvEncodeAPI.h", header.read_bytes())
    write_owned(OUTPUT / "notices/nvidia/NVIDIA-nvEncodeAPI-MIT.txt", (REPO / "LICENSES/NVIDIA-nvEncodeAPI-MIT.txt").read_bytes())
    manifest["nvidiaHeader"] = {"version": "API 13.0 / mirror tag n13.0.19.0", "license": "MIT",
        "sourceUrl": "https://raw.githubusercontent.com/FFmpeg/nv-codec-headers/e844e5b26f46bb77479f063029595293aa8f812d/include/ffnvcodec/nvEncodeAPI.h",
        "sha256": expected_header, "driverBundled": False}
    manifest["nativeBuildProvenance"] = report["nativeBuildProvenance"]
    report["complete"] = not report["failures"] and len(report["archives"]) == 16
    report["nativeBuildConfigurationVerified"] = False
    report["publicationReady"] = False
    report["remaining"] = ["Verify exact official native build configuration and conservative dependency closure.", "Host source companion with distribution and record immutable companion URL/hash.", "Verify minimized runtime plugin bundle including AAC output."]
    manifest["sourceCompanionStatus"] = "archives-assembled-source-revision-matched-dependency-build-config-unverified" if report["complete"] else "archive-assembly-incomplete"
    manifest["noticeFiles"] = report["notices"]
    MANIFEST.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    (OUTPUT / "assembly-report.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    owned(OUTPUT / "source-manifest.json").write_bytes(MANIFEST.read_bytes())
    owned(OUTPUT / "Assemble-LosslessSourceCompanion.py").write_bytes(Path(__file__).read_bytes())
    owned(OUTPUT / "README.txt").write_text(
        "Wisp lossless playback source companion\n\n"
        "archives/ preserves all 16 original source archives. The manifest records the two exact NuGet package identities, "
        "native DLL selection, recipe SHA512 hashes, and downloaded archive SHA256 hashes.\n"
        "notices/ preserves original upstream license/copyright files; full nested notices remain in each source archive. "
        "Build-tool and optional-component license texts do not change Wisp's proprietary license.\n"
        "build-materials/ preserves the verified VLC release's dependency recipes, patches, checksums, and Windows wrapper. "
        "It is a convenience subset; use the complete VLC source archive for a rebuild.\n"
        "headers/nvEncodeAPI.h is the exact MIT-licensed NVIDIA header used by Wisp. No NVIDIA driver is included.\n\n"
        "Native provenance: source and binary both report 3.0.24-rc1-0-g6de05adcba. The binary reports GCC 6.4.0; "
        "native-build-provenance.json records the public getters and selected configure switches, read statically without loading DLLs. "
        "The NuGet package omits its native dependency build manifest. This companion preserves the release recipes and "
        "their checksum-pinned upstream inputs, including conservative conditional dependencies; it does not claim a reproduced binary build.\n\n"
        "The assembly script runs from Wisp's checkout, downloads only allowlisted HTTPS sources with bounded size/time, "
        "checks the recipe hashes, and copies notices/recipes without executing downloaded code. It is not a native library build script.\n"
        "Distribution must provide this companion alongside the matching Wisp binary and preserve the included third-party notices. "
        "No source bundle has been published by this assembly step.\n", encoding="utf-8")
    print(json.dumps({"complete": report["complete"], "archives": len(report["archives"]), "noticeFiles": len(report["notices"]), "buildFiles": len(report["buildMaterials"]), "publicationReady": False}), flush=True)
    return 0 if report["complete"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
