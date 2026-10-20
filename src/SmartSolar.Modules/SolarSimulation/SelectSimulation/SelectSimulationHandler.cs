using System.Text;
using FluentValidation;
using Microsoft.Extensions.Logging;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.SolarSimulation.Constants;
using SmartSolar.Modules.SolarSimulation.Contracts.Caching;
using SmartSolar.Modules.SolarSimulation.Contracts.Persistence;
using SmartSolar.Modules.SolarSimulation.Contracts.Storage;
using SmartSolar.Modules.SolarSimulation.CreateSimulation;
using SmartSolar.Modules.SolarSimulation.Models;

namespace SmartSolar.Modules.SolarSimulation.SelectSimulation;

/// <summary>The customer chooses one cached result; <see cref="ExpectedGeometryVersion"/> is the version they are looking at.</summary>
public sealed record SelectSimulationCommand(Guid UserId, Guid PreSurveyId, Guid SimulationId, int? ExpectedGeometryVersion);

public sealed class SelectSimulationCommandValidator : AbstractValidator<SelectSimulationCommand>
{
    public SelectSimulationCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.PreSurveyId).NotEmpty();
        RuleFor(x => x.SimulationId).NotEmpty();
        RuleFor(x => x.ExpectedGeometryVersion).NotNull().GreaterThanOrEqualTo(0);
    }
}

public enum SelectSimulationResultOutcome
{
    Selected,
    PreSurveyNotFound,
    NotOwned,
    NotEditable,
    SourceChanged,
    SimulationNotFound,
    Expired,
    CacheUnavailable,
    Conflict
}

public sealed record SelectSimulationResult(SelectSimulationResultOutcome Outcome, SelectedSimulationView? Selection)
{
    public static SelectSimulationResult Fail(SelectSimulationResultOutcome outcome) => new(outcome, null);
}

public enum ClearSimulationSelectionOutcome
{
    Cleared,
    PreSurveyNotFound,
    NotOwned,
    NotEditable
}

/// <summary>
/// Explicit selection of a cached simulation result (creating a simulation never selects it).
/// <list type="number">
/// <item>Customer owner, pre-survey Draft, expected geometry version = current version.</item>
/// <item>The result must still be cached (else 410 SIMULATION_EXPIRED; outage 503), belong to this
/// pre-survey and owner, and have been calculated at that same geometry version.</item>
/// <item>Optional durable snapshot upload, BEFORE the database transaction: when the transaction then
/// fails (concurrent submit, geometry edit) the object is orphaned, which is acceptable (it is
/// unreferenced, keyed by simulation id, and can be removed by a bucket lifecycle rule). A failed
/// upload does not block the selection; the summary is recorded without a snapshot.</item>
/// <item>One transaction replaces the selected_simulation row and sets pre_survey.selected_simulation_id,
/// conditional on Draft + geometry version. Re-selecting the current selection is a no-op.</item>
/// </list>
/// After submission the selection is frozen (the conditional update requires Draft).
/// </summary>
public sealed class SelectSimulationHandler
{
    private readonly ISolarSimulationUnitOfWork _unitOfWork;
    private readonly ISimulationResultCache _cache;
    private readonly ISimulationSnapshotStore _snapshots;
    private readonly Options.SolarSimulationOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<SelectSimulationHandler> _logger;

