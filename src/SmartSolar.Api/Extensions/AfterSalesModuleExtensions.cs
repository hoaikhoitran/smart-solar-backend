using SmartSolar.Infrastructure.Persistence.AfterSales;
using SmartSolar.Modules.AfterSales;

namespace SmartSolar.Api.Extensions;

/// <summary>
/// Flow 3 — After-Sales Services (warranty and repairs, maintenance, disaster protection, Sales queue, refunds, dashboard). Builds on the
/// Installation Flow (projects, assets, payments, scheduling), so it is enabled together with it. The background sweep can be switched
/// off with <c>AfterSales:WorkerEnabled = false</c>.
/// </summary>
public static class AfterSalesModuleExtensions
{
    public static IServiceCollection AddAfterSales(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IAfterSalesUnitOfWork, AfterSalesUnitOfWork>();
        services.AddScoped<IAfterSalesReportQueries, AfterSalesReportQueries>();
        services.AddScoped<IAdminOverviewQueries, AdminOverviewQueries>();
        services.AddScoped<AdminOverview>();
        services.AddScoped<AfterSalesViews>();
        services.AddScoped<SubscriptionTerms>();
        // Per-panel coverage (2026-11-25): bundled package provisioning after full payment, eligibility and staff reconciliation.
        services.AddScoped<PanelCoverageService>();
        services.AddScoped<SmartSolar.Modules.CustomerContracts.IBundledPackageProvisioner>(sp => sp.GetRequiredService<PanelCoverageService>());
        // Applies the after-sales lines of verified payments inside the payment confirmation transaction (wrapped by the installation-quotation
        // settlement registered as IPaymentItemSettlement in AddInstallationWorkflow).
        services.AddScoped<AfterSalesPaymentSettlement>();
        services.AddScoped<ServiceCustomerHandlers>();
        services.AddScoped<ServiceManagerHandlers>();
        services.AddScoped<TechnicianServiceHandlers>();
        services.AddScoped<AfterSalesEvidenceUrlHandlers>();
        services.AddScoped<ServiceQuotationHandlers>();
        services.AddScoped<ServicePackageHandlers>();
        services.AddScoped<SalesTaskHandlers>();
        services.AddScoped<AfterSalesSweepService>();
        services.AddScoped<AfterSalesDashboard>();
        if (configuration.GetValue("AfterSales:WorkerEnabled", true))
            services.AddHostedService<Background.AfterSalesWorker>();
        return services;
    }
}
