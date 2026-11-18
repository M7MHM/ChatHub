using ValidationException = ChatHub.Application.Common.Exceptions.ValidationException;
using ChatHub.Application.Features.Users.Queries;
using ChatHub.Application.IntegrationTests.Infrastructure;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace ChatHub.Application.IntegrationTests.Features;

public sealed class ValidationPipelineTests(DatabaseFixture database) : AppTestBase(database)
{
    protected override void ConfigureServices(IServiceCollection services) =>
        services.AddScoped<IValidator<SearchUsersQuery>, ExtraSearchUsersQueryValidator>();

    [Fact]
    public async Task Send_TwoValidatorsFailingOnTheSameProperty_ReportsEachFailureExactlyOnce()
    {
        var vera = await CreateUserAsync("Vera");

        var exception = await Should.ThrowAsync<ValidationException>(() => SendAsync(vera, new SearchUsersQuery("!")));

        exception.Errors["Query"].Length.ShouldBe(2);
        exception.Errors["Query"].Distinct().Count().ShouldBe(2);
    }

    private sealed class ExtraSearchUsersQueryValidator : AbstractValidator<SearchUsersQuery>
    {
        public ExtraSearchUsersQueryValidator() =>
            RuleFor(x => x.Query).NotEqual("!").WithMessage("Extra validator rule.");
    }
}
