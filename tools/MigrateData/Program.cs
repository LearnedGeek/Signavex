using Microsoft.EntityFrameworkCore;
using Signavex.Infrastructure.Persistence;

// SQL Server's `datetime2` doesn't carry a timezone, so EF reads it as
// DateTime.Kind=Unspecified. Npgsql refuses to write Unspecified into
// `timestamp with time zone` columns — only Utc. All Signavex's DateTime
// columns are *Utc by intent (StartedAtUtc, CompletedAtUtc, FetchedAtUtc,
// etc.), so this AppContext switch tells Npgsql to treat the inbound
// Unspecified values as UTC. Must be set before any DbContext spins up.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

// =============================================================================
// MigrateData — Azure SQL → PostgreSQL one-shot data migration tool.
//
// Reads from the SQL Server source (Azure SQL prod) via the same
// SignavexDbContext class the app uses, writes to the PostgreSQL
// destination (Hetzner box). Walks tables in FK-dependency order;
// preserves auto-generated primary key IDs to keep foreign-key
// references intact, then bumps each Postgres sequence to past the
// max imported ID so subsequent app-driven inserts don't collide.
//
// Usage:
//   SOURCE_CONNSTR='Server=tcp:...azuresql...;Database=signavex;User Id=...;Password=...;Encrypt=true'  \
//   DEST_CONNSTR='Host=hetzner;Database=signavex;Username=signavex;Password=...'                       \
//   dotnet run --project tools/MigrateData                                                              \
//     [--dry-run]            count source rows only, write nothing
//     [--force-overwrite]    proceed even if destination already has rows
//     [--validate-only]      skip writes; only re-count source + dest and diff
//
// The tool refuses to run unless both connection strings are explicitly set.
// =============================================================================

var dryRun = args.Contains("--dry-run");
var forceOverwrite = args.Contains("--force-overwrite");
var validateOnly = args.Contains("--validate-only");

var sourceConn = Environment.GetEnvironmentVariable("SOURCE_CONNSTR");
var destConn = Environment.GetEnvironmentVariable("DEST_CONNSTR");

if (string.IsNullOrWhiteSpace(sourceConn) || string.IsNullOrWhiteSpace(destConn))
{
    Console.Error.WriteLine("ERROR: SOURCE_CONNSTR and DEST_CONNSTR env vars are both required.");
    Console.Error.WriteLine("See header comment in Program.cs for usage.");
    return 1;
}

Console.WriteLine($"Source: SQL Server {Truncate(sourceConn, 60)}");
Console.WriteLine($"Dest:   PostgreSQL {Truncate(destConn, 60)}");
Console.WriteLine($"Mode:   {(dryRun ? "DRY RUN " : "")}{(forceOverwrite ? "FORCE-OVERWRITE " : "")}{(validateOnly ? "VALIDATE-ONLY" : "")}");
Console.WriteLine();

// ── Build contexts ──────────────────────────────────────────────────────────
var sourceOptions = new DbContextOptionsBuilder<SignavexDbContext>()
    .UseSqlServer(sourceConn)
    .Options;
var destOptions = new DbContextOptionsBuilder<SignavexDbContext>()
    .UseNpgsql(destConn)
    .Options;

using var source = new SignavexDbContext(sourceOptions);
using var dest = new SignavexDbContext(destOptions);

// ── Preflight: destination should be empty unless --force-overwrite ─────────
if (!dryRun && !validateOnly && !forceOverwrite)
{
    var destUserCount = await dest.Users.CountAsync();
    if (destUserCount > 0)
    {
        Console.Error.WriteLine($"ABORT: destination already has {destUserCount} users. Re-run with --force-overwrite to proceed.");
        return 2;
    }
}

// ── Table migrations in FK order ────────────────────────────────────────────
// Order matters:
//   1. Identity tables first (string IDs, no remap needed)
//   2. ScanRuns (has FK from ScanCandidates)
//   3. ScanCandidates (FK → ScanRuns.Id)
//   4. EconomicSeries (FK from EconomicObservations via SeriesId string)
//   5. EconomicObservations
//   6. Everything else (flat tables, no FKs to internal IDs)

var totalRowsMoved = 0;

