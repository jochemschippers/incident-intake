using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Incidents.Api;

// ponytail: shared API key for the read endpoints. Production upgrade: App Service Easy Auth / Entra ID tokens.
public class ApiKeyFilter(IConfiguration config) : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext ctx)
    {
        var expected = config["Api:ReadKey"];
        var given = ctx.HttpContext.Request.Headers["X-Api-Key"].ToString();
        if (string.IsNullOrEmpty(expected) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(given)))
            ctx.Result = new UnauthorizedResult();
    }

    public void OnActionExecuted(ActionExecutedContext ctx) { }
}
