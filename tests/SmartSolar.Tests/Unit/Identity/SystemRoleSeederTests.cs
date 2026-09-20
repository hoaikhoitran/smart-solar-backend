using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Infrastructure.Persistence.Seed;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.Identity;

public class SystemRoleSeederTests
{
    [Fact]
    public async Task Seeds_the_five_system_roles()
    {
        await using var ctx = new IdentityTestContext();

        await new SystemRoleSeeder(ctx.Db, NullLogger<SystemRoleSeeder>.Instance)
            .SeedAsync(CancellationToken.None);

        var codes = await ctx.Db.Roles.Select(r => r.Code).OrderBy(c => c).ToListAsync();
        Assert.Equal(RoleCodes.All.OrderBy(c => c), codes);
    }

    [Fact]
    public async Task Running_twice_does_not_duplicate_roles()
    {
        await using var ctx = new IdentityTestContext();
        var seeder = new SystemRoleSeeder(ctx.Db, NullLogger<SystemRoleSeeder>.Instance);

        await seeder.SeedAsync(CancellationToken.None);
        await seeder.SeedAsync(CancellationToken.None);

        Assert.Equal(RoleCodes.All.Count, await ctx.Db.Roles.CountAsync());
    }

    [Fact]
    public async Task Keeps_roles_that_already_exist_untouched()
    {
        await using var ctx = new IdentityTestContext();
        var existing = await ctx.SeedRoleAsync(RoleCodes.Customer);

        await new SystemRoleSeeder(ctx.Db, NullLogger<SystemRoleSeeder>.Instance)
            .SeedAsync(CancellationToken.None);

        var customer = await ctx.Db.Roles.SingleAsync(r => r.Code == RoleCodes.Customer);
        Assert.Equal(existing.Id, customer.Id);
        Assert.Equal(RoleCodes.All.Count, await ctx.Db.Roles.CountAsync());
    }
}
