using AnimeIndex.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AnimeIndex.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Series> Series => Set<Series>();
    public DbSet<Episode> Episodes => Set<Episode>();
    public DbSet<Mirror> Mirrors => Set<Mirror>();
    public DbSet<Genre> Genres => Set<Genre>();
    public DbSet<SeriesGenre> SeriesGenres => Set<SeriesGenre>();
    public DbSet<BlockedSlug> BlockedSlugs => Set<BlockedSlug>();
    public DbSet<ScrapeJob> ScrapeJobs => Set<ScrapeJob>();
    public DbSet<WatchProgress> WatchProgress => Set<WatchProgress>();
    public DbSet<InstagramPost> InstagramPosts => Set<InstagramPost>();
    public DbSet<DiscordPost> DiscordPosts => Set<DiscordPost>();
    public DbSet<TelegramPost> TelegramPosts => Set<TelegramPost>();
    public DbSet<AnimeNewsItem> AnimeNewsItems => Set<AnimeNewsItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ─── Series ──────────────────────────────────────
        modelBuilder.Entity<Series>(e =>
        {
            e.ToTable("series");
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(s => s.Slug).IsRequired();
            e.HasIndex(s => s.Slug).IsUnique();
            e.Property(s => s.Title).IsRequired();
            e.Property(s => s.Score).HasColumnType("numeric(4,2)");
            e.Property(s => s.Metadata).HasColumnType("jsonb").HasDefaultValueSql("'{}'");
            e.Property(s => s.CreatedAt).HasDefaultValueSql("now()");
            e.Property(s => s.UpdatedAt).HasDefaultValueSql("now()");

            // Status check constraint
            e.ToTable(t => t.HasCheckConstraint("CK_series_status",
                "status IN ('ongoing','completed','upcoming','hiatus')"));

            // Type check constraint
            e.ToTable(t => t.HasCheckConstraint("CK_series_type",
                "type IN ('tv','movie','ova','ona','special')"));

            // Indexes
            e.HasIndex(s => s.Slug).HasDatabaseName("idx_series_slug");
            e.HasIndex(s => new { s.Status, s.Year, s.Score })
                .HasDatabaseName("idx_series_status_year")
                .IsDescending(false, true, true);
        });

        // ─── Episode ─────────────────────────────────────
        modelBuilder.Entity<Episode>(e =>
        {
            e.ToTable("episodes");
            e.HasKey(ep => ep.Id);
            e.Property(ep => ep.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(ep => ep.EpisodeNumber).IsRequired();
            e.Property(ep => ep.IsPublished).HasDefaultValue(false);
            e.Property(ep => ep.CreatedAt).HasDefaultValueSql("now()");

            e.HasIndex(ep => new { ep.SeriesId, ep.EpisodeNumber })
                .IsUnique()
                .HasDatabaseName("idx_episodes_series");

            e.HasOne(ep => ep.Series)
                .WithMany(s => s.Episodes)
                .HasForeignKey(ep => ep.SeriesId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ─── Mirror ──────────────────────────────────────
        modelBuilder.Entity<Mirror>(e =>
        {
            e.ToTable("mirrors");
            e.HasKey(m => m.Id);
            e.Property(m => m.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(m => m.ProviderName).IsRequired();
            e.Property(m => m.EmbedUrl).IsRequired();
            e.Property(m => m.QualityLabel).HasDefaultValue((short)720);
            e.Property(m => m.Priority).HasDefaultValue((short)0);
            e.Property(m => m.IsActive).HasDefaultValue(true);
            e.Property(m => m.ConsecutiveFailures).HasDefaultValue((short)0);
            e.Property(m => m.CreatedAt).HasDefaultValueSql("now()");

            e.HasIndex(m => new { m.EpisodeId, m.EmbedUrl })
                .IsUnique();

            // Partial index: active mirrors ordered by priority
            e.HasIndex(m => new { m.EpisodeId, m.Priority })
                .HasDatabaseName("idx_mirrors_episode_active")
                .HasFilter("is_active = true");

            e.HasOne(m => m.Episode)
                .WithMany(ep => ep.Mirrors)
                .HasForeignKey(m => m.EpisodeId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ─── Genre ───────────────────────────────────────
        modelBuilder.Entity<Genre>(e =>
        {
            e.ToTable("genres");
            e.HasKey(g => g.Id);
            e.Property(g => g.Id).UseIdentityAlwaysColumn();
            e.Property(g => g.Name).IsRequired();
            e.HasIndex(g => g.Name).IsUnique();
        });

        // ─── SeriesGenre (join table) ────────────────────
        modelBuilder.Entity<SeriesGenre>(e =>
        {
            e.ToTable("series_genres");
            e.HasKey(sg => new { sg.SeriesId, sg.GenreId });

            e.HasOne(sg => sg.Series)
                .WithMany(s => s.SeriesGenres)
                .HasForeignKey(sg => sg.SeriesId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(sg => sg.Genre)
                .WithMany(g => g.SeriesGenres)
                .HasForeignKey(sg => sg.GenreId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ─── BlockedSlug ─────────────────────────────────
        modelBuilder.Entity<BlockedSlug>(e =>
        {
            e.ToTable("blocked_slugs");
            e.HasKey(b => b.Slug);
            e.Property(b => b.BlockedAt).HasDefaultValueSql("now()");
        });

        // ─── ScrapeJob ──────────────────────────────────
        modelBuilder.Entity<ScrapeJob>(e =>
        {
            e.ToTable("scrape_jobs");
            e.HasKey(j => j.Id);
            e.Property(j => j.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(j => j.JobType).IsRequired();
            e.Property(j => j.Status).HasDefaultValue("pending");
            e.Property(j => j.AttemptCount).HasDefaultValue((short)0);
            e.Property(j => j.ScheduledAt).HasDefaultValueSql("now()");
            e.Property(j => j.ProgressMessage).HasMaxLength(500);

            e.ToTable(t => t.HasCheckConstraint("CK_scrape_jobs_status",
                "status IN ('pending','running','completed','failed','dead_letter')"));

            e.HasOne(j => j.Series)
                .WithMany()
                .HasForeignKey(j => j.SeriesId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // ─── WatchProgress ───────────────────────────────
        modelBuilder.Entity<WatchProgress>(e =>
        {
            e.ToTable("watch_progress");
            e.HasKey(w => new { w.DeviceId, w.EpisodeId });
            e.Property(w => w.SeriesSlug).IsRequired();
            e.Property(w => w.UpdatedAt).HasDefaultValueSql("now()");
            e.Property(w => w.Completed).HasDefaultValue(false);

            e.HasIndex(w => new { w.DeviceId, w.UpdatedAt })
                .IsDescending(false, true)
                .HasDatabaseName("idx_watch_progress_device_updated");

            e.HasOne(w => w.Episode)
                .WithMany()
                .HasForeignKey(w => w.EpisodeId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ─── InstagramPost ────────────────────────────────
        modelBuilder.Entity<InstagramPost>(e =>
        {
            e.ToTable("instagram_posts");
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(p => p.PostType).IsRequired();
            e.Property(p => p.Status).IsRequired().HasDefaultValue("published");
            e.Property(p => p.CreatedAt).HasDefaultValueSql("now()");

            e.ToTable(t => t.HasCheckConstraint("CK_instagram_posts_type",
                "post_type IN ('story','feed','carousel_item')"));
            e.ToTable(t => t.HasCheckConstraint("CK_instagram_posts_status",
                "status IN ('published','failed','skipped')"));

            e.HasIndex(p => p.EpisodeId).HasDatabaseName("idx_instagram_posts_episode");
            e.HasIndex(p => p.CreatedAt).HasDatabaseName("idx_instagram_posts_created");

            e.HasOne(p => p.Episode)
                .WithMany()
                .HasForeignKey(p => p.EpisodeId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ─── TelegramPost ─────────────────────────────────
        modelBuilder.Entity<TelegramPost>(e =>
        {
            e.ToTable("telegram_posts");
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(p => p.Status).IsRequired().HasDefaultValue("published");
            e.Property(p => p.CreatedAt).HasDefaultValueSql("now()");

            e.ToTable(t => t.HasCheckConstraint("CK_telegram_posts_status",
                "status IN ('published','failed')"));

            e.HasIndex(p => p.EpisodeId).HasDatabaseName("idx_telegram_posts_episode");
            e.HasIndex(p => p.CreatedAt).HasDatabaseName("idx_telegram_posts_created");

            e.HasOne(p => p.Episode)
                .WithMany()
                .HasForeignKey(p => p.EpisodeId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ─── DiscordPost ──────────────────────────────────
        modelBuilder.Entity<DiscordPost>(e =>
        {
            e.ToTable("discord_posts");
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(p => p.Status).IsRequired().HasDefaultValue("published");
            e.Property(p => p.CreatedAt).HasDefaultValueSql("now()");

            e.ToTable(t => t.HasCheckConstraint("CK_discord_posts_status",
                "status IN ('published','failed')"));

            e.HasIndex(p => p.EpisodeId).HasDatabaseName("idx_discord_posts_episode");
            e.HasIndex(p => p.CreatedAt).HasDatabaseName("idx_discord_posts_created");

            e.HasOne(p => p.Episode)
                .WithMany()
                .HasForeignKey(p => p.EpisodeId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ─── AnimeNewsItem ────────────────────────────────
        modelBuilder.Entity<AnimeNewsItem>(e =>
        {
            e.ToTable("anime_news_items");
            e.HasKey(n => n.Id);
            e.Property(n => n.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(n => n.SourceKey).IsRequired();
            e.Property(n => n.RssGuid).IsRequired();
            e.Property(n => n.Title).IsRequired();
            e.Property(n => n.ArticleUrl).IsRequired();
            e.Property(n => n.IgPostStatus).IsRequired().HasDefaultValue("pending");
            e.Property(n => n.FetchedAt).HasDefaultValueSql("now()");

            e.ToTable(t => t.HasCheckConstraint("CK_anime_news_items_status",
                "ig_post_status IN ('pending','published','skipped','failed')"));

            e.HasIndex(n => new { n.SourceKey, n.RssGuid })
                .IsUnique()
                .HasDatabaseName("uq_anime_news_source_guid");
            e.HasIndex(n => n.IgPostStatus).HasDatabaseName("idx_anime_news_ig_status");
            e.HasIndex(n => n.PublishedAt).HasDatabaseName("idx_anime_news_published_at");
        });

        // Snake case naming convention for all columns
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }
        }
    }

    private static string ToSnakeCase(string name)
    {
        return string.Concat(name.Select((c, i) =>
            i > 0 && char.IsUpper(c) ? "_" + char.ToLower(c) : char.ToLower(c).ToString()));
    }
}
