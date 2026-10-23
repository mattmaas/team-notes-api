using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TeamNotes.Api.Data;

namespace TeamNotes.Api.Tests;

/// <summary>
/// Hosts the real application against its own SQLite file, so every test class gets an isolated database
/// that uses the same provider, migrations and constraints as production. Pass an existing path to simulate a restart.
/// </summary>
public sealed class TeamNotesFactory : WebApplicationFactory<Program>
{
    private readonly bool _ownsDatabaseFile;
    private readonly bool _seedData;

    public TeamNotesFactory(string? databasePath = null, bool seedData = true)
    {
        _ownsDatabaseFile = databasePath is null;
        DatabasePath = databasePath ?? NewDatabasePath();
        _seedData = seedData;
    }

    public string DatabasePath { get; }

    public static string NewDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"teamnotes-test-{Guid.NewGuid():N}.db");

    public NotesDbContext CreateDbContext() =>
        Services.CreateScope().ServiceProvider.GetRequiredService<NotesDbContext>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Notes"] = $"Data Source={DatabasePath}",
            ["Demo:SeedData"] = _seedData.ToString(),
            ["Demo:HeaderAuth"] = "true",
        }));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && _ownsDatabaseFile)
        {
            DeleteDatabaseFile(DatabasePath);
        }
    }

    public static void DeleteDatabaseFile(string path)
    {
        // Pooled connections keep the file open on Windows.
        SqliteConnection.ClearAllPools();
        File.Delete(path);
    }
}
