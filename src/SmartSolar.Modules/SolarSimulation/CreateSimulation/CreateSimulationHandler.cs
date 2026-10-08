using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Catalog.Constants;
using SmartSolar.Modules.Catalog.Contracts.Persistence;
using SmartSolar.Modules.Catalog.Installation;
using SmartSolar.Modules.Catalog.Models;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.PreSurvey.Surface;
using SmartSolar.Modules.SolarSimulation.Calculation;
using SmartSolar.Modules.SolarSimulation.Constants;
using SmartSolar.Modules.SolarSimulation.Contracts.Persistence;
using SmartSolar.Modules.SolarSimulation.Energy;
using SmartSolar.Modules.SolarSimulation.Geometry;
using SmartSolar.Modules.SolarSimulation.Installation;
using SmartSolar.Modules.SolarSimulation.Models;
using SmartSolar.Modules.SolarSimulation.Options;
using SimulationEntity = SmartSolar.Modules.SolarSimulation.Entities.SolarSimulation;

namespace SmartSolar.Modules.SolarSimulation.CreateSimulation;

/// <summary>
/// Calculates and stores a simulation snapshot for a draft pre-survey owned by the caller.
/// <list type="number">
/// <item>Validate ownership, Draft status, expected geometry version, surface and product.</item>
/// <item>Resolve mounting and installation values; compute the input fingerprint.</item>
/// <item>Reuse an identical reusable snapshot when one exists (no provider calls).</item>
/// <item>Otherwise calculate once per fingerprint (single-flight), call providers within a bounded budget,
/// and save + select in one transaction that re-checks Draft status and geometry version.</item>
/// </list>
/// Provider failures become explicit statuses; energy is never fabricated.
/// </summary>
public sealed class CreateSimulationHandler
{
    private const string EnergyDisclaimer =
        "Typical-year estimate from PVGIS using historical data. Not a forecast for a specific year and not a production guarantee.";

    private const string ClimateDisclaimer =
        "Historical monthly averages from NASA POWER for the stated period. Context only; not a forecast and not applied to the energy estimate.";

    private readonly ISolarSimulationUnitOfWork _unitOfWork;
    private readonly ICatalogUnitOfWork _catalog;
    private readonly IPvEnergyEstimator _pvEnergy;
    private readonly IClimateContextProvider _climate;
    private readonly SolarSimulationOptions _options;
    private readonly SimulationSingleFlight _singleFlight;
    private readonly ILogger<CreateSimulationHandler> _logger;

