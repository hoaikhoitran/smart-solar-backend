using System.Text.RegularExpressions;
using FluentValidation;
using Microsoft.Extensions.Logging;
using SmartSolar.Modules.AdministrativeUnits;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Contracts.Persistence;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;

namespace SmartSolar.Modules.Scheduling.Zones;

public sealed record CreateZoneCommand(Guid ActorUserId, string? Code, string? Name, bool IsActive, bool IsServiceable, Guid? ResponsibleManagerUserId);

/// <param name="IsActive">Null keeps the current active flag.</param>
/// <param name="IsServiceable">Null keeps the current serviceability.</param>
public sealed record UpdateZoneCommand(Guid ActorUserId, Guid ZoneId, string? Name, bool? IsActive, bool? IsServiceable, Guid? ResponsibleManagerUserId, int Revision);

/// <summary>Replaces the zone's commune set: new codes are mapped, codes left out are soft-removed (with <see cref="Reason"/>).</summary>
public sealed record SetZoneCommunesCommand(Guid ActorUserId, Guid ZoneId, IReadOnlyList<string>? CommuneCodes, string? Reason, int Revision);

/// <summary>The customer sets the official location of their own site (both codes required).</summary>
public sealed record SetSiteLocationCommand(Guid CustomerUserId, Guid PropertySiteId, string? ProvinceCode, string? CommuneCode);

