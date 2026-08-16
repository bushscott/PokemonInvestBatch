#!/bin/sh
# Self-contained linux-arm64 publish — no .NET runtime on the Pi, by design.
#
# Because the deployment is self-contained, the .NET runtime ships INSIDE this
# bundle. Whichever SDK runs this script therefore decides which runtime the Pi
# executes, and nothing on the Pi can ever patch it — there is no system .NET
# there to update, no package manager that knows about it, no notification when
# a CVE lands. An out-of-date SDK silently ships an out-of-date runtime.
#
# That is not hypothetical: this deployment ran on 10.0.2 for weeks while 10.0.10
# carried seventeen CVE fixes, because a second, older SDK happened to be first
# on PATH. So the SDK is chosen deliberately below, and — more importantly — the
# runtime that actually landed in the bundle is verified before this script is
# willing to call the build a success.
set -eu
cd "$(dirname "$0")/.."

# The oldest runtime we are willing to put on the Pi. Raise it whenever a
# servicing release fixes something that matters; the build then refuses to
# produce a bundle below the line rather than shipping one quietly.
MIN_RUNTIME=10.0.10

# Every plausible SDK location on a dev machine, not just whatever PATH found.
# Newest SDK wins.
newest_sdk=""
dotnet_bin=""
for candidate in \
    "$(command -v dotnet 2>/dev/null || true)" \
    /usr/local/share/dotnet/dotnet \
    "$HOME/.dotnet/dotnet" \
    /opt/homebrew/opt/dotnet/bin/dotnet
do
    [ -n "$candidate" ] && [ -x "$candidate" ] || continue
    version=$(DOTNET_ROOT="$(dirname "$candidate")" "$candidate" --version 2>/dev/null) || continue
    [ -n "$version" ] || continue
    if [ -z "$newest_sdk" ] \
       || [ "$(printf '%s\n%s\n' "$newest_sdk" "$version" | sort -V | tail -1)" = "$version" ]; then
        newest_sdk="$version"
        dotnet_bin="$candidate"
    fi
done

if [ -z "$dotnet_bin" ]; then
    echo "No .NET SDK found. Install one from https://dotnet.microsoft.com/" >&2
    exit 1
fi

# DOTNET_ROOT must match the binary we picked, or it resolves packs from the
# other install and we are back to shipping whatever that one happens to hold.
DOTNET_ROOT="$(dirname "$dotnet_bin")"
export DOTNET_ROOT
echo "Using SDK $newest_sdk  ($dotnet_bin)"

rm -rf publish
"$dotnet_bin" publish src/PokemonInvestBatch.Worker \
    -c Release -r linux-arm64 --self-contained -o publish

# The check that actually matters. Not "which SDK ran" — that is a proxy — but
# "which runtime is in the box we are about to ship".
shipped=$(grep -o 'Microsoft\.NETCore\.App\.Runtime\.linux-arm64/[0-9][0-9.]*' \
    publish/PokemonInvestBatch.Worker.deps.json | head -1 | cut -d/ -f2)

if [ -z "$shipped" ]; then
    echo "Could not read the bundled runtime version — refusing to ship blind." >&2
    exit 1
fi

if [ "$(printf '%s\n%s\n' "$MIN_RUNTIME" "$shipped" | sort -V | head -1)" != "$MIN_RUNTIME" ]; then
    echo >&2
    echo "REFUSING TO SHIP" >&2
    echo "  bundled runtime : $shipped" >&2
    echo "  minimum allowed : $MIN_RUNTIME" >&2
    echo "  newest SDK found: $newest_sdk ($dotnet_bin)" >&2
    echo >&2
    echo "That SDK is too old to bundle a patched runtime. Install a current" >&2
    echo ".NET 10 SDK and run this again — the Pi has no other way to be patched." >&2
    exit 1
fi

echo
echo "Bundled .NET runtime: $shipped  (minimum $MIN_RUNTIME)"
echo "Publish complete. Deploy with e.g.:"
echo "  rsync -av --delete --exclude appsettings.Production.json --exclude blacklist.json \\"
echo "        --exclude tcgdex-set-aliases.json --exclude tcgdex-series-eras.json \\"
echo "        publish/ pokemon@<pi-ip>:/opt/pokemon-invest-batch/"
echo "  rsync -av blacklist.json tcgdex-set-aliases.json tcgdex-series-eras.json pokemon@<pi-ip>:/opt/pokemon-invest-batch/"
echo "Then on the Pi: sudo systemctl restart pokemon-invest-batch"
