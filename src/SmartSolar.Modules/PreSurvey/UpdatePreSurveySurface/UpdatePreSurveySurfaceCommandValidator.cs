using FluentValidation;
using SmartSolar.Modules.SolarSimulation.Options;

namespace SmartSolar.Modules.PreSurvey.UpdatePreSurveySurface;

public sealed class UpdatePreSurveySurfaceCommandValidator : AbstractValidator<UpdatePreSurveySurfaceCommand>
{
    public UpdatePreSurveySurfaceCommandValidator(SolarSimulationOptions options)
    {
        var limits = options.Limits;

        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.PreSurveyId).NotEmpty();

        RuleFor(x => x.ExpectedRevision).NotNull().GreaterThanOrEqualTo(0);

        RuleFor(x => x.SurfaceLengthM).NotNull().GreaterThan(0).LessThanOrEqualTo(limits.MaxSurfaceSideM);
        RuleFor(x => x.SurfaceWidthM).NotNull().GreaterThan(0).LessThanOrEqualTo(limits.MaxSurfaceSideM);

        RuleFor(x => x.SurfaceTiltDegree).NotNull().InclusiveBetween(0, 90);
        RuleFor(x => x.SurfaceAzimuthDegree).NotNull().InclusiveBetween(0, 360);

        RuleFor(x => x.Obstacles)
            .NotNull()
            .Must(list => list!.Count <= limits.MaxObstacles)
            .When(x => x.Obstacles is not null, ApplyConditionTo.CurrentValidator)
            .WithMessage($"At most {limits.MaxObstacles} obstacles are allowed.");

        // A null item would skip the child rules below; reject it explicitly.
        RuleForEach(x => x.Obstacles)
            .NotNull()
            .WithMessage("Obstacle must not be null.");

        RuleForEach(x => x.Obstacles).ChildRules(obstacle =>
        {
            obstacle.RuleFor(o => o.Name)
                .Must(name => !string.IsNullOrWhiteSpace(name))
                .WithMessage("'Name' must not be empty.")
                .MaximumLength(limits.MaxObstacleNameLength);

            obstacle.RuleFor(o => o.XM).NotNull().GreaterThanOrEqualTo(0);
            obstacle.RuleFor(o => o.YM).NotNull().GreaterThanOrEqualTo(0);
            obstacle.RuleFor(o => o.WidthM).NotNull().GreaterThan(0);
            obstacle.RuleFor(o => o.LengthM).NotNull().GreaterThan(0);
            obstacle.RuleFor(o => o.HeightM)
                .InclusiveBetween(0, limits.MaxObstacleHeightM)
                .When(o => o.HeightM.HasValue);
        });

        // Every obstacle must lie fully inside the surface; touching an edge is allowed.
        RuleFor(x => x).Custom((command, context) =>
        {
            if (command.Obstacles is null) return;
            for (var i = 0; i < command.Obstacles.Count; i++)
            {
                var o = command.Obstacles[i];
                if (o is null) continue;
                if (command.SurfaceWidthM is > 0 && o.XM is { } x && o.WidthM is { } w && x + w > command.SurfaceWidthM)
                {
                    context.AddFailure($"Obstacles[{i}].XM", "Obstacle extends beyond the surface width (xM + widthM > surfaceWidthM).");
                }
                if (command.SurfaceLengthM is > 0 && o.YM is { } y && o.LengthM is { } l && y + l > command.SurfaceLengthM)
                {
                    context.AddFailure($"Obstacles[{i}].YM", "Obstacle extends beyond the surface length (yM + lengthM > surfaceLengthM).");
                }
            }
        });
    }
}
