using Microsoft.EntityFrameworkCore;
using PhotoIQPro.Core.Models;

namespace PhotoIQPro.Data;

public class PhotoIQContext : DbContext
{
    public PhotoIQContext(DbContextOptions<PhotoIQContext> options) : base(options) { }

    public DbSet<MediaFile> MediaFiles => Set<MediaFile>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<Face> Faces => Set<Face>();
    public DbSet<Person> People => Set<Person>();
    public DbSet<Collection> Collections => Set<Collection>();
    public DbSet<ExclusionRule> ExclusionRules { get; set; }
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MediaFile>().HasKey(e => e.Id);
        modelBuilder.Entity<MediaFile>().HasIndex(e => e.FilePath).IsUnique();
        modelBuilder.Entity<MediaFile>().HasMany(m => m.Tags).WithMany(t => t.MediaFiles);
        modelBuilder.Entity<MediaFile>().HasMany(m => m.Collections).WithMany(c => c.MediaFiles);

        modelBuilder.Entity<Tag>().HasKey(e => e.Id);
        modelBuilder.Entity<Tag>().HasIndex(e => e.NormalizedName).IsUnique();

        modelBuilder.Entity<Face>().HasKey(e => e.Id);
        modelBuilder.Entity<Face>().HasOne(f => f.MediaFile).WithMany(m => m.Faces).HasForeignKey(f => f.MediaFileId);
        modelBuilder.Entity<Face>().HasOne(f => f.Person).WithMany(p => p.Faces).HasForeignKey(f => f.PersonId);

        modelBuilder.Entity<Person>().HasKey(e => e.Id);
        modelBuilder.Entity<Collection>().HasKey(e => e.Id);
        modelBuilder.Entity<Collection>().HasOne(c => c.Parent).WithMany(c => c.Children).HasForeignKey(c => c.ParentId);
    }
}
