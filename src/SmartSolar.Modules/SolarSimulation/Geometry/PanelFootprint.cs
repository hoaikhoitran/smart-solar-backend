namespace SmartSolar.Modules.SolarSimulation.Geometry;

/// <summary>
/// The orthogonal projection, along the surface normal, of one panel's full solid
/// (front face plus thickness) onto the surface, expressed in the row frame.
/// <para>
/// Row frame: the surface frame rotated by <see cref="RowRotationDegree"/> so that rows run
/// along S (the panel's horizontal edge projected onto the surface) and stack along T.
/// </para>
/// Because every panel is the same solid translated within the surface plane, two panels
/// whose footprints do not overlap cannot intersect in 3D, and a panel whose footprint
/// avoids an obstacle's footprint cannot intersect that obstacle extruded along the normal.
/// </summary>
public sealed record PanelFootprint(
    double PhysicalWidthM,
    double PhysicalLengthM,
    double ThicknessM,
    double RowRotationDegree,
    double MinS,
    double MinT,
    double MaxS,
    double MaxT,
    double FrontCenterHeightM,
    double LowEdgeClearanceM,
    double HighEdgeHeightM,
    bool IsConservativeBoundingBox,
    IReadOnlyList<Vec3> SolidCorners,
    PanelOrientation Orientation)
{
    public double WidthAlongRowM => MaxS - MinS;

    public double DepthAcrossRowM => MaxT - MinT;
}
