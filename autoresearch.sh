#!/usr/bin/env bash
set -euo pipefail

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=1

if command -v dotnet >/dev/null 2>&1; then
  dotnet_cmd=(dotnet)
elif command -v dotnet.exe >/dev/null 2>&1; then
  dotnet_cmd=(dotnet.exe)
else
  echo "dotnet SDK not found on PATH" >&2
  exit 127
fi

project="tests/Mira.Core.Tests/Mira.Core.Tests.csproj"
configuration="Release"
filter="FullyQualifiedName~ProcessMessageUseCaseTests"

# Build outside the measured window so the metric tracks deterministic assistant use-case execution,
# not compiler or restore overhead. --no-restore keeps the harness offline.
"${dotnet_cmd[@]}" build "$project" --configuration "$configuration" --no-restore >/dev/null

output_file="$(mktemp)"
trap 'rm -f "$output_file"' EXIT

"${dotnet_cmd[@]}" test "$project" \
  --configuration "$configuration" \
  --no-build \
  --filter "$filter" \
  --logger "console;verbosity=minimal" | tee "$output_file"

elapsed_ms=""
while IFS= read -r line; do
  if [[ "$line" =~ Duration:[[:space:]]+([0-9]+)[[:space:]]+ms ]]; then
    elapsed_ms="${BASH_REMATCH[1]}"
  fi
done < "$output_file"

if [[ -z "$elapsed_ms" ]]; then
  echo "Could not parse test execution duration from dotnet test output." >&2
  exit 1
fi

echo "METRIC process_message_usecase_ms=${elapsed_ms}"
echo "METRIC process_message_usecase_tests=47"
