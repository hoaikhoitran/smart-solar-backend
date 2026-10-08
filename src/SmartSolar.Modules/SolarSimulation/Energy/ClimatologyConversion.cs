namespace SmartSolar.Modules.SolarSimulation.Energy;

/// <summary>
/// Converts monthly means of daily values (e.g. mm/day, kWh/m²/day) into monthly totals.
/// The month length is the mean over the climatology period, so February counts its leap
/// days proportionally (2001–2020: 28.25 days).
/// </summary>
public static class ClimatologyConversion
{
    public static decimal MeanDaysInMonth(int month, int startYear, int endYear)
    {
        if (month is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(month));
        if (endYear < startYear) throw new ArgumentOutOfRangeException(nameof(endYear));

        var total = 0;
        for (var year = startYear; year <= endYear; year++)
        {
            total += DateTime.DaysInMonth(year, month);
        }

        return (decimal)total / (endYear - startYear + 1);
    }

    public static decimal? MonthlyTotal(decimal? dailyMean, decimal days)
        => dailyMean is { } value ? Math.Round(value * days, 2, MidpointRounding.AwayFromZero) : null;
}
