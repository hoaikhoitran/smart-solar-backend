using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.PreSurvey.Entities;
using SmartSolar.Modules.Scheduling.Dispatch;
using SmartSolar.Modules.Scheduling.Entities;
using SmartSolar.Modules.Scheduling.Zones;

namespace SmartSolar.Infrastructure.Persistence.Configurations.Scheduling;

internal static class DispatchSql
{
    public static string In(IEnumerable<string> values) => string.Join(", ", values.Select(v => $"'{v}'"));
}

public sealed class DispatchRequestConfiguration : IEntityTypeConfiguration<DispatchRequest>
{
    /// <summary>One OPEN/PROPOSAL_PENDING request per survey request (customer create is idempotent on it).</summary>
    public const string ActiveSurveyRequestIndex = "ux_dispatch_request_active_survey_request";

    public void Configure(EntityTypeBuilder<DispatchRequest> builder)
    {
        const string open = DispatchStatuses.Open, pending = DispatchStatuses.ProposalPending, assigned = DispatchStatuses.Assigned,
            unserviceable = DispatchStatuses.Unserviceable, closed = DispatchStatuses.Closed;
        builder.ToTable("dispatch_request", t =>
        {
            t.HasCheckConstraint("ck_dispatch_request_status", $"status IN ({DispatchSql.In(DispatchStatuses.All)})");
            t.HasCheckConstraint("ck_dispatch_request_reason", $"reason IN ({DispatchSql.In(DispatchReasons.All)})");
            t.HasCheckConstraint("ck_dispatch_request_resolution",
                $"resolution IS NULL OR resolution IN ({DispatchSql.In(DispatchResolutions.All)})");
            // Terminal ⇔ resolved; each terminal status has its resolutions; ASSIGNED records the appointment.
            t.HasCheckConstraint("ck_dispatch_request_resolved",
                $"(status IN ('{open}', '{pending}')) = (resolved_at IS NULL AND resolution IS NULL)");
            t.HasCheckConstraint("ck_dispatch_request_terminal",
                $"(status <> '{assigned}' OR (resolution = '{DispatchResolutions.Assigned}' AND appointment_id IS NOT NULL))"
                + $" AND (status <> '{unserviceable}' OR resolution = '{DispatchResolutions.Unserviceable}')"
                + $" AND (status <> '{closed}' OR resolution IN ('{DispatchResolutions.ClosedByStaff}', '{DispatchResolutions.CancelledByCustomer}'))");
            t.HasCheckConstraint("ck_dispatch_request_sla", "review_sla_minutes > 0 AND review_due_at > created_at AND escalation_due_at >= review_due_at");
            t.HasCheckConstraint("ck_dispatch_request_reminders", "reminder_interval_minutes >= 0 AND reminder_count >= 0");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.SurveyRequestId).HasColumnName("survey_request_id").IsRequired();
        builder.Property(x => x.PropertySiteId).HasColumnName("property_site_id").IsRequired();
        builder.Property(x => x.CustomerUserId).HasColumnName("customer_user_id").IsRequired();
        builder.Property(x => x.OperationalZoneId).HasColumnName("operational_zone_id").IsRequired(false);
        builder.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(40).IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(30).IsRequired();
        builder.Property(x => x.CustomerNote).HasColumnName("customer_note").HasMaxLength(1000).IsRequired(false);
        builder.Property(x => x.ReviewSlaMinutes).HasColumnName("review_sla_minutes").IsRequired();
        builder.Property(x => x.ReviewDueAt).HasColumnName("review_due_at").IsRequired();
        builder.Property(x => x.EscalationDueAt).HasColumnName("escalation_due_at").IsRequired();
        builder.Property(x => x.ReminderIntervalMinutes).HasColumnName("reminder_interval_minutes").IsRequired();
        builder.Property(x => x.SettingsVersion).HasColumnName("settings_version").IsRequired();
        builder.Property(x => x.OverdueAt).HasColumnName("overdue_at").IsRequired(false);
        builder.Property(x => x.EscalatedAt).HasColumnName("escalated_at").IsRequired(false);
        builder.Property(x => x.LastReminderAt).HasColumnName("last_reminder_at").IsRequired(false);
        builder.Property(x => x.ReminderCount).HasColumnName("reminder_count").IsRequired();
        builder.Property(x => x.ResolvedAt).HasColumnName("resolved_at").IsRequired(false);
        builder.Property(x => x.ResolvedByUserId).HasColumnName("resolved_by_user_id").IsRequired(false);
        builder.Property(x => x.Resolution).HasColumnName("resolution").HasMaxLength(30).IsRequired(false);
        builder.Property(x => x.ResolutionReason).HasColumnName("resolution_reason").HasMaxLength(500).IsRequired(false);
        builder.Property(x => x.AppointmentId).HasColumnName("appointment_id").IsRequired(false);
        builder.Property(x => x.ReleasedAppointmentId).HasColumnName("released_appointment_id").IsRequired(false);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(x => x.Revision).HasColumnName("revision").IsConcurrencyToken().IsRequired();

        builder.HasIndex(x => x.SurveyRequestId).IsUnique()
            .HasFilter($"status IN ('{open}', '{pending}')")
            .HasDatabaseName(ActiveSurveyRequestIndex);
        builder.HasIndex(x => new { x.SurveyRequestId, x.CreatedAt }).HasDatabaseName("ix_dispatch_request_survey_request_created_at");
        // Staff queue and the worker's SLA sweeps.
        builder.HasIndex(x => new { x.Status, x.ReviewDueAt }).HasDatabaseName("ix_dispatch_request_status_review_due_at");
        builder.HasIndex(x => new { x.Status, x.EscalationDueAt }).HasDatabaseName("ix_dispatch_request_status_escalation_due_at");
        builder.HasIndex(x => x.OperationalZoneId).HasDatabaseName("ix_dispatch_request_operational_zone_id");

        builder.HasOne<SurveyRequest>().WithMany().HasForeignKey(x => x.SurveyRequestId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PropertySite>().WithMany().HasForeignKey(x => x.PropertySiteId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CustomerUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ResolvedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<OperationalZone>().WithMany().HasForeignKey(x => x.OperationalZoneId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_dispatch_request_operational_zone");
        builder.HasOne<TechnicianAppointment>().WithMany().HasForeignKey(x => x.AppointmentId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_dispatch_request_appointment");
        builder.HasOne<TechnicianAppointment>().WithMany().HasForeignKey(x => x.ReleasedAppointmentId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_dispatch_request_released_appointment");
        builder.HasIndex(x => x.ReleasedAppointmentId).HasDatabaseName("ix_dispatch_request_released_appointment_id");
    }
}

public sealed class DispatchPreferredWindowConfiguration : IEntityTypeConfiguration<DispatchPreferredWindow>
{
    public void Configure(EntityTypeBuilder<DispatchPreferredWindow> builder)
    {
        builder.ToTable("dispatch_preferred_window", t => t.HasCheckConstraint("ck_dispatch_preferred_window_range", "ends_at > starts_at"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.DispatchRequestId).HasColumnName("dispatch_request_id").IsRequired();
        builder.Property(x => x.StartsAt).HasColumnName("starts_at").IsRequired();
        builder.Property(x => x.EndsAt).HasColumnName("ends_at").IsRequired();
        builder.HasIndex(x => new { x.DispatchRequestId, x.StartsAt }).HasDatabaseName("ix_dispatch_preferred_window_request_starts_at");
        builder.HasOne<DispatchRequest>().WithMany().HasForeignKey(x => x.DispatchRequestId)
            .HasConstraintName("fk_dispatch_preferred_window_dispatch_request").OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class DispatchProposalConfiguration : IEntityTypeConfiguration<DispatchProposal>
{
    /// <summary>At most one live (PROPOSED) proposal per request.</summary>
    public const string LiveIndex = "ux_dispatch_proposal_live";

    /// <summary>At most one ACCEPTED proposal per request.</summary>
    public const string AcceptedIndex = "ux_dispatch_proposal_accepted";

    public void Configure(EntityTypeBuilder<DispatchProposal> builder)
    {
        builder.ToTable("dispatch_proposal", t =>
        {
            t.HasCheckConstraint("ck_dispatch_proposal_status", $"status IN ({DispatchSql.In(ProposalStatuses.All)})");
            t.HasCheckConstraint("ck_dispatch_proposal_range", "ends_at > starts_at");
            // expires_at = LEAST(created_at + window, starts_at): never after the start.
            t.HasCheckConstraint("ck_dispatch_proposal_expiry", "expires_at <= starts_at AND expires_at > created_at AND response_window_minutes > 0");
            t.HasCheckConstraint("ck_dispatch_proposal_cross_zone", "cross_zone = (cross_zone_authorization_id IS NOT NULL)");
            t.HasCheckConstraint("ck_dispatch_proposal_responded",
                $"status IN ('{ProposalStatuses.Proposed}', '{ProposalStatuses.Expired}') OR responded_at IS NOT NULL");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.DispatchRequestId).HasColumnName("dispatch_request_id").IsRequired();
        builder.Property(x => x.TechnicianUserId).HasColumnName("technician_user_id").IsRequired();
        builder.Property(x => x.StartsAt).HasColumnName("starts_at").IsRequired();
        builder.Property(x => x.EndsAt).HasColumnName("ends_at").IsRequired();
        builder.Property(x => x.CrossZone).HasColumnName("cross_zone").IsRequired();
        builder.Property(x => x.CrossZoneAuthorizationId).HasColumnName("cross_zone_authorization_id").IsRequired(false);
        builder.Property(x => x.ProposedByUserId).HasColumnName("proposed_by_user_id").IsRequired();
        builder.Property(x => x.Note).HasColumnName("note").HasMaxLength(500).IsRequired(false);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.ResponseWindowMinutes).HasColumnName("response_window_minutes").IsRequired();
        builder.Property(x => x.SettingsVersion).HasColumnName("settings_version").IsRequired();
        builder.Property(x => x.ExpiresAt).HasColumnName("expires_at").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        builder.Property(x => x.RespondedAt).HasColumnName("responded_at").IsRequired(false);
        builder.Property(x => x.FailureReason).HasColumnName("failure_reason").HasMaxLength(500).IsRequired(false);
        builder.Property(x => x.Revision).HasColumnName("revision").IsConcurrencyToken().IsRequired();

        builder.HasIndex(x => x.DispatchRequestId).IsUnique().HasFilter($"status = '{ProposalStatuses.Proposed}'").HasDatabaseName(LiveIndex);
        builder.HasIndex(x => x.DispatchRequestId).IsUnique().HasFilter($"status = '{ProposalStatuses.Accepted}'").HasDatabaseName(AcceptedIndex);
        // Expiry sweep.
        builder.HasIndex(x => new { x.Status, x.ExpiresAt }).HasDatabaseName("ix_dispatch_proposal_status_expires_at");
        builder.HasIndex(x => new { x.DispatchRequestId, x.CreatedAt }).HasDatabaseName("ix_dispatch_proposal_request_created_at");

        builder.HasOne<DispatchRequest>().WithMany().HasForeignKey(x => x.DispatchRequestId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.TechnicianUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ProposedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CrossZoneAuthorization>().WithMany().HasForeignKey(x => x.CrossZoneAuthorizationId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_dispatch_proposal_cross_zone_authorization");
    }
}

public sealed class CrossZoneAuthorizationConfiguration : IEntityTypeConfiguration<CrossZoneAuthorization>
{
    public void Configure(EntityTypeBuilder<CrossZoneAuthorization> builder)
    {
        builder.ToTable("cross_zone_authorization", t =>
        {
            // Exactly one subject: a dispatch request or an installation project.
            t.HasCheckConstraint("ck_cross_zone_authorization_subject", "(dispatch_request_id IS NULL) <> (installation_project_id IS NULL)");
            t.HasCheckConstraint("ck_cross_zone_authorization_reason", "length(reason) > 0");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.DispatchRequestId).HasColumnName("dispatch_request_id").IsRequired(false);
        builder.Property(x => x.InstallationProjectId).HasColumnName("installation_project_id").IsRequired(false);
        builder.Property(x => x.TechnicianUserId).HasColumnName("technician_user_id").IsRequired();
        builder.Property(x => x.TargetZoneId).HasColumnName("target_zone_id").IsRequired(false);
        builder.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(500).IsRequired();
        builder.Property(x => x.AuthorizedByUserId).HasColumnName("authorized_by_user_id").IsRequired();
        builder.Property(x => x.AuthorizedAt).HasColumnName("authorized_at").IsRequired();
        builder.HasIndex(x => x.DispatchRequestId).HasDatabaseName("ix_cross_zone_authorization_dispatch_request_id");
        builder.HasIndex(x => x.InstallationProjectId).HasDatabaseName("ix_cross_zone_authorization_installation_project_id");
        builder.HasIndex(x => new { x.TechnicianUserId, x.AuthorizedAt }).HasDatabaseName("ix_cross_zone_authorization_technician_authorized_at");
        builder.HasOne<DispatchRequest>().WithMany().HasForeignKey(x => x.DispatchRequestId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SmartSolar.Modules.Installations.InstallationProject>().WithMany().HasForeignKey(x => x.InstallationProjectId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_cross_zone_authorization_installation_project");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.TechnicianUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.AuthorizedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<OperationalZone>().WithMany().HasForeignKey(x => x.TargetZoneId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_cross_zone_authorization_target_zone");
    }
}

public sealed class DispatchEventConfiguration : IEntityTypeConfiguration<DispatchEvent>
{
    public void Configure(EntityTypeBuilder<DispatchEvent> builder)
    {
        builder.ToTable("dispatch_event");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.DispatchRequestId).HasColumnName("dispatch_request_id").IsRequired();
        builder.Property(x => x.ProposalId).HasColumnName("proposal_id").IsRequired(false);
        builder.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(40).IsRequired();
        builder.Property(x => x.ActorUserId).HasColumnName("actor_user_id").IsRequired(false);
        builder.Property(x => x.Detail).HasColumnName("detail").HasMaxLength(1000).IsRequired(false);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.HasIndex(x => new { x.DispatchRequestId, x.CreatedAt }).HasDatabaseName("ix_dispatch_event_request_created_at");
        builder.HasOne<DispatchRequest>().WithMany().HasForeignKey(x => x.DispatchRequestId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<DispatchProposal>().WithMany().HasForeignKey(x => x.ProposalId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_dispatch_event_proposal");
    }
}

public sealed class DispatchNotificationConfiguration : IEntityTypeConfiguration<DispatchNotification>
{
    public void Configure(EntityTypeBuilder<DispatchNotification> builder)
    {
        builder.ToTable("dispatch_notification");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.RecipientUserId).HasColumnName("recipient_user_id").IsRequired();
        builder.Property(x => x.DispatchRequestId).HasColumnName("dispatch_request_id").IsRequired();
        builder.Property(x => x.EventId).HasColumnName("event_id").IsRequired();
        builder.Property(x => x.Type).HasColumnName("type").HasMaxLength(60).IsRequired();
        builder.Property(x => x.DeliveryStatus).HasColumnName("delivery_status").HasMaxLength(30).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.HasIndex(x => new { x.RecipientUserId, x.CreatedAt }).HasDatabaseName("ix_dispatch_notification_recipient_created_at");
        builder.HasIndex(x => x.EventId).HasDatabaseName("ix_dispatch_notification_event_id");
        builder.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.RecipientUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<DispatchRequest>().WithMany().HasForeignKey(x => x.DispatchRequestId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<DispatchEvent>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict);
    }
}
