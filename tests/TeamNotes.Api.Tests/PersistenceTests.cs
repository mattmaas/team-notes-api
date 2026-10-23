using FluentAssertions;
using Microsoft.Data.Sqlite;
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
        db.Memberships.Should().BeEmpty();
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
    public void Seed_NoteInsertFails_RollsBackEntireBootstrap()
    {
        using var factory = new TeamNotesFactory(seedData: false);
        using (var db = factory.CreateDbContext())
        {
            // The trigger fails the notes insert, which happens after the seed has already saved its users, teams
            // and memberships; whatever survives therefore came from the seed's own transaction.
            db.Database.ExecuteSqlRaw(
                "CREATE TRIGGER FailDemoNotes BEFORE INSERT ON Notes BEGIN SELECT RAISE(ABORT, 'seed failure'); END;");

            var act = () => DevSeeder.Seed(db);
            act.Should().Throw<DbUpdateException>().WithInnerException<SqliteException>()
                .Where(e => e.Message.Contains("seed failure"));
        }

        using var verify = factory.CreateDbContext();
        verify.Users.Should().BeEmpty();
        verify.Teams.Should().BeEmpty();
        verify.Memberships.Should().BeEmpty();
        verify.Notes.Should().BeEmpty();
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
    public void Startup_ProductionEnvironmentWithSeedFlagEnabled_SeedsNothing()
    {
        using var factory = new TeamNotesFactory(seedData: true, environment: "Production");
        using var db = factory.CreateDbContext();

        db.Users.Should().BeEmpty();
        db.Teams.Should().BeEmpty();
        db.Memberships.Should().BeEmpty();
        db.Notes.Should().BeEmpty();
    }

    [Fact]
    public void Startup_DatabaseWithExistingUser_IsNotBootstrapped()
    {
        var path = TeamNotesFactory.NewDatabasePath();
        var userId = Guid.NewGuid();
        try
        {
            using (var firstRun = new TeamNotesFactory(path, seedData: false))
            using (var db = firstRun.CreateDbContext())
            {
                db.Users.Add(new User { Id = userId, Name = "Real user" });
                db.SaveChanges();
            }

            using var secondRun = new TeamNotesFactory(path, seedData: true);
            using var verify = secondRun.CreateDbContext();
            verify.Users.Select(u => u.Id).Should().BeEquivalentTo(new[] { userId });
            verify.Teams.Should().BeEmpty();
            verify.Memberships.Should().BeEmpty();
            verify.Notes.Should().BeEmpty();
        }
        finally
        {
            TeamNotesFactory.DeleteDatabaseFile(path);
        }
    }

    [Fact]
    public void Startup_RestartAfterDemoNoteDeleted_DoesNotRecreateIt()
    {
        var path = TeamNotesFactory.NewDatabasePath();
        try
        {
            using (var firstRun = new TeamNotesFactory(path))
            using (var db = firstRun.CreateDbContext())
            {
                db.Notes.Remove(db.Notes.Single(n => n.Id == DevSeeder.ApolloWelcomeNoteId));
                db.SaveChanges();
            }

            using var secondRun = new TeamNotesFactory(path, seedData: true);
            using var verify = secondRun.CreateDbContext();
            verify.Notes.Select(n => n.Id).Should().BeEquivalentTo(new[] { DevSeeder.AlicePrivateNoteId });
            verify.Users.Count().Should().Be(3);
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
        var error = act.Should().Throw<DbUpdateException>().WithInnerException<SqliteException>().Which;
        error.SqliteErrorCode.Should().Be(19);            // SQLITE_CONSTRAINT
        error.SqliteExtendedErrorCode.Should().Be(1555);  // SQLITE_CONSTRAINT_PRIMARYKEY

        // The rejected insert must not have promoted or demoted the existing membership.
        using var verify = factory.CreateDbContext();
        verify.Memberships
            .Single(m => m.UserId == DevSeeder.AliceId && m.TeamId == DevSeeder.ApolloTeamId)
            .Role.Should().Be(TeamRole.Owner);
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
        act.Should().Throw<DbUpdateException>().WithInnerException<SqliteException>()
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
        act.Should().Throw<DbUpdateException>().WithInnerException<SqliteException>()
            .Where(e => e.Message.Contains("CK_Notes_Title"));
    }

    [Fact]
    public void SaveChanges_TitleAtMaxLength_IsAccepted()
    {
        using var factory = new TeamNotesFactory();
        using var db = factory.CreateDbContext();
        var id = Guid.NewGuid();

        db.Notes.Add(NewNote(id, new string('x', 200)));
        db.SaveChanges();

        using var verify = factory.CreateDbContext();
        verify.Notes.Single(n => n.Id == id).Title.Length.Should().Be(200);
    }

    [Fact]
    public void SaveChanges_TitleOverMaxLength_IsRejectedByCheckConstraint()
    {
        using var factory = new TeamNotesFactory();
        using var db = factory.CreateDbContext();

        db.Notes.Add(NewNote(Guid.NewGuid(), new string('x', 201)));

        var act = () => db.SaveChanges();
        act.Should().Throw<DbUpdateException>().WithInnerException<SqliteException>()
            .Where(e => e.Message.Contains("CK_Notes_Title"));
    }

    [Fact]
    public void SaveChanges_UnknownVisibilityViaSql_IsRejectedByCheckConstraint()
    {
        using var factory = new TeamNotesFactory();
        using var db = factory.CreateDbContext();

        var act = () => db.Database.ExecuteSqlRaw(
            "UPDATE Notes SET Visibility = 'Public' WHERE Id = {0}", DevSeeder.ApolloWelcomeNoteId);

        act.Should().Throw<SqliteException>().Where(e => e.Message.Contains("CK_Notes_Visibility"));
    }

    [Fact]
    public void SaveChanges_InvalidRoleViaSql_IsRejectedByCheckConstraint()
    {
        using var factory = new TeamNotesFactory();
        using var db = factory.CreateDbContext();

        var act = () => db.Database.ExecuteSqlRaw(
            "UPDATE Memberships SET Role = 'Admin' WHERE UserId = {0} AND TeamId = {1}",
            DevSeeder.AliceId, DevSeeder.ApolloTeamId);

        act.Should().Throw<SqliteException>().Where(e => e.Message.Contains("CK_Memberships_Role"));
    }

    [Fact]
    public void SaveChanges_VersionZeroViaSql_IsRejectedByCheckConstraint()
    {
        using var factory = new TeamNotesFactory();
        using var db = factory.CreateDbContext();

        var act = () => db.Database.ExecuteSqlRaw(
            "UPDATE Notes SET Version = 0 WHERE Id = {0}", DevSeeder.ApolloWelcomeNoteId);

        act.Should().Throw<SqliteException>().Where(e => e.Message.Contains("CK_Notes_Version"));
    }

    [Fact]
    public void SaveChanges_DeleteTeamWithNotes_IsRejectedByForeignKey()
    {
        using var factory = new TeamNotesFactory();
        using var db = factory.CreateDbContext();

        var act = () => db.Database.ExecuteSqlRaw("DELETE FROM Teams WHERE Id = {0}", DevSeeder.ApolloTeamId);

        act.Should().Throw<SqliteException>().Where(e => e.Message.Contains("FOREIGN KEY"));
    }

    [Fact]
    public void SaveChanges_NoteWithUnknownAuthor_IsRejectedByForeignKey()
    {
        using var factory = new TeamNotesFactory();
        using var db = factory.CreateDbContext();

        var note = NewNote(Guid.NewGuid(), "Ghost writer");
        note.AuthorId = Guid.NewGuid();
        db.Notes.Add(note);

        var act = () => db.SaveChanges();
        act.Should().Throw<DbUpdateException>().WithInnerException<SqliteException>()
            .Where(e => e.Message.Contains("FOREIGN KEY"));
    }

    [Fact]
    public void SaveChanges_BodyAtMaxLength_IsAccepted()
    {
        using var factory = new TeamNotesFactory();
        using var db = factory.CreateDbContext();
        var id = Guid.NewGuid();

        db.Notes.Add(NewNote(id, "Long note", new string('x', 50000)));
        db.SaveChanges();

        using var verify = factory.CreateDbContext();
        verify.Notes.Single(n => n.Id == id).Body.Length.Should().Be(50000);
    }

    [Fact]
    public void SaveChanges_BodyOverMaxLength_IsRejectedByCheckConstraint()
    {
        using var factory = new TeamNotesFactory();
        using var db = factory.CreateDbContext();

        db.Notes.Add(NewNote(Guid.NewGuid(), "Too long", new string('x', 50001)));

        var act = () => db.SaveChanges();
        act.Should().Throw<DbUpdateException>().WithInnerException<SqliteException>()
            .Where(e => e.Message.Contains("CK_Notes_Body"));
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

        // The losing write must not have touched the stored row: the winner's title and version survive.
        using var verify = factory.CreateDbContext();
        var stored = verify.Notes.Single(n => n.Id == DevSeeder.ApolloWelcomeNoteId);
        stored.Title.Should().Be("First writer");
        stored.Version.Should().Be(2);
    }

    private static Note NewNote(Guid id, string title, string body = "") => new()
    {
        Id = id,
        TeamId = DevSeeder.ApolloTeamId,
        AuthorId = DevSeeder.BobId,
        Title = title,
        Body = body,
        Visibility = NoteVisibility.Team,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };
}