totalRowsMoved += await CopyTable("AspNetRoles",        source.Roles,                     dest.Roles,                     dryRun);
totalRowsMoved += await CopyTable("AspNetUsers",        source.Users,                     dest.Users,                     dryRun);
totalRowsMoved += await CopyTable("AspNetUserRoles",    source.UserRoles,                 dest.UserRoles,                 dryRun);
totalRowsMoved += await CopyTable("AspNetUserClaims",   source.UserClaims,                dest.UserClaims,                dryRun);
totalRowsMoved += await CopyTable("AspNetUserLogins",   source.UserLogins,                dest.UserLogins,                dryRun);
totalRowsMoved += await CopyTable("AspNetUserTokens",   source.UserTokens,                dest.UserTokens,                dryRun);
totalRowsMoved += await CopyTable("AspNetRoleClaims",   source.RoleClaims,                dest.RoleClaims,                dryRun);

totalRowsMoved += await CopyTable("ScanRuns",           source.ScanRuns,                  dest.ScanRuns,                  dryRun);
totalRowsMoved += await CopyTable("ScanCandidates",     source.ScanCandidates,            dest.ScanCandidates,            dryRun);
totalRowsMoved += await CopyTable("ScanCheckpoints",    source.ScanCheckpoints,           dest.ScanCheckpoints,           dryRun);
totalRowsMoved += await CopyTable("ScanCommands",       source.ScanCommands,              dest.ScanCommands,              dryRun);

totalRowsMoved += await CopyTable("EconomicSeries",     source.EconomicSeries,            dest.EconomicSeries,            dryRun);
totalRowsMoved += await CopyTable("EconomicObservations", source.EconomicObservations,    dest.EconomicObservations,      dryRun);
totalRowsMoved += await CopyTable("EconomicSyncTrackers", source.EconomicSyncTrackers,    dest.EconomicSyncTrackers,      dryRun);

totalRowsMoved += await CopyTable("DailyBriefs",        source.DailyBriefs,               dest.DailyBriefs,               dryRun);
totalRowsMoved += await CopyTable("FundamentalsCache",  source.FundamentalsCache,         dest.FundamentalsCache,         dryRun);
totalRowsMoved += await CopyTable("HistoricalOhlcv",    source.HistoricalOhlcv,           dest.HistoricalOhlcv,           dryRun);
totalRowsMoved += await CopyTable("QuantbackRuns",      source.QuantbackRuns,             dest.QuantbackRuns,             dryRun);
totalRowsMoved += await CopyTable("PickOutcomes",       source.PickOutcomes,              dest.PickOutcomes,              dryRun);

Console.WriteLine();
Console.WriteLine($"Total rows {(dryRun ? "to migrate" : "migrated")}: {totalRowsMoved:N0}");

// ── Sequence advance (Postgres only) ────────────────────────────────────────
if (!dryRun && !validateOnly && totalRowsMoved > 0)
{
    Console.WriteLine();
    Console.WriteLine("Advancing Postgres sequences past imported IDs...");
    await AdvanceSequencesAsync(dest);
}

// ── Validation: compare counts ──────────────────────────────────────────────
if (!dryRun)
{
    Console.WriteLine();
    Console.WriteLine("Validating row counts source vs destination...");
    var mismatches = 0;
    mismatches += await Validate("AspNetRoles",        source.Roles,             dest.Roles);
    mismatches += await Validate("AspNetUsers",        source.Users,             dest.Users);
    mismatches += await Validate("AspNetUserRoles",    source.UserRoles,         dest.UserRoles);
    mismatches += await Validate("ScanRuns",           source.ScanRuns,          dest.ScanRuns);
    mismatches += await Validate("ScanCandidates",     source.ScanCandidates,    dest.ScanCandidates);
    mismatches += await Validate("EconomicSeries",     source.EconomicSeries,    dest.EconomicSeries);
    mismatches += await Validate("EconomicObservations", source.EconomicObservations, dest.EconomicObservations);
    mismatches += await Validate("DailyBriefs",        source.DailyBriefs,       dest.DailyBriefs);
    mismatches += await Validate("FundamentalsCache",  source.FundamentalsCache, dest.FundamentalsCache);
    mismatches += await Validate("HistoricalOhlcv",    source.HistoricalOhlcv,   dest.HistoricalOhlcv);
    mismatches += await Validate("QuantbackRuns",      source.QuantbackRuns,     dest.QuantbackRuns);
    mismatches += await Validate("PickOutcomes",       source.PickOutcomes,      dest.PickOutcomes);

    if (mismatches > 0)
    {
        Console.Error.WriteLine($"FAIL: {mismatches} row-count mismatch(es). See log above.");
        return 3;
    }
    Console.WriteLine("All row counts match.");
}

Console.WriteLine();
Console.WriteLine(dryRun ? "Dry run complete — no rows written." : "Migration complete.");
return 0;

