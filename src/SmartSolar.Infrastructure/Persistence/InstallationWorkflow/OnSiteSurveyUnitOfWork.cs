using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.OnSiteSurveys;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.Scheduling.Constants;

namespace SmartSolar.Infrastructure.Persistence.InstallationWorkflow;

public sealed class OnSiteSurveyUnitOfWork(AppDbContext db) : TransactionalUnitOfWork(db), IOnSiteSurveyUnitOfWork
{
    public async Task<SurveyAppointmentContext?> GetActiveAppointmentAsync(Guid appointmentId, CancellationToken cancellationToken)
    {
        var row = await Db.TechnicianAppointments.AsNoTracking()
            .Where(a => a.Id == appointmentId && a.Status != AppointmentStatuses.Cancelled && a.ReplacedByAppointmentId == null && a.SurveyRequestId != null)
            .Join(Db.SurveyRequests, a => a.SurveyRequestId, s => s.Id, (a, s) => new { a, s.AssignedSaleId, s.Status })
            .Join(Db.PropertySites, x => x.a.PropertySiteId, p => p.Id, (x, p) => new { x.a, x.AssignedSaleId, x.Status, p.Latitude, p.Longitude })
            .FirstOrDefaultAsync(cancellationToken);
        return row is null
            ? null
            : new SurveyAppointmentContext(row.a.Id, row.a.SurveyRequestId!.Value, row.a.AssignedTechnicianUserId ?? Guid.Empty, row.a.Status,
                row.a.StartsAt, row.a.EndsAt, row.AssignedSaleId, row.Status.ToString(), row.a.PropertySiteId, row.Latitude, row.Longitude);
    }

    public Task<OnSiteSurveyReport?> FindReportBySurveyRequestAsync(Guid surveyRequestId, bool track, CancellationToken cancellationToken)
        => (track ? Db.OnSiteSurveyReports : Db.OnSiteSurveyReports.AsNoTracking()).FirstOrDefaultAsync(r => r.SurveyRequestId == surveyRequestId, cancellationToken);

    public Task<OnSiteSurveyReport?> FindReportAsync(Guid reportId, bool track, CancellationToken cancellationToken)
        => (track ? Db.OnSiteSurveyReports : Db.OnSiteSurveyReports.AsNoTracking()).FirstOrDefaultAsync(r => r.Id == reportId, cancellationToken);

    public async Task<IReadOnlyList<OnSiteSurveyObstacle>> GetObstaclesAsync(Guid reportId, CancellationToken cancellationToken)
        => await Db.OnSiteSurveyObstacles.AsNoTracking().Where(o => o.ReportId == reportId).OrderBy(o => o.SortOrder).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<OnSiteSurveyAttachment>> GetAttachmentsAsync(Guid reportId, CancellationToken cancellationToken)
        => (await Db.OnSiteSurveyAttachments.AsNoTracking().Where(a => a.ReportId == reportId).ToListAsync(cancellationToken)).OrderBy(a => a.AddedAt).ToList();

    public async Task<IReadOnlyList<OnSiteSurveyAmendment>> GetAmendmentsAsync(Guid reportId, CancellationToken cancellationToken)
        => await Db.OnSiteSurveyAmendments.AsNoTracking().Where(a => a.ReportId == reportId).OrderBy(a => a.AmendmentNumber).ToListAsync(cancellationToken);

    public Task<bool> IsFileAttachedAsync(Guid fileId, CancellationToken cancellationToken)
        => Db.OnSiteSurveyAttachments.AsNoTracking().AnyAsync(a => a.FileId == fileId, cancellationToken);

    public async Task ReplaceObstaclesAsync(Guid reportId, IReadOnlyList<OnSiteSurveyObstacle> obstacles, CancellationToken cancellationToken)
    {
        Db.OnSiteSurveyObstacles.RemoveRange(await Db.OnSiteSurveyObstacles.Where(o => o.ReportId == reportId).ToListAsync(cancellationToken));
        Db.OnSiteSurveyObstacles.AddRange(obstacles);
    }

    public async Task<bool> RemoveAttachmentAsync(Guid reportId, Guid attachmentId, CancellationToken cancellationToken)
    {
        var attachment = await Db.OnSiteSurveyAttachments.FirstOrDefaultAsync(a => a.Id == attachmentId && a.ReportId == reportId, cancellationToken);
        if (attachment is null) return false;
        Db.OnSiteSurveyAttachments.Remove(attachment);
        return true;
    }

    public async Task<bool> TryCompleteSurveyRequestAsync(Guid surveyRequestId, CancellationToken cancellationToken)
        => await Db.SurveyRequests
            .Where(s => s.Id == surveyRequestId && s.Status == SurveyRequestStatus.Scheduled)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, SurveyRequestStatus.Completed), cancellationToken) == 1;

    public void AddReport(OnSiteSurveyReport report) => Db.OnSiteSurveyReports.Add(report);
    public void AddAttachment(OnSiteSurveyAttachment attachment) => Db.OnSiteSurveyAttachments.Add(attachment);
    public void AddAmendment(OnSiteSurveyAmendment amendment) => Db.OnSiteSurveyAmendments.Add(amendment);
}
