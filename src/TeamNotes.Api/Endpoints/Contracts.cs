using TeamNotes.Api.Domain;

namespace TeamNotes.Api.Endpoints;

/// <summary>
/// Client-writable fields only. Ids, author, team, timestamps and version are set by the server, so extra
/// properties such as "authorId" in the JSON are ignored rather than bound (no mass assignment).
/// </summary>
public sealed record CreateNoteRequest(string? Title, string? Body, NoteVisibility? Visibility)
{
    public Dictionary<string, string[]> Validate() => NoteValidation.Validate(Title, Body);
}

/// <summary>
/// Full replacement of the editable content. Visibility is fixed at creation in this version, so it is not part of
/// this contract; ids, author, team and version are server-owned as for create.
/// </summary>
public sealed record UpdateNoteRequest(string? Title, string? Body)
{
    public Dictionary<string, string[]> Validate() => NoteValidation.Validate(Title, Body);
}

public static class NoteValidation
{
    /// <summary>
    /// Limits are in UTF-16 code units (.NET string length). Any string within them is also within the
    /// database CHECK constraints, which count code points, so a valid request never trips a constraint.
    /// </summary>
    public static Dictionary<string, string[]> Validate(string? rawTitle, string? body)
    {
        var errors = new Dictionary<string, string[]>();

        var title = rawTitle?.Trim();
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

        if (body is null)
        {
            errors["body"] = ["Body is required; use an empty string for an empty note."];
        }
        else if (body.Length > Note.BodyMaxLength)
        {
            errors["body"] = [$"Body must be at most {Note.BodyMaxLength} characters."];
        }
        else if (body.Contains('\0'))
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

/// <summary>List item: everything except the body, which can be 50,000 characters. Fetch the note for the body.</summary>
public sealed record NoteSummary(
    Guid Id,
    Guid TeamId,
    Guid AuthorId,
    string Title,
    NoteVisibility Visibility,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int Version);

/// <summary>One page of visible notes. <c>TotalCount</c> counts only notes the caller may read.</summary>
public sealed record NotePage(IReadOnlyList<NoteSummary> Items, int Page, int PageSize, int TotalCount);

/// <summary>Query-string parameters for listing. Invalid values are rejected with the field named, never clamped.</summary>
public static class NoteListQuery
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;
    public const int MaxSearchLength = 200;

    public sealed record Parsed(int Page, int PageSize, string? Search);

    /// <summary>
    /// Parses page, pageSize and q from the raw query string. Parsing is done here rather than by parameter binding so
    /// that every bad value (missing digits, overflow, repeats) produces a ProblemDetails entry naming the field.
    /// </summary>
    public static Parsed Parse(IQueryCollection query, out Dictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(query);
        errors = new Dictionary<string, string[]>();

        var page = ParsePositiveInt(query, "page", 1, errors);
        var pageSize = ParsePositiveInt(query, "pageSize", DefaultPageSize, errors);

        if (!errors.ContainsKey("pageSize") && pageSize > MaxPageSize)
        {
            errors["pageSize"] = [$"pageSize must be between 1 and {MaxPageSize}."];
        }

        if (!errors.ContainsKey("page") && !errors.ContainsKey("pageSize") && (long)(page - 1) * pageSize > int.MaxValue)
        {
            // The row offset must fit the database's int offset; no real team gets near this.
            errors["page"] = ["page is too large."];
        }

        string? search = null;
        if (query.TryGetValue("q", out var searchValues))
        {
            if (searchValues.Count != 1)
            {
                errors["q"] = ["q may be given at most once."];
            }
            else
            {
                search = searchValues[0]?.Trim();
                if (search is not null && search.Length > MaxSearchLength)
                {
                    errors["q"] = [$"q must be at most {MaxSearchLength} characters."];
                }
                else if (search is not null && search.Contains('\0'))
                {
                    // SQLite stops reading a LIKE pattern at NUL, which would silently turn the filter off.
                    errors["q"] = ["q cannot contain NUL characters."];
                }
            }
        }

        return new Parsed(page, pageSize, string.IsNullOrEmpty(search) ? null : search);
    }

    private static int ParsePositiveInt(IQueryCollection query, string name, int defaultValue, Dictionary<string, string[]> errors)
    {
        if (!query.TryGetValue(name, out var values))
        {
            return defaultValue;
        }

        // Digits only: no sign, spaces, decimals or exponents. int.TryParse also rejects values beyond int range.
        if (values.Count != 1 || string.IsNullOrEmpty(values[0]) || !values[0]!.All(char.IsAsciiDigit)
            || !int.TryParse(values[0], out var value) || value < 1)
        {
            errors[name] = name == "pageSize"
                ? [$"pageSize must be a single whole number between 1 and {MaxPageSize}."]
                : ["page must be a single whole number, 1 or greater."];
            return defaultValue;
        }

        return value;
    }

    /// <summary>
    /// Builds a LIKE pattern that matches <paramref name="search"/> literally: the escape character and LIKE's own
    /// wildcards (%, _) are escaped, so "100%" finds "100%" and not "1000".
    /// </summary>
    public static string ToLikePattern(string search)
    {
        ArgumentNullException.ThrowIfNull(search);
        var escaped = search.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
        return $"%{escaped}%";
    }

    public const string LikeEscape = @"\";
}
