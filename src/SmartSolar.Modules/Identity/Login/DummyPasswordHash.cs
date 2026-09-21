namespace SmartSolar.Modules.Identity.Login;

/// <summary>
/// A fixed ASP.NET Core Identity V3 hash used only to spend hashing time on
/// login paths that have no real password to check. It corresponds to a random
/// passphrase nobody holds, so it can never verify successfully in practice.
/// </summary>
internal static class DummyPasswordHash
{
    public const string Value =
        "AQAAAAIAAYagAAAAEGm0Y5Qe0mHkQ2H4hR6M3n1J6gHq3pJ2wWQ7sO8kZ1yT4v5x6C7d8E9f0G1h2I3j4A==";
}
