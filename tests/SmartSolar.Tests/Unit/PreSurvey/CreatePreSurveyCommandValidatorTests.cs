using SmartSolar.Modules.PreSurvey.CreatePreSurvey;

namespace SmartSolar.Tests.Unit.PreSurvey;

public sealed class CreatePreSurveyCommandValidatorTests
{
    private static CreatePreSurveyCommand ValidCommand() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        100m,
        80m,
        15m,
        180m,
        false);

    private static async Task<IReadOnlyCollection<string>> ErrorsFor(CreatePreSurveyCommand command)
    {
        var result = await new CreatePreSurveyCommandValidator().ValidateAsync(command);
        return result.Errors.Select(x => x.PropertyName).ToHashSet();
    }

    [Fact]
    public async Task Accepts_valid_command()
    {
        // Act
        var errors = await ErrorsFor(ValidCommand());

        // Assert
        Assert.Empty(errors);
    }

    [Fact]
    public async Task Accepts_draft_without_technical_fields()
    {
        // Arrange
        var command = ValidCommand() with
        {
            TotalAreaM2 = null,
            UsableAreaM2 = null,
            TiltDegree = null,
            AzimuthDegree = null,
            HasObstruction = null
        };

        // Act
        var errors = await ErrorsFor(command);

        // Assert
        Assert.Empty(errors);
    }

    [Fact]
    public async Task Accepts_angles_on_the_inclusive_boundaries()
    {
        // Arrange
        var lower = ValidCommand() with { TiltDegree = 0m, AzimuthDegree = 0m };
        var upper = ValidCommand() with { TiltDegree = 90m, AzimuthDegree = 360m };

        // Act
        var lowerErrors = await ErrorsFor(lower);
        var upperErrors = await ErrorsFor(upper);

        // Assert
        Assert.Empty(lowerErrors);
        Assert.Empty(upperErrors);
    }

    [Fact]
    public async Task Rejects_empty_property_site_id()
    {
        // Arrange
        var command = ValidCommand() with { PropertySiteId = Guid.Empty };

        // Act
        var errors = await ErrorsFor(command);

        // Assert
        Assert.Contains(nameof(CreatePreSurveyCommand.PropertySiteId), errors);
    }

    [Fact]
    public async Task Rejects_empty_user_id()
    {
        // Arrange
        var command = ValidCommand() with { UserId = Guid.Empty };

        // Act
        var errors = await ErrorsFor(command);

        // Assert
        Assert.Contains(nameof(CreatePreSurveyCommand.UserId), errors);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Rejects_non_positive_total_area(int totalArea)
    {
        // Arrange
        var command = ValidCommand() with { TotalAreaM2 = totalArea, UsableAreaM2 = null };

        // Act
        var errors = await ErrorsFor(command);

        // Assert
        Assert.Contains(nameof(CreatePreSurveyCommand.TotalAreaM2), errors);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Rejects_non_positive_usable_area(int usableArea)
    {
        // Arrange
        var command = ValidCommand() with { UsableAreaM2 = usableArea };

        // Act
        var errors = await ErrorsFor(command);

        // Assert
        Assert.Contains(nameof(CreatePreSurveyCommand.UsableAreaM2), errors);
    }

    [Fact]
    public async Task Rejects_usable_area_greater_than_total_area()
    {
        // Arrange
        var command = ValidCommand() with { TotalAreaM2 = 100m, UsableAreaM2 = 100.01m };

        // Act
        var errors = await ErrorsFor(command);

        // Assert
        Assert.Contains(nameof(CreatePreSurveyCommand.UsableAreaM2), errors);
    }

    [Fact]
    public async Task Accepts_usable_area_equal_to_total_area()
    {
        // Arrange
        var command = ValidCommand() with { TotalAreaM2 = 100m, UsableAreaM2 = 100m };

        // Act
        var errors = await ErrorsFor(command);

        // Assert
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(90.1)]
    public async Task Rejects_tilt_degree_outside_0_to_90(double tilt)
    {
        // Arrange
        var command = ValidCommand() with { TiltDegree = (decimal)tilt };

        // Act
        var errors = await ErrorsFor(command);

        // Assert
        Assert.Contains(nameof(CreatePreSurveyCommand.TiltDegree), errors);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(360.1)]
    public async Task Rejects_azimuth_degree_outside_0_to_360(double azimuth)
    {
        // Arrange
        var command = ValidCommand() with { AzimuthDegree = (decimal)azimuth };

        // Act
        var errors = await ErrorsFor(command);

        // Assert
        Assert.Contains(nameof(CreatePreSurveyCommand.AzimuthDegree), errors);
    }
}
