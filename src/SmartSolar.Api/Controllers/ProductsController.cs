using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartSolar.Api.Contracts;
using SmartSolar.Api.Contracts.Catalog;
using SmartSolar.Api.Extensions;
using SmartSolar.Modules.Catalog.Constants;
using SmartSolar.Modules.Catalog.GetProduct;
using SmartSolar.Modules.Catalog.ListProducts;
using SmartSolar.Modules.Common.Paging;
using SmartSolar.Modules.Identity.Constants;

namespace SmartSolar.Api.Controllers;

/// <summary>Catalog reads for any signed-in user; only ACTIVE, non-deleted products are visible.</summary>
[ApiController]
[Route("api/products")]
[EnableRateLimiting(RateLimitingExtensions.CatalogReadPolicy)]
public sealed class ProductsController : ControllerBase
{
    private readonly ListProductsHandler _listProductsHandler;
    private readonly GetProductHandler _getProductHandler;
    private readonly IValidator<ListProductsQuery> _listValidator;

    public ProductsController(
        ListProductsHandler listProductsHandler,
        GetProductHandler getProductHandler,
        IValidator<ListProductsQuery> listValidator)
    {
        _listProductsHandler = listProductsHandler;
        _getProductHandler = getProductHandler;
        _listValidator = listValidator;
    }

    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<ProductResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] ProductListRequest request,
        CancellationToken cancellationToken)
    {
        var query = new ListProductsQuery(
            request.Search,
            request.ProductType,
            request.Category,
            request.Brand,
            request.MinPower,
            request.MaxPower,
            request.MinPrice,
            request.MaxPrice,
            request.Page ?? ListProductsQuery.DefaultPage,
            request.PageSize ?? ListProductsQuery.DefaultPageSize,
            request.SortBy,
            request.SortDirection);

        if (await Validate(_listValidator, query, cancellationToken) is { } validationError)
        {
            return validationError;
        }

        var page = await _listProductsHandler.HandleAsync(query, cancellationToken);

        return Ok(Success(new PagedResult<ProductResponse>(
            page.Items.Select(ProductResponse.From).ToList(),
            page.Page,
            page.PageSize,
            page.TotalItems,
            page.TotalPages)));
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse<ProductResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(
        [FromRoute] Guid id,
        CancellationToken cancellationToken)
    {
        var product = await _getProductHandler.HandleAsync(id, cancellationToken);

        if (product is null)
        {
            return NotFound(Failure(
                CatalogErrorCodes.ProductNotFound,
                "The product was not found."));
        }

        return Ok(Success(ProductResponse.From(product)));
    }

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
