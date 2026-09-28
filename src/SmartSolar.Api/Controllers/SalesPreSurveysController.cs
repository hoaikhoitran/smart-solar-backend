using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SmartSolar.Modules.Identity.Constants;

namespace SmartSolar.Api.Controllers
{
    [Route("api/sales/pre-surveys")]

    [ApiController]
    [Authorize(Roles = RoleCodes.Sales)]
    public sealed class SalesPreSurveysController : ControllerBase
    {
    }
}
