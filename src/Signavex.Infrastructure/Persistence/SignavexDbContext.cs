using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Signavex.Infrastructure.Persistence.Entities;

namespace Signavex.Infrastructure.Persistence;

public class SignavexDbContext : IdentityDbContext<ApplicationUser>
{
    public SignavexDbContext(DbContextOptions<SignavexDbContext> options) : base(options) { }

    public DbSet<ScanRunEntity> ScanRuns => Set<ScanRunEntity>();
    public DbSet<ScanCandidateEntity> ScanCandidates => Set<ScanCandidateEntity>();
    public DbSet<ScanCheckpointEntity> ScanCheckpoints => Set<ScanCheckpointEntity>();
    public DbSet<ScanCommandEntity> ScanCommands => Set<ScanCommandEntity>();
    public DbSet<EconomicSeriesEntity> EconomicSeries => Set<EconomicSeriesEntity>();
    public DbSet<EconomicObservationEntity> EconomicObservations => Set<EconomicObservationEntity>();
    public DbSet<EconomicSyncTrackerEntity> EconomicSyncTrackers => Set<EconomicSyncTrackerEntity>();
    public DbSet<DailyBriefEntity> DailyBriefs => Set<DailyBriefEntity>();
    public DbSet<FundamentalsCacheEntity> FundamentalsCache => Set<FundamentalsCacheEntity>();
    public DbSet<HistoricalOhlcvEntity> HistoricalOhlcv => Set<HistoricalOhlcvEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ScanRunEntity>(e =>
        {
            e.ToTable("ScanRuns");
            e.HasKey(x => x.Id);
            e.Property(x => x.ScanId).IsRequired();
            e.HasIndex(x => x.ScanId).IsUnique();
            e.HasIndex(x => x.CompletedAtUtc);
            e.HasMany(x => x.Candidates)
                .WithOne(x => x.ScanRun)
                .HasForeignKey(x => x.ScanRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ScanCandidateEntity>(e =>
        {
            e.ToTable("ScanCandidates");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Ticker);
            e.HasIndex(x => new { x.ScanRunId, x.Ticker }).IsUnique();
        });

        modelBuilder.Entity<ScanCheckpointEntity>(e =>
        {
            e.ToTable("ScanCheckpoints");
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<ScanCommandEntity>(e =>
        {
            e.ToTable("ScanCommands");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.RequestedAtUtc);
        });

        modelBuilder.Entity<EconomicSeriesEntity>(e =>
        {
            e.ToTable("EconomicSeries");
            e.HasKey(x => x.Id);
            e.Property(x => x.SeriesId).IsRequired();
            e.HasIndex(x => x.SeriesId).IsUnique();
            e.HasMany(x => x.Observations)
                .WithOne(x => x.Series)
                .HasForeignKey(x => x.SeriesId)
                .HasPrincipalKey(x => x.SeriesId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EconomicObservationEntity>(e =>
        {
            e.ToTable("EconomicObservations");
            e.HasKey(x => new { x.SeriesId, x.Date });
            e.HasIndex(x => x.SeriesId);
        });

        modelBuilder.Entity<EconomicSyncTrackerEntity>(e =>
        {
            e.ToTable("EconomicSyncTrackers");
            e.HasKey(x => x.Id);
            e.Property(x => x.SeriesId).IsRequired();
            e.HasIndex(x => x.SeriesId).IsUnique();
        });

        modelBuilder.Entity<DailyBriefEntity>(e =>
        {
            e.ToTable("DailyBriefs");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Date).IsUnique();
        });

        modelBuilder.Entity<FundamentalsCacheEntity>(e =>
        {
            e.ToTable("FundamentalsCache");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Ticker).IsUnique();
            e.HasIndex(x => x.RetrievedAtUtc);
        });

        modelBuilder.Entity<HistoricalOhlcvEntity>(e =>
        {
            e.ToTable("HistoricalOhlcv");
            e.HasKey(x => x.Id);
            // (Ticker, TradingDate) is the natural lookup key — covered by a
            // unique index so DB enforces no duplicates and queries are fast.
            e.HasIndex(x => new { x.Ticker, x.TradingDate }).IsUnique();
            e.Property(x => x.Open).HasPrecision(18, 4);
            e.Property(x => x.High).HasPrecision(18, 4);
            e.Property(x => x.Low).HasPrecision(18, 4);
            e.Property(x => x.Close).HasPrecision(18, 4);
        });
    }
}
