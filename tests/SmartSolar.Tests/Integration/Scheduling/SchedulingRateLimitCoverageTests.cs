using System.Reflection;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.RateLimiting;
using SmartSolar.Api.Controllers;
using SmartSolar.Api.Extensions;

namespace SmartSolar.Tests.Integration.Scheduling;

/// <summary>
/// Every admin scheduling endpoint (service areas, coverage, area technicians, technician configuration, Sales owner, Manager
/// override, uncovered-zone dispatch for staff and customers) and every Installation Flow endpoint carries a named per-user rate-limit policy: reads the scheduling read policy,
/// writes the scheduling write policy (installation endpoints their own installation policies).
/// </summary>
public sealed class SchedulingRateLimitCoverageTests
{
    private static IEnumerable<(MethodInfo Action, HttpMethodAttribute Verb)> Actions(Type controller)
        => controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(m => m.GetCustomAttributes<HttpMethodAttribute>().Select(v => (m, v)));

    private static string? PolicyOf(MethodInfo action)
        => (action.GetCustomAttribute<EnableRateLimitingAttribute>() ?? action.DeclaringType!.GetCustomAttribute<EnableRateLimitingAttribute>())?.PolicyName;

    public static TheoryData<Type> AdminSchedulingControllers => new()
    {
        typeof(AdminOperationalZonesController),
        typeof(AdminSurveyAppointmentsController),
        typeof(AdminSurveyRequestsController),
        typeof(AdminTechniciansController),
        typeof(CustomerSiteLocationController),
        typeof(AdminDispatchRequestsController),
        typeof(CustomerDispatchRequestsController),
    };

    [Theory]
    [MemberData(nameof(AdminSchedulingControllers))]
    public void Admin_scheduling_reads_and_writes_use_the_scheduling_policies(Type controller)
    {
        var actions = Actions(controller).ToList();
        Assert.NotEmpty(actions);
        Assert.All(actions, a =>
        {
            var expected = a.Verb.HttpMethods.Single() == "GET" ? RateLimitingExtensions.SchedulingReadPolicy : RateLimitingExtensions.SchedulingWritePolicy;
            Assert.True(PolicyOf(a.Action) == expected, $"{controller.Name}.{a.Action.Name} ({a.Verb.HttpMethods.Single()}) should use '{expected}' but uses '{PolicyOf(a.Action)}'.");
        });
    }

    [Theory]
    [InlineData(typeof(ManagerInstallationsController))]
    [InlineData(typeof(TechnicianInstallationsController))]
    [InlineData(typeof(CustomerInstallationsController))]
    public void Every_installation_endpoint_has_an_installation_policy(Type controller)
    {
        var policies = RateLimitingExtensions.InstallationWorkflowPolicies.Select(p => p.Name).ToHashSet();
        Assert.All(Actions(controller), a => Assert.True(PolicyOf(a.Action) is { } policy && policies.Contains(policy),
            $"{controller.Name}.{a.Action.Name} has no installation rate-limit policy."));
    }
}
