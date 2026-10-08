using SmartSolar.Modules.Catalog.Installation;
using SmartSolar.Modules.Catalog.ManageProducts;

namespace SmartSolar.Tests.Unit.Catalog;

/// <summary>
/// The optional "installation" block inside Product.Spec. Products without it must
/// behave exactly as before; a block that is present must follow the strict format.
/// </summary>
public class ProductInstallationSpecTests
{
    private const string ValidBlock = """
        {
          "cells": 144,
          "installation": {
            "schemaVersion": 1,
            "requirements": {
              "panelGapMm": { "minMm": 10, "documentRef": "Model X Installation Manual rev 3", "section": "4.2", "url": "https://example.com/manual.pdf" },
              "edgeSetbackMm": { "minMm": 0, "documentRef": "Model X Installation Manual rev 3" }
            },
            "moduleThicknessMm": { "valueMm": 35, "documentRef": "Model X Datasheet rev 2" }
          }
        }
        """;

    private static CreateProductCommand PanelWithSpec(string? spec) => new(
        Sku: "PNL-550", ProductType: "SOLAR_PANEL", Category: "PANEL", Name: "Mono 550", Brand: "Jinko",
        Model: "Tiger Neo", Unit: "PCS", UnitPrice: 2_500_000m, Currency: "VND", RatedPowerW: 550m,
        WidthMm: 1134m, HeightMm: 2278m, WarrantyMonth: 144, Spec: spec, ImageUrl: null, Status: null);

    private static List<string> ValidatorMessages(string? spec)
        => new CreateProductCommandValidator().Validate(PanelWithSpec(spec)).Errors.Select(e => e.ErrorMessage).ToList();

    [Theory]
    [InlineData(null)]
    [InlineData("{\"cells\":144}")]
    [InlineData("{}")]
    public void Spec_without_installation_block_is_absent_and_valid(string? spec)
    {
        var result = ProductInstallationSpecParser.Parse(spec);

        Assert.False(result.Present);
        Assert.True(result.IsValid);
        Assert.Null(result.Spec);
        Assert.Empty(ValidatorMessages(spec));
    }

    [Fact]
    public void Valid_block_is_parsed_with_its_source_references()
    {
        var result = ProductInstallationSpecParser.Parse(ValidBlock);

        Assert.True(result.Present);
        Assert.True(result.IsValid);
        var gap = result.Spec!.Requirements[InstallationRequirementKeys.PanelGapMm];
        Assert.Equal(10m, gap.MinMm);
        Assert.Equal("Model X Installation Manual rev 3", gap.DocumentRef);
        Assert.Equal("4.2", gap.Section);
        Assert.Equal("https://example.com/manual.pdf", gap.Url);
        Assert.Equal(0m, result.Spec.Requirements[InstallationRequirementKeys.EdgeSetbackMm].MinMm);
        Assert.False(result.Spec.Requirements.ContainsKey(InstallationRequirementKeys.RowGapMm));
        Assert.Equal(35m, result.Spec.ModuleThicknessMm!.ValueMm);
        Assert.Empty(ValidatorMessages(ValidBlock));
    }

