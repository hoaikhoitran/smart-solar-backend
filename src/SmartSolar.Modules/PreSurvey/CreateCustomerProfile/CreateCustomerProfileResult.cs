namespace SmartSolar.Modules.PreSurvey.CreateCustomerProfile;

public enum CreateCustomerProfileOutcome
{
    Created,
    AlreadyExists
}

public sealed record CreateCustomerProfileResult(
    CreateCustomerProfileOutcome Outcome,
    Guid? CustomerId)
{
    public static CreateCustomerProfileResult Created(Guid customerId)
        => new(
            CreateCustomerProfileOutcome.Created,
            customerId);

    public static CreateCustomerProfileResult AlreadyExists()
        => new(
            CreateCustomerProfileOutcome.AlreadyExists,
            null);
}