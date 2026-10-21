using SmartSolar.Modules.Common.Persistence;

namespace SmartSolar.Modules.OnSiteSurveys;

/// <summary>The Flow 1 appointment a technician surveys, as seen by Installation Flow.</summary>
public sealed record SurveyAppointmentContext(
    Guid AppointmentId, Guid SurveyRequestId, Guid TechnicianUserId, string AppointmentStatus,
    DateTimeOffset StartsAt, DateTimeOffset EndsAt, Guid? AssignedSaleId, string SurveyRequestStatus,
    Guid PropertySiteId, decimal? SiteLatitude, decimal? SiteLongitude);

public interface IOnSiteSurveyUnitOfWork : ITransactionalUnitOfWork
{
    /// <summary>The ACTIVE (not cancelled/replaced) appointment, or null.</summary>
    Task<SurveyAppointmentContext?> GetActiveAppointmentAsync(Guid appointmentId, CancellationToken cancellationToken);

    /// <summary>
    /// Locks the technician's scheduling profile inside the current transaction (the lock staff reassignment takes), so an
    /// on-site write and a reassignment of the same appointment are serialised.
    /// </summary>
    Task LockTechnicianAsync(Guid technicianUserId, CancellationToken cancellationToken);

    Task<OnSiteSurveyReport?> FindReportBySurveyRequestAsync(Guid surveyRequestId, bool track, CancellationToken cancellationToken);
    Task<OnSiteSurveyReport?> FindReportAsync(Guid reportId, bool track, CancellationToken cancellationToken);
    Task<IReadOnlyList<OnSiteSurveyObstacle>> GetObstaclesAsync(Guid reportId, CancellationToken cancellationToken);
    Task<IReadOnlyList<OnSiteSurveyAttachment>> GetAttachmentsAsync(Guid reportId, CancellationToken cancellationToken);
    Task<IReadOnlyList<OnSiteSurveyAmendment>> GetAmendmentsAsync(Guid reportId, CancellationToken cancellationToken);
    Task<bool> IsFileAttachedAsync(Guid fileId, CancellationToken cancellationToken);
    Task ReplaceObstaclesAsync(Guid reportId, IReadOnlyList<OnSiteSurveyObstacle> obstacles, CancellationToken cancellationToken);
    Task<bool> RemoveAttachmentAsync(Guid reportId, Guid attachmentId, CancellationToken cancellationToken);

    /// <summary>
    /// Moves the appointment from ACCEPTED/ASSIGNED to COMPLETED (conditional update, sets completed_at) only while
    /// <paramref name="technicianUserId"/> is still its confirmed technician; false otherwise (e.g. reassigned meanwhile).
    /// </summary>
    Task<bool> TryCompleteAppointmentAsync(Guid appointmentId, Guid technicianUserId, DateTimeOffset completedAt, CancellationToken cancellationToken);

    /// <summary>Moves the survey request from Scheduled to Completed (conditional update); false when it was not Scheduled.</summary>
    Task<bool> TryCompleteSurveyRequestAsync(Guid surveyRequestId, CancellationToken cancellationToken);

    void AddReport(OnSiteSurveyReport report);
    void AddAttachment(OnSiteSurveyAttachment attachment);
    void AddAmendment(OnSiteSurveyAmendment amendment);
}
