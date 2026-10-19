#!/usr/bin/env bash
# Writes the two software bills of materials (CycloneDX, JSON) into the folder given (default "sbom"):
#   caseregister-api.cdx.json       the API's packages (the CycloneDX .NET tool in dotnet-tools.json)
#   caseregister-frontend.cdx.json  what the site ships (production packages only; the version of the tool is fixed here)
# Run "dotnet tool restore" and "npm ci --ignore-scripts" in frontend first.
set -euo pipefail
cd "$(dirname "$0")/.."

out="${1:-sbom}"
mkdir -p "$out"
out="$(cd "$out" && pwd)"

dotnet tool run dotnet-CycloneDX CaseRegister.Api/CaseRegister.Api.csproj --output "$out" --filename caseregister-api.cdx.json --json
(cd frontend && npx --yes @cyclonedx/cyclonedx-npm@6.0.1 --omit dev --output-format JSON --output-file "$out/caseregister-frontend.cdx.json" package.json)

for file in caseregister-api.cdx.json caseregister-frontend.cdx.json; do
  test -s "$out/$file"
  grep -q '"bomFormat": *"CycloneDX"' "$out/$file"
done
echo "Wrote $out/caseregister-api.cdx.json and $out/caseregister-frontend.cdx.json"
