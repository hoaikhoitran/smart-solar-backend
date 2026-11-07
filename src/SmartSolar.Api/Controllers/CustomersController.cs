using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SmartSolar.Api.Contracts;
using SmartSolar.Api.Contracts.Customers;
using SmartSolar.Api.Contracts.PropertySites;
using SmartSolar.Api.Extensions;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.PreSurvey.Constants;
using SmartSolar.Modules.PreSurvey.CreateCustomerProfile;
using SmartSolar.Modules.PreSurvey.CreatePropertySite;

namespace SmartSolar.Api.Controllers
{
    [Route("api/customers")]
    [ApiController]
    [Authorize(Roles = RoleCodes.Customer)]
    public sealed class CustomersController : ControllerBase
    {
        private readonly CreateCustomerProfileHandler _handler;
        private readonly IValidator<CreateCustomerProfileCommand> _validator;
        private readonly CreatePropertySiteHandler
        _createPropertySiteHandler;

        private readonly IValidator<CreatePropertySiteCommand>
         _createPropertySiteValidator;
        public CustomersController(CreateCustomerProfileHandler handler, IValidator<CreateCustomerProfileCommand> validator, CreatePropertySiteHandler createPropertySiteHandler,
            IValidator<CreatePropertySiteCommand> createPropertySiteValidator)
        {
            _handler = handler;
            _validator = validator;
            _createPropertySiteHandler = createPropertySiteHandler;
            _createPropertySiteValidator = createPropertySiteValidator;

        }

        /// <summary>The caller's own property sites (by name), paged: <c>page</c> ≥ 1, <c>pageSize</c> 1–100 (default 20).</summary>
        [ProducesResponseType(typeof(SmartSolar.Api.Contracts.ApiResponse<SmartSolar.Modules.Common.Paging.PagedResult<SmartSolar.Modules.PreSurvey.ListMine.CustomerSiteListItem>>), StatusCodes.Status200OK)]
        [HttpGet("me/sites")]
        public async Task<IActionResult> ListMySites(
            [FromQuery] int page = 1, [FromQuery] int pageSize = 20,
            [FromServices] SmartSolar.Modules.PreSurvey.ListMine.CustomerListingHandler listing = null!,
            CancellationToken cancellationToken = default)
        {
            if (GetAuthenticatedUserId() is not { } userId)
                return Unauthorized(Failure(PreSurveyErrorCodes.Unauthorized, "Authentication is required."));
            if (PageProblem(page, pageSize) is { } problem) return problem;
            return Ok(Success(await listing.ListSitesAsync(userId, new SmartSolar.Modules.PreSurvey.ListMine.CustomerListPage(page, pageSize), cancellationToken)));
        }

        /// <summary>
        /// The caller's own pre-surveys, paged. <c>status</c> = DRAFT | SUBMITTED (optional); <c>sortBy</c> = updatedAt (default) |
        /// createdAt; <c>sortDirection</c> = desc (default) | asc.
        /// </summary>
        [ProducesResponseType(typeof(SmartSolar.Api.Contracts.ApiResponse<SmartSolar.Modules.Common.Paging.PagedResult<SmartSolar.Modules.PreSurvey.ListMine.CustomerPreSurveyListItem>>), StatusCodes.Status200OK)]
        [HttpGet("me/pre-surveys")]
        public async Task<IActionResult> ListMyPreSurveys(
            [FromQuery] string? status = null, [FromQuery] string? sortBy = null, [FromQuery] string? sortDirection = null,
            [FromQuery] int page = 1, [FromQuery] int pageSize = 20,
            [FromServices] SmartSolar.Modules.PreSurvey.ListMine.CustomerListingHandler listing = null!,
            CancellationToken cancellationToken = default)
        {
            if (GetAuthenticatedUserId() is not { } userId)
                return Unauthorized(Failure(PreSurveyErrorCodes.Unauthorized, "Authentication is required."));
            var errors = new Dictionary<string, string[]>();
            if (status is not null && !SmartSolar.Modules.PreSurvey.ListMine.CustomerListingHandler.PreSurveyStatuses.Contains(status.ToUpperInvariant()))
                errors["status"] = ["'status' must be DRAFT or SUBMITTED."];
            if (sortBy is not null && !SmartSolar.Modules.PreSurvey.ListMine.CustomerListingHandler.PreSurveySortFields.Contains(sortBy, StringComparer.OrdinalIgnoreCase))
                errors["sortBy"] = ["'sortBy' must be updatedAt or createdAt."];
            if (sortDirection is not null && sortDirection.ToLowerInvariant() is not ("asc" or "desc"))
                errors["sortDirection"] = ["'sortDirection' must be asc or desc."];
            if (errors.Count > 0) return BadRequest(Failure(PreSurveyErrorCodes.ValidationFailed, "One or more fields are invalid.", errors));
            if (PageProblem(page, pageSize) is { } problem) return problem;
            var ascending = string.Equals(sortDirection, "asc", StringComparison.OrdinalIgnoreCase);
            return Ok(Success(await listing.ListPreSurveysAsync(userId, status, sortBy, ascending,
                new SmartSolar.Modules.PreSurvey.ListMine.CustomerListPage(page, pageSize), cancellationToken)));
        }

