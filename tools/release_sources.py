"""Supply the pinned library sources and verify the complete public release bundle."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import tempfile
import time
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
import zipfile


ROOT = Path(__file__).resolve().parents[1]
SHA256 = re.compile(r"[a-f0-9]{64}")
ALLOWED_HOSTS = {"github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com"}


def check_path(path, *, directory=False):
    for current in (path, *path.parents):
        if not current.exists() and not current.is_symlink():
            continue
        info = current.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise ValueError("Release paths must not traverse reparse points or links.")
    if path.exists() and (not path.is_dir() if directory else not path.is_file()):
        raise ValueError("Release artifact path has the wrong file type.")


def digest(path):
    check_path(path)
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def read_pins(root):
    versions = ET.parse(root / "src/Wisp.App/Wisp.App.csproj").findall("./PropertyGroup/Version")
    if len(versions) != 1 or not re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)", versions[0].text or ""):
        raise ValueError("A single canonical release version is required.")
    version = versions[0].text
    distribution = json.loads((root / "LICENSES/library-sources-distribution.json").read_text(encoding="utf-8"))
    manifest = json.loads((root / "LICENSES/libmpv-source-manifest.json").read_text(encoding="utf-8"))
    archive = distribution["archive"]
    filename = f"Wisp-{version}-library-sources.zip"
    closure = manifest["sourceClosure"]
    companion = manifest["sourceCompanion"]
    expected_url = f"https://github.com/Views2k/Wisp/releases/download/library-sources-{version}/{filename}"
    if (distribution["schemaVersion"] != 1 or distribution["version"] != version or
            archive["filename"] != filename or archive["url"] != expected_url or
            type(archive["bytes"]) is not int or not 0 < archive["bytes"] <= 2 * 1024**3 or
            not SHA256.fullmatch(archive["sha256"]) or manifest["sourceStatus"] != "complete" or
            closure["status"] != "complete" or closure["distributionReady"] is not True or
            archive["bytes"] != companion["bytes"] or archive["sha256"] != companion["sha256"] or
            not SHA256.fullmatch(companion["enclosedManifestSha256"])):
        raise ValueError("The release source archive must match the completed decoder source manifest.")
    return version, archive, manifest


def verify_archive(path, archive, manifest):
    check_path(path)
    if path.stat().st_size != archive["bytes"] or digest(path) != archive["sha256"]:
        raise ValueError("The source companion differs from its pinned size or SHA-256.")
    with zipfile.ZipFile(path) as bundle:
        entries = bundle.infolist()
        if not 0 < len(entries) <= 1024:
            raise ValueError("The source companion has an invalid entry count.")
        names = set()
        total = 0
        for entry in entries:
            name = entry.filename
            pieces = name.rstrip("/").split("/")
            if (not name or "\\" in name or ":" in name or
                    any(part in ("", ".", "..") for part in pieces) or
                    name.casefold() in names or stat.S_ISLNK(entry.external_attr >> 16)):
                raise ValueError("The source companion contains an unsafe or duplicate entry.")
            names.add(name.casefold())
            total += entry.file_size
            if total > 4 * 1024**3:
                raise ValueError("The source companion exceeds its expansion limit.")
        enclosed = bundle.getinfo("source-manifest.json")
        if enclosed.file_size > 2 * 1024**2:
            raise ValueError("The enclosed source manifest exceeds its size limit.")
        data = bundle.read(enclosed)
        if hashlib.sha256(data).hexdigest() != manifest["sourceCompanion"]["enclosedManifestSha256"]:
            raise ValueError("The enclosed source manifest does not match its reviewed identity.")
        source = json.loads(data)
        for field in ("nativeFiles", "noticeFiles", "sourceArchives", "libvlcSourceCompanion"):
            if source[field] != manifest[field]:
                raise ValueError("The supplied library sources differ from the packaged decoder manifest.")


def check_url(url):
    parsed = urllib.parse.urlsplit(url)
    if (parsed.scheme != "https" or parsed.hostname not in ALLOWED_HOSTS or
            parsed.username is not None or parsed.password is not None or parsed.port not in (None, 443)):
        raise ValueError("The source download destination is not an allowed HTTPS endpoint.")


class SafeRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, newurl):
        check_url(newurl)
        return super().redirect_request(request, response, code, message, headers, newurl)


def download(archive, destination):
    check_url(archive["url"])
    opener = urllib.request.build_opener(SafeRedirect())
    deadline = time.monotonic() + 600
    with opener.open(archive["url"], timeout=30) as response, destination.open("wb") as output:
        length = response.headers.get("Content-Length")
        if length is not None and int(length) != archive["bytes"]:
            raise ValueError("The source response size does not match the pinned archive.")
        received = 0
        while block := response.read(1024**2):
            received += len(block)
            if received > archive["bytes"] or time.monotonic() > deadline:
                raise ValueError("The source download exceeded its size or time limit.")
            output.write(block)
        if received != archive["bytes"]:
            raise ValueError("The source download is incomplete.")


def prepare(root, directory, local_archive=None):
    _, archive, manifest = read_pins(root)
    check_path(directory, directory=True)
    directory.mkdir(parents=True, exist_ok=True)
    target = directory / archive["filename"]
    checksum = directory / (archive["filename"] + ".sha256")
    check_path(target)
    check_path(checksum)
    if target.exists():
        verify_archive(target, archive, manifest)
    else:
        descriptor, name = tempfile.mkstemp(prefix=".library-sources-", dir=directory)
        os.close(descriptor)
        temporary = Path(name)
        try:
            if local_archive is None:
                download(archive, temporary)
            else:
                verify_archive(local_archive, archive, manifest)
                with local_archive.open("rb") as source, temporary.open("wb") as output:
                    while block := source.read(1024**2):
                        output.write(block)
            verify_archive(temporary, archive, manifest)
            # An existing release file is never overwritten.
            with temporary.open("rb") as source, target.open("xb") as output:
                while block := source.read(1024**2):
                    output.write(block)
        finally:
            temporary.unlink(missing_ok=True)
    expected = f"{archive['sha256']} *{archive['filename']}\n"
    if checksum.exists():
        if checksum.read_text(encoding="utf-8") != expected:
            raise ValueError("The existing source checksum differs from the pinned archive.")
    else:
        with checksum.open("x", encoding="utf-8", newline="\n") as output:
            output.write(expected)


def verify_bundle(root, directory):
    version, archive, manifest = read_pins(root)
    check_path(directory, directory=True)
    filenames = (f"Wisp-Setup-{version}.exe", f"Wisp-Setup-{version}.zip", archive["filename"])
    expected = set(filenames) | {name + ".sha256" for name in filenames}
    if {entry.name for entry in directory.iterdir()} != expected:
        raise ValueError("The release bundle must contain exactly the EXE, installer ZIP, source ZIP and three checksums.")
    for name in filenames:
        artifact = directory / name
        checksum = directory / (name + ".sha256")
        check_path(artifact)
        check_path(checksum)
        if artifact.stat().st_size <= 0 or checksum.stat().st_size > 256:
            raise ValueError("A release artifact or checksum has an invalid size.")
        contents = checksum.read_text(encoding="utf-8")
        match = re.fullmatch(r"([a-fA-F0-9]{64}) \*" + re.escape(name) + r"\n?", contents)
        if match is None or digest(artifact) != match[1].lower():
            raise ValueError("A release artifact does not match its checksum.")
    verify_archive(directory / archive["filename"], archive, manifest)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("prepare", "verify"))
    parser.add_argument("--directory", type=Path, default=ROOT / "outputs")
    parser.add_argument("--archive", type=Path)
    args = parser.parse_args()
    try:
        if args.action == "prepare":
            prepare(ROOT, args.directory.absolute(), args.archive.absolute() if args.archive else None)
        else:
            if args.archive is not None:
                raise ValueError("An archive override is valid only for preparation.")
            verify_bundle(ROOT, args.directory.absolute())
    except (OSError, ValueError, KeyError, TypeError, zipfile.BadZipFile):
        parser.exit(1, "Release source validation failed; no release may be published.\n")
    print("Pinned source companion verified." if args.action == "prepare" else "Complete release bundle verified.")


if __name__ == "__main__":
    main()
