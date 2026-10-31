using SmartSolar.Modules.Quotations;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.InstallationWorkflow;

/// <summary>BL-07: quotation settings outside the rules the database enforces stop the API at startup instead of failing every quotation.</summary>
public sealed class QuotationOptionsTests
{
    [Fact]
    public void Defaults_are_valid()
        => Assert.Empty(new QuotationOptions().Validate());

    [Theory]
    [InlineData(25, 30, 90, "DefaultTaxRatePercent")]
    [InlineData(-1, 30, 90, "DefaultTaxRatePercent")]
    [InlineData(8, 0, 90, "AutomaticValidityDays")]
    [InlineData(8, 120, 90, "AutomaticValidityDays")]
    [InlineData(8, 30, 400, "MaxValidityDays")]
    public void Out_of_range_values_are_reported_with_their_key(decimal tax, int automaticDays, int maxDays, string key)
    {
        var problems = new QuotationOptions { DefaultTaxRatePercent = tax, AutomaticValidityDays = automaticDays, MaxValidityDays = maxDays }.Validate();

        Assert.Contains(problems, p => p.StartsWith($"Quotation:{key}", StringComparison.Ordinal));
    }

    [Fact]
    public void The_api_refuses_to_start_with_an_invalid_tax_rate()
    {
        using var factory = new AuthApiFactory(new Dictionary<string, string?> { ["Quotation:DefaultTaxRatePercent"] = "25" });

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Quotation:DefaultTaxRatePercent", error.ToString());
    }
}
