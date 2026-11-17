#!/usr/bin/env bash
# Builds and runs every test project directly (xUnit v3 test projects are executables).
# Same results as `dotnet test`, without its driver process, which crashed intermittently with
# "Internal CLR error (0x80131506)" on SDK 10.0.115 before running any tests.
set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"
export MSBUILDDISABLENODEREUSE=1
dotnet build --nologo -v quiet || exit 1
status=0
for exe in tests/*/bin/Debug/net10.0/Pdrive.*.Tests; do
  "$exe" -noLogo -noColor "$@" | grep -E "\[FAIL\]|Total:|^\s+(Assert|Expected|Actual|Exception)" || true
  [ "${PIPESTATUS[0]}" -eq 0 ] || status=1
done
exit $status
