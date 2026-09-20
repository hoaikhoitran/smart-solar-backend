using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.Identity.Enums;

namespace SmartSolar.Infrastructure.Persistence.Configurations.Identity;

public sealed class AuthActionTokenConfiguration
    : IEntityTypeConfiguration<AuthActionToken>
{
    private const string EmailVerification = "EMAIL_VERIFICATION";
    private const string PasswordReset = "PASSWORD_RESET";

    public void Configure(EntityTypeBuilder<AuthActionToken> builder)
    {
        builder.ToTable("auth_action_token");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasColumnName("id");

        builder.Property(x => x.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.Property(x => x.TokenHash)
            .HasColumnName("token_hash")
            .IsRequired();

        builder.Property(x => x.Type)
            .HasColumnName("type")
            .HasMaxLength(30)
            .HasConversion(
                type => ToDatabase(type),
                value => FromDatabase(value))
            .IsRequired();

        builder.Property(x => x.ExpiresAt)
            .HasColumnName("expires_at")
            .IsRequired();

        builder.Property(x => x.UsedAt)
            .HasColumnName("used_at")
            .IsRequired(false);

        builder.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .IsRequired();
    }

    private static string ToDatabase(AuthActionTokenType type) => type switch
    {
        AuthActionTokenType.EmailVerification => EmailVerification,
        AuthActionTokenType.PasswordReset => PasswordReset,
        _ => throw new ArgumentOutOfRangeException(
            nameof(type), type, "Unsupported auth action token type.")
    };

    private static AuthActionTokenType FromDatabase(string value) => value switch
    {
        EmailVerification => AuthActionTokenType.EmailVerification,
        PasswordReset => AuthActionTokenType.PasswordReset,
        _ => throw new ArgumentOutOfRangeException(
            nameof(value), value, "Unsupported auth action token type.")
    };
}
