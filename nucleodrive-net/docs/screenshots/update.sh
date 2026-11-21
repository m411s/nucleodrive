#!/usr/bin/env bash
# Regenerates the README's screenshots (readme-{sync,files,settings}-{light,dark}.png) from the
# headless screenshot test, which renders the app with made-up sample data, never your own Drive.
#   docs/screenshots/update.sh
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."
out="$(mktemp -d)"
trap 'rm -rf "$out"' EXIT
dotnet build tests/NucleoDrive.App.Tests --nologo -v quiet
NUCLEODRIVE_SCREENSHOTS="$out" tests/NucleoDrive.App.Tests/bin/Debug/net10.0/NucleoDrive.App.Tests \
  -noLogo -class "NucleoDrive.App.Tests.Screenshots" >/dev/null
cp "$out"/readme-*.png docs/screenshots/
ls -l docs/screenshots/*.png
