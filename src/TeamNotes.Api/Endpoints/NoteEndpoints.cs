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
        notes.MapPost("/", CreateNote);
        notes.MapGet("/{noteId:guid}", GetNote).WithName(nameof(GetNote));
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

    private static async Task<Results<Created<NoteResponse>, ValidationProblem, NotFound>> CreateNote(
        Guid teamId, CreateNoteRequest request, ClaimsPrincipal user, NotesDbContext db, HttpContext http, CancellationToken ct)
    {
        var userId = user.GetUserId();

        // Non-members get 404 whether or not the team exists, so team ids cannot be probed.
        if (!await IsMemberAsync(db, userId, teamId, ct))
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

        http.Response.Headers.ETag = ETag(note.Version);
        return TypedResults.Created($"/teams/{teamId}/notes/{note.Id}", NoteResponse.From(note));
    }

    private static async Task<Results<Ok<NoteResponse>, NotFound>> GetNote(
        Guid teamId, Guid noteId, ClaimsPrincipal user, NotesDbContext db, HttpContext http, CancellationToken ct)
    {
        var userId = user.GetUserId();
        if (!await IsMemberAsync(db, userId, teamId, ct))
        {
            return TypedResults.NotFound();
        }

        // Matching on both ids means a note requested under the wrong team route is simply not found.
        var note = await db.Notes.AsNoTracking().SingleOrDefaultAsync(n => n.Id == noteId && n.TeamId == teamId, ct);
        if (note is null || !NoteAccess.CanRead(note, userId))
        {
            return TypedResults.NotFound();
        }

        http.Response.Headers.ETag = ETag(note.Version);
        return TypedResults.Ok(NoteResponse.From(note));
    }

    private static Task<bool> IsMemberAsync(NotesDbContext db, Guid userId, Guid teamId, CancellationToken ct) =>
        db.Memberships.AnyAsync(m => m.UserId == userId && m.TeamId == teamId, ct);

    /// <summary>Strong ETag carrying the note version, e.g. "3".</summary>
    private static string ETag(int version) => $"\"{version}\"";
}
