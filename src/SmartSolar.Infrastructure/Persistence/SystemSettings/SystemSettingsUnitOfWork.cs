using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.SystemSettings;

namespace SmartSolar.Infrastructure.Persistence.SystemSettings;

public sealed class SystemSettingsUnitOfWork(AppDbContext db) : TransactionalUnitOfWork(db), ISystemSettingsUnitOfWork
{
    public async Task<IReadOnlyList<SystemSetting>> GetSettingsAsync(bool track, CancellationToken cancellationToken)
        => await (track ? Db.Set<SystemSetting>() : Db.Set<SystemSetting>().AsNoTracking()).ToListAsync(cancellationToken);

    public async Task<SystemSettingsVersion?> LockVersionAsync(CancellationToken cancellationToken)
    {
        if (Db.Database.IsNpgsql())
            await Db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM system_settings_version WHERE id = {SystemSettingsVersion.SingletonId} FOR UPDATE", cancellationToken);
        return await Db.Set<SystemSettingsVersion>().FirstOrDefaultAsync(v => v.Id == SystemSettingsVersion.SingletonId, cancellationToken);
    }

    public Task<SystemSettingsVersion?> FindVersionAsync(CancellationToken cancellationToken)
        => Db.Set<SystemSettingsVersion>().AsNoTracking().FirstOrDefaultAsync(v => v.Id == SystemSettingsVersion.SingletonId, cancellationToken);

    public async Task<(IReadOnlyList<SystemSettingRevision> Items, int Total)> ListRevisionsAsync(string? key, int skip, int take, CancellationToken cancellationToken)
    {
        var query = Db.Set<SystemSettingRevision>().AsNoTracking().Where(r => key == null || r.Key == key);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(r => r.Version).ThenBy(r => r.Key).Skip(skip).Take(take).ToListAsync(cancellationToken);
        return (items, total);
    }

    public void Add<TEntity>(TEntity entity) where TEntity : class => Db.Set<TEntity>().Add(entity);
}
