using SmartSolar.Modules.PreSurvey.CreateCustomerProfile;
using SmartSolar.Modules.PreSurvey.CreatePreSurvey;
using SmartSolar.Modules.PreSurvey.CreatePropertySite;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.PreSurvey.UpdatePreSurvey;

namespace SmartSolar.Tests.Unit.PreSurvey;

/// <summary>
/// Input validation of the pre-survey commands (customer profile, property site, pre-survey create/update). Each row changes one
/// field of a valid command; boundaries are inclusive. Create and Update pre-survey share the same technical rules, so both
/// validators run every pre-survey row.
/// </summary>
public sealed class PreSurveyCommandValidatorTests
{
    // ------------------------------------------------------------------ customer profile

    private static CreateCustomerProfileCommand Profile() => new(Guid.NewGuid(), CustomerType.Business, "ABC Company", "0312345678", "Normal note");

    [Theory]
    [InlineData("valid", null)]
    [InlineData("fields at their maximum length", null)]
    [InlineData("empty user", nameof(CreateCustomerProfileCommand.UserId))]
    [InlineData("company name of 256", nameof(CreateCustomerProfileCommand.CompanyName))]
    [InlineData("tax code of 51", nameof(CreateCustomerProfileCommand.TaxCode))]
    [InlineData("note of 1001", nameof(CreateCustomerProfileCommand.Note))]
    public async Task Customer_profile_rules(string change, string? invalidField)
    {
        var command = change switch
        {
            "fields at their maximum length" => Profile() with { CompanyName = new string('A', 255), TaxCode = new string('1', 50), Note = new string('N', 1000) },
            "empty user" => Profile() with { UserId = Guid.Empty },
            "company name of 256" => Profile() with { CompanyName = new string('A', 256) },
            "tax code of 51" => Profile() with { TaxCode = new string('1', 51) },
            "note of 1001" => Profile() with { CustomerType = CustomerType.Individual, Note = new string('N', 1001) },
            _ => Profile(),
        };

        AssertErrors(invalidField, (await new CreateCustomerProfileCommandValidator().ValidateAsync(command)).Errors.Select(e => e.PropertyName));
    }

    // ------------------------------------------------------------------ property site

    private static CreatePropertySiteCommand Site() => new(Guid.NewGuid(), "Main Rooftop", "Ho Chi Minh", "District 1", "Ben Nghe", "1 Le Loi",
        10.77m, 106.70m, InstallationSurfaceType.Rooftop, "Concrete", null);

    [Theory]
    [InlineData("valid", null, null)]
    [InlineData("coordinates on the inclusive boundaries", null, null)]
    [InlineData("empty user", null, nameof(CreatePropertySiteCommand.UserId))]
    [InlineData("name", "", nameof(CreatePropertySiteCommand.Name))]
    [InlineData("name", "   ", nameof(CreatePropertySiteCommand.Name))]
    [InlineData("name of 151", null, nameof(CreatePropertySiteCommand.Name))]
    [InlineData("province", "", nameof(CreatePropertySiteCommand.Province))]
    [InlineData("province", "   ", nameof(CreatePropertySiteCommand.Province))]
    [InlineData("latitude", "-90.0001", nameof(CreatePropertySiteCommand.Latitude))]
    [InlineData("latitude", "-91", nameof(CreatePropertySiteCommand.Latitude))]
    [InlineData("latitude", "90.0001", nameof(CreatePropertySiteCommand.Latitude))]
    [InlineData("latitude", "91", nameof(CreatePropertySiteCommand.Latitude))]
    [InlineData("longitude", "-180.0001", nameof(CreatePropertySiteCommand.Longitude))]
    [InlineData("longitude", "-181", nameof(CreatePropertySiteCommand.Longitude))]
    [InlineData("longitude", "180.0001", nameof(CreatePropertySiteCommand.Longitude))]
    [InlineData("longitude", "181", nameof(CreatePropertySiteCommand.Longitude))]
    public async Task Property_site_rules(string field, string? value, string? invalidField)
    {
        decimal Number(string v) => decimal.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
        var command = field switch
        {
            "coordinates on the inclusive boundaries" => Site() with { Latitude = -90m, Longitude = 180m },
            "empty user" => Site() with { UserId = Guid.Empty },
            "name" => Site() with { Name = value! },
            "name of 151" => Site() with { Name = new string('A', 151) },
            "province" => Site() with { Province = value! },
            "latitude" => Site() with { Latitude = Number(value!) },
            "longitude" => Site() with { Longitude = Number(value!) },
            _ => Site(),
        };

        AssertErrors(invalidField, (await new CreatePropertySiteCommandValidator().ValidateAsync(command)).Errors.Select(e => e.PropertyName));
    }

