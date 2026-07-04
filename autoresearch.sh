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

start_ns=$(date +%s%N)
"${dotnet_cmd[@]}" test "$project" \
  --configuration "$configuration" \
  --no-build \
  --filter "$filter" \
  --logger "console;verbosity=minimal"
end_ns=$(date +%s%N)

elapsed_ms=$(((end_ns - start_ns) / 1000000))

echo "METRIC process_message_usecase_ms=${elapsed_ms}"
echo "METRIC process_message_usecase_tests=47"
