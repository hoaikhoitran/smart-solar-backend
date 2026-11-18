namespace SmartSolar.Api.Contracts.InstallationWorkflow;

/// <summary><paramref name="WarrantyMonths"/>: installation warranty chosen from the Admin options (omitted = the default option).</summary>
public sealed record ContractTermsRequest(int? Revision, string? Terms, int? WarrantyMonths = null);

/// <summary>canonicalHash must equal the contract's current canonicalHash (proves the PDF was rendered from this version).</summary>
public sealed record OfficialDocumentRequest(int Revision, Guid FileId, string? CanonicalHash);

/// <summary>decision: APPROVE or REJECT (reason required).</summary>
public sealed record ContractReviewRequest(int Revision, string? Decision, string? Reason);

/// <summary>officialDocumentSha256 identifies which published PDF the customer printed and signed.</summary>
public sealed record SignedDocumentRequest(Guid FileId, string? OfficialDocumentSha256);

/// <summary>
/// Staff upload of the scan of the physically signed paper contract: the VERIFIED <c>SIGNED_CONTRACT</c> PDF the caller registered, the SHA-256 of
/// the official PDF that was printed and signed, and optionally the date written on the paper (<c>yyyy-MM-dd</c>) and a note (≤ 300).
/// </summary>
public sealed record PaperSignedDocumentRequest(Guid FileId, string? OfficialDocumentSha256, DateOnly? SignedOn, string? Note);

/// <summary>Discard of an initial DRAFT contract: the current revision and a reason (1–1000 characters).</summary>
public sealed record DiscardContractRequest(int? Revision, string? Reason);
