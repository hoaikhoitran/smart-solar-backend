using SmartSolar.Modules.SolarSimulation.Contracts.Persistence;

namespace SmartSolar.Modules.SolarSimulation.Access;

/// <summary>Who is asking. Roles come from the JWT; only Customer and Sales can read simulations.</summary>
public sealed record SimulationViewer(Guid UserId, bool IsCustomer, bool IsSales);

/// <summary>
/// Read access: the owning customer, or the Sales user assigned to the pre-survey's survey
/// request. Unassigned or other Sales users and other customers are refused.
/// </summary>
public static class SimulationAccess
{
    public static bool CanRead(SimulationSource source, SimulationViewer viewer)
        => (viewer.IsCustomer && source.OwnerUserId == viewer.UserId)
           || (viewer.IsSales && source.AssignedSaleId is { } assigned && assigned == viewer.UserId);
}
