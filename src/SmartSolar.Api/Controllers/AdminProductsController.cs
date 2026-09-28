using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartSolar.Api.Contracts;
using SmartSolar.Api.Contracts.Catalog;
using SmartSolar.Api.Extensions;
using SmartSolar.Modules.Catalog.Constants;
using SmartSolar.Modules.Catalog.ManageProducts;
using SmartSolar.Modules.Identity.Constants;

namespace SmartSolar.Api.Controllers;

/// <summary>Catalog management, restricted to ADMIN and MANAGER.</summary>
[ApiController]
[Route("api/admin/products")]
[Authorize(Roles = $"{RoleCodes.Admin},{RoleCodes.Manager}")]
[EnableRateLimiting(RateLimitingExtensions.CatalogWritePolicy)]
public sealed class AdminProductsController : ControllerBase
{
    private readonly CreateProductHandler _createHandler;
    private readonly UpdateProductHandler _updateHandler;
    private readonly ChangeProductStatusHandler _changeStatusHandler;
    private readonly DeleteProductHandler _deleteHandler;
    private readonly IValidator<CreateProductCommand> _createValidator;
    private readonly IValidator<UpdateProductCommand> _updateValidator;
    private readonly IValidator<ChangeProductStatusCommand> _changeStatusValidator;

    public AdminProductsController(
        CreateProductHandler createHandler,
        UpdateProductHandler updateHandler,
        ChangeProductStatusHandler changeStatusHandler,
        DeleteProductHandler deleteHandler,
        IValidator<CreateProductCommand> createValidator,
        IValidator<UpdateProductCommand> updateValidator,
        IValidator<ChangeProductStatusCommand> changeStatusValidator)
    {
        _createHandler = createHandler;
        _updateHandler = updateHandler;
        _changeStatusHandler = changeStatusHandler;
        _deleteHandler = deleteHandler;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
        _changeStatusValidator = changeStatusValidator;
    }

    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<ProductResponse>), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(
        [FromBody] CreateProductRequest request,
        CancellationToken cancellationToken)
    {
        var command = new CreateProductCommand(
            request.Sku ?? string.Empty,
            request.ProductType ?? string.Empty,
            request.Category,
            request.Name ?? string.Empty,
            request.Brand ?? string.Empty,
            request.Model,
            request.Unit ?? string.Empty,
            request.UnitPrice,
            request.Currency ?? string.Empty,
            request.RatedPowerW,
            request.WidthMm,
            request.HeightMm,
            request.WarrantyMonth,
            SpecJson.ToRaw(request.Spec),
            request.ImageUrl,
            request.Status);

        if (await Validate(_createValidator, command, cancellationToken) is { } validationError)
        {
            return validationError;
        }

        var result = await _createHandler.HandleAsync(command, cancellationToken);

        if (result.Outcome == ProductCommandOutcome.DuplicateSku)
        {
            return DuplicateSku();
        }

        return StatusCode(
            StatusCodes.Status201Created,
            Success(ProductResponse.From(result.Product!)));
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse<ProductResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(
        [FromRoute] Guid id,
        [FromBody] UpdateProductRequest request,
        CancellationToken cancellationToken)
    {
        var command = new UpdateProductCommand(
            id,
            request.Sku ?? string.Empty,
            request.ProductType ?? string.Empty,
            request.Category,
            request.Name ?? string.Empty,
            request.Brand ?? string.Empty,
            request.Model,
            request.Unit ?? string.Empty,
            request.UnitPrice,
            request.Currency ?? string.Empty,
            request.RatedPowerW,
            request.WidthMm,
            request.HeightMm,
            request.WarrantyMonth,
            SpecJson.ToRaw(request.Spec),
            request.ImageUrl);

        if (await Validate(_updateValidator, command, cancellationToken) is { } validationError)
        {
            return validationError;
        }

        var result = await _updateHandler.HandleAsync(command, cancellationToken);

        return result.Outcome switch
        {
            ProductCommandOutcome.NotFound => ProductNotFound(),
            ProductCommandOutcome.DuplicateSku => DuplicateSku(),
            _ => Ok(Success(ProductResponse.From(result.Product!)))
        };
    }

    [HttpPatch("{id:guid}/status")]
    [ProducesResponseType(typeof(ApiResponse<ProductResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ChangeStatus(
        [FromRoute] Guid id,
        [FromBody] ChangeProductStatusRequest request,
        CancellationToken cancellationToken)
    {
        var command = new ChangeProductStatusCommand(id, request.Status ?? string.Empty);

        if (await Validate(_changeStatusValidator, command, cancellationToken) is { } validationError)
        {
            return validationError;
        }

        var result = await _changeStatusHandler.HandleAsync(command, cancellationToken);

        if (result.Outcome == ProductCommandOutcome.NotFound)
        {
            return ProductNotFound();
        }

        return Ok(Success(ProductResponse.From(result.Product!)));
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse<ProductDeletedResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Delete(
        [FromRoute] Guid id,
        CancellationToken cancellationToken)
    {
        var result = await _deleteHandler.HandleAsync(id, cancellationToken);

        if (result.Outcome == ProductCommandOutcome.NotFound)
        {
            return ProductNotFound();
        }

        return Ok(Success(new ProductDeletedResponse(id, Deleted: true)));
    }

    private IActionResult ProductNotFound()
        => NotFound(Failure(
            CatalogErrorCodes.ProductNotFound,
            "The product was not found."));

    private IActionResult DuplicateSku()
        => Conflict(Failure(
            CatalogErrorCodes.SkuAlreadyExists,
            "A product with this SKU already exists."));

    private async Task<IActionResult?> Validate<T>(
        IValidator<T> validator,
        T command,
        CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(command, cancellationToken);

        if (validation.IsValid)
        {
            return null;
        }

        var details = validation.Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());

        return BadRequest(Failure(
            AuthErrorCodes.ValidationFailed,
            "One or more fields are invalid.",
            details));
    }

    private ApiResponse<TData> Success<TData>(TData data)
        => ApiResponse<TData>.Success(data, HttpContext.GetTraceId());

    private ApiResponse<object> Failure(string code, string message, object? details = null)
        => ApiResponse<object>.Failure(new ApiError(code, message, details), HttpContext.GetTraceId());
}
