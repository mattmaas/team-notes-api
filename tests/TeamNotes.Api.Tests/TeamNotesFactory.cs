using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using TeamNotes.Api.Data;

namespace TeamNotes.Api.Tests;

/// <summary>
/// Hosts the real application against its own SQLite file, so every test class gets an isolated database
/// that uses the same provider, migrations and constraints as production. Pass an existing path to simulate a restart,
/// and an environment to run the host outside Development.
/// </summary>
public sealed class TeamNotesFactory : WebApplicationFactory<Program>
{
    private readonly bool _ownsDatabaseFile;
    private readonly bool _seedData;
    private readonly string _environment;
    private readonly bool _headerAuth;
    private readonly int? _rateLimitPermits;

    public TeamNotesFactory(string? databasePath = null, bool seedData = true, string environment = "Development", bool headerAuth = true, int? rateLimitPermits = null)
    {
        _ownsDatabaseFile = databasePath is null;
        DatabasePath = databasePath ?? NewDatabasePath();
        _seedData = seedData;
        _environment = environment;
        _headerAuth = headerAuth;
        _rateLimitPermits = rateLimitPermits;
    }

    public string DatabasePath { get; }

    // Pooling is off per factory: a pooled handle would keep the temp file locked on Windows.
    private string ConnectionString => $"Data Source={DatabasePath};Pooling=False";

    public static string NewDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"teamnotes-test-{Guid.NewGuid():N}.db");

    /// <summary>
    /// Returns a context the caller owns and disposes. It is built outside DI on purpose: EF captures the resolving
    /// scope's service provider in <see cref="DbContextOptions"/>, so a context obtained from a short-lived scope
    /// throws once that scope is disposed, while keeping the scope alive would leak it into every test.
    /// </summary>
    public NotesDbContext CreateDbContext()
    {
        // Reading Services starts the host, which applies the migrations (and seeds a fresh database).
        _ = Services;

        return new(new DbContextOptionsBuilder<NotesDbContext>().UseSqlite(ConnectionString).Options);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Notes"] = ConnectionString,
            ["Demo:SeedData"] = _seedData.ToString(),
            ["Demo:HeaderAuth"] = _headerAuth.ToString(),
            // Tests fire many requests per user; a large default keeps the limiter out of the way unless a test sets one.
            ["RateLimit:PermitLimit"] = (_rateLimitPermits ?? 100_000).ToString(),
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
        // Only this factory's files: the connection strings disable pooling, so nothing else holds them open.
        foreach (var file in new[] { path, $"{path}-wal", $"{path}-shm", $"{path}-journal" })
        {
            File.Delete(file);
        }
    }
}
