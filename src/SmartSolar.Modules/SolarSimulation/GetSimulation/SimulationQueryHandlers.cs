using Microsoft.Extensions.Logging;
using SmartSolar.Modules.SolarSimulation.Access;
using SmartSolar.Modules.SolarSimulation.Contracts.Caching;
using SmartSolar.Modules.SolarSimulation.Contracts.Persistence;
using SmartSolar.Modules.SolarSimulation.Contracts.Storage;
using SmartSolar.Modules.SolarSimulation.CreateSimulation;
using SmartSolar.Modules.SolarSimulation.Entities;
using SmartSolar.Modules.SolarSimulation.Models;

namespace SmartSolar.Modules.SolarSimulation.GetSimulation;

public enum SimulationQueryOutcome
{
    /// <summary>Full payload (cache or verified snapshot) in <see cref="GetSimulationResult.Simulation"/>.</summary>
    Found,

    /// <summary>Only the durable selected summary is available (<see cref="GetSimulationResult.Summary"/>).</summary>
    SummaryOnly,
    PreSurveyNotFound,
    Forbidden,

    /// <summary>The id belongs to another pre-survey.</summary>
    SimulationNotFound,

    /// <summary>Not cached (expired or never existed) and not the stored selection.</summary>
    Expired,

    /// <summary>The cache could not be reached and the id is not the stored selection.</summary>
    CacheUnavailable
}

public sealed record GetSimulationResult(SimulationQueryOutcome Outcome, SimulationDetail? Simulation, SelectedSimulationView? Summary = null);

public sealed record ListSimulationsResult(SimulationQueryOutcome Outcome, SimulationListView? Simulations);

/// <summary>
/// Reads a simulation; never calls PVGIS or NASA POWER. Order: the cached full result (stale ones
/// stay readable while cached); else, when the id is the stored selection, its verified durable
/// snapshot or its compact summary; else 410 SIMULATION_EXPIRED. A cache outage is reported as
/// such (503), except for the stored selection, whose durable data does not depend on the cache.
/// </summary>
public sealed class GetSimulationHandler
{
    private readonly ISolarSimulationUnitOfWork _unitOfWork;
    private readonly ISimulationResultCache _cache;
    private readonly ISimulationSnapshotStore _snapshots;
    private readonly ILogger<GetSimulationHandler> _logger;

    public GetSimulationHandler(
        ISolarSimulationUnitOfWork unitOfWork,
        ISimulationResultCache cache,
        ISimulationSnapshotStore snapshots,
        ILogger<GetSimulationHandler> logger)
    {
        _unitOfWork = unitOfWork;
        _cache = cache;
        _snapshots = snapshots;
        _logger = logger;
    }

    public async Task<GetSimulationResult> HandleAsync(SimulationViewer viewer, Guid preSurveyId, Guid simulationId, CancellationToken cancellationToken)
    {
        var source = await _unitOfWork.GetSourceAsync(preSurveyId, cancellationToken);
        if (source is null) return new(SimulationQueryOutcome.PreSurveyNotFound, null);
        if (!SimulationAccess.CanRead(source, viewer)) return new(SimulationQueryOutcome.Forbidden, null);

        var cacheDown = false;
        try
        {
            var cached = await _cache.GetAsync(simulationId, cancellationToken);
            if (cached is not null)
            {
                return cached.PreSurveyId == preSurveyId
                    ? new(SimulationQueryOutcome.Found, SimulationDetailMapper.ForViewer(cached, source))
                    : new(SimulationQueryOutcome.SimulationNotFound, null);
            }
        }
        catch (SimulationCacheUnavailableException)
        {
            cacheDown = true;
        }

        if (source.SelectedSimulationId == simulationId
            && await _unitOfWork.GetSelectionAsync(preSurveyId, cancellationToken) is { } selection
            && selection.SimulationId == simulationId)
        {
            return await FromSelectionAsync(selection, source, cancellationToken);
        }

        return new(cacheDown ? SimulationQueryOutcome.CacheUnavailable : SimulationQueryOutcome.Expired, null);
    }

