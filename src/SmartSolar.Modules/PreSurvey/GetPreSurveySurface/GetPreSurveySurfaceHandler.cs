using SmartSolar.Modules.PreSurvey.Contracts.Persistence;
using SmartSolar.Modules.PreSurvey.Surface;

namespace SmartSolar.Modules.PreSurvey.GetPreSurveySurface;

/// <summary>Coordinate and unit conventions shared by the 2D editor and the 3D view.</summary>
public sealed record SurfaceConventions(
    string Units,
    string Origin,
    string XAxis,
    string YAxis,
    string Azimuth,
    string ObstacleHeight)
{
    public static SurfaceConventions V1 { get; } = new(
        Units: "meters on the surface plane; installation spacing in millimetres",
        Origin: "upper-left corner of the surface in the 2D editor (high edge of an inclined surface)",
        XAxis: "along surface width",
        YAxis: "along surface length, pointing down-slope toward the surface azimuth",
        Azimuth: "compass degrees: 0 = North, 90 = East, 180 = South, 270 = West, clockwise",
        ObstacleHeight: "meters along the surface normal; used for 3D rendering only");
}

/// <summary>Customer-declared values from the existing pre-survey form. Never derived from geometry.</summary>
public sealed record DeclaredSurveyValues(
    decimal? TotalAreaM2,
    decimal? UsableAreaM2,
    bool? HasObstruction,
    string Note);

public sealed record PreSurveySurfaceView(
    Guid PreSurveyId,
    string Status,
    int Revision,
    int GeometryVersion,
    bool SurfaceDefined,
    decimal? SurfaceLengthM,
    decimal? SurfaceWidthM,
    decimal? SurfaceTiltDegree,
    decimal? SurfaceAzimuthDegree,
    IReadOnlyList<SurfaceObstacle> Obstacles,
    DeclaredSurveyValues Declared,
    decimal? Latitude,
    decimal? Longitude,
    Guid? SelectedSimulationId,
    SurfaceConventions Conventions);

public enum GetPreSurveySurfaceOutcome
{
    Found,
    CustomerNotFound,
    PreSurveyNotFound,
    NotOwned
}

public sealed record GetPreSurveySurfaceResult(GetPreSurveySurfaceOutcome Outcome, PreSurveySurfaceView? Surface);

public sealed class GetPreSurveySurfaceHandler
{
    private const string DeclaredNote =
        "Customer-declared estimates from the pre-survey form. Computed geometric areas are returned with each simulation.";

    private readonly IPreSurveyUnitOfWork _unitOfWork;

    public GetPreSurveySurfaceHandler(IPreSurveyUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<GetPreSurveySurfaceResult> HandleAsync(Guid userId, Guid preSurveyId, CancellationToken cancellationToken)
    {
        var customer = await _unitOfWork.FindCustomerByUserIdAsync(userId, cancellationToken);
        if (customer is null)
        {
            return new(GetPreSurveySurfaceOutcome.CustomerNotFound, null);
        }

        var preSurvey = await _unitOfWork.FindPreSurveyByIdAsync(preSurveyId, cancellationToken);
        if (preSurvey is null)
        {
            return new(GetPreSurveySurfaceOutcome.PreSurveyNotFound, null);
        }

        var property = await _unitOfWork.FindPropertyByIdAsync(preSurvey.PropertyId, cancellationToken);
        if (property is null || property.CustomerId != customer.Id)
        {
            return new(GetPreSurveySurfaceOutcome.NotOwned, null);
        }

        var obstacles = SurfaceObstacleSerializer.Deserialize(preSurvey.Obstacles);

        return new(GetPreSurveySurfaceOutcome.Found, new PreSurveySurfaceView(
            preSurvey.Id,
            preSurvey.Status.ToString().ToUpperInvariant(),
            preSurvey.Revision,
            preSurvey.GeometryVersion,
            preSurvey.SurfaceLengthM.HasValue && preSurvey.SurfaceWidthM.HasValue && obstacles is not null,
            preSurvey.SurfaceLengthM,
            preSurvey.SurfaceWidthM,
            preSurvey.TiltDegree,
            preSurvey.AzimuthDegree,
            obstacles ?? [],
            new DeclaredSurveyValues(preSurvey.TotalAreaM2, preSurvey.UsableAreaM2, preSurvey.HasObstruction, DeclaredNote),
            property.Latitude,
            property.Longitude,
            preSurvey.SelectedSimulationId,
            SurfaceConventions.V1));
    }
}
