using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSolar.Modules.Identity.Entities;

namespace SmartSolar.Infrastructure.Persistence.Configurations.Identity;

public sealed class UserRoleConfiguration
    : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> builder)
    {
        builder.ToTable("user_role");

        builder.HasKey(x => new { x.UserId, x.RoleId });

        builder.Property(x => x.UserId)
            .HasColumnName("user_id");

        builder.Property(x => x.RoleId)
            .HasColumnName("role_id");

        builder.Property(x => x.AssignedAt)
            .HasColumnName("assigned_at")
            .IsRequired();

        builder.Property(x => x.AssignedBy)
            .HasColumnName("assigned_by")
            .IsRequired(false);

        builder.HasOne(x => x.User)
            .WithMany(x => x.UserRoles)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Role)
            .WithMany(x => x.UserRoles)
            .HasForeignKey(x => x.RoleId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
