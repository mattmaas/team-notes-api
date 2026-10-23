using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using TeamNotes.Api.Data;

namespace TeamNotes.Api.Tests;

/// <summary>
/// HTTP-level tests for identity, team listing and note create/read. Expectations come from the access rules in
/// docs/DESIGN.md and the seeded fixture (Alice owns Apollo, Carol owns Gemini, Bob is a member of both).
/// </summary>
public class NotesApiTests : IDisposable
{
    private static readonly Guid Alice = DevSeeder.AliceId;
    private static readonly Guid Bob = DevSeeder.BobId;
    private static readonly Guid Carol = DevSeeder.CarolId;
    private static readonly Guid Apollo = DevSeeder.ApolloTeamId;
    private static readonly Guid Gemini = DevSeeder.GeminiTeamId;

    private readonly TeamNotesFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient ClientFor(Guid? userId)
    {
        var client = _factory.CreateClient();
        if (userId is not null)
        {
            client.DefaultRequestHeaders.Add("X-User-Id", userId.ToString());
        }

        return client;
    }

    private static Task<HttpResponseMessage> PostNote(HttpClient client, Guid teamId, object body) =>
        client.PostAsJsonAsync($"/teams/{teamId}/notes", body);

    // ---- identity ----

    [Fact]
    public async Task GetMyTeams_NoIdentityHeader_ReturnsUnauthorized()
    {
        var response = await ClientFor(null).GetAsync("/me/teams");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("99999999-9999-9999-9999-999999999999")]
    [InlineData("")]
    // No surrounding-whitespace case: HTTP strips optional whitespace around field values (RFC 9110 section 5.5)
    // before the handler sees them, so " <guid> " is the same header as "<guid>".
    [InlineData("{22222222-2222-2222-2222-222222222222}")]
    [InlineData("22222222222222222222222222222222")]
    public async Task GetMyTeams_MalformedOrUnknownUser_ReturnsUnauthorized(string headerValue)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-User-Id", headerValue);

        var response = await client.GetAsync("/me/teams");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetMyTeams_RepeatedIdentityHeader_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-User-Id", new[] { Bob.ToString(), Alice.ToString() });

        var response = await client.GetAsync("/me/teams");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetMyTeams_Unauthorized_ReturnsProblemDetailsAndChallenge()
    {
        var response = await ClientFor(null).GetAsync("/me/teams");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        response.Headers.WwwAuthenticate.Select(h => h.Scheme).Should().Equal("DemoHeader");
    }

