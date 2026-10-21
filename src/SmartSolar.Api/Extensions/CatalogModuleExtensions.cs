using SmartSolar.Modules.Catalog.GetProduct;
using SmartSolar.Modules.Catalog.ListProducts;
using SmartSolar.Modules.Catalog.ManageProducts;
using SmartSolar.Modules.Catalog.ProductImages;

namespace SmartSolar.Api.Extensions;

public static class CatalogModuleExtensions
{
    public static IServiceCollection AddCatalogModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(ConfigurationBinding.Bind<CatalogOptions>(configuration, CatalogOptions.SectionName));

        services.AddScoped<ListProductsHandler>();
        services.AddScoped<GetProductHandler>();
        services.AddScoped<CreateProductHandler>();
        services.AddScoped<UpdateProductHandler>();
        services.AddScoped<ChangeProductStatusHandler>();
        services.AddScoped<DeleteProductHandler>();
        services.AddScoped<ProductCacheInvalidator>();
        services.AddScoped<ProductImageHandlers>();

        // Catalog validators live in the Modules assembly, which AddIdentityModule
        // already scans; scanning it again would register every validator twice.

        return services;
    }
}
