using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.PreSurvey.CreateCustomerProfile;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.PreSurvey;

public sealed class CreateCustomerProfileHandlerTests
{
    [Fact]
    public async Task Creates_customer_profile_when_user_does_not_have_one()
    {
        await using var ctx = new PreSurveyTestContext();

        var user = await ctx.SeedUserAsync();

        var command = new CreateCustomerProfileCommand(
            user.Id,
            CustomerType.Business,
            "ABC Company",
            "0312345678",
            "New customer");

        var result = await ctx
            .CreateCustomerProfileHandler()
            .HandleAsync(command, CancellationToken.None);

        Assert.Equal(
            CreateCustomerProfileOutcome.Created,
            result.Outcome);

        var customer = await ctx.Db.Customers.SingleAsync();

        Assert.Equal(user.Id, customer.UserId);
        Assert.Equal(
            CustomerType.Business,
            customer.CustomerType);
        Assert.Equal("ABC Company", customer.CompanyName);
        Assert.Equal("0312345678", customer.TaxCode);
        Assert.Equal("New customer", customer.Note);

        Assert.Equal(customer.Id, result.CustomerId);
    }

    [Fact]
    public async Task Returns_already_exists_when_user_already_has_customer_profile()
    {
        // Arrange ( chuẩn bị test)
        await using var ctx = new PreSurveyTestContext();
        var user = await ctx.SeedUserAsync();
        await ctx.SeedCustomerAsync(user.Id);
        var command = new CreateCustomerProfileCommand(user.Id, CustomerType.Business, "Another Company", "0999999999", null);
        // Act - bước gọi các hàm hoạt động để có kêt quá để so sánh ở bước cuối 
        var result = await ctx.CreateCustomerProfileHandler().HandleAsync(command, CancellationToken.None);
        // Asert
        Assert.Equal(CreateCustomerProfileOutcome.AlreadyExists, result.Outcome);
        Assert.Null(result.CustomerId);
        Assert.Equal(1, await ctx.Db.Customers.CountAsync());
    }

    [Fact]
    public async Task Trims_customer_profile_fields_before_persisting()
    {
        // Arrange
        await using var ctx = new PreSurveyTestContext();

        var user = await ctx.SeedUserAsync();

        var command = new CreateCustomerProfileCommand(
            user.Id,
            CustomerType.Business,
            "  ABC Company  ",
            "  0312345678  ",
            "  Important customer  ");

        // Act
        await ctx
            .CreateCustomerProfileHandler()
            .HandleAsync(command, CancellationToken.None);

        // Assert
        var customer = await ctx.Db.Customers.SingleAsync();

        Assert.Equal(CustomerType.Business, customer.CustomerType);
        Assert.Equal("ABC Company", customer.CompanyName);
        Assert.Equal("0312345678", customer.TaxCode);
        Assert.Equal("Important customer", customer.Note);
    }

            [Fact]
        public async Task Stores_optional_whitespace_fields_as_null()
        {
            // Arrange
            await using var ctx = new PreSurveyTestContext();

            var user = await ctx.SeedUserAsync();

            var command = new CreateCustomerProfileCommand(
                user.Id,
                null,
                "   ",
                null,
                "   ");

            // Act
            await ctx
                .CreateCustomerProfileHandler()
                .HandleAsync(command, CancellationToken.None);

            // Assert
            var customer = await ctx.Db.Customers.SingleAsync();

            Assert.Null(customer.CustomerType);
            Assert.Null(customer.CompanyName);
            Assert.Null(customer.TaxCode);
            Assert.Null(customer.Note);
        }

}