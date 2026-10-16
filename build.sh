#!/bin/sh
# Builds WikiDumpMods.dll with Mono's mcs (no game DLLs needed: game types are reached by reflection).
# Needs BepInEx 5.4.23.2 core in refs/BepInEx/core:
#   curl -L -o bep.zip https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.2/BepInEx_win_x64_5.4.23.2.zip
#   unzip bep.zip 'BepInEx/core/*' -d refs
set -e
cd "$(dirname "$0")"
mkdir -p stub/strict build
mcs -target:library -sdk:4.x -out:stub/strict/UnityEngine.dll stub/UnityEngine.cs
mcs -target:library -sdk:4.x -r:stub/strict/UnityEngine.dll -r:refs/BepInEx/core/BepInEx.dll -r:System.Core.dll -r:System.dll \
    -resource:baseline/vanilla_baseline.jsonl.gz,WikiDumpMods.vanilla_baseline.jsonl.gz -out:build/WikiDumpMods.dll src/*.cs
echo "built build/WikiDumpMods.dll"
