#!/usr/bin/env bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

dotnet build src/VoxMind.Api/VoxMind.Api.csproj -c Release --nologo -q
exec dotnet src/VoxMind.Api/bin/Release/net8.0/VoxMind.Api.dll "$@"
