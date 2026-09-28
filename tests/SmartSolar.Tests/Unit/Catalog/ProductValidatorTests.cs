using System.Globalization;
using SmartSolar.Modules.Catalog.ListProducts;
using SmartSolar.Modules.Catalog.ManageProducts;

namespace SmartSolar.Tests.Unit.Catalog;

public class ProductValidatorTests
{
    private static CreateProductCommand ValidPanel() => new(
        Sku: "PNL-550",
        ProductType: "SOLAR_PANEL",
        Category: "PANEL",
        Name: "Mono 550",
        Brand: "Jinko",
        Model: "Tiger Neo",
        Unit: "PCS",
        UnitPrice: 2_500_000m,
        Currency: "VND",
        RatedPowerW: 550m,
        WidthMm: 1134m,
        HeightMm: 2278m,
        WarrantyMonth: 144,
        Spec: "{\"cells\":144}",
        ImageUrl: null,
        Status: null);

    private static ListProductsQuery ValidQuery() => new(
        null, null, null, null, null, null, null, null,
        Page: 1, PageSize: 20, SortBy: null, SortDirection: null);

    private static IReadOnlyCollection<string> ErrorsFor(CreateProductCommand command)
        => new CreateProductCommandValidator().Validate(command).Errors.Select(e => e.PropertyName).ToHashSet();

    private static IReadOnlyCollection<string> ErrorsFor(ListProductsQuery query)
        => new ListProductsQueryValidator().Validate(query).Errors.Select(e => e.PropertyName).ToHashSet();

    [Fact]
    public void A_complete_solar_panel_is_valid()
    {
        Assert.Empty(ErrorsFor(ValidPanel()));
    }

    [Fact]
    public void Required_fields_are_reported()
    {
        var command = ValidPanel() with
        {
            Sku = "",
            ProductType = "",
            Name = " ",
            Brand = "",
            Unit = "",
            UnitPrice = null,
            Currency = ""
        };

        var errors = ErrorsFor(command);

        Assert.Contains("Sku", errors);
        Assert.Contains("ProductType", errors);
        Assert.Contains("Name", errors);
        Assert.Contains("Brand", errors);
        Assert.Contains("Unit", errors);
        Assert.Contains("UnitPrice", errors);
        Assert.Contains("Currency", errors);
    }

    [Fact]
    public void Maximum_lengths_follow_the_schema()
    {
        var command = ValidPanel() with
        {
            Sku = new string('S', 51),
            Category = new string('C', 51),
            Name = new string('N', 256),
            Brand = new string('B', 101),
            Model = new string('M', 101),
            Unit = new string('U', 21)
        };

        var errors = ErrorsFor(command);

        Assert.Equal(
            new[] { "Brand", "Category", "Model", "Name", "Sku", "Unit" },
            errors.OrderBy(e => e).ToArray());
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("0.001")]
    [InlineData("12345678901234")]
    public void Invalid_unit_prices_are_rejected(string price)
    {
        var errors = ErrorsFor(ValidPanel() with { UnitPrice = decimal.Parse(price, CultureInfo.InvariantCulture) });

        Assert.Contains("UnitPrice", errors);
    }

    [Fact]
    public void A_free_product_is_allowed()
    {
        Assert.Empty(ErrorsFor(ValidPanel() with { UnitPrice = 0m }));
    }

    [Theory]
    [InlineData("VN")]
    [InlineData("VNDD")]
    public void Currency_must_be_exactly_three_characters(string currency)
    {
        Assert.Contains("Currency", ErrorsFor(ValidPanel() with { Currency = currency }));
    }

    [Fact]
    public void Negative_warranty_is_rejected()
    {
        Assert.Contains("WarrantyMonth", ErrorsFor(ValidPanel() with { WarrantyMonth = -1 }));
    }

    [Fact]
    public void Solar_panels_require_power_width_and_height()
    {
        var errors = ErrorsFor(ValidPanel() with { RatedPowerW = null, WidthMm = null, HeightMm = null });

        Assert.Contains("RatedPowerW", errors);
        Assert.Contains("WidthMm", errors);
        Assert.Contains("HeightMm", errors);
    }

    [Fact]
    public void Solar_panel_power_and_dimensions_must_be_positive()
    {
        var errors = ErrorsFor(ValidPanel() with { RatedPowerW = 0m, WidthMm = -1m, HeightMm = 0m });

        Assert.Contains("RatedPowerW", errors);
        Assert.Contains("WidthMm", errors);
        Assert.Contains("HeightMm", errors);
    }

