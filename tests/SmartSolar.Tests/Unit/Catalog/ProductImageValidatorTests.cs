using SmartSolar.Modules.Catalog.ManageProducts;
using SmartSolar.Modules.Catalog.ProductImages;

namespace SmartSolar.Tests.Unit.Catalog;

public class ProductImageValidatorTests
{
    private static CreateProductCommand Panel(string? imageUrl = null, IReadOnlyList<ProductImageInput>? images = null) => new(
        Sku: "PNL-550", ProductType: "SOLAR_PANEL", Category: "PANEL", Name: "Mono 550", Brand: "Jinko",
        Model: null, Unit: "PCS", UnitPrice: 1m, Currency: "VND", RatedPowerW: 550m, WidthMm: 1134m,
        HeightMm: 2278m, WarrantyMonth: null, Spec: null, ImageUrl: imageUrl, Status: null, Images: images);

    private static HashSet<string> CreateErrors(CreateProductCommand command, CatalogOptions? options = null)
        => new CreateProductCommandValidator(options ?? new CatalogOptions())
            .Validate(command).Errors.Select(e => e.PropertyName).ToHashSet();

    private static HashSet<string> AddErrors(string url, string? altText = null, CatalogOptions? options = null)
        => new AddProductImageCommandValidator(options ?? new CatalogOptions())
            .Validate(new AddProductImageCommand(Guid.NewGuid(), url, altText, false))
            .Errors.Select(e => e.PropertyName).ToHashSet();

    [Theory]
    [InlineData("https://cdn.example.com/panel.jpg")]
    [InlineData("  https://cdn.example.com/a%20b.png?v=2  ")]
    [InlineData("HTTPS://CDN.EXAMPLE.COM/x.webp")]
    public void Absolute_https_urls_are_accepted(string url)
    {
        Assert.Empty(AddErrors(url));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://cdn.example.com/panel.jpg")]
    [InlineData("ftp://cdn.example.com/panel.jpg")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("/images/panel.jpg")]
    [InlineData("cdn.example.com/panel.jpg")]
    [InlineData("https://user:secret@cdn.example.com/panel.jpg")]
    public void Invalid_or_insecure_urls_are_rejected(string url)
    {
        Assert.Contains("Url", AddErrors(url));
    }

    [Fact]
    public void Urls_and_alt_texts_have_length_limits()
    {
        var longUrl = "https://cdn.example.com/" + new string('a', 1000 - "https://cdn.example.com/".Length);

        Assert.Empty(AddErrors(longUrl, new string('x', 255)));
        Assert.Contains("Url", AddErrors(longUrl + "a"));
        Assert.Contains("AltText", AddErrors("https://cdn.example.com/a.jpg", new string('x', 256)));
    }

    [Fact]
    public void A_configured_host_allowlist_is_enforced_case_insensitively()
    {
        var options = new CatalogOptions { ImageAllowedHosts = ["cdn.smartsolar.vn", " Images.Example.com "] };

        Assert.Empty(AddErrors("https://CDN.smartsolar.vn/a.jpg", options: options));
        Assert.Empty(AddErrors("https://images.example.com/a.jpg", options: options));
        Assert.Contains("Url", AddErrors("https://evil.example.net/a.jpg", options: options));
        // Exact host match: a subdomain or a look-alike suffix is not on the list.
        Assert.Contains("Url", AddErrors("https://x.cdn.smartsolar.vn/a.jpg", options: options));
        Assert.Contains("Url", AddErrors("https://cdn.smartsolar.vn.evil.net/a.jpg", options: options));
    }

    [Fact]
    public void Create_accepts_no_images_and_a_valid_gallery()
    {
        Assert.Empty(CreateErrors(Panel()));
        Assert.Empty(CreateErrors(Panel(images: [new("https://cdn.example.com/a.jpg", "Front"), new("https://cdn.example.com/b.jpg", null)])));
    }

    [Fact]
    public void Create_rejects_more_images_than_the_configured_maximum()
    {
        var options = new CatalogOptions { MaxImagesPerProduct = 2 };
        var images = Enumerable.Range(0, 3).Select(i => new ProductImageInput($"https://cdn.example.com/{i}.jpg", null)).ToList();

        Assert.Contains("Images", CreateErrors(Panel(images: images), options));
        Assert.Empty(CreateErrors(Panel(images: images.Take(2).ToList()), options));
    }

    [Fact]
    public void Create_reports_each_bad_image_and_duplicate_urls()
    {
        var errors = CreateErrors(Panel(images:
        [
            new("https://cdn.example.com/a.jpg", null),
            new("http://cdn.example.com/b.jpg", null),
            new(" https://cdn.example.com/a.jpg ", new string('x', 256))
        ]));

        Assert.Contains("Images[1].Url", errors);
        Assert.Contains("Images[2].Url", errors);
        Assert.Contains("Images[2].AltText", errors);
        Assert.DoesNotContain("Images[0].Url", errors);
    }

    [Fact]
    public void The_legacy_image_url_follows_the_same_url_rules()
    {
        Assert.Empty(CreateErrors(Panel(imageUrl: "https://cdn.example.com/a.jpg")));
        Assert.Empty(CreateErrors(Panel(imageUrl: "   ")));
        Assert.Contains("ImageUrl", CreateErrors(Panel(imageUrl: "http://cdn.example.com/a.jpg")));
    }

    [Fact]
    public void The_legacy_image_url_may_accompany_images_only_as_the_first_image()
    {
        IReadOnlyList<ProductImageInput> images = [new("https://cdn.example.com/a.jpg", null), new("https://cdn.example.com/b.jpg", null)];

        Assert.Empty(CreateErrors(Panel(imageUrl: "https://cdn.example.com/a.jpg", images: images)));
        Assert.Contains("ImageUrl", CreateErrors(Panel(imageUrl: "https://cdn.example.com/b.jpg", images: images)));
    }

    [Fact]
    public void Reorder_requires_an_id_list()
    {
        var validator = new ReorderProductImagesCommandValidator();

        Assert.False(validator.Validate(new ReorderProductImagesCommand(Guid.NewGuid(), null!)).IsValid);
        Assert.True(validator.Validate(new ReorderProductImagesCommand(Guid.NewGuid(), [])).IsValid);
    }
}
