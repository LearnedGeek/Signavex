# Hetzner Migration Plan — Addendum / Review Notes

> **STATUS: SHIPPED 2026-05-30 alongside the main plan.** Production live at https://signavex.learnedgeek.com. Of the addendum's suggestions, the ones that landed: grey-cloud Cloudflare, DataProtection keys persisted via Ansible, Postgres `Maximum Pool Size=20`, systemd timers (not cron), Ansible `aspnet_site` role extended in P3.B, fire-and-forget try/catch around job dispatch. See [project_hetzner_cutover_shipped](../../../../Users/mcart/.claude/projects/e--dev-work-Signavex/memory/project_hetzner_cutover_shipped.md) for the as-shipped state.

Author: another Claude session (via Mark, 2026-05-29)
Purpose: review feedback on the [Signavex Hetzner Migration Plan](./HETZNER-MIGRATION-PLAN.md), written from the perspective of the Claude session that just built out the `learnedgeek-host` Ansible playbook that this migration will plug into. This document is meant to be read alongside the main plan and is non-authoritative — when in conflict, the main plan (curated by Mark) wins.

If you are an OC working on this migration: read the main plan first, then this addendum. Apply the notes you find useful; defer the rest to Mark.

---

## Context the main plan assumes but doesn't fully spell out

The `learnedgeek-host` Hetzner box was set up under Terraform (`learnedgeek-infra/learnedgeek-host/main.tf`) and is now also managed by an Ansible playbook (`learnedgeek-infra/learnedgeek-host/ansible/`) that owns the host-config layer: systemd units, Postgres roles + databases, the Caddyfile. Adding signavex to the box means appending one entry to `host_vars/learnedgeek-host.yml` and running the playbook. Cf. [`learnedgeek-infra/learnedgeek-host/ansible/README.md`](../../learnedgeek-infra/learnedgeek-host/ansible/README.md) for the `sites:` schema and the add-a-new-site workflow.

The relevant existing template (`roles/aspnet_site/templates/site.service.j2`) renders a systemd unit per site from a per-site dict. The Caddyfile template renders the whole file from the same sites list. Vault holds the DB password.

**What the playbook does NOT yet do that signavex needs:**

- **Cron management.** No task currently writes `/etc/cron.d/<site>` from a list of jobs. Signavex's 6 scheduled jobs are the first use case.
- **logrotate config.** No task currently writes `/etc/logrotate.d/<site>`. Signavex needs daily rotation, 14-day retention.
- **DataProtection key directory creation.** Not currently in the role. Signavex (and any other site using cookie auth) needs a persisted out-of-deploy-tree path. See section below.
- **systemd timer unit support.** If you decide to use `systemd timer` units instead of cron for scheduled jobs (more modern, integrated logging, easier debugging via `systemctl list-timers`), the role would need to grow templates for those too.

If you extend the role to support cron + logrotate before signavex's first deploy, every future site benefits. Suggested shape for `host_vars`:

```yaml
- name: signavex
  description: "Signavex"
  port: 5001                       # whatever's next free; learnedgeek is 5000
  dll: Signavex.Web.dll
  needs_postgres: true
  db_name: signavex
  db_role: signavex
  db_password: "{{ vault_signavex_db_password }}"
  caddy_hosts:
    - signavex.learnedgeek.com
  cron_jobs:                       # NEW shape — role would template /etc/cron.d/signavex
    - name: scan
      schedule: "0 22 * * 1-5"
      command: /usr/bin/flock -n /tmp/signavex-scan.lock /usr/bin/dotnet /var/www/signavex/Signavex.Jobs.dll scan
      user: www-data
      log: /var/log/signavex/scan.log
    - name: brief
      ...
  logrotate:                       # NEW shape — role would template /etc/logrotate.d/signavex
    paths: ["/var/log/signavex/*.log"]
    frequency: daily
    rotate: 14
    compress: true
  data_protection_keys: /var/lib/signavex/dp-keys   # NEW — role would ensure dir exists, www-data:www-data, 0700
```

