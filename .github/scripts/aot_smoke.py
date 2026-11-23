#!/usr/bin/env python3
"""Publishes the NativeAOT smoke test, fails on any trim/AOT warning, and runs the native executable.

Usage: aot_smoke.py [runtime-identifier]      (default: this machine's, e.g. linux-x64)
DOTNET names the dotnet executable (default "dotnet"). The SDL output is checked when SDL_AUDIO_DRIVER is set;
CI sets it to "dummy", SDL's driver that needs no sound device.
"""

from __future__ import annotations

import os
import platform
import re
import subprocess
import sys
from pathlib import Path

DOTNET = os.environ.get("DOTNET", "dotnet")
ROOT = Path(__file__).resolve().parents[2]
PROJECT = ROOT / "tests" / "Encore.Audio.AotSmoke" / "Encore.Audio.AotSmoke.csproj"


def default_rid() -> str:
    system = {"Linux": "linux", "Darwin": "osx", "Windows": "win"}[platform.system()]
    machine = platform.machine().lower()
    arch = "arm64" if machine in ("arm64", "aarch64") else "x64"
    return f"{system}-{arch}"


def main() -> None:
    rid = sys.argv[1] if len(sys.argv) > 1 else default_rid()
    output = ROOT / "artifacts" / "aot" / rid
    publish = subprocess.run(
        [DOTNET, "publish", str(PROJECT), "-c", "Release", "-r", rid, "-o", str(output), "--nologo"],
        cwd=ROOT, text=True, capture_output=True)
    log = publish.stdout + publish.stderr
    print(log)
    if publish.returncode != 0:
        sys.exit(f"::error::NativeAOT publish failed ({publish.returncode})")
    # Trim (IL2xxx) and AOT (IL3xxx) analysis warnings mean code that may break once compiled natively.
    warnings = sorted(set(re.findall(r"warning (IL[23]\d{3}[^\r\n]*)", log)))
    if warnings:
        for warning in warnings:
            print(f"::error::{warning}")
        sys.exit(f"::error::NativeAOT publish reported {len(warnings)} trim/AOT warning(s)")

    executable = output / ("Encore.Audio.AotSmoke.exe" if rid.startswith("win") else "Encore.Audio.AotSmoke")
    if not executable.exists():
        sys.exit(f"::error::{executable} was not produced")
    size = executable.stat().st_size / 1e6
    print(f"Native executable: {executable.name}, {size:.1f} MB")
    run = subprocess.run([str(executable)], cwd=output, text=True, capture_output=True, timeout=120)
    print(run.stdout + run.stderr)
    if run.returncode != 0:
        sys.exit(f"::error::The NativeAOT smoke test failed ({run.returncode})")


if __name__ == "__main__":
    main()
