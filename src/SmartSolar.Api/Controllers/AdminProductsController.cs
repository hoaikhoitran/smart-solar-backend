using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartSolar.Api.Contracts;
using SmartSolar.Api.Contracts.Catalog;
using SmartSolar.Api.Extensions;
using SmartSolar.Modules.Catalog.Constants;
using SmartSolar.Modules.Catalog.ManageProducts;
using SmartSolar.Modules.Catalog.ProductImages;
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
    private readonly ProductImageHandlers _imageHandlers;
    private readonly IValidator<AddProductImageCommand> _addImageValidator;
    private readonly IValidator<ReorderProductImagesCommand> _reorderImagesValidator;

    public AdminProductsController(
        CreateProductHandler createHandler,
        UpdateProductHandler updateHandler,
        ChangeProductStatusHandler changeStatusHandler,
        DeleteProductHandler deleteHandler,
        IValidator<CreateProductCommand> createValidator,
        IValidator<UpdateProductCommand> updateValidator,
        IValidator<ChangeProductStatusCommand> changeStatusValidator,
        ProductImageHandlers imageHandlers,
        IValidator<AddProductImageCommand> addImageValidator,
        IValidator<ReorderProductImagesCommand> reorderImagesValidator)
    {
        _createHandler = createHandler;
        _updateHandler = updateHandler;
        _changeStatusHandler = changeStatusHandler;
        _deleteHandler = deleteHandler;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
        _changeStatusValidator = changeStatusValidator;
        _imageHandlers = imageHandlers;
        _addImageValidator = addImageValidator;
        _reorderImagesValidator = reorderImagesValidator;
    }

    /// <summary>
    /// Management detail of a product in any status (soft-deleted products are not found).
    /// Like every admin catalog action it shares the controller's catalog-write limit.
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse<ProductResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(
        [FromRoute] Guid id,
        CancellationToken cancellationToken)
    {
        var product = await _imageHandlers.GetProductAsync(id, cancellationToken);

        return product is null
            ? ProductNotFound()
            : Ok(Success(ProductResponse.From(product)));
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
            request.Status,
            request.Images?.Select(i => i is null ? null! : new ProductImageInput(i.Url ?? string.Empty, i.AltText)).ToList());

        if (await Validate(_createValidator, command, cancellationToken) is { } validationError)
        {
            return validationError;
        }

        var result = await _createHandler.HandleAsync(command, CurrentUserId(), cancellationToken);

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
            SpecJson.ToRaw(request.Spec));

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

    /// <summary>Appends an image (the first image of a product is always primary). Returns the product with its gallery.</summary>
    [HttpPost("{id:guid}/images")]
    [ProducesResponseType(typeof(ApiResponse<ProductResponse>), StatusCodes.Status201Created)]
    public async Task<IActionResult> AddImage(
        [FromRoute] Guid id,
        [FromBody] AddProductImageRequest request,
        CancellationToken cancellationToken)
    {
        var command = new AddProductImageCommand(
            id, request.Url ?? string.Empty, request.AltText, request.MakePrimary ?? false);

        if (await Validate(_addImageValidator, command, cancellationToken) is { } validationError)
        {
            return validationError;
        }

        var result = await _imageHandlers.AddAsync(command, CurrentUserId(), cancellationToken);

        return ImageResult(result, StatusCodes.Status201Created);
    }

    /// <summary>Removes an image; removing the primary promotes the lowest remaining sort order.</summary>
    [HttpDelete("{id:guid}/images/{imageId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<ProductResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> RemoveImage(
        [FromRoute] Guid id,
        [FromRoute] Guid imageId,
        CancellationToken cancellationToken)
        => ImageResult(await _imageHandlers.RemoveAsync(id, imageId, cancellationToken));

    /// <summary>Reorders the gallery; imageIds must be exactly the current image set.</summary>
    [HttpPut("{id:guid}/images/order")]
    [ProducesResponseType(typeof(ApiResponse<ProductResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ReorderImages(
        [FromRoute] Guid id,
        [FromBody] ReorderProductImagesRequest request,
        CancellationToken cancellationToken)
    {
        var command = new ReorderProductImagesCommand(id, request.ImageIds!);

        if (await Validate(_reorderImagesValidator, command, cancellationToken) is { } validationError)
        {
            return validationError;
        }

        return ImageResult(await _imageHandlers.ReorderAsync(command, cancellationToken));
    }

    [HttpPut("{id:guid}/images/{imageId:guid}/primary")]
    [ProducesResponseType(typeof(ApiResponse<ProductResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> SetPrimaryImage(
        [FromRoute] Guid id,
        [FromRoute] Guid imageId,
        CancellationToken cancellationToken)
        => ImageResult(await _imageHandlers.SetPrimaryAsync(id, imageId, cancellationToken));

    private IActionResult ImageResult(ProductImageResult result, int successStatus = StatusCodes.Status200OK)
        => result.Outcome switch
        {
            ProductImageOutcome.Succeeded => StatusCode(successStatus, Success(ProductResponse.From(result.Product!))),
            ProductImageOutcome.ProductNotFound => ProductNotFound(),
            ProductImageOutcome.ImageNotFound => NotFound(Failure(
                CatalogErrorCodes.ProductImageNotFound,
                "The product image was not found.")),
            ProductImageOutcome.Duplicate => Conflict(Failure(
                CatalogErrorCodes.ProductImageDuplicate,
                "This product already has an image with this URL.")),
            ProductImageOutcome.LimitReached => Conflict(Failure(
                CatalogErrorCodes.ProductImageLimitReached,
                "This product already has the maximum number of images.")),
            ProductImageOutcome.OrderMismatch => BadRequest(Failure(
                CatalogErrorCodes.ProductImageOrderMismatch,
                "'imageIds' must list every current image of the product exactly once.")),
            _ => Conflict(Failure(
                CatalogErrorCodes.ProductImageConflict,
                "The product images were changed concurrently. Reload and try again."))
        };

    private Guid? CurrentUserId()
    {
        var subject = User.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(subject, out var userId) ? userId : null;
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