    [Fact]
    public void A_verified_flag_is_rejected_because_it_cannot_prove_certification()
    {
        const string spec = """
            {"installation":{"schemaVersion":1,"requirements":{"panelGapMm":{"minMm":10,"documentRef":"Manual","verified":true}}}}
            """;

        var result = ProductInstallationSpecParser.Parse(spec);

        Assert.True(result.Present);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("installation.requirements.panelGapMm.verified"));
        Assert.NotEmpty(ValidatorMessages(spec));
    }

    [Theory]
    [InlineData("""{"installation":[]}""", "installation")]
    [InlineData("""{"installation":{"requirements":{}}}""", "installation.schemaVersion")]
    [InlineData("""{"installation":{"schemaVersion":2}}""", "installation.schemaVersion")]
    [InlineData("""{"installation":{"schemaVersion":1,"extra":1}}""", "installation.extra")]
    [InlineData("""{"installation":{"schemaVersion":1,"requirements":{"wallGapMm":{"minMm":1,"documentRef":"x"}}}}""", "installation.requirements.wallGapMm")]
    [InlineData("""{"installation":{"schemaVersion":1,"requirements":{"panelGapMm":{"documentRef":"x"}}}}""", "installation.requirements.panelGapMm.minMm")]
    [InlineData("""{"installation":{"schemaVersion":1,"requirements":{"panelGapMm":{"minMm":-1,"documentRef":"x"}}}}""", "installation.requirements.panelGapMm.minMm")]
    [InlineData("""{"installation":{"schemaVersion":1,"requirements":{"panelGapMm":{"minMm":5001,"documentRef":"x"}}}}""", "installation.requirements.panelGapMm.minMm")]
    [InlineData("""{"installation":{"schemaVersion":1,"requirements":{"panelGapMm":{"minMm":"10","documentRef":"x"}}}}""", "installation.requirements.panelGapMm.minMm")]
    [InlineData("""{"installation":{"schemaVersion":1,"requirements":{"panelGapMm":{"minMm":10}}}}""", "installation.requirements.panelGapMm.documentRef")]
    [InlineData("""{"installation":{"schemaVersion":1,"requirements":{"panelGapMm":{"minMm":10,"documentRef":"   "}}}}""", "installation.requirements.panelGapMm.documentRef")]
    [InlineData("""{"installation":{"schemaVersion":1,"requirements":{"panelGapMm":{"minMm":10,"documentRef":"x","url":"ftp://a/b"}}}}""", "installation.requirements.panelGapMm.url")]
    [InlineData("""{"installation":{"schemaVersion":1,"moduleThicknessMm":{"valueMm":0,"documentRef":"x"}}}""", "installation.moduleThicknessMm.valueMm")]
    [InlineData("""{"installation":{"schemaVersion":1,"moduleThicknessMm":{"valueMm":35}}}""", "installation.moduleThicknessMm.documentRef")]
    public void Invalid_blocks_report_the_offending_path(string spec, string expectedPath)
    {
        var result = ProductInstallationSpecParser.Parse(spec);

        Assert.True(result.Present);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains($"'{expectedPath}'"));
        Assert.Contains(ValidatorMessages(spec), m => m.Contains($"'{expectedPath}'"));
    }

    [Fact]
    public void Duplicate_requirement_keys_are_rejected()
    {
        const string spec = """
            {"installation":{"schemaVersion":1,"requirements":{"panelGapMm":{"minMm":1,"documentRef":"a"},"panelGapMm":{"minMm":2,"documentRef":"b"}}}}
            """;

        var result = ProductInstallationSpecParser.Parse(spec);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("'installation.requirements.panelGapMm'"));
    }

    [Fact]
    public void Validator_reports_installation_errors_under_the_spec_property()
    {
        const string spec = """{"installation":{"schemaVersion":1,"requirements":{"panelGapMm":{"minMm":-5,"documentRef":"x"}}}}""";

        var failures = new CreateProductCommandValidator().Validate(PanelWithSpec(spec)).Errors;

        Assert.All(failures, f => Assert.Equal("Spec", f.PropertyName));
        Assert.Single(failures);
    }

    [Fact]
    public void Update_validator_applies_the_same_installation_rules()
    {
        const string spec = """{"installation":{"schemaVersion":1,"bogus":true}}""";
        var command = new UpdateProductCommand(
            ProductId: Guid.NewGuid(), Sku: "PNL-550", ProductType: "SOLAR_PANEL", Category: "PANEL", Name: "Mono 550", Brand: "Jinko",
            Model: null, Unit: "PCS", UnitPrice: 1m, Currency: "VND", RatedPowerW: 550m, WidthMm: 1134m,
            HeightMm: 2278m, WarrantyMonth: null, Spec: spec, ImageUrl: null);

        var failures = new UpdateProductCommandValidator().Validate(command).Errors;

        Assert.Contains(failures, f => f.ErrorMessage.Contains("'installation.bogus'"));
    }
}
