#!/usr/bin/env python3
"""Collect local NuGet license metadata/notices alongside a self-contained publish."""
import json
from pathlib import Path
import shutil
import sys
import xml.etree.ElementTree as ET

assets_path, runtimeconfig_path, runtime, destination = sys.argv[1:]
assets = json.loads(Path(assets_path).read_text())
config = json.loads(Path(runtimeconfig_path).read_text())
destination = Path(destination)
folders = [Path(folder) for folder in assets["packageFolders"]]
packages = {name: entry["path"] for name, entry in assets["libraries"].items() if entry["type"] == "package"}
for framework in config["runtimeOptions"]["includedFrameworks"]:
    name = f"{framework['name']}.Runtime.{runtime}"
    version = framework["version"]
    packages[f"{name}/{version}"] = f"{name.lower()}/{version}"

lines = [
    "Rowfinch third-party dependency inventory",
    "",
    "Includes restore/build dependencies; some apply only to other platforms.",
    "License declarations come from the packages. Available license and notice",
    "files are copied under third-party/. Each component retains its license.",
    "",
]
for name, relative in sorted(packages.items(), key=lambda item: item[0].lower()):
    folder = next((base / relative for base in folders if (base / relative).is_dir()), None)
    if folder is None:
        raise SystemExit(f"Package folder missing for notices: {name}")
    nuspec = next(folder.glob("*.nuspec"))
    metadata = ET.parse(nuspec).getroot().find("{*}metadata")
    license_node = metadata.find("{*}license")
    declaration = license_node.text if license_node is not None else metadata.findtext("{*}licenseUrl", "Unspecified")
    lines.extend([name, f"  License: {declaration}"])
    copyright_text = metadata.findtext("{*}copyright")
    if copyright_text:
        lines.append(f"  Copyright: {copyright_text}")
    package_doc = destination / "third-party" / relative
    package_doc.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(nuspec, package_doc / nuspec.name)
    for source in sorted(folder.rglob("*")):
        filename = source.name.lower()
        if not source.is_file() or not filename.startswith(("license", "notice", "third-party", "thirdparty", "copyright", "ofl")):
            continue
        if source.suffix.lower() not in ("", ".txt", ".md", ".rst"):
            continue
        target = package_doc / source.relative_to(folder)
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
        lines.append(f"  Notice: {target.relative_to(destination)}")
    if license_node is not None and license_node.get("type") == "file":
        source = (folder / license_node.text).resolve()
        if not source.is_relative_to(folder.resolve()) or not source.is_file():
            raise SystemExit(f"Declared license file missing: {name}")
        target = package_doc / source.relative_to(folder.resolve())
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
    lines.append("")
(destination / "THIRD-PARTY-NOTICES.txt").write_text("\n".join(lines), encoding="utf-8")
