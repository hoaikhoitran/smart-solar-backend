using FluentValidation;
using SmartSolar.Modules.SolarSimulation.Constants;
using SmartSolar.Modules.SolarSimulation.Options;

namespace SmartSolar.Modules.SolarSimulation.CreateSimulation;

/// <summary>Input sanity bounds. These are configurable computational limits, not engineering limits.</summary>
public sealed class CreateSimulationCommandValidator : AbstractValidator<CreateSimulationCommand>
{
    public CreateSimulationCommandValidator(SolarSimulationOptions options)
    {
        var limits = options.Limits;

        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.PreSurveyId).NotEmpty();
        RuleFor(x => x.ProductId).NotEmpty();
        RuleFor(x => x.ExpectedGeometryVersion).NotNull().GreaterThanOrEqualTo(0);

        RuleFor(x => x.MountingType)
            .NotEmpty()
            .Must(MountingTypes.IsKnown)
            .WithMessage($"'Mounting Type' must be {MountingTypes.Flush} or {MountingTypes.Rack}.");

        When(x => x.MountingType == MountingTypes.Rack, () =>
        {
            RuleFor(x => x.PanelTiltDegree).NotNull().InclusiveBetween(0, 90);
            RuleFor(x => x.PanelAzimuthDegree).NotNull().InclusiveBetween(0, 360);
        });

        When(x => x.MountingType == MountingTypes.Flush, () =>
        {
            RuleFor(x => x.Installation.RackLowEdgeClearanceMm)
                .Null()
                .WithMessage("'rackLowEdgeClearanceMm' applies only to RACK mounting.")
                .OverridePropertyName("Installation.RackLowEdgeClearanceMm");
        });

        RuleFor(x => x.Installation).NotNull();

        void Spacing(string name, Func<CreateSimulationCommand, decimal?> selector, decimal max)
            => RuleFor(x => selector(x))
                .InclusiveBetween(0, max)
                .When(x => x.Installation is not null && selector(x).HasValue)
                .OverridePropertyName($"Installation.{name}");

        Spacing("PanelGapMm", x => x.Installation.PanelGapMm, limits.MaxSpacingMm);
        Spacing("RowGapMm", x => x.Installation.RowGapMm, limits.MaxSpacingMm);
        Spacing("EdgeSetbackMm", x => x.Installation.EdgeSetbackMm, limits.MaxSpacingMm);
        Spacing("ObstacleClearanceMm", x => x.Installation.ObstacleClearanceMm, limits.MaxSpacingMm);
        Spacing("RackLowEdgeClearanceMm", x => x.Installation.RackLowEdgeClearanceMm, limits.MaxRackClearanceMm);

        RuleFor(x => x.SystemLossPercent)
            .InclusiveBetween(limits.MinSystemLossPercent, limits.MaxSystemLossPercent)
            .When(x => x.SystemLossPercent.HasValue);
    }
}
