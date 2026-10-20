using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Infrastructure.Persistence.SystemSettings;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.OnSiteSurveys;
using SmartSolar.Modules.Scheduling.Options;
using SmartSolar.Modules.SystemSettings;
using SmartSolar.Tests.TestSupport;
using K = SmartSolar.Modules.SystemSettings.SchedulingPolicyKeys;

namespace SmartSolar.Tests.Unit.SystemSettings;

/// <summary>
/// Typed scheduling policy settings: registry defaults and ranges, validation (key, unit, integer range, cross-field rules),
/// versioned all-or-nothing updates with audit rows, optimistic concurrency, and the provider's cache + version invalidation.
/// </summary>
public sealed class SystemSettingsTests : IAsyncDisposable
{
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly PreSurveyTestContext _ctx = new();
    private readonly FixedTimeProvider _clock = new(new DateTimeOffset(2026, 10, 19, 3, 0, 0, TimeSpan.Zero));

    public ValueTask DisposeAsync() => _ctx.DisposeAsync();

    private SchedulingPolicyCache Cache { get; set; } = new(TimeSpan.FromSeconds(15));

    private SchedulingPolicyProvider Provider(ISystemSettingsUnitOfWork? uow = null)
        => new(uow ?? new SystemSettingsUnitOfWork(_ctx.Db), SchedulingPolicyDefinitions.Default, Cache, _clock);

    private SystemSettingsService Service(ISystemSettingsUnitOfWork? uow = null)
    {
        var unitOfWork = uow ?? new SystemSettingsUnitOfWork(_ctx.Db);
        return new SystemSettingsService(unitOfWork, SchedulingPolicyDefinitions.Default, Provider(unitOfWork), Cache, _clock,
            NullLogger<SystemSettingsService>.Instance);
    }

    private async Task<Guid> AdminAsync()
    {
        var user = PreSurveyTestContext.NewUser("Settings admin");
        _ctx.Db.UserAccounts.Add(user);
        await _ctx.Db.SaveChangesAsync();
        _ctx.Db.ChangeTracker.Clear();
        return user.Id;
    }

    private static SchedulingSettingsUpdateInput Input(int? expectedVersion, string? reason, params (string Key, int? Value, string? Unit)[] settings)
        => new(settings.Select(s => new SystemSettingChangeInput(s.Key, s.Value, s.Unit)).ToList(), expectedVersion, reason);

    private static IReadOnlyDictionary<string, string[]> Errors<T>(OperationResult<T> result)
        => Assert.IsAssignableFrom<IReadOnlyDictionary<string, string[]>>(result.Details);

    // ------------------------------------------------------------------ registry and defaults

    [Fact]
    public void Registry_exposes_exactly_the_inventory_with_the_current_defaults_units_and_ranges()
    {
        var d = SchedulingPolicyDefinitions.Default;
        var expected = new (string Key, string Unit, int Default, int Min, int Max)[]
        {
            (K.DispatchReviewSlaMinutes, SystemSettingUnits.Minutes, 240, 5, 10080),
            (K.DispatchEscalationDelayMinutes, SystemSettingUnits.Minutes, 0, 0, 10080),
            (K.DispatchReminderIntervalMinutes, SystemSettingUnits.Minutes, 0, 0, 10080),
            (K.DispatchProposalResponseWindowMinutes, SystemSettingUnits.Minutes, 1440, 15, 10080),
            (K.AcceptanceWindowMinutes, SystemSettingUnits.Minutes, 240, 5, 10080),
            (K.AcceptanceMinimumLeadMinutes, SystemSettingUnits.Minutes, 120, 0, 10080),
            (K.AcceptanceReminderLeadMinutes, SystemSettingUnits.Minutes, 0, 0, 10080),
            (K.CustomerChangeLeadMinutes, SystemSettingUnits.Minutes, 120, 0, 10080),
            (K.MinimumBookingLeadMinutes, SystemSettingUnits.Minutes, 0, 0, 10080),
            (K.BookingHorizonDays, SystemSettingUnits.Days, 30, 1, 365),
            (K.BufferBetweenAppointmentsMinutes, SystemSettingUnits.Minutes, 30, 0, 240),
            (K.OnSiteSurveyEarlyStartMinutes, SystemSettingUnits.Minutes, 120, 0, 1440),
            (K.InstallationMaxAssignmentDays, SystemSettingUnits.Days, 14, 1, 365),
        };
        Assert.Equal(expected, d.All.Select(x => (x.Key, x.Unit, x.Default, x.Min, x.Max)));
        Assert.All(d.All, x => Assert.Equal(SystemSettingTypes.Int, x.Type));
        Assert.All(d.All, x => Assert.True(x.AdminEditable));
        Assert.All(d.All, x => Assert.False(string.IsNullOrWhiteSpace(x.Description)));
        Assert.Empty(d.ValidateDefaults());

        // Current option defaults are the source of the scheduling/on-site/installation defaults.
        var options = new SchedulingOptions();
        Assert.Equal(options.AcceptanceWindowMinutes, d.Defaults[K.AcceptanceWindowMinutes]);
        Assert.Equal(options.AcceptanceMinimumLeadMinutes, d.Defaults[K.AcceptanceMinimumLeadMinutes]);
        Assert.Equal(options.CustomerChangeLeadMinutes, d.Defaults[K.CustomerChangeLeadMinutes]);
        Assert.Equal(options.MinimumBookingLeadMinutes, d.Defaults[K.MinimumBookingLeadMinutes]);
        Assert.Equal(options.BookingHorizonDays, d.Defaults[K.BookingHorizonDays]);
        Assert.Equal(options.BufferBetweenAppointmentsMinutes, d.Defaults[K.BufferBetweenAppointmentsMinutes]);
        Assert.Equal(new OnSiteSurveyOptions().EarlyStartMinutes, d.Defaults[K.OnSiteSurveyEarlyStartMinutes]);
        Assert.Equal(new InstallationOptions().MaxAssignmentDays, d.Defaults[K.InstallationMaxAssignmentDays]);
    }

