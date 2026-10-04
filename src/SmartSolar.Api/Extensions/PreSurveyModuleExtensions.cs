using SmartSolar.Modules.PreSurvey.ClaimSurveyRequest;
using SmartSolar.Modules.PreSurvey.CreateCustomerProfile;
using SmartSolar.Modules.PreSurvey.CreatePreSurvey;
using SmartSolar.Modules.PreSurvey.CreatePropertySite;
using SmartSolar.Modules.PreSurvey.GetMySurveyRequests;
using SmartSolar.Modules.PreSurvey.GetPendingSurveyRequests;
using SmartSolar.Modules.PreSurvey.GetSurveyRequestDetail;
using SmartSolar.Modules.PreSurvey.SubmitPreSurvey;
using SmartSolar.Modules.PreSurvey.UpdatePreSurvey;

namespace SmartSolar.Api.Extensions;

public static class PreSurveyModuleExtensions
{
    public static IServiceCollection AddPreSurveyModule(
        this IServiceCollection services)
    {
        services.AddScoped<CreateCustomerProfileHandler>();
        services.AddScoped<CreatePropertySiteHandler>();
        services.AddScoped<UpdatePreSurveyHandler>();
        services.AddScoped<ClaimSurveyRequestHandler>();
        services.AddScoped<CreatePreSurveyHandler>();
        services.AddScoped<SubmitPreSurveyHandler>();
        services.AddScoped<GetPendingSurveyRequestsHandler>();
        services.AddScoped<GetMySurveyRequestsHandler>();
        services.AddScoped<GetSurveyRequestDetailHandler>();
        return services;
    }
}