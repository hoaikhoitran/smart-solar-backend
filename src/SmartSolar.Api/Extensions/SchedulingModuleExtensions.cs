using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.Options;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;
using SmartSolar.Modules.Scheduling.Services;

namespace SmartSolar.Api.Extensions;

public static class SchedulingModuleExtensions
{
    /// <summary>
    /// Shared scheduling core (calendar, feasibility, reservations) plus the Flow 1 pre-survey
    /// appointment handlers. "Scheduling" options are validated at startup, including the time zone.
    /// </summary>
    public static IServiceCollection AddSchedulingModule(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new SchedulingOptions();
        configuration.GetSection(SchedulingOptions.SectionName).Bind(options);
        var calendar = Validate(options);

        services.AddSingleton(options);
        services.AddSingleton(calendar);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<TechnicianAvailabilityEvaluator>();
        services.AddScoped<AppointmentReservationService>();
        services.AddScoped<TechnicianDayLoader>();
        services.AddScoped<ScheduleRecalculationService>();
        services.AddScoped<SalesRescheduleHandler>();
        if (options.RecalculationWorkerEnabled)
        {
            services.AddHostedService<Background.ScheduleRecalculationWorker>();
        }

        services.AddScoped<GetSurveySlotsHandler>();
        services.AddScoped<BookSurveyAppointmentHandler>();
        services.AddScoped<RescheduleSurveyAppointmentHandler>();
        services.AddScoped<CancelSurveyAppointmentHandler>();
        services.AddScoped<CustomerSurveyRequestQueries>();
        services.AddScoped<TechnicianAvailabilityHandler>();
        services.AddScoped<AssignTechnicianHandler>();
        services.AddScoped<TechnicianJobHandlers>();
        services.AddScoped<TechnicianScheduleHandler>();
        services.AddScoped<AdminTechnicianHandlers>();
        return services;
    }

    private static WorkingCalendar Validate(SchedulingOptions o)
    {
        var errors = new List<string>();
        if (o.AppointmentDurationMinutes is < 15 or > 600) errors.Add("Scheduling:AppointmentDurationMinutes must be between 15 and 600.");
        if (o.SlotIntervalMinutes is < 5 or > 240) errors.Add("Scheduling:SlotIntervalMinutes must be between 5 and 240.");
        if (o.MaxAppointmentsPerTechnicianPerDay is < 1 or > 20) errors.Add("Scheduling:MaxAppointmentsPerTechnicianPerDay must be between 1 and 20.");
        if (o.SafetyBufferMinutes is < 0 or > 240) errors.Add("Scheduling:SafetyBufferMinutes must be between 0 and 240.");
        if (o.BookingHorizonDays is < 1 or > 365) errors.Add("Scheduling:BookingHorizonDays must be between 1 and 365.");
        if (o.MinimumBookingLeadMinutes is < 0 or > 10080) errors.Add("Scheduling:MinimumBookingLeadMinutes must be between 0 and 10080.");
        if (o.MaxReportedSlotCapacity is < 1 or > 50) errors.Add("Scheduling:MaxReportedSlotCapacity must be between 1 and 50.");
        if (o.MaxRouteRequestsPerOperation is < 1 or > 1000) errors.Add("Scheduling:MaxRouteRequestsPerOperation must be between 1 and 1000.");
        if (o.MaxTechniciansEvaluatedPerSlot is < 1 or > 100) errors.Add("Scheduling:MaxTechniciansEvaluatedPerSlot must be between 1 and 100.");
        if (o.MaxReservationAttempts is < 1 or > 10) errors.Add("Scheduling:MaxReservationAttempts must be between 1 and 10.");
        if (o.RecalculationPollSeconds is < 5 or > 3600) errors.Add("Scheduling:RecalculationPollSeconds must be between 5 and 3600.");
        if (o.RecalculationLeaseSeconds is < 10 or > 3600) errors.Add("Scheduling:RecalculationLeaseSeconds must be between 10 and 3600.");
        if (o.RecalculationMaxBackoffMinutes is < 1 or > 1440) errors.Add("Scheduling:RecalculationMaxBackoffMinutes must be between 1 and 1440.");

        WorkingCalendar? calendar = null;
        try
        {
            calendar = new WorkingCalendar(o);
            if (calendar.CandidateWindows(new DateOnly(2026, 1, 5), calendar.Periods).Count == 0)
                errors.Add("Scheduling:WorkingHours leave no room for an appointment of the configured duration.");
        }
        catch (Exception ex) when (ex is FormatException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            errors.Add($"Scheduling configuration is invalid: {ex.Message}");
        }

        if (errors.Count > 0 || calendar is null) throw new InvalidOperationException(string.Join(" ", errors));
        return calendar;
    }
}
