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
                var details = validation.Errors
                .GroupBy(x => x.PropertyName)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .Select(x => x.ErrorMessage)
                        .ToArray());

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
            request.Note);

        var validation =
            await _createPropertySiteValidator.ValidateAsync(
                command,
                cancellationToken);

        if (!validation.IsValid)
        {
            var details = validation.Errors
                .GroupBy(x => x.PropertyName)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .Select(x => x.ErrorMessage)
                        .ToArray());

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
