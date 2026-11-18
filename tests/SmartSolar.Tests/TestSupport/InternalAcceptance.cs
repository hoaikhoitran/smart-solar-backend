using Microsoft.EntityFrameworkCore;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Installations;

namespace SmartSolar.Tests.TestSupport;

/// <summary>
/// Acceptance of a reported completion. Since 2026-11-19 only the CUSTOMER accepts (the Manager's internal acceptance was retired); the Manager can
/// still return a report to rework. <see cref="AcceptAsync"/> records the project customer's ACCEPT at the project's current revision (the manager
/// argument is kept for source compatibility and ignored).
/// </summary>
public static class InternalAcceptance
{
    public static async Task<OperationResult<InstallationCustomerView>> AcceptAsync(InstallationWorkflowTestContext ctx, Guid projectId, Guid? managerUserId = null,
        string note = "Handover done on site; checked panels and inverter", Guid? evidenceFileId = null, AppDbContext? db = null)
    {
        var project = await (db ?? ctx.Db).InstallationProjects.AsNoTracking().SingleAsync(p => p.Id == projectId);
        return await ctx.Installations(db).DecideAcceptanceAsync(project.CustomerUserId, projectId,
            new CustomerAcceptanceInput(CustomerAcceptanceDecisions.Accept, note, null, project.Revision), CancellationToken.None);
    }

    public static async Task<OperationResult<InstallationStaffView>> ReworkAsync(InstallationWorkflowTestContext ctx, Guid projectId, string reason,
        Guid? managerUserId = null, AppDbContext? db = null)
    {
        var manager = managerUserId ?? (await ctx.Scheduling.Inner.SeedUserAsync("Acceptance manager")).Id;
        var revision = (await (db ?? ctx.Db).InstallationProjects.AsNoTracking().SingleAsync(p => p.Id == projectId)).Revision;
        return await ctx.Installations(db).ReturnToReworkAsync(manager, projectId, revision, reason, CancellationToken.None);
    }
}
