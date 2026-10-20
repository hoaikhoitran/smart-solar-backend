using SmartSolar.Infrastructure.Persistence.InstallationWorkflow;
using SmartSolar.Modules.OnSiteSurveys;

namespace SmartSolar.Api.Extensions;

/// <summary>
/// Installation Flow: on-site survey → quotation → contract → PayOS payment → installation.
/// Requires <see cref="FileStorageModuleExtensions.AddFeatureFlagsAndFileStorage"/>. Options are
/// validated at startup; secrets come only from the environment.
/// </summary>
public static class InstallationWorkflowModuleExtensions
{
    public static IServiceCollection AddInstallationWorkflow(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(ConfigurationBinding.Bind<OnSiteSurveyOptions>(configuration, OnSiteSurveyOptions.SectionName));
        services.AddScoped<IOnSiteSurveyUnitOfWork, OnSiteSurveyUnitOfWork>();
        services.AddScoped<OnSiteSurveyHandlers>();

        services.AddSingleton(ConfigurationBinding.Bind<SmartSolar.Modules.Quotations.QuotationOptions>(configuration, SmartSolar.Modules.Quotations.QuotationOptions.SectionName));
        services.AddScoped<SmartSolar.Modules.Quotations.IQuotationUnitOfWork, QuotationUnitOfWork>();
        services.AddScoped<SmartSolar.Modules.Quotations.QuotationHandlers>();

        services.AddScoped<SmartSolar.Modules.CustomerContracts.IContractUnitOfWork, ContractUnitOfWork>();
        services.AddScoped<SmartSolar.Modules.CustomerContracts.ContractHandlers>();

        var payments = ConfigurationBinding.Bind<SmartSolar.Modules.Payments.PaymentOptions>(configuration, SmartSolar.Modules.Payments.PaymentOptions.SectionName);
        var payOs = ConfigurationBinding.Bind<SmartSolar.Infrastructure.ExternalProviders.PayOS.PayOsOptions>(configuration, SmartSolar.Infrastructure.ExternalProviders.PayOS.PayOsOptions.SectionName);
        if (payOs.Enabled)
        {
            var missing = new[] { (payOs.ClientId, "ClientId"), (payOs.ApiKey, "ApiKey"), (payOs.ChecksumKey, "ChecksumKey") }
                .Where(x => string.IsNullOrWhiteSpace(x.Item1)).Select(x => x.Item2).ToList();
            if (missing.Count > 0) throw new InvalidOperationException($"'Providers:PayOS' is enabled but missing: {string.Join(", ", missing)}.");
            if (!Uri.TryCreate(payments.ReturnUrl, UriKind.Absolute, out _) || !Uri.TryCreate(payments.CancelUrl, UriKind.Absolute, out _))
                throw new InvalidOperationException("'Payments:ReturnUrl' and 'Payments:CancelUrl' must be absolute URLs when PayOS is enabled.");
        }
        services.AddSingleton(payments);
        services.AddSingleton(payOs);
        services.AddHttpClient<SmartSolar.Modules.Payments.IPaymentGateway, SmartSolar.Infrastructure.ExternalProviders.PayOS.PayOsGateway>(client =>
        {
            client.BaseAddress = new Uri(payOs.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(payOs.TimeoutSeconds, 3, 60));
        }).RemoveAllLoggers();
        services.AddScoped<SmartSolar.Modules.Payments.IPaymentUnitOfWork, PaymentUnitOfWork>();
        services.AddScoped<SmartSolar.Modules.Payments.PaymentService>();
        services.AddSingleton(ConfigurationBinding.Bind<SmartSolar.Modules.Installations.InstallationOptions>(configuration, SmartSolar.Modules.Installations.InstallationOptions.SectionName));
        services.AddScoped<SmartSolar.Modules.Installations.IInstallationUnitOfWork, InstallationUnitOfWork>();
        services.AddScoped<SmartSolar.Modules.Installations.InstallationSchedulePlanner>();
        services.AddScoped<SmartSolar.Modules.Installations.InstallationHandlers>();
        services.AddScoped<SmartSolar.Modules.Installations.InstallationEvidenceHandlers>();

        if (configuration.GetValue("Payments:ReconciliationWorkerEnabled", true))
            services.AddHostedService<Background.PaymentReconciliationWorker>();

        return services;
    }
}
