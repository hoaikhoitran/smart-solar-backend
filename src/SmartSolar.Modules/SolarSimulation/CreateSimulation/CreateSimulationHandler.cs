using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Catalog.Constants;
using SmartSolar.Modules.Catalog.Contracts.Persistence;
using SmartSolar.Modules.Catalog.Installation;
using SmartSolar.Modules.Catalog.Models;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.PreSurvey.Surface;
using SmartSolar.Modules.SolarSimulation.Calculation;
using SmartSolar.Modules.SolarSimulation.Constants;
using SmartSolar.Modules.SolarSimulation.Contracts.Caching;
using SmartSolar.Modules.SolarSimulation.Contracts.Persistence;
using SmartSolar.Modules.SolarSimulation.Energy;
using SmartSolar.Modules.SolarSimulation.Geometry;
using SmartSolar.Modules.SolarSimulation.Installation;
using SmartSolar.Modules.SolarSimulation.Models;
using SmartSolar.Modules.SolarSimulation.Options;
using SmartSolar.Modules.SolarSimulation.Regional;
using SmartSolar.Modules.SolarSimulation.Shading;

namespace SmartSolar.Modules.SolarSimulation.CreateSimulation;

/// <summary>
/// Calculates a simulation for a draft pre-survey owned by the caller and caches the full result
/// temporarily (<c>SolarSimulation:ResultTtlMinutes</c>). Nothing is written to PostgreSQL and
/// nothing is selected: the customer selects a result explicitly (SelectSimulationHandler).
/// <list type="number">
/// <item>Validate ownership, Draft status, expected geometry version, surface and product.</item>
/// <item>Resolve mounting and installation values; compute the input fingerprint.</item>
/// <item>Reuse an identical reusable cached result when one exists within its TTL (no provider calls).</item>
/// <item>Otherwise calculate once per fingerprint (single-flight), call providers within a bounded budget,
/// re-check Draft status and geometry version, and cache the result.</item>
/// </list>
/// Provider failures become explicit statuses; energy is never fabricated. Results with a failed
/// provider are cached (readable, selectable) but not indexed for reuse, so a retry recalculates.
/// A cache outage fails the request with <see cref="CreateSimulationOutcome.CacheUnavailable"/>.
/// <para>
/// Energy and climate (2026-11-23): one 2025 Ho Chi Minh City regional baseline (<see cref="IRegionalSolarBaselineProvider"/>,
/// area-weighted over verified sample points) is applied to every site in province 79 and scaled to the site's installed kWp; the
/// site's own coordinates are used only for local shading geometry (as monthly loss fractions) and rack row spacing, never to pick a
/// different irradiance baseline. Sites outside (or not known to be inside) the territory get UNAVAILABLE energy and climate.
/// </para>
/// </summary>
public sealed class CreateSimulationHandler
{
    private const string EnergyDisclaimer =
        "Ước tính trung bình khu vực TP.HCM (sau sáp nhập): typical-year regional estimate, the area-weighted average of PVGIS over the 2025 "
        + "Ho Chi Minh City territory scaled to this system's installed capacity. Not a forecast for a specific year, not a measurement at this roof "
        + "and not a production guarantee.";

    private const string ClimateDisclaimer =
        "Area-weighted average of NASA POWER historical monthly means over the 2025 Ho Chi Minh City territory for the stated period. "
        + "Context only; not a forecast, not measured weather and not applied to the energy estimate.";

    private readonly ISolarSimulationUnitOfWork _unitOfWork;
    private readonly ICatalogUnitOfWork _catalog;
    private readonly IPvEnergyEstimator _pvEnergy;
    private readonly IClimateContextProvider _climate;
    private readonly IRegionalSolarBaselineProvider _regional;
    private readonly SolarSimulationOptions _options;
    private readonly SimulationSingleFlight _singleFlight;
    private readonly ISimulationResultCache _cache;
    private readonly TimeProvider _time;
    private readonly ILogger<CreateSimulationHandler> _logger;

    public CreateSimulationHandler(
        ISolarSimulationUnitOfWork unitOfWork,
        ICatalogUnitOfWork catalog,
        IPvEnergyEstimator pvEnergy,
        IClimateContextProvider climate,
        IRegionalSolarBaselineProvider regional,
        SolarSimulationOptions options,
        SimulationSingleFlight singleFlight,
        ISimulationResultCache cache,
        TimeProvider time,
        ILogger<CreateSimulationHandler> logger)
    {
        _unitOfWork = unitOfWork;
        _catalog = catalog;
        _pvEnergy = pvEnergy;
        _climate = climate;
        _regional = regional;
        _options = options;
        _singleFlight = singleFlight;
        _cache = cache;
        _time = time;
        _logger = logger;
    }

