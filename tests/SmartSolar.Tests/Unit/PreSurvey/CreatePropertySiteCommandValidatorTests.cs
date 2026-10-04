using SmartSolar.Modules.PreSurvey.CreatePropertySite;
using SmartSolar.Modules.PreSurvey.Enums;

namespace SmartSolar.Tests.Unit.PreSurvey;

public sealed class CreatePropertySiteCommandValidatorTests
{
    private static CreatePropertySiteCommand ValidCommand() => new(
        Guid.NewGuid(),
        "Main Rooftop",
        "Ho Chi Minh",
        "District 1",
        "Ben Nghe",
        "1 Le Loi",
        10.77m,
        106.70m,
        InstallationSurfaceType.Rooftop,
        "Concrete",
        null);

    private static async Task<IReadOnlyCollection<string>> ErrorsFor(CreatePropertySiteCommand command)
    {
        var result = await new CreatePropertySiteCommandValidator().ValidateAsync(command);
        return result.Errors.Select(x => x.PropertyName).ToHashSet();
    }

    [Fact]
    public async Task Accepts_valid_command()
    {
        // Arrange
        var command = ValidCommand();

        // Act
        var errors = await ErrorsFor(command);

        // Assert
        Assert.Empty(errors);
    }

    [Fact]
    public async Task Accepts_coordinates_on_the_inclusive_boundaries()
    {
        // Arrange
        var command = ValidCommand() with { Latitude = -90m, Longitude = 180m };

        // Act
        var errors = await ErrorsFor(command);

        // Assert
        Assert.Empty(errors);
    }

    [Fact]
    public async Task Rejects_empty_user_id()
    {
        // Arrange
        var command = ValidCommand() with { UserId = Guid.Empty };

        // Act
        var errors = await ErrorsFor(command);

        // Assert
        Assert.Contains(nameof(CreatePropertySiteCommand.UserId), errors);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rejects_empty_name(string name)
    {
        // Arrange
        var command = ValidCommand() with { Name = name };

        // Act
        var errors = await ErrorsFor(command);

        // Assert
        Assert.Contains(nameof(CreatePropertySiteCommand.Name), errors);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rejects_empty_province(string province)
    {
        // Arrange
        var command = ValidCommand() with { Province = province };

        // Act
        var errors = await ErrorsFor(command);

        // Assert
        Assert.Contains(nameof(CreatePropertySiteCommand.Province), errors);
    }

    [Fact]
    public async Task Rejects_name_longer_than_150_characters()
    {
        // Arrange
        var command = ValidCommand() with { Name = new string('A', 151) };

        // Act
        var errors = await ErrorsFor(command);

        // Assert
        Assert.Contains(nameof(CreatePropertySiteCommand.Name), errors);
    }

    [Theory]
    [InlineData(-90.0001)]
    [InlineData(-91)]
    public async Task Rejects_latitude_below_minus_90(double latitude)
    {
        // Arrange
        var command = ValidCommand() with { Latitude = (decimal)latitude };

        // Act
        var errors = await ErrorsFor(command);

        // Assert
        Assert.Contains(nameof(CreatePropertySiteCommand.Latitude), errors);
    }

    [Theory]
    [InlineData(90.0001)]
    [InlineData(91)]
    public async Task Rejects_latitude_above_90(double latitude)
    {
        // Arrange
        var command = ValidCommand() with { Latitude = (decimal)latitude };

        // Act
        var errors = await ErrorsFor(command);

        // Assert
        Assert.Contains(nameof(CreatePropertySiteCommand.Latitude), errors);
    }

    [Theory]
    [InlineData(-180.0001)]
    [InlineData(-181)]
    public async Task Rejects_longitude_below_minus_180(double longitude)
    {
        // Arrange
        var command = ValidCommand() with { Longitude = (decimal)longitude };

        // Act
        var errors = await ErrorsFor(command);

        // Assert
        Assert.Contains(nameof(CreatePropertySiteCommand.Longitude), errors);
    }

    [Theory]
    [InlineData(180.0001)]
    [InlineData(181)]
    public async Task Rejects_longitude_above_180(double longitude)
    {
        // Arrange
        var command = ValidCommand() with { Longitude = (decimal)longitude };

        // Act
        var errors = await ErrorsFor(command);

        // Assert
        Assert.Contains(nameof(CreatePropertySiteCommand.Longitude), errors);
    }
}