    private async Task<GetSimulationResult> FromSelectionAsync(SelectedSimulation selection, SimulationSource source, CancellationToken cancellationToken)
    {
        if (selection.SnapshotObjectKey is null)
        {
            return new(SimulationQueryOutcome.SummaryOnly, null, SimulationDetailMapper.ToView(selection, source.GeometryVersion));
        }

        var location = new SimulationSnapshotLocation(
            selection.SnapshotBucket!, selection.SnapshotObjectKey, selection.SnapshotSha256!, selection.SnapshotSizeBytes!.Value);
        var read = await _snapshots.GetAsync(location, cancellationToken);
        string status;
        if (read.Status == SnapshotReadStatus.Found && read.Content is { } content)
        {
            if (SnapshotIntegrity.Matches(content, location))
            {
                var document = SnapshotJson.Deserialize<SimulationSnapshotDocument>(System.Text.Encoding.UTF8.GetString(content));
                if (document.SimulationId == selection.SimulationId && document.PreSurveyId == selection.PreSurveyId)
                {
                    return new(SimulationQueryOutcome.Found, document.Detail with
                    {
                        IsStale = document.GeometryVersion != source.GeometryVersion,
                        IsSelected = true,
                        ExpiresAt = null,
                        Source = SimulationSources.Snapshot,
                    });
                }
            }

            // Tampered or replaced object: never served.
            _logger.LogError("Simulation snapshot {ObjectKey} failed integrity verification.", location.ObjectKey);
            status = SnapshotStatuses.IntegrityFailed;
        }
        else
        {
            status = read.Status == SnapshotReadStatus.Missing ? SnapshotStatuses.Missing : SnapshotStatuses.Unavailable;
        }

        return new(SimulationQueryOutcome.SummaryOnly, null, SimulationDetailMapper.ToView(selection, source.GeometryVersion, status));
    }
}

public sealed class ListSimulationsHandler
{
    private readonly ISolarSimulationUnitOfWork _unitOfWork;
    private readonly ISimulationResultCache _cache;

    public ListSimulationsHandler(ISolarSimulationUnitOfWork unitOfWork, ISimulationResultCache cache)
    {
        _unitOfWork = unitOfWork;
        _cache = cache;
    }

    /// <summary>
    /// Cached results of this pre-survey, newest first (created_at, then id), plus the durable selected
    /// summary. Expired results simply drop out of the list; the selected summary never does.
    /// </summary>
    public async Task<ListSimulationsResult> HandleAsync(SimulationViewer viewer, Guid preSurveyId, CancellationToken cancellationToken)
    {
        var source = await _unitOfWork.GetSourceAsync(preSurveyId, cancellationToken);
        if (source is null) return new(SimulationQueryOutcome.PreSurveyNotFound, null);
        if (!SimulationAccess.CanRead(source, viewer)) return new(SimulationQueryOutcome.Forbidden, null);

        SelectedSimulationView? selected = null;
        if (source.SelectedSimulationId is { } selectedId
            && await _unitOfWork.GetSelectionAsync(preSurveyId, cancellationToken) is { } selection
            && selection.SimulationId == selectedId)
        {
            selected = SimulationDetailMapper.ToView(selection, source.GeometryVersion);
        }

        var items = new List<SimulationListItem>();
        var cacheAvailable = true;
        try
        {
            foreach (var id in await _cache.GetRecentIdsAsync(preSurveyId, cancellationToken))
            {
                if (await _cache.GetAsync(id, cancellationToken) is { } cached && cached.PreSurveyId == preSurveyId)
                {
                    items.Add(SimulationDetailMapper.ToListItem(cached, source));
                }
            }
        }
        catch (SimulationCacheUnavailableException)
        {
            cacheAvailable = false;
            items.Clear();
        }

        var ordered = items
            .DistinctBy(x => x.SimulationId)
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.SimulationId)
            .ToList();
        return new(SimulationQueryOutcome.Found, new SimulationListView(ordered, selected, cacheAvailable));
    }
}
