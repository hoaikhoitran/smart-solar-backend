using System;
using SmartSolar.Modules.PreSurvey.Contracts.Persistence;
using SmartSolar.Modules.PreSurvey.Entities;

namespace SmartSolar.Modules.PreSurvey.CreateCustomerProfile;

public sealed class CreateCustomerProfileHandler
{
    private readonly IPreSurveyUnitOfWork _unitOfWork;
    public CreateCustomerProfileHandler(IPreSurveyUnitOfWork preSurveyUnitOfWork)
    {
        _unitOfWork = preSurveyUnitOfWork;
    }
    public async Task<CreateCustomerProfileResult> HandleAsync(CreateCustomerProfileCommand command,
                                                                 CancellationToken cancellationToken)
    {
        var existingCustomer = await _unitOfWork.FindCustomerByUserIdAsync(command.UserId, cancellationToken);
        if(existingCustomer is not null)
        {
            return CreateCustomerProfileResult.AlreadyExists();
        }
        var newCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            UserId = command.UserId,
            CustomerType = command.CustomerType,
            CompanyName = string.IsNullOrWhiteSpace(command.CompanyName) ? null : command.CompanyName.Trim(),
            TaxCode = string.IsNullOrWhiteSpace(command.TaxCode) ? null : command.TaxCode.Trim(),
            Note = string.IsNullOrWhiteSpace(command.Note) ? null : command.Note.Trim()
        };

        _unitOfWork.AddCustomer(newCustomer);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return CreateCustomerProfileResult.Created(newCustomer.Id);
    }
}