        private IActionResult? PageProblem(int page, int pageSize)
        {
            var errors = new Dictionary<string, string[]>();
            if (page < 1) errors["page"] = ["'page' must be at least 1."];
            if (pageSize is < 1 or > SmartSolar.Modules.PreSurvey.ListMine.CustomerListPage.MaxPageSize)
                errors["pageSize"] = [$"'pageSize' must be between 1 and {SmartSolar.Modules.PreSurvey.ListMine.CustomerListPage.MaxPageSize}."];
            return errors.Count == 0 ? null : BadRequest(Failure(PreSurveyErrorCodes.ValidationFailed, "One or more fields are invalid.", errors));
        }

        [HttpPost("me")]
        public async Task<IActionResult> CreatMyProfile([FromBody] CreateCustomerProfileRequest request, 
                                                            CancellationToken token)
        {
            var userId = GetAuthenticatedUserId();
            if(userId is null)
            {
                return Unauthorized(Failure(PreSurveyErrorCodes.Unauthorized, "Authentication is required"));
            }

            var command = new CreateCustomerProfileCommand(userId.Value, request.CustomerType, request.CompanyName, request.TaxCode, request.Note);
            var validation = await _validator.ValidateAsync(command, token);
            if (!validation.IsValid)
            {
                var details = ValidationDetails.From(validation);

                return BadRequest(
                    Failure(
                        PreSurveyErrorCodes.ValidationFailed,
                        "One or more fields are invalid.",
                        details));
            }
            var result = await _handler.HandleAsync(
            command,
            token);

            if (result.Outcome ==
                CreateCustomerProfileOutcome.AlreadyExists)
            {
                return Conflict(
                    Failure(
                        PreSurveyErrorCodes.CustomerAlreadyExists,
                        "Customer profile already exists."));
            }

            return StatusCode(
                StatusCodes.Status201Created,
                Success(
                    new CreateCustomerProfileResponse(
                        result.CustomerId!.Value)));
    }

    [HttpPost("me/sites")]
    public async Task<IActionResult> CreatePropertySite(
        [FromBody] CreatePropertySiteRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetAuthenticatedUserId();

        if (userId is null)
        {
            return Unauthorized(
                Failure(
                    PreSurveyErrorCodes.Unauthorized,
                    "Authentication is required."));
        }

        var command = new CreatePropertySiteCommand(
            userId.Value,
            request.Name,
            request.Province,
            request.District,
            request.Ward,
            request.StreetLine,
            request.Latitude,
            request.Longitude,
            request.InstallationSurfaceType,
            request.SurfaceMaterial,
            request.Note,
            request.ProvinceCode,
            request.CommuneCode);

        var validation =
            await _createPropertySiteValidator.ValidateAsync(
                command,
                cancellationToken);

        if (!validation.IsValid)
        {
            var details = ValidationDetails.From(validation);

            return BadRequest(
                Failure(
                    PreSurveyErrorCodes.ValidationFailed,
                    "One or more fields are invalid.",
                    details));
        }

        var result =
            await _createPropertySiteHandler.HandleAsync(
                command,
                cancellationToken);

        if (result.Outcome ==
            CreatePropertySiteOutcome.CustomerNotFound)
        {
            return NotFound(
                Failure(
                    PreSurveyErrorCodes.CustomerProfileNotFound,
                    "Customer profile was not found."));
        }

        if (result.Outcome ==
            CreatePropertySiteOutcome.InvalidLocation)
        {
            var problem = result.LocationProblem!;
            return UnprocessableEntity(
                Failure(
                    problem.Code,
                    problem.Message,
                    new Dictionary<string, string[]>
                    {
                        [problem.Field] = [problem.Message]
                    }));
        }

        return StatusCode(
            StatusCodes.Status201Created,
            Success(
                new CreatePropertySiteResponse(
                    result.PropertySiteId!.Value)));
    }

    private Guid? GetAuthenticatedUserId()
    {
        var subject = User.FindFirstValue(JwtRegisteredClaimNames.Sub)
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(subject, out var userId) ? userId : null;
    }

    private ApiResponse<T> Success<T>(T data)
    => ApiResponse<T>.Success(
        data,
        HttpContext.GetTraceId());

    private ApiResponse<object> Failure(
    string code,
    string message,
    object? details = null)
    => ApiResponse<object>.Failure(
        new ApiError(code, message, details),
        HttpContext.GetTraceId());

    }
}
