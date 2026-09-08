#!/usr/bin/env bash
# Publishes the ingest Lambda for the managed dotnet10 runtime and zips it for Terraform.
# Usage: ./scripts/build-lambda.sh
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="$root/dist"
staging="$out/lambda"

rm -rf "$staging" "$out/lambda.zip"
mkdir -p "$staging"

# --self-contained false: the managed runtime supplies .NET, so shipping it would only
# make the package larger and the cold start slower.
dotnet publish "$root/app/src/MarketData.Lambda/MarketData.Lambda.csproj" \
  --configuration Release \
  --runtime linux-arm64 \
  --self-contained false \
  --output "$staging"

if command -v zip > /dev/null 2>&1; then
  ( cd "$staging" && zip -qr "$out/lambda.zip" . )
else
  powershell.exe -NoProfile -Command \
    "Compress-Archive -Path '$(cygpath -w "$staging")\*' -DestinationPath '$(cygpath -w "$out")\lambda.zip' -Force"
fi

echo "built $out/lambda.zip ($(du -h "$out/lambda.zip" | cut -f1))"
