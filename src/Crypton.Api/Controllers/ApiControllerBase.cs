using Crypton.Api.Infrastructure;
using Crypton.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Crypton.Api.Controllers;

[ApiController]
[Authorize]
[Produces("application/json")]
public abstract class ApiControllerBase : ControllerBase
{
    protected Guid CurrentUserId => User.UserId();

    protected static PageRequest Page(int page, int pageSize) => new(page, pageSize);
}
