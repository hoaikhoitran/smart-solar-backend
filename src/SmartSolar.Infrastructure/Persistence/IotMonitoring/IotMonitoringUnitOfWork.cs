using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.IotMonitoring;

namespace SmartSolar.Infrastructure.Persistence.IotMonitoring;

/// <summary>
/// IoT persistence over the shared context. Uniqueness that matters under concurrency lives in the database: one device id, one
/// ACTIVE binding per device and per installation project (partial unique indexes). Readings are read through the (binding, received_at) index.
/// </summary>
public sealed class IotMonitoringUnitOfWork(AppDbContext db) : TransactionalUnitOfWork(db), IIotMonitoringUnitOfWork
{
    private IQueryable<IotDevice> Devices(bool track) => track ? Db.IotDevices : Db.IotDevices.AsNoTracking();
    private IQueryable<IotDeviceBinding> Bindings(bool track) => track ? Db.IotDeviceBindings : Db.IotDeviceBindings.AsNoTracking();

    public Task<IotDevice?> FindDeviceAsync(Guid id, bool track, CancellationToken cancellationToken)
        => Devices(track).FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

    public Task<IotDevice?> FindDeviceByCodeAsync(string deviceId, bool track, CancellationToken cancellationToken)
        => Devices(track).FirstOrDefaultAsync(d => d.DeviceId == deviceId, cancellationToken);

    public async Task<(IReadOnlyList<IotDevice> Items, int Total)> ListDevicesAsync(string? status, string? search, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = Db.IotDevices.AsNoTracking();
        if (status is not null) query = query.Where(d => d.Status == status);
        if (search is not null)
        {
            var upper = search.ToUpperInvariant();
            var lower = search.ToLowerInvariant();
            query = query.Where(d => d.DeviceId.Contains(upper) || d.DisplayName.ToLower().Contains(lower));
        }
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(d => d.CreatedAt).ThenBy(d => d.DeviceId).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return (items, total);
    }

    public Task TouchDeviceAsync(Guid deviceId, DateTimeOffset seenAt, CancellationToken cancellationToken)
        => Db.IotDevices.Where(d => d.Id == deviceId).ExecuteUpdateAsync(s => s.SetProperty(d => d.LastSeenAt, seenAt), cancellationToken);

    public Task<IotProjectInfo?> FindProjectAsync(Guid projectId, CancellationToken cancellationToken)
        => Db.InstallationProjects.AsNoTracking().Where(p => p.Id == projectId)
            .Select(p => new IotProjectInfo(p.Id, p.CustomerUserId, p.Status, p.HandoverAt)).FirstOrDefaultAsync(cancellationToken);

    public Task<IotDeviceBinding?> FindBindingAsync(Guid bindingId, bool track, CancellationToken cancellationToken)
        => Bindings(track).FirstOrDefaultAsync(b => b.Id == bindingId, cancellationToken);

    public Task<IotDeviceBinding?> GetActiveBindingForDeviceAsync(Guid deviceId, bool track, CancellationToken cancellationToken)
        => Bindings(track).FirstOrDefaultAsync(b => b.DeviceId == deviceId && b.Status == IotBindingStatuses.Active, cancellationToken);

    public Task<IotDeviceBinding?> GetActiveBindingForProjectAsync(Guid projectId, bool track, CancellationToken cancellationToken)
        => Bindings(track).FirstOrDefaultAsync(b => b.ProjectId == projectId && b.Status == IotBindingStatuses.Active, cancellationToken);

    public async Task<IReadOnlyList<IotDeviceBinding>> ListBindingsAsync(Guid? projectId, Guid? deviceId, string? status, CancellationToken cancellationToken)
    {
        var query = Db.IotDeviceBindings.AsNoTracking();
        if (projectId is { } p) query = query.Where(b => b.ProjectId == p);
        if (deviceId is { } d) query = query.Where(b => b.DeviceId == d);
        if (status is not null) query = query.Where(b => b.Status == status);
        return await query.OrderByDescending(b => b.BoundAt).ThenBy(b => b.Id).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<IotDeviceBinding>> ListProjectBindingsAsync(Guid projectId, CancellationToken cancellationToken)
        => await Db.IotDeviceBindings.AsNoTracking().Where(b => b.ProjectId == projectId).OrderBy(b => b.BoundAt).ThenBy(b => b.Id).ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, IotDevice>> GetDevicesAsync(IReadOnlyCollection<Guid> deviceIds, CancellationToken cancellationToken)
        => deviceIds.Count == 0
            ? new Dictionary<Guid, IotDevice>()
            : await Db.IotDevices.AsNoTracking().Where(d => deviceIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, cancellationToken);

    public Task<IotTelemetryReading?> GetLatestReadingAsync(IReadOnlyCollection<Guid> bindingIds, CancellationToken cancellationToken)
        => Db.IotTelemetryReadings.AsNoTracking().Where(r => bindingIds.Contains(r.BindingId))
            .OrderByDescending(r => r.ReceivedAt).ThenByDescending(r => r.Id).FirstOrDefaultAsync(cancellationToken);

    public async Task<IotReadingPage> ListReadingsAsync(IReadOnlyCollection<Guid> bindingIds, DateTimeOffset? from, DateTimeOffset? to, DateTimeOffset? before, int limit,
        CancellationToken cancellationToken)
    {
        var query = Db.IotTelemetryReadings.AsNoTracking().Where(r => bindingIds.Contains(r.BindingId));
        if (from is { } f) query = query.Where(r => r.ReceivedAt >= f);
        if (to is { } t) query = query.Where(r => r.ReceivedAt <= t);
        if (before is { } b) query = query.Where(r => r.ReceivedAt < b);
        var rows = await query.OrderByDescending(r => r.ReceivedAt).ThenByDescending(r => r.Id).Take(limit + 1).ToListAsync(cancellationToken);
        var hasMore = rows.Count > limit;
        var items = hasMore ? rows.Take(limit).ToList() : rows;
        return new IotReadingPage(items, hasMore, hasMore ? items[^1].ReceivedAt : null);
    }

    public void Add<TEntity>(TEntity entity) where TEntity : class => Db.Add(entity);
}
