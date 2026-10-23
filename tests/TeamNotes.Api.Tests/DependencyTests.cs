using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace TeamNotes.Api.Tests;

public class DependencyTests
{
    // CVE-2025-6965 (GHSA-2m69-gcr7-jv3q) affects SQLite engines before 3.50.2. Guards against a package
    // downgrade silently bringing the vulnerable native engine back.
    [Fact]
    public void SqliteEngine_BundledNativeLibrary_IsPatchedForCve20256965()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version()";

        var version = Version.Parse((string)command.ExecuteScalar()!);

        version.Should().BeGreaterThanOrEqualTo(new Version(3, 50, 2));
    }
}
