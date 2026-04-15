using Microsoft.EntityFrameworkCore;
using PhotoIQPro.Core.Models;

namespace PhotoIQPro.Data;

public class PhotoIQContext : DbContext
{
    public PhotoIQContext(DbContextOptions<PhotoIQContext> options) : base(options) { }

    public DbSet<MediaFile>      MediaFiles      => Set<MediaFile>();
    public DbSet<Tag>            Tags            => Set<Tag>();
    public DbSet<Face>           Faces           => Set<Face>();
    public DbSet<Person>         People          => Set<Person>();
    public DbSet<Library>        Libraries       => Set<Library>();
    public DbSet<Album>          Albums          => Set<Album>();
    public DbSet<ExclusionRule>  ExclusionRules  { get; set; }
    public DbSet<AnalysisMetric> AnalysisMetrics => Set<AnalysisMetric>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MediaFile>().HasKey(e => e.Id);
        modelBuilder.Entity<MediaFile>().HasIndex(e => e.FilePath).IsUnique();
        modelBuilder.Entity<MediaFile>().HasIndex(e => e.AnalysisStatus);
        modelBuilder.Entity<MediaFile>().HasIndex(e => e.DateTaken);
        modelBuilder.Entity<MediaFile>().HasIndex(e => e.IsFavorite);
        modelBuilder.Entity<MediaFile>().HasIndex(e => new { e.Latitude, e.Longitude });
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
