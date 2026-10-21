using System.Globalization;
using System.Text;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Scheduling.Domain;

namespace SmartSolar.Modules.AfterSales;

/// <summary>Dashboard query: two local date ranges [from, to] (inclusive days). Omitted ranges default to this month vs the previous month.</summary>
public sealed record DashboardQuery(DateOnly? From, DateOnly? To, DateOnly? CompareFrom, DateOnly? CompareTo, Guid? OperationalZoneId, string? ServiceType, string? Status);

public sealed record DashboardMetric(string Key, string Description, decimal Current, decimal Previous, decimal? ChangePercent);

public sealed record DashboardView(DateOnly From, DateOnly To, DateOnly CompareFrom, DateOnly CompareTo, IReadOnlyList<DashboardMetric> Metrics);

/// <summary>
/// Admin-only cash-collection and activity dashboard (not an accounting system). Periods are local calendar days in the business time
/// zone; money is collected PayOS money by provider transaction time split per item category, and refunds by the date they were
/// actually returned, so net = collected − refunded.
/// </summary>
public sealed class AfterSalesDashboard(IAfterSalesReportQueries reports, WorkingCalendar calendar, TimeProvider time)
{
    public async Task<OperationResult<DashboardView>> GetAsync(DashboardQuery query, CancellationToken cancellationToken)
    {
        if (query.ServiceType is { } type && !ServiceRequestKinds.All.Contains(type))
            return OperationResult<DashboardView>.Fail(AfterSalesErrorCodes.ValidationFailed, $"Service type must be one of {string.Join(", ", ServiceRequestKinds.All)}.");
        if (query.Status is { } status && !ServiceRequestStatuses.All.Contains(status))
            return OperationResult<DashboardView>.Fail(AfterSalesErrorCodes.ValidationFailed, "Unknown request status.");

        var now = time.GetUtcNow();
        var today = calendar.LocalDateOf(now);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var from = query.From ?? monthStart;
        var to = query.To ?? monthStart.AddMonths(1).AddDays(-1);
        var compareFrom = query.CompareFrom ?? from.AddMonths(-1);
        var compareTo = query.CompareTo ?? (query.From is null && query.To is null ? monthStart.AddDays(-1) : compareFrom.AddDays(to.DayNumber - from.DayNumber));
        if (to < from || compareTo < compareFrom || to.DayNumber - from.DayNumber > 366 || compareTo.DayNumber - compareFrom.DayNumber > 366)
            return OperationResult<DashboardView>.Fail(AfterSalesErrorCodes.ValidationFailed, "Each range is at most 367 days and ends on or after its start.");

        var filter = new DashboardFilter(query.OperationalZoneId, query.ServiceType, query.Status);
        var current = await reports.GetNumbersAsync(filter, Start(from), Start(to.AddDays(1)), now, calendar.TimeZone, cancellationToken);
        var previous = await reports.GetNumbersAsync(filter, Start(compareFrom), Start(compareTo.AddDays(1)), now, calendar.TimeZone, cancellationToken);
        return OperationResult<DashboardView>.Ok(new DashboardView(from, to, compareFrom, compareTo, Metrics(current, previous)));
    }

    /// <summary>The same metrics as <see cref="GetAsync"/>, as CSV (one row per metric).</summary>
    public async Task<OperationResult<string>> ExportCsvAsync(DashboardQuery query, CancellationToken cancellationToken)
    {
        var view = await GetAsync(query, cancellationToken);
        if (!view.Succeeded) return view.As<string>();
        var csv = new StringBuilder();
        csv.AppendLine($"metric,description,current ({view.Value!.From:yyyy-MM-dd}..{view.Value.To:yyyy-MM-dd}),previous ({view.Value.CompareFrom:yyyy-MM-dd}..{view.Value.CompareTo:yyyy-MM-dd}),change_percent");
        foreach (var m in view.Value.Metrics)
            csv.AppendLine(string.Join(",", m.Key, Quote(m.Description), m.Current.ToString(CultureInfo.InvariantCulture), m.Previous.ToString(CultureInfo.InvariantCulture),
                m.ChangePercent?.ToString("0.##", CultureInfo.InvariantCulture) ?? ""));
        return OperationResult<string>.Ok(csv.ToString());
    }

    private DateTimeOffset Start(DateOnly localDate)
    {
        var local = localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, calendar.TimeZone.GetUtcOffset(local)).ToUniversalTime();
    }

    private static IReadOnlyList<DashboardMetric> Metrics(DashboardNumbers c, DashboardNumbers p)
    {
        DashboardMetric M(string key, string description, decimal current, decimal previous)
            => new(key, description, current, previous, previous == 0 ? null : Math.Round((current - previous) / previous * 100m, 2));
        return
        [
            M("total_customers", "Customer accounts at the end of the period", c.TotalCustomers, p.TotalCustomers),
            M("new_customers", "Customer accounts created in the period", c.NewCustomers, p.NewCustomers),
            M("installation_projects", "Installation projects created in the period", c.InstallationProjects, p.InstallationProjects),
            M("completed_installations", "Installations handed over in the period", c.CompletedInstallations, p.CompletedInstallations),
            M("active_installation_warranties", "Handed-over installations whose warranty covers the end of the period", c.ActiveInstallationWarranties, p.ActiveInstallationWarranties),
            M("fault_requests", "Fault requests reported in the period", c.ReportedFaultRequests, p.ReportedFaultRequests),
            M("completed_repairs", "Fault requests accepted by the customer in the period", c.CompletedRepairs, p.CompletedRepairs),
            M("completed_maintenance_visits", "Maintenance visits accepted by the customer in the period", c.CompletedMaintenanceVisits, p.CompletedMaintenanceVisits),
            M("active_maintenance_subscriptions", "Maintenance terms covering the end of the period", c.ActiveMaintenanceSubscriptions, p.ActiveMaintenanceSubscriptions),
            M("active_disaster_subscriptions", "Disaster-protection terms covering the end of the period", c.ActiveDisasterSubscriptions, p.ActiveDisasterSubscriptions),
            M("package_purchases", "Packages whose payment was verified in the period", c.PackagePurchases, p.PackagePurchases),
            M("active_technicians", "Technicians with at least one after-sales or installation visit in the period", c.ActiveTechnicians, p.ActiveTechnicians),
            M("technician_service_visits", "After-sales visits reported in the period", c.TechnicianServiceVisits, p.TechnicianServiceVisits),
            M("collected_contracts", "Installation contract money collected (PayOS transaction time)", c.CollectedContracts, p.CollectedContracts),
            M("collected_packages", "Service package money collected", c.CollectedPackages, p.CollectedPackages),
            M("collected_services", "Repair and one-time maintenance money collected", c.CollectedServices, p.CollectedServices),
            M("collected_total", "Gross money collected", c.CollectedTotal, p.CollectedTotal),
            M("refunded", "Manual refunds actually returned in the period", c.Refunded, p.Refunded),
            M("net_collected", "Gross collected minus refunded", c.NetCollected, p.NetCollected),
        ];
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
