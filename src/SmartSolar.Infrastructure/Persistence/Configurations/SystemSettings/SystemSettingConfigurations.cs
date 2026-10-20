using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.SystemSettings;

namespace SmartSolar.Infrastructure.Persistence.Configurations.SystemSettings;

internal static class SystemSettingSql
{
    public static string UnitCheck => $"unit IN ({string.Join(", ", SystemSettingUnits.All.Select(u => $"'{u}'"))})";
}

/// <summary>
/// Admin-saved setting values keyed by the stable setting key. The unit is checked here; value ranges and cross-field rules are
/// validated in code (they depend on the definitions registry). <c>revision</c> is the optimistic concurrency token.
/// </summary>
public sealed class SystemSettingConfiguration : IEntityTypeConfiguration<SystemSetting>
{
    public void Configure(EntityTypeBuilder<SystemSetting> builder)
    {
        builder.ToTable("system_setting", t =>
        {
            t.HasCheckConstraint("ck_system_setting_unit", SystemSettingSql.UnitCheck);
            t.HasCheckConstraint("ck_system_setting_key", "length(key) > 0");
            t.HasCheckConstraint("ck_system_setting_version", "version >= 1");
        });
        builder.HasKey(x => x.Key);
        builder.Property(x => x.Key).HasColumnName("key").HasMaxLength(100);
        builder.Property(x => x.ValueInt).HasColumnName("value_int").IsRequired();
        builder.Property(x => x.Unit).HasColumnName("unit").HasMaxLength(20).IsRequired();
        builder.Property(x => x.Version).HasColumnName("version").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(x => x.UpdatedByUserId).HasColumnName("updated_by_user_id");
        builder.Property(x => x.Revision).HasColumnName("revision").IsConcurrencyToken().IsRequired();
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UpdatedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_system_setting_user_account_updated_by_user_id");
        builder.HasIndex(x => x.UpdatedByUserId).HasDatabaseName("ix_system_setting_updated_by_user_id");
    }
}

/// <summary>Append-only audit of setting changes: one row per key and version (unique).</summary>
public sealed class SystemSettingRevisionConfiguration : IEntityTypeConfiguration<SystemSettingRevision>
{
    public void Configure(EntityTypeBuilder<SystemSettingRevision> builder)
    {
        builder.ToTable("system_setting_revision", t =>
        {
            t.HasCheckConstraint("ck_system_setting_revision_unit", SystemSettingSql.UnitCheck);
            t.HasCheckConstraint("ck_system_setting_revision_version", "version >= 1");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.Key).HasColumnName("key").HasMaxLength(100).IsRequired();
        builder.Property(x => x.Version).HasColumnName("version").IsRequired();
        builder.Property(x => x.OldValue).HasColumnName("old_value").IsRequired();
        builder.Property(x => x.NewValue).HasColumnName("new_value").IsRequired();
        builder.Property(x => x.Unit).HasColumnName("unit").HasMaxLength(20).IsRequired();
        builder.Property(x => x.ChangedByUserId).HasColumnName("changed_by").IsRequired();
        builder.Property(x => x.ChangedAt).HasColumnName("changed_at").IsRequired();
        builder.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(500);
        builder.HasIndex(x => new { x.Key, x.Version }).IsUnique().HasDatabaseName("ux_system_setting_revision_key_version");
        builder.HasIndex(x => x.Version).HasDatabaseName("ix_system_setting_revision_version");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ChangedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_system_setting_revision_user_account_changed_by");
        builder.HasIndex(x => x.ChangedByUserId).HasDatabaseName("ix_system_setting_revision_changed_by");
    }
}

/// <summary>The single global version row (id = 1); updates lock it FOR UPDATE and check <c>revision</c>.</summary>
public sealed class SystemSettingsVersionConfiguration : IEntityTypeConfiguration<SystemSettingsVersion>
{
    public void Configure(EntityTypeBuilder<SystemSettingsVersion> builder)
    {
        builder.ToTable("system_settings_version", t =>
        {
            t.HasCheckConstraint("ck_system_settings_version_singleton", $"id = {SystemSettingsVersion.SingletonId}");
            t.HasCheckConstraint("ck_system_settings_version_version", "version >= 1");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.Version).HasColumnName("version").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(x => x.UpdatedByUserId).HasColumnName("updated_by_user_id");
        builder.Property(x => x.Revision).HasColumnName("revision").IsConcurrencyToken().IsRequired();
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UpdatedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_system_settings_version_user_account_updated_by_user_id");
        builder.HasIndex(x => x.UpdatedByUserId).HasDatabaseName("ix_system_settings_version_updated_by_user_id");
    }
}