    /// <summary>Assumes the command passed <see cref="CreateSimulationCommandValidator"/>.</summary>
    public async Task<CreateSimulationResult> HandleAsync(CreateSimulationCommand command, CancellationToken cancellationToken)
    {
        var source = await _unitOfWork.GetSourceAsync(command.PreSurveyId, cancellationToken);
        if (source is null)
        {
            return CreateSimulationResult.Fail(CreateSimulationOutcome.PreSurveyNotFound);
        }

        if (source.OwnerUserId != command.UserId)
        {
            return CreateSimulationResult.Fail(CreateSimulationOutcome.NotOwned);
        }

        if (source.Status != PreSurveyStatus.Draft)
        {
            return CreateSimulationResult.Fail(CreateSimulationOutcome.NotEditable);
        }

        if (source.GeometryVersion != command.ExpectedGeometryVersion)
        {
            return CreateSimulationResult.Fail(CreateSimulationOutcome.SourceChanged, SimulationErrorCodes.SourceChanged);
        }

        // The separate surface step (PUT …/surface: rectangle + obstacles) is optional since 2026-11-17. Without it the layout uses the
        // declared usable area of the pre-survey as a square surface without obstacles, with the declared tilt and azimuth.
        var surface = EffectiveSurface(source, _options.Limits.MaxSurfaceSideM);
        if (surface is not var (length, width, obstacles) || source.SurfaceTiltDegree is not { } surfaceTilt || source.SurfaceAzimuthDegree is not { } surfaceAzimuthRaw)
        {
            return CreateSimulationResult.Fail(CreateSimulationOutcome.SurfaceNotDefined, SimulationErrorCodes.SurfaceNotDefined);
        }

        var surfaceAzimuth = (decimal)AzimuthConvention.NormalizeCompass((double)surfaceAzimuthRaw);

        var product = await _catalog.FindActiveProductAsync(command.ProductId, cancellationToken);
        if (product is null)
        {
            return CreateSimulationResult.Fail(CreateSimulationOutcome.ProductNotFound, SimulationErrorCodes.ProductNotFound);
        }

        if (ProductTypes.Normalize(product.ProductType) != ProductTypes.SolarPanel)
        {
            return Reject(SimulationErrorCodes.ProductNotSolarPanel, "productId", "The selected product is not a SOLAR_PANEL.");
        }

        if (product.RatedPowerW is not > 0 || product.WidthMm is not > 0 || product.HeightMm is not > 0)
        {
            return Reject(SimulationErrorCodes.ProductSpecIncomplete, "productId",
                "The selected product has no valid rated power, width or height in the Catalog.");
        }

        // Mounting: FLUSH follows the surface; RACK uses absolute panel angles.
        decimal panelTilt, panelAzimuth;
        if (command.MountingType == MountingTypes.Flush)
        {
            if ((command.PanelTiltDegree is { } t && t != surfaceTilt)
                || (command.PanelAzimuthDegree is { } a && AzimuthConvention.Difference((double)a, (double)surfaceAzimuth) > 0.01))
            {
                return CreateSimulationResult.Fail(CreateSimulationOutcome.Rejected, SimulationErrorCodes.ValidationFailed,
                    [new SimulationProblem(SimulationErrorCodes.ValidationFailed, "panelTiltDegree",
                        "FLUSH panels follow the surface; omit panel angles or send the surface tilt and azimuth.")]);
            }

            panelTilt = surfaceTilt;
            panelAzimuth = surfaceAzimuth;
        }
        else
        {
            panelTilt = command.PanelTiltDegree!.Value;
            panelAzimuth = (decimal)AzimuthConvention.NormalizeCompass((double)command.PanelAzimuthDegree!.Value);
        }

        var surfaceFrame = new SurfaceFrame((double)surfaceTilt, (double)surfaceAzimuth);
        var panelOrientation = new PanelOrientation((double)panelTilt, (double)panelAzimuth);
        if (PanelFootprintCalculator.FacesIntoSurface(surfaceFrame, panelOrientation))
        {
            return Reject(SimulationErrorCodes.PanelFacesIntoSurface, "panelAzimuthDegree",
                "The panel's front would face into the installation surface; this orientation cannot be mounted.");
        }

        if (PanelFootprintCalculator.HasDegenerateRowAxis(surfaceFrame, panelOrientation))
        {
            return Reject(SimulationErrorCodes.PanelOrientationUnsupported, "panelAzimuthDegree",
                "The panel's horizontal edge is perpendicular to the surface; V1 cannot lay out this orientation.");
        }

        var specParse = ProductInstallationSpecParser.Parse(product.Spec);
        if (specParse.Present && !specParse.IsValid)
        {
            _logger.LogWarning("Product {ProductId} has an invalid installation block that was ignored: {Errors}",
                product.Id, string.Join(" ", specParse.Errors));
        }
        var hasLocation = source.Latitude is not null && source.Longitude is not null;
        var resolution = InstallationResolver.Resolve(
            command.Installation, specParse.Spec, _options.PreliminaryDefaults, command.MountingType!,
            shadeEstimateAvailable: command.MountingType == MountingTypes.Rack && hasLocation);

        if (resolution.Installation is null)
        {
            return CreateSimulationResult.Fail(CreateSimulationOutcome.Rejected, resolution.Errors[0].Code,
                resolution.Errors.Select(e => new SimulationProblem(e.Code, e.Parameter, e.Message)).ToList());
        }

        var installation = resolution.Installation;
        var systemLoss = command.SystemLossPercent ?? _options.DefaultSystemLossPercent;
        var window = new ShadingDesignWindow(
            [_options.Shading.DeclinationDegree, -_options.Shading.DeclinationDegree],
            _options.Shading.StartSolarHour, _options.Shading.EndSolarHour, _options.Shading.StepMinutes);

        // The regional model applies by the site's official province code (or, without one, its coordinates inside the verified boundary).
        var membership = _regional.ResolveMembership(source.ProvinceCode, source.Latitude, source.Longitude);

        var fingerprint = SimulationFingerprint.Compute(new Dictionary<string, object?>
        {
            ["fingerprintVersion"] = AlgorithmVersions.Fingerprint,
            ["algorithmVersion"] = AlgorithmVersions.Current,
            ["pvgisMountingMapping"] = AlgorithmVersions.PvgisMountingMapping,
            ["preSurveyId"] = source.PreSurveyId,
            ["geometryVersion"] = source.GeometryVersion,
            ["surface"] = new { length, width, surfaceTilt, surfaceAzimuth },
            ["obstacles"] = obstacles,
            ["location"] = new { source.Latitude, source.Longitude },
            ["declaredTotalAreaM2"] = source.DeclaredTotalAreaM2,
            ["product"] = new { product.Id, product.Sku, product.RatedPowerW, product.WidthMm, product.HeightMm, product.Name, product.Brand, product.Model },
            ["productInstallationSpec"] = new { specParse.Present, Valid = specParse.IsValid, specParse.Spec },
            ["mounting"] = new { command.MountingType, panelTilt, panelAzimuth },
            ["installation"] = installation,
            ["systemLossPercent"] = systemLoss,
            ["systemLossSpecified"] = command.SystemLossPercent.HasValue,
            ["pvSettings"] = _pvEnergy.Settings,
            ["climateSettings"] = _climate.Settings,
            ["estimateBasis"] = EstimateBases.HcmcRegionalAreaWeighted,
            ["regionalBaseline"] = _regional.Fingerprint,
            ["regionMembership"] = membership,
            ["shadingWindow"] = window,
            ["shadingAnalysis"] = _options.ShadingAnalysis,
            ["layoutOffsetSamples"] = _options.LayoutOffsetSamples,
            ["maxPlacements"] = _options.Limits.MaxPlacements,
            ["rackSupportHeightWarningMm"] = _options.RackSupportHeightWarningMm,
            ["declaredAreaMismatchRatio"] = _options.DeclaredAreaMismatchRatio,
        });

        CachedSimulation? reusable;
        try
        {
            reusable = await _cache.FindByFingerprintAsync(source.PreSurveyId, fingerprint, cancellationToken);
        }
        catch (SimulationCacheUnavailableException)
        {
            return CreateSimulationResult.Fail(CreateSimulationOutcome.CacheUnavailable, SimulationErrorCodes.CacheUnavailable);
        }

        if (reusable is not null && reusable.OwnerUserId == source.OwnerUserId && reusable.GeometryVersion == source.GeometryVersion)
        {
            return CreateSimulationResult.Success(CreateSimulationOutcome.Reused, SimulationDetailMapper.ForViewer(reusable, source));
        }

        var input = new SimulationGeometryInput(
            length, width, surfaceTilt, surfaceAzimuth, obstacles,
            product.WidthMm!.Value, product.HeightMm!.Value,
            command.MountingType!, panelTilt, panelAzimuth, installation,
            source.Latitude, window, _options.LayoutOffsetSamples, _options.Limits.MaxPlacements,
            _options.RackSupportHeightWarningMm, _options.Limits.MaxLayoutWorkUnits);

        // Cheap, pure pre-check so an oversized layout never reaches the providers.
        var layout = SimulationCalculator.Calculate(input);
        if (layout.ExceededMaxPlacements)
        {
            return Reject(SimulationErrorCodes.LayoutLimitExceeded, null,
                $"The layout would exceed {_options.Limits.MaxPlacements} panels, the configured V1 computational limit.");
        }

        if (layout.ExceededComputationBudget)
        {
            return Reject(SimulationErrorCodes.LayoutLimitExceeded, null,
                "The layout calculation exceeded the configured computational budget for this surface and panel size.");
        }

        // The identity is assigned inside the flight, so every caller sharing it reports the same result;
        // only the caller whose factory ran reports Created, the others Reused.
        var ranHere = false;
        var computed = await _singleFlight.RunAsync(
            fingerprint,
            async () =>
            {
                ranHere = true;
                var result = await ComputeAsync(layout, input, product, specParse, installation, systemLoss, command.SystemLossPercent.HasValue, source, command.MountingType!, obstacles, surfaceFrame, membership);
                return result with { SimulationId = Guid.NewGuid(), CreatedAt = _time.GetUtcNow() };
            },
            cancellationToken);

        // Re-check after the (slow) provider calls: a result for a superseded geometry or a submitted
        // pre-survey is not cached at all.
        var current = await _unitOfWork.GetSourceAsync(source.PreSurveyId, cancellationToken);
        if (current is null || current.Status != PreSurveyStatus.Draft)
        {
            return CreateSimulationResult.Fail(CreateSimulationOutcome.NotEditable);
        }

        if (current.GeometryVersion != source.GeometryVersion)
        {
            return CreateSimulationResult.Fail(CreateSimulationOutcome.SourceChanged, SimulationErrorCodes.SourceChanged);
        }

        var detail = SimulationDetailMapper.ToDetail(
            computed, computed.SimulationId, source.PreSurveyId, source.GeometryVersion, computed.CreatedAt, product,
            length, width, surfaceTilt, surfaceAzimuth, source.Latitude, source.Longitude, command.MountingType!, panelTilt, panelAzimuth);
        var cached = new CachedSimulation(
            computed.SimulationId, source.PreSurveyId, source.OwnerUserId, command.UserId, source.GeometryVersion, fingerprint,
            computed.IsReusable, computed.CreatedAt, computed.CreatedAt.AddMinutes(_options.ResultTtlMinutes), detail);

        try
        {
            // Idempotent: callers sharing one flight store the same entry.
            await _cache.StoreAsync(cached, cancellationToken);
        }
        catch (SimulationCacheUnavailableException)
        {
            return CreateSimulationResult.Fail(CreateSimulationOutcome.CacheUnavailable, SimulationErrorCodes.CacheUnavailable);
        }

        return CreateSimulationResult.Success(
            ranHere ? CreateSimulationOutcome.Created : CreateSimulationOutcome.Reused,
            SimulationDetailMapper.ForViewer(cached, current));
    }