    [Fact]
    public void Configured_option_values_become_the_bootstrap_defaults_and_invalid_ones_are_reported()
    {
        var configured = SchedulingPolicyDefinitions.Create(
            new SchedulingOptions { AcceptanceWindowMinutes = 300, BookingHorizonDays = 14, MinimumBookingLeadMinutes = 60, BufferBetweenAppointmentsMinutes = 15 },
            new OnSiteSurveyOptions { EarlyStartMinutes = 90 }, new InstallationOptions { MaxAssignmentDays = 7 });
        Assert.Equal((300, 14, 60, 15, 90, 7), (configured.Defaults[K.AcceptanceWindowMinutes], configured.Defaults[K.BookingHorizonDays],
            configured.Defaults[K.MinimumBookingLeadMinutes], configured.Defaults[K.BufferBetweenAppointmentsMinutes],
            configured.Defaults[K.OnSiteSurveyEarlyStartMinutes], configured.Defaults[K.InstallationMaxAssignmentDays]));
        Assert.Empty(configured.ValidateDefaults());

        var invalid = SchedulingPolicyDefinitions.Create(new SchedulingOptions { AcceptanceWindowMinutes = 2, BookingHorizonDays = 1, MinimumBookingLeadMinutes = 1440 },
            new OnSiteSurveyOptions(), new InstallationOptions { MaxAssignmentDays = 0 });
        var errors = invalid.ValidateDefaults();
        Assert.Contains(errors, e => e.Contains(K.AcceptanceWindowMinutes));
        Assert.Contains(errors, e => e.Contains(K.InstallationMaxAssignmentDays));
        Assert.Contains(errors, e => e.Contains(K.MinimumBookingLeadMinutes));
    }

    [Theory]
    [InlineData(K.MinimumBookingLeadMinutes, 1439, 1, true)]
    [InlineData(K.MinimumBookingLeadMinutes, 1440, 1, false)]
    [InlineData(K.AcceptanceMinimumLeadMinutes, 2880, 2, false)]
    [InlineData(K.AcceptanceMinimumLeadMinutes, 2879, 2, true)]
    [InlineData(K.CustomerChangeLeadMinutes, 1440, 1, false)]
    public void Leads_must_stay_below_the_booking_horizon(string key, int value, int horizonDays, bool valid)
    {
        var values = new Dictionary<string, int>(SchedulingPolicyDefinitions.Default.Defaults)
        {
            [K.BookingHorizonDays] = horizonDays, [K.MinimumBookingLeadMinutes] = 0, [K.AcceptanceMinimumLeadMinutes] = 0, [K.CustomerChangeLeadMinutes] = 0,
            [key] = value,
        };
        var errors = SchedulingPolicyDefinitions.RuleErrors(values);
        Assert.Equal(valid, !errors.ContainsKey(key));
    }

