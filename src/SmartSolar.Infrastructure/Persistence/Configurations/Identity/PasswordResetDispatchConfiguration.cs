using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.Identity.PasswordRecovery;

namespace SmartSolar.Infrastructure.Persistence.Configurations.Identity;

public sealed class PasswordResetDispatchConfiguration : IEntityTypeConfiguration<PasswordResetDispatch>
{
    public void Configure(EntityTypeBuilder<PasswordResetDispatch> builder)
    {
        builder.ToTable("password_reset_dispatch", t =>
        {
            t.HasCheckConstraint("ck_password_reset_dispatch_status", $"status IN ({string.Join(", ", PasswordResetDispatchStatuses.All.Select(s => $"'{s}'"))})");
            // The raw-token URL exists only while the email still has to be published.
            t.HasCheckConstraint("ck_password_reset_dispatch_url", $"(status = '{PasswordResetDispatchStatuses.Pending}') = (reset_url IS NOT NULL)");
            t.HasCheckConstraint("ck_password_reset_dispatch_attempts", "publish_attempts >= 0");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(x => x.ActionTokenId).HasColumnName("action_token_id").IsRequired();
        builder.Property(x => x.Email).HasColumnName("email").HasMaxLength(320).IsRequired();
        builder.Property(x => x.FullName).HasColumnName("full_name").HasMaxLength(200).IsRequired();
        builder.Property(x => x.ResetUrl).HasColumnName("reset_url").HasMaxLength(2048);
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        builder.Property(x => x.PublishAttempts).HasColumnName("publish_attempts").IsRequired();
        builder.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at").IsRequired();
        builder.Property(x => x.LastError).HasColumnName("last_error").HasMaxLength(200);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.ExpiresAt).HasColumnName("expires_at").IsRequired();
        builder.Property(x => x.PublishedAt).HasColumnName("published_at");
        builder.Property(x => x.SentAt).HasColumnName("sent_at");
        builder.HasIndex(x => new { x.Status, x.NextAttemptAt }).HasDatabaseName("ix_password_reset_dispatch_status_next_attempt");
        builder.HasIndex(x => x.ActionTokenId).IsUnique().HasDatabaseName("ux_password_reset_dispatch_action_token");
        builder.HasIndex(x => x.UserId).HasDatabaseName("ix_password_reset_dispatch_user");
        // A newer request replaces the unused token; its pending email goes with it, so a superseded link is never mailed.
        builder.HasOne<AuthActionToken>().WithMany().HasForeignKey(x => x.ActionTokenId).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_password_reset_dispatch_action_token");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_password_reset_dispatch_user");
    }
}
