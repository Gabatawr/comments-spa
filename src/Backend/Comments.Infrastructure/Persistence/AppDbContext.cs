using Comments.Domain;
using Microsoft.EntityFrameworkCore;

namespace Comments.Infrastructure.Persistence;

/// <summary>
/// EF Core model over PostgreSQL (Npgsql). Table/column names, types, FK actions and indexes
/// follow docs/API-v2.md §8. Case-insensitive userName/email ordering uses <c>lower(value)</c>
/// (never a collation), backed by functional indexes declared in the initial migration.
/// </summary>
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
        comment.ToTable("comments");
        comment.HasKey(c => c.Id);
        comment.Property(c => c.Id)
            .HasColumnName("id")
            .HasColumnType("bigint")
            .UseIdentityByDefaultColumn();
        comment.Property(c => c.ParentId).HasColumnName("parent_id").HasColumnType("bigint");
        comment.Property(c => c.UserName).HasColumnName("user_name").HasMaxLength(50).IsRequired();
        comment.Property(c => c.Email).HasColumnName("email").HasMaxLength(100).IsRequired();
        comment.Property(c => c.HomePage).HasColumnName("home_page").HasMaxLength(200);
        comment.Property(c => c.TextHtml).HasColumnName("text_html").HasColumnType("text").IsRequired();
        comment.Property(c => c.TextPlain).HasColumnName("text_plain").HasColumnType("text").IsRequired();
        comment.Property(c => c.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").IsRequired();
        comment.Property(c => c.ClientIp).HasColumnName("client_ip").HasMaxLength(64);
        comment.Property(c => c.UserAgent).HasColumnName("user_agent").HasMaxLength(512);
        comment.Property(c => c.AttachmentId).HasColumnName("attachment_id").HasColumnType("bigint");

        comment.HasIndex(c => c.ParentId).HasDatabaseName("ix_comments_parent_id");
        comment.HasIndex(c => new { c.CreatedAt, c.Id })
            .HasDatabaseName("ix_comments_created_at_id")
            .IsDescending(true, true);
        comment.HasIndex(c => new { c.ParentId, c.CreatedAt, c.Id })
            .HasDatabaseName("ix_comments_parent_created");

        comment.HasOne(c => c.Parent)
            .WithMany(c => c.Replies)
            .HasForeignKey(c => c.ParentId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_comments_parent_id");

        comment.HasOne(c => c.Attachment)
            .WithMany()
            .HasForeignKey(c => c.AttachmentId)
            .OnDelete(DeleteBehavior.SetNull)
            .HasConstraintName("fk_comments_attachment_id");

        var attachment = modelBuilder.Entity<Attachment>();
        attachment.ToTable("attachments");
        attachment.HasKey(a => a.Id);
        attachment.Property(a => a.Id)
            .HasColumnName("id")
            .HasColumnType("bigint")
            .UseIdentityByDefaultColumn();
        attachment.Property(a => a.FileName).HasColumnName("file_name").HasMaxLength(260).IsRequired();
        attachment.Property(a => a.StoredName).HasColumnName("stored_name").HasMaxLength(260).IsRequired();
        attachment.Property(a => a.StoragePath).HasColumnName("storage_path").HasMaxLength(512).IsRequired();
        attachment.Property(a => a.ContentType).HasColumnName("content_type").HasMaxLength(100).IsRequired();
        attachment.Property(a => a.Kind).HasColumnName("kind").HasMaxLength(10).IsRequired();
        attachment.Property(a => a.SizeBytes).HasColumnName("size_bytes").HasColumnType("bigint").IsRequired();
        attachment.Property(a => a.Width).HasColumnName("width");
        attachment.Property(a => a.Height).HasColumnName("height");
        attachment.Property(a => a.OriginalWidth).HasColumnName("original_width");
        attachment.Property(a => a.OriginalHeight).HasColumnName("original_height");
        attachment.Property(a => a.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").IsRequired();
        attachment.Property(a => a.Sha256).HasColumnName("sha256").HasMaxLength(64).IsRequired();

        attachment.HasIndex(a => a.Kind).HasDatabaseName("ix_attachments_kind");
        attachment.HasIndex(a => a.Sha256).HasDatabaseName("ix_attachments_sha256");

        base.OnModelCreating(modelBuilder);
    }
}
