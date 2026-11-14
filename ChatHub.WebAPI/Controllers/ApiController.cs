using ChatHub.Application.Common.Models;
using Microsoft.AspNetCore.Mvc;

namespace ChatHub.WebAPI.Controllers;

[ApiController]
public abstract class ApiController : ControllerBase
{
    protected IActionResult Problem(Error error)
    {
        var status = error.Type switch
        {
            ErrorType.Validation => StatusCodes.Status400BadRequest,
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            ErrorType.Forbidden => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest
        };

        var problem = new ProblemDetails
        {
            Status = status,
            Title = error.Type.ToString(),
            Detail = error.Description
        };
        problem.Extensions["code"] = error.Code;

        return new ObjectResult(problem) { StatusCode = status };
    }

    protected IActionResult ToOk(Result result) => result.IsSuccess ? Ok() : Problem(result.Error);

    protected IActionResult ToOk<T>(Result<T> result) => result.IsSuccess ? Ok(result.Value) : Problem(result.Error);

    protected IActionResult ToNoContent(Result result) => result.IsSuccess ? NoContent() : Problem(result.Error);

    protected IActionResult ToCreated<T>(Result<T> result, Func<T, string> locationFactory) =>
        result.IsSuccess ? Created(locationFactory(result.Value), result.Value) : Problem(result.Error);
}
