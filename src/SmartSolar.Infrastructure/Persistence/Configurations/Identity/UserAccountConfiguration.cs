using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.Identity.Enums;

namespace SmartSolar.Infrastructure.Persistence.Configurations.Identity;

public sealed class UserAccountConfiguration
    : IEntityTypeConfiguration<UserAccount>
{
    private const string PendingVerification = "PENDING_VERIFICATION";
    private const string Active = "ACTIVE";
    private const string Suspended = "SUSPENDED";
    private const string Disabled = "DISABLED";

    public void Configure(EntityTypeBuilder<UserAccount> builder)
    {
        builder.ToTable("user_account");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasColumnName("id");

        builder.Property(x => x.Email)
            .HasColumnName("email")
            .HasMaxLength(255)
            .IsRequired();

        builder.HasIndex(x => x.Email)
            .IsUnique();

        builder.Property(x => x.FullName)
            .HasColumnName("full_name")
            .HasMaxLength(255)
            .IsRequired();

        builder.Property(x => x.Status)
            .HasColumnName("status")
            .HasMaxLength(30)
            .HasConversion(
                status => ToDatabase(status),
                value => FromDatabase(value))
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(x => x.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.Property(x => x.Phone)
            .HasColumnName("phone")
            .HasMaxLength(20)
            .IsRequired(false);

        builder.Property(x => x.PasswordHash)
            .HasColumnName("password_hash")
            .HasMaxLength(255)
            .IsRequired(false);

        builder.Property(x => x.AvatarFileId)
            .HasColumnName("avatar_file_id")
            .IsRequired(false);

        builder.Property(x => x.EmailVerifiedAt)
            .HasColumnName("email_verified_at")
            .IsRequired(false);

        builder.Property(x => x.LastLoginAt)
            .HasColumnName("last_login_at")
            .IsRequired(false);

        builder.Property(x => x.DeletedAt)
            .HasColumnName("deleted_at")
            .IsRequired(false);
    }

    private static string ToDatabase(UserStatus status) => status switch
    {
        UserStatus.PendingVerification => PendingVerification,
        UserStatus.Active => Active,
        UserStatus.Suspended => Suspended,
        UserStatus.Disabled => Disabled,
        _ => throw new ArgumentOutOfRangeException(
            nameof(status), status, "Unsupported user status.")
    };

    private static UserStatus FromDatabase(string value) => value switch
    {
        PendingVerification => UserStatus.PendingVerification,
        Active => UserStatus.Active,
        Suspended => UserStatus.Suspended,
        Disabled => UserStatus.Disabled,
        _ => throw new ArgumentOutOfRangeException(
            nameof(value), value, "Unsupported user status.")
    };
}
