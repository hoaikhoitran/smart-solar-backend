namespace SmartSolar.Api.Contracts.InstallationWorkflow;

public sealed record ObstacleRequest(string? Kind, string? Description, decimal? HeightM, decimal? AreaM2);

public sealed record OnSiteSurveyRequest(
    int? Revision, string? RoofType, string? RoofMaterial, decimal? RoofLengthM, decimal? RoofWidthM, decimal? UsableAreaM2,
    decimal? TiltDegree, decimal? AzimuthDegree, string? RoofCondition, string? ElectricalCabinetNotes, string? ElectricalConnectionNotes,
    string? InstallationFeasibility, string? ProposedInverterLocation, string? ProposedPanelLayoutNotes, decimal? MeasuredLatitude,
    decimal? MeasuredLongitude, string? Notes, IReadOnlyList<ObstacleRequest>? Obstacles, BillOfMaterialsRequest? BillOfMaterials = null);

/// <summary>A catalog product and its quantity in the product's unit. There is deliberately no price field.</summary>
public sealed record BillOfMaterialsItemRequest(Guid ProductId, decimal Quantity);

/// <summary>Technician-confirmed equipment and priced service items (labor, transport, other services) for the automatic quotation.</summary>
public sealed record BillOfMaterialsRequest(IReadOnlyList<BillOfMaterialsItemRequest>? Materials, IReadOnlyList<BillOfMaterialsItemRequest>? ServiceItems, string? InstallationNotes);

public sealed record OnSiteSurveyAttachmentRequest(Guid FileId, string? Category, string? Caption);

public sealed record RevisionRequest(int Revision);

public sealed record OnSiteSurveyAmendmentRequest(int Revision, string? Reason, OnSiteSurveyRequest? Changes, IReadOnlyList<Guid>? AdditionalFileIds);
