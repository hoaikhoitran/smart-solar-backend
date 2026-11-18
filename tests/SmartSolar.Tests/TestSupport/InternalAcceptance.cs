using Microsoft.EntityFrameworkCore;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Installations;

namespace SmartSolar.Tests.TestSupport;

/// <summary>
/// The internal acceptance that replaced the customer's in-app acceptance (2026-11-18): a Manager accepts the reported completion (or returns it to
/// rework) at the project's current revision.
/// </summary>
public static class InternalAcceptance
{
    public static async Task<OperationResult<InstallationStaffView>> AcceptAsync(InstallationWorkflowTestContext ctx, Guid projectId, Guid? managerUserId = null,
        string note = "Handover done on site with the customer; checklist and photos reviewed", Guid? evidenceFileId = null, AppDbContext? db = null)
    {
        var manager = managerUserId ?? (await ctx.Scheduling.Inner.SeedUserAsync("Acceptance manager")).Id;
        var revision = (await (db ?? ctx.Db).InstallationProjects.AsNoTracking().SingleAsync(p => p.Id == projectId)).Revision;
        return await ctx.Installations(db).AcceptInternallyAsync(manager, projectId, revision, note, evidenceFileId, CancellationToken.None);
    }

    public static async Task<OperationResult<InstallationStaffView>> ReworkAsync(InstallationWorkflowTestContext ctx, Guid projectId, string reason,
        Guid? managerUserId = null, AppDbContext? db = null)
    {
        var manager = managerUserId ?? (await ctx.Scheduling.Inner.SeedUserAsync("Acceptance manager")).Id;
        var revision = (await (db ?? ctx.Db).InstallationProjects.AsNoTracking().SingleAsync(p => p.Id == projectId)).Revision;
        return await ctx.Installations(db).ReturnToReworkAsync(manager, projectId, revision, reason, CancellationToken.None);
    }
}
