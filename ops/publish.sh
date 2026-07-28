#!/bin/sh
# Self-contained linux-arm64 publish — no .NET runtime on the Pi, by design.
set -eu
cd "$(dirname "$0")/.."

dotnet publish src/PokemonInvestBatch.Worker -c Release -r linux-arm64 --self-contained -o publish

echo
echo "Publish complete. Deploy with e.g.:"
echo "  rsync -av --exclude appsettings.Production.json publish/ pokemon@<pi-ip>:/opt/pokemon-invest-batch/"
echo "  rsync -av blacklist.json pokemon@<pi-ip>:/opt/pokemon-invest-batch/"
echo "Then on the Pi: sudo systemctl restart pokemon-invest-batch"
