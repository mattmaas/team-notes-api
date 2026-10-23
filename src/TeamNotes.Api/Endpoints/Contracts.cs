using TeamNotes.Api.Domain;

namespace TeamNotes.Api.Endpoints;

/// <summary>
/// Client-writable fields only. Ids, author, team, timestamps and version are set by the server, so extra
/// properties such as "authorId" in the JSON are ignored rather than bound (no mass assignment).
/// </summary>
public sealed record CreateNoteRequest(string? Title, string? Body, NoteVisibility? Visibility)
{
    /// <summary>
    /// Limits are in UTF-16 code units (.NET string length). Any string within them is also within the
    /// database CHECK constraints, which count code points, so a valid request never trips a constraint.
    /// </summary>
    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();

        var title = Title?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            errors["title"] = ["Title is required and cannot be blank."];
        }
        else if (title.Length > Note.TitleMaxLength)
        {
            errors["title"] = [$"Title must be at most {Note.TitleMaxLength} characters."];
        }
        else if (title.Contains('\0'))
        {
            errors["title"] = ["Title cannot contain NUL characters."];
        }

        if (Body is null)
        {
            errors["body"] = ["Body is required; use an empty string for an empty note."];
        }
        else if (Body.Length > Note.BodyMaxLength)
        {
            errors["body"] = [$"Body must be at most {Note.BodyMaxLength} characters."];
        }
        else if (Body.Contains('\0'))
        {
            errors["body"] = ["Body cannot contain NUL characters."];
        }

        return errors;
    }
}

public sealed record NoteResponse(
    Guid Id,
    Guid TeamId,
    Guid AuthorId,
    string Title,
    string Body,
    NoteVisibility Visibility,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int Version)
{
    // SQLite does not store DateTimeKind; timestamps are always written as UTC, so mark them UTC to serialize with "Z".
    public static NoteResponse From(Note note) => new(
        note.Id,
        note.TeamId,
        note.AuthorId,
        note.Title,
        note.Body,
        note.Visibility,
        DateTime.SpecifyKind(note.CreatedAt, DateTimeKind.Utc),
        DateTime.SpecifyKind(note.UpdatedAt, DateTimeKind.Utc),
        note.Version);
}

public sealed record TeamResponse(Guid Id, string Name, TeamRole Role);
