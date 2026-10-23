using TeamNotes.Api.Domain;

namespace TeamNotes.Api.Data;

/// <summary>
/// Inserts the fixed demo users, teams, memberships and notes that the README and .http examples refer to.
/// Idempotent: each row is inserted only if its key is missing, and existing rows are never modified or deleted.
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

        AddIfMissing(db, new User { Id = AliceId, Name = "Alice" }, AliceId);
        AddIfMissing(db, new User { Id = BobId, Name = "Bob" }, BobId);
        AddIfMissing(db, new User { Id = CarolId, Name = "Carol" }, CarolId);

        AddIfMissing(db, new Team { Id = ApolloTeamId, Name = "Apollo" }, ApolloTeamId);
        AddIfMissing(db, new Team { Id = GeminiTeamId, Name = "Gemini" }, GeminiTeamId);

        // Alice owns Apollo; Carol owns Gemini; Bob is a plain member of both, which demonstrates cross-team isolation.
        AddMembershipIfMissing(db, AliceId, ApolloTeamId, TeamRole.Owner);
        AddMembershipIfMissing(db, BobId, ApolloTeamId, TeamRole.Member);
        AddMembershipIfMissing(db, CarolId, GeminiTeamId, TeamRole.Owner);
        AddMembershipIfMissing(db, BobId, GeminiTeamId, TeamRole.Member);

        // Users and teams must be saved before the rows that reference them.
        db.SaveChanges();

        AddIfMissing(db, new Note
        {
            Id = ApolloWelcomeNoteId,
            TeamId = ApolloTeamId,
            AuthorId = AliceId,
            Title = "Welcome to Apollo",
            Body = "Team-visible note: every Apollo member can read and edit it.",
            Visibility = NoteVisibility.Team,
            CreatedAt = SeedTime,
            UpdatedAt = SeedTime,
        }, ApolloWelcomeNoteId);

        AddIfMissing(db, new Note
        {
            Id = AlicePrivateNoteId,
            TeamId = ApolloTeamId,
            AuthorId = AliceId,
            Title = "Alice's private draft",
            Body = "Private note: only Alice can see it, even though Bob is in the same team.",
            Visibility = NoteVisibility.Private,
            CreatedAt = SeedTime,
            UpdatedAt = SeedTime,
        }, AlicePrivateNoteId);

        db.SaveChanges();
    }

    private static void AddIfMissing<TEntity>(NotesDbContext db, TEntity entity, Guid id) where TEntity : class
    {
        if (db.Set<TEntity>().Find(id) is null)
        {
            db.Set<TEntity>().Add(entity);
        }
    }

    private static void AddMembershipIfMissing(NotesDbContext db, Guid userId, Guid teamId, TeamRole role)
    {
        if (db.Memberships.Find(userId, teamId) is null)
        {
            db.Memberships.Add(new Membership { UserId = userId, TeamId = teamId, Role = role });
        }
    }
}
