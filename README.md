# Signavex

A stock-screening app: daily technical + fundamental signal scans across the S&P 500/400, an AI-generated daily market brief, and a forward-test "predictions" view that records every scored candidate so we can measure how the scoring actually performs over time.

Live at [signavex.learnedgeek.com](https://signavex.learnedgeek.com).

## Stack

- **Web:** ASP.NET Core 10 (Blazor static SSR — no SignalR, no WebSocket). Single Kestrel process behind Caddy reverse-proxy.
- **Jobs:** `Signavex.Jobs` — .NET 10 console app. Same publish directory as the Web binary; systemd timers invoke it as `dotnet Signavex.Jobs.dll <job>` for scheduled work (daily scan, brief, economic sync, fundamentals backfill).
- **Database:** PostgreSQL (Hetzner production), LocalDB SQL Server (Windows dev). EF Core 10 + Npgsql 10.0.2.
- **Hosting:** Hetzner VPS (`learnedgeek-host`), shared with `learnedgeek.com` and `allevotherapeutics.com`. Caddy + Cloudflare DNS (grey-cloud).
- **CI/CD:** GitHub Actions — `ci.yml` (build + test) and `deploy.yml` (manual `workflow_dispatch`, tar-over-SSH atomic swap into `/var/www/signavex/`).
- **Data providers:** Polygon (OHLCV + ticker metadata), Alpha Vantage (fundamentals), FRED (economic indicators), Anthropic Claude (daily brief generation).

## Local dev

Web (against LocalDB):

```powershell
dotnet run --project src/Signavex.Web
```

Jobs (against the same LocalDB):

```powershell
dotnet run --project src/Signavex.Jobs -- scan
dotnet run --project src/Signavex.Jobs -- daily-brief
```

Production Postgres inspection (SSH tunnel):

```powershell
ssh -L 15432:localhost:5432 learnedgeek-host
psql -h localhost -p 15432 -U signavex signavex
```

## Project layout

```
src/
  Signavex.Domain/        # Entities, value types, enums
  Signavex.Infrastructure/# EF Core DbContext, migrations, providers
  Signavex.Signals/       # Technical + fundamental signal calculations
  Signavex.Engine/        # ScanEngine + orchestrators (scan, brief, sync)
  Signavex.Jobs/          # Console app — scheduled job dispatcher
  Signavex.Web/           # Blazor pages + admin endpoints
tests/
  Signavex.Domain.Tests/
  Signavex.Infrastructure.Tests/
  Signavex.Signals.Tests/
  Signavex.Engine.Tests/
tools/
  MigrateData/            # One-shot Azure SQL → Postgres migration tool (2026-05-30)
  Quantback/              # Prototype portfolio-simulation Backtest
docs/                     # Implementation plans, design docs, phase trackers
```

## Docs

- [HETZNER-MIGRATION-PLAN.md](docs/HETZNER-MIGRATION-PLAN.md) — Azure → Hetzner cutover, shipped 2026-05-30.
- [PRODUCT-DESIGN-PUBLIC-LAUNCH.md](docs/PRODUCT-DESIGN-PUBLIC-LAUNCH.md) — Phases L1–L10 product plan.
- [FORWARD-TEST-PHASE-TRACKER.md](docs/FORWARD-TEST-PHASE-TRACKER.md) — Forward-testing / predictions feature.
- [Signavex-Implementation-Plan.md](docs/Signavex-Implementation-Plan.md) — Original scanner implementation plan.