    private static CreateSimulationResult Reject(string code, string? parameter, string message)
        => CreateSimulationResult.Fail(CreateSimulationOutcome.Rejected, code, [new SimulationProblem(code, parameter, message)]);

    private async Task<ComputedSimulation> ComputeAsync(
        SimulationLayoutResult layout,
        SimulationGeometryInput input,
        ProductDto product,
        InstallationSpecParseResult specParse,
        ResolvedInstallation installation,
        decimal systemLoss,
        bool systemLossSpecified,
        SimulationSource source,
        string mountingType,
        IReadOnlyList<SurfaceObstacle> obstacles,
        SurfaceFrame surface,
        RegionMembership membership)
    {
        var panelCount = layout.Placements.Count;
        var kwp = Math.Round(panelCount * product.RatedPowerW!.Value / 1000m, 3);
        // Site coordinates: local shading geometry and rack row spacing only. The energy / climate baseline is regional.
        var hasLocation = source.Latitude is not null && source.Longitude is not null;
        var inRegion = _regional.Enabled && membership.InRegion;
        var regionReason = !_regional.Enabled ? RegionalFailureCodes.Disabled
            : membership.Status == RegionMembershipStatus.OutOfRegion ? RegionalFailureCodes.RegionNotSupported
            : membership.Status == RegionMembershipStatus.Unknown ? RegionalFailureCodes.RegionUnknown
            : null;

        // One budget for every provider call; independent of any single HTTP request so a shared
        // (single-flight) calculation is not cancelled when one caller disconnects.
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(_options.ProviderBudgetSeconds));

