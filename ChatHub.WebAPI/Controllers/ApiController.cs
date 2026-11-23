using ChatHub.Application.Common.Models;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatHub.WebAPI.Controllers;

[ApiController]
[Authorize]
[Route("api")]
public abstract class ApiController(ISender sender) : ControllerBase
{
    protected ISender Sender { get; } = sender;

    protected ActionResult Problem(Error error)
    {
        var status = error.Type switch
        {
            ErrorType.Validation => StatusCodes.Status400BadRequest,
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            ErrorType.Forbidden => StatusCodes.Status403Forbidden,
            ErrorType.TooManyRequests => StatusCodes.Status429TooManyRequests,
            _ => StatusCodes.Status400BadRequest
        };

        var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, status, detail: error.Description);
        problem.Extensions["code"] = error.Code;

        return new ObjectResult(problem) { StatusCode = status };
    }

    protected ActionResult ToOk<T>(Result<T> result) =>
        result.IsSuccess ? Ok(result.Value) : Problem(result.Error);

    protected ActionResult ToAccepted<T>(Result<T> result) =>
        result.IsSuccess ? Accepted(result.Value) : Problem(result.Error);

    protected ActionResult ToNoContent(Result result) =>
        result.IsSuccess ? NoContent() : Problem(result.Error);

    protected ActionResult ToCreated<T>(Result<T> result, Func<T, string> location) =>
        result.IsSuccess ? Created(location(result.Value), result.Value) : Problem(result.Error);

    protected ActionResult ToCreatedOrOk<T>(Result<T> result, Func<T, bool> wasCreated, Func<T, string> location)
    {
        if (result.IsFailure) return Problem(result.Error);
        return wasCreated(result.Value) ? Created(location(result.Value), result.Value) : Ok(result.Value);
    }
}
