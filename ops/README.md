# Dev machine, once per clone

```bash
git config core.hooksPath ops/git-hooks   # blocks committing appsettings.Production.json
```

# New Relic (as deployed 2026-07-28)

- Worker → OTLP via `NewRelic:LicenseKey` in appsettings.Production.json (US endpoint otlp.nr-data.net).
- Host: `newrelic-infra` via NR apt repo; `/etc/newrelic-infra.yml` (license_key, display_name).
- Postgres: `pg_stat_statements` in shared_preload_libraries + `CREATE EXTENSION`; role `newrelic` with `pg_monitor`; `nri-postgresql` config in `/etc/newrelic-infra/integrations.d/` with ENABLE_QUERY_MONITORING.
- Logs: `/etc/newrelic-infra/logging.d/pokemon.yml` forwards the worker's systemd unit + postgres log.
- **Pi 5 gotcha:** NR's fluent-bit crashes on the default 16K-page kernel
  (`jemalloc: Unsupported system page size`). Fix: `kernel=kernel8.img` in
  /boot/firmware/config.txt (4K pages) + reboot.
- Integrity metrics: `crawl.monotonicity_violations` (single hits are market
  noise — alert on a step change in the rate, the signature of a silent tier
  remap) and `crawl.pop_anomalies` by grader/kind (spike = census restatement
  like PSA's June 2026 one, decrease = census shrank). A real restatement hits
  hundreds of cards in one sweep; suggested condition:
  `SELECT sum(`crawl.pop_anomalies`) FROM Metric` above ~20 for 30 min sliding.

# Pi setup

Target: 16GB Raspberry Pi 5, 64-bit Raspberry Pi OS (Debian 12/13), SSD.

## 1. Postgres (apt)

```bash
sudo apt-get update && sudo apt-get install -y postgresql
```

## 2. Network access (LAN only)

Edit `/etc/postgresql/*/main/postgresql.conf`:

```
listen_addresses = '*'
```

Edit `/etc/postgresql/*/main/pg_hba.conf` — add ONE line, scoped to your LAN
subnet (adjust `192.168.1.0/24` to yours; never use `0.0.0.0/0`):

```
host    all    all    192.168.1.0/24    scram-sha-256
```

Then: `sudo systemctl restart postgresql`

## 3. Roles and databases

Edit the three passwords in `postgres-setup.sql`, then:

```bash
sudo -u postgres psql -f postgres-setup.sql
```

## 4. Schema

From the dev machine (owner role holds DDL; the app role never does):

```bash
POKEMON_DB="Host=<pi-ip>;Database=pokemon;Username=pokemon_owner;Password=..." \
  dotnet ef database update -p src/PokemonInvestBatch.Infrastructure -s src/PokemonInvestBatch.Infrastructure
```

Then apply the post-migration grants noted at the bottom of `postgres-setup.sql`:

```bash
sudo -u postgres psql -d pokemon -c "GRANT UPDATE ON cards, shapes, sets TO pokemon_app;"
```

## 5. App deployment

Self-contained publish — no runtime installed on the Pi:

```bash
dotnet publish src/PokemonInvestBatch.Worker -c Release -r linux-arm64 --self-contained
```

systemd unit and connection config land with the Worker task.
