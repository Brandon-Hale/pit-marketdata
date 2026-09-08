#!/usr/bin/env bash
# Verifies the Twelve Data free tier provides everything Layer 1 depends on.
# Usage: TWELVEDATA_API_KEY=... ./scripts/verify-vendor.sh
set -euo pipefail

: "${TWELVEDATA_API_KEY:?set TWELVEDATA_API_KEY}"

base="https://api.twelvedata.com"
symbol="AAPL"
fail=0

check() {
  local name="$1" url="$2" expect="$3"
  local body
  body=$(curl -s -m 30 "$url&apikey=${TWELVEDATA_API_KEY}")

  if echo "$body" | grep -q "$expect"; then
    echo "PASS  $name"
  else
    echo "FAIL  $name"
    echo "      $(echo "$body" | head -c 300)"
    fail=1
  fi
}

check "time_series (unadjusted daily bars)" \
  "$base/time_series?symbol=$symbol&interval=1day&outputsize=5000&start_date=2015-01-01" '"values"'

check "splits" "$base/splits?symbol=$symbol&range=full" '"splits"'

check "dividends" "$base/dividends?symbol=$symbol&range=full" '"dividends"'

exit "$fail"
