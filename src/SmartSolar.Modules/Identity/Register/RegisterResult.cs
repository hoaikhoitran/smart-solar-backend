namespace SmartSolar.Modules.Identity.Register;

public enum RegisterOutcome
{
    Created = 1,
    DuplicateEmail = 2
}

public sealed record RegisterResult(RegisterOutcome Outcome, Guid UserId, string Email)
{
    public static RegisterResult Created(Guid userId, string email)
        => new(RegisterOutcome.Created, userId, email);

    public static RegisterResult DuplicateEmail(string email)
        => new(RegisterOutcome.DuplicateEmail, Guid.Empty, email);
}
