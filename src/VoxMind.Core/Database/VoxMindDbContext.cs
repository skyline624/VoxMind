using Microsoft.EntityFrameworkCore;

namespace VoxMind.Core.Database;

public class VoxMindDbContext : DbContext
{
    public DbSet<SpeakerProfileEntity> SpeakerProfiles => Set<SpeakerProfileEntity>();
    public DbSet<SpeakerEmbeddingEntity> SpeakerEmbeddings => Set<SpeakerEmbeddingEntity>();
    public DbSet<ListeningSessionEntity> ListeningSessions => Set<ListeningSessionEntity>();
    public DbSet<SessionSegmentEntity> SessionSegments => Set<SessionSegmentEntity>();
    public DbSet<SessionSummaryEntity> SessionSummaries => Set<SessionSummaryEntity>();

    public VoxMindDbContext(DbContextOptions<VoxMindDbContext> options) : base(options) { }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
            optionsBuilder.UseSqlite("Data Source=voxmind.sqlite");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // SpeakerProfiles
        modelBuilder.Entity<SpeakerProfileEntity>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Name).IsRequired();
            e.HasIndex(p => p.LastSeenAt).HasDatabaseName("idx_profiles_lastseen");
        });

        // SpeakerEmbeddings
        modelBuilder.Entity<SpeakerEmbeddingEntity>(e =>
        {
            e.HasKey(em => em.Id);
            e.HasOne(em => em.Profile)
             .WithMany(p => p.Embeddings)
             .HasForeignKey(em => em.ProfileId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(em => em.ProfileId).HasDatabaseName("idx_embeddings_profile");
        });

        // ListeningSessions
        modelBuilder.Entity<ListeningSessionEntity>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.StartedAt).HasDatabaseName("idx_sessions_started");
        });

        // SessionSegments
        modelBuilder.Entity<SessionSegmentEntity>(e =>
        {
            e.HasKey(seg => seg.Id);
            e.HasOne(seg => seg.Session)
             .WithMany(s => s.Segments)
             .HasForeignKey(seg => seg.SessionId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(seg => seg.SessionId).HasDatabaseName("idx_segments_session");
        });

        // SessionSummaries
        modelBuilder.Entity<SessionSummaryEntity>(e =>
        {
            e.HasKey(sum => sum.Id);
            e.HasOne(sum => sum.Session)
             .WithOne(s => s.Summary)
             .HasForeignKey<SessionSummaryEntity>(sum => sum.SessionId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(sum => sum.SessionId).IsUnique();
        });
    }
}