    [Fact]
    public async Task GetSwagger_ProductionEnvironment_ReturnsNotFound()
    {
        using var factory = new TeamNotesFactory(environment: "Production");

        var response = await factory.CreateClient().GetAsync("/swagger/v1/swagger.json");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetHealth_ProductionEnvironment_ReturnsOkWithoutIdentity()
    {
        using var factory = new TeamNotesFactory(environment: "Production");

        var response = await factory.CreateClient().GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetMyTeams_HeaderAuthDisabled_ReturnsUnauthorized()
    {
        using var factory = new TeamNotesFactory(headerAuth: false);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", Bob.ToString());

        var response = await client.GetAsync("/me/teams");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetMyTeams_ProductionEnvironmentWithHeaderAuthFlag_ReturnsUnauthorized()
    {
        // Seed in Development first: Production never seeds, and without Bob in the database the request would
        // fail as an unknown user, so this test would pass without exercising the environment check at all.
        var path = TeamNotesFactory.NewDatabasePath();
        try
        {
            using (var development = new TeamNotesFactory(path))
            {
                var seeded = development.CreateClient();
                seeded.DefaultRequestHeaders.Add("X-User-Id", Bob.ToString());
                (await seeded.GetAsync("/me/teams")).StatusCode.Should().Be(HttpStatusCode.OK);
            }

            using var production = new TeamNotesFactory(path, environment: "Production");
            var client = production.CreateClient();
            client.DefaultRequestHeaders.Add("X-User-Id", Bob.ToString());

            var response = await client.GetAsync("/me/teams");

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        finally
        {
            TeamNotesFactory.DeleteDatabaseFile(path);
        }
    }

    [Fact]
    public async Task GetHealth_NoIdentityHeader_ReturnsOk()
    {
        var response = await ClientFor(null).GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- /me/teams ----

    [Fact]
    public async Task GetMyTeams_MemberOfTwoTeams_ReturnsBothWithRolesOrderedByName()
    {
        var teams = await ClientFor(Bob).GetFromJsonAsync<JsonElement>("/me/teams");

        teams.EnumerateArray().Select(t => (t.GetProperty("name").GetString(), t.GetProperty("role").GetString()))
            .Should().Equal(("Apollo", "Member"), ("Gemini", "Member"));
    }

    [Fact]
    public async Task GetMyTeams_OwnerOfOneTeam_ReturnsOnlyThatTeam()
    {
        var teams = await ClientFor(Alice).GetFromJsonAsync<JsonElement>("/me/teams");

        teams.EnumerateArray().Select(t => (t.GetProperty("name").GetString(), t.GetProperty("role").GetString()))
            .Should().BeEquivalentTo(new[] { ("Apollo", "Owner") });
    }

    // ---- create ----

    [Fact]
    public async Task CreateNote_ValidRequest_ReturnsCreatedWithLocationAndETag()
    {
        var response = await PostNote(ClientFor(Bob), Apollo, new { title = "  Sprint goals  ", body = "Ship it", visibility = "Team" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.ETag!.Tag.Should().Be("\"1\"");
        response.Headers.ETag.IsWeak.Should().BeFalse();

        var note = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = note.GetProperty("id").GetGuid();
        response.Headers.Location!.ToString().Should().Be($"/teams/{Apollo}/notes/{id}");
        note.GetProperty("title").GetString().Should().Be("Sprint goals");
        note.GetProperty("body").GetString().Should().Be("Ship it");
        note.GetProperty("visibility").GetString().Should().Be("Team");
        note.GetProperty("teamId").GetGuid().Should().Be(Apollo);
        note.GetProperty("authorId").GetGuid().Should().Be(Bob);
        note.GetProperty("version").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task CreateNote_ThenGetLocation_ReturnsSameNote()
    {
        var client = ClientFor(Bob);
        var created = await PostNote(client, Apollo, new { title = "Round trip", body = "" });

        var fetched = await client.GetAsync(created.Headers.Location);

        fetched.StatusCode.Should().Be(HttpStatusCode.OK);
        fetched.Headers.ETag!.Tag.Should().Be("\"1\"");
        (await fetched.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString().Should().Be("Round trip");
    }

    [Fact]
    public async Task CreateNote_VisibilityOmitted_DefaultsToPrivate()
    {
        var response = await PostNote(ClientFor(Bob), Apollo, new { title = "Draft", body = "" });

        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("visibility").GetString().Should().Be("Private");
    }

    [Fact]
    public async Task CreateNote_ClientSuppliesAuthorAndIds_ServerValuesWin()
    {
        var forgedId = Guid.NewGuid();
        var response = await PostNote(ClientFor(Bob), Apollo, new
        {
            id = forgedId, authorId = Alice, teamId = Gemini, version = 99, title = "Forged", body = "",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var note = await response.Content.ReadFromJsonAsync<JsonElement>();
        note.GetProperty("id").GetGuid().Should().NotBe(forgedId);
        note.GetProperty("authorId").GetGuid().Should().Be(Bob);
        note.GetProperty("teamId").GetGuid().Should().Be(Apollo);
        note.GetProperty("version").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task CreateNote_Timestamps_SerializeAsUtc()
    {
        var response = await PostNote(ClientFor(Bob), Apollo, new { title = "Clock", body = "" });
        var location = response.Headers.Location;

        // Read back through a GET so the value has round-tripped through SQLite, which drops DateTimeKind.
        var json = await ClientFor(Bob).GetStringAsync(location);
        var note = JsonDocument.Parse(json).RootElement;

        note.GetProperty("createdAt").GetString().Should().EndWith("Z");
        note.GetProperty("updatedAt").GetString().Should().EndWith("Z");
    }

    [Fact]
    public async Task CreateNote_NonMemberOfTeam_ReturnsNotFound()
    {
        var response = await PostNote(ClientFor(Alice), Gemini, new { title = "Intruder", body = "" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateNote_UnknownTeam_ReturnsNotFound()
    {
        var response = await PostNote(ClientFor(Bob), Guid.NewGuid(), new { title = "Nowhere", body = "" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateNote_NoIdentityHeader_ReturnsUnauthorized()
    {
        var response = await PostNote(ClientFor(null), Apollo, new { title = "Anonymous", body = "" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(null, "title")]
    [InlineData("", "title")]
    [InlineData("   ", "title")]
    [InlineData("\u00A0\t", "title")]       // Unicode whitespace that SQLite's trim() would not strip
    [InlineData("bad\u0000title", "title")]
    public async Task CreateNote_InvalidTitle_ReturnsValidationProblem(string? title, string field)
    {
        var response = await PostNote(ClientFor(Bob), Apollo, new { title, body = "" });

        await ShouldBeValidationProblem(response, field);
    }

    [Fact]
    public async Task CreateNote_TitleAt200Characters_IsAccepted()
    {
        var response = await PostNote(ClientFor(Bob), Apollo, new { title = new string('t', 200), body = "" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateNote_TitleAt201Characters_ReturnsValidationProblem()
    {
        var response = await PostNote(ClientFor(Bob), Apollo, new { title = new string('t', 201), body = "" });

        await ShouldBeValidationProblem(response, "title");
    }

    [Fact]
    public async Task CreateNote_BodyAt50000Characters_IsAccepted()
    {
        var response = await PostNote(ClientFor(Bob), Apollo, new { title = "Long", body = new string('b', 50000) });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateNote_BodyAt50001Characters_ReturnsValidationProblem()
    {
        var response = await PostNote(ClientFor(Bob), Apollo, new { title = "Too long", body = new string('b', 50001) });

        await ShouldBeValidationProblem(response, "body");
    }

    [Fact]
    public async Task CreateNote_MissingBody_ReturnsValidationProblem()
    {
        var response = await PostNote(ClientFor(Bob), Apollo, new { title = "No body" });

        await ShouldBeValidationProblem(response, "body");
    }

    [Fact]
    public async Task CreateNote_BodyContainsNul_ReturnsValidationProblem()
    {
        var response = await PostNote(ClientFor(Bob), Apollo, new { title = "Nul", body = "before\u0000after" });

        await ShouldBeValidationProblem(response, "body");
    }

    [Fact]
    public async Task CreateNote_ValidationFailureByNonMember_ReturnsNotFound()
    {
        // Membership is checked before validation, so an outsider learns nothing about the team from error details.
        var response = await PostNote(ClientFor(Alice), Gemini, new { title = "   ", body = "" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("\"Public\"")]
    [InlineData("1")]
    public async Task CreateNote_UnknownVisibility_ReturnsBadRequest(string visibilityJson)
    {
        var client = ClientFor(Bob);
        var content = new StringContent(
            $$"""{"title":"Visibility","body":"","visibility":{{visibilityJson}}}""",
            System.Text.Encoding.UTF8,
            "application/json");

        var response = await client.PostAsync($"/teams/{Apollo}/notes", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateNote_MalformedJson_ReturnsBadRequest()
    {
        var content = new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json");

        var response = await ClientFor(Bob).PostAsync($"/teams/{Apollo}/notes", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- read / visibility ----

    [Fact]
    public async Task GetNote_TeamNoteRequestedByTeamMember_ReturnsOk()
    {
        var response = await ClientFor(Bob).GetAsync($"/teams/{Apollo}/notes/{DevSeeder.ApolloWelcomeNoteId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetNote_PrivateNoteRequestedByAuthor_ReturnsOk()
    {
        var response = await ClientFor(Alice).GetAsync($"/teams/{Apollo}/notes/{DevSeeder.AlicePrivateNoteId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetNote_PrivateNoteRequestedByTeammate_ReturnsNotFound()
    {
        var response = await ClientFor(Bob).GetAsync($"/teams/{Apollo}/notes/{DevSeeder.AlicePrivateNoteId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetNote_PrivateNoteRequestedByTeamOwner_ReturnsNotFound()
    {
        // Bob writes a private note in Apollo; Alice owns Apollo but gets no override on private notes.
        var created = await PostNote(ClientFor(Bob), Apollo, new { title = "Bob's secret", body = "", visibility = "Private" });

        var response = await ClientFor(Alice).GetAsync(created.Headers.Location);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetNote_NonMemberOfTeam_ReturnsNotFound()
    {
        var response = await ClientFor(Carol).GetAsync($"/teams/{Apollo}/notes/{DevSeeder.ApolloWelcomeNoteId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetNote_RequestedUnderWrongTeamRoute_ReturnsNotFound()
    {
        // Bob belongs to both teams, but the Apollo note does not live under Gemini.
        var response = await ClientFor(Bob).GetAsync($"/teams/{Gemini}/notes/{DevSeeder.ApolloWelcomeNoteId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetNote_UnknownNote_ReturnsNotFound()
    {
        var response = await ClientFor(Bob).GetAsync($"/teams/{Apollo}/notes/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetNote_HiddenAndMissingNotes_ReturnIdenticalResponses()
    {
        var client = ClientFor(Bob);

        var hidden = await client.GetAsync($"/teams/{Apollo}/notes/{DevSeeder.AlicePrivateNoteId}");
        var missing = await client.GetAsync($"/teams/{Apollo}/notes/{Guid.NewGuid()}");

        hidden.StatusCode.Should().Be(missing.StatusCode);
        (await hidden.Content.ReadAsStringAsync()).Should().Be(await missing.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GetNote_MalformedNoteId_ReturnsNotFound()
    {
        var response = await ClientFor(Bob).GetAsync($"/teams/{Apollo}/notes/not-a-guid");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static async Task ShouldBeValidationProblem(HttpResponseMessage response, string field)
    {
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("errors").TryGetProperty(field, out _).Should().BeTrue($"the error should name '{field}'");
    }
}
