#!/usr/bin/env python3
"""Check the exact admin facade without requiring native live partitions.

The partition manager and core dispatch are inert; native routes are tested separately.
Usage: run.py NEW_OUTPUT [OfflineCompensation.cs]
"""
from pathlib import Path
import hashlib
import json
import subprocess
import sys

root = Path(__file__).resolve().parents[2]
source = Path(sys.argv[2]) if len(sys.argv) > 2 else root / 'Shared/Cluster/Offline/OfflineCompensation.cs'
method = next(line.strip() for line in source.read_text().splitlines() if line.strip().startswith('internal static object AdminRead('))
out = Path(sys.argv[1]).resolve()
out.mkdir(parents=True, exist_ok=False)
(out / 'Check.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><LangVersion>14</LangVersion><UseSharedCompilation>false</UseSharedCompilation></PropertyGroup></Project>')
(out / 'Subject.cs').write_text('static class OfflineCompensation { internal static object Call(string name, params object[] args) => new Invocation(name,args);\n' + method + '\n}')
(out / 'Checks.cs').write_bytes((Path(__file__).parent / 'Checks.cs').read_bytes())
(out / 'source.json').write_text(json.dumps({'source': str(source), 'sha256': hashlib.sha256(source.read_bytes()).hexdigest(), 'boundary': __doc__}, indent=2) + '\n')
subprocess.run(['dotnet', 'run', '--project', str(out / 'Check.csproj'), '-c', 'Release'], check=True)