    [Theory]
    [InlineData(K.DispatchReminderIntervalMinutes, 0, true)]
    [InlineData(K.DispatchReminderIntervalMinutes, 14, false)]
    [InlineData(K.DispatchReminderIntervalMinutes, 15, true)]
    [InlineData(K.AcceptanceReminderLeadMinutes, 0, true)]
    [InlineData(K.AcceptanceReminderLeadMinutes, 239, true)]
    [InlineData(K.AcceptanceReminderLeadMinutes, 240, false)]
    public void Reminders_are_disabled_by_zero_and_otherwise_bounded(string key, int value, bool valid)
    {
        var values = new Dictionary<string, int>(SchedulingPolicyDefinitions.Default.Defaults) { [key] = value };
        Assert.Equal(valid, !SchedulingPolicyDefinitions.RuleErrors(values).ContainsKey(key));
    }

    [Fact]
    public void Snapshot_is_immutable_and_typed()
    {
        var source = new Dictionary<string, int>(SchedulingPolicyDefinitions.Default.Defaults);
        var snapshot = new SchedulingPolicySnapshot(3, source);
        source[K.DispatchReviewSlaMinutes] = 1;
        Assert.Equal(240, snapshot.DispatchReviewSlaMinutes);
        Assert.Equal(3, snapshot.Version);
        Assert.Equal(1440, snapshot.DispatchProposalResponseWindowMinutes);
        Assert.Equal(30, snapshot.BookingHorizonDays);
        Assert.Throws<KeyNotFoundException>(() => snapshot.Get("scheduling.unknown"));
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, int>)snapshot.Values)[K.DispatchReviewSlaMinutes] = 1);
    }

    // ------------------------------------------------------------------ provider and cache

    [Fact]
    public async Task Provider_returns_the_bootstrap_defaults_at_version_zero()
    {
        var snapshot = await Provider().GetAsync(None);
        Assert.Equal(0, snapshot.Version);
        Assert.Equal(SchedulingPolicyDefinitions.Default.Defaults.OrderBy(x => x.Key), snapshot.Values.OrderBy(x => x.Key));
    }

    [Fact]
    public async Task Provider_caches_for_at_most_the_ttl_and_a_local_update_invalidates_immediately()
    {
        var admin = await AdminAsync();
        var provider = Provider();
        Assert.Equal(0, (await provider.GetAsync(None)).Version);

        // A change written by another instance (directly in the database) is not visible until the TTL expires.
        _ctx.Db.Add(new SystemSetting { Key = K.DispatchReviewSlaMinutes, ValueInt = 60, Unit = SystemSettingUnits.Minutes, Version = 1, UpdatedAt = _clock.Now, Revision = 1 });
        _ctx.Db.Add(new SystemSettingsVersion { Version = 1, UpdatedAt = _clock.Now, Revision = 1 });
        await _ctx.Db.SaveChangesAsync();
        _ctx.Db.ChangeTracker.Clear();
        _clock.Now = _clock.Now.AddSeconds(14);
        Assert.Equal((0, 240), Read(await provider.GetAsync(None)));
        _clock.Now = _clock.Now.AddSeconds(1);
        Assert.Equal((1, 60), Read(await provider.GetAsync(None)));

        // A local update is visible at once (no TTL wait), with the new version.
        var updated = await Service().UpdateSchedulingAsync(admin, Input(1, "Faster review", (K.DispatchReviewSlaMinutes, 30, null)), None);
        Assert.True(updated.Succeeded, updated.Message);
        Assert.Equal((2, 30), Read(await provider.GetAsync(None)));

        static (int, int) Read(SchedulingPolicySnapshot s) => (s.Version, s.DispatchReviewSlaMinutes);
    }

    [Fact]
    public void Cache_ttl_is_clamped_to_fifteen_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), new SchedulingPolicyCache(TimeSpan.FromMinutes(5)).Ttl);
        Assert.Equal(TimeSpan.Zero, new SchedulingPolicyCache(TimeSpan.FromSeconds(-1)).Ttl);
        Assert.Equal(TimeSpan.FromSeconds(5), new SchedulingPolicyCache(TimeSpan.FromSeconds(5)).Ttl);
    }

    [Fact]
    public async Task A_read_that_started_before_an_invalidation_does_not_cache_the_old_snapshot()
    {
        var cache = new SchedulingPolicyCache(TimeSpan.FromSeconds(15));
        Assert.False(cache.TryGet(_clock.Now, out _, out var generation));
        cache.Invalidate();
        cache.Set(new SchedulingPolicySnapshot(0, SchedulingPolicyDefinitions.Default.Defaults), _clock.Now, generation);
        Assert.False(cache.TryGet(_clock.Now, out _, out _));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Stored_values_outside_the_current_range_or_with_another_unit_fall_back_to_the_default()
    {
        _ctx.Db.Add(new SystemSetting { Key = K.BufferBetweenAppointmentsMinutes, ValueInt = 999, Unit = SystemSettingUnits.Minutes, Version = 1, UpdatedAt = _clock.Now });
        _ctx.Db.Add(new SystemSetting { Key = K.BookingHorizonDays, ValueInt = 2, Unit = SystemSettingUnits.Hours, Version = 1, UpdatedAt = _clock.Now });
        _ctx.Db.Add(new SystemSetting { Key = "scheduling.retired", ValueInt = 5, Unit = SystemSettingUnits.Minutes, Version = 1, UpdatedAt = _clock.Now });
        await _ctx.Db.SaveChangesAsync();
        _ctx.Db.ChangeTracker.Clear();

        var snapshot = await Provider().GetAsync(None);
        Assert.Equal((30, 30), (snapshot.BufferBetweenAppointmentsMinutes, snapshot.BookingHorizonDays));
        Assert.False(snapshot.Values.ContainsKey("scheduling.retired"));
        var view = await Service().GetSchedulingAsync(None);
        Assert.Equal(SystemSettingSources.Default, view.Settings.Single(s => s.Key == K.BufferBetweenAppointmentsMinutes).Source);
    }

    // ------------------------------------------------------------------ updates

    [Fact]
    public async Task Update_increments_the_version_stamps_changed_keys_and_writes_one_audit_row_per_change()
    {
        var admin = await AdminAsync();
        var initial = await Service().GetSchedulingAsync(None);
        Assert.Equal((0, true, 13), (initial.Version, initial.Bootstrap, initial.Settings.Count));
        Assert.All(initial.Settings, s => Assert.Equal(SystemSettingSources.Default, s.Source));
        Assert.Equal(15, initial.CacheTtlSeconds);

        var first = await Service().UpdateSchedulingAsync(admin, Input(0, "  Pilot  ",
            (K.DispatchReviewSlaMinutes, 180, SystemSettingUnits.Minutes), (K.BookingHorizonDays, 21, "days"), (K.InstallationMaxAssignmentDays, 14, null)), None);
        Assert.True(first.Succeeded, first.Message);
        var view = first.Value!;
        Assert.Equal((1, false, admin), (view.Version, view.Bootstrap, view.UpdatedByUserId));
        var sla = view.Settings.Single(s => s.Key == K.DispatchReviewSlaMinutes);
        Assert.Equal((180, 240, SystemSettingSources.Admin, 1), (sla.Value, sla.DefaultValue, sla.Source, sla.Version));
        // An unchanged value (14 = default) is not stored, versioned or audited.
        Assert.Equal(SystemSettingSources.Default, view.Settings.Single(s => s.Key == K.InstallationMaxAssignmentDays).Source);

        _clock.Now = _clock.Now.AddMinutes(5);
        var second = await Service().UpdateSchedulingAsync(admin, Input(1, "test", (K.DispatchReviewSlaMinutes, 120, null)), None);
        Assert.True(second.Succeeded, second.Message);
        Assert.Equal(2, second.Value!.Version);

        var history = await Service().HistoryAsync(1, 10, null, None);
        Assert.Equal(3, history.TotalItems);
        Assert.Equal([(2, K.DispatchReviewSlaMinutes, 180, 120, "test"), (1, K.DispatchReviewSlaMinutes, 240, 180, "Pilot"), (1, K.BookingHorizonDays, 30, 21, "Pilot")],
            history.Items.Select(r => (r.Version, r.Key, r.OldValue, r.NewValue, r.Reason)));
        Assert.All(history.Items, r => Assert.Equal(admin, r.ChangedByUserId));
        Assert.Equal(SystemSettingUnits.Days, history.Items[2].Unit);

        var oneKey = await Service().HistoryAsync(1, 1, K.DispatchReviewSlaMinutes, None);
        Assert.Equal((2, 2, 1), (oneKey.TotalItems, oneKey.TotalPages, oneKey.Items.Count));

        var stored = await _ctx.Db.Set<SystemSetting>().AsNoTracking().SingleAsync(s => s.Key == K.DispatchReviewSlaMinutes);
        Assert.Equal((120, 2, 2), (stored.ValueInt, stored.Version, stored.Revision));
        var version = await _ctx.Db.Set<SystemSettingsVersion>().AsNoTracking().SingleAsync();
        Assert.Equal((2, 2), (version.Version, version.Revision));
    }

    [Fact]
    public async Task An_update_that_changes_nothing_keeps_the_version_and_writes_no_audit()
    {
        var admin = await AdminAsync();
        var result = await Service().UpdateSchedulingAsync(admin, Input(0, "test", (K.DispatchReviewSlaMinutes, 240, null)), None);
        Assert.True(result.Succeeded);
        Assert.Equal(0, result.Value!.Version);
        Assert.Equal(0, await _ctx.Db.Set<SystemSettingRevision>().CountAsync());
        Assert.Equal(0, await _ctx.Db.Set<SystemSettingsVersion>().CountAsync());
    }

    [Fact]
    public async Task A_stale_expected_version_is_a_version_conflict_and_changes_nothing()
    {
        var admin = await AdminAsync();
        Assert.True((await Service().UpdateSchedulingAsync(admin, Input(0, "test", (K.DispatchReviewSlaMinutes, 200, null)), None)).Succeeded);

        foreach (var stale in new[] { 0, 2, 7 })
        {
            var result = await Service().UpdateSchedulingAsync(admin, Input(stale, "test", (K.DispatchReviewSlaMinutes, 100, null)), None);
            Assert.Equal(SystemSettingErrorCodes.VersionConflict, result.ErrorCode);
            Assert.Equal(1, (int)result.Details!.GetType().GetProperty("currentVersion")!.GetValue(result.Details)!);
        }
        Assert.Equal(200, (await Provider().GetAsync(None)).DispatchReviewSlaMinutes);
        Assert.Equal(1, await _ctx.Db.Set<SystemSettingRevision>().CountAsync());
    }

    [Fact]
    public async Task A_concurrent_writer_detected_at_save_time_is_a_version_conflict()
    {
        var admin = await AdminAsync();
        Assert.True((await Service().UpdateSchedulingAsync(admin, Input(0, "test", (K.DispatchReviewSlaMinutes, 200, null)), None)).Succeeded);

        // Another writer bumps the version row after this one read it (the revision token catches it).
        var interleaving = new InterleavingUnitOfWork(_ctx.Db);
        var result = await Service(interleaving).UpdateSchedulingAsync(admin, Input(1, "test", (K.DispatchReviewSlaMinutes, 100, null)), None);
        Assert.Equal(SystemSettingErrorCodes.VersionConflict, result.ErrorCode);
        Assert.Null(result.Details);
        Assert.Equal(200, (await _ctx.Db.Set<SystemSetting>().AsNoTracking().SingleAsync()).ValueInt);
        Assert.Equal(1, await _ctx.Db.Set<SystemSettingRevision>().CountAsync());
    }

    [Fact]
    public async Task Validation_reports_unknown_keys_units_values_and_rules_without_writing()
    {
        var admin = await AdminAsync();
        var service = Service();

        async Task<IReadOnlyDictionary<string, string[]>> Fails(string code, SchedulingSettingsUpdateInput input)
        {
            var r = await service.UpdateSchedulingAsync(admin, input, None);
            Assert.Equal(code, r.ErrorCode);
            return Errors(r);
        }

        Assert.Contains("scheduling.unknown", (await Fails(SystemSettingErrorCodes.UnknownKey,
            Input(0, "test", ("scheduling.unknown", 5, null), (K.DispatchReviewSlaMinutes, 1, null)))).Keys);
        Assert.Contains(K.BookingHorizonDays, (await Fails(SystemSettingErrorCodes.InvalidUnit, Input(0, "test", (K.BookingHorizonDays, 10, SystemSettingUnits.Minutes)))).Keys);
        Assert.Contains(K.DispatchReviewSlaMinutes, (await Fails(SystemSettingErrorCodes.InvalidValue, Input(0, "test", (K.DispatchReviewSlaMinutes, null, null)))).Keys);
        Assert.Contains(K.DispatchReviewSlaMinutes, (await Fails(SystemSettingErrorCodes.InvalidValue, Input(0, "test", (K.DispatchReviewSlaMinutes, 4, null)))).Keys);
        Assert.Contains(K.BufferBetweenAppointmentsMinutes, (await Fails(SystemSettingErrorCodes.InvalidValue, Input(0, "test", (K.BufferBetweenAppointmentsMinutes, 241, null)))).Keys);
        Assert.Contains(K.MinimumBookingLeadMinutes, (await Fails(SystemSettingErrorCodes.RuleViolation,
            Input(0, "test", (K.BookingHorizonDays, 1, null), (K.MinimumBookingLeadMinutes, 1440, null)))).Keys);
        Assert.Contains("settings", (await Fails(SystemSettingErrorCodes.ValidationFailed, Input(0, null))).Keys);
        Assert.Contains("settings", (await Fails(SystemSettingErrorCodes.ValidationFailed,
            Input(0, "test", (K.DispatchReviewSlaMinutes, 100, null), (K.DispatchReviewSlaMinutes, 120, null)))).Keys);
        Assert.Contains("expectedVersion", (await Fails(SystemSettingErrorCodes.ValidationFailed, Input(null, "test", (K.DispatchReviewSlaMinutes, 100, null)))).Keys);
        Assert.Contains("reason", (await Fails(SystemSettingErrorCodes.ValidationFailed, Input(0, new string('r', 501), (K.DispatchReviewSlaMinutes, 100, null)))).Keys);
        foreach (var missing in new[] { null, "", "   " })
            Assert.Contains("reason", (await Fails(SystemSettingErrorCodes.ValidationFailed, Input(0, missing, (K.DispatchReviewSlaMinutes, 100, null)))).Keys);

        Assert.Equal(0, await _ctx.Db.Set<SystemSetting>().CountAsync());
        Assert.Equal(0, await _ctx.Db.Set<SystemSettingRevision>().CountAsync());
    }

    [Fact]
    public async Task Cross_field_rules_apply_to_the_merged_values_including_stored_ones()
    {
        var admin = await AdminAsync();
        Assert.True((await Service().UpdateSchedulingAsync(admin, Input(0, "test", (K.BookingHorizonDays, 1, null)), None)).Succeeded);

        // The stored 1-day horizon makes a 1-day minimum lead invalid even though only the lead is submitted.
        var lead = await Service().UpdateSchedulingAsync(admin, Input(1, "test", (K.MinimumBookingLeadMinutes, 1440, null)), None);
        Assert.Equal(SystemSettingErrorCodes.RuleViolation, lead.ErrorCode);
        Assert.Contains(K.MinimumBookingLeadMinutes, Errors(lead).Keys);

        // Changing both together in one request is accepted.
        var both = await Service().UpdateSchedulingAsync(admin, Input(1, "test", (K.BookingHorizonDays, 2, null), (K.MinimumBookingLeadMinutes, 1440, null)), None);
        Assert.True(both.Succeeded, both.Message);
        Assert.Equal(2, both.Value!.Version);
    }

    /// <summary>Bumps the version row's revision right after the lock/read, as a concurrent writer committing first would.</summary>
    private sealed class InterleavingUnitOfWork(AppDbContext db) : ISystemSettingsUnitOfWork
    {
        private readonly SystemSettingsUnitOfWork _inner = new(db);

        public async Task<SystemSettingsVersion?> LockVersionAsync(CancellationToken cancellationToken)
        {
            var row = await _inner.LockVersionAsync(cancellationToken);
            await db.Database.ExecuteSqlRawAsync("UPDATE system_settings_version SET revision = revision + 1", cancellationToken);
            return row;
        }

        public Task<IReadOnlyList<SystemSetting>> GetSettingsAsync(bool track, CancellationToken cancellationToken) => _inner.GetSettingsAsync(track, cancellationToken);
        public Task<SystemSettingsVersion?> FindVersionAsync(CancellationToken cancellationToken) => _inner.FindVersionAsync(cancellationToken);
        public Task<(IReadOnlyList<SystemSettingRevision> Items, int Total)> ListRevisionsAsync(string? key, int skip, int take, CancellationToken cancellationToken)
            => _inner.ListRevisionsAsync(key, skip, take, cancellationToken);
        public void Add<TEntity>(TEntity entity) where TEntity : class => _inner.Add(entity);
        public Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<(T Result, bool Commit)>> work, CancellationToken cancellationToken)
            => _inner.ExecuteInTransactionAsync(work, cancellationToken);
        public Task SaveChangesAsync(CancellationToken cancellationToken) => _inner.SaveChangesAsync(cancellationToken);
    }
}
