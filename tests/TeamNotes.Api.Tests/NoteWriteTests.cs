using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TeamNotes.Api.Data;

namespace TeamNotes.Api.Tests;

/// <summary>
/// Update and delete with optimistic concurrency. Contract (docs/DESIGN.md): membership and visibility first (404),
/// then permission (403), then If-Match (428 missing, 400 malformed, 412 stale), then body validation (400).
/// Seed: Alice owns Apollo, Bob is a plain Apollo member, Carol is not in Apollo.
/// </summary>
public class NoteWriteTests : IDisposable
{
    private static readonly Guid Alice = DevSeeder.AliceId;
    private static readonly Guid Bob = DevSeeder.BobId;
    private static readonly Guid Carol = DevSeeder.CarolId;
    private static readonly Guid Apollo = DevSeeder.ApolloTeamId;
    private static readonly Guid Gemini = DevSeeder.GeminiTeamId;
    private static readonly string WelcomeUrl = $"/teams/{DevSeeder.ApolloTeamId}/notes/{DevSeeder.ApolloWelcomeNoteId}";
    private static readonly string AlicePrivateUrl = $"/teams/{DevSeeder.ApolloTeamId}/notes/{DevSeeder.AlicePrivateNoteId}";

    private readonly TeamNotesFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient ClientFor(Guid userId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId.ToString());
        return client;
    }

    private static HttpRequestMessage Put(string url, string? ifMatch, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return request;
    }

    private static HttpRequestMessage Delete(string url, string? ifMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, url);
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return request;
    }

    private async Task<string> CreateNote(Guid author, string visibility)
    {
        var response = await ClientFor(author).PostAsJsonAsync($"/teams/{Apollo}/notes", new { title = "Original", body = "v1", visibility });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return response.Headers.Location!.ToString();
    }

    // ---- update: happy path ----

    [Fact]
    public async Task UpdateNote_CurrentVersion_ReplacesTitleAndBodyAndBumpsVersion()
    {
        var response = await ClientFor(Bob).SendAsync(Put(WelcomeUrl, "\"1\"", new { title = "  Edited  ", body = "new body" }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.ETag!.Tag.Should().Be("\"2\"");
        var note = await response.Content.ReadFromJsonAsync<JsonElement>();
        note.GetProperty("title").GetString().Should().Be("Edited");
        note.GetProperty("body").GetString().Should().Be("new body");
        note.GetProperty("version").GetInt32().Should().Be(2);
        note.GetProperty("authorId").GetGuid().Should().Be(Alice, "editing does not change the author");
        note.GetProperty("visibility").GetString().Should().Be("Team");

        var reread = await ClientFor(Alice).GetAsync(WelcomeUrl);
        reread.Headers.ETag!.Tag.Should().Be("\"2\"");
        (await reread.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString().Should().Be("Edited");
    }

    [Fact]
    public async Task UpdateNote_Success_AdvancesUpdatedAtButNotCreatedAt()
    {
        var response = await ClientFor(Bob).SendAsync(Put(WelcomeUrl, "\"1\"", new { title = "Edited", body = "" }));

        var note = await response.Content.ReadFromJsonAsync<JsonElement>();
        // Seeded at 2026-01-01T00:00:00Z.
        note.GetProperty("createdAt").GetDateTime().Should().Be(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        note.GetProperty("updatedAt").GetDateTime().Should().BeAfter(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        note.GetProperty("updatedAt").GetString().Should().EndWith("Z");
    }

    [Fact]
    public async Task UpdateNote_PrivateNoteByAuthor_Succeeds()
    {
        var response = await ClientFor(Alice).SendAsync(Put(AlicePrivateUrl, "\"1\"", new { title = "Mine", body = "" }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UpdateNote_BodyIncludesVisibilityOrAuthor_TheyAreIgnored()
    {
        var response = await ClientFor(Bob).SendAsync(Put(WelcomeUrl, "\"1\"",
            new { title = "Edited", body = "", visibility = "Private", authorId = Bob, teamId = Gemini, version = 50 }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var note = await response.Content.ReadFromJsonAsync<JsonElement>();
        note.GetProperty("visibility").GetString().Should().Be("Team");
        note.GetProperty("authorId").GetGuid().Should().Be(Alice);
        note.GetProperty("teamId").GetGuid().Should().Be(Apollo);
        note.GetProperty("version").GetInt32().Should().Be(2);
    }

    // ---- update: visibility and membership (404 before anything else) ----

    [Fact]
    public async Task UpdateNote_PrivateNoteByTeamOwner_ReturnsNotFound()
    {
        var bobPrivate = await CreateNote(Bob, "Private");

        var response = await ClientFor(Alice).SendAsync(Put(bobPrivate, "\"1\"", new { title = "Owner edit", body = "" }));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateNote_NonMember_ReturnsNotFound()
    {
        var response = await ClientFor(Carol).SendAsync(Put(WelcomeUrl, "\"1\"", new { title = "Intruder", body = "" }));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateNote_NonMemberWithoutIfMatch_ReturnsNotFoundNot428()
    {
        // Precondition errors must not reveal that a hidden note exists.
        var response = await ClientFor(Carol).SendAsync(Put(WelcomeUrl, null, new { title = "Intruder", body = "" }));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateNote_HiddenNoteWithStaleIfMatch_ReturnsNotFoundNot412()
    {
        var response = await ClientFor(Bob).SendAsync(Put(AlicePrivateUrl, "\"9\"", new { title = "Peek", body = "" }));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateNote_WrongTeamRoute_ReturnsNotFound()
    {
        var response = await ClientFor(Bob).SendAsync(
            Put($"/teams/{Gemini}/notes/{DevSeeder.ApolloWelcomeNoteId}", "\"1\"", new { title = "Wrong team", body = "" }));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateNote_UnknownNote_ReturnsNotFound()
    {
        var response = await ClientFor(Bob).SendAsync(Put($"/teams/{Apollo}/notes/{Guid.NewGuid()}", "\"1\"", new { title = "Ghost", body = "" }));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- update: preconditions ----

    [Fact]
    public async Task UpdateNote_MissingIfMatch_ReturnsPreconditionRequired()
    {
        var response = await ClientFor(Bob).SendAsync(Put(WelcomeUrl, null, new { title = "No precondition", body = "" }));

        response.StatusCode.Should().Be((HttpStatusCode)428);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Theory]
    [InlineData("1")]           // unquoted
    [InlineData("W/\"1\"")]     // weak validators are not accepted for writes
    [InlineData("*")]           // wildcard unsupported: a client must say which version it edited
    [InlineData("\"1\", \"2\"")] // lists unsupported
    [InlineData("\"abc\"")]
    [InlineData("\"0\"")]
    [InlineData("\"-1\"")]
    [InlineData("\"99999999999\"")]
    [InlineData("\"\"")]
    [InlineData("\"01\"")]      // not the canonical tag the server issued
    public async Task UpdateNote_MalformedIfMatch_ReturnsBadRequest(string ifMatch)
    {
        var response = await ClientFor(Bob).SendAsync(Put(WelcomeUrl, ifMatch, new { title = "Bad precondition", body = "" }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task UpdateNote_StaleIfMatch_ReturnsPreconditionFailedWithCurrentETag()
    {
        var client = ClientFor(Bob);
        (await client.SendAsync(Put(WelcomeUrl, "\"1\"", new { title = "First", body = "" }))).StatusCode.Should().Be(HttpStatusCode.OK);

        var stale = await client.SendAsync(Put(WelcomeUrl, "\"1\"", new { title = "Second", body = "" }));

        stale.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
        stale.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        stale.Headers.ETag!.Tag.Should().Be("\"2\"", "the client needs the current version to re-read and retry");

        var stored = await (await client.GetAsync(WelcomeUrl)).Content.ReadFromJsonAsync<JsonElement>();
        stored.GetProperty("title").GetString().Should().Be("First", "a stale write must not change the note");
        stored.GetProperty("version").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task UpdateNote_FutureVersion_ReturnsPreconditionFailed()
    {
        var response = await ClientFor(Bob).SendAsync(Put(WelcomeUrl, "\"7\"", new { title = "From the future", body = "" }));

        response.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
    }

    [Fact]
    public async Task UpdateNote_MissingIfMatchAndInvalidBody_ReturnsPreconditionRequired()
    {
        // Precondition is checked before validation: a client without a version must fetch the note first anyway.
        var response = await ClientFor(Bob).SendAsync(Put(WelcomeUrl, null, new { title = "   ", body = "" }));

        response.StatusCode.Should().Be((HttpStatusCode)428);
    }

    [Fact]
    public async Task UpdateNote_InvalidTitle_ReturnsValidationProblemAndLeavesNoteUnchanged()
    {
        var response = await ClientFor(Bob).SendAsync(Put(WelcomeUrl, "\"1\"", new { title = "\u00A0", body = "" }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("errors").TryGetProperty("title", out _).Should().BeTrue();

        var stored = await ClientFor(Bob).GetAsync(WelcomeUrl);
        stored.Headers.ETag!.Tag.Should().Be("\"1\"");
    }

    [Fact]
    public async Task UpdateNote_BodyOver50000Characters_ReturnsValidationProblem()
    {
        var response = await ClientFor(Bob).SendAsync(Put(WelcomeUrl, "\"1\"", new { title = "Big", body = new string('b', 50001) }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- simultaneous edits ----

    [Fact]
    public async Task UpdateNote_TwoSimultaneousEditsWithSameVersion_ExactlyOneSucceeds()
    {
        // Both teammates read version 1, then save at the same moment. Exactly one write may win; the other must get
        // 412 rather than silently overwriting it (no lost update).
        using var start = new ManualResetEventSlim(false);
        var alice = ClientFor(Alice);
        var bob = ClientFor(Bob);

        var aliceTask = Task.Run(async () => { start.Wait(); return await alice.SendAsync(Put(WelcomeUrl, "\"1\"", new { title = "Alice wins", body = "" })); });
        var bobTask = Task.Run(async () => { start.Wait(); return await bob.SendAsync(Put(WelcomeUrl, "\"1\"", new { title = "Bob wins", body = "" })); });
        start.Set();
        var responses = await Task.WhenAll(aliceTask, bobTask);

        responses.Select(r => r.StatusCode).Should().BeEquivalentTo(new[] { HttpStatusCode.OK, HttpStatusCode.PreconditionFailed });
        var winner = responses.Single(r => r.StatusCode == HttpStatusCode.OK);
        var winnerTitle = (await winner.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString();

        var stored = await (await alice.GetAsync(WelcomeUrl)).Content.ReadFromJsonAsync<JsonElement>();
        stored.GetProperty("title").GetString().Should().Be(winnerTitle);
        stored.GetProperty("version").GetInt32().Should().Be(2, "only one increment happened");
    }

    [Fact]
    public async Task UpdateNote_ManySimultaneousEditsWithSameVersion_ExactlyOneSucceeds()
    {
        const int writers = 10;
        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, writers).Select(i =>
        {
            var client = ClientFor(i % 2 == 0 ? Alice : Bob);
            return Task.Run(async () => { start.Wait(); return await client.SendAsync(Put(WelcomeUrl, "\"1\"", new { title = $"Writer {i}", body = "" })); });
        }).ToArray();
        start.Set();
        var responses = await Task.WhenAll(tasks);

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.PreconditionFailed).Should().Be(writers - 1);
        var stored = await (await ClientFor(Bob).GetAsync(WelcomeUrl)).Content.ReadFromJsonAsync<JsonElement>();
        stored.GetProperty("version").GetInt32().Should().Be(2);
    }

    // ---- delete ----

    [Fact]
    public async Task DeleteNote_AuthorWithCurrentVersion_ReturnsNoContentAndNoteIsGone()
    {
        var url = await CreateNote(Bob, "Team");

        var response = await ClientFor(Bob).SendAsync(Delete(url, "\"1\""));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ClientFor(Bob).GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteNote_TeamOwnerOnSomeoneElsesTeamNote_ReturnsNoContent()
    {
        var url = await CreateNote(Bob, "Team");

        var response = await ClientFor(Alice).SendAsync(Delete(url, "\"1\""));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeleteNote_TeamNoteByNonAuthorMember_ReturnsForbidden()
    {
        // Bob can see and edit Alice's team note, but deleting shared work is reserved for its author or an owner.
        var response = await ClientFor(Bob).SendAsync(Delete(WelcomeUrl, "\"1\""));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await ClientFor(Bob).GetAsync(WelcomeUrl)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DeleteNote_ForbiddenMemberWithoutIfMatch_ReturnsForbiddenNot428()
    {
        // Permission is decided before the precondition: no point asking for a version on a request that can never succeed.
        var response = await ClientFor(Bob).SendAsync(Delete(WelcomeUrl, null));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DeleteNote_PrivateNoteByTeamOwner_ReturnsNotFound()
    {
        var bobPrivate = await CreateNote(Bob, "Private");

        var response = await ClientFor(Alice).SendAsync(Delete(bobPrivate, "\"1\""));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ClientFor(Bob).GetAsync(bobPrivate)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DeleteNote_NonMember_ReturnsNotFound()
    {
        var response = await ClientFor(Carol).SendAsync(Delete(WelcomeUrl, "\"1\""));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteNote_WrongTeamRoute_ReturnsNotFound()
    {
        var response = await ClientFor(Bob).SendAsync(Delete($"/teams/{Gemini}/notes/{DevSeeder.ApolloWelcomeNoteId}", "\"1\""));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteNote_MissingIfMatch_ReturnsPreconditionRequired()
    {
        var response = await ClientFor(Alice).SendAsync(Delete(WelcomeUrl, null));

        response.StatusCode.Should().Be((HttpStatusCode)428);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("W/\"1\"")]
    [InlineData("*")]
    [InlineData("\"1\", \"2\"")]
    [InlineData("\"abc\"")]
    [InlineData("\"0\"")]
    [InlineData("\"-1\"")]
    [InlineData("\"01\"")]
    [InlineData("\"99999999999\"")]
    [InlineData("\"\"")]
    public async Task DeleteNote_MalformedIfMatch_ReturnsBadRequestAndKeepsNote(string ifMatch)
    {
        var response = await ClientFor(Alice).SendAsync(Delete(WelcomeUrl, ifMatch));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await ClientFor(Alice).GetAsync(WelcomeUrl)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DeleteNote_MissingIfMatch_ReturnsProblemDetails()
    {
        var response = await ClientFor(Alice).SendAsync(Delete(WelcomeUrl, null));

        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task UpdateNote_NoIdentityHeader_ReturnsUnauthorized()
    {
        var response = await _factory.CreateClient().SendAsync(Put(WelcomeUrl, "\"1\"", new { title = "Anonymous", body = "" }));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DeleteNote_NoIdentityHeader_ReturnsUnauthorized()
    {
        var response = await _factory.CreateClient().SendAsync(Delete(WelcomeUrl, "\"1\""));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UpdateNote_TitleAt200Characters_IsAccepted()
    {
        var response = await ClientFor(Bob).SendAsync(Put(WelcomeUrl, "\"1\"", new { title = new string('t', 200), body = "" }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UpdateNote_TitleAt201Characters_ReturnsValidationProblem()
    {
        var response = await ClientFor(Bob).SendAsync(Put(WelcomeUrl, "\"1\"", new { title = new string('t', 201), body = "" }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateNote_AnotherWriteLandsRightAfterOurs_ResponseDescribesOurWrite()
    {
        // Deterministic stand-in for a race: a trigger applies "someone else's" edit inside the same statement as ours,
        // so by the time the handler could re-read, the note is already at version 3. The response must still
        // describe the version this request created (2) and what it wrote, not the other writer's state.
        using (var db = _factory.CreateDbContext())
        {
            db.Database.ExecuteSqlRaw("""
                CREATE TRIGGER SomeoneElseEdits AFTER UPDATE OF Title ON Notes
                WHEN NEW.Title = 'Ours'
                BEGIN
                    UPDATE Notes SET Title = 'Theirs', Version = NEW.Version + 1 WHERE Id = NEW.Id;
                END;
                """);
        }

        var response = await ClientFor(Bob).SendAsync(Put(WelcomeUrl, "\"1\"", new { title = "Ours", body = "" }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.ETag!.Tag.Should().Be("\"2\"");
        var note = await response.Content.ReadFromJsonAsync<JsonElement>();
        note.GetProperty("title").GetString().Should().Be("Ours");
        note.GetProperty("version").GetInt32().Should().Be(2);

        // The other write really happened, so the client's next write with "2" correctly gets 412.
        (await ClientFor(Bob).GetAsync(WelcomeUrl)).Headers.ETag!.Tag.Should().Be("\"3\"");
    }

    [Fact]
    public async Task DeleteNote_StaleVersion_ReturnsPreconditionFailedAndKeepsNote()
    {
        // Bob edits the shared note; Alice still holds version 1 and must not delete work she has not seen.
        (await ClientFor(Bob).SendAsync(Put(WelcomeUrl, "\"1\"", new { title = "Bob's edit", body = "" }))).StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await ClientFor(Alice).SendAsync(Delete(WelcomeUrl, "\"1\""));

        response.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
        response.Headers.ETag!.Tag.Should().Be("\"2\"");
        (await ClientFor(Alice).GetAsync(WelcomeUrl)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DeleteNote_AlreadyDeleted_ReturnsNotFound()
    {
        var url = await CreateNote(Bob, "Team");
        (await ClientFor(Bob).SendAsync(Delete(url, "\"1\""))).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var again = await ClientFor(Bob).SendAsync(Delete(url, "\"1\""));

        again.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateAndDelete_Simultaneous_NeverBothSucceed()
    {
        using var start = new ManualResetEventSlim(false);
        var bob = ClientFor(Bob);
        var alice = ClientFor(Alice);

        var update = Task.Run(async () => { start.Wait(); return await bob.SendAsync(Put(WelcomeUrl, "\"1\"", new { title = "Edit", body = "" })); });
        var delete = Task.Run(async () => { start.Wait(); return await alice.SendAsync(Delete(WelcomeUrl, "\"1\"")); });
        start.Set();
        var results = await Task.WhenAll(update, delete);

        // Either the edit lands and the delete is stale (412), or the delete lands and the edit finds nothing (404 or 412).
        var updateStatus = results[0].StatusCode;
        var deleteStatus = results[1].StatusCode;
        (updateStatus == HttpStatusCode.OK && deleteStatus == HttpStatusCode.NoContent).Should().BeFalse("an edit must never be silently deleted");
        new[] { updateStatus, deleteStatus }.Should().Contain(s => s == HttpStatusCode.OK || s == HttpStatusCode.NoContent, "one of them wins");
    }
}
