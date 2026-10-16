using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartSolar.Api.Contracts.InstallationWorkflow;
using SmartSolar.Api.Extensions;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.OnSiteSurveys;
using SmartSolar.Modules.Common.Features;

namespace SmartSolar.Api.Controllers;

/// <summary>Technician: on-site survey of an officially assigned, acknowledged appointment.</summary>
[ApiController]
[Route("api/technicians/me/appointments/{appointmentId:guid}/onsite-survey")]
[Authorize(Roles = RoleCodes.Technician)]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class TechnicianOnSiteSurveyController(OnSiteSurveyHandlers handlers) : OperationResultControllerBase
{
    [HttpGet]
    [EnableRateLimiting(RateLimitingExtensions.OnSiteSurveyReadPolicy)]
    public async Task<IActionResult> Get(Guid appointmentId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.GetForTechnicianAsync(user, appointmentId, cancellationToken)) : UnauthorizedFailure();

    [HttpPost("start")]
    [EnableRateLimiting(RateLimitingExtensions.OnSiteSurveyWritePolicy)]
    public async Task<IActionResult> Start(Guid appointmentId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.StartAsync(user, appointmentId, cancellationToken)) : UnauthorizedFailure();

    /// <summary>Creates or updates the draft. Send the current <c>revision</c> (omit only when nothing exists yet).</summary>
    [HttpPut]
    [EnableRateLimiting(RateLimitingExtensions.OnSiteSurveyWritePolicy)]
    public async Task<IActionResult> Save(Guid appointmentId, [FromBody] OnSiteSurveyRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await handlers.SaveAsync(new SaveOnSiteSurveyCommand(user, appointmentId, request.Revision, ToData(request)), cancellationToken))
            : UnauthorizedFailure();

    [HttpPost("attachments")]
    [EnableRateLimiting(RateLimitingExtensions.OnSiteSurveyWritePolicy)]
    public async Task<IActionResult> AddAttachment(Guid appointmentId, [FromBody] OnSiteSurveyAttachmentRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await handlers.AddAttachmentAsync(new AddOnSiteSurveyAttachmentCommand(user, appointmentId, request.FileId, request.Category ?? "", request.Caption), cancellationToken))
            : UnauthorizedFailure();

    [HttpDelete("attachments/{attachmentId:guid}")]
    [EnableRateLimiting(RateLimitingExtensions.OnSiteSurveyWritePolicy)]
    public async Task<IActionResult> RemoveAttachment(Guid appointmentId, Guid attachmentId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.RemoveAttachmentAsync(user, appointmentId, attachmentId, cancellationToken)) : UnauthorizedFailure();

    [HttpPost("complete")]
    [EnableRateLimiting(RateLimitingExtensions.OnSiteSurveyWritePolicy)]
    public async Task<IActionResult> Complete(Guid appointmentId, [FromBody] RevisionRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await handlers.CompleteAsync(new CompleteOnSiteSurveyCommand(user, appointmentId, request.Revision), cancellationToken))
            : UnauthorizedFailure();

    /// <summary>Correction of a completed survey. Fields left null are unchanged; history keeps the previous values.</summary>
    [HttpPost("amendments")]
    [EnableRateLimiting(RateLimitingExtensions.OnSiteSurveyWritePolicy)]
    public async Task<IActionResult> Amend(Guid appointmentId, [FromBody] OnSiteSurveyAmendmentRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await handlers.AmendAsync(new AmendOnSiteSurveyCommand(user, appointmentId, request.Reason ?? "",
                ToData(request.Changes ?? new OnSiteSurveyRequest(null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null)),
                request.AdditionalFileIds, request.Revision), cancellationToken))
            : UnauthorizedFailure();

    internal static OnSiteSurveyData ToData(OnSiteSurveyRequest r) => new(
        r.RoofType, r.RoofMaterial, r.RoofLengthM, r.RoofWidthM, r.UsableAreaM2, r.TiltDegree, r.AzimuthDegree, r.RoofCondition,
        r.ElectricalCabinetNotes, r.ElectricalConnectionNotes, r.InstallationFeasibility, r.ProposedInverterLocation, r.ProposedPanelLayoutNotes,
        r.MeasuredLatitude, r.MeasuredLongitude, r.Notes,
        r.Obstacles?.Select(o => new ObstacleInput(o.Kind ?? "", o.Description, o.HeightM, o.AreaM2)).ToList());
}

/// <summary>Sales (owner of the request) and Managers/Admins: read the completed on-site survey.</summary>
[ApiController]
[Route("api/survey-requests/{surveyRequestId:guid}/onsite-survey")]
[Authorize(Roles = $"{RoleCodes.Sales},{RoleCodes.Manager},{RoleCodes.Admin}")]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class StaffOnSiteSurveyController(OnSiteSurveyHandlers handlers) : OperationResultControllerBase
{
    [HttpGet]
    [EnableRateLimiting(RateLimitingExtensions.OnSiteSurveyReadPolicy)]
    public async Task<IActionResult> Get(Guid surveyRequestId, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        var manager = IsInRole(RoleCodes.Manager) || IsInRole(RoleCodes.Admin);
        return Respond(await handlers.GetForStaffAsync(surveyRequestId, user, manager, owner => owner == user, cancellationToken));
    }
}
