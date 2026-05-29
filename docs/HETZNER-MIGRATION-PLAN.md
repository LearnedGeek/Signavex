# Signavex → Hetzner Migration Plan

Moving Signavex off Azure (App Service + Functions + SQL Basic + App Insights + Storage) and onto the existing `learnedgeek-host` Hetzner box. Same physical machine that already serves the static LearnedGeek site, plus Postgres, plus Caddy fronted by Cloudflare.

**Why:** ~$6–10/mo on Azure → ~$0 incremental on the box that's already running. Plus no more F1 cold-start UX, no SCM auth weirdness, no Functions Consumption-plan 10-minute execution limits.

## Decisions confirmed (2026-05-29)
1. **Data migration:** preserve everything. Fall back to hybrid only if the migration tool turns out to be more than ~300 LOC of throwaway code.
2. **Scheduling:** system `cron` invoking a console app (`Signavex.Jobs`). No in-process scheduler.
3. **Local dev DB:** none — work against staging/prod Postgres via SSH tunnel as needed.
4. **Deploys:** GitHub Actions → SSH/rsync (mirror `learnedgeek` deploy.yml verbatim).
5. **Cutover:** hard cutover, validate, swap Cloudflare DNS, `terraform destroy` Azure.
6. **Domain:** `signavex.learnedgeek.com` initially. Future `signavex.com` registration deferred.
7. **Off-box backup:** skipped for now. Local `pg_dump` to `/var/backups/` only (P3.C.1–2 retained, P3.C.3 dropped).
8. **Cutover timing:** no hard date — proceeds when Phase 1–3 are validated on staging and we're confident.

## Target architecture

```
┌─────────────────────────────────────────────────────┐
│ Cloudflare (DNS + proxy + TLS termination at edge)  │
└────────────────────┬────────────────────────────────┘
                     │
                     ▼
┌─────────────────────────────────────────────────────┐
│ learnedgeek-host (Hetzner VPS)                      │
│                                                      │
│  Caddy ── reverse proxy ──→ Signavex.Web (kestrel)  │
│                                  │                   │
│                                  ▼                   │
│                          PostgreSQL (localhost)      │
│                                  ▲                   │
│  cron ──→ Signavex.Jobs.dll <job> ───┘               │
│                                                      │
│  Files: /var/www/signavex (publish artifacts)        │
│         /var/log/signavex (Serilog file output)      │
└─────────────────────────────────────────────────────┘
```

One ASP.NET Core 10 process serving HTTP. Six cron entries invoking the same binary with a job name argument. One Postgres database, same box. Caddy fronts both Signavex and the existing LearnedGeek site behind Cloudflare.

## Status legend
- [ ] not started
- [~] in progress
- [x] complete

---

## Phase 1 — Code shape (target: 1 long session)

### P1.A: TFM + package bump to .NET 10
- [ ] **P1.A.1** Bump every `<TargetFramework>net8.0</TargetFramework>` → `net10.0` across all `.csproj` files (8 projects: Domain, Domain.Tests, Engine, Engine.Tests, Infrastructure, Infrastructure.Tests, Signals, Signals.Tests, Web). Also Functions for now — we delete it in P1.D but it builds in the interim.
- [ ] **P1.A.2** Bump these specific packages that are still on 8.x:
  - `Microsoft.AspNetCore.Identity.EntityFrameworkCore 8.0.12` → `10.x`
  - `Microsoft.EntityFrameworkCore.SqlServer 8.0.12` → drops entirely (replaced in P1.B)
  - `Microsoft.EntityFrameworkCore.Sqlite 8.0.12` → `10.x` (tests still use this)
  - `Microsoft.NET.Test.Sdk 17.8.0` → `17.10.x` for net10 test runner
- [ ] **P1.A.3** Run full test suite. Expected: green. No known breaking changes 8 → 10 for the surface area we use.
- [ ] **P1.A.4** Spot-check the IDE for nullable / source-gen warnings introduced by the SDK bump; treat any new ones as a small cleanup pass.

**Exit:** Solution builds on net10.0. 272 tests still pass.

