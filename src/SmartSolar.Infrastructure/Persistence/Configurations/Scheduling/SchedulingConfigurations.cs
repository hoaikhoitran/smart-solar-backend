using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.PreSurvey.Entities;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Entities;

namespace SmartSolar.Infrastructure.Persistence.Configurations.Scheduling;

public sealed class TechnicianProfileConfiguration : IEntityTypeConfiguration<TechnicianProfile>
{
    public void Configure(EntityTypeBuilder<TechnicianProfile> builder)
    {
        builder.ToTable("technician_profile", t =>
        {
            t.HasCheckConstraint("ck_technician_profile_base_paired", "(base_latitude IS NULL) = (base_longitude IS NULL)");
            t.HasCheckConstraint("ck_technician_profile_max_per_day_positive", "max_appointments_per_day IS NULL OR max_appointments_per_day > 0");
        });
        builder.HasKey(x => x.UserId);
        builder.Property(x => x.UserId).HasColumnName("user_id").ValueGeneratedNever();
        builder.Property(x => x.BaseLatitude).HasColumnName("base_latitude").IsRequired(false);
        builder.Property(x => x.BaseLongitude).HasColumnName("base_longitude").IsRequired(false);
        builder.Property(x => x.BaseLabel).HasColumnName("base_label").HasMaxLength(200).IsRequired(false);
        builder.Property(x => x.IsSchedulingActive).HasColumnName("is_scheduling_active").IsRequired();
        builder.Property(x => x.MaxAppointmentsPerDay).HasColumnName("max_appointments_per_day").IsRequired(false);
        builder.Property(x => x.HasCustomWorkingSchedule).HasColumnName("has_custom_working_schedule").IsRequired();
        builder.Property(x => x.EarliestBaseDeparture).HasColumnName("earliest_base_departure").IsRequired(false);
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
            t.HasCheckConstraint("ck_technician_appointment_range", "ends_at > starts_at");
            t.HasCheckConstraint("ck_technician_appointment_type", $"appointment_type IN ('{AppointmentTypes.PreSurvey}')");
            t.HasCheckConstraint("ck_technician_appointment_status",
                $"status IN ('{AppointmentStatuses.Booked}', '{AppointmentStatuses.Assigned}', '{AppointmentStatuses.Acknowledged}', '{AppointmentStatuses.Cancelled}')");
            t.HasCheckConstraint("ck_technician_appointment_cancelled_at", $"(status = '{AppointmentStatuses.Cancelled}') = (cancelled_at IS NOT NULL)");
            t.HasCheckConstraint("ck_technician_appointment_assignment",
                $"status NOT IN ('{AppointmentStatuses.Assigned}', '{AppointmentStatuses.Acknowledged}') OR (assigned_technician_user_id IS NOT NULL AND assigned_technician_user_id = reserved_technician_user_id AND assigned_at IS NOT NULL)");
            t.HasCheckConstraint("ck_technician_appointment_acknowledged",
                $"status <> '{AppointmentStatuses.Acknowledged}' OR acknowledged_at IS NOT NULL");
            t.HasCheckConstraint("ck_technician_appointment_pre_survey_link",
                $"appointment_type <> '{AppointmentTypes.PreSurvey}' OR survey_request_id IS NOT NULL");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.AppointmentType).HasColumnName("appointment_type").HasMaxLength(30).IsRequired();
        builder.Property(x => x.SurveyRequestId).HasColumnName("survey_request_id").IsRequired(false);
        builder.Property(x => x.PropertySiteId).HasColumnName("property_site_id").IsRequired();
        builder.Property(x => x.StartsAt).HasColumnName("starts_at").IsRequired();
        builder.Property(x => x.EndsAt).HasColumnName("ends_at").IsRequired();
        builder.Property(x => x.ReservedTechnicianUserId).HasColumnName("reserved_technician_user_id").IsRequired();
        builder.Property(x => x.AssignedTechnicianUserId).HasColumnName("assigned_technician_user_id").IsRequired(false);
        builder.Property(x => x.AssignedBySaleUserId).HasColumnName("assigned_by_sale_user_id").IsRequired(false);
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(30).IsRequired();
        builder.Property(x => x.CustomerNote).HasColumnName("customer_note").HasMaxLength(1000).IsRequired(false);
        builder.Property(x => x.BookedByUserId).HasColumnName("booked_by_user_id").IsRequired();
        builder.Property(x => x.BookedAt).HasColumnName("booked_at").IsRequired();
        builder.Property(x => x.AssignedAt).HasColumnName("assigned_at").IsRequired(false);
        builder.Property(x => x.AcknowledgedAt).HasColumnName("acknowledged_at").IsRequired(false);
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

