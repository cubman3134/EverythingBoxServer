#!/usr/bin/env pwsh
# Runs EverythingBoxServer via `dotnet run`. Generic — knows nothing about any
# particular plugin. Honors EBS_CONFIG / EBS_PLUGINS_DIR / EBS_FILES_DIR from the
# environment if already set; otherwise the server falls back to its own defaults
# (see docs/DEPLOY.md).
#
# Usage:
#   ./run.ps1
#   $env:EBS_CONFIG = "C:\path\to\everythingbox-server.json"; ./run.ps1

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

dotnet run --project (Join-Path $root "EverythingBox.Server") -c Release
