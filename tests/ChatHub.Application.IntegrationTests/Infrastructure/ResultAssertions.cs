using ChatHub.Application.Common.Models;

namespace ChatHub.Application.IntegrationTests.Infrastructure;

public static class ResultAssertions
{
    public static T ShouldSucceed<T>(this Result<T> result)
    {
        result.IsSuccess.ShouldBeTrue($"expected success but got {(result.IsFailure ? result.Error.Code : "")}");
        return result.Value;
    }

    public static void ShouldSucceed(this Result result) =>
        result.IsSuccess.ShouldBeTrue($"expected success but got {(result.IsFailure ? result.Error.Code : "")}");

    public static void ShouldFailWith(this Result result, Error error)
    {
        result.IsFailure.ShouldBeTrue($"expected {error.Code} but the request succeeded");
        result.Error.ShouldBe(error);
    }
}
