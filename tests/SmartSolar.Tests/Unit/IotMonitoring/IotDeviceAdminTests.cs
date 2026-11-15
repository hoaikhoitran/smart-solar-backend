using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.IotMonitoring;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.IotMonitoring;

/// <summary>Admin device registry: registration (secret shown once, hash stored), validation, duplicates, lock/unlock and credential rotation.</summary>
public sealed class IotDeviceAdminTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task Registration_returns_the_secret_once_and_stores_only_its_hash()
    {
        await using var ctx = IotTestContext.Create();
        var admin = await ctx.AdminAsync();

        var registered = await ctx.Devices().RegisterAsync(admin, new DeviceRegistrationInput(" esp32-001 ", "Roof meter", "ACS712-30A", null), None);

        Assert.True(registered.Succeeded, registered.Message);
        var credential = registered.Value!;
        Assert.StartsWith(IotDeviceCredentials.SecretPrefix, credential.Secret);
        Assert.Equal(1, credential.SecretVersion);
        Assert.Equal("ESP32-001", credential.Device.DeviceId);
        Assert.Equal(IotDeviceStatuses.Active, credential.Device.Status);
        Assert.Equal(ctx.Options.DefaultMaxCurrentAmps, credential.Device.MaxCurrentAmps);
        Assert.Equal(IotConnectionStatuses.Unassigned, credential.Device.ConnectionStatus);
        Assert.Null(credential.Device.CurrentBinding);

        var row = await ctx.Db.IotDevices.AsNoTracking().SingleAsync();
        Assert.Equal(IotDeviceCredentials.Hash(credential.Secret), row.SecretHash);
        Assert.DoesNotContain(credential.Secret, row.SecretHash);
        Assert.True(IotDeviceCredentials.Matches(credential.Secret, row.SecretHash));
        Assert.False(IotDeviceCredentials.Matches(credential.Secret + "x", row.SecretHash));

        var fetched = await ctx.Devices().GetAsync(credential.Device.Id, None);
        Assert.Equal(credential.Device, fetched.Value);
        var list = await ctx.Devices().ListAsync(null, "roof", 1, 20, None);
        Assert.Equal(1, list.Value!.TotalItems);
        Assert.Equal(credential.Device.Id, list.Value.Items.Single().Id);
        Assert.Empty((await ctx.Devices().ListAsync(IotDeviceStatuses.Disabled, null, 1, 20, None)).Value!.Items);
    }

    [Fact]
    public async Task Registration_validates_fields_and_refuses_a_duplicate_id()
    {
        await using var ctx = IotTestContext.Create();
        var admin = await ctx.AdminAsync();
        var devices = ctx.Devices();

        foreach (var (input, field) in new (DeviceRegistrationInput, string)[]
                 {
                     (new("ab", "Name", null, null), "deviceId"),
                     (new("bad id!", "Name", null, null), "deviceId"),
                     (new("ESP32-002", "", null, null), "displayName"),
                     (new("ESP32-002", "Name", null, 0m), "maxCurrentAmps"),
                     (new("ESP32-002", "Name", null, 1001m), "maxCurrentAmps"),
                     (new("ESP32-002", "Name", new string('x', 51), null), "sensorModel"),
                 })
        {
            var refused = await devices.RegisterAsync(admin, input, None);
            Assert.Equal(IotErrorCodes.ValidationFailed, refused.ErrorCode);
            Assert.Contains(field, ((Dictionary<string, string[]>)refused.Details!).Keys);
        }

        Assert.True((await devices.RegisterAsync(admin, new DeviceRegistrationInput("ESP32-002", "First", null, 20m), None)).Succeeded);
        var duplicate = await devices.RegisterAsync(admin, new DeviceRegistrationInput("esp32-002", "Second", null, null), None);
        Assert.Equal(IotErrorCodes.DeviceIdTaken, duplicate.ErrorCode);
        Assert.Equal(1, await ctx.Db.IotDevices.CountAsync());
        Assert.Equal(IotErrorCodes.ValidationFailed, (await devices.ListAsync("WEIRD", null, 1, 20, None)).ErrorCode);
        Assert.Equal(IotErrorCodes.ValidationFailed, (await devices.ListAsync(null, null, 1, IotDeviceHandlers.MaxPageSize + 1, None)).ErrorCode);
    }

    [Fact]
    public async Task Disable_enable_and_rotate_change_the_state_and_the_credential()
    {
        await using var ctx = IotTestContext.Create();
        var admin = await ctx.AdminAsync();
        var (id, _, secret) = await ctx.DeviceAsync(admin);
        var devices = ctx.Devices();

        Assert.Equal(IotErrorCodes.ValidationFailed, (await devices.DisableAsync(admin, id, " ", null, None)).ErrorCode);
        Assert.Equal(IotErrorCodes.DeviceNotFound, (await devices.DisableAsync(admin, Guid.NewGuid(), "lost", null, None)).ErrorCode);
        Assert.Equal(IotErrorCodes.StaleRevision, (await devices.DisableAsync(admin, id, "lost", 99, None)).ErrorCode);

        var disabled = await devices.DisableAsync(admin, id, "Lost on site", 1, None);
        Assert.True(disabled.Succeeded, disabled.Message);
        Assert.Equal((IotDeviceStatuses.Disabled, IotConnectionStatuses.Disabled, "Lost on site", 2), (disabled.Value!.Status, disabled.Value.ConnectionStatus, disabled.Value.DisabledReason, disabled.Value.Revision));
        Assert.Equal(IotErrorCodes.DeviceDisabled, (await devices.DisableAsync(admin, id, "again", null, None)).ErrorCode);

        var enabled = await devices.EnableAsync(admin, id, null, None);
        Assert.Equal((IotDeviceStatuses.Active, null, null), (enabled.Value!.Status, enabled.Value.DisabledAt, enabled.Value.DisabledReason));
        Assert.Equal(IotErrorCodes.DeviceNotDisabled, (await devices.EnableAsync(admin, id, null, None)).ErrorCode);

        var rotated = await devices.RotateCredentialAsync(admin, id, null, None);
        Assert.True(rotated.Succeeded, rotated.Message);
        Assert.NotEqual(secret, rotated.Value!.Secret);
        Assert.Equal(2, rotated.Value.SecretVersion);
        var row = await ctx.Db.IotDevices.AsNoTracking().SingleAsync(d => d.Id == id);
        Assert.False(IotDeviceCredentials.Matches(secret, row.SecretHash));
        Assert.True(IotDeviceCredentials.Matches(rotated.Value.Secret, row.SecretHash));
        Assert.Equal(ctx.Clock.GetUtcNow(), row.SecretRotatedAt);
    }
}
