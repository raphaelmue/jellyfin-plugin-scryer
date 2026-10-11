#!/usr/bin/env python3
"""Package both server ABIs and stage a catalog for a separately signed publication."""
import argparse
import datetime
import hashlib
import json
from pathlib import Path
import re
import subprocess
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def package(version, output):
    if not re.fullmatch(r"\d+\.\d+\.\d+\.0", version):
        raise ValueError("Release version must have four numeric parts and end in .0")
    project = ET.parse(ROOT / "Jellyfin.Plugin.Scryer.csproj").getroot()
    if project.findtext("PropertyGroup/Version") != version:
        raise ValueError("Release version does not match the project")
    metadata = (ROOT / "build.yaml").read_text()
    if f'version: "{version}"' not in metadata:
        raise ValueError("Release version does not match build.yaml")
    changelog = " ".join(metadata.split("changelog: >\n", 1)[1].split())
    catalog = json.loads((ROOT / "manifest.json").read_text())
    plugin = next(item for item in catalog if item["guid"] == "6a9c9f2e-6b2f-4a1a-9d3d-2c7f2f5b3d10")
    variants = [(version[:-1] + "1", "net10.0", "12.0.0.0"), (version, "net9.0", "10.11.0.0")]
    published = {entry["version"] for entry in plugin["versions"]}
    if any(number in published for number, _, _ in variants):
        raise ValueError("Refusing to reuse an already published plugin version")
    timestamp = datetime.datetime.fromtimestamp(
        int(subprocess.check_output(["git", "show", "-s", "--format=%ct", "HEAD"], cwd=ROOT)), datetime.timezone.utc)
    output.mkdir(parents=True, exist_ok=True)
    entries = []
    for number, framework, abi in variants:
        dll = ROOT / "bin" / "Release" / framework / "Jellyfin.Plugin.Scryer.dll"
        data = dll.read_bytes()
        if not data.startswith(b"MZ"):
            raise ValueError(f"Invalid plugin assembly: {dll}")
        archive = output / f"Jellyfin.Plugin.Scryer_{number}.zip"
        info = zipfile.ZipInfo(dll.name, timestamp.timetuple()[:6])
        info.compress_type = zipfile.ZIP_DEFLATED
        info.external_attr = 0o644 << 16
        with zipfile.ZipFile(archive, "w") as zipped:
            zipped.writestr(info, data)
        checksum = hashlib.md5(archive.read_bytes()).hexdigest()
        entries.append(dict(version=number, changelog=changelog, targetAbi=abi,
                            sourceUrl=f"https://github.com/raphaelmue/jellyfin-plugin-scryer/releases/download/v{version}/{archive.name}",
                            checksum=checksum, timestamp=timestamp.strftime("%Y-%m-%dT%H:%M:%SZ")))
        print(f"{number} {abi} {checksum} {archive.name}")
    plugin["versions"] = entries + plugin["versions"]
    (output / "manifest.json").write_text(json.dumps(catalog, indent=2) + "\n")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", required=True)
    parser.add_argument("--output", type=Path, default=ROOT / "dist")
    args = parser.parse_args()
    package(args.version, args.output)
