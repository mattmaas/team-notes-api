using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TeamNotes.Api.Data;
using TeamNotes.Api.Domain;

namespace TeamNotes.Api.Tests;

public class PersistenceTests
{
    [Fact]
    public void Startup_FreshDatabase_AppliesAllMigrations()
    {
        using var factory = new TeamNotesFactory();
        using var db = factory.CreateDbContext();

        db.Database.GetPendingMigrations().Should().BeEmpty();
        db.Database.GetAppliedMigrations().Should().NotBeEmpty();
    }

    [Fact]
    public void Startup_SeedEnabled_InsertsDemoData()
    {
        using var factory = new TeamNotesFactory();
        using var db = factory.CreateDbContext();

        db.Users.Count().Should().Be(3);
        db.Teams.Count().Should().Be(2);
        db.Memberships.Count().Should().Be(4);
        db.Notes.Count().Should().Be(2);
    }

    [Fact]
    public void Startup_SeedDisabled_LeavesTablesEmpty()
    {
        using var factory = new TeamNotesFactory(databasePath: null, seedData: false);
        using var db = factory.CreateDbContext();

        db.Users.Should().BeEmpty();
        db.Notes.Should().BeEmpty();
    }

    [Fact]
    public void Seed_RunTwice_DoesNotDuplicateOrModifyRows()
    {
        using var factory = new TeamNotesFactory();
        using (var db = factory.CreateDbContext())
        {
            var welcome = db.Notes.Single(n => n.Id == DevSeeder.ApolloWelcomeNoteId);
            welcome.Title = "Edited by a user";
            db.SaveChanges();
        }

        using (var db = factory.CreateDbContext())
        {
            DevSeeder.Seed(db);
        }

        using var verify = factory.CreateDbContext();
        verify.Users.Count().Should().Be(3);
        verify.Memberships.Count().Should().Be(4);
        verify.Notes.Count().Should().Be(2);
        verify.Notes.Single(n => n.Id == DevSeeder.ApolloWelcomeNoteId).Title.Should().Be("Edited by a user");
    }

    [Fact]
    public void Startup_RestartWithExistingDatabase_PreservesData()
    {
        var path = TeamNotesFactory.NewDatabasePath();
        var userNoteId = Guid.NewGuid();
        try
        {
            using (var firstRun = new TeamNotesFactory(path))
            using (var db = firstRun.CreateDbContext())
            {
                db.Notes.Add(NewNote(userNoteId, "Written before restart"));
                var welcome = db.Notes.Single(n => n.Id == DevSeeder.ApolloWelcomeNoteId);
                welcome.Title = "Edited before restart";
                db.SaveChanges();
            }

            using var secondRun = new TeamNotesFactory(path, seedData: true);
            using var verify = secondRun.CreateDbContext();
            verify.Notes.Single(n => n.Id == userNoteId).Title.Should().Be("Written before restart");
            verify.Notes.Single(n => n.Id == DevSeeder.ApolloWelcomeNoteId).Title.Should().Be("Edited before restart");
            verify.Notes.Count().Should().Be(3);
        }
        finally
        {
            TeamNotesFactory.DeleteDatabaseFile(path);
        }
    }

    [Fact]
    public void SaveChanges_DuplicateMembership_IsRejectedByPrimaryKey()
    {
        using var factory = new TeamNotesFactory();
        using var db = factory.CreateDbContext();

        db.Memberships.Add(new Membership { UserId = DevSeeder.AliceId, TeamId = DevSeeder.ApolloTeamId, Role = TeamRole.Member });

        var act = () => db.SaveChanges();
        act.Should().Throw<DbUpdateException>().WithInnerException<Microsoft.Data.Sqlite.SqliteException>()
            .Where(e => e.Message.Contains("UNIQUE"));
    }

    [Fact]
    public void SaveChanges_NoteForUnknownTeam_IsRejectedByForeignKey()
    {
        using var factory = new TeamNotesFactory();
        using var db = factory.CreateDbContext();

        var note = NewNote(Guid.NewGuid(), "Orphan");
        note.TeamId = Guid.NewGuid();
        db.Notes.Add(note);

        var act = () => db.SaveChanges();
        act.Should().Throw<DbUpdateException>().WithInnerException<Microsoft.Data.Sqlite.SqliteException>()
            .Where(e => e.Message.Contains("FOREIGN KEY"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SaveChanges_BlankTitle_IsRejectedByCheckConstraint(string title)
    {
        using var factory = new TeamNotesFactory();
        using var db = factory.CreateDbContext();

        db.Notes.Add(NewNote(Guid.NewGuid(), title));

        var act = () => db.SaveChanges();
        act.Should().Throw<DbUpdateException>().WithInnerException<Microsoft.Data.Sqlite.SqliteException>()
            .Where(e => e.Message.Contains("CK_Notes_Title"));
    }

    [Fact]
    public void SaveChanges_TitleOverMaxLength_IsRejectedByCheckConstraint()
    {
        using var factory = new TeamNotesFactory();
        using var db = factory.CreateDbContext();

        db.Notes.Add(NewNote(Guid.NewGuid(), new string('x', Note.TitleMaxLength + 1)));

        var act = () => db.SaveChanges();
        act.Should().Throw<DbUpdateException>().WithInnerException<Microsoft.Data.Sqlite.SqliteException>()
            .Where(e => e.Message.Contains("CK_Notes_Title"));
    }

    [Fact]
    public void SaveChanges_UnknownVisibilityViaSql_IsRejectedByCheckConstraint()
    {
        using var factory = new TeamNotesFactory();
        using var db = factory.CreateDbContext();

        var act = () => db.Database.ExecuteSqlRaw(
            "UPDATE Notes SET Visibility = 'Public' WHERE Id = {0}", DevSeeder.ApolloWelcomeNoteId);

        act.Should().Throw<Microsoft.Data.Sqlite.SqliteException>().Where(e => e.Message.Contains("CK_Notes_Visibility"));
    }

    [Fact]
    public void SaveChanges_StaleVersion_ThrowsConcurrencyException()
    {
        using var factory = new TeamNotesFactory();
        using var first = factory.CreateDbContext();
        using var second = factory.CreateDbContext();

        var firstCopy = first.Notes.Single(n => n.Id == DevSeeder.ApolloWelcomeNoteId);
        var secondCopy = second.Notes.Single(n => n.Id == DevSeeder.ApolloWelcomeNoteId);

        firstCopy.Title = "First writer";
        firstCopy.Version++;
        first.SaveChanges();

        secondCopy.Title = "Second writer";
        secondCopy.Version++;
        var act = () => second.SaveChanges();

        act.Should().Throw<DbUpdateConcurrencyException>();
    }

    private static Note NewNote(Guid id, string title) => new()
    {
        Id = id,
        TeamId = DevSeeder.ApolloTeamId,
        AuthorId = DevSeeder.BobId,
        Title = title,
        Body = "",
        Visibility = NoteVisibility.Team,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };
}