        var scenarios = new List<(string Role, string Place)>();
        if (panelCount > 0 && inRegion)
        {
            if (mountingType == MountingTypes.Flush)
            {
                scenarios.Add((EnergyScenarioRoles.PrimaryConservativeReference, PvgisMountingPlaces.Building));
                scenarios.Add((EnergyScenarioRoles.Comparison, PvgisMountingPlaces.Free));
            }
            else
            {
                scenarios.Add((EnergyScenarioRoles.Primary, PvgisMountingPlaces.Free));
            }
        }

        // One city-wide 1 kWp baseline per mounting place (shared cache, bounded provider concurrency), scaled to this system below.
        var energyTasks = scenarios.Select(s => SafeRegionalPvAsync(new RegionalPvRequest(systemLoss, input.PanelTiltDegree, input.PanelAzimuthDegree, s.Place),
            budget.Token)).ToList();
        var climateTask = inRegion
            ? SafeRegionalClimateAsync(budget.Token)
            : Task.FromResult<RegionalClimateResult?>(null);

        // Shading: decided before the provider calls; the hourly series uses exactly the primary scenario's system parameters.
        var shadingOptions = _options.ShadingAnalysis;
        var shadingPlan = ShadingAnalysis.Plan(layout, input, product.RatedPowerW!.Value, hasLocation, shadingOptions);
        var hourlyTask = shadingPlan.Kind == ShadingPlanKind.RayCast && scenarios.Count > 0
            ? SafeHourlyAsync(new PvHourlyRequest(source.Latitude!.Value, source.Longitude!.Value, kwp, systemLoss, input.PanelTiltDegree,
                input.PanelAzimuthDegree, scenarios[0].Place, shadingOptions.StartYear, shadingOptions.EndYear), budget.Token)
            : Task.FromResult<PvHourlyResult?>(null);

        await Task.WhenAll(energyTasks.Cast<Task>().Append(climateTask).Append(hourlyTask));

        var warnings = new List<SimulationWarning>
        {
            new(SimulationWarningCodes.StructureNotAssessed, "Structural capacity, attachment and support design are not assessed."),
            new(SimulationWarningCodes.MountingSystemRequirementsUnavailable, "Mounting-system installation requirements are not available in the catalog and were not applied."),
            new(SimulationWarningCodes.BuildingFireCodeNotChecked, "Building and fire code setbacks or access requirements are not checked."),
        };
        if (panelCount == 0)
        {
            warnings.Add(new(SimulationWarningCodes.LayoutInfeasible,
                $"No complete panel fits ({layout.NoPanelsReason}). This preliminary result is not a viable installation design; a site survey can still verify the measurements."));
        }
        warnings.AddRange(InstallationWarnings(installation, layout.RowGap, specParse));
        warnings.AddRange(layout.Warnings);

        if (!hasLocation)
        {
            warnings.Add(new(SimulationWarningCodes.LocationMissing, inRegion
                ? "The property site has no coordinates, so local obstacle shading cannot be estimated; the regional energy estimate is unshaded."
                : "The property site has no coordinates and no official province code, so energy and climate estimates are unavailable."));
        }

        if (regionReason is RegionalFailureCodes.RegionNotSupported or RegionalFailureCodes.RegionUnknown)
        {
            warnings.Add(new(SimulationWarningCodes.RegionNotSupported, regionReason == RegionalFailureCodes.RegionNotSupported
                ? "The site is outside the supported 2025 Ho Chi Minh City territory (province 79); no regional energy or climate estimate is applied."
                : "The site has neither an official province code nor coordinates, so it cannot be placed in the supported 2025 Ho Chi Minh City territory."));
        }

        if (source.DeclaredTotalAreaM2 is { } declared && layout.Areas.GrossSurfaceAreaM2 > 0)
        {
            var gross = (decimal)layout.Areas.GrossSurfaceAreaM2;
            if (Math.Abs(declared - gross) / gross > _options.DeclaredAreaMismatchRatio)
            {
                warnings.Add(new(SimulationWarningCodes.DeclaredAreaDiffers,
                    $"Declared total area {declared} m² differs from the drawn surface area {Math.Round(gross, 2)} m². Simulation values use the drawn surface."));
            }
        }

        // Energy.
        // E_site[m] = Y_HCM[m] × kWp per scenario; the annual value is the sum of the months. The interannual standard deviation of a
        // regional mean is not the weighted mean of the point deviations, so it is not reported (null, never invented).
        var scenarioDocs = scenarios.Select((s, i) =>
        {
            var result = energyTasks[i].Result;
            var baseline = result.Baseline;
            var monthly = baseline is null ? [] : RegionalAggregationCalculator.ScaleToSite(baseline, kwp);
            return new EnergyScenarioDocument(
                s.Role, s.Place,
                result.Succeeded ? ComponentStatusCodes.Succeeded : ComponentStatusCodes.Failed,
                result.Failure,
                baseline is null ? null : monthly.Sum(m => m.EnergyKwh),
                null,
                baseline is null ? null : Math.Round(baseline.AnnualSpecificYieldKwhPerKwp, 1),
                monthly,
                baseline?.ReferenceRequest);
        }).ToList();
        var primaryBaseline = scenarios.Count > 0 ? energyTasks[0].Result.Baseline : null;

