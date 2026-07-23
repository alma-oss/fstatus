#!/usr/bin/env bash

set -eu
set -o pipefail

run() {
    if [ -n "${RTK_ACTIVE:-}" ]; then
        local output
        if ! output=$("$@" 2>&1); then
            printf '%s\n' "$output" >&2
            return 1
        fi
    else
        "$@"
    fi
}

run dotnet tool restore
run dotnet tool run paket restore
# pre-build so the `dotnet run --no-build` below emits no restore/build chatter
run dotnet build ./build/build.fsproj

# shellcheck disable=SC2068
FAKE_DETAILED_ERRORS=true dotnet run --no-build --project ./build/build.fsproj -- $@
