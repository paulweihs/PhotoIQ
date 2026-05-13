using Microsoft.EntityFrameworkCore;
using PhotoWell.Core.Models;

namespace PhotoWell.Data;

/// <summary>
/// Entity Framework Core context for PhotoWell's SQLite database.
/// </summary>
/// <remarks>
/// This context manages all entities in the system: MediaFile (photos/videos), Tag (AI and user tags),
/// Face (detected faces), Person (recognized people), Library (photo libraries), Album (user-created albums),
/// ExclusionRule (folder exclusions for import), and AnalysisMetric (performance tracking).
///
/// Key relationships:
/// - MediaFile ↔ Tag: Many-to-many (auto join table)
/// - MediaFile ↔ Album: Many-to-many (via photo_albums table)
/// - MediaFile → Face: One-to-many
/// - Face → Person: Many-to-one
/// - Album → Library: Many-to-one (with cascade delete)
///
/// Indexes are configured on frequently-queried columns: FilePath (unique), AnalysisStatus,
/// DateTaken, IsFavorite, and geolocation (Latitude, Longitude) for spatial queries.
/// </remarks>
public class PhotoWellContext : DbContext
{
    /// <summary>
    /// Initializes a new instance of the PhotoWellContext.
    /// </summary>
    /// <param name="options">EF Core options (connection string, logging, etc.).</param>
    public PhotoWellContext(DbContextOptions<PhotoWellContext> options) : base(options) { }

    /// <summary>Gets the DbSet for MediaFile entities (photos and videos).</summary>
    public DbSet<MediaFile>      MediaFiles      => Set<MediaFile>();
    /// <summary>Gets the DbSet for Tag entities (AI-generated and user tags).</summary>
    public DbSet<Tag>            Tags            => Set<Tag>();
    /// <summary>Gets the DbSet for Face entities (detected faces from AI analysis).</summary>
    public DbSet<Face>           Faces           => Set<Face>();
    /// <summary>Gets the DbSet for Person entities (recognized people across photos).</summary>
    public DbSet<Person>         People          => Set<Person>();
    /// <summary>Gets the DbSet for Library entities (user libraries for organizing photos).</summary>
    public DbSet<Library>        Libraries       => Set<Library>();
    /// <summary>Gets the DbSet for Album entities (albums within libraries).</summary>
    public DbSet<Album>          Albums          => Set<Album>();
    /// <summary>Gets the DbSet for ExclusionRule entities (folder exclusions during import scan).</summary>
    public DbSet<ExclusionRule>  ExclusionRules  { get; set; }
    /// <summary>Gets the DbSet for AnalysisMetric entities (performance tracking for CLIP/LLaVA).</summary>
    public DbSet<AnalysisMetric> AnalysisMetrics => Set<AnalysisMetric>();

    /// <summary>
    /// Configures the EF Core data model: keys, indexes, relationships, and cascade delete behavior.
    /// </summary>
    /// <remarks>
    /// This method is called once during context initialization. It establishes:
    /// - Primary keys for all entities
    /// - Indexes: FilePath (unique, for deduplication), AnalysisStatus/DateTaken/IsFavorite (for filtering), Latitude/Longitude (for geo queries)
    /// - Many-to-many relationships: MediaFile ↔ Tag, MediaFile ↔ Album (via photo_albums join table)
    /// - One-to-many relationships: MediaFile → Face, Face → Person, Library → Album
    /// - Cascade delete: Album to its parent Library
    /// </remarks>
    /// <param name="modelBuilder">EF Core model builder for fluent API configuration.</param>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MediaFile>().HasKey(e => e.Id);
        modelBuilder.Entity<MediaFile>().HasIndex(e => e.FilePath).IsUnique();
        modelBuilder.Entity<MediaFile>().HasIndex(e => e.AnalysisStatus);
        modelBuilder.Entity<MediaFile>().HasIndex(e => e.DateTaken);
        modelBuilder.Entity<MediaFile>().HasIndex(e => e.IsFavorite);
        modelBuilder.Entity<MediaFile>().HasIndex(e => new { e.Latitude, e.Longitude });
        // Performance optimization: composite indexes for common filtering patterns
        modelBuilder.Entity<MediaFile>().HasIndex(e => new { e.IsExcluded, e.AnalysisStatus });
        modelBuilder.Entity<MediaFile>().HasIndex(e => new { e.IsExcluded, e.IsAnalyzed, e.AiModelUsed });
        modelBuilder.Entity<MediaFile>().HasMany(m => m.Tags).WithMany(t => t.MediaFiles);
        modelBuilder.Entity<MediaFile>()
            .HasMany(m => m.Albums)
            .WithMany(a => a.MediaFiles)
            .UsingEntity(j => j.ToTable("photo_albums"));

        modelBuilder.Entity<Tag>().HasKey(e => e.Id);
        modelBuilder.Entity<Tag>().HasIndex(e => e.NormalizedName).IsUnique();

        modelBuilder.Entity<Face>().HasKey(e => e.Id);
        modelBuilder.Entity<Face>().HasOne(f => f.MediaFile).WithMany(m => m.Faces).HasForeignKey(f => f.MediaFileId);
        modelBuilder.Entity<Face>().HasOne(f => f.Person).WithMany(p => p.Faces).HasForeignKey(f => f.PersonId);

        modelBuilder.Entity<Person>().HasKey(e => e.Id);

        modelBuilder.Entity<Library>().HasKey(e => e.Id);
        modelBuilder.Entity<Album>().HasKey(e => e.Id);
        modelBuilder.Entity<Album>()
            .HasOne(a => a.Library)
            .WithMany(l => l.Albums)
            .HasForeignKey(a => a.LibraryId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
