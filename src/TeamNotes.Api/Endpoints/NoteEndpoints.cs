using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TeamNotes.Api.Auth;
using TeamNotes.Api.Data;
using TeamNotes.Api.Domain;

namespace TeamNotes.Api.Endpoints;

public static class NoteEndpoints
{
    public static void MapNoteEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/me/teams", GetMyTeams).RequireAuthorization();

        var notes = app.MapGroup("/teams/{teamId:guid}/notes").RequireAuthorization();
        notes.MapGet("/", ListNotes);
        notes.MapPost("/", CreateNote);
        notes.MapGet("/{noteId:guid}", GetNote);
        notes.MapPut("/{noteId:guid}", UpdateNote);
        notes.MapDelete("/{noteId:guid}", DeleteNote);
    }

    private static async Task<Ok<List<TeamResponse>>> GetMyTeams(ClaimsPrincipal user, NotesDbContext db, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var teams = await (
                from membership in db.Memberships
                join team in db.Teams on membership.TeamId equals team.Id
                where membership.UserId == userId
                orderby team.Name
                select new TeamResponse(team.Id, team.Name, membership.Role))
            .ToListAsync(ct);
        return TypedResults.Ok(teams);
    }

    /// <summary>
    /// Lists the notes the caller can read, newest first. Visibility is part of the SQL WHERE clause, so hidden notes
    /// never reach COUNT, OFFSET or LIMIT: page sizes and totals reveal nothing about other people's private notes.
    /// </summary>
    private static async Task<Results<Ok<NotePage>, ValidationProblem, NotFound>> ListNotes(
        Guid teamId, ClaimsPrincipal user, NotesDbContext db, HttpContext http, CancellationToken ct)
    {
        var userId = user.GetUserId();
        if (await FindMembershipAsync(db, userId, teamId, ct) is null)
        {
            return TypedResults.NotFound();
        }

        var (pageNumber, size, search) = NoteListQuery.Parse(http.Request.Query, out var errors);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var visible = db.Notes.AsNoTracking()
            .Where(n => n.TeamId == teamId)
            .Where(NoteAccess.ReadableBy(userId));

        if (search is not null)
        {
            // SQLite LIKE folds case for ASCII letters only; see DESIGN.md "Listing and search" for the trade-off.
            var pattern = NoteListQuery.ToLikePattern(search);
            visible = visible.Where(n =>
                EF.Functions.Like(n.Title, pattern, NoteListQuery.LikeEscape)
                || EF.Functions.Like(n.Body, pattern, NoteListQuery.LikeEscape));
        }

        var totalCount = await visible.CountAsync(ct);

        // Id breaks ties between equal timestamps, so the order is total and pages never repeat or skip a note.
        var items = await visible
            .OrderByDescending(n => n.UpdatedAt)
            .ThenByDescending(n => n.Id)
            .Skip((pageNumber - 1) * size)
            .Take(size)
            .Select(n => new NoteSummary(n.Id, n.TeamId, n.AuthorId, n.Title, n.Visibility, n.CreatedAt, n.UpdatedAt, n.Version))
            .ToListAsync(ct);

        // SQLite drops DateTimeKind; values are written as UTC, so mark them UTC to serialize with "Z".
        var utcItems = items
            .Select(i => i with
            {
                CreatedAt = DateTime.SpecifyKind(i.CreatedAt, DateTimeKind.Utc),
                UpdatedAt = DateTime.SpecifyKind(i.UpdatedAt, DateTimeKind.Utc),
            })
            .ToList();

        return TypedResults.Ok(new NotePage(utcItems, pageNumber, size, totalCount));
    }

    private static async Task<Results<Created<NoteResponse>, ValidationProblem, NotFound>> CreateNote(
        Guid teamId, CreateNoteRequest request, ClaimsPrincipal user, NotesDbContext db, HttpContext http, CancellationToken ct)
    {
        var userId = user.GetUserId();

        // Non-members get 404 whether or not the team exists, so team ids cannot be probed.
        if (await FindMembershipAsync(db, userId, teamId, ct) is null)
        {
            return TypedResults.NotFound();
        }

        var errors = request.Validate();
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var now = DateTime.UtcNow;
        var note = new Note
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            AuthorId = userId,
            Title = request.Title!.Trim(),
            Body = request.Body!,
            Visibility = request.Visibility ?? NoteVisibility.Private,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
        };
        db.Notes.Add(note);
        await db.SaveChangesAsync(ct);

        http.Response.Headers.ETag = NoteETag.Format(note.Version);
        return TypedResults.Created($"/teams/{teamId}/notes/{note.Id}", NoteResponse.From(note));
    }

    private static async Task<Results<Ok<NoteResponse>, NotFound>> GetNote(
        Guid teamId, Guid noteId, ClaimsPrincipal user, NotesDbContext db, HttpContext http, CancellationToken ct)
    {
        var userId = user.GetUserId();
        if (await FindMembershipAsync(db, userId, teamId, ct) is null)
        {
            return TypedResults.NotFound();
        }

        // Matching on both ids means a note requested under the wrong team route is simply not found.
        var note = await db.Notes.AsNoTracking().SingleOrDefaultAsync(n => n.Id == noteId && n.TeamId == teamId, ct);
        if (note is null || !NoteAccess.CanRead(note, userId))
        {
            return TypedResults.NotFound();
        }

        http.Response.Headers.ETag = NoteETag.Format(note.Version);
        return TypedResults.Ok(NoteResponse.From(note));
    }

    /// <summary>
    /// Replaces title and body. Order of checks: membership and visibility (404), If-Match (428/400), body
    /// validation (400), then a single conditional UPDATE. The version check and increment happen inside that one SQL
    /// statement, so two writers holding the same version cannot both succeed, whatever the timing.
    /// </summary>
    private static async Task<IResult> UpdateNote(
        Guid teamId, Guid noteId, UpdateNoteRequest request, ClaimsPrincipal user, NotesDbContext db, HttpContext http, CancellationToken ct)
    {
        var userId = user.GetUserId();
        if (await FindMembershipAsync(db, userId, teamId, ct) is null)
        {
            return TypedResults.NotFound();
        }

        var note = await FindNoteAsync(db, teamId, noteId, ct);
        if (note is null || !NoteAccess.CanEdit(note, userId))
        {
            return TypedResults.NotFound();
        }

        if (CheckIfMatch(http, out var expectedVersion) is { } preconditionError)
        {
            return preconditionError;
        }

        var errors = request.Validate();
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var title = request.Title!.Trim();
        var body = request.Body!;
        var now = DateTime.UtcNow;
        var updated = await db.Notes
            .Where(n => n.Id == noteId && n.TeamId == teamId && n.Version == expectedVersion)
            .ExecuteUpdateAsync(set => set
                .SetProperty(n => n.Title, title)
                .SetProperty(n => n.Body, body)
                .SetProperty(n => n.UpdatedAt, now)
                .SetProperty(n => n.Version, n => n.Version + 1), ct);

        if (updated == 0)
        {
            return await ConcurrencyFailureAsync(db, teamId, noteId, http, ct);
        }

        // Build the response from what this request wrote, not from a re-read: another writer may already have moved
        // the note on (or deleted it) since our UPDATE committed, and this response must describe our version.
        // Immutable fields (id, team, author, visibility, createdAt) come from the earlier read.
        note.Title = title;
        note.Body = body;
        note.UpdatedAt = now;
        note.Version = expectedVersion + 1;
        http.Response.Headers.ETag = NoteETag.Format(note.Version);
        return TypedResults.Ok(NoteResponse.From(note));
    }

    /// <summary>
    /// Hard delete. Order: membership and visibility (404), permission (403), If-Match (428/400), then a single
    /// conditional DELETE. A stale version returns 412, so nobody deletes an edit they have not seen.
    /// </summary>
    private static async Task<IResult> DeleteNote(
        Guid teamId, Guid noteId, ClaimsPrincipal user, NotesDbContext db, HttpContext http, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var membership = await FindMembershipAsync(db, userId, teamId, ct);
        if (membership is null)
        {
            return TypedResults.NotFound();
        }

        var note = await FindNoteAsync(db, teamId, noteId, ct);
        if (note is null || !NoteAccess.CanRead(note, userId))
        {
            return TypedResults.NotFound();
        }

        if (!NoteAccess.CanDelete(note, userId, membership.Role))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Only the note's author or a team owner can delete it.");
        }

        if (CheckIfMatch(http, out var expectedVersion) is { } preconditionError)
        {
            return preconditionError;
        }

        var deleted = await db.Notes
            .Where(n => n.Id == noteId && n.TeamId == teamId && n.Version == expectedVersion)
            .ExecuteDeleteAsync(ct);

        return deleted == 0
            ? await ConcurrencyFailureAsync(db, teamId, noteId, http, ct)
            : TypedResults.NoContent();
    }

    /// <summary>
    /// The conditional write matched no row: either the version moved on (412, with the current ETag so the client
    /// can re-read and retry) or the note was deleted meanwhile (404).
    /// </summary>
    private static async Task<IResult> ConcurrencyFailureAsync(
        NotesDbContext db, Guid teamId, Guid noteId, HttpContext http, CancellationToken ct)
    {
        var current = await FindNoteAsync(db, teamId, noteId, ct);
        if (current is null)
        {
            return TypedResults.NotFound();
        }

        http.Response.Headers.ETag = NoteETag.Format(current.Version);
        return TypedResults.Problem(
            statusCode: StatusCodes.Status412PreconditionFailed,
            title: "The note was changed by someone else.",
            detail: "Fetch the latest version and retry with its ETag in If-Match.");
    }

    /// <summary>Returns an error result when If-Match is missing (428) or not a single strong version tag (400).</summary>
    private static IResult? CheckIfMatch(HttpContext http, out int expectedVersion) =>
        NoteETag.TryParseIfMatch(http.Request.Headers.IfMatch, out expectedVersion) switch
        {
            NoteETag.ParseOutcome.Missing => TypedResults.Problem(
                statusCode: StatusCodes.Status428PreconditionRequired,
                title: "If-Match is required.",
                detail: "Send the ETag you received when reading the note, e.g. If-Match: \"3\"."),
            NoteETag.ParseOutcome.Malformed => TypedResults.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "If-Match must be a single strong ETag such as \"3\".",
                detail: "Weak tags, '*' and lists of tags are not supported."),
            _ => null,
        };

    private static Task<Membership?> FindMembershipAsync(NotesDbContext db, Guid userId, Guid teamId, CancellationToken ct) =>
        db.Memberships.AsNoTracking().SingleOrDefaultAsync(m => m.UserId == userId && m.TeamId == teamId, ct);

    private static Task<Note?> FindNoteAsync(NotesDbContext db, Guid teamId, Guid noteId, CancellationToken ct) =>
        db.Notes.AsNoTracking().SingleOrDefaultAsync(n => n.Id == noteId && n.TeamId == teamId, ct);
}
