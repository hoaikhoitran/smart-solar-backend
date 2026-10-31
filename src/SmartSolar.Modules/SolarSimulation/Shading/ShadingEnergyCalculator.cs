using SmartSolar.Modules.SolarSimulation.Energy;

namespace SmartSolar.Modules.SolarSimulation.Shading;

/// <summary>
/// Panels that share one orientation and therefore one PVGIS hourly series (same tilt, azimuth and irradiance).
/// Panels with different orientations must be separate subarrays: they are never averaged together.
/// </summary>
public sealed record ShadingSubarray(PanelShadingCalculator Panels, IReadOnlyList<PvHourlyRecord> Hours);

/// <summary>Average over the historical years of one calendar month (UTC months, like PVGIS).</summary>
public sealed record ShadingMonth(int Month, double EnergyBeforeKwh, double EnergyLossKwh)
{
    public double EnergyAfterKwh => EnergyBeforeKwh - EnergyLossKwh;

    public double LossFraction => EnergyBeforeKwh > 0 ? EnergyLossKwh / EnergyBeforeKwh : 0;
}

/// <summary>
/// Result of the linear irradiance-based shading estimate. Annual and monthly energies are per-year sums averaged over the
/// <see cref="YearCount"/> historical years (never the sum of several years). Per-panel fractions are irradiance weighted.
/// </summary>
public sealed record ShadingEnergyResult(
    int YearCount,
    double AnnualEnergyBeforeKwh,
    double AnnualEnergyLossKwh,
    IReadOnlyList<ShadingMonth> Monthly,
    IReadOnlyDictionary<int, double> AnnualDirectShadedFractionByPanel,
    double MeanSunHeightDeviationDegree)
{
    public double AnnualEnergyAfterKwh => AnnualEnergyBeforeKwh - AnnualEnergyLossKwh;

    public double AnnualLossFraction => AnnualEnergyBeforeKwh > 0 ? AnnualEnergyLossKwh / AnnualEnergyBeforeKwh : 0;
}

/// <summary>
/// Linear irradiance-based shading estimate (SmartSolar simplification, not an electrical model):
/// <code>
/// G_POA                 = Gb + Gd + Gr                                  (W/m², PVGIS hourly plane-of-array components)
/// G_shaded              = (1 − S) × Gb + Gd + Gr                       (only the direct beam is blocked)
/// S_system(t)           = Σ(P_rated,i × shadedFraction_i(t)) / Σ P_rated,i
/// irradianceLoss(t)     = S_system(t) × Gb / (Gb + Gd + Gr)            (clamped to [0, 1]; 0 when Gb + Gd + Gr = 0)
/// E_base,hour  [kWh]    = P_hour [W] × 1 h / 1000
/// E_loss,hour  [kWh]    = Σ over 4 sub-samples of P_hour [W] × irradianceLoss(sub-sample) × 0.25 h / 1000
/// E_after,hour [kWh]    = E_base,hour − E_loss,hour
/// </code>
/// Each PVGIS hourly row is split into four 15-minute sub-samples at +7.5, +22.5, +37.5 and +52.5 minutes from the start
/// of its UTC hour; the sun position (and therefore the shadow) is computed for each sub-sample, while the hourly P, Gb, Gd
/// and Gr of PVGIS are used unchanged for all four (PVGIS provides hourly values, not 15-minute measurements). When the sun
/// is below the horizon or Gb = 0 the direct loss of that sub-sample is zero.
/// </summary>
public static class ShadingEnergyCalculator
{
    /// <summary>Sub-sample offsets from the start of the hour (minutes) and their duration (hours).</summary>
    public static readonly double[] SubSampleMinutes = [7.5, 22.5, 37.5, 52.5];
    public const double SubSampleHours = 0.25;

