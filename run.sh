#!/usr/bin/env bash
# Runs EverythingBoxServer via `dotnet run`. Generic — knows nothing about any
# particular plugin. Honors EBS_CONFIG / EBS_PLUGINS_DIR / EBS_FILES_DIR from the
# environment if already set; otherwise the server falls back to its own defaults
# (see docs/DEPLOY.md).
#
# Usage:
#   ./run.sh
#   EBS_CONFIG=/path/to/everythingbox-server.json ./run.sh

set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

exec dotnet run --project "$root/EverythingBox.Server" -c Release