// ── Helpers ─────────────────────────────────────────────────────────────────

static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

async Task<int> CopyTable<T>(string name, IQueryable<T> sourceQuery, DbSet<T> destSet, bool dry) where T : class
{
    var sourceCount = await sourceQuery.AsNoTracking().CountAsync();
    if (sourceCount == 0)
    {
        Console.WriteLine($"  SKIP {name,-25} (empty in source)");
        return 0;
    }

    var existing = await destSet.CountAsync();
    if (existing > 0 && !forceOverwrite)
    {
        Console.WriteLine($"  SKIP {name,-25} ({existing:N0} already in dest)");
        return 0;
    }

    if (dry || validateOnly)
    {
        Console.WriteLine($"  PEEK {name,-25} ({sourceCount:N0} rows in source)");
        return sourceCount;
    }

    // Pull in batches to keep memory bounded on the wide HistoricalOhlcv +
    // EconomicObservations tables (potentially 100k+ rows each).
    const int batchSize = 1000;
    var copied = 0;
    var rows = await sourceQuery.AsNoTracking().ToListAsync();

    foreach (var batch in rows.Chunk(batchSize))
    {
        destSet.AddRange(batch);
        await dest.SaveChangesAsync();
        dest.ChangeTracker.Clear();
        copied += batch.Length;
    }

    Console.WriteLine($"  OK   {name,-25} ({copied:N0} rows)");
    return copied;
}

async Task<int> Validate<T>(string name, IQueryable<T> sourceQuery, DbSet<T> destSet) where T : class
{
    var srcCount = await sourceQuery.AsNoTracking().CountAsync();
    var dstCount = await destSet.AsNoTracking().CountAsync();
    if (srcCount == dstCount)
    {
        Console.WriteLine($"  OK   {name,-25} src={srcCount,8:N0}  dst={dstCount,8:N0}");
        return 0;
    }
    Console.Error.WriteLine($"  FAIL {name,-25} src={srcCount,8:N0}  dst={dstCount,8:N0}  (Δ={dstCount - srcCount:+#;-#;0})");
    return 1;
}

async Task AdvanceSequencesAsync(SignavexDbContext db)
{
    // Postgres sequences backing IDENTITY columns don't auto-advance when
    // explicit values are inserted (which is what we just did). Without
    // this fix-up, the next app-driven insert would try the next sequence
    // value — typically `1` — and hit a duplicate-key error.
    //
    // We use pg_get_serial_sequence(table, column) to find the sequence
    // name, then setval(...) past the current max(id). For tables with
    // string PKs (Identity tables, EconomicSeries which uses SeriesId as
    // a unique key, etc.) there's nothing to do.
    var tablesWithIntId = new[]
    {
        ("\"ScanRuns\"",              "\"Id\""),
        ("\"ScanCandidates\"",        "\"Id\""),
        ("\"ScanCheckpoints\"",       "\"Id\""),
        ("\"ScanCommands\"",          "\"Id\""),
        ("\"EconomicSeries\"",        "\"Id\""),
        ("\"EconomicSyncTrackers\"",  "\"Id\""),
        ("\"DailyBriefs\"",           "\"Id\""),
        ("\"FundamentalsCache\"",     "\"Id\""),
        ("\"HistoricalOhlcv\"",       "\"Id\""),
        ("\"QuantbackRuns\"",         "\"Id\""),
        ("\"PickOutcomes\"",          "\"Id\""),
    };

    foreach (var (table, idCol) in tablesWithIntId)
    {
        // pg_get_serial_sequence treats its first arg as a regular identifier
        // (case-folded to lowercase) unless the string literal itself contains
        // embedded double-quotes — e.g. '"ScanRuns"' preserves case. Without
        // the embedded quotes, the lookup tries `scanruns` and fails because
        // EF created the table as `"ScanRuns"`. The column arg, by contrast,
        // is already treated as case-preserved.
        var bareTable = table.Replace("\"", "");
        var bareCol = idCol.Replace("\"", "");
        var sql = $@"
            SELECT setval(
                pg_get_serial_sequence('""{bareTable}""', '{bareCol}'),
                COALESCE((SELECT MAX({idCol}) FROM {table}), 1),
                true
            );";
        try
        {
            await db.Database.ExecuteSqlRawAsync(sql);
            Console.WriteLine($"  OK   sequence reset for {table}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  WARN sequence reset failed for {table}: {ex.Message}");
        }
    }
}
