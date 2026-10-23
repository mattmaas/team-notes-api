using TeamNotes.Api.Domain;

namespace TeamNotes.Api.Data;

/// <summary>
/// Bootstraps the fixed demo users, teams, memberships and notes that the README and .http examples refer to.
/// The seed only ever builds a fresh database: once any user exists the call is a no-op, so rows deleted through
/// the API stay deleted across restarts and an existing database is never repaired or topped up. Running this from
/// single-instance startup keeps that check race-free; several instances would need their own coordination.
/// </summary>
public static class DevSeeder
{
    public static readonly Guid AliceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid BobId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid CarolId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public static readonly Guid ApolloTeamId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    public static readonly Guid GeminiTeamId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");

    public static readonly Guid ApolloWelcomeNoteId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    public static readonly Guid AlicePrivateNoteId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private static readonly DateTime SeedTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static void Seed(NotesDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);

        // Bootstrap only: a database that already holds users belongs to its users now.
        if (db.Users.Any())
        {
            return;
        }

        // One transaction for the whole seed, so a failure part-way through leaves no half-seeded database behind.
        using var transaction = db.Database.BeginTransaction();

        db.Users.AddRange(
            new User { Id = AliceId, Name = "Alice" },
            new User { Id = BobId, Name = "Bob" },
            new User { Id = CarolId, Name = "Carol" });

        db.Teams.AddRange(
            new Team { Id = ApolloTeamId, Name = "Apollo" },
            new Team { Id = GeminiTeamId, Name = "Gemini" });

        // Alice owns Apollo; Carol owns Gemini; Bob is a plain member of both, which demonstrates cross-team isolation.
        db.Memberships.AddRange(
            new Membership { UserId = AliceId, TeamId = ApolloTeamId, Role = TeamRole.Owner },
            new Membership { UserId = BobId, TeamId = ApolloTeamId, Role = TeamRole.Member },
            new Membership { UserId = CarolId, TeamId = GeminiTeamId, Role = TeamRole.Owner },
            new Membership { UserId = BobId, TeamId = GeminiTeamId, Role = TeamRole.Member });

        // Users and teams must be saved before the rows that reference them.
        db.SaveChanges();

        db.Notes.AddRange(
            new Note
            {
                Id = ApolloWelcomeNoteId,
                TeamId = ApolloTeamId,
                AuthorId = AliceId,
                Title = "Welcome to Apollo",
                Body = "Team-visible note: every Apollo member can read and edit it.",
                Visibility = NoteVisibility.Team,
                CreatedAt = SeedTime,
                UpdatedAt = SeedTime,
            },
            new Note
            {
                Id = AlicePrivateNoteId,
                TeamId = ApolloTeamId,
                AuthorId = AliceId,
                Title = "Alice's private draft",
                Body = "Private note: only Alice can see it, even though Bob is in the same team.",
                Visibility = NoteVisibility.Private,
                CreatedAt = SeedTime,
                UpdatedAt = SeedTime,
            });

        db.SaveChanges();

        transaction.Commit();
    }
}
