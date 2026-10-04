using SmartSolar.Modules.PreSurvey.CreateCustomerProfile;
using SmartSolar.Modules.PreSurvey.Enums;

namespace SmartSolar.Tests.Unit.PreSurvey;

public sealed class CreateCustomerProfileCommandValidatorTests
{
    [Fact]
    public async Task Accepts_valid_command()
    {
        // Arrange
        var validator = new CreateCustomerProfileCommandValidator();

        var command = new CreateCustomerProfileCommand(
            Guid.NewGuid(),
            CustomerType.Business,
            "ABC Company",
            "0312345678",
            "Normal note");

        // Act
        var result = await validator.ValidateAsync(command);

        // Assert
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Rejects_empty_user_id()
    {
        var validator = new CreateCustomerProfileCommandValidator();

        var command = new CreateCustomerProfileCommand(
            Guid.Empty,
            CustomerType.Business,
            "ABC Company",
            null,
            null);

        var result = await validator.ValidateAsync(command);

        Assert.False(result.IsValid);

        Assert.Contains(
            result.Errors,
            x => x.PropertyName == nameof(CreateCustomerProfileCommand.UserId));
    }

    [Fact]
    public async Task Rejects_company_name_longer_than_255_characters()
    {
        var validator = new CreateCustomerProfileCommandValidator();

        var command = new CreateCustomerProfileCommand(
            Guid.NewGuid(),
            CustomerType.Business,
            new string('A', 256),
            null,
            null);

        var result = await validator.ValidateAsync(command);

        Assert.False(result.IsValid);

        Assert.Contains(
            result.Errors,
            x => x.PropertyName == nameof(CreateCustomerProfileCommand.CompanyName));
    }

    [Fact]
    public async Task Rejects_tax_code_longer_than_50_characters()
    {
        // Arrange
        var validator = new CreateCustomerProfileCommandValidator();

        var command = new CreateCustomerProfileCommand(
            Guid.NewGuid(),
            CustomerType.Business,
            null,
            new string('1', 51),
            null);

        // Act
        var result = await validator.ValidateAsync(command);

        // Assert
        Assert.False(result.IsValid);

        Assert.Contains(
            result.Errors,
            x => x.PropertyName == nameof(CreateCustomerProfileCommand.TaxCode));
    }

    [Fact]
    public async Task Rejects_note_longer_than_1000_characters()
    {
        // Arrange
        var validator = new CreateCustomerProfileCommandValidator();

        var command = new CreateCustomerProfileCommand(
            Guid.NewGuid(),
            CustomerType.Individual,
            null,
            null,
            new string('N', 1001));

        // Act
        var result = await validator.ValidateAsync(command);

        // Assert
        Assert.False(result.IsValid);

        Assert.Contains(
            result.Errors,
            x => x.PropertyName == nameof(CreateCustomerProfileCommand.Note));
    }

    [Fact]
    public async Task Accepts_fields_at_their_maximum_length()
    {
        // Arrange
        var validator = new CreateCustomerProfileCommandValidator();

        var command = new CreateCustomerProfileCommand(
            Guid.NewGuid(),
            CustomerType.Business,
            new string('A', 255),
            new string('1', 50),
            new string('N', 1000));

        // Act
        var result = await validator.ValidateAsync(command);

        // Assert
        Assert.True(result.IsValid);
    }
}