        builder.HasOne<SurveyRequest>().WithMany().HasForeignKey(x => x.SurveyRequestId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PropertySite>().WithMany().HasForeignKey(x => x.PropertySiteId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ReservedTechnicianUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.AssignedTechnicianUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.AssignedBySaleUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.BookedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TechnicianAppointment>().WithMany().HasForeignKey(x => x.ReplacedByAppointmentId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class TechnicianTravelEstimateConfiguration : IEntityTypeConfiguration<TechnicianTravelEstimate>
{
    public void Configure(EntityTypeBuilder<TechnicianTravelEstimate> builder)
    {
        builder.ToTable("technician_travel_estimate", t =>
        {
            t.HasCheckConstraint("ck_technician_travel_estimate_origin_kind",
                $"origin_kind IN ('{TravelOriginKinds.Base}', '{TravelOriginKinds.Appointment}')");
            t.HasCheckConstraint("ck_technician_travel_estimate_non_negative",
                "(distance_meters IS NULL OR distance_meters >= 0) AND (duration_seconds IS NULL OR duration_seconds >= 0) AND safety_buffer_minutes >= 0");
            t.HasCheckConstraint("ck_technician_travel_estimate_ok_has_values",
                "status <> 'OK' OR (distance_meters IS NOT NULL AND duration_seconds IS NOT NULL)");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.AppointmentId).HasColumnName("appointment_id").IsRequired();
        builder.Property(x => x.TechnicianUserId).HasColumnName("technician_user_id").IsRequired();
        builder.Property(x => x.Context).HasColumnName("context").HasMaxLength(30).IsRequired();
        builder.Property(x => x.OriginKind).HasColumnName("origin_kind").HasMaxLength(20).IsRequired();
        builder.Property(x => x.OriginAppointmentId).HasColumnName("origin_appointment_id").IsRequired(false);
        builder.Property(x => x.OriginLatitude).HasColumnName("origin_latitude").IsRequired();
        builder.Property(x => x.OriginLongitude).HasColumnName("origin_longitude").IsRequired();
        builder.Property(x => x.DestinationLatitude).HasColumnName("destination_latitude").IsRequired();
        builder.Property(x => x.DestinationLongitude).HasColumnName("destination_longitude").IsRequired();
        builder.Property(x => x.DepartureAt).HasColumnName("departure_at").IsRequired();
        builder.Property(x => x.EstimatedAt).HasColumnName("estimated_at").IsRequired();
        builder.Property(x => x.DistanceMeters).HasColumnName("distance_meters").IsRequired(false);
        builder.Property(x => x.DurationSeconds).HasColumnName("duration_seconds").IsRequired(false);
        builder.Property(x => x.SafetyBufferMinutes).HasColumnName("safety_buffer_minutes").IsRequired();
        builder.Property(x => x.EstimatedArrivalAt).HasColumnName("estimated_arrival_at").IsRequired(false);
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(30).IsRequired();
        builder.Property(x => x.Provider).HasColumnName("provider").HasMaxLength(30).IsRequired();
        builder.Property(x => x.ProviderVersion).HasColumnName("provider_version").HasMaxLength(20).IsRequired();
        builder.Property(x => x.Vehicle).HasColumnName("vehicle").HasMaxLength(20).IsRequired();
        builder.Property(x => x.CongestionSummary).HasColumnName("congestion_summary").HasColumnType("jsonb").IsRequired(false);
        builder.Property(x => x.SupersededAt).HasColumnName("superseded_at").IsRequired(false);
        builder.HasIndex(x => new { x.AppointmentId, x.EstimatedAt }).IsDescending(false, true).HasDatabaseName("ix_technician_travel_estimate_appointment_estimated_at");
        builder.HasOne<TechnicianAppointment>().WithMany().HasForeignKey(x => x.AppointmentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TechnicianAppointment>().WithMany().HasForeignKey(x => x.OriginAppointmentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.TechnicianUserId).OnDelete(DeleteBehavior.Restrict);
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

public sealed class TechnicianScheduleRecalculationConfiguration : IEntityTypeConfiguration<TechnicianScheduleRecalculation>
{
    public void Configure(EntityTypeBuilder<TechnicianScheduleRecalculation> builder)
    {
        builder.ToTable("technician_schedule_recalculation", t =>
        {
            t.HasCheckConstraint("ck_technician_schedule_recalculation_status",
                $"status IN ('{RecalculationStatuses.Pending}', '{RecalculationStatuses.Failed}')");
            t.HasCheckConstraint("ck_technician_schedule_recalculation_attempts", "attempts >= 0");
        });
        builder.HasKey(x => new { x.TechnicianUserId, x.LocalDate });
        builder.Property(x => x.TechnicianUserId).HasColumnName("technician_user_id");
        builder.Property(x => x.LocalDate).HasColumnName("local_date");
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        builder.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(50).IsRequired();
        builder.Property(x => x.RequestedAt).HasColumnName("requested_at").IsRequired();
        builder.Property(x => x.Attempts).HasColumnName("attempts").IsRequired();
        builder.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at").IsRequired();
        builder.Property(x => x.LockedUntil).HasColumnName("locked_until").IsRequired(false);
        builder.Property(x => x.LastError).HasColumnName("last_error").HasMaxLength(200).IsRequired(false);
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.HasIndex(x => x.NextAttemptAt).HasDatabaseName("ix_technician_schedule_recalculation_next_attempt_at");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.TechnicianUserId).OnDelete(DeleteBehavior.Restrict);
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
