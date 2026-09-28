using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SmartSolar.Modules.Identity.Constants;

namespace SmartSolar.Api.Controllers
{
    [Route("api/pre-surveys")]
    [ApiController]
    [Authorize(Roles = RoleCodes.Customer)]
    public sealed class PreSurveysController : ControllerBase
    {
    }
}
