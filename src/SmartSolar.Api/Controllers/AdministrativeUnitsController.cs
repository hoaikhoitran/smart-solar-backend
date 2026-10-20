using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartSolar.Api.Contracts;
using SmartSolar.Api.Extensions;
using SmartSolar.Modules.AdministrativeUnits;

namespace SmartSolar.Api.Controllers;

/// <summary>
/// Public (anonymous) reads of the official administrative catalog for address dropdowns: provinces, communes of a province, and the
/// legacy lookup (pre-2025 commune / district names) that suggests current communes but never selects one. Reference data that only
/// the controlled CLI import changes, so responses may be cached for an hour.
/// </summary>
[ApiController]
[Route("api/administrative-units")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitingExtensions.CatalogReadPolicy)]
public sealed class AdministrativeUnitsController(AdministrativeUnitQueries queries) : OperationResultControllerBase
{
    private const int CacheSeconds = 3600;

    /// <summary>GET /api/administrative-units/provinces — provinces in force.</summary>
    [HttpGet("provinces")]
    [ResponseCache(Duration = CacheSeconds, Location = ResponseCacheLocation.Any)]
    public async Task<IActionResult> Provinces(CancellationToken cancellationToken)
        => base.Ok(ApiResponse<IReadOnlyList<ProvinceView>>.Success(await queries.ProvincesAsync(cancellationToken), HttpContext.GetTraceId()));

    /// <summary>GET /api/administrative-units/communes?provinceCode=79[&amp;q=thu duc][&amp;unitType=WARD] — communes in force, by name.</summary>
    [HttpGet("communes")]
    [ResponseCache(Duration = CacheSeconds, Location = ResponseCacheLocation.Any)]
    public async Task<IActionResult> Communes([FromQuery] string? provinceCode, [FromQuery] string? q, [FromQuery] string? unitType, CancellationToken cancellationToken)
        => Respond(await queries.CommunesAsync(provinceCode, q, unitType, cancellationToken));

    /// <summary>
    /// GET /api/administrative-units/legacy-lookup?q=phuong 9&amp;provinceCode=79 — legacy (pre-2025) commune or district names/codes,
    /// accent-insensitive, at least 2 characters, at most 50 matches; every match lists all candidate current communes and
    /// requiresConfirmation is always true.
    /// </summary>
    [HttpGet("legacy-lookup")]
    [ResponseCache(Duration = CacheSeconds, Location = ResponseCacheLocation.Any)]
    public async Task<IActionResult> LegacyLookup([FromQuery] string? q, [FromQuery] string? provinceCode, CancellationToken cancellationToken)
        => Respond(await queries.LegacyLookupAsync(q, provinceCode, cancellationToken));
}