    public CreateSimulationHandler(
        ISolarSimulationUnitOfWork unitOfWork,
        ICatalogUnitOfWork catalog,
        IPvEnergyEstimator pvEnergy,
        IClimateContextProvider climate,
        SolarSimulationOptions options,
        SimulationSingleFlight singleFlight,
        ILogger<CreateSimulationHandler> logger)
    {
        _unitOfWork = unitOfWork;
        _catalog = catalog;
        _pvEnergy = pvEnergy;
        _climate = climate;
        _options = options;
        _singleFlight = singleFlight;
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

        var obstacles = SurfaceObstacleSerializer.Deserialize(source.ObstaclesJson);
        if (source.SurfaceLengthM is not { } length || source.SurfaceWidthM is not { } width
            || source.SurfaceTiltDegree is not { } surfaceTilt || source.SurfaceAzimuthDegree is not { } surfaceAzimuthRaw
            || obstacles is null)
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
            ["shadingWindow"] = window,
            ["layoutOffsetSamples"] = _options.LayoutOffsetSamples,
            ["maxPlacements"] = _options.Limits.MaxPlacements,
            ["rackSupportHeightWarningMm"] = _options.RackSupportHeightWarningMm,
            ["declaredAreaMismatchRatio"] = _options.DeclaredAreaMismatchRatio,
        });

        var reusable = await _unitOfWork.FindReusableAsync(source.PreSurveyId, fingerprint, cancellationToken);
        if (reusable is not null)
        {
            return await SelectExistingAsync(reusable, source, cancellationToken);
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

        var computed = await _singleFlight.RunAsync(
            fingerprint,
            () => ComputeAsync(layout, input, product, specParse, installation, systemLoss, command.SystemLossPercent.HasValue, source, command.MountingType!, obstacles, surfaceFrame),
            cancellationToken);

        var entity = ToEntity(computed, fingerprint, command, source, product, specParse, length, width, surfaceTilt, surfaceAzimuth, panelTilt, panelAzimuth);

        var saved = await _unitOfWork.TrySaveAndSelectAsync(entity, cancellationToken);
        switch (saved)
        {
            case SaveSimulationOutcome.Saved:
                return CreateSimulationResult.Success(CreateSimulationOutcome.Created,
                    SimulationDetailMapper.ToDetail(entity, source.GeometryVersion, entity.Id));

            case SaveSimulationOutcome.DuplicateFingerprint:
                var winner = await _unitOfWork.FindReusableAsync(source.PreSurveyId, fingerprint, cancellationToken);
                return winner is null
                    ? CreateSimulationResult.Fail(CreateSimulationOutcome.SourceChanged, SimulationErrorCodes.SourceChanged)
                    : await SelectExistingAsync(winner, source, cancellationToken);

            case SaveSimulationOutcome.NotDraft:
                return CreateSimulationResult.Fail(CreateSimulationOutcome.NotEditable);

            default:
                return CreateSimulationResult.Fail(CreateSimulationOutcome.SourceChanged, SimulationErrorCodes.SourceChanged);
        }
    }

    private static CreateSimulationResult Reject(string code, string? parameter, string message)
        => CreateSimulationResult.Fail(CreateSimulationOutcome.Rejected, code, [new SimulationProblem(code, parameter, message)]);

    private async Task<CreateSimulationResult> SelectExistingAsync(SimulationEntity existing, SimulationSource source, CancellationToken cancellationToken)
    {
        var selected = await _unitOfWork.TrySelectAsync(source.PreSurveyId, existing.Id, source.GeometryVersion, cancellationToken);
        return selected switch
        {
            SelectSimulationOutcome.Selected => CreateSimulationResult.Success(CreateSimulationOutcome.Reused,
                SimulationDetailMapper.ToDetail(existing, source.GeometryVersion, existing.Id)),
            SelectSimulationOutcome.NotDraft => CreateSimulationResult.Fail(CreateSimulationOutcome.NotEditable),
            _ => CreateSimulationResult.Fail(CreateSimulationOutcome.SourceChanged, SimulationErrorCodes.SourceChanged),
        };
    }

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
        SurfaceFrame surface)
    {
        var panelCount = layout.Placements.Count;
        var kwp = Math.Round(panelCount * product.RatedPowerW!.Value / 1000m, 3);
        var hasLocation = source.Latitude is not null && source.Longitude is not null;

        // One budget for every provider call; independent of any single HTTP request so a shared
        // (single-flight) calculation is not cancelled when one caller disconnects.
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(_options.ProviderBudgetSeconds));

        var scenarios = new List<(string Role, string Place)>();
        if (panelCount > 0 && hasLocation)
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

        var energyTasks = scenarios.Select(s => SafeEstimateAsync(new PvEnergyRequest(
            source.Latitude!.Value, source.Longitude!.Value, kwp, systemLoss, input.PanelTiltDegree, input.PanelAzimuthDegree, s.Place), budget.Token)).ToList();
        var climateTask = hasLocation
            ? SafeClimateAsync(source.Latitude!.Value, source.Longitude!.Value, budget.Token)
            : Task.FromResult<ClimateContextResult?>(null);

        await Task.WhenAll(energyTasks.Cast<Task>().Append(climateTask));

        var warnings = new List<SimulationWarning>
        {
            new(SimulationWarningCodes.StructureNotAssessed, "Structural capacity, attachment and support design are not assessed."),
            new(SimulationWarningCodes.MountingSystemRequirementsUnavailable, "Mounting-system installation requirements are not available in the catalog and were not applied."),
            new(SimulationWarningCodes.BuildingFireCodeNotChecked, "Building and fire code setbacks or access requirements are not checked."),
        };
        warnings.AddRange(InstallationWarnings(installation, layout.RowGap, specParse));
        warnings.AddRange(layout.Warnings);

        if (!hasLocation)
        {
            warnings.Add(new(SimulationWarningCodes.LocationMissing,
                "The property site has no coordinates, so energy and climate estimates are unavailable."));
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
        var scenarioDocs = scenarios.Select((s, i) =>
        {
            var result = energyTasks[i].Result;
            var estimate = result.Estimate;
            return new EnergyScenarioDocument(
                s.Role, s.Place,
                result.Succeeded ? ComponentStatusCodes.Succeeded : ComponentStatusCodes.Failed,
                result.Failure,
                estimate?.AnnualEnergyKwh,
                estimate?.AnnualStdDevKwh,
                estimate is null || kwp == 0 ? null : Math.Round(estimate.AnnualEnergyKwh / kwp, 1),
                estimate?.Monthly ?? [],
                estimate?.Provider);
        }).ToList();

        string energyStatus;
        string? energyReason;
        var primary = scenarioDocs.FirstOrDefault();
        if (panelCount == 0)
        {
            (energyStatus, energyReason) = (ComponentStatusCodes.NotApplicable, "NO_PANELS");
        }
        else if (!hasLocation)
        {
            (energyStatus, energyReason) = (ComponentStatusCodes.Unavailable, "LOCATION_MISSING");
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
                "PVGIS did not return an estimate for at least one scenario: " +
                string.Join("; ", scenarioDocs.Where(s => s.Failure is not null).Select(s => $"{s.MountingPlace}: {s.Failure!.Code}")) + "."));
        }

        var assumptions = new List<string>
        {
            $"System losses {systemLoss}% ({(systemLossSpecified ? "site-specified" : "PVGIS documented default")}).",
            $"Module technology '{_pvEnergy.Settings.Technology}' (PVGIS default; the catalog has no technology field).",
            $"Terrain horizon {(_pvEnergy.Settings.UseHorizon ? "included" : "excluded")} (PVGIS); local obstacle shading not modeled.",
        };
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

        var energy = new EnergyDocument(
            energyStatus,
            energyReason,
            "TYPICAL_YEAR_HISTORICAL",
            energyStatus == ComponentStatusCodes.Succeeded ? primary!.AnnualEnergyKwh : null,
            energyStatus == ComponentStatusCodes.Succeeded ? primary!.SpecificYieldKwhPerKwpYear : null,
            energyStatus == ComponentStatusCodes.Succeeded ? primary!.Monthly : [],
            primary?.MountingPlace,
            scenarioDocs,
            assumptions,
            EnergyDisclaimer);

        // Climate.
        var climateResult = climateTask.Result;
        string climateStatus;
        if (!hasLocation)
        {
            climateStatus = ComponentStatusCodes.Unavailable;
        }
        else if (climateResult!.Succeeded)
        {
            climateStatus = ComponentStatusCodes.Succeeded;
        }
        else
        {
            climateStatus = ComponentStatusCodes.Failed;
            warnings.Add(new(SimulationWarningCodes.ClimateProviderFailed, $"NASA POWER climate context is unavailable: {climateResult.Failure!.Code}."));
        }

        var climate = new ClimateDocument(
            climateStatus,
            hasLocation ? climateResult?.Failure?.Code : "LOCATION_MISSING",
            climateResult?.Failure,
            climateResult?.Context,
            ClimateDisclaimer);

        var status = energyStatus is ComponentStatusCodes.Succeeded or ComponentStatusCodes.NotApplicable
                     && climateStatus == ComponentStatusCodes.Succeeded
            ? SimulationStatusCodes.Completed
            : SimulationStatusCodes.PartiallyCompleted;

        var isReusable = scenarioDocs.All(s => s.Status == ComponentStatusCodes.Succeeded)
                         && climateStatus != ComponentStatusCodes.Failed;

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
            warnings);
    }

    private async Task<PvEnergyResult> SafeEstimateAsync(PvEnergyRequest request, CancellationToken budget)
    {
        try
        {
            return await _pvEnergy.EstimateAsync(request, budget);
        }
        catch (OperationCanceledException)
        {
            return PvEnergyResult.Fail(ProviderFailureCodes.Timeout, "The provider time budget for this simulation was exceeded.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected PVGIS failure.");
            return PvEnergyResult.Fail(ProviderFailureCodes.Unavailable, "PVGIS failed unexpectedly.");
        }
    }

    private async Task<ClimateContextResult?> SafeClimateAsync(decimal latitude, decimal longitude, CancellationToken budget)
    {
        try
        {
            return await _climate.GetMonthlyClimatologyAsync(latitude, longitude, budget);
        }
        catch (OperationCanceledException)
        {
            return ClimateContextResult.Fail(ProviderFailureCodes.Timeout, "The provider time budget for this simulation was exceeded.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected NASA POWER failure.");
            return ClimateContextResult.Fail(ProviderFailureCodes.Unavailable, "NASA POWER failed unexpectedly.");
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

    private static SimulationEntity ToEntity(
        ComputedSimulation computed, string fingerprint, CreateSimulationCommand command, SimulationSource source, ProductDto product,
        InstallationSpecParseResult specParse, decimal length, decimal width, decimal surfaceTilt, decimal surfaceAzimuth,
        decimal panelTilt, decimal panelAzimuth)
        => new()
        {
            Id = Guid.NewGuid(),
            PreSurveyId = source.PreSurveyId,
            CreatedByUserId = command.UserId,
            CreatedAt = DateTimeOffset.UtcNow,
            PreSurveyGeometryVersion = source.GeometryVersion,
            InputFingerprint = fingerprint,
            IsReusable = computed.IsReusable,
            Status = computed.Status,
            EnergyStatus = computed.EnergyStatus,
            ClimateStatus = computed.ClimateStatus,
            AlgorithmVersion = AlgorithmVersions.Current,
            ProductId = product.Id,
            ProductSku = product.Sku,
            ProductName = product.Name,
            ProductBrand = product.Brand,
            ProductModel = product.Model,
            ProductRatedPowerW = product.RatedPowerW!.Value,
            ProductWidthMm = product.WidthMm!.Value,
            ProductHeightMm = product.HeightMm!.Value,
            ProductInstallationSpec = specParse.Spec is null ? null : SnapshotJson.Serialize(specParse.Spec),
            SurfaceLengthM = length,
            SurfaceWidthM = width,
            SurfaceTiltDegree = surfaceTilt,
            SurfaceAzimuthDegree = surfaceAzimuth,
            Latitude = source.Latitude,
            Longitude = source.Longitude,
            MountingType = command.MountingType!,
            PanelTiltDegree = panelTilt,
            PanelAzimuthDegree = panelAzimuth,
            LayoutOrientation = computed.LayoutOrientation,
            PanelCount = computed.PanelCount,
            InstalledCapacityKwp = computed.InstalledCapacityKwp,
            GrossSurfaceAreaM2 = computed.Areas.GrossSurfaceAreaM2,
            ObstacleOccupiedAreaM2 = computed.Areas.ObstacleOccupiedAreaM2,
            AvailableSurfaceAreaM2 = computed.Areas.AvailableSurfaceAreaM2,
            InstallableAreaM2 = computed.Areas.InstallableAreaM2,
            PanelCoveredAreaM2 = computed.Areas.PanelCoveredAreaM2,
            TotalModuleAreaM2 = computed.Areas.TotalModuleAreaM2,
            AnnualEnergyKwh = computed.AnnualEnergyKwh,
            SpecificYieldKwhPerKwpYear = computed.SpecificYieldKwhPerKwpYear,
            Obstacles = SnapshotJson.Serialize(computed.Obstacles),
            Installation = SnapshotJson.Serialize(computed.Installation),
            Layout = SnapshotJson.Serialize(computed.Layout),
            Warnings = SnapshotJson.Serialize(computed.Warnings),
            Energy = SnapshotJson.Serialize(computed.Energy),
            Climate = SnapshotJson.Serialize(computed.Climate),
        };
}
