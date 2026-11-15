using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.IotMonitoring;

namespace SmartSolar.Infrastructure.Persistence.Configurations.IotMonitoring;

public sealed class IotDeviceConfiguration : IEntityTypeConfiguration<IotDevice>
{
    public void Configure(EntityTypeBuilder<IotDevice> builder)
    {
        builder.ToTable("iot_device", t =>
        {
            t.HasCheckConstraint("ck_iot_device_status", $"status IN ({string.Join(", ", IotDeviceStatuses.All.Select(s => $"'{s}'"))})");
            t.HasCheckConstraint("ck_iot_device_max_current", $"max_current_amps > 0 AND max_current_amps <= {IotLimits.MaxConfigurableCurrentAmps}");
            t.HasCheckConstraint("ck_iot_device_secret_version", "secret_version >= 1");
            t.HasCheckConstraint("ck_iot_device_disabled",
                $"(status = '{IotDeviceStatuses.Disabled}') = (disabled_at IS NOT NULL) AND (status <> '{IotDeviceStatuses.Disabled}' OR (disabled_by_user_id IS NOT NULL AND disabled_reason IS NOT NULL))");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.DeviceId).HasColumnName("device_id").HasMaxLength(IotLimits.DeviceIdMaxLength).IsRequired();
        builder.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(IotLimits.DisplayNameMaxLength).IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        builder.Property(x => x.SecretHash).HasColumnName("secret_hash").HasMaxLength(64).IsRequired();
        builder.Property(x => x.SecretVersion).HasColumnName("secret_version").IsRequired();
        builder.Property(x => x.SecretRotatedAt).HasColumnName("secret_rotated_at").IsRequired();
        builder.Property(x => x.SensorModel).HasColumnName("sensor_model").HasMaxLength(IotLimits.SensorModelMaxLength);
        builder.Property(x => x.MaxCurrentAmps).HasColumnName("max_current_amps").HasPrecision(9, 3).IsRequired();
        builder.Property(x => x.LastSeenAt).HasColumnName("last_seen_at");
        builder.Property(x => x.DisabledAt).HasColumnName("disabled_at");
        builder.Property(x => x.DisabledByUserId).HasColumnName("disabled_by_user_id");
        builder.Property(x => x.DisabledReason).HasColumnName("disabled_reason").HasMaxLength(IotLimits.ReasonMaxLength);
        builder.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(x => x.Revision).HasColumnName("revision").IsConcurrencyToken().IsRequired();
        builder.HasIndex(x => x.DeviceId).IsUnique().HasDatabaseName("ux_iot_device_device_id");
        builder.HasIndex(x => x.Status).HasDatabaseName("ix_iot_device_status");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_iot_device_created_by");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.DisabledByUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_iot_device_disabled_by");
    }
}

public sealed class IotDeviceBindingConfiguration : IEntityTypeConfiguration<IotDeviceBinding>
{
    public void Configure(EntityTypeBuilder<IotDeviceBinding> builder)
    {
        builder.ToTable("iot_device_binding", t =>
        {
            t.HasCheckConstraint("ck_iot_device_binding_status", $"status IN ({string.Join(", ", IotBindingStatuses.All.Select(s => $"'{s}'"))})");
            t.HasCheckConstraint("ck_iot_device_binding_released",
                $"(status = '{IotBindingStatuses.Active}') = (released_at IS NULL) AND (status <> '{IotBindingStatuses.Released}' OR (released_by_user_id IS NOT NULL AND release_reason IS NOT NULL))");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.DeviceId).HasColumnName("device_id").IsRequired();
        builder.Property(x => x.ProjectId).HasColumnName("project_id").IsRequired();
        builder.Property(x => x.CustomerUserId).HasColumnName("customer_user_id").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        builder.Property(x => x.BoundAt).HasColumnName("bound_at").IsRequired();
        builder.Property(x => x.BoundByUserId).HasColumnName("bound_by_user_id").IsRequired();
        builder.Property(x => x.Note).HasColumnName("note").HasMaxLength(IotLimits.ReasonMaxLength);
        builder.Property(x => x.ReleasedAt).HasColumnName("released_at");
        builder.Property(x => x.ReleasedByUserId).HasColumnName("released_by_user_id");
        builder.Property(x => x.ReleaseReason).HasColumnName("release_reason").HasMaxLength(IotLimits.ReasonMaxLength);
        // One ACTIVE binding per device and one per installation project: the database decides races, not the handler. A customer
        // with several projects may have one device on each, so the customer column is indexed for lookups only.
        builder.HasIndex(x => x.DeviceId).IsUnique().HasFilter($"status = '{IotBindingStatuses.Active}'").HasDatabaseName("ux_iot_device_binding_active_device");
        builder.HasIndex(x => x.ProjectId).IsUnique().HasFilter($"status = '{IotBindingStatuses.Active}'").HasDatabaseName("ux_iot_device_binding_active_project");
        builder.HasIndex(x => new { x.ProjectId, x.BoundAt }).HasDatabaseName("ix_iot_device_binding_project_bound_at");
        builder.HasIndex(x => x.CustomerUserId).HasDatabaseName("ix_iot_device_binding_customer");
        builder.HasOne<IotDevice>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_iot_device_binding_device");
        builder.HasOne<InstallationProject>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_iot_device_binding_project");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CustomerUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_iot_device_binding_customer");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.BoundByUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_iot_device_binding_bound_by");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ReleasedByUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_iot_device_binding_released_by");
    }
}

public sealed class IotTelemetryReadingConfiguration : IEntityTypeConfiguration<IotTelemetryReading>
{
    public void Configure(EntityTypeBuilder<IotTelemetryReading> builder)
    {
        // NaN and ±Infinity fail a BETWEEN check in PostgreSQL (NaN sorts above every number), so only finite plausible values can be stored.
        builder.ToTable("iot_telemetry_reading", t => t.HasCheckConstraint("ck_iot_telemetry_reading_current", "current_amps BETWEEN -10000 AND 10000"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.DeviceId).HasColumnName("device_id").IsRequired();
        builder.Property(x => x.BindingId).HasColumnName("binding_id").IsRequired();
        builder.Property(x => x.CurrentAmps).HasColumnName("current_amps").IsRequired();
        builder.Property(x => x.ReceivedAt).HasColumnName("received_at").IsRequired();
        builder.HasIndex(x => new { x.DeviceId, x.ReceivedAt }).IsDescending(false, true).HasDatabaseName("ix_iot_telemetry_reading_device_received_at");
        builder.HasIndex(x => new { x.BindingId, x.ReceivedAt }).IsDescending(false, true).HasDatabaseName("ix_iot_telemetry_reading_binding_received_at");
        builder.HasOne<IotDevice>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_iot_telemetry_reading_device");
        builder.HasOne<IotDeviceBinding>().WithMany().HasForeignKey(x => x.BindingId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_iot_telemetry_reading_binding");
    }
}