    // ------------------------------------------------------------------ pre-survey (create and update)

    /// <summary>Applies one row to the technical fields: (total, usable, tilt, azimuth).</summary>
    private static (decimal? Total, decimal? Usable, decimal? Tilt, decimal? Azimuth) Technical(string change) => change switch
    {
        "draft without technical fields" => (null, null, null, null),
        "lower angle boundaries" => (100m, 80m, 0m, 0m),
        "upper angle boundaries" => (100m, 80m, 90m, 360m),
        "usable equals total" => (100m, 100m, 15m, 180m),
        "total 0" => (0m, null, 15m, 180m),
        "total -1" => (-1m, null, 15m, 180m),
        "usable 0" => (100m, 0m, 15m, 180m),
        "usable -1" => (100m, -1m, 15m, 180m),
        "usable above total" => (100m, 100.01m, 15m, 180m),
        "tilt -0.1" => (100m, 80m, -0.1m, 180m),
        "tilt 90.1" => (100m, 80m, 90.1m, 180m),
        "azimuth -0.1" => (100m, 80m, 15m, -0.1m),
        "azimuth 360.1" => (100m, 80m, 15m, 360.1m),
        _ => (100m, 80m, 15m, 180m),
    };

    [Theory]
    [InlineData("valid", null)]
    [InlineData("draft without technical fields", null)]
    [InlineData("lower angle boundaries", null)]
    [InlineData("upper angle boundaries", null)]
    [InlineData("usable equals total", null)]
    [InlineData("empty user", "UserId")]
    [InlineData("empty target", "Target")]
    [InlineData("total 0", "TotalAreaM2")]
    [InlineData("total -1", "TotalAreaM2")]
    [InlineData("usable 0", "UsableAreaM2")]
    [InlineData("usable -1", "UsableAreaM2")]
    [InlineData("usable above total", "UsableAreaM2")]
    [InlineData("tilt -0.1", "TiltDegree")]
    [InlineData("tilt 90.1", "TiltDegree")]
    [InlineData("azimuth -0.1", "AzimuthDegree")]
    [InlineData("azimuth 360.1", "AzimuthDegree")]
    public async Task Pre_survey_rules_are_the_same_for_create_and_update(string change, string? invalidField)
    {
        var (total, usable, tilt, azimuth) = Technical(change);
        var user = change == "empty user" ? Guid.Empty : Guid.NewGuid();
        var target = change == "empty target" ? Guid.Empty : Guid.NewGuid();
        bool? obstruction = change == "draft without technical fields" ? null : false;

        var create = await new CreatePreSurveyCommandValidator().ValidateAsync(new CreatePreSurveyCommand(user, target, total, usable, tilt, azimuth, obstruction));
        var update = await new UpdatePreSurveyCommandValidator().ValidateAsync(new UpdatePreSurveyCommand(user, target, total, usable, tilt, azimuth, obstruction));

        AssertErrors(invalidField == "Target" ? nameof(CreatePreSurveyCommand.PropertySiteId) : invalidField, create.Errors.Select(e => e.PropertyName));
        AssertErrors(invalidField == "Target" ? nameof(UpdatePreSurveyCommand.PreSurveyId) : invalidField, update.Errors.Select(e => e.PropertyName));
    }

    private static void AssertErrors(string? invalidField, IEnumerable<string> errors)
    {
        if (invalidField is null) Assert.Empty(errors);
        else Assert.Contains(invalidField, errors);
    }
}
