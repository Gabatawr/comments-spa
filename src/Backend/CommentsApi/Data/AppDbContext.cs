using CommentsApi.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommentsApi.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<Attachment> Attachments => Set<Attachment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var comment = modelBuilder.Entity<Comment>();
        comment.ToTable("Comments");
        comment.HasKey(c => c.Id);
        comment.Property(c => c.Id).ValueGeneratedOnAdd();
        comment.Property(c => c.UserName).IsRequired();
        comment.Property(c => c.Email).IsRequired();
        comment.Property(c => c.TextHtml).IsRequired();
        comment.Property(c => c.TextPlain).IsRequired();
        comment.Property(c => c.CreatedAt).IsRequired();

        comment.HasIndex(c => c.ParentId);
        comment.HasIndex(c => c.CreatedAt);
        comment.HasIndex(c => c.UserName);
        comment.HasIndex(c => c.Email);

        comment.HasOne(c => c.Parent)
            .WithMany(c => c.Replies)
            .HasForeignKey(c => c.ParentId)
            .OnDelete(DeleteBehavior.Restrict);

        comment.HasOne(c => c.Attachment)
            .WithMany()
            .HasForeignKey(c => c.AttachmentId)
            .OnDelete(DeleteBehavior.SetNull);

        var attachment = modelBuilder.Entity<Attachment>();
        attachment.ToTable("Attachments");
        attachment.HasKey(a => a.Id);
        attachment.Property(a => a.Id).ValueGeneratedOnAdd();
        attachment.Property(a => a.FileName).IsRequired();
        attachment.Property(a => a.StoredName).IsRequired();
        attachment.Property(a => a.StoragePath).IsRequired();
        attachment.Property(a => a.ContentType).IsRequired();
        attachment.Property(a => a.Kind).IsRequired();
        attachment.Property(a => a.Sha256).IsRequired();
        attachment.HasIndex(a => a.Kind);

        base.OnModelCreating(modelBuilder);
    }
}