    public SelectSimulationHandler(
        ISolarSimulationUnitOfWork unitOfWork,
        ISimulationResultCache cache,
        ISimulationSnapshotStore snapshots,
        Options.SolarSimulationOptions options,
        TimeProvider time,
        ILogger<SelectSimulationHandler> logger)
    {
        _unitOfWork = unitOfWork;
        _cache = cache;
        _snapshots = snapshots;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>Assumes the command passed <see cref="SelectSimulationCommandValidator"/>.</summary>
    public async Task<SelectSimulationResult> HandleAsync(SelectSimulationCommand command, CancellationToken cancellationToken)
    {
        var source = await _unitOfWork.GetSourceAsync(command.PreSurveyId, cancellationToken);
        if (source is null) return SelectSimulationResult.Fail(SelectSimulationResultOutcome.PreSurveyNotFound);
        if (source.OwnerUserId != command.UserId) return SelectSimulationResult.Fail(SelectSimulationResultOutcome.NotOwned);
        if (source.Status != PreSurveyStatus.Draft) return SelectSimulationResult.Fail(SelectSimulationResultOutcome.NotEditable);
        if (source.GeometryVersion != command.ExpectedGeometryVersion) return SelectSimulationResult.Fail(SelectSimulationResultOutcome.SourceChanged);

        CachedSimulation? cached;
        try
        {
            cached = await _cache.GetAsync(command.SimulationId, cancellationToken);
        }
        catch (SimulationCacheUnavailableException)
        {
            return SelectSimulationResult.Fail(SelectSimulationResultOutcome.CacheUnavailable);
        }

        if (cached is null) return SelectSimulationResult.Fail(SelectSimulationResultOutcome.Expired);
        if (cached.PreSurveyId != source.PreSurveyId || cached.OwnerUserId != source.OwnerUserId)
            return SelectSimulationResult.Fail(SelectSimulationResultOutcome.SimulationNotFound);
        if (cached.GeometryVersion != command.ExpectedGeometryVersion) return SelectSimulationResult.Fail(SelectSimulationResultOutcome.SourceChanged);

        if (source.SelectedSimulationId == cached.SimulationId
            && await _unitOfWork.GetSelectionAsync(source.PreSurveyId, cancellationToken) is { } current
            && current.SimulationId == cached.SimulationId)
        {
            return new(SelectSimulationResultOutcome.Selected, SimulationDetailMapper.ToView(current, source.GeometryVersion));
        }

        var snapshot = await TryUploadSnapshotAsync(cached, cancellationToken);
        var selection = SimulationDetailMapper.ToSelection(cached, command.UserId, _time.GetUtcNow(), snapshot);

        return await _unitOfWork.TrySelectAsync(selection, cancellationToken) switch
        {
            SelectSimulationOutcome.Selected => new(SelectSimulationResultOutcome.Selected,
                SimulationDetailMapper.ToView(selection, source.GeometryVersion, snapshot is null ? SnapshotStatuses.None : SnapshotStatuses.Verified)),
            SelectSimulationOutcome.NotDraft => SelectSimulationResult.Fail(SelectSimulationResultOutcome.NotEditable),
            SelectSimulationOutcome.Conflict => SelectSimulationResult.Fail(SelectSimulationResultOutcome.Conflict),
            _ => SelectSimulationResult.Fail(SelectSimulationResultOutcome.SourceChanged),
        };
    }

    public async Task<ClearSimulationSelectionOutcome> ClearAsync(Guid userId, Guid preSurveyId, CancellationToken cancellationToken)
    {
        var source = await _unitOfWork.GetSourceAsync(preSurveyId, cancellationToken);
        if (source is null) return ClearSimulationSelectionOutcome.PreSurveyNotFound;
        if (source.OwnerUserId != userId) return ClearSimulationSelectionOutcome.NotOwned;
        if (source.Status != PreSurveyStatus.Draft) return ClearSimulationSelectionOutcome.NotEditable;

        return await _unitOfWork.TryClearSelectionAsync(preSurveyId, cancellationToken) == ClearSelectionOutcome.Cleared
            ? ClearSimulationSelectionOutcome.Cleared
            : ClearSimulationSelectionOutcome.NotEditable;
    }

    private async Task<SimulationSnapshotLocation?> TryUploadSnapshotAsync(CachedSimulation cached, CancellationToken cancellationToken)
    {
        if (!_snapshots.IsEnabled) return null;

        var document = new SimulationSnapshotDocument(
            SimulationSnapshotDocument.CurrentSchema, cached.SimulationId, cached.PreSurveyId, cached.GeometryVersion,
            cached.Fingerprint, cached.CreatedAt, cached.Detail with { ExpiresAt = null, Source = SimulationSources.Snapshot });
        var content = Encoding.UTF8.GetBytes(SnapshotJson.Serialize(document));
        if (content.LongLength > _options.Snapshots.MaxSnapshotBytes)
        {
            _logger.LogWarning("Simulation {SimulationId} snapshot ({Size} bytes) exceeds the configured maximum; selected without a snapshot.",
                cached.SimulationId, content.LongLength);
            return null;
        }

        try
        {
            var location = await _snapshots.PutAsync(cached.PreSurveyId, cached.SimulationId, content, cancellationToken);
            // The store must report exactly what was written.
            return SnapshotIntegrity.Matches(content, location) ? location : null;
        }
        catch (SimulationSnapshotUnavailableException ex)
        {
            _logger.LogWarning("Simulation {SimulationId} snapshot upload failed ({Reason}); selected without a snapshot.",
                cached.SimulationId, ex.Message);
            return null;
        }
    }
}
