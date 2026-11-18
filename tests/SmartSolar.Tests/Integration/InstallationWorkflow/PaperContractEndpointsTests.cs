using System.Net;
using System.Net.Http.Json;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Tests.Integration.Catalog;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.FeatureEndpointTestSupport;

namespace SmartSolar.Tests.Integration.InstallationWorkflow;

/// <summary>INSTALL-CONTRACT through the API pipeline: the staff paper-scan upload route admits Sales / Manager / Admin only.</summary>
public sealed class PaperContractEndpointsTests
{
    [Fact]
    public async Task INSTALL_CONTRACT_API_Only_sales_manager_and_admin_reach_the_paper_upload_and_unknown_contracts_are_404()
    {
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient());
        var id = Guid.NewGuid();
        var path = $"/api/contracts/{id}/signed-document";
        var body = new { fileId = id, officialDocumentSha256 = "x" };

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync(path, body)).StatusCode);
        foreach (var role in new[] { RoleCodes.Customer, RoleCodes.Technician })
            Assert.Equal(HttpStatusCode.Forbidden, (await CatalogEndpointsTests.ClientFor(factory, role).PostAsJsonAsync(path, body)).StatusCode);
        foreach (var role in new[] { RoleCodes.Sales, RoleCodes.Manager, RoleCodes.Admin })
            await AssertFailureAsync(await CatalogEndpointsTests.ClientFor(factory, role).PostAsJsonAsync(path, body), HttpStatusCode.NotFound, ContractErrorCodes.NotFound);
    }

    [Fact]
    public async Task INSTALL_CONTRACT_API_Sales_may_request_an_upload_url_for_a_signed_contract_scan_but_a_technician_may_not()
    {
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient());
        var request = new { purpose = "SIGNED_CONTRACT", fileName = "hop-dong.pdf", contentType = "application/pdf", sizeBytes = 1024 };

        var sales = await CatalogEndpointsTests.ClientFor(factory, RoleCodes.Sales).PostAsJsonAsync("/api/files/presigned-upload", request);
        var technician = await CatalogEndpointsTests.ClientFor(factory, RoleCodes.Technician).PostAsJsonAsync("/api/files/presigned-upload", request);

        Assert.NotEqual(HttpStatusCode.Forbidden, sales.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, technician.StatusCode);
    }
}