public sealed record AdminZoneView(
    Guid ZoneId, string Code, string Name, bool IsActive, bool IsServiceable, Guid? ResponsibleManagerUserId,
    int CommuneCount, int MemberCount, int SchedulableMemberCount, bool HasAvailableTechnician, int Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record ZoneCommuneView(string Code, string Name, string UnitType, string ProvinceCode, DateTimeOffset AssignedAt);

public sealed record ZoneDetailView(AdminZoneView Zone, IReadOnlyList<ZoneCommuneView> Communes);

public sealed record CoverageCommuneView(string Code, string Name, string UnitType, string ProvinceCode);

/// <summary>
/// Coverage gaps for Admin/Manager: active catalog communes not mapped to any active zone, and active zones without a schedulable
/// technician (active account, TECHNICIAN role, scheduling on).
/// </summary>
public sealed record ZoneCoverageReportView(
    int UnmappedCommuneCount, IReadOnlyList<CoverageCommuneView> UnmappedCommunes, IReadOnlyList<AdminZoneView> ZonesWithoutSchedulableTechnicians);

public sealed record ZoneTechnicianView(Guid TechnicianUserId, string FullName, bool IsActiveUser, bool IsSchedulingActive, bool IsSchedulable, DateTimeOffset AssignedAt);

/// <summary>The site's location after the change and the technician-independent coverage status (COVERED, NO_ZONE_COVERAGE, ZONE_NOT_SERVICEABLE).</summary>
public sealed record SiteLocationView(Guid PropertySiteId, string ProvinceCode, string CommuneCode, string? CommuneName, string CoverageStatus, ZoneView? Zone);

public static partial class ZoneCodeFormat
{
    public const int MaxLength = 50;

    [GeneratedRegex("^[A-Z0-9][A-Z0-9-]{1,49}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    /// <summary>Trimmed, upper-case (invariant) code; null for blank input.</summary>
    public static string? Normalize(string? code) => string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant();

    public static bool IsValid(string? code) => code is not null && Pattern().IsMatch(code);
}

public sealed class CreateZoneCommandValidator : AbstractValidator<CreateZoneCommand>
{
    public CreateZoneCommandValidator()
    {
        RuleFor(x => x.Code).NotEmpty().Must(c => ZoneCodeFormat.IsValid(ZoneCodeFormat.Normalize(c)))
            .WithMessage("'code' must be 2–50 characters: letters A–Z, digits and '-', starting with a letter or digit (e.g. \"KHU-THU-DUC\").");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(150);
    }
}

public sealed class UpdateZoneCommandValidator : AbstractValidator<UpdateZoneCommand>
{
    public UpdateZoneCommandValidator()
    {
        RuleFor(x => x.ZoneId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(150);
        RuleFor(x => x.Revision).GreaterThanOrEqualTo(0);
    }
}

public sealed class SetZoneCommunesCommandValidator : AbstractValidator<SetZoneCommunesCommand>
{
    public const int MaxCommunes = 500;

    public SetZoneCommunesCommandValidator()
    {
        RuleFor(x => x.ZoneId).NotEmpty();
        RuleFor(x => x.CommuneCodes).NotNull();
        RuleFor(x => x.CommuneCodes!).Must(c => c.Count <= MaxCommunes).When(x => x.CommuneCodes is not null)
            .WithMessage($"At most {MaxCommunes} communes per zone.");
        RuleForEach(x => x.CommuneCodes).Must(c => AdministrativeCodeFormats.IsCommuneCode(c?.Trim()))
            .WithMessage("Each commune code must be a five-digit official code such as \"26740\" (leading zeros kept).");
        RuleFor(x => x.Reason).MaximumLength(500);
        RuleFor(x => x.Revision).GreaterThanOrEqualTo(0);
    }
}

/// <summary>
/// Operational zones (Admin/Manager). A zone is a set of official communes; a commune belongs to at most one zone at a time
/// (exclusive mapping, history kept). Codes are immutable; name, flags and manager are edited with a revision check. Deactivating a
/// zone or marking it not serviceable is refused with ZONE_IN_USE while unfinished work records the zone. Changing the commune set
/// never reassigns confirmed work: removing or moving a commune whose sites have unfinished survey appointments or installation
/// projects is refused with COMMUNE_IN_USE; mapping a commune that another ACTIVE zone holds is refused with COMMUNE_ALREADY_MAPPED
/// (a commune held by an inactive zone is moved, with history). Every write takes the zone row FOR UPDATE, which bookings and
/// assignments hold FOR SHARE, so the usage checks cannot miss work that is committing.
/// </summary>
public sealed class ZoneHandlers(ISchedulingUnitOfWork unitOfWork, IAdministrativeCatalogReader catalog, TimeProvider time, ILogger<ZoneHandlers> logger)
{
    public async Task<IReadOnlyList<AdminZoneView>> ListAsync(CancellationToken cancellationToken)
    {
        var zones = await unitOfWork.ListZonesAsync(cancellationToken);
        var stats = (await unitOfWork.ListZoneStatsAsync(cancellationToken)).ToDictionary(s => s.ZoneId);
        return zones.Select(z => View(z, stats.GetValueOrDefault(z.Id))).ToList();
    }

    public async Task<SchedulingResult<ZoneDetailView>> GetAsync(Guid zoneId, CancellationToken cancellationToken)
    {
        var zone = await unitOfWork.GetZoneAsync(zoneId, cancellationToken);
        if (zone is null) return NotFound<ZoneDetailView>();
        return SchedulingResult<ZoneDetailView>.Ok(await DetailAsync(zone, cancellationToken));
    }

    public async Task<ZoneCoverageReportView> CoverageAsync(string? provinceCode, CancellationToken cancellationToken)
    {
        var unmapped = await unitOfWork.ListUnmappedCommunesAsync(AdministrativeCodeFormats.Normalize(provinceCode), AdministrativeCatalogClock.Today(time),
            cancellationToken);
        var withoutTechnicians = (await ListAsync(cancellationToken)).Where(z => z.IsActive && !z.HasAvailableTechnician).ToList();
        return new ZoneCoverageReportView(unmapped.Count, unmapped.Select(c => new CoverageCommuneView(c.Code, c.Name, c.UnitType, c.ProvinceCode)).ToList(),
            withoutTechnicians);
    }

    public async Task<SchedulingResult<IReadOnlyList<ZoneTechnicianView>>> TechniciansAsync(Guid zoneId, CancellationToken cancellationToken)
    {
        if (await unitOfWork.GetZoneAsync(zoneId, cancellationToken) is null) return NotFound<IReadOnlyList<ZoneTechnicianView>>();
        return SchedulingResult<IReadOnlyList<ZoneTechnicianView>>.Ok((await unitOfWork.ListZoneMembersAsync(zoneId, cancellationToken))
            .Select(m => new ZoneTechnicianView(m.TechnicianUserId, m.FullName, m.IsActiveUser, m.IsSchedulingActive,
                m.IsActiveUser && m.HasTechnicianRole && m.IsSchedulingActive, m.AssignedAt)).ToList());
    }

    /// <summary>
    /// Zone structure (create, update, deactivate, serviceability, commune mapping) is Admin-only. Checked here against the actor's
    /// CURRENT account and roles, not only by the controller attribute: a Manager token, a demoted or disabled Admin is refused.
    /// </summary>
    private async Task<SchedulingResult<T>?> NotAdminAsync<T>(Guid actorUserId, CancellationToken cancellationToken)
        => await unitOfWork.IsActiveUserWithRoleAsync(actorUserId, [RoleCodes.Admin], cancellationToken)
            ? null
            : SchedulingResult<T>.Fail(SchedulingErrorCodes.ZoneManagementForbidden,
                "Only an Admin can create or change operational zones and their commune mapping.");

    public async Task<SchedulingResult<AdminZoneView>> CreateAsync(CreateZoneCommand command, CancellationToken cancellationToken)
    {
        if (await NotAdminAsync<AdminZoneView>(command.ActorUserId, cancellationToken) is { } forbidden) return forbidden;
        var code = ZoneCodeFormat.Normalize(command.Code)!;
        if (await unitOfWork.FindZoneByCodeAsync(code, cancellationToken) is not null) return CodeExists<AdminZoneView>();
        if (await ManagerErrorAsync<AdminZoneView>(command.ResponsibleManagerUserId, cancellationToken) is { } managerError) return managerError;

        var now = time.GetUtcNow();
        var zone = new OperationalZone
        {
            Id = Guid.NewGuid(), Code = code, Name = command.Name!.Trim(), IsActive = command.IsActive, IsServiceable = command.IsServiceable,
            ResponsibleManagerUserId = command.ResponsibleManagerUserId, CreatedAt = now, UpdatedAt = now, Revision = 0,
            CreatedByUserId = command.ActorUserId, UpdatedByUserId = command.ActorUserId,
        };
        try
        {
            await unitOfWork.ExecuteInTransactionAsync<bool>(async token =>
            {
                unitOfWork.AddZone(zone);
                await unitOfWork.SaveChangesAsync(token);
                return (true, true);
            }, cancellationToken);
        }
        catch (SchedulingConflictException ex) when (ex.Kind == SchedulingConflictException.Duplicate)
        {
            return CodeExists<AdminZoneView>();
        }
        logger.LogInformation("Operational zone {ZoneId} ({ZoneCode}) created by {ActorUserId}: active={IsActive} serviceable={IsServiceable}",
            zone.Id, zone.Code, command.ActorUserId, zone.IsActive, zone.IsServiceable);
        return SchedulingResult<AdminZoneView>.Ok(View(zone, null));
    }

    public async Task<SchedulingResult<AdminZoneView>> UpdateAsync(UpdateZoneCommand command, CancellationToken cancellationToken)
    {
        if (await NotAdminAsync<AdminZoneView>(command.ActorUserId, cancellationToken) is { } forbidden) return forbidden;
        if (await ManagerErrorAsync<AdminZoneView>(command.ResponsibleManagerUserId, cancellationToken) is { } managerError) return managerError;
        try
        {
            var result = await unitOfWork.ExecuteInTransactionAsync<SchedulingResult<OperationalZone>>(async token =>
            {
                // FOR UPDATE: bookings recording this zone hold it FOR SHARE, so the usage count below cannot miss one.
                await unitOfWork.LockZonesAsync([command.ZoneId], shared: false, token);
                var zone = await unitOfWork.FindZoneForUpdateAsync(command.ZoneId, token);
                if (zone is null) return (NotFound<OperationalZone>(), false);
                if (zone.Revision != command.Revision) return (Stale<OperationalZone>(), false);
                var active = command.IsActive ?? zone.IsActive;
                var serviceable = command.IsServiceable ?? zone.IsServiceable;
                var stopsNewWork = (zone.IsActive && !active) || (zone.IsServiceable && !serviceable);
                if (stopsNewWork && await unitOfWork.GetZoneUsageAsync(zone.Id, time.GetUtcNow(), token) is { InUse: true } usage)
                {
                    return (SchedulingResult<OperationalZone>.Fail(SchedulingErrorCodes.ZoneInUse,
                        "The zone still has unfinished survey appointments or installation projects. Finish, cancel or move them before deactivating it or marking it not serviceable.",
                        new { blockingAppointments = usage.Appointments, blockingInstallationProjects = usage.InstallationProjects }), false);
                }
                var before = (zone.Name, zone.IsActive, zone.IsServiceable, zone.ResponsibleManagerUserId);
                zone.Name = command.Name!.Trim();
                zone.IsActive = active;
                zone.IsServiceable = serviceable;
                zone.ResponsibleManagerUserId = command.ResponsibleManagerUserId;
                zone.UpdatedAt = time.GetUtcNow();
                zone.UpdatedByUserId = command.ActorUserId;
                zone.Revision++;
                await unitOfWork.SaveChangesAsync(token);
                logger.LogInformation(
                    "Operational zone {ZoneId} updated by {ActorUserId} (revision {Revision}): active {WasActive}->{IsActive}, serviceable {WasServiceable}->{IsServiceable}, renamed={Renamed}, managerChanged={ManagerChanged}",
                    zone.Id, command.ActorUserId, zone.Revision, before.IsActive, zone.IsActive, before.IsServiceable, zone.IsServiceable,
                    before.Name != zone.Name, before.ResponsibleManagerUserId != zone.ResponsibleManagerUserId);
                return (SchedulingResult<OperationalZone>.Ok(zone), true);
            }, cancellationToken);
            if (!result.Succeeded) return SchedulingResult<AdminZoneView>.Fail(result.ErrorCode!, result.Message!, result.Details);
            return SchedulingResult<AdminZoneView>.Ok(await StatsViewAsync(result.Value!, cancellationToken));
        }
        catch (SchedulingConflictException ex) when (ex.Kind == SchedulingConflictException.StaleAppointment)
        {
            return Stale<AdminZoneView>();
        }
    }

    public async Task<SchedulingResult<ZoneDetailView>> SetCommunesAsync(SetZoneCommunesCommand command, CancellationToken cancellationToken)
    {
        if (await NotAdminAsync<ZoneDetailView>(command.ActorUserId, cancellationToken) is { } forbidden) return forbidden;
        var wanted = command.CommuneCodes!.Select(c => c.Trim()).Distinct(StringComparer.Ordinal).ToList();
        var today = AdministrativeCatalogClock.Today(time);
        var active = (await catalog.GetActiveCommunesAsync(wanted, today, cancellationToken)).Select(c => c.Code).ToHashSet(StringComparer.Ordinal);
        var missing = wanted.Where(c => !active.Contains(c)).ToList();
        if (missing.Count > 0)
        {
            var superseded = new List<string>();
            foreach (var code in missing)
                if (await catalog.FindCommuneAsync(code, cancellationToken) is not null) superseded.Add(code);
            var unknown = missing.Except(superseded).ToList();
            return SchedulingResult<ZoneDetailView>.Fail(unknown.Count > 0 ? SchedulingErrorCodes.LocationUnknown : SchedulingErrorCodes.LocationSuperseded,
                unknown.Count > 0
                    ? "One or more commune codes are not official communes."
                    : "One or more communes are no longer in force; map their current successors instead.",
                new { unknownCommuneCodes = unknown, supersededCommuneCodes = superseded });
        }
        if (await unitOfWork.GetZoneAsync(command.ZoneId, cancellationToken) is null) return NotFound<ZoneDetailView>();
        var reason = string.IsNullOrWhiteSpace(command.Reason) ? null : command.Reason.Trim();

        try
        {
            var result = await unitOfWork.ExecuteInTransactionAsync<SchedulingResult<OperationalZone>>(async token =>
            {
                var elsewhere = (await unitOfWork.FindCommuneMappingsAsync(wanted, token)).Where(m => m.ZoneId != command.ZoneId).ToList();
                var heldByActive = elsewhere.Where(m => m.ZoneIsActive).ToList();
                if (heldByActive.Count > 0) return (AlreadyMapped<OperationalZone>(heldByActive), false);
                var otherZones = elsewhere.Select(m => m.ZoneId).Distinct().ToList();

                // FOR UPDATE on this zone and on the inactive zones the moved communes leave (sorted inside).
                await unitOfWork.LockZonesAsync(otherZones.Append(command.ZoneId), shared: false, token);
                var zone = await unitOfWork.FindZoneForUpdateAsync(command.ZoneId, token);
                if (zone is null) return (NotFound<OperationalZone>(), false);
                if (zone.Revision != command.Revision) return (Stale<OperationalZone>(), false);
                // Re-read under the locks: a commune may have been mapped by an active zone meanwhile.
                elsewhere = (await unitOfWork.FindCommuneMappingsAsync(wanted, token)).Where(m => m.ZoneId != command.ZoneId).ToList();
                heldByActive = elsewhere.Where(m => m.ZoneIsActive).ToList();
                if (heldByActive.Count > 0) return (AlreadyMapped<OperationalZone>(heldByActive), false);

                var current = await unitOfWork.FindActiveMappingsForUpdateAsync(otherZones.Append(command.ZoneId).ToList(), token);
                var own = current.Where(m => m.ZoneId == command.ZoneId).ToList();
                var removed = own.Where(m => !wanted.Contains(m.CommuneCode)).ToList();
                var moved = current.Where(m => m.ZoneId != command.ZoneId && wanted.Contains(m.CommuneCode)).ToList();
                var added = wanted.Where(c => own.All(m => m.CommuneCode != c)).ToList();
                if (removed.Count == 0 && added.Count == 0) return (SchedulingResult<OperationalZone>.Ok(zone), false);   // nothing to change

                var now = time.GetUtcNow();
                var leaving = removed.Concat(moved).Select(m => m.CommuneCode).ToList();
                var usage = await unitOfWork.GetCommuneUsageAsync(leaving, now, token);
                if (usage.Count > 0)
                {
                    return (SchedulingResult<OperationalZone>.Fail(SchedulingErrorCodes.CommuneInUse,
                        "Sites in one or more communes being removed or moved have unfinished survey appointments or installation projects. Confirmed work is never reassigned silently: finish, cancel or move it first.",
                        new
                        {
                            communes = usage.Select(u => new { communeCode = u.CommuneCode, blockingAppointments = u.Appointments, blockingInstallationProjects = u.InstallationProjects }),
                            blockingAppointments = usage.Sum(u => u.Appointments),
                            blockingInstallationProjects = usage.Sum(u => u.InstallationProjects),
                        }), false);
                }

                foreach (var mapping in removed)
                {
                    mapping.RemovedAt = now;
                    mapping.RemovedByUserId = command.ActorUserId;
                    mapping.RemovalReason = reason;
                }
                foreach (var mapping in moved)
                {
                    mapping.RemovedAt = now;
                    mapping.RemovedByUserId = command.ActorUserId;
                    mapping.RemovalReason = Trim($"MOVED_TO_ZONE {zone.Code}{(reason is null ? "" : $": {reason}")}", 500);
                }
                // Soft removals first: the partial unique index only counts active mappings.
                await unitOfWork.SaveChangesAsync(token);
                foreach (var code in added)
                    unitOfWork.AddZoneCommune(new OperationalZoneCommune
                    {
                        Id = Guid.NewGuid(), ZoneId = zone.Id, CommuneCode = code, AssignedByUserId = command.ActorUserId, AssignedAt = now,
                    });
                zone.UpdatedAt = now;
                zone.Revision++;
                await unitOfWork.SaveChangesAsync(token);
                return (SchedulingResult<OperationalZone>.Ok(zone), true);
            }, cancellationToken);
            if (!result.Succeeded) return SchedulingResult<ZoneDetailView>.Fail(result.ErrorCode!, result.Message!, result.Details);
            return SchedulingResult<ZoneDetailView>.Ok(await DetailAsync((await unitOfWork.GetZoneAsync(command.ZoneId, cancellationToken))!, cancellationToken));
        }
        catch (SchedulingConflictException ex) when (ex.Kind == SchedulingConflictException.CommuneAlreadyMapped)
        {
            var holders = (await unitOfWork.FindCommuneMappingsAsync(wanted, cancellationToken)).Where(m => m.ZoneId != command.ZoneId).ToList();
            return AlreadyMapped<ZoneDetailView>(holders);
        }
        catch (SchedulingConflictException ex) when (ex.Kind == SchedulingConflictException.StaleAppointment)
        {
            return Stale<ZoneDetailView>();
        }
    }

    /// <summary>
    /// The customer sets the official location (province + current commune) of their own site. Both codes must be ACTIVE catalog rows
    /// and belong together (422 ADMINISTRATIVE_LOCATION_UNKNOWN / ADMINISTRATIVE_LOCATION_SUPERSEDED). A change is refused with
    /// SITE_HAS_ACTIVE_BOOKING while an open or confirmed appointment of the site records a zone (the location decided who may serve
    /// it), and with SITE_HAS_ACTIVE_INSTALLATION while an installation project of the site is unfinished. Open appointments whose start
    /// passed are ignored (they are auto-cancelled). Legacy appointments booked before zones existed (operational_zone_id NULL) do not
    /// block: they record the active zone now mapping the commune, in the same transaction under the site lock (rule M1), so they can
    /// be accepted/assigned again. Re-sending the current location changes nothing.
    /// </summary>
    public async Task<SchedulingResult<SiteLocationView>> SetSiteLocationAsync(SetSiteLocationCommand command, CancellationToken cancellationToken)
    {
        var province = AdministrativeCodeFormats.Normalize(command.ProvinceCode);
        var commune = AdministrativeCodeFormats.Normalize(command.CommuneCode);
        if (AdministrativeLocationRules.CheckFormat(province, commune, communeRequired: true) is { } format)
            return SchedulingResult<SiteLocationView>.Fail(SchedulingErrorCodes.ValidationFailed, format.Message,
                new Dictionary<string, string[]> { [format.Field] = [format.Message] });
        if (await AdministrativeLocationRules.CheckCatalogAsync(catalog, province, commune, AdministrativeCatalogClock.Today(time), cancellationToken) is { } problem)
            return SchedulingResult<SiteLocationView>.Fail(problem.Code, problem.Message, new Dictionary<string, string[]> { [problem.Field] = [problem.Message] });

        var result = await unitOfWork.ExecuteInTransactionAsync<SchedulingResult<bool>>(async token =>
        {
            await unitOfWork.LockPropertySiteAsync(command.PropertySiteId, token);
            var site = await unitOfWork.FindPropertySiteForUpdateAsync(command.PropertySiteId, token);
            var owner = site is null ? null : await unitOfWork.GetPropertySiteAsync(site.Id, token);
            if (site is null || owner?.CustomerUserId != command.CustomerUserId)
                return (SchedulingResult<bool>.Fail(SchedulingErrorCodes.PropertySiteNotFound, "Property site was not found."), false);
            var now = time.GetUtcNow();
            var active = (await unitOfWork.FindActiveSiteAppointmentsForUpdateAsync(site.Id, token))
                .Where(a => !(AppointmentStatuses.IsOpen(a.Status) && a.StartsAt <= now)).ToList();
            var changed = site.ProvinceCode != province || site.CommuneCode != commune;
            if (changed)
            {
                if (active.Any(a => a.OperationalZoneId is not null))
                    return (SchedulingResult<bool>.Fail(SchedulingErrorCodes.SiteHasActiveBooking,
                        "This site has an active survey appointment. Cancel it before changing the administrative location."), false);
                // Installation Flow: the project's zone and its technician's coverage were resolved from this location. Project creation and
                // assignment take the same site lock, so a project cannot appear between this check and the commit.
                if (await unitOfWork.SiteHasActiveInstallationProjectAsync(site.Id, token))
                    return (SchedulingResult<bool>.Fail(SchedulingErrorCodes.SiteHasActiveInstallation,
                        "This site has an unfinished installation project. The administrative location cannot change until it is completed or cancelled."), false);
                site.ProvinceCode = province;
                site.CommuneCode = commune;
                await unitOfWork.SaveChangesAsync(token);
            }

            var legacy = active.Where(a => a.OperationalZoneId is null).ToList();
            if (legacy.Count > 0)
            {
                // Legacy bookings (M1): record the zone that now serves the site (FOR SHARE: it cannot be deactivated before commit).
                var coverage = await unitOfWork.GetSiteCoverageAsync(site.Id, token);
                if (coverage.Zone is { } zone)
                {
                    await unitOfWork.LockZonesAsync([zone.Id], shared: true, token);
                    foreach (var appointment in legacy)
                    {
                        appointment.OperationalZoneId = zone.Id;
                        appointment.UpdatedAt = now;
                        appointment.Revision++;
                    }
                    await unitOfWork.SaveChangesAsync(token);
                }
            }
            return (SchedulingResult<bool>.Ok(true), true);
        }, cancellationToken);
        if (!result.Succeeded) return SchedulingResult<SiteLocationView>.Fail(result.ErrorCode!, result.Message!);

        var coverage = await unitOfWork.GetSiteCoverageAsync(command.PropertySiteId, cancellationToken);
        var name = (await catalog.FindCommuneAsync(commune!, cancellationToken))?.Name;
        return SchedulingResult<SiteLocationView>.Ok(new SiteLocationView(command.PropertySiteId, province!, commune!, name, coverage.Status, ZoneView.From(coverage.Zone)));
    }

    private async Task<ZoneDetailView> DetailAsync(OperationalZone zone, CancellationToken cancellationToken)
        => new(await StatsViewAsync(zone, cancellationToken), (await unitOfWork.ListZoneCommunesAsync(zone.Id, cancellationToken))
            .Select(c => new ZoneCommuneView(c.CommuneCode, c.CommuneName, c.UnitType, c.ProvinceCode, c.AssignedAt)).ToList());

    private async Task<AdminZoneView> StatsViewAsync(OperationalZone zone, CancellationToken cancellationToken)
        => View(zone, (await unitOfWork.ListZoneStatsAsync(cancellationToken)).FirstOrDefault(s => s.ZoneId == zone.Id));

    private async Task<SchedulingResult<T>?> ManagerErrorAsync<T>(Guid? managerUserId, CancellationToken cancellationToken)
        => managerUserId is { } id && !await unitOfWork.IsActiveUserWithRoleAsync(id, [RoleCodes.Manager, RoleCodes.Admin], cancellationToken)
            ? SchedulingResult<T>.Fail(SchedulingErrorCodes.ResponsibleManagerInvalid, "The responsible manager must be an active MANAGER or ADMIN user.")
            : null;

    private static AdminZoneView View(OperationalZone z, ZoneStats? stats) => new(
        z.Id, z.Code, z.Name, z.IsActive, z.IsServiceable, z.ResponsibleManagerUserId, stats?.Communes ?? 0, stats?.Technicians ?? 0,
        stats?.SchedulableTechnicians ?? 0, (stats?.SchedulableTechnicians ?? 0) > 0, z.Revision, z.CreatedAt, z.UpdatedAt);

    private static SchedulingResult<T> NotFound<T>() => SchedulingResult<T>.Fail(SchedulingErrorCodes.ZoneNotFound, "Operational zone was not found.");

    private static SchedulingResult<T> Stale<T>() => SchedulingResult<T>.Fail(SchedulingErrorCodes.ZoneStale, "The zone changed meanwhile. Reload and try again.");

    private static SchedulingResult<T> CodeExists<T>() => SchedulingResult<T>.Fail(SchedulingErrorCodes.ZoneCodeExists, "An operational zone with this code already exists.");

    private static SchedulingResult<T> AlreadyMapped<T>(IReadOnlyList<CommuneMappingRow> holders)
        => SchedulingResult<T>.Fail(SchedulingErrorCodes.CommuneAlreadyMapped,
            "One or more communes already belong to another active zone. Remove them from that zone first (a commune belongs to one zone at a time).",
            new { communes = holders.Select(h => new { communeCode = h.CommuneCode, zoneId = h.ZoneId, zoneCode = h.ZoneCode, zoneName = h.ZoneName }) });

    private static string? Trim(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= max ? value.Trim() : value.Trim()[..max];
}
