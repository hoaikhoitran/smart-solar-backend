using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.PreSurvey.ListMine;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.PreSurvey;

/// <summary>GET /api/customers/me/sites and /pre-surveys: only the caller's own data, paged and sorted.</summary>
public sealed class CustomerListingHandlerTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>A customer with two sites; the first has a DRAFT (updated 1 h ago) and a SUBMITTED (updated 3 h ago) pre-survey.</summary>
    private static async Task<(Guid UserId, Guid DraftId, Guid SubmittedId)> SeedCustomerAsync(PreSurveyTestContext ctx, string name)
    {
        var user = await ctx.SeedUserAsync(name);
        var customer = await ctx.SeedCustomerAsync(user.Id);
        var roof = await ctx.SeedPropertySiteAsync(customer.Id, $"{name} roof");
        await ctx.SeedPropertySiteAsync(customer.Id, $"{name} garage");
        var draft = PreSurveyTestContext.NewPreSurvey(roof.Id, PreSurveyStatus.Draft);
        draft.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var submitted = PreSurveyTestContext.NewPreSurvey(roof.Id, PreSurveyStatus.Submitted);
        submitted.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-3);
        ctx.Db.AddRange(draft, submitted);
        await ctx.Db.SaveChangesAsync();
        return (user.Id, draft.Id, submitted.Id);
    }

    [Fact]
    public async Task A_customer_sees_only_their_own_sites_sorted_by_name()
    {
        // Arrange
        await using var ctx = new PreSurveyTestContext();
        var (me, _, _) = await SeedCustomerAsync(ctx, "Alice");
        await SeedCustomerAsync(ctx, "Bob");
        var handler = new CustomerListingHandler(ctx.UnitOfWork);

        // Act
        var page = await handler.ListSitesAsync(me, new CustomerListPage(1, 20), None);

        // Assert
        Assert.Equal(new[] { "Alice garage", "Alice roof" }, page.Items.Select(s => s.Name));
        Assert.Equal((2, 1), (page.TotalItems, page.TotalPages));
        Assert.Equal(new[] { 0, 2 }, page.Items.Select(s => s.PreSurveyCount));
        Assert.Equal("ROOFTOP", page.Items[0].InstallationSurfaceType);
    }

    [Fact]
    public async Task Pre_surveys_filter_by_status_and_sort_newest_first_by_default()
    {
        await using var ctx = new PreSurveyTestContext();
        var (me, draftId, submittedId) = await SeedCustomerAsync(ctx, "Alice");
        await SeedCustomerAsync(ctx, "Bob");
        var handler = new CustomerListingHandler(ctx.UnitOfWork);

        var drafts = await handler.ListPreSurveysAsync(me, "DRAFT", null, false, new CustomerListPage(1, 20), None);
        var all = await handler.ListPreSurveysAsync(me, null, null, false, new CustomerListPage(1, 20), None);
        var oldestFirst = await handler.ListPreSurveysAsync(me, null, "updatedAt", true, new CustomerListPage(1, 20), None);

        var draft = Assert.Single(drafts.Items);
        Assert.Equal((draftId, "DRAFT", "Alice roof"), (draft.PreSurveyId, draft.Status, draft.SiteName));
        Assert.Equal(new[] { draftId, submittedId }, all.Items.Select(p => p.PreSurveyId));
        Assert.Equal(new[] { submittedId, draftId }, oldestFirst.Items.Select(p => p.PreSurveyId));
        Assert.Equal("SUBMITTED", all.Items[1].Status);
    }

    [Fact]
    public async Task Pages_split_the_list_and_a_user_without_a_profile_gets_an_empty_page()
    {
        await using var ctx = new PreSurveyTestContext();
        var (me, _, _) = await SeedCustomerAsync(ctx, "Alice");
        var stranger = await ctx.SeedUserAsync("No profile");
        var handler = new CustomerListingHandler(ctx.UnitOfWork);

        var first = await handler.ListSitesAsync(me, new CustomerListPage(1, 1), None);
        var second = await handler.ListSitesAsync(me, new CustomerListPage(2, 1), None);
        var beyond = await handler.ListSitesAsync(me, new CustomerListPage(3, 1), None);
        var empty = await handler.ListPreSurveysAsync(stranger.Id, null, null, false, new CustomerListPage(1, 20), None);

        Assert.Equal(("Alice garage", 2, 2), (Assert.Single(first.Items).Name, first.TotalItems, first.TotalPages));
        Assert.Equal("Alice roof", Assert.Single(second.Items).Name);
        Assert.Empty(beyond.Items);
        Assert.Equal((0, 0), (empty.Items.Count, empty.TotalItems));
    }
}
