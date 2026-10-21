using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Identity.Entities;

namespace SmartSolar.Infrastructure.Persistence.Configurations.Files;

public sealed class StoredFileConfiguration : IEntityTypeConfiguration<StoredFile>
{
    public void Configure(EntityTypeBuilder<StoredFile> builder)
    {
        builder.ToTable("file_reference", t =>
        {
            t.HasCheckConstraint("ck_file_reference_status", $"status IN ('{FileStatuses.PendingVerification}', '{FileStatuses.Verified}', '{FileStatuses.Rejected}')");
            t.HasCheckConstraint("ck_file_reference_purpose", $"purpose IN ({string.Join(", ", FilePurposes.All.Select(p => $"'{p}'"))})");
            t.HasCheckConstraint("ck_file_reference_sizes", "declared_size_bytes > 0 AND (verified_size_bytes IS NULL OR verified_size_bytes > 0)");
            // Every purpose gets a backend-computed SHA-256: a VERIFIED row without one is impossible. The
            // object version is enforced by the application (Storage:S3:RequireObjectVersioning), not here,
            // because stores without versioning are a supported configuration.
            t.HasCheckConstraint("ck_file_reference_verified_complete",
                $"status <> '{FileStatuses.Verified}' OR (verified_content_type IS NOT NULL AND verified_size_bytes IS NOT NULL AND verified_at IS NOT NULL"
                + " AND verified_checksum_sha256 IS NOT NULL)");
            t.HasCheckConstraint("ck_file_reference_checksum_format",
                "(verified_checksum_sha256 IS NULL OR (length(verified_checksum_sha256) = 64 AND verified_checksum_sha256 = lower(verified_checksum_sha256)))"
                + " AND (declared_checksum_sha256 IS NULL OR (length(declared_checksum_sha256) = 64 AND declared_checksum_sha256 = lower(declared_checksum_sha256)))");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.Purpose).HasColumnName("purpose").HasMaxLength(40).IsRequired();
        builder.Property(x => x.Provider).HasColumnName("provider").HasMaxLength(20).IsRequired();
        builder.Property(x => x.Bucket).HasColumnName("bucket").HasMaxLength(100).IsRequired();
        builder.Property(x => x.ObjectKey).HasColumnName("object_key").HasMaxLength(512).IsRequired();
        builder.Property(x => x.OriginalFileName).HasColumnName("original_file_name").HasMaxLength(255).IsRequired();
        builder.Property(x => x.DeclaredContentType).HasColumnName("declared_content_type").HasMaxLength(100).IsRequired();
        builder.Property(x => x.DeclaredSizeBytes).HasColumnName("declared_size_bytes").IsRequired();
        builder.Property(x => x.DeclaredChecksumSha256).HasColumnName("declared_checksum_sha256").HasMaxLength(64);
        builder.Property(x => x.ObjectVersionId).HasColumnName("object_version_id").HasMaxLength(255);
        builder.Property(x => x.ETag).HasColumnName("etag").HasMaxLength(255);
        builder.Property(x => x.VerifiedContentType).HasColumnName("verified_content_type").HasMaxLength(100);
        builder.Property(x => x.VerifiedSizeBytes).HasColumnName("verified_size_bytes");
        builder.Property(x => x.VerifiedChecksumSha256).HasColumnName("verified_checksum_sha256").HasMaxLength(64);
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(30).IsRequired();
        builder.Property(x => x.RejectionReason).HasColumnName("rejection_reason").HasMaxLength(300);
        builder.Property(x => x.VerificationAttempts).HasColumnName("verification_attempts").IsRequired();
        builder.Property(x => x.UploadedByUserId).HasColumnName("uploaded_by_user_id").IsRequired();
        builder.Property(x => x.VerifiedAt).HasColumnName("verified_at");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(x => x.Revision).HasColumnName("revision").IsConcurrencyToken().IsRequired();
        builder.HasIndex(x => new { x.Bucket, x.ObjectKey }).IsUnique().HasDatabaseName("ux_file_reference_bucket_object_key");
        builder.HasIndex(x => new { x.UploadedByUserId, x.CreatedAt }).HasDatabaseName("ix_file_reference_uploader_created_at");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UploadedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