### P1.B: SQL Server → PostgreSQL provider swap
- [ ] **P1.B.1** Add `Npgsql.EntityFrameworkCore.PostgreSQL 10.x` to `Signavex.Infrastructure.csproj`. Remove `Microsoft.EntityFrameworkCore.SqlServer`.
- [ ] **P1.B.2** In `ServiceCollectionExtensions.AddSignavexInfrastructure`: change `options.UseSqlServer(connectionString)` → `options.UseNpgsql(connectionString)`.
- [ ] **P1.B.3** Connection string format: `Host=localhost;Database=signavex;Username=signavex;Password=...`. Rendered from template via GH Actions just like LearnedGeek's pattern.
- [ ] **P1.B.4** Delete the entire `src/Signavex.Infrastructure/Persistence/Migrations/` folder. SQL Server migrations are not Postgres-compatible.
- [ ] **P1.B.5** Generate fresh initial migration: `dotnet ef migrations add InitialPostgres --project src/Signavex.Infrastructure --startup-project src/Signavex.Web`. One file replaces ~10 prior migrations.
- [ ] **P1.B.6** Sanity check the migration: enum/decimal/DateOnly/DateTime columns should map cleanly. EF Core 10's Npgsql provider handles `DateOnly` natively as `date` and `DateTime UTC` as `timestamp with time zone`.
- [ ] **P1.B.7** Local sanity: spin up Postgres 16 in Docker for one off (`docker run --rm -e POSTGRES_PASSWORD=test -p 5432:5432 postgres:16`), point connection string at it, run `dotnet ef database update`. Inspect schema with `psql`. Run `dotnet test` against it.

**Exit:** schema applies cleanly to a fresh Postgres; existing tests (which use SQLite in-memory) still green; integration smoke test against Docker Postgres passes.

### P1.C: Extract `Signavex.Jobs` console app
- [ ] **P1.C.1** New `src/Signavex.Jobs/Signavex.Jobs.csproj` (Microsoft.NET.Sdk.Worker SDK or just `Microsoft.NET.Sdk` with `Host.CreateApplicationBuilder`). References Domain, Engine, Infrastructure, Signals.
- [ ] **P1.C.2** `Program.cs`: ~50 LOC. `Host.CreateApplicationBuilder(args)`, `AddSignavexInfrastructure`, `AddSignavexEngine`, `AddSignavexSignals`. Then a switch on `args[0]`:
  - `scan` → `ScanOrchestrator.RunScanAsync`
  - `scan-resume` → `ScanOrchestrator.RunScanAsync` (the orchestrator already checks for a resumable checkpoint)
  - `brief` → `BriefOrchestrator.RunBriefAsync`
  - `sync-economic` → `EconomicSyncOrchestrator.RunSyncAsync`
  - `fundamentals-backfill` → `FundamentalsBackfillOrchestrator.RunBackfillCycleAsync`
  - `evaluate-pick-outcomes` → `PickOutcomeEvaluatorOrchestrator.RunCycleAsync`
  - `backfill-pick-outcomes` → `PickOutcomeBackfillOrchestrator.RunAsync`
- [ ] **P1.C.3** Lift the orchestrator classes out of `Signavex.Functions/Orchestrators/` and into either `Signavex.Engine` or a new `Signavex.Engine/Orchestrators/` folder. They're already DI-friendly and Functions-runtime-free; only the `[Function]` attribute classes know about Azure. Web app continues to reference these for the admin HTTP endpoints.
- [ ] **P1.C.4** Exit code: 0 on success, 1 on unknown job, 2 on orchestrator exception. cron logs both stdout and stderr to `/var/log/signavex/<job>.log` so failures are visible without App Insights.

**Exit:** `dotnet run --project src/Signavex.Jobs -- scan` runs a full scan locally against Postgres in Docker.

