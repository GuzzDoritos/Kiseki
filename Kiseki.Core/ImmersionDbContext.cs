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

                if (!string.IsNullOrWhiteSpace(connStr))
                {
                    options.UseNpgsql(connStr);
                }
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
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