    [Fact]
    public void Solar_panel_rules_apply_regardless_of_type_casing()
    {
        Assert.Contains("RatedPowerW", ErrorsFor(ValidPanel() with { ProductType = " solar_panel ", RatedPowerW = null }));
    }

    [Fact]
    public void Other_product_types_may_omit_power_and_dimensions()
    {
        var inverter = ValidPanel() with
        {
            ProductType = "INVERTER",
            RatedPowerW = null,
            WidthMm = null,
            HeightMm = null
        };

        Assert.Empty(ErrorsFor(inverter));
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("{not json")]
    public void Spec_must_be_a_json_object(string spec)
    {
        Assert.Contains("Spec", ErrorsFor(ValidPanel() with { Spec = spec }));
    }

    [Theory]
    [InlineData("ACTIVE", true)]
    [InlineData("inactive", true)]
    [InlineData("DELETED", false)]
    public void Create_accepts_only_known_statuses(string status, bool valid)
    {
        Assert.Equal(valid, !ErrorsFor(ValidPanel() with { Status = status }).Contains("Status"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ARCHIVED")]
    public void Status_change_rejects_unknown_statuses(string status)
    {
        var result = new ChangeProductStatusCommandValidator().Validate(
            new ChangeProductStatusCommand(Guid.NewGuid(), status));

        Assert.Contains(result.Errors, e => e.PropertyName == "Status");
    }

    [Fact]
    public void Update_uses_the_same_field_rules()
    {
        var command = new UpdateProductCommand(
            Guid.NewGuid(), "", "SOLAR_PANEL", null, "Name", "Brand", null, "PCS",
            -1m, "VND", null, 1m, 1m, null, null, null);

        var errors = new UpdateProductCommandValidator().Validate(command).Errors.Select(e => e.PropertyName).ToHashSet();

        Assert.Contains("Sku", errors);
        Assert.Contains("UnitPrice", errors);
        Assert.Contains("RatedPowerW", errors);
    }

    [Fact]
    public void Default_list_query_is_valid()
    {
        Assert.Empty(ErrorsFor(ValidQuery()));
    }

    [Theory]
    [InlineData(0, 20, "Page")]
    [InlineData(-1, 20, "Page")]
    [InlineData(1, 0, "PageSize")]
    [InlineData(1, 101, "PageSize")]
    public void Invalid_paging_is_rejected(int page, int pageSize, string expectedProperty)
    {
        Assert.Contains(expectedProperty, ErrorsFor(ValidQuery() with { Page = page, PageSize = pageSize }));
    }

    [Fact]
    public void Page_size_of_one_hundred_is_allowed()
    {
        Assert.Empty(ErrorsFor(ValidQuery() with { PageSize = 100 }));
    }

    [Theory]
    [InlineData("price")]
    [InlineData("sku")]
    [InlineData("name; DROP TABLE product")]
    public void Sort_fields_outside_the_whitelist_are_rejected(string sortBy)
    {
        Assert.Contains("SortBy", ErrorsFor(ValidQuery() with { SortBy = sortBy }));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("brand")]
    [InlineData("ratedPowerW")]
    [InlineData("unitPrice")]
    [InlineData("createdAt")]
    [InlineData("UNITPRICE")]
    public void Whitelisted_sort_fields_are_accepted(string sortBy)
    {
        Assert.Empty(ErrorsFor(ValidQuery() with { SortBy = sortBy }));
    }

    [Theory]
    [InlineData("up")]
    [InlineData("ascending")]
    public void Unknown_sort_directions_are_rejected(string direction)
    {
        Assert.Contains("SortDirection", ErrorsFor(ValidQuery() with { SortDirection = direction }));
    }

    [Fact]
    public void Negative_ranges_are_rejected()
    {
        var errors = ErrorsFor(ValidQuery() with { MinPower = -1m, MaxPower = -1m, MinPrice = -1m, MaxPrice = -1m });

        Assert.Contains("MinPower", errors);
        Assert.Contains("MaxPower", errors);
        Assert.Contains("MinPrice", errors);
        Assert.Contains("MaxPrice", errors);
    }

    [Fact]
    public void Minimum_must_not_exceed_maximum()
    {
        var errors = ErrorsFor(ValidQuery() with { MinPower = 600m, MaxPower = 500m, MinPrice = 10m, MaxPrice = 5m });

        Assert.Contains("MinPower", errors);
        Assert.Contains("MinPrice", errors);
    }
}
