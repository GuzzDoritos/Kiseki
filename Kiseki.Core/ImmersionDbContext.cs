using Kiseki.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Kiseki.Core
{
    public class ImmersionDbContext : DbContext
    {
        public DbSet<Franchise> Franchises { get; set; }

        public DbSet<MediaSeries> MediaSeries { get; set; }

        public DbSet<MediaWork> MediaWorks { get; set; }

        public DbSet<MediaInstallment> MediaInstallments { get; set; }
        public DbSet<InstallmentProviderIdentity> InstallmentProviderIdentities { get; set; }
        public DbSet<InstallmentProviderSnapshot> InstallmentProviderSnapshots { get; set; }
        public DbSet<JitenCatalogueRefreshReceipt> JitenCatalogueRefreshReceipts { get; set; }
        public DbSet<JitenFranchiseGraphNodeState> JitenFranchiseGraphNodeStates { get; set; }
        public DbSet<JitenFranchiseTopologyReceipt> JitenFranchiseTopologyReceipts { get; set; }

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
                entity.ToTable(table =>
                {
                    table.HasCheckConstraint("CK_TtsuBindings_CurrentCharacterPosition",
                        "\"CurrentCharacterPosition\" IS NULL OR \"CurrentCharacterPosition\" >= 0");
                    table.HasCheckConstraint("CK_TtsuBindings_ProgressFraction",
                        "\"ProgressFraction\" IS NULL OR (\"ProgressFraction\" >= 0 AND \"ProgressFraction\" <= 1)");
                    table.HasCheckConstraint("CK_TtsuBindings_ProgressRevision",
                        "\"ProgressRevision\" IS NULL OR \"ProgressRevision\" >= 0");
                });
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

            modelBuilder.Entity<JitenFranchiseGraphNodeState>(entity =>
            {
                entity.HasKey(state => new { state.FranchiseId, state.DeckId });
                entity.Property(state => state.LastProviderTitle).HasMaxLength(512);
                entity.Property(state => state.ProviderFingerprint).HasMaxLength(128);
                entity.Property(state => state.Version).IsConcurrencyToken().HasDefaultValue(Guid.Empty);
                entity.HasIndex(state => state.MediaSeriesId);
                entity.HasOne(state => state.Franchise)
                    .WithMany()
                    .HasForeignKey(state => state.FranchiseId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasOne(state => state.MediaSeries)
                    .WithMany()
                    .HasForeignKey(state => state.MediaSeriesId)
                    .OnDelete(DeleteBehavior.SetNull);
                entity.ToTable(table =>
                {
                    table.HasCheckConstraint("CK_JitenFranchiseGraphNodeStates_Deck", "\"DeckId\" > 0");
                    table.HasCheckConstraint("CK_JitenFranchiseGraphNodeStates_Resolution",
                        "\"Resolution\" BETWEEN 1 AND 3");
                    table.HasCheckConstraint("CK_JitenFranchiseGraphNodeStates_Fingerprint",
                        "length(trim(\"ProviderFingerprint\")) BETWEEN 1 AND 128");
                });
            });

            modelBuilder.Entity<JitenFranchiseTopologyReceipt>(entity =>
            {
                entity.HasKey(receipt => receipt.Id);
                entity.Property(receipt => receipt.ReviewFingerprint).HasMaxLength(128);
                entity.HasIndex(receipt => receipt.FranchiseId);
                entity.HasOne<Franchise>()
                    .WithMany()
                    .HasForeignKey(receipt => receipt.FranchiseId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.ToTable(table =>
                {
                    table.HasCheckConstraint("CK_JitenFranchiseTopologyReceipts_Anchor", "\"AnchorDeckId\" > 0");
                    table.HasCheckConstraint("CK_JitenFranchiseTopologyReceipts_Fingerprint",
                        "length(trim(\"ReviewFingerprint\")) BETWEEN 1 AND 128");
                });
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

            modelBuilder.Entity<MediaInstallment>(entity =>
            {
                entity.Property(installment => installment.LegacyTitle).HasMaxLength(512);
                entity.Property(installment => installment.CanonicalTitle).HasMaxLength(512);
                entity.Property(installment => installment.TitleOverride).HasMaxLength(512);
                entity.Property(installment => installment.MediaType).HasDefaultValue(MediaType.Book);
                entity.Property(installment => installment.IsIncluded).HasDefaultValue(true);
                entity.Property(installment => installment.CanonicalCoverUrl).HasMaxLength(2048);
                entity.Property(installment => installment.Version)
                    .IsConcurrencyToken()
                    .HasDefaultValue(Guid.Empty);
                entity.HasIndex(installment => new { installment.MediaSeriesId, installment.OrderKey });
                entity.HasOne(installment => installment.MediaSeries)
                    .WithMany(series => series.Installments)
                    .HasForeignKey(installment => installment.MediaSeriesId)
                    .OnDelete(DeleteBehavior.SetNull);
                entity.ToTable(table =>
                {
                    table.HasCheckConstraint("CK_MediaInstallments_OrderKey", "\"OrderKey\" >= 0");
                    table.HasCheckConstraint("CK_MediaInstallments_MediaType", "\"MediaType\" IN (1, 2, 3)");
                    table.HasCheckConstraint("CK_MediaInstallments_Kind", "\"Kind\" BETWEEN 0 AND 7");
                    table.HasCheckConstraint("CK_MediaInstallments_ReleaseState",
                        "\"ReleaseState\" BETWEEN 0 AND 2 AND (\"ReleaseStateOverride\" IS NULL OR \"ReleaseStateOverride\" BETWEEN 0 AND 2)");
                    table.HasCheckConstraint("CK_MediaInstallments_CanonicalCoverSource",
                        "\"CanonicalCoverSource\" BETWEEN 0 AND 3");
                    table.HasCheckConstraint("CK_MediaInstallments_Title",
                        "coalesce(length(trim(\"LegacyTitle\")), 0) > 0 OR coalesce(length(trim(\"CanonicalTitle\")), 0) > 0 OR coalesce(length(trim(\"TitleOverride\")), 0) > 0");
                    table.HasCheckConstraint("CK_MediaInstallments_CanonicalCharacterCount",
                        "\"CanonicalCharacterCount\" IS NULL OR \"CanonicalCharacterCount\" > 0");
                    table.HasCheckConstraint("CK_MediaInstallments_CharacterCountOverride",
                        "\"CharacterCountOverride\" IS NULL OR \"CharacterCountOverride\" > 0");
                    table.HasCheckConstraint("CK_MediaInstallments_CanonicalCover",
                        "(\"CanonicalCoverUrl\" IS NULL AND \"CanonicalCoverSource\" = 0) OR (\"CanonicalCoverUrl\" IS NOT NULL AND \"CanonicalCoverSource\" <> 0)");
                });
            });

            modelBuilder.Entity<InstallmentProviderIdentity>(entity =>
            {
                entity.HasKey(identity => new { identity.Provider, identity.NormalizedKey });
                entity.Property(identity => identity.Provider)
                    .HasMaxLength(InstallmentProviderIdentity.MaxProviderLength);
                entity.Property(identity => identity.NormalizedKey)
                    .HasMaxLength(InstallmentProviderIdentity.MaxNormalizedKeyLength);
                entity.HasIndex(identity => identity.MediaInstallmentId);
                entity.HasOne(identity => identity.MediaInstallment)
                    .WithMany(installment => installment.ProviderIdentities)
                    .HasForeignKey(identity => identity.MediaInstallmentId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.ToTable(table => table.HasCheckConstraint(
                    "CK_InstallmentProviderIdentities_ItemIds",
                    "\"ProviderItemId\" > 0 AND (\"ParentProviderItemId\" IS NULL OR \"ParentProviderItemId\" > 0)"));
                entity.ToTable(table => table.HasCheckConstraint(
                    "CK_InstallmentProviderIdentities_Keys",
                    "length(trim(\"Provider\")) BETWEEN 1 AND 64 AND length(trim(\"NormalizedKey\")) BETWEEN 1 AND 256"));
            });

            modelBuilder.Entity<InstallmentProviderSnapshot>(entity =>
            {
                entity.HasKey(snapshot => new { snapshot.Provider, snapshot.NormalizedKey, snapshot.Fingerprint });
                entity.Property(snapshot => snapshot.Provider)
                    .HasMaxLength(InstallmentProviderIdentity.MaxProviderLength);
                entity.Property(snapshot => snapshot.NormalizedKey)
                    .HasMaxLength(InstallmentProviderIdentity.MaxNormalizedKeyLength);
                entity.Property(snapshot => snapshot.Fingerprint)
                    .HasMaxLength(InstallmentProviderSnapshot.MaxFingerprintLength);
                entity.Property(snapshot => snapshot.Title).HasMaxLength(512);
                entity.Property(snapshot => snapshot.CoverUrl).HasMaxLength(2048);
                entity.Property(snapshot => snapshot.PayloadJson).HasColumnType("text");
                entity.Property(snapshot => snapshot.Version)
                    .IsConcurrencyToken()
                    .HasDefaultValue(Guid.Empty);
                entity.HasIndex(snapshot => new { snapshot.Provider, snapshot.NormalizedKey, snapshot.ObservedAtUtc });
                entity.HasOne(snapshot => snapshot.ProviderIdentity)
                    .WithMany(identity => identity.Snapshots)
                    .HasForeignKey(snapshot => new { snapshot.Provider, snapshot.NormalizedKey })
                    .OnDelete(DeleteBehavior.Cascade);
                entity.ToTable(table =>
                {
                    table.HasCheckConstraint("CK_InstallmentProviderSnapshots_CharacterCount",
                        "\"CharacterCount\" IS NULL OR \"CharacterCount\" >= 0");
                    table.HasCheckConstraint("CK_InstallmentProviderSnapshots_ProviderOrder",
                        "\"ProviderOrder\" IS NULL OR \"ProviderOrder\" >= 0");
                    table.HasCheckConstraint("CK_InstallmentProviderSnapshots_Enums",
                        "\"CoverSource\" BETWEEN 0 AND 3 AND \"ReleaseState\" BETWEEN 0 AND 2");
                    table.HasCheckConstraint("CK_InstallmentProviderSnapshots_Fingerprint",
                        "length(trim(\"Fingerprint\")) BETWEEN 1 AND 128");
                    table.HasCheckConstraint("CK_InstallmentProviderSnapshots_Cover",
                        "(\"CoverUrl\" IS NULL AND \"CoverSource\" = 0) OR (\"CoverUrl\" IS NOT NULL AND \"CoverSource\" <> 0)");
                });
            });

            modelBuilder.Entity<JitenCatalogueRefreshReceipt>(entity =>
            {
                entity.HasKey(receipt => receipt.Id);
                entity.Property(receipt => receipt.ReviewFingerprint).HasMaxLength(128);
                entity.HasIndex(receipt => receipt.MediaSeriesId);
                entity.ToTable(table =>
                {
                    table.HasCheckConstraint("CK_JitenCatalogueRefreshReceipts_Deck", "\"JitenDeckId\" > 0");
                    table.HasCheckConstraint("CK_JitenCatalogueRefreshReceipts_Fingerprint",
                        "length(trim(\"ReviewFingerprint\")) BETWEEN 1 AND 128");
                });
            });

            modelBuilder.Entity<MediaWork>(entity =>
            {
                entity.Property(work => work.MediaType)
                    .HasDefaultValue(MediaType.Book);
                entity.Property(work => work.CoverUrl)
                    .HasMaxLength(2048);
                entity.Property(work => work.CoverSource)
                    .HasDefaultValue(MediaCoverSource.None);
                entity.Property(work => work.CoverProviderItemId)
                    .HasMaxLength(128);
                entity.Property(work => work.Version)
                    .IsConcurrencyToken()
                    .HasDefaultValue(Guid.Empty);

                entity.HasIndex(work => work.MediaSeriesId);
                entity.HasIndex(work => work.MediaInstallmentId);
                entity.HasIndex(work => work.JitenDeckId);
                entity.HasIndex(work => work.JitenSubdeckId);

                entity.HasOne(work => work.MediaSeries)
                    .WithMany(series => series.Works)
                    .HasForeignKey(work => work.MediaSeriesId)
                    .OnDelete(DeleteBehavior.SetNull);

                entity.HasOne(work => work.MediaInstallment)
                    .WithMany(installment => installment.Copies)
                    .HasForeignKey(work => work.MediaInstallmentId)
                    .OnDelete(DeleteBehavior.Restrict);

                entity.ToTable(table =>
                {
                    table.HasCheckConstraint(
                        "CK_MediaWorks_JitenSubdeckRequiresDeck",
                        "\"JitenSubdeckId\" IS NULL OR \"JitenDeckId\" IS NOT NULL");
                    table.HasCheckConstraint(
                        "CK_MediaWorks_TtsuCharacterCount",
                        "\"TtsuCharacterCount\" IS NULL OR \"TtsuCharacterCount\" > 0");
                    table.HasCheckConstraint(
                        "CK_MediaWorks_CoverUrlAndSource",
                        "(\"CoverUrl\" IS NULL AND \"CoverSource\" = 0) OR (\"CoverUrl\" IS NOT NULL AND \"CoverSource\" <> 0)");
                    table.HasCheckConstraint(
                        "CK_MediaWorks_CoverProviderItemId",
                        "(\"CoverSource\" IN (5, 6) AND \"CoverProviderItemId\" IS NOT NULL) OR (\"CoverSource\" NOT IN (5, 6) AND \"CoverProviderItemId\" IS NULL)");
                });
            });
        }
    }
}