        string energyStatus;
        string? energyReason;
        var primary = scenarioDocs.FirstOrDefault();
        if (panelCount == 0)
        {
            (energyStatus, energyReason) = (ComponentStatusCodes.NotApplicable, "NO_PANELS");
        }
        else if (regionReason is not null)
        {
            (energyStatus, energyReason) = (ComponentStatusCodes.Unavailable, regionReason);
        }
        else if (primary is { Status: ComponentStatusCodes.Succeeded })
        {
            (energyStatus, energyReason) = (ComponentStatusCodes.Succeeded, null);
        }
        else
        {
            (energyStatus, energyReason) = (ComponentStatusCodes.Failed, primary?.Failure?.Code);
        }

        if (scenarioDocs.Any(s => s.Status == ComponentStatusCodes.Failed))
        {
            warnings.Add(new(SimulationWarningCodes.EnergyProviderFailed,
                "The regional PVGIS baseline is unavailable for at least one scenario: " +
                string.Join("; ", scenarioDocs.Where(s => s.Failure is not null).Select(s => $"{s.MountingPlace}: {s.Failure!.Code}")) + "."));
        }

        var degradedEnergy = energyTasks.Select(t => t.Result.Baseline).Where(b => b?.Coverage.Quality == RegionalQualities.Degraded).ToList();
        if (degradedEnergy.Count > 0)
        {
            var c = degradedEnergy[0]!.Coverage;
            warnings.Add(new(SimulationWarningCodes.RegionalBaselineDegraded,
                $"The regional energy baseline used {c.SucceededSampleCount} of {c.SampleCount} samples ({c.CoveredWeightFraction:P1} of the area, renormalised); missing: {string.Join(", ", c.MissingLocationIds)}."));
        }

        var shading = BuildShading(shadingPlan, layout.Placements.Select(p => p.Index).ToList(), energyStatus, primary, hourlyTask.Result, source, shadingOptions);
        var shadingApplied = shading.Status == ComponentStatusCodes.Succeeded;
        if (shadingApplied) shading = shading with { CorrectionBasis = ShadingCorrectionBases.LocalLossFractionOnRegionalBaseline };
        var assumptions = new List<string>
        {
            primaryBaseline is { } pb && _regional.Dataset is { } ds
                ? $"Energy baseline: {EstimateBases.LabelVi} — area-weighted PVGIS average of {pb.Coverage.SucceededSampleCount}/{pb.Coverage.SampleCount} verified sample points ({ds.DatasetKey}) for a 1 kWp reference system with this tilt, azimuth, losses and mounting, scaled to {kwp} kWp."
                : "Energy baseline: the 2025 Ho Chi Minh City regional area-weighted PVGIS average (not computed for this result).",
            $"System losses {systemLoss}% ({(systemLossSpecified ? "site-specified" : "PVGIS documented default")}); PVGIS system losses do not include nearby-object shading, so the shading estimate is not double-counted.",
            $"Module technology '{_pvEnergy.Settings.Technology}' (PVGIS default; the catalog has no technology field).",
            $"Terrain horizon {(_pvEnergy.Settings.UseHorizon ? "included" : "excluded")} (PVGIS); " +
            (shadingApplied ? "nearby obstacle and rack-row shading applied once as monthly loss fractions from this site's geometry (a local shading correction, not a local climate baseline)." : "local obstacle shading not applied."),
        };
        if (shadingApplied)
        {
            // The shading-only adjustment can only remove energy: the result is never above the unshaded baseline.
            warnings.RemoveAll(w => w.Code == SimulationWarningCodes.ObstacleShadingNotModeled);
            if (shading.HourlyReferenceAnnualEnergyKwh is { } reference && shading.AnnualEnergyBeforeShadingKwh is > 0
                && Math.Abs(reference - shading.AnnualEnergyBeforeShadingKwh.Value) / shading.AnnualEnergyBeforeShadingKwh.Value > 0.05m)
            {
                warnings.Add(new(SimulationWarningCodes.ShadingBaselineDiffers,
                    $"The PVGIS hourly series at the site ({shading.StartYear}–{shading.EndYear}) gives {reference} kWh/year unshaded versus the regional typical-year baseline {shading.AnnualEnergyBeforeShadingKwh} kWh; only its monthly loss fractions are used."));
            }
            if (shading.SunHeightCheckDeviationDegree is > 1.0)
            {
                warnings.Add(new(SimulationWarningCodes.SunPositionCheckDeviation,
                    $"Computed sun heights differ from PVGIS H_sun by {shading.SunHeightCheckDeviationDegree:0.###}° on average; check the timestamps."));
            }
        }
        else if (shading.Status != ComponentStatusCodes.NotApplicable && shading.Reason != ShadingReasons.Disabled)
        {
            warnings.Add(new(SimulationWarningCodes.ShadingNotApplied,
                $"The shading estimate is {shading.Status} ({shading.Reason}); energy values are unshaded."));
        }
        if (mountingType == MountingTypes.Flush && scenarios.Count > 0)
        {
            assumptions.Add("Flush mounting with an air gap lies between PVGIS 'building' (no air movement) and 'free' (free airflow). " +
                            "The 'building' scenario is used as the primary conservative-reference estimate; this is a preliminary modeling assumption, not a guaranteed lower bound.");
            warnings.Add(new(SimulationWarningCodes.FlushMountThermalAssumption,
                "Flush-mount energy uses the PVGIS 'building' scenario as the primary conservative reference and returns the 'free' scenario for comparison."));
        }
        else if (scenarios.Count > 0)
        {
            assumptions.Add("Rack mounting uses PVGIS 'free' (modules on a rack with air flowing freely behind them).");
        }