    public static ShadingEnergyResult Calculate(double latitudeDegree, double longitudeDegree, IReadOnlyList<ShadingSubarray> subarrays)
    {
        // Per (year, month) sums of the whole system.
        var baseByMonth = new Dictionary<(int Year, int Month), double>();
        var lossByMonth = new Dictionary<(int Year, int Month), double>();
        // Irradiance-weighted shaded fraction: Σ(fraction × Gb × Δt) / Σ(Gb × Δt) per panel.
        var shadedBeamByPanel = new Dictionary<int, double>();
        var beamByPanel = new Dictionary<int, double>();
        var sunCheckTotal = 0.0;
        var sunCheckCount = 0;

        foreach (var subarray in subarrays)
        {
            var panels = subarray.Panels.Panels;
            var totalRatedW = panels.Sum(p => p.RatedPowerW);
            foreach (var panel in panels)
            {
                shadedBeamByPanel.TryAdd(panel.Index, 0);
                beamByPanel.TryAdd(panel.Index, 0);
            }

            foreach (var hour in subarray.Hours)
            {
                var key = (hour.HourStartUtc.Year, hour.HourStartUtc.Month);
                var powerW = Math.Max(0, hour.PowerW);
                baseByMonth[key] = baseByMonth.GetValueOrDefault(key) + powerW * 1.0 / 1000;   // kWh in this hour
                lossByMonth.TryAdd(key, 0);

                // UTC consistency check: our sun height at the PVGIS timestamp versus PVGIS H_sun.
                if (hour.SunHeightDegree > 5)
                {
                    var atStamp = SunPositionCalculator.Calculate(latitudeDegree, longitudeDegree, hour.HourStartUtc.AddMinutes(hour.MinuteOfTimestamp));
                    sunCheckTotal += Math.Abs(atStamp.ElevationDegree - hour.SunHeightDegree);
                    sunCheckCount++;
                }

                var poa = hour.BeamW + hour.DiffuseW + hour.ReflectedW;
                if (hour.BeamW <= 0 || poa <= 0) continue;   // no direct irradiance: no direct shading loss
                var beamShare = hour.BeamW / poa;

                foreach (var minutes in SubSampleMinutes)
                {
                    var sun = SunPositionCalculator.Calculate(latitudeDegree, longitudeDegree, hour.HourStartUtc.AddMinutes(minutes));
                    if (!sun.IsAboveHorizon) continue;   // sun below the horizon: no direct irradiance

                    var fractions = subarray.Panels.FractionsFor(sun);
                    var systemFraction = 0.0;
                    for (var i = 0; i < panels.Count; i++)
                    {
                        systemFraction += panels[i].RatedPowerW * fractions[i];
                        shadedBeamByPanel[panels[i].Index] += fractions[i] * hour.BeamW * SubSampleHours;
                        beamByPanel[panels[i].Index] += hour.BeamW * SubSampleHours;
                    }
                    systemFraction = totalRatedW > 0 ? systemFraction / totalRatedW : 0;

                    var irradianceLoss = Math.Clamp(systemFraction * beamShare, 0, 1);
                    lossByMonth[key] += powerW * irradianceLoss * SubSampleHours / 1000;
                }
            }
        }

        var years = baseByMonth.Keys.Select(k => k.Year).Distinct().Count();
        var monthly = Enumerable.Range(1, 12).Select(month =>
        {
            var before = baseByMonth.Where(kv => kv.Key.Month == month).Sum(kv => kv.Value);
            var loss = lossByMonth.Where(kv => kv.Key.Month == month).Sum(kv => kv.Value);
            return years == 0 ? new ShadingMonth(month, 0, 0) : new ShadingMonth(month, before / years, Math.Min(loss, before) / years);
        }).ToList();

        var fractionsByPanel = beamByPanel.ToDictionary(kv => kv.Key, kv => kv.Value > 0 ? Math.Clamp(shadedBeamByPanel[kv.Key] / kv.Value, 0, 1) : 0);
        return new ShadingEnergyResult(years, monthly.Sum(m => m.EnergyBeforeKwh), monthly.Sum(m => m.EnergyLossKwh), monthly, fractionsByPanel,
            sunCheckCount == 0 ? 0 : sunCheckTotal / sunCheckCount);
    }
}
