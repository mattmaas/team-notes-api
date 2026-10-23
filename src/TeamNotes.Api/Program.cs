using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using TeamNotes.Api.Auth;
using TeamNotes.Api.Data;
using TeamNotes.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);

// The connection string is read when the context is resolved, so test overrides applied at Build() are honoured.
builder.Services.AddDbContext<NotesDbContext>((services, options) =>
{
    var connectionString = services.GetRequiredService<IConfiguration>().GetConnectionString("Notes");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException("Connection string 'ConnectionStrings:Notes' is not configured.");
    }

    options.UseSqlite(connectionString);
});

// Every error response (400/401/404/500) is RFC 7807 ProblemDetails. UseExceptionHandler below returns a generic
// 500 body in every environment; exception details only go to the log.
builder.Services.AddProblemDetails();

// Malformed JSON or route values produce a 400 response instead of an exception.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);

// Enums travel as names ("Private"), and numbers are rejected so undefined values like 7 cannot be bound.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));

builder.Services
    .AddAuthentication(DemoHeaderAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, DemoHeaderAuthenticationHandler>(DemoHeaderAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorization();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    var scheme = new OpenApiSecurityScheme
    {
        Name = DemoHeaderAuthenticationHandler.HeaderName,
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Description = "Development-only demo identity: a seeded user id, e.g. 22222222-2222-2222-2222-222222222222 (Bob).",
        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = DemoHeaderAuthenticationHandler.SchemeName },
    };
    options.AddSecurityDefinition(DemoHeaderAuthenticationHandler.SchemeName, scheme);
    options.AddSecurityRequirement(new OpenApiSecurityRequirement { [scheme] = [] });
});

var app = builder.Build();

InitializeDatabase(app);

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapNoteEndpoints();

app.Run();

// Applies pending migrations; it never drops or recreates the database, so existing data survives restarts.
// Running migrations at startup suits this single-instance demo; with several instances they belong in the deployment pipeline.
static void InitializeDatabase(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<NotesDbContext>();
    db.Database.Migrate();

    // Demo data needs the Development environment and an explicit opt-in, so a production host never seeds users
    // even when the flag is left on. DevSeeder then seeds only a fresh database; see its remarks.
    if (app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("Demo:SeedData"))
    {
        DevSeeder.Seed(db);
    }
}

// Exposes the implicit Program class to WebApplicationFactory<Program> in the test project.
public partial class Program;