### P1.D: Re-absorb admin HTTP endpoints into Web; delete Functions
- [ ] **P1.D.1** Currently the Web app's `/admin/scan`, `/admin/sync-economic`, etc. proxy via `HttpClient("functions")` to the Functions app. Delete that handler and replace each with a direct call: `_ = Task.Run(() => orchestrator.RunScanAsync(default))`. The proxy was only there because Functions was a separate process.
- [ ] **P1.D.2** Delete `Functions__Url` and `Functions__AdminKey` config keys, AdminKeyAuthorizer, the HttpClient("functions") registration.
- [ ] **P1.D.3** Delete `src/Signavex.Functions/` and all migration/build references. Remove from `Signavex.sln`.
- [ ] **P1.D.4** Delete `.github/workflows/deploy-functions.yml`.
- [ ] **P1.D.5** Re-run full test suite. Update tests that referenced Functions types (likely none, given Functions had no test project).

**Exit:** Functions code gone. Single `Signavex.Web` HTTP process, single `Signavex.Jobs` cron-driven console app. All tests pass.

### P1.E: Application Insights → Serilog file
- [ ] **P1.E.1** Add `Serilog.AspNetCore`, `Serilog.Sinks.File` to Web; `Serilog.Extensions.Hosting`, `Serilog.Sinks.File` to Jobs.
- [ ] **P1.E.2** Configure `RollingFile` sink writing to `/var/log/signavex/web-.log` (Web) and `/var/log/signavex/jobs-<job>-.log` (Jobs). Daily rotation, 14-day retention.
- [ ] **P1.E.3** Remove `Microsoft.ApplicationInsights.AspNetCore` if referenced anywhere.

**Exit:** Logs visible via `tail -f /var/log/signavex/*.log` over SSH.

---

## Phase 2 — Data migration tool (target: 1 session)

Throwaway console app `tools/Signavex.MigrationTool/`. **Not** part of the solution that gets deployed — exists only to move data once.

### P2.A: Tool design
- [ ] **P2.A.1** Two `DbContextOptionsBuilder`s in `Program.cs` — one with `.UseSqlServer(azureConnString)`, one with `.UseNpgsql(localConnString)`. Both instantiate the same `SignavexDbContext` class.
- [ ] **P2.A.2** Read connection strings from env: `SOURCE_CONNSTR`, `DEST_CONNSTR`. Never commit them.
- [ ] **P2.A.3** Confirm destination is empty before starting (refuse to overwrite). Manual flag `--force-overwrite` if you really mean it.
- [ ] **P2.A.4** Order matters because of FKs. Walk in this sequence:
  1. ASP.NET Identity tables (`AspNetUsers`, `AspNetRoles`, `AspNetUserRoles`, `AspNetUserLogins`, `AspNetUserClaims`, `AspNetUserTokens`, `AspNetRoleClaims`). String IDs — copy as-is. PBKDF2 hashes are provider-agnostic.
  2. `ScanRuns` then `ScanCandidates` (capture old→new run ID mapping, rewrite FK on candidates as we go).
  3. `EconomicSeries` then `EconomicObservations` (the FK is on `SeriesId` string, not auto-gen — straight copy).
  4. `EconomicSyncTrackers`, `DailyBriefs`, `FundamentalsCache`, `ScanCheckpoints`, `ScanCommands`, `HistoricalOhlcv`, `QuantbackRuns`, `PickOutcomes` — all flat, straight copy.
- [ ] **P2.A.5** Batch inserts (`AddRange` + `SaveChanges` every 1000 rows) so we don't hold the whole `HistoricalOhlcv` table in memory.
- [ ] **P2.A.6** Print row counts at start (source) and end (destination) per table. Fail loudly if they don't match.

### P2.B: Validate
- [ ] **P2.B.1** Run tool with source=Azure SQL, dest=local Docker Postgres. End-to-end smoke.
- [ ] **P2.B.2** Spot check via psql: `select count(*) from "AspNetUsers"`, same for top 3 tables. Compare to Azure SQL via the portal query editor.
- [ ] **P2.B.3** Boot `Signavex.Web` locally against the migrated Postgres. Log in as your account. Visit `/predictions`, `/quantback`, `/today`. Confirm everything reads the migrated data correctly.

**Hard guardrail:** if the tool grows past ~300 LOC or P2.B reveals weird type mismatches that are non-trivial to fix, **abort and fall back to hybrid migration** (Identity + ScanRuns + PickOutcomes only, let other caches rebuild). That call is yours; I'll flag if I see us getting near the line.

