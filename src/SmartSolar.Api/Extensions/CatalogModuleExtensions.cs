using SmartSolar.Modules.Catalog.GetProduct;
using SmartSolar.Modules.Catalog.ListProducts;
using SmartSolar.Modules.Catalog.ManageProducts;

namespace SmartSolar.Api.Extensions;

public static class CatalogModuleExtensions
{
    public static IServiceCollection AddCatalogModule(this IServiceCollection services)
    {
        services.AddScoped<ListProductsHandler>();
        services.AddScoped<GetProductHandler>();
        services.AddScoped<CreateProductHandler>();
        services.AddScoped<UpdateProductHandler>();
        services.AddScoped<ChangeProductStatusHandler>();
        services.AddScoped<DeleteProductHandler>();
        services.AddScoped<ProductCacheInvalidator>();

        // Catalog validators live in the Modules assembly, which AddIdentityModule
        // already scans; scanning it again would register every validator twice.

        return services;
    }
}
