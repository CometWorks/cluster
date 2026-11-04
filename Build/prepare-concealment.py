#!/usr/bin/env python3
"""Build the pinned Concealment plugin once; both cluster roles load that plugin.

Use --source PATH for an existing repository (git archive of the pinned commit,
never the working tree), otherwise download the exact GitHub source archive.
No plugin installation, publication or deployment is performed.
"""
import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import tarfile
import urllib.request
import xml.etree.ElementTree as ET


REPO = Path(__file__).resolve().parent.parent


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path)
    parser.add_argument("--output", type=Path, default=REPO / "Dependencies/Concealment")
    parser.add_argument("--source-only", action="store_true",
                        help="Verify/extract the pin for SDK-only model checks; do not build the native plugin")
    args = parser.parse_args()
    pin = json.loads((REPO / "Dependencies/Concealment.lock.json").read_text())
    commit = pin["commit"]
    if len(commit) != 40 or any(c not in "0123456789abcdef" for c in commit):
        raise ValueError("Concealment must be pinned to a full source commit")
    if args.source:
        data = subprocess.check_output(["git", "-C", str(args.source), "archive", "--format=tar", commit])
    else:
        with urllib.request.urlopen(f"https://api.github.com/repos/{pin['repository']}/tarball/{commit}", timeout=60) as response:
            data = response.read(64 * 1024 * 1024 + 1)
        if len(data) > 64 * 1024 * 1024:
            raise ValueError("Oversized Concealment source archive")
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    source = output / "Source"
    if source.exists():
        shutil.rmtree(source)
    source.mkdir()
    with tarfile.open(fileobj=io.BytesIO(data), mode="r:*") as archive:
        members = archive.getmembers()
        # GitHub adds a root directory; git archive does not.
        prefix = members[0].name.split("/")[0] + "/" if not args.source else ""
        for member in members:
            if prefix:
                if member.name.rstrip("/") == prefix.rstrip("/"):
                    continue
                if not member.name.startswith(prefix):
                    raise ValueError("Invalid source archive root")
                member.name = member.name[len(prefix):]
            archive.extract(member, source, filter="data")
    # Verify unpacked source content, independent of GitHub's tar/gzip encoding.
    files = {str(p.relative_to(source)): hashlib.sha256(p.read_bytes()).hexdigest()
             for p in sorted(source.rglob("*")) if p.is_file()}
    digest = hashlib.sha256(json.dumps(files, sort_keys=True, separators=(",", ":")).encode()).hexdigest()
    if digest != pin["sourceSha256"]:
        raise ValueError("Concealment source does not match the pinned content")
    if args.source_only:
        print(f"Verified Concealment source {commit}: {digest}")
        return
    command = ["dotnet", "build", str(source / "ServerPlugin/ServerPlugin.csproj"), "-c", "Release",
               "--nologo", "-v:q", "-p:RunPostBuildEvent=Never",
               "-p:EnableSourceControlManagerQueries=false", f"-p:SourceRevisionId={commit}"]
    for env, prop in (("DEDICATED64", "Dedicated64"), ("MAGNETAR_SDK", "Magnetar")):
        if os.environ.get(env):
            command.append(f"-p:{prop}={os.environ[env]}")
    subprocess.run(command, check=True)
    built = source / "ServerPlugin/bin/Release/net10.0"
    shutil.copy2(built / "Concealment.dll", output)
    # Concealment is not a profile plugin, so Quasar exports no configuration for it and a managed
    # node refuses to start without one (SE-1078). Ship its defaults, serialized by the PluginSdk
    # it was built against.
    sdk, game = (subprocess.check_output(["dotnet", "msbuild", command[2], *command[7:], f"-getProperty:{name}"],
                                         text=True).strip() for name in ("MagnetarBinDir", "Dedicated64"))
    subprocess.run(["dotnet", "run", "--project", str(REPO / "Tools/PluginConfigDefaults"), "-c", "Release", "--",
                    str(Path(sdk) / "PluginSdk.dll"), str(output / "Concealment.dll"), "Shared.Config.PluginConfig",
                    str(output / "canonical-configuration.json"), game], check=True)
    manifest = ET.parse(source / "Concealment.xml")
    manifest.getroot().find("Commit").text = commit
    manifest.write(output / "Concealment.xml", encoding="utf-8", xml_declaration=True)
    for directory in ("", "ServerPlugin/Accounting"):
        destination = output / "licenses" / ("accounting" if directory else "concealment")
        destination.mkdir(parents=True, exist_ok=True)
        for name in ("LICENSE", "NOTICE"):
            shutil.copy2(source / directory / name, destination / name)
    provenance = dict(pin, dllSha256=hashlib.sha256((output / "Concealment.dll").read_bytes()).hexdigest())
    (output / "provenance.json").write_text(json.dumps(provenance, indent=2) + "\n")
    print(f"Prepared Concealment contract {pin['contract']} from {commit}: {provenance['dllSha256']}")


if __name__ == "__main__":
    main()
