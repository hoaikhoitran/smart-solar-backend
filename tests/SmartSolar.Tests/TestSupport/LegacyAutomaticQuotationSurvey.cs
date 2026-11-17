using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.OnSiteSurveys;
using SmartSolar.Modules.Quotations;

namespace SmartSolar.Tests.TestSupport;

/// <summary>
/// LEGACY test shim (2026-11-17). Before the simplified flow, completing an on-site survey ran the automatic quotation and returned its outcome.
/// Production no longer does that (the technician builds a cart), but the automatic-generation code is kept for legacy data and its tests. This
/// wrapper reproduces the old wiring explicitly: survey operations go to the real handlers, and a (repeated) completion then calls
/// <see cref="QuotationHandlers.GenerateStandardQuotationAsync"/> and reports its outcome as before.
/// </summary>
public sealed class LegacyAutomaticQuotationSurvey(OnSiteSurveyHandlers inner, QuotationHandlers quotations)
{
    public Task<OperationResult<OnSiteSurveyView>> StartAsync(Guid technicianUserId, Guid appointmentId, CancellationToken cancellationToken)
        => inner.StartAsync(technicianUserId, appointmentId, cancellationToken);

    public Task<OperationResult<OnSiteSurveyView>> SaveAsync(SaveOnSiteSurveyCommand command, CancellationToken cancellationToken) => inner.SaveAsync(command, cancellationToken);

    public Task<OperationResult<OnSiteSurveyView>> AddAttachmentAsync(AddOnSiteSurveyAttachmentCommand command, CancellationToken cancellationToken)
        => inner.AddAttachmentAsync(command, cancellationToken);

    public Task<OperationResult<OnSiteSurveyView?>> GetForTechnicianAsync(Guid technicianUserId, Guid appointmentId, CancellationToken cancellationToken)
        => inner.GetForTechnicianAsync(technicianUserId, appointmentId, cancellationToken);

    public Task<OperationResult<OnSiteSurveyView>> AmendAsync(AmendOnSiteSurveyCommand command, CancellationToken cancellationToken) => inner.AmendAsync(command, cancellationToken);

    public async Task<OperationResult<OnSiteSurveyView>> CompleteAsync(CompleteOnSiteSurveyCommand command, CancellationToken cancellationToken)
    {
        var completed = await inner.CompleteAsync(command, cancellationToken);
        if (!completed.Succeeded) return completed;
        string outcome;
        try
        {
            outcome = (await quotations.GenerateStandardQuotationAsync(completed.Value!.SurveyRequestId, command.TechnicianUserId, cancellationToken)).Outcome;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            outcome = AutomaticQuotationOutcomes.Failed;
        }
#pragma warning disable CS0618 // the retired outcome field is exactly what these legacy tests assert
        return OperationResult<OnSiteSurveyView>.Ok(completed.Value! with { AutomaticQuotationOutcome = outcome });
#pragma warning restore CS0618
    }
}
