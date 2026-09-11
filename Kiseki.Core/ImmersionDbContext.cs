using Kiseki.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Core
{
    public class ImmersionDbContext : DbContext
    {
        public DbSet<Franchise> Franchises { get; set; }

        public DbSet<MediaSeries> MediaSeries { get; set; }

        public DbSet<MediaWork> MediaWorks { get; set; }

        public DbSet<ImmersionLog> ImmersionLogs { get; set; }
        public DbSet<TtsuBinding> TtsuBindings { get; set; }
        public DbSet<TtsuImportReceipt> TtsuImportReceipts { get; set; }

        public string DbPath => string.Empty;

        public ImmersionDbContext()
        {
        }

        public ImmersionDbContext(DbContextOptions<ImmersionDbContext> options)
            : base(options)
        {
        }

        protected override void OnConfiguring(DbContextOptionsBuilder options)
        {
            if (!options.IsConfigured)
            {
                var connStr = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
                    ?? Environment.GetEnvironmentVariable("DATABASE_URL");

                var provider = Environment.GetEnvironmentVariable("DatabaseProvider")?.Trim().ToLowerInvariant();
                if (provider is not (null or "sqlite" or "postgres" or "postgresql" or "npgsql"))
                    throw new InvalidOperationException("DatabaseProvider must be sqlite or postgres.");
                if (provider != "sqlite" && !string.IsNullOrWhiteSpace(connStr))
                {
                    options.UseNpgsql(Services.PostgreSqlConnectionStringNormalizer.Normalize(connStr),
                        npgsql => npgsql.ExecutionStrategy(deps => new Services.NeonRetryingExecutionStrategy(deps)));
                }
                else if (provider is "postgres" or "postgresql" or "npgsql")
                    throw new InvalidOperationException("Set ConnectionStrings__DefaultConnection for PostgreSQL.");
                else
                {
                    var path = Environment.GetEnvironmentVariable("KISEKI_DB_PATH") ??
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kiseki.db");
                    path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    options.UseSqlite(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path }.ToString());
                }
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TtsuBinding>(entity =>
            {
                entity.HasKey(binding => binding.MediaWorkId);
                entity.Property(binding => binding.Version).IsConcurrencyToken();
                entity.HasOne<MediaWork>().WithOne().HasForeignKey<TtsuBinding>(binding => binding.MediaWorkId)
                    .OnDelete(DeleteBehavior.Cascade);
            });
            modelBuilder.Entity<ImmersionLog>(entity =>
            {
                entity.HasOne<MediaWork>().WithMany(work => work.Logs).HasForeignKey(log => log.MediaWorkId);
                entity.HasOne<TtsuBinding>().WithMany().HasForeignKey(log => log.TtsuBindingId)
                    .OnDelete(DeleteBehavior.Restrict);
                entity.HasIndex(log => new { log.TtsuBindingId, log.Date }).IsUnique();
                entity.ToTable(table => table.HasCheckConstraint("CK_ImmersionLogs_TtsuBinding",
                    "\"TtsuBindingId\" IS NULL OR (\"MediaWorkId\" IS NOT NULL AND \"TtsuBindingId\" = \"MediaWorkId\" AND \"Source\" = 'ttsu')"));
            });
            modelBuilder.Entity<Franchise>(entity =>
            {
                entity.Property(franchise => franchise.Title).IsRequired();
                entity.HasIndex(franchise => franchise.JitenAnchorDeckId);
            });

            modelBuilder.Entity<MediaSeries>(entity =>
            {
                entity.Property(series => series.Title).IsRequired();
                entity.HasIndex(series => series.FranchiseId);
                entity.HasIndex(series => series.JitenDeckId);

                entity.HasOne(series => series.Franchise)
                    .WithMany(franchise => franchise.Series)
                    .HasForeignKey(series => series.FranchiseId)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            modelBuilder.Entity<MediaWork>(entity =>
            {
                entity.Property(work => work.MediaType)
                    .HasDefaultValue(MediaType.Book);
                entity.Property(work => work.JitenCoverUrl)
                    .HasMaxLength(2048);

                entity.HasIndex(work => work.MediaSeriesId);
                entity.HasIndex(work => work.JitenDeckId);
                entity.HasIndex(work => work.JitenSubdeckId);

                entity.HasOne(work => work.MediaSeries)
                    .WithMany(series => series.Works)
                    .HasForeignKey(work => work.MediaSeriesId)
                    .OnDelete(DeleteBehavior.SetNull);

                entity.ToTable(table => table.HasCheckConstraint(
                    "CK_MediaWorks_JitenSubdeckRequiresDeck",
                    "\"JitenSubdeckId\" IS NULL OR \"JitenDeckId\" IS NOT NULL"));
            });
        }
    }
}
