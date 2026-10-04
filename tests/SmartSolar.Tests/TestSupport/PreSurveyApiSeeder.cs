using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.PreSurvey.Entities;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Tests.Integration.Catalog;

namespace SmartSolar.Tests.TestSupport;

public sealed record SeededCustomer(
    UserAccount User,
    Customer Customer,
    PropertySite Site,
    HttpClient Client);

/// <summary>
/// Writes PreSurvey rows straight into an <see cref="AuthApiFactory"/> database.
/// Users are persisted (not just put in the token) because Customer and
/// SurveyRequest have foreign keys to user_account.
/// </summary>
internal static class PreSurveyApiSeeder
{
    public static async Task<T> SeedAsync<T>(this AuthApiFactory factory, T entity)
        where T : class
    {
        // The factory creates the schema while building the host, so start it first.
        _ = factory.Services;
        await using var db = factory.CreateDbContext();
        db.Add(entity);
        await db.SaveChangesAsync();
        return entity;
    }

    public static async Task<(UserAccount User, HttpClient Client)> SeedUserClientAsync(
        this AuthApiFactory factory,
        string role,
        string fullName = "Seeded User")
    {
        var user = await factory.SeedAsync(PreSurveyTestContext.NewUser(fullName));
        return (user, CatalogEndpointsTests.ClientFor(factory, role, user.Id));
    }

    public static async Task<SeededCustomer> SeedCustomerWithSiteAsync(
        this AuthApiFactory factory,
        string fullName = "Customer",
        InstallationSurfaceType? installationSurfaceType = InstallationSurfaceType.Rooftop)
    {
        var (user, client) = await factory.SeedUserClientAsync(RoleCodes.Customer, fullName);
        var customer = await factory.SeedAsync(PreSurveyTestContext.NewCustomer(user.Id));
        var site = await factory.SeedAsync(PreSurveyTestContext.NewPropertySite(
            customer.Id,
            installationSurfaceType: installationSurfaceType));
        return new SeededCustomer(user, customer, site, client);
    }

    public static async Task<Modules.PreSurvey.Entities.PreSurvey> ReloadPreSurveyAsync(
        this AuthApiFactory factory,
        Guid preSurveyId)
    {
        await using var db = factory.CreateDbContext();
        return await db.PreSurveys.AsNoTracking().SingleAsync(x => x.Id == preSurveyId);
    }

    public static async Task<List<SurveyRequest>> SurveyRequestsAsync(this AuthApiFactory factory)
    {
        await using var db = factory.CreateDbContext();
        return await db.SurveyRequests.AsNoTracking().ToListAsync();
    }
}