        // With a successful shading estimate the headline energy is the after-shading value (monthly too); the PVGIS
        // scenarios stay unshaded for reference.
        var succeeded = energyStatus == ComponentStatusCodes.Succeeded;
        var annualEnergy = succeeded ? (shadingApplied ? shading.AnnualEnergyAfterShadingKwh : primary!.AnnualEnergyKwh) : null;
        var monthlyEnergy = !succeeded ? []
            : shadingApplied
                ? primary!.Monthly.Select(m => m with { EnergyKwh = shading.Monthly.Single(x => x.Month == m.Month).EnergyAfterShadingKwh }).ToList()
                : primary!.Monthly;
        var energy = new EnergyDocument(
            energyStatus,
            energyReason,
            "TYPICAL_YEAR_HISTORICAL",
            annualEnergy,
            annualEnergy is { } kwh && kwp > 0 ? Math.Round(kwh / kwp, 1) : null,
            monthlyEnergy,
            primary?.MountingPlace,
            scenarioDocs,
            assumptions,
            EnergyDisclaimer,
            succeeded ? primary!.AnnualEnergyKwh : null,
            succeeded ? annualEnergy : null,
            EstimateBases.HcmcRegionalAreaWeighted,
            succeeded && primaryBaseline is not null && _regional.Dataset is { } dataset ? RegionalDocuments.ForPv(dataset, primaryBaseline, membership) : null);
        if (succeeded)
        {
            warnings.Add(new(SimulationWarningCodes.RegionalEstimate,
                $"{EstimateBases.LabelVi}: one area-weighted regional baseline for the whole 2025 Ho Chi Minh City territory, scaled to this system; not a forecast or a measurement for this roof."));
        }

        // Climate.
        var climateResult = climateTask.Result;
        string climateStatus;
        if (!inRegion)
        {
            climateStatus = ComponentStatusCodes.Unavailable;
        }
        else if (climateResult!.Succeeded)
        {
            climateStatus = ComponentStatusCodes.Succeeded;
            if (climateResult.Baseline!.Coverage.Quality == RegionalQualities.Degraded)
            {
                var c = climateResult.Baseline.Coverage;
                warnings.Add(new(SimulationWarningCodes.RegionalBaselineDegraded,
                    $"The regional climate context used {c.SucceededSampleCount} of {c.SampleCount} samples ({c.CoveredWeightFraction:P1} of the area, renormalised)."));
            }
        }
        else
        {
            climateStatus = ComponentStatusCodes.Failed;
            warnings.Add(new(SimulationWarningCodes.ClimateProviderFailed, $"NASA POWER regional climate context is unavailable: {climateResult.Failure!.Code}."));
        }

        var climate = new ClimateDocument(
            climateStatus,
            inRegion ? climateResult?.Failure?.Code : regionReason,
            climateResult?.Failure,
            climateResult?.Baseline?.Context,
            ClimateDisclaimer,
            EstimateBases.HcmcRegionalAreaWeighted,
            climateResult?.Baseline is { } cb && _regional.Dataset is { } cds ? RegionalDocuments.ForClimate(cds, cb, membership) : null);

        var status = energyStatus is ComponentStatusCodes.Succeeded or ComponentStatusCodes.NotApplicable
                     && climateStatus == ComponentStatusCodes.Succeeded
            ? SimulationStatusCodes.Completed
            : SimulationStatusCodes.PartiallyCompleted;

        // A DEGRADED regional baseline is readable and selectable, but never reused: a retry may complete it.
        var isReusable = scenarioDocs.All(s => s.Status == ComponentStatusCodes.Succeeded)
                         && degradedEnergy.Count == 0
                         && climateStatus != ComponentStatusCodes.Failed
                         && climateResult?.Baseline?.Coverage.Quality != RegionalQualities.Degraded
                         && shading.Status != ComponentStatusCodes.Failed;

        return new ComputedSimulation(
            status,
            energyStatus,
            climateStatus,
            isReusable,
            layout.Orientation?.ToString().ToUpperInvariant(),
            panelCount,
            kwp,
            SimulationDetailMapper.Areas(layout.Areas, R3),
            energy.AnnualEnergyKwh,
            energy.SpecificYieldKwhPerKwpYear,
            obstacles.Select(o => ToObstacleDocument(o, surface)).ToList(),
            ToInstallationDocument(installation, layout, specParse),
            ToLayoutDocument(layout, surface, input),
            energy,
            climate,
            warnings,
            shading);
    }

    /// <summary>Turns the shading plan and the hourly data into the shading document (explicit statuses; never fabricated values).</summary>
    private ShadingDocument BuildShading(ShadingPlan plan, IReadOnlyList<int> placementIndices, string energyStatus, EnergyScenarioDocument? primary,
        PvHourlyResult? hourly, SimulationSource source, ShadingAnalysisOptions options)
    {
        int? start = plan.Kind == ShadingPlanKind.RayCast ? options.StartYear : null;
        int? end = plan.Kind == ShadingPlanKind.RayCast ? options.EndYear : null;
        if (plan.Kind == ShadingPlanKind.Unavailable)
        {
            var status = plan.Reason == ShadingReasons.NoPanels ? ComponentStatusCodes.NotApplicable : ComponentStatusCodes.Unavailable;
            return ShadingAnalysis.NotSucceeded(status, plan.Reason!, null, options, null, null);
        }
        if (energyStatus != ComponentStatusCodes.Succeeded)
            return ShadingAnalysis.NotSucceeded(ComponentStatusCodes.Unavailable, ShadingReasons.EnergyUnavailable, null, options, start, end);

        if (plan.Kind == ShadingPlanKind.NoOccluders)
        {
            // Nothing can cast a shadow: zero loss for every panel, no hourly data needed.
            return ShadingAnalysis.Succeeded(options, primary!.Monthly, null, placementIndices, options.SamplesAlongWidth * options.SamplesAlongLength,
                ShadingReasons.NoOccluders, null, null);
        }

        if (hourly is not { Succeeded: true })
            return ShadingAnalysis.NotSucceeded(ComponentStatusCodes.Failed, ShadingReasons.HourlyDataFailed, hourly?.Failure, options, start, end);

        var calculator = plan.Calculator!;
        var latitude = (double)source.Latitude!.Value;
        var longitude = (double)source.Longitude!.Value;
        var work = ShadingAnalysis.EstimateWork(calculator, calculator.Scene.OccluderCount, latitude, longitude, hourly.Series!.Records);
        if (work > options.MaxWorkUnits)
        {
            _logger.LogWarning("Shading estimate skipped: {Work} ray tests exceed the limit {Limit}.", work, options.MaxWorkUnits);
            return ShadingAnalysis.NotSucceeded(ComponentStatusCodes.Unavailable, ShadingReasons.ComputationLimit, null, options, start, end);
        }

        var estimate = ShadingEnergyCalculator.Calculate(latitude, longitude, [new ShadingSubarray(calculator, hourly.Series.Records)]);
        return ShadingAnalysis.Succeeded(options, primary!.Monthly, estimate, placementIndices, calculator.SamplesPerPanel, null,
            hourly.Series.Provider.YearMin ?? start, hourly.Series.Provider.YearMax ?? end);
    }

    private async Task<PvHourlyResult?> SafeHourlyAsync(PvHourlyRequest request, CancellationToken budget)
    {
        try
        {
            return await _pvEnergy.GetHourlySeriesAsync(request, budget);
        }
        catch (OperationCanceledException)
        {
            return PvHourlyResult.Fail(ProviderFailureCodes.Timeout, "The provider time budget for this simulation was exceeded.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected PVGIS hourly failure.");
            return PvHourlyResult.Fail(ProviderFailureCodes.Unavailable, "PVGIS failed unexpectedly.");
        }
    }

    private async Task<RegionalPvResult> SafeRegionalPvAsync(RegionalPvRequest request, CancellationToken budget)
    {
        try
        {
            return await _regional.GetPvBaselineAsync(request, budget);
        }
        catch (OperationCanceledException)
        {
            return new RegionalPvResult(null, new ProviderFailure(ProviderFailureCodes.Timeout, "The provider time budget for this simulation was exceeded."), null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected regional PVGIS baseline failure.");
            return new RegionalPvResult(null, new ProviderFailure(ProviderFailureCodes.Unavailable, "The regional PVGIS baseline failed unexpectedly."), null);
        }
    }

    private async Task<RegionalClimateResult?> SafeRegionalClimateAsync(CancellationToken budget)
    {
        try
        {
            return await _regional.GetClimateBaselineAsync(budget);
        }
        catch (OperationCanceledException)
        {
            return new RegionalClimateResult(null, new ProviderFailure(ProviderFailureCodes.Timeout, "The provider time budget for this simulation was exceeded."), null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected regional NASA POWER baseline failure.");
            return new RegionalClimateResult(null, new ProviderFailure(ProviderFailureCodes.Unavailable, "The regional NASA POWER baseline failed unexpectedly."), null);
        }
    }

    private static IEnumerable<SimulationWarning> InstallationWarnings(
        ResolvedInstallation installation, ResolvedParameter rowGap, InstallationSpecParseResult specParse)
    {
        var values = new[] { installation.PanelGap, rowGap, installation.EdgeSetback, installation.ObstacleClearance, installation.RackLowEdgeClearance };

        if (specParse.Present && !specParse.IsValid)
        {
            yield return new(SimulationWarningCodes.ProductInstallationSpecInvalid,
                "The product's installation block is invalid and was ignored; no documented minimums were applied.");
        }

        if (values.Any(v => v.Source == InstallationSources.PreliminaryDefault))
        {
            yield return new(SimulationWarningCodes.PreliminaryInstallationValues,
                "Some spacing values are configurable preliminary assumptions, not manufacturer-certified or regulatory minimums.");
        }

        if (values.Any(v => v.Source == InstallationSources.SiteSpecified))
        {
            yield return new(SimulationWarningCodes.SiteValuesNotVerified, "Site-specified spacing values were not verified.");
        }

        if (values.Any(v => v.DocumentedMinimumMm is not null) || installation.ModuleThickness.Source == InstallationSources.ManufacturerDocumented)
        {
            yield return new(SimulationWarningCodes.ManufacturerValuesNotIndependentlyVerified,
                "Documented manufacturer values were recorded by catalog staff and are not independently verified by SmartSolar.");
        }
    }

    private static decimal R3(double value) => Math.Round((decimal)value, 3, MidpointRounding.AwayFromZero);

    private static double D3(double value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);

    private static double D6(double value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);

    private static WorldPoint Point(Vec3 v) => new(D3(v.X), D3(v.Y), D3(v.Z));

    private static WorldPoint Axis(Vec3 v) => new(D6(v.X), D6(v.Y), D6(v.Z));

    private static QuaternionDocument Rotation(Quaternion q) => new(D6(q.X), D6(q.Y), D6(q.Z), D6(q.W));

    private static ObstacleDocument ToObstacleDocument(SurfaceObstacle o, SurfaceFrame surface)
    {
        double x0 = (double)o.XM, y0 = (double)o.YM, x1 = (double)(o.XM + o.WidthM), y1 = (double)(o.YM + o.LengthM);
        return new ObstacleDocument(o.Name, o.XM, o.YM, o.WidthM, o.LengthM, o.HeightM,
        [
            Point(surface.ToWorld(x0, y0, 0)), Point(surface.ToWorld(x1, y0, 0)),
            Point(surface.ToWorld(x1, y1, 0)), Point(surface.ToWorld(x0, y1, 0)),
        ]);
    }

    private static InstallationValueDocument Value(ResolvedParameter p)
        => new(p.ValueMm, p.Source, p.DocumentedMinimumMm, p.DocumentRef, p.DocumentSection, p.DocumentUrl);

    private InstallationDocument ToInstallationDocument(ResolvedInstallation installation, SimulationLayoutResult layout, InstallationSpecParseResult specParse)
        => new(
            "mm",
            Value(installation.PanelGap),
            Value(layout.RowGap),
            Value(installation.EdgeSetback),
            Value(installation.ObstacleClearance),
            Value(installation.RackLowEdgeClearance),
            Value(installation.ModuleThickness),
            layout.ShadeEstimateRowGapMm,
            $"Advisory: solstices (declination ±{_options.Shading.DeclinationDegree}°), {TimeSpan.FromHours(_options.Shading.StartSolarHour):hh\\:mm}–{TimeSpan.FromHours(_options.Shading.EndSolarHour):hh\\:mm} solar time, every {_options.Shading.StepMinutes} minutes.",
            !specParse.Present ? "ABSENT" : specParse.IsValid ? "VALID" : "INVALID",
            [],
            "Panel gap: clear distance between neighbouring panels in a row. Row gap: clear distance between neighbouring rows' footprints on the surface. " +
            "Edge setback: keep-out from every surface edge. Obstacle clearance: buffer around each obstacle. Values in millimetres (20 mm = 2 cm).");

    private static LayoutDocument ToLayoutDocument(SimulationLayoutResult layout, SurfaceFrame surface, SimulationGeometryInput input)
    {
        var w = (double)input.SurfaceWidthM;
        var l = (double)input.SurfaceLengthM;
        var panelRotation = new PanelOrientation(layout.PanelTiltDegree, layout.PanelAzimuthDegree).ToQuaternion();
        var first = layout.Placements.FirstOrDefault();

        var frame = new FrameDocument(
            "Local surface frame: origin at the upper-left corner of the 2D editor (high edge when inclined); X along surface width; Y along surface length pointing down-slope; meters on the surface plane.",
            "World frame: E = East, N = North, U = Up, meters, origin at the surface origin (Z-up). For Three.js (Y-up) rotate the scene root -90° about X, or set camera.up to (0, 0, 1).",
            "Azimuths are compass degrees: 0 = North, 90 = East, 180 = South, 270 = West, clockwise.",
            "Panel frame (right-handed): x = horizontal width axis, y = up-slope axis, z = front normal. A unit box scaled by (physical width, length, thickness), with the front face at z = 0 and depth toward -z, positioned at worldCenter with worldRotation reproduces the module.",
            "The local surface frame is left-handed (like screen coordinates): use surfaceCornersWorld and the axis vectors to place points, and use the per-panel quaternion for rotations.",
            [Point(surface.ToWorld(0, 0, 0)), Point(surface.ToWorld(w, 0, 0)), Point(surface.ToWorld(w, l, 0)), Point(surface.ToWorld(0, l, 0))],
            Axis(surface.XAxis),
            Axis(surface.YAxis),
            Axis(surface.Normal),
            Rotation(panelRotation));

        return new LayoutDocument(
            "BEST_FOUND_PRELIMINARY_LAYOUT",
            layout.Orientation?.ToString().ToUpperInvariant(),
            D3(layout.RowRotationDegree),
            first is null ? null : D3(first.FootprintWidthM),
            first is null ? null : D3(first.FootprintDepthM),
            layout.FootprintIsConservative,
            layout.NoPanelsReason,
            layout.Placements.Select(p => new PanelPlacementDocument(
                p.Index,
                new FootprintDocument(D3(p.FootprintCenterXM), D3(p.FootprintCenterYM), D3(p.FootprintWidthM), D3(p.FootprintDepthM), D3(p.RowRotationDegree)),
                new PhysicalPanelDocument(D3(p.PhysicalWidthM), D3(p.PhysicalLengthM), D3(p.ThicknessM)),
                new FrontCenterDocument(D3(p.FrontCenterXM), D3(p.FrontCenterYM), D3(p.FrontCenterHeightM)),
                Point(p.WorldCenter),
                Rotation(p.Rotation),
                D3(p.LowEdgeClearanceM),
                D3(p.HighEdgeHeightM))).ToList(),
            frame);
    }
    /// <summary>
    /// The surface the layout runs on: the saved surface when the customer defined one (dimensions + obstacles), otherwise a square of the
    /// declared usable area without obstacles (null when there is neither, or when that square would exceed the maximum surface side).
    /// </summary>
    public static (decimal Length, decimal Width, IReadOnlyList<SurfaceObstacle> Obstacles)? EffectiveSurface(SimulationSource source, decimal maxSideM)
    {
        if (source.SurfaceLengthM is { } length && source.SurfaceWidthM is { } width)
            return SurfaceObstacleSerializer.Deserialize(source.ObstaclesJson) is { } saved ? (length, width, saved) : null;
        if (source.DeclaredUsableAreaM2 is not > 0) return null;
        var side = Math.Round((decimal)Math.Sqrt((double)source.DeclaredUsableAreaM2.Value), 3, MidpointRounding.ToZero);
        return side > 0 && side <= maxSideM ? (side, side, Array.Empty<SurfaceObstacle>()) : null;
    }
}
