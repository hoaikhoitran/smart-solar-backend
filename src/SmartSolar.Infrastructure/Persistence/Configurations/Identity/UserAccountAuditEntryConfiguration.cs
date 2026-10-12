using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSolar.Modules.Identity.Entities;

namespace SmartSolar.Infrastructure.Persistence.Configurations.Identity;

public sealed class UserAccountAuditEntryConfiguration : IEntityTypeConfiguration<UserAccountAuditEntry>
{
    public void Configure(EntityTypeBuilder<UserAccountAuditEntry> builder)
    {
        builder.ToTable("user_account_audit");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.TargetUserId).HasColumnName("target_user_id").IsRequired();
        builder.Property(x => x.ActorUserId).HasColumnName("actor_user_id").IsRequired(false);
        builder.Property(x => x.Action).HasColumnName("action").HasMaxLength(50).IsRequired();
        builder.Property(x => x.Detail).HasColumnName("detail").HasMaxLength(500).IsRequired(false);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.HasIndex(x => new { x.TargetUserId, x.CreatedAt }).HasDatabaseName("ix_user_account_audit_target_created_at");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.TargetUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
