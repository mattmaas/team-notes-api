namespace TeamNotes.Api.Domain;

public enum TeamRole
{
    Member,
    Owner,
}

public enum NoteVisibility
{
    /// <summary>Only the author can see or change the note.</summary>
    Private,

    /// <summary>Every current member of the note's team can see and edit it.</summary>
    Team,
}

public class User
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
}

public class Team
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
}

/// <summary>A user's membership of a team. The (UserId, TeamId) pair is the primary key, so a user has one role per team.</summary>
public class Membership
{
    public Guid UserId { get; set; }
    public Guid TeamId { get; set; }
    public TeamRole Role { get; set; }
}

public class Note
{
    public const int TitleMaxLength = 200;
    public const int BodyMaxLength = 50_000;

    public Guid Id { get; set; }

    /// <summary>The team (workspace) that contains the note. Never changes after creation.</summary>
    public Guid TeamId { get; set; }

    /// <summary>The user who created the note. Never changes after creation.</summary>
    public Guid AuthorId { get; set; }

    public required string Title { get; set; }
    public required string Body { get; set; }
    public NoteVisibility Visibility { get; set; }

    /// <summary>UTC. Stored as DateTime because SQLite cannot order DateTimeOffset values.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>UTC.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>Starts at 1 and increases by 1 on every successful update; exposed to clients as the ETag.</summary>
    public int Version { get; set; } = 1;
}
