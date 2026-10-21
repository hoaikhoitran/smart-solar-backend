using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Files;

namespace SmartSolar.Modules.OnSiteSurveys;

/// <summary>
/// Short-lived read URLs for on-site survey attachments. The caller is authorized exactly like reading the survey itself
/// (the appointment's technician, or the request's Sales owner / a Manager / an Admin); only attachments of that survey are
/// signed. The object location comes from the stored file, never from the client.
/// </summary>
public sealed class OnSiteSurveyAttachmentUrlHandlers(OnSiteSurveyHandlers surveys, IFileStore files, FileReadUrlService urls)
{
    public async Task<OperationResult<AttachedFileUrlView>> ForTechnicianAsync(Guid technicianUserId, Guid appointmentId, Guid attachmentId,
        StorageUrlDisposition disposition, CancellationToken cancellationToken)
    {
        var survey = await surveys.GetForTechnicianAsync(technicianUserId, appointmentId, cancellationToken);
        if (!survey.Succeeded) return survey.As<AttachedFileUrlView>();
        return await SignAsync(survey.Value, attachmentId, disposition, cancellationToken);
    }

    public async Task<OperationResult<AttachedFileUrlView>> ForStaffAsync(Guid surveyRequestId, Guid actorUserId, bool isManager, Func<Guid?, bool> isOwner,
        Guid attachmentId, StorageUrlDisposition disposition, CancellationToken cancellationToken)
    {
        var survey = await surveys.GetForStaffAsync(surveyRequestId, actorUserId, isManager, isOwner, cancellationToken);
        if (!survey.Succeeded) return survey.As<AttachedFileUrlView>();
        return await SignAsync(survey.Value, attachmentId, disposition, cancellationToken);
    }

    private async Task<OperationResult<AttachedFileUrlView>> SignAsync(OnSiteSurveyView? survey, Guid attachmentId, StorageUrlDisposition disposition,
        CancellationToken cancellationToken)
    {
        var attachment = survey?.Attachments.FirstOrDefault(a => a.AttachmentId == attachmentId);
        var file = attachment is null ? null : await files.FindAsync(attachment.FileId, cancellationToken);
        if (attachment is null || file is null)
            return OperationResult<AttachedFileUrlView>.Fail(OnSiteSurveyErrorCodes.AttachmentNotFound, "Attachment was not found.");

        var signed = await urls.CreateAsync(file, FilePurposes.OnSiteSurveyEvidence, disposition, null, cancellationToken);
        return signed.Succeeded
            ? OperationResult<AttachedFileUrlView>.Ok(AttachedFileUrlView.From(attachmentId, signed.Value!))
            : signed.As<AttachedFileUrlView>();
    }
}
