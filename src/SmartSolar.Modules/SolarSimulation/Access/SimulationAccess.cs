using SmartSolar.Modules.SolarSimulation.Contracts.Persistence;

namespace SmartSolar.Modules.SolarSimulation.Access;

/// <summary>Who is asking. Roles come from the JWT; Customer, Sales and Technician can read simulations.</summary>
public sealed record SimulationViewer(Guid UserId, bool IsCustomer, bool IsSales, bool IsTechnician = false);

/// <summary>
/// Read access: the owning customer, the Sales user assigned to the pre-survey's survey request,
/// or a technician officially assigned to that request's active appointment. Everyone else is refused.
/// </summary>
public static class SimulationAccess
{
    public static bool CanRead(SimulationSource source, SimulationViewer viewer)
        => (viewer.IsCustomer && source.OwnerUserId == viewer.UserId)
           || (viewer.IsSales && source.AssignedSaleId is { } assigned && assigned == viewer.UserId)
           || (viewer.IsTechnician && source.AssignedTechnicianUserIds is { } technicians && technicians.Contains(viewer.UserId));
}
