using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.PreSurvey.Entities;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Entities;
using SmartSolar.Modules.AdministrativeUnits;
using SmartSolar.Modules.Scheduling.Zones;

namespace SmartSolar.Infrastructure.Persistence.Configurations.Scheduling;

public sealed class TechnicianProfileConfiguration : IEntityTypeConfiguration<TechnicianProfile>
{
    public void Configure(EntityTypeBuilder<TechnicianProfile> builder)
    {
        builder.ToTable("technician_profile", t =>
            t.HasCheckConstraint("ck_technician_profile_max_per_day_positive", "max_appointments_per_day IS NULL OR max_appointments_per_day > 0"));
        builder.HasKey(x => x.UserId);
        builder.Property(x => x.UserId).HasColumnName("user_id").ValueGeneratedNever();
        // base_latitude / base_longitude / base_label / earliest_base_departure stay in the database (nullable, unused) until the cleanup draft.
        builder.Property(x => x.IsSchedulingActive).HasColumnName("is_scheduling_active").IsRequired();
        builder.Property(x => x.MaxAppointmentsPerDay).HasColumnName("max_appointments_per_day").IsRequired(false);
        builder.Property(x => x.HasCustomWorkingSchedule).HasColumnName("has_custom_working_schedule").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.HasOne<UserAccount>().WithOne().HasForeignKey<TechnicianProfile>(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class TechnicianUnavailablePeriodConfiguration : IEntityTypeConfiguration<TechnicianUnavailablePeriod>
{
    public void Configure(EntityTypeBuilder<TechnicianUnavailablePeriod> builder)
    {
        builder.ToTable("technician_unavailable_period", t =>
            t.HasCheckConstraint("ck_technician_unavailable_period_range", "ends_at > starts_at"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.TechnicianUserId).HasColumnName("technician_user_id").IsRequired();
        builder.Property(x => x.StartsAt).HasColumnName("starts_at").IsRequired();
        builder.Property(x => x.EndsAt).HasColumnName("ends_at").IsRequired();
        builder.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(500).IsRequired(false);
        builder.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired(false);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.HasIndex(x => new { x.TechnicianUserId, x.StartsAt }).HasDatabaseName("ix_technician_unavailable_period_technician_starts_at");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.TechnicianUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class TechnicianAppointmentConfiguration : IEntityTypeConfiguration<TechnicianAppointment>
{
    public void Configure(EntityTypeBuilder<TechnicianAppointment> builder)
    {
        builder.ToTable("technician_appointment", t =>
        {
            const string pending = AppointmentStatuses.PendingAcceptance, escalated = AppointmentStatuses.Escalated, accepted = AppointmentStatuses.Accepted,
                assigned = AppointmentStatuses.Assigned, completed = AppointmentStatuses.Completed, cancelled = AppointmentStatuses.Cancelled;
            t.HasCheckConstraint("ck_technician_appointment_range", "ends_at > starts_at");
            t.HasCheckConstraint("ck_technician_appointment_type", $"appointment_type IN ('{AppointmentTypes.PreSurvey}')");
            t.HasCheckConstraint("ck_technician_appointment_status",
                $"status IN ('{pending}', '{escalated}', '{accepted}', '{assigned}', '{completed}', '{cancelled}')");
            t.HasCheckConstraint("ck_technician_appointment_cancelled_at", $"(status = '{cancelled}') = (cancelled_at IS NOT NULL)");
            // An open appointment always holds one technician's capacity until it escalates; nobody is confirmed yet.
            t.HasCheckConstraint("ck_technician_appointment_pending_holder",
                $"status <> '{pending}' OR (reserved_technician_user_id IS NOT NULL AND assigned_technician_user_id IS NULL)");
            t.HasCheckConstraint("ck_technician_appointment_escalated_unassigned", $"status <> '{escalated}' OR assigned_technician_user_id IS NULL");
            t.HasCheckConstraint("ck_technician_appointment_assignment",
                $"status NOT IN ('{accepted}', '{assigned}', '{completed}') OR (assigned_technician_user_id IS NOT NULL AND assigned_technician_user_id = reserved_technician_user_id"
                + " AND assigned_at IS NOT NULL AND assignment_source IS NOT NULL)");
            t.HasCheckConstraint("ck_technician_appointment_assignment_reason", $"status <> '{assigned}' OR assignment_reason IS NOT NULL");
            t.HasCheckConstraint("ck_technician_appointment_assignment_source",
                $"assignment_source IS NULL OR assignment_source IN ({string.Join(", ", AssignmentSources.All.Select(x => $"'{x}'"))})");
            t.HasCheckConstraint("ck_technician_appointment_completed_at", $"status <> '{completed}' OR completed_at IS NOT NULL");
            t.HasCheckConstraint("ck_technician_appointment_pre_survey_link",
                $"appointment_type <> '{AppointmentTypes.PreSurvey}' OR survey_request_id IS NOT NULL");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.AppointmentType).HasColumnName("appointment_type").HasMaxLength(30).IsRequired();
        builder.Property(x => x.SurveyRequestId).HasColumnName("survey_request_id").IsRequired(false);
        builder.Property(x => x.PropertySiteId).HasColumnName("property_site_id").IsRequired();
        // service_area_id stays in the database (nullable, unused) until the cleanup draft; the zone replaces it.
        builder.Property(x => x.OperationalZoneId).HasColumnName("operational_zone_id").IsRequired(false);
        builder.Property(x => x.StartsAt).HasColumnName("starts_at").IsRequired();
        builder.Property(x => x.EndsAt).HasColumnName("ends_at").IsRequired();
        // The capacity holder; nullable since an ESCALATED appointment may have nobody holding it.
        builder.Property(x => x.ReservedTechnicianUserId).HasColumnName("reserved_technician_user_id").IsRequired(false);
        builder.Property(x => x.AssignedTechnicianUserId).HasColumnName("assigned_technician_user_id").IsRequired(false);
        builder.Property(x => x.AssignedBySaleUserId).HasColumnName("assigned_by_sale_user_id").IsRequired(false);
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(30).IsRequired();
        builder.Property(x => x.CustomerNote).HasColumnName("customer_note").HasMaxLength(1000).IsRequired(false);
        builder.Property(x => x.BookedByUserId).HasColumnName("booked_by_user_id").IsRequired();
        builder.Property(x => x.BookedAt).HasColumnName("booked_at").IsRequired();
        builder.Property(x => x.AcceptanceDeadlineAt).HasColumnName("acceptance_deadline_at").IsRequired(false);
        // Snapshot of the setting the deadline was derived from (non-retroactive settings; null for legacy rows and direct assignments).
        builder.Property(x => x.AcceptanceWindowMinutes).HasColumnName("acceptance_window_minutes").IsRequired(false);
        builder.Property(x => x.SettingsVersion).HasColumnName("settings_version").IsRequired(false);
        builder.Property(x => x.AcceptanceReminderSentAt).HasColumnName("acceptance_reminder_sent_at").IsRequired(false);
        builder.Property(x => x.CrossZoneAuthorizationId).HasColumnName("cross_zone_authorization_id").IsRequired(false);
        builder.Property(x => x.EscalatedAt).HasColumnName("escalated_at").IsRequired(false);
        builder.Property(x => x.AcceptedAt).HasColumnName("accepted_at").IsRequired(false);
        builder.Property(x => x.AssignedAt).HasColumnName("assigned_at").IsRequired(false);
        builder.Property(x => x.AssignmentSource).HasColumnName("assignment_source").HasMaxLength(30).IsRequired(false);
        builder.Property(x => x.AssignmentReason).HasColumnName("assignment_reason").HasMaxLength(500).IsRequired(false);
        builder.Property(x => x.AcknowledgedAt).HasColumnName("acknowledged_at").IsRequired(false);
        builder.Property(x => x.CompletedAt).HasColumnName("completed_at").IsRequired(false);
        builder.Property(x => x.CancelledAt).HasColumnName("cancelled_at").IsRequired(false);
        builder.Property(x => x.CancellationReason).HasColumnName("cancellation_reason").HasMaxLength(200).IsRequired(false);
        builder.Property(x => x.ReplacedByAppointmentId).HasColumnName("replaced_by_appointment_id").IsRequired(false);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(x => x.Revision).HasColumnName("revision").IsConcurrencyToken().IsRequired();

        // One active appointment per survey request (cancelled history rows are not counted).
        builder.HasIndex(x => x.SurveyRequestId)
            .IsUnique()
            .HasFilter($"status <> '{AppointmentStatuses.Cancelled}'")
            .HasDatabaseName("ux_technician_appointment_active_survey_request");

        builder.HasIndex(x => new { x.ReservedTechnicianUserId, x.StartsAt }).HasDatabaseName("ix_technician_appointment_reserved_starts_at");
        builder.HasIndex(x => new { x.AssignedTechnicianUserId, x.StartsAt }).HasDatabaseName("ix_technician_appointment_assigned_starts_at");
        // Escalation sweep and the staff queue.
        builder.HasIndex(x => new { x.Status, x.AcceptanceDeadlineAt }).HasDatabaseName("ix_technician_appointment_status_deadline");
        builder.HasIndex(x => x.OperationalZoneId).HasDatabaseName("ix_technician_appointment_operational_zone_id");

        builder.HasOne<SurveyRequest>().WithMany().HasForeignKey(x => x.SurveyRequestId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PropertySite>().WithMany().HasForeignKey(x => x.PropertySiteId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ReservedTechnicianUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.AssignedTechnicianUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.AssignedBySaleUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.BookedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TechnicianAppointment>().WithMany().HasForeignKey(x => x.ReplacedByAppointmentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<OperationalZone>().WithMany().HasForeignKey(x => x.OperationalZoneId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_technician_appointment_operational_zone");
        builder.HasOne<SmartSolar.Modules.Scheduling.Dispatch.CrossZoneAuthorization>().WithMany().HasForeignKey(x => x.CrossZoneAuthorizationId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_technician_appointment_cross_zone_authorization");
    }
}

public sealed class TechnicianWorkingPeriodConfiguration : IEntityTypeConfiguration<TechnicianWorkingPeriod>
{
    public void Configure(EntityTypeBuilder<TechnicianWorkingPeriod> builder)
    {
        builder.ToTable("technician_working_period", t =>
        {
            t.HasCheckConstraint("ck_technician_working_period_day", "day_of_week BETWEEN 0 AND 6");
            t.HasCheckConstraint("ck_technician_working_period_range", "end_time > start_time");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.TechnicianUserId).HasColumnName("technician_user_id").IsRequired();
        builder.Property(x => x.DayOfWeek).HasColumnName("day_of_week").IsRequired();
        builder.Property(x => x.StartTime).HasColumnName("start_time").IsRequired();
        builder.Property(x => x.EndTime).HasColumnName("end_time").IsRequired();
        builder.HasIndex(x => new { x.TechnicianUserId, x.DayOfWeek, x.StartTime }).IsUnique()
            .HasDatabaseName("ux_technician_working_period_day_start");
        builder.HasOne<TechnicianProfile>().WithMany().HasForeignKey(x => x.TechnicianUserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class TechnicianAppointmentEventConfiguration : IEntityTypeConfiguration<TechnicianAppointmentEvent>
{
    public void Configure(EntityTypeBuilder<TechnicianAppointmentEvent> builder)
    {
        builder.ToTable("technician_appointment_event");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.AppointmentId).HasColumnName("appointment_id").IsRequired();
        builder.Property(x => x.SurveyRequestId).HasColumnName("survey_request_id").IsRequired(false);
        builder.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(40).IsRequired();
        builder.Property(x => x.ActorUserId).HasColumnName("actor_user_id").IsRequired(false);
        builder.Property(x => x.PreviousTechnicianUserId).HasColumnName("previous_technician_user_id").IsRequired(false);
        builder.Property(x => x.NewTechnicianUserId).HasColumnName("new_technician_user_id").IsRequired(false);
        builder.Property(x => x.PreviousStartsAt).HasColumnName("previous_starts_at").IsRequired(false);
        builder.Property(x => x.NewStartsAt).HasColumnName("new_starts_at").IsRequired(false);
        builder.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(500).IsRequired(false);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.HasIndex(x => new { x.AppointmentId, x.CreatedAt }).HasDatabaseName("ix_technician_appointment_event_appointment_created_at");
        builder.HasIndex(x => new { x.SurveyRequestId, x.CreatedAt }).HasDatabaseName("ix_technician_appointment_event_survey_request_created_at");
        builder.HasOne<TechnicianAppointment>().WithMany().HasForeignKey(x => x.AppointmentId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class SchedulingNotificationConfiguration : IEntityTypeConfiguration<SchedulingNotification>
{
    public void Configure(EntityTypeBuilder<SchedulingNotification> builder)
    {
        builder.ToTable("scheduling_notification");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.RecipientUserId).HasColumnName("recipient_user_id").IsRequired();
        builder.Property(x => x.AppointmentId).HasColumnName("appointment_id").IsRequired();
        builder.Property(x => x.EventId).HasColumnName("event_id").IsRequired();
        builder.Property(x => x.Type).HasColumnName("type").HasMaxLength(40).IsRequired();
        builder.Property(x => x.DeliveryStatus).HasColumnName("delivery_status").HasMaxLength(30).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.HasIndex(x => new { x.RecipientUserId, x.CreatedAt }).HasDatabaseName("ix_scheduling_notification_recipient_created_at");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.RecipientUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TechnicianAppointment>().WithMany().HasForeignKey(x => x.AppointmentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TechnicianAppointmentEvent>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class OperationalZoneConfiguration : IEntityTypeConfiguration<OperationalZone>
{
    public void Configure(EntityTypeBuilder<OperationalZone> builder)
    {
        builder.ToTable("operational_zone", t =>
            // Portable form of ^[A-Z0-9][A-Z0-9-]{1,49}$ (the application validates the full pattern): length, upper case, no blanks.
            t.HasCheckConstraint("ck_operational_zone_code",
                "length(code) BETWEEN 2 AND 50 AND code = upper(code) AND code NOT LIKE '% %' AND code NOT LIKE '-%'"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.Code).HasColumnName("code").HasMaxLength(50).IsRequired();
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(150).IsRequired();
        builder.Property(x => x.IsActive).HasColumnName("is_active").IsRequired();
        // Database default true (for rows written outside the application); EF always sends the value (no bool-default sentinel trap).
        builder.Property(x => x.IsServiceable).HasColumnName("is_serviceable").HasDefaultValue(true).ValueGeneratedNever().IsRequired();
        builder.Property(x => x.ResponsibleManagerUserId).HasColumnName("responsible_manager_user_id").IsRequired(false);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired(false);
        builder.Property(x => x.UpdatedByUserId).HasColumnName("updated_by_user_id").IsRequired(false);
        builder.Property(x => x.Revision).HasColumnName("revision").IsConcurrencyToken().IsRequired();
        builder.HasIndex(x => x.Code).IsUnique().HasDatabaseName("ux_operational_zone_code");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_operational_zone_created_by");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UpdatedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_operational_zone_updated_by");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ResponsibleManagerUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_operational_zone_responsible_manager");
    }
}

public sealed class OperationalZoneCommuneConfiguration : IEntityTypeConfiguration<OperationalZoneCommune>
{
    public void Configure(EntityTypeBuilder<OperationalZoneCommune> builder)
    {
        builder.ToTable("operational_zone_commune", t =>
            t.HasCheckConstraint("ck_operational_zone_commune_removed", "(removed_at IS NULL) = (removed_by_user_id IS NULL)"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.ZoneId).HasColumnName("zone_id").IsRequired();
        builder.Property(x => x.CommuneCode).HasColumnName("commune_code").HasMaxLength(5).IsRequired();
        builder.Property(x => x.AssignedByUserId).HasColumnName("assigned_by_user_id").IsRequired();
        builder.Property(x => x.AssignedAt).HasColumnName("assigned_at").IsRequired();
        builder.Property(x => x.RemovedAt).HasColumnName("removed_at").IsRequired(false);
        builder.Property(x => x.RemovedByUserId).HasColumnName("removed_by_user_id").IsRequired(false);
        builder.Property(x => x.RemovalReason).HasColumnName("removal_reason").HasMaxLength(500).IsRequired(false);
        // Exclusive mapping: a commune has at most one active zone; removed rows are history.
        builder.HasIndex(x => x.CommuneCode).IsUnique().HasFilter("removed_at IS NULL").HasDatabaseName("ux_operational_zone_commune_active");
        builder.HasIndex(x => new { x.ZoneId, x.RemovedAt }).HasDatabaseName("ix_operational_zone_commune_zone_removed_at");
        builder.HasOne<OperationalZone>().WithMany().HasForeignKey(x => x.ZoneId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_operational_zone_commune_zone");
        builder.HasOne<AdministrativeCommune>().WithMany().HasForeignKey(x => x.CommuneCode).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_operational_zone_commune_commune");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.AssignedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_operational_zone_commune_assigned_by");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.RemovedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_operational_zone_commune_removed_by");
    }
}

public sealed class TechnicianZoneMembershipConfiguration : IEntityTypeConfiguration<TechnicianZoneMembership>
{
    public void Configure(EntityTypeBuilder<TechnicianZoneMembership> builder)
    {
        builder.ToTable("technician_zone_membership", t =>
            t.HasCheckConstraint("ck_technician_zone_membership_removed", "removed_at IS NULL OR removed_by_user_id IS NOT NULL"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.TechnicianUserId).HasColumnName("technician_user_id").IsRequired();
        builder.Property(x => x.ZoneId).HasColumnName("zone_id").IsRequired();
        builder.Property(x => x.AssignedByUserId).HasColumnName("assigned_by_user_id").IsRequired(false);
        builder.Property(x => x.AssignedAt).HasColumnName("assigned_at").IsRequired();
        builder.Property(x => x.RemovedAt).HasColumnName("removed_at").IsRequired(false);
        builder.Property(x => x.RemovedByUserId).HasColumnName("removed_by_user_id").IsRequired(false);
        // One active membership per technician and zone; soft-removed rows are history.
        builder.HasIndex(x => new { x.TechnicianUserId, x.ZoneId }).IsUnique().HasFilter("removed_at IS NULL")
            .HasDatabaseName("ux_technician_zone_membership_active");
        builder.HasIndex(x => x.ZoneId).HasDatabaseName("ix_technician_zone_membership_zone_id");
        builder.HasOne<TechnicianProfile>().WithMany().HasForeignKey(x => x.TechnicianUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_technician_zone_membership_technician_profile");
        builder.HasOne<OperationalZone>().WithMany().HasForeignKey(x => x.ZoneId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_technician_zone_membership_zone");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.AssignedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_technician_zone_membership_assigned_by");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.RemovedByUserId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_technician_zone_membership_removed_by");
    }
}
