#!/usr/bin/env python3
"""Packs every packable project at one version and checks the packages before anything is published.

Usage: pack_packages.py <version> <output-directory>     (DOTNET names the dotnet executable; default "dotnet")

Checks:
- every packable project under src/ produced exactly one package, and has a row in README.md;
- every package carries the README and the MIT license expression;
- Encore.Audio, the core, declares no dependencies at all: it stays standalone;
- Encore.Audio.Sdl depends on Encore.Audio at exactly this version and on ppy.SDL3-CS;
- a throwaway consumer of every package at this version restores and builds, with the packages resolvable
  only from the staging directory (and their own dependencies from nuget.org).
"""

from __future__ import annotations

import json
import os
import re
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path
from xml.sax.saxutils import quoteattr

DOTNET = os.environ.get("DOTNET", "dotnet")
ROOT = Path(__file__).resolve().parents[2]


def run(*args: str, cwd: Path = ROOT) -> str:
    print("+", " ".join(args), flush=True)
    result = subprocess.run(args, cwd=cwd, text=True, capture_output=True)
    if result.returncode != 0:
        sys.stdout.write(result.stdout)
        sys.stderr.write(result.stderr)
        sys.exit(f"::error::{' '.join(args[:3])} failed ({result.returncode})")
    return result.stdout


def fail(message: str) -> None:
    sys.exit(f"::error::{message}")


def packable_projects() -> dict[str, Path]:
    files = run("git", "ls-files", "src/*.csproj").split()
    projects = {}
    for file in sorted(files):
        properties = json.loads(run(DOTNET, "msbuild", file, "-getProperty:IsPackable", "-getProperty:PackageId"))["Properties"]
        if properties["IsPackable"].lower() == "true":
            projects[properties["PackageId"]] = ROOT / file
    return projects


def nuspec(package: Path) -> ET.Element:
    with zipfile.ZipFile(package) as archive:
        name = next(n for n in archive.namelist() if n.endswith(".nuspec") and "/" not in n)
        root = ET.fromstring(archive.read(name))
        files = set(archive.namelist())
    metadata = next(child for child in root if child.tag.endswith("metadata"))
    metadata.set("_files", "\n".join(sorted(files)))
    return metadata


def text(metadata: ET.Element, tag: str) -> str | None:
    element = next((e for e in metadata if e.tag.endswith("}" + tag) or e.tag == tag), None)
    return None if element is None else (element.text or "").strip()


def dependencies(metadata: ET.Element) -> dict[str, str]:
    return {e.get("id"): e.get("version") for e in metadata.iter() if e.tag.endswith("dependency")}


def verify_consumer(feed: Path, version: str, ids: list[str]) -> None:
    with tempfile.TemporaryDirectory(prefix="encore-consumer-") as directory:
        work = Path(directory)
        patterns = "\n".join(f"      <package pattern={quoteattr(i)} />" for i in ids)
        (work / "nuget.config").write_text(f"""<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="staged" value={quoteattr(str(feed.resolve()))} />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="staged">
{patterns}
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
""", encoding="utf-8")
        references = "\n".join(f'    <PackageReference Include="{i}" Version="[{version}]" />' for i in ids)
        (work / "Consumer.csproj").write_text(f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net11.0</TargetFramework>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <WarningsAsErrors>$(WarningsAsErrors);NU1603;NU1605</WarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
{references}
  </ItemGroup>
</Project>
""", encoding="utf-8")
        (work / "Program.cs").write_text("""using Encore.Audio;
using Encore.Audio.Music;
using Encore.Audio.Sdl;

// Compiles against both packages' public surface; nothing opens a device.
var song = new Song([new Instrument()], [new Note(0, 0.1, 60, 1, 0)], 0.5);
var player = new MusicPlayer(song);
player.Play(loop: false);
var buffer = new float[2 * 1024];
player.Render(buffer);
System.Console.WriteLine($"{typeof(AudioMixer).Assembly.GetName().Name} {typeof(SdlAudioOutput).Assembly.GetName().Name} {buffer.Length}");
""", encoding="utf-8")
        run(DOTNET, "build", "-c", "Release", "--nologo", cwd=work)
        print(run(DOTNET, "run", "-c", "Release", "--no-build", cwd=work).strip())


def main() -> None:
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    version, output = sys.argv[1], Path(sys.argv[2])
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+([.-][0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?", version):
        fail(f"'{version}' is not a package version like 1.2.3 or 1.2.3-beta.1")
    output.mkdir(parents=True, exist_ok=True)

    projects = packable_projects()
    print(f"Packing {len(projects)} projects: {', '.join(projects)}")
    readme = (ROOT / "README.md").read_text(encoding="utf-8")
    for package_id in projects:
        if f"[{package_id}](" not in readme:
            fail(f"{package_id} is packable but has no row in README.md")

    for project in projects.values():
        run(DOTNET, "pack", str(project), "-c", os.environ.get("CONFIGURATION", "Release"), "-o", str(output),
            "-p:ContinuousIntegrationBuild=true", f"-p:Version={version}", "--nologo")

    packages = {}
    for package_id in projects:
        path = output / f"{package_id}.{version}.nupkg"
        if not path.exists():
            fail(f"{package_id} produced no {path.name}")
        packages[package_id] = nuspec(path)
    found = sorted(p.name for p in output.glob(f"*.{version}.nupkg"))
    if len(found) != len(projects):
        fail(f"Expected {len(projects)} packages, found {found}")

    for package_id, metadata in packages.items():
        files = metadata.get("_files", "").split("\n")
        if text(metadata, "license") != "MIT":
            fail(f"{package_id} does not declare the MIT license")
        if text(metadata, "readme") != "README.md" or "README.md" not in files:
            fail(f"{package_id} does not carry README.md")
        if not any(f.startswith("lib/net11.0/") and f.endswith(".dll") for f in files):
            fail(f"{package_id} has no lib/net11.0 assembly")

    core = dependencies(packages["Encore.Audio"])
    if core:
        fail(f"Encore.Audio must stay standalone but depends on {core}")
    sdl = dependencies(packages["Encore.Audio.Sdl"])
    if sdl.get("Encore.Audio") not in (version, f"[{version}]", f"[{version}, )"):
        fail(f"Encore.Audio.Sdl must depend on Encore.Audio {version}, not {sdl.get('Encore.Audio')}")
    if "ppy.SDL3-CS" not in sdl or set(sdl) != {"Encore.Audio", "ppy.SDL3-CS"}:
        fail(f"Encore.Audio.Sdl must depend on Encore.Audio and ppy.SDL3-CS only, not {sdl}")
    for package_id, metadata in packages.items():
        print(f"{package_id} {version}: dependencies {dependencies(metadata) or 'none'}")

    verify_consumer(output, version, list(projects))
    print("All packages verified.")


if __name__ == "__main__":
    main()
