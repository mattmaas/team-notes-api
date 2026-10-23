using Microsoft.EntityFrameworkCore;
using TeamNotes.Api.Data;

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

var app = builder.Build();

InitializeDatabase(app);

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

// Applies pending migrations; it never drops or recreates the database, so existing data survives restarts.
// Running migrations at startup suits this single-instance demo; with several instances they belong in the deployment pipeline.
static void InitializeDatabase(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<NotesDbContext>();
    db.Database.Migrate();

    if (app.Configuration.GetValue<bool>("Demo:SeedData"))
    {
        DevSeeder.Seed(db);
    }
}

// Exposes the implicit Program class to WebApplicationFactory<Program> in the test project.
public partial class Program;
