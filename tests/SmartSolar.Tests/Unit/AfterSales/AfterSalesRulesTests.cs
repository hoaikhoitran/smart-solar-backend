using SmartSolar.Modules.AfterSales;

namespace SmartSolar.Tests.Unit.AfterSales;

/// <summary>The pure calculations of Flow 3: calendar-month periods, disaster support caps and disaster eligibility.</summary>
public sealed class AfterSalesRulesTests
{
    private static readonly TimeZoneInfo Vietnam = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
    private static DateTimeOffset Local(int y, int m, int d) => new(y, m, d, 0, 0, 0, TimeSpan.FromHours(7));

    [Fact]
    public void Terms_and_maintenance_periods_follow_month_anniversaries_in_local_time()
    {
        Assert.Equal(Local(2027, 10, 20), AfterSalesRules.AddMonths(Local(2026, 10, 20), 12, Vietnam));
        Assert.Equal(Local(2027, 2, 28), AfterSalesRules.AddMonths(Local(2027, 1, 31), 1, Vietnam));   // clamped, not 30 days

        var (start, end) = AfterSalesRules.MaintenancePeriod(Local(2026, 10, 20), 6, Vietnam);
        Assert.Equal(Local(2027, 3, 20), start);
        Assert.Equal(Local(2027, 4, 20), end);
        Assert.Equal(TimeSpan.Zero, start.ToUniversalTime().Offset);
    }

    [Theory]
    [InlineData(10_000_000, 70, 9_000_000, 9_000_000, 7_000_000)]   // the percentage decides
    [InlineData(10_000_000, 70, 5_000_000, 9_000_000, 5_000_000)]   // the per-incident limit decides
    [InlineData(10_000_000, 70, 9_000_000, 2_500_000, 2_500_000)]   // the remaining term limit decides
    [InlineData(999_999, 33.3, 9_000_000, 9_000_000, 332_999)]       // rounded DOWN to whole VND (332,999.667)
    [InlineData(1_000_000, 50, 0, 9_000_000, 0)]                     // nothing left
    public void Disaster_support_is_the_minimum_of_share_incident_limit_and_term_limit(double eligible, double percent, double incident, double term, double expected)
        => Assert.Equal((decimal)expected, AfterSalesRules.DisasterSupport((decimal)eligible, (decimal)percent, (decimal)incident, (decimal)term));

    [Fact]
    public void A_disaster_is_covered_only_after_the_waiting_period_inside_the_term_and_for_covered_types()
    {
        var term = new ServiceSubscription
        {
            PackageName = "Storm shield", CoveredDisasterTypes = "STORM,FLOOD",
            StartsAt = Local(2026, 10, 1), CoverageStartsAt = Local(2026, 10, 8), EndsAt = Local(2027, 10, 1),
        };
        Assert.Null(AfterSalesRules.DisasterIneligibility(term, DisasterTypes.Storm, Local(2026, 10, 8), Local(2026, 10, 9)));
        Assert.Contains("waiting period", AfterSalesRules.DisasterIneligibility(term, DisasterTypes.Storm, Local(2026, 10, 7), Local(2026, 10, 9)));
        Assert.Contains("not covered", AfterSalesRules.DisasterIneligibility(term, DisasterTypes.Lightning, Local(2026, 11, 1), Local(2026, 11, 2)));
        Assert.Contains("after the protection term ended", AfterSalesRules.DisasterIneligibility(term, DisasterTypes.Flood, Local(2027, 10, 1), Local(2027, 10, 2)));
        // The incident date decides which term applies, but the claim must still be filed while that term runs.
        Assert.Contains("submitted after", AfterSalesRules.DisasterIneligibility(term, DisasterTypes.Flood, Local(2027, 9, 1), Local(2027, 10, 2)));
    }

    [Fact]
    public void Request_status_follows_its_items()
    {
        ServiceItem Item(string coverage, string status) => new() { Coverage = coverage, Status = status };
        Assert.Equal(ServiceRequestStatuses.AwaitingDecision, AfterSalesWorkflow.NextStatus([Item(ItemCoverages.Pending, ItemStatuses.Open)]));
        Assert.Equal(ServiceRequestStatuses.AwaitingQuotation, AfterSalesWorkflow.NextStatus([Item(ItemCoverages.Chargeable, ItemStatuses.Open)]));
        Assert.Equal(ServiceRequestStatuses.ReadyForWork,
            AfterSalesWorkflow.NextStatus([Item(ItemCoverages.InstallationWarranty, ItemStatuses.Authorized), Item(ItemCoverages.Chargeable, ItemStatuses.Declined)]));
        Assert.Equal(ServiceRequestStatuses.PendingCustomerAcceptance, AfterSalesWorkflow.NextStatus([Item(ItemCoverages.Chargeable, ItemStatuses.Done)]));
        Assert.Equal(ServiceRequestStatuses.Cancelled, AfterSalesWorkflow.NextStatus([Item(ItemCoverages.Chargeable, ItemStatuses.Declined)]));
    }
}
