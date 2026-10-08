using SmartSolar.Modules.PreSurvey.Contracts.Persistence;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.PreSurvey.Surface;

namespace SmartSolar.Modules.PreSurvey.UpdatePreSurveySurface;

public sealed class UpdatePreSurveySurfaceHandler
{
    private readonly IPreSurveyUnitOfWork _unitOfWork;

    public UpdatePreSurveySurfaceHandler(IPreSurveyUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    /// <summary>Assumes the command passed <see cref="UpdatePreSurveySurfaceCommandValidator"/>.</summary>
    public async Task<UpdatePreSurveySurfaceResult> HandleAsync(
        UpdatePreSurveySurfaceCommand command,
        CancellationToken cancellationToken)
    {
        var customer = await _unitOfWork.FindCustomerByUserIdAsync(command.UserId, cancellationToken);
        if (customer is null)
        {
            return UpdatePreSurveySurfaceResult.Failed(UpdatePreSurveySurfaceOutcome.CustomerNotFound);
        }

        var preSurvey = await _unitOfWork.FindPreSurveyByIdAsync(command.PreSurveyId, cancellationToken);
        if (preSurvey is null)
        {
            return UpdatePreSurveySurfaceResult.Failed(UpdatePreSurveySurfaceOutcome.PreSurveyNotFound);
        }

        var property = await _unitOfWork.FindPropertyByIdAsync(preSurvey.PropertyId, cancellationToken);
        if (property is null || property.CustomerId != customer.Id)
        {
            return UpdatePreSurveySurfaceResult.Failed(UpdatePreSurveySurfaceOutcome.NotOwned);
        }

        if (preSurvey.Status != PreSurveyStatus.Draft)
        {
            return UpdatePreSurveySurfaceResult.Failed(UpdatePreSurveySurfaceOutcome.NotEditable);
        }

        if (preSurvey.Revision != command.ExpectedRevision)
        {
            return UpdatePreSurveySurfaceResult.Failed(UpdatePreSurveySurfaceOutcome.ConcurrentlyModified);
        }

        var obstacles = command.Obstacles!
            .Select(o => new SurfaceObstacle(o.Name!.Trim(), o.XM!.Value, o.YM!.Value, o.WidthM!.Value, o.LengthM!.Value, o.HeightM))
            .ToList();
        var obstaclesJson = SurfaceObstacleSerializer.Serialize(obstacles);

        var geometryChanged =
            preSurvey.SurfaceLengthM != command.SurfaceLengthM ||
            preSurvey.SurfaceWidthM != command.SurfaceWidthM ||
            preSurvey.TiltDegree != command.SurfaceTiltDegree ||
            preSurvey.AzimuthDegree != command.SurfaceAzimuthDegree ||
            !SurfaceObstacleSerializer.AreEquivalent(preSurvey.Obstacles, obstacles);

        preSurvey.SurfaceLengthM = command.SurfaceLengthM;
        preSurvey.SurfaceWidthM = command.SurfaceWidthM;
        preSurvey.TiltDegree = command.SurfaceTiltDegree;
        preSurvey.AzimuthDegree = command.SurfaceAzimuthDegree;
        preSurvey.Obstacles = obstaclesJson;
        preSurvey.UpdatedAt = DateTimeOffset.UtcNow;
        preSurvey.Revision++;

        if (geometryChanged)
        {
            preSurvey.GeometryVersion++;
        }

        if (!await _unitOfWork.TrySaveDraftChangesAsync(cancellationToken))
        {
            var status = await _unitOfWork.GetPreSurveyStatusAsync(command.PreSurveyId, cancellationToken);
            return UpdatePreSurveySurfaceResult.Failed(status == PreSurveyStatus.Draft
                ? UpdatePreSurveySurfaceOutcome.ConcurrentlyModified
                : UpdatePreSurveySurfaceOutcome.NotEditable);
        }

        return UpdatePreSurveySurfaceResult.Updated(preSurvey.Revision, preSurvey.GeometryVersion);
    }
}
