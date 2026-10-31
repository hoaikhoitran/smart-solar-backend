using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SmartSolar.Api.Contracts;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Tests.Integration.Catalog;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Integration.Auth;

/// <summary>
/// The error envelope: readable details keyed as before, plus machine-readable fieldErrors (camelCase path + stable code),
/// and consistent statuses for authentication, authorization, unknown routes and wrong methods.
/// </summary>
public sealed class ApiErrorContractTests
{
    private static async Task<JsonElement> ErrorOf(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error");

    private static List<(string Field, string Code)> FieldErrors(JsonElement error)
        => error.GetProperty("fieldErrors").EnumerateArray().Select(e => (e.GetProperty("field").GetString()!, e.GetProperty("code").GetString()!)).ToList();

    [Fact]
    public async Task Validation_errors_keep_their_details_and_add_stable_field_codes()
    {
        // Arrange
        using var factory = new AuthApiFactory();
        var admin = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin);

        // Act: negative price, 2-letter currency, panel without rated power.
        var response = await admin.PostAsJsonAsync("/api/admin/products", new
        {
            sku = "BAD", productType = "SOLAR_PANEL", name = "Panel", brand = "Brand", unit = "PCS", unitPrice = -1, currency = "VN",
        });

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await ErrorOf(response);
        Assert.Equal(AuthErrorCodes.ValidationFailed, error.GetProperty("code").GetString());
        Assert.True(error.GetProperty("details").TryGetProperty("UnitPrice", out _));   // unchanged for existing clients
        var fields = FieldErrors(error);
        Assert.Contains(("unitPrice", ValidationErrorCodes.TooSmall), fields);
        Assert.Contains(("currency", ValidationErrorCodes.InvalidLength), fields);
        Assert.Contains(("ratedPowerW", ValidationErrorCodes.Required), fields);
        Assert.All(error.GetProperty("fieldErrors").EnumerateArray(), e => Assert.False(string.IsNullOrWhiteSpace(e.GetProperty("message").GetString())));
    }

    [Fact]
    public async Task Malformed_bodies_and_single_invalid_parameters_also_carry_field_codes()
    {
        using var factory = new AuthApiFactory();
        var admin = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin);

        var malformed = await admin.PostAsync("/api/admin/products", new StringContent("{\"unitPrice\": \"abc\"", Encoding.UTF8, "application/json"));
        var badStatus = await admin.GetAsync("/api/admin/products?status=DELETED");

        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.All(FieldErrors(await ErrorOf(malformed)), f => Assert.Equal(ValidationErrorCodes.InvalidFormat, f.Code));
        Assert.Equal(("status", ValidationErrorCodes.InvalidValue), Assert.Single(FieldErrors(await ErrorOf(badStatus))));
    }

    [Fact]
    public async Task Non_validation_errors_use_the_envelope_with_their_status_and_no_field_errors()
    {
        using var factory = new AuthApiFactory();
        var sales = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Sales);

        var unauthenticated = await factory.CreateClient().PostAsJsonAsync("/api/admin/products", new { });
        var forbidden = await sales.GetAsync("/api/admin/products");
        var notFound = await sales.GetAsync($"/api/products/{Guid.NewGuid()}");
        var wrongMethod = await sales.PutAsJsonAsync("/api/products", new { });

        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
        foreach (var response in new[] { unauthenticated, forbidden, notFound, wrongMethod })
        {
            var error = await ErrorOf(response);
            Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("code").GetString()));
            Assert.Equal(JsonValueKind.Null, error.GetProperty("fieldErrors").ValueKind);
        }
    }

    [Theory]
    [InlineData("UnitPrice", "unitPrice")]
    [InlineData("Images[0].Url", "images[0].url")]
    [InlineData("$.status", "status")]
    [InlineData("", "request")]
    public void Field_paths_are_camel_case_json_paths(string name, string expected)
        => Assert.Equal(expected, ValidationErrorCodes.FieldPath(name));
}
