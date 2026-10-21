namespace SmartSolar.Api.Contracts.InstallationWorkflow;

/// <summary><paramref name="WarrantyMonths"/>: installation warranty chosen from the Admin options (omitted = the default option).</summary>
public sealed record ContractTermsRequest(int? Revision, string? Terms, int? WarrantyMonths = null);

/// <summary>canonicalHash must equal the contract's current canonicalHash (proves the PDF was rendered from this version).</summary>
public sealed record OfficialDocumentRequest(int Revision, Guid FileId, string? CanonicalHash);

/// <summary>decision: APPROVE or REJECT (reason required).</summary>
public sealed record ContractReviewRequest(int Revision, string? Decision, string? Reason);

/// <summary>officialDocumentSha256 identifies which published PDF the customer printed and signed.</summary>
public sealed record SignedDocumentRequest(Guid FileId, string? OfficialDocumentSha256);