**Exit:** Local Postgres holds an exact functional copy of the prod database. Web app boots against it.

---

## Phase 3 — Deployment pipeline (target: half a session)

Mirror `learnedgeek/.github/workflows/deploy.yml`. Differences vs LearnedGeek:

### P3.A: GH Actions workflow
- [ ] **P3.A.1** New `.github/workflows/deploy.yml` in `LearnedGeek/Signavex` repo. Copy LearnedGeek's deploy.yml verbatim, then:
  - `working-directory` for npm steps: `src/Signavex.Web`
  - publish target: `src/Signavex.Web/Signavex.Web.csproj`
  - Additionally publish `src/Signavex.Jobs/Signavex.Jobs.csproj` to the same output dir (or a sibling `publish/jobs/` folder)
  - Smoke test URL: whatever the eventual signavex domain is (decide in P3.D)
- [ ] **P3.A.2** Secrets to add in the LearnedGeek/Signavex GH repo settings:
  - `DEPLOY_SSH_KEY`, `DEPLOY_HOST`, `DEPLOY_USER` (same VPS, can reuse the existing key)
  - `DB_PASSWORD`, `POLYGON_API_KEY`, `ALPHA_VANTAGE_API_KEY`, `FRED_API_KEY`, `ANTHROPIC_API_KEY`, `SENDGRID_API_KEY`, `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET`, `ADMIN_KEY` (or rename — it's now in-process so the header check moves to a route filter)
- [ ] **P3.A.3** `appsettings.Production.json.template` committed; secrets rendered via `envsubst` at deploy time, identical pattern to LearnedGeek.
- [ ] **P3.A.4** Delete the existing `.github/workflows/deploy.yml` and `deploy-functions.yml` that target Azure. Optionally keep them on a `legacy-azure` branch for ~1 month, in case we need to redeploy Azure for rollback.

### P3.B: Ansible role
- [ ] **P3.B.1** In `learnedgeek-infra`: new `host_vars/learnedgeek-host.yml` entry under whatever the existing `aspnet_site` role takes — site name `signavex`, port (e.g., 5001 since LearnedGeek is on 5000), domain, etc.
- [ ] **P3.B.2** Vault entries for the secrets that need to be on the box at install time (DB password — most others are injected via the rendered appsettings file, so they don't need Ansible vault). Per the existing learnedgeek pattern.
- [ ] **P3.B.3** Postgres role: create database `signavex` and user `signavex` with appropriate grants. If the existing role doesn't have a "create database" task, add one. Otherwise do it manually once on the box.
- [ ] **P3.B.4** systemd unit for `signavex.service` (mirrors `learnedgeek.service`, just different ExecStart and WorkingDirectory).
- [ ] **P3.B.5** Caddy block for `signavex.<domain>` (decide domain in P3.D). Reverse proxy to localhost:5001. Cloudflare in front as usual.
- [ ] **P3.B.6** Crontab entries for the 6 jobs, owner `www-data` (matching the systemd unit), env-vars include `DOTNET_ENVIRONMENT=Production`. Use `flock` so a long-running scan doesn't get re-invoked while still in flight:
  ```
  0 22 * * 1-5  www-data  /usr/bin/flock -n /tmp/signavex-scan.lock /usr/bin/dotnet /var/www/signavex/Signavex.Jobs.dll scan >> /var/log/signavex/scan.log 2>&1
  ```
  Plus 5 more for brief, sync-economic, fundamentals-backfill, evaluate-pick-outcomes, scan-resume.
- [ ] **P3.B.7** Log rotation: `/etc/logrotate.d/signavex` rotates `/var/log/signavex/*.log` daily, keeps 14 days, compresses old ones.

### P3.C: Backups (was Azure SQL automatic; now ours)
- [ ] **P3.C.1** Nightly `pg_dump signavex` to `/var/backups/signavex-YYYY-MM-DD.sql.gz`. Either cron entry on the box or a system timer unit.
- [ ] **P3.C.2** Retention: 14 daily + 4 weekly + 6 monthly. `find -mtime` cleanup.
- [ ] ~~**P3.C.3** Off-box copy~~ — skipped per 2026-05-29 decision. Revisit if there's ever real-user data worth losing.

### P3.D: Domain
- [x] **P3.D.1** ~~Decide domain~~ — `signavex.learnedgeek.com` confirmed. Future `signavex.com` registration deferred.
- [ ] **P3.D.2** Cloudflare DNS: A record for `signavex` → VPS IP, orange-cloud proxy ON. Wildcard cert on `*.learnedgeek.com` already covers it.

**Exit:** Push to a `migration` branch triggers a deploy to a temporary path on the VPS (e.g., `/var/www/signavex-staging`), Caddy serves it on a staging subdomain. Validate end-to-end.

---

## Phase 4 — Cutover (target: half a session, pick a quiet evening)

### P4.A: Pre-cutover checklist
- [ ] All Phase 1, 2, 3 phases green.
- [ ] Staging instance on Hetzner serves correctly with a recent (but not final) data snapshot.
- [ ] Backup of Azure SQL captured (export to bacpac via portal — free).

### P4.B: Cutover steps
- [ ] **P4.B.1** Stop the Azure Functions app (portal → signavex-functions → Stop). This freezes scan/brief/sync writes so the final data export is stable.
- [ ] **P4.B.2** Final run of the migration tool against Azure SQL → production Hetzner Postgres (not the staging one).
- [ ] **P4.B.3** Spot-check counts via psql; compare to Azure portal query editor.
- [ ] **P4.B.4** Switch `signavex.service` on the VPS to point at the production data (just a config reload — connection string already points at local Postgres). `systemctl restart signavex`.
- [ ] **P4.B.5** Cloudflare DNS: change the signavex domain from Azure App Service to the VPS. Lower TTL ahead of time if you want a fast revert path; otherwise propagation is 1–5 min via CF.
- [ ] **P4.B.6** Smoke test: log in as your account, hit `/today`, `/predictions`, `/quantback`. Trigger a manual scan via `/admin` → check the cron log file 30 seconds later for a "Daily scan started" entry.
- [ ] **P4.B.7** Wait for the 10pm UTC scan to fire from cron. Verify next morning that `ScanRuns` has a fresh row, `PickOutcomes` has new entries.

### P4.C: Decommission Azure
- [ ] **P4.C.1** Watch for 7 days. If anything regresses, point Cloudflare back at Azure (Azure resources still running, just idle).
- [ ] **P4.C.2** After 7 days clean: `cd learnedgeek-infra/signavex && terraform destroy`. Web App, Functions App, SQL Server, Storage Account, App Insights, Log Analytics Workspace all gone.
- [ ] **P4.C.3** Delete the `legacy-azure` branch + the two old GH Actions workflows.
- [ ] **P4.C.4** Update CLAUDE.md and project memory to reflect new architecture.

**Exit:** Signavex runs entirely on Hetzner, Azure cost line goes to $0 next billing cycle.

---

## Risks and rollback

- **Data migration tool surfaces a type mismatch we didn't anticipate.** Mitigation: P2.B validates against real data; abort criterion at ~300 LOC.
- **Cron job doesn't fire.** Mitigation: smoke test in P4.B.7 (wait for first scheduled scan). Cron failures land in `/var/log/syslog`; orchestrator failures land in `/var/log/signavex/`.
- **PostgreSQL connection exhaustion.** Mitigation: tune `MaxPoolSize` in connection string; default Npgsql is 100. Signavex's concurrency is low (1 scan at a time, occasional admin reqs) so unlikely to hit.
- **DataProtection keys not persisted.** Mitigation: the systemd unit sets `DOTNET_ENVIRONMENT=Production` and the deploy chowns the directory to `www-data` so DataProtection's default key ring location is writable. Same pattern that LearnedGeek already uses.
- **Rollback path:** for the first 7 days post-cutover, Azure resources stay up; Cloudflare DNS flip is the rollback. After 7 days, the rollback is "re-create from terraform + redeploy from the `legacy-azure` branch + import the most recent backup" — multi-hour. By design — we're not paying for a long parallel run.

## Open questions before kickoff
All resolved 2026-05-29. Ready to start Phase 1 when you are.