The playbook lives in a separate repo (`learnedgeek-infra`), so this signavex plan's P3.B mostly becomes "submit a PR to learnedgeek-infra that extends `aspnet_site` to support these new keys, then add the signavex entry." Maybe 30–60 minutes if you're touching the role for the first time. The previous Claude session offered to do this prep work; up to Mark whether to take that.

---

## Specific items worth re-examining in the plan

### Cloudflare SSL mode for `signavex.learnedgeek.com` (P3.D.2)

The plan says "orange-cloud proxy ON." This is a different posture from `lakecountryspanish.com` (grey cloud, Caddy gets its own Let's Encrypt cert directly). Both are valid, but they require different setup:

**Orange cloud implications:**
- Cloudflare terminates TLS at the edge using its cert. Visitors see a Cloudflare-issued cert, not the origin's.
- Caddy on the origin **can't obtain a Let's Encrypt cert via the default HTTP-01 or TLS-ALPN-01 challenges** — Cloudflare's proxy intercepts port 80 and 443. Three options:
  1. Use the **DNS-01 challenge** (Caddy → Cloudflare API token → adds TXT records for ACME validation). Clean. Requires giving Caddy a scoped Cloudflare API token. Configured in the Caddyfile with `tls { dns cloudflare <TOKEN> }`.
  2. Use a **Cloudflare Origin Certificate** (free, 15-year cert issued by Cloudflare's CA, installed manually on origin). Requires switching Cloudflare's SSL mode to **Full (Strict)**.
  3. **Cloudflare SSL mode = Full** (not Strict) accepts any cert from origin — even self-signed. Less secure but lowest friction. Caddy can self-sign and CF won't complain.
- The origin will see Cloudflare's IPs as the request source. Real client IPs come from `CF-Connecting-IP` header. Configure ASP.NET Core `ForwardedHeaders` middleware to trust Cloudflare's IP ranges (or just trust the `CF-Connecting-IP` header directly via a custom middleware).

**Pre-decision before kickoff:** which SSL mode and which cert path. Otherwise this becomes a mid-Phase-3 refactor where Caddy can't issue a cert and you discover why the hard way. If you're not sure, **Full + DNS-01 challenge** is the conservative pick — origin still has Let's Encrypt, no manual Origin Cert install, no risk of Cloudflare → origin TLS failing.

By contrast, `lakecountryspanish.com` is grey cloud, so Caddy obtains its own cert via TLS-ALPN-01 with no special config. If signavex stays grey cloud too, no Cloudflare SSL decision needed and the Caddyfile block looks identical to LCS's. The only loss is DDoS / WAF / CDN protection — which signavex doesn't really need given its usage profile.

**Recommendation:** consider grey cloud for signavex initially, matching the LCS pattern. If you later want CF protection, orange-cloud the record and add the DNS-01 cert config in one focused change.

### Fire-and-forget pattern (P1.D.1)

The plan suggests:

```csharp
_ = Task.Run(() => orchestrator.RunScanAsync(default));
```

This pattern has a known footgun: an unobserved task that throws results in an unhandled exception that's silently swallowed (and depending on .NET version, may even crash the process via `UnobservedTaskException`). The global CLAUDE.md rule about it is explicit: *"Fire-and-forget (_ = SomeAsync()) swallows exceptions. If you don't await it, you won't know it failed."*

**Two safer alternatives:**

1. **`IBackgroundTaskQueue` + a `BackgroundService` consumer** (the canonical ASP.NET Core pattern). ~30 LOC. Returns immediately to the user; the work runs in a hosted service with proper logging and exception capture. Microsoft docs cover the pattern under "Background tasks with hosted services."
2. **Invoke the same cron-runnable binary** that the scheduled job uses: `systemd-run --on-active=0 /usr/bin/dotnet /var/www/signavex/Signavex.Jobs.dll scan`. The web app shells out to systemd, which runs the same code with the same logging and the same exception handling as the scheduled cron job. Crude but the failure mode is identical to what you already have to handle for cron.

Option 1 is cleaner long-term. Option 2 minimizes new code surface and reuses the cron logging path.

### DataProtection keys

The plan flags the risk in the Risks section but doesn't specify a path. ASP.NET Core's default DP key location on Linux is `~/.aspnet/DataProtection-Keys`, which under `www-data` becomes `/var/www/.aspnet/DataProtection-Keys` — that's *under* the deploy tree's parent.

The atomic `/var/www/signavex.new` → `/var/www/signavex` swap pattern (which I'm assuming will mirror what `learnedgeek-host/ansible` uses for the LCS deploy) doesn't touch sibling directories, so the default location might actually be fine. But it's still better to be explicit:

```csharp
services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo("/var/lib/signavex/dp-keys"))
    .SetApplicationName("signavex");
```

And have Ansible create `/var/lib/signavex/dp-keys` with the right ownership (`www-data:www-data`, `0700`). The `SetApplicationName` is important if multiple ASP.NET apps share the box and you don't want them to share key rings.

(LCS has this same issue, flagged in the site audit as a known gap and not yet fixed. Worth aligning the fix when you do signavex.)

### Identity tables in Postgres are case-sensitive (P2.A.4 step 1)

SQL Server is case-insensitive by default; Postgres is case-sensitive. EF Core's generated migrations create tables with quoted names like `"AspNetUsers"`. EF queries through the model handle this fine — but **any raw SQL anywhere in the codebase that references Identity tables unquoted will break**.

Things to grep for before Phase 2:
- `FROM AspNetUsers` (unquoted) anywhere in `*.cs` or `*.sql` files
- `JOIN AspNetRoles`
- Stored procedures (if any)
- Migration files outside the EF-managed set
- Any SQL passed via `FromSqlRaw`, `ExecuteSqlRaw`, `Dapper`, etc.

The fix is either to quote the identifiers (`FROM "AspNetUsers"`) or to add a Postgres convention in EF that lowercases table names. Worth deciding before P2 surprise you mid-migration.

### Connection pool sizing (touched in P3.B but worth elaborating)

Npgsql defaults to MaxPoolSize=100. Postgres on Hetzner defaults to `max_connections=100`. With multiple apps sharing one Postgres instance:

- learnedgeek (currently 0 active DB connections, doesn't use Postgres)
- allevo (~5–10 connections in pool)
- lakecountryspanish + lakecountryspanish-stg (~5–10 each)
- signavex (~5–10 expected)

Total expected: 25–40 max. Comfortable headroom. But Postgres reserves connections for superuser tasks (3 default), and the box itself may have other clients (pg_dump cron, manual psql). If you ever hit "FATAL: remaining connection slots are reserved for non-replication superuser," lower app-side `MaxPoolSize` or bump Postgres `max_connections` (the latter requires a Postgres restart, so prefer the former).

Worth setting `MaxPoolSize=20` explicitly in signavex's connection string. Same hygiene worth applying to other sites when you touch them.

---

## Things to add to the plan

### Observability replacement for App Insights

When App Insights goes away (P1.E), you lose:
- Request tracing (request count, latency p50/p95/p99)
- Exception aggregation (group exceptions, see frequency over time, drill down to stack)
- Dependency tracking (SQL queries, HTTP outbound)
- Live metrics stream

Serilog file logging gives you raw logs, which you can `tail`, grep, and write ad-hoc dashboards from. That's enough for many sites. But if you want the aggregation/UI experience back:

- **Seq** (local, free for dev tier, ~30MB Docker image, web UI on port 5341). Ships logs from Serilog directly via `Serilog.Sinks.Seq`. Could run alongside Postgres on the same box. Zero recurring cost.
- **Grafana + Loki + Promtail** stack. More moving parts. More powerful. Probably overkill for signavex alone but pays off when you have 5+ sites on the box.
- **OpenTelemetry collector → Honeycomb / Datadog / etc.** SaaS aggregation. Costs money but takes seconds to set up.

Worth a one-line decision in the plan. Default: file logs + Seq sidecar on the same box, decided at P1.E.

### Pre-Phase-1 spike on Cloudflare SSL mode

(Already covered above.) Should be a deliberate sub-phase, not discovered mid-Phase-3.

### Phase 1 time estimate is optimistic

Realistically Phase 1 is 2 sessions, not 1. The DB provider swap (P1.B) alone can surface DateOnly mappings, enum-to-int discriminators, decimal precision issues, and timestamp-with-time-zone gotchas (Npgsql is strict about `DateTime.Kind = Utc` for `timestamptz` columns; will throw at insert time if Kind is Local or Unspecified). The Functions-to-console-app refactor (P1.C + P1.D) is another half-session of careful refactoring even when the orchestrators are already DI-friendly.

Not a problem — just calibrate expectations. Two focused sessions instead of one rushed one usually produces better code anyway.

---

## Things the plan got right that are worth preserving

These are choices that, in my reading, are worth defending if a future review tries to second-guess them:

1. **Console app + cron for scheduled work** instead of an in-process scheduler. Survives app restarts cleanly; failures are visible in log files immediately; you can manually trigger a job from a shell in seconds. In-process schedulers (Hangfire, Quartz.NET) add complexity that pays off in a multi-server environment but not on a single VPS.
2. **Throwaway migration tool** instead of trying to write a reusable framework. ~300 LOC budget keeps it disciplined.
3. **Hard cutover + 7-day Azure parallel cost as rollback insurance**. Realistic; the cost of one extra week of Azure billing is cheap compared to a stressful rollback.
4. **`flock -n` on cron entries** to prevent overlapping runs. Defensive; nearly free; saves you from a slow-scan-collides-with-next-scheduled-scan incident at 4am.
5. **Pre-Phase-1 decisions section** with dates. Locks in choices that would otherwise drift mid-project.
6. **Abort criterion on the migration tool** (~300 LOC). Forces a sane fallback rather than scope-creeping the tool into something that owns the migration.

---

## Quick reference: what's in the related infrastructure repo

For anyone working on this who hasn't looked at `learnedgeek-infra/learnedgeek-host/ansible/`:

| File | Purpose |
|---|---|
| `inventory.yml` | Single `webhosts` group, single host (`learnedgeek-host`). Adding boxes = adding entries here. |
| `host_vars/learnedgeek-host.yml` | The `sites:` list — source of truth for what runs on the box. **This is where signavex's entry goes.** |
| `group_vars/webhosts/main.yml` | Non-secret defaults (paths, dotnet location, user/group). |
| `group_vars/webhosts/vault.yml` | Encrypted DB passwords. `ansible-vault edit` to add signavex's. |
| `roles/aspnet_site/templates/site.service.j2` | The systemd unit template. Signavex will need either this template extended for "no Caddy hosts, console-only" or a separate template if it has a non-web component. |
| `roles/aspnet_site/templates/Caddyfile.j2` | Renders the whole `/etc/caddy/Caddyfile` from the sites list. Signavex's reverse-proxy block goes through here. |
| `roles/aspnet_site/tasks/main.yml` | The orchestrating task file. Cron + logrotate + DataProtection key dir tasks would be added here. |

Adding signavex is, in the ideal case, ~20 lines added to `host_vars/learnedgeek-host.yml` + a few extensions to `aspnet_site` to support cron and DP keys. The Ansible side is small. Phase 1 (code shape changes) is by far the bulk of the work.

---

*This is review feedback, not authority. Mark drives the migration. Use what's useful, ignore what isn't.*
