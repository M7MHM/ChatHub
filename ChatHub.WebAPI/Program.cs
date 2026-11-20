using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatHub.Application;
using ChatHub.Infrastructure;
using ChatHub.Infrastructure.Persistence.Data;
using ChatHub.WebAPI.Extensions;
using ChatHub.WebAPI.Middlewares;
using ChatHub.WebAPI.OpenApi;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddAuthorization();
builder.Services
    .AddControllers(options => options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true)
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer<BearerSecuritySchemeTransformer>();
    options.AddOperationTransformer<BearerSecuritySchemeTransformer>();
});

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    context.ProblemDetails.Extensions.TryAdd("traceId", Activity.Current?.Id ?? context.HttpContext.TraceIdentifier));

builder.Services.AddTrustedForwardedHeaders(builder.Configuration);
builder.Services.AddApiRateLimiting();

const string readyTag = "ready";
builder.Services.AddHealthChecks()
    .AddDbContextCheck<ApplicationDbContext>(tags: [readyTag])
    .AddRedisHealthCheck(builder.Configuration, readyTag);

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options =>
    options.AddPolicy("Client", policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()));

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

if (app.Configuration.GetValue("Https:RedirectEnabled", true))
    app.UseHttpsRedirection();

app.UseCors("Client");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
var readiness = new HealthCheckOptions { Predicate = check => check.Tags.Contains(readyTag) };
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", readiness).AllowAnonymous();
app.MapHealthChecks("/health", readiness).AllowAnonymous();
app.MapHub<ChatHub.Infrastructure.RealTime.Hubs.ChatHub>(ChatHub.Infrastructure.DependencyInjection.ChatHubPath);

app.Run();
