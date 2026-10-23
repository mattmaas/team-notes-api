using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TeamNotes.Api.Data;
using TeamNotes.Api.Domain;

namespace TeamNotes.Api.Tests;

/// <summary>
/// GET /teams/{teamId}/notes. Contract (docs/DESIGN.md, Listing): visibility filtered in the query before counting
/// and paging; order UpdatedAt desc then Id desc; page >= 1, pageSize 1..100 (default 20), invalid -> 400;
/// q is a trimmed, literal, case-insensitive substring of title or body, at most 200 characters.
/// Seed: Apollo has Alice's team note (welcome) and Alice's private draft. Bob is an Apollo member, Carol is not.
/// </summary>
public class NoteListTests : IDisposable
{
    private static readonly Guid Alice = DevSeeder.AliceId;
    private static readonly Guid Bob = DevSeeder.BobId;
    private static readonly Guid Carol = DevSeeder.CarolId;
    private static readonly Guid Apollo = DevSeeder.ApolloTeamId;
    private static readonly Guid Gemini = DevSeeder.GeminiTeamId;
    private static readonly string ApolloNotes = $"/teams/{DevSeeder.ApolloTeamId}/notes";

    private readonly TeamNotesFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient ClientFor(Guid userId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId.ToString());
        return client;
    }

    private async Task<JsonElement> List(Guid user, string query = "")
    {
        var response = await ClientFor(user).GetAsync(ApolloNotes + query);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<string> Titles(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()!).ToList();

    /// <summary>Inserts notes directly with controlled timestamps, so ordering expectations are exact.</summary>
    private void InsertNotes(params (string Title, string Body, Guid Author, NoteVisibility Visibility, DateTime UpdatedAt, Guid? Id)[] notes)
    {
        using var db = _factory.CreateDbContext();
        foreach (var n in notes)
        {
            db.Notes.Add(new Note
            {
                Id = n.Id ?? Guid.NewGuid(),
                TeamId = Apollo,
                AuthorId = n.Author,
                Title = n.Title,
                Body = n.Body,
                Visibility = n.Visibility,
                CreatedAt = n.UpdatedAt,
                UpdatedAt = n.UpdatedAt,
            });
        }

        db.SaveChanges();
    }

    private static DateTime At(int minute) => new(2026, 3, 1, 12, minute, 0, DateTimeKind.Utc);

    // ---- access ----

    [Fact]
    public async Task ListNotes_NonMember_ReturnsNotFound()
    {
        var response = await ClientFor(Carol).GetAsync(ApolloNotes);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListNotes_UnknownTeam_ReturnsNotFound()
    {
        var response = await ClientFor(Bob).GetAsync($"/teams/{Guid.NewGuid()}/notes");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListNotes_NoIdentity_ReturnsUnauthorized()
    {
        var response = await _factory.CreateClient().GetAsync(ApolloNotes);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ListNotes_NonMemberWithInvalidPaging_ReturnsNotFoundNotBadRequest()
    {
        var response = await ClientFor(Carol).GetAsync(ApolloNotes + "?page=0");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- visibility before paging ----

    [Fact]
    public async Task ListNotes_Teammate_SeesTeamNoteButNotOthersPrivateNote()
    {
        var page = await List(Bob);

        Titles(page).Should().Equal("Welcome to Apollo");
        page.GetProperty("totalCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task ListNotes_Author_SeesOwnPrivateNote()
    {
        var page = await List(Alice);

        Titles(page).Should().BeEquivalentTo("Welcome to Apollo", "Alice's private draft");
        page.GetProperty("totalCount").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task ListNotes_TeamOwner_DoesNotSeeMembersPrivateNotes()
    {
        InsertNotes(("Bob's secret", "", Bob, NoteVisibility.Private, At(1), null));

        var page = await List(Alice);

        Titles(page).Should().NotContain("Bob's secret");
        page.GetProperty("totalCount").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task ListNotes_ManyHiddenNotes_PageIsFullAndCountExcludesThem()
    {
        // 30 of Alice's private notes are newer than Bob's visible notes. If filtering ran after paging, Bob's first
        // page would be empty or short and totalCount would include notes he cannot see.
        InsertNotes(Enumerable.Range(0, 30)
            .Select(i => ($"Hidden {i}", "", Alice, NoteVisibility.Private, At(50), (Guid?)null))
            .Concat(Enumerable.Range(0, 5).Select(i => ($"Visible {i}", "", Bob, NoteVisibility.Team, At(10 + i), (Guid?)null)))
            .ToArray());

        var page = await List(Bob, "?pageSize=3");

        Titles(page).Should().Equal("Visible 4", "Visible 3", "Visible 2");
        page.GetProperty("totalCount").GetInt32().Should().Be(6, "5 visible inserted + the seeded welcome note");
    }

    [Fact]
    public async Task ListNotes_OtherTeamsNotes_AreNeverIncluded()
    {
        // Bob is in both teams; a Gemini note must not appear when listing Apollo.
        var created = await ClientFor(Bob).PostAsJsonAsync($"/teams/{Gemini}/notes", new { title = "Gemini only", body = "", visibility = "Team" });
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        Titles(await List(Bob)).Should().NotContain("Gemini only");
    }

    [Fact]
    public async Task ListNotes_ResultsMatchSingleNoteAccessRule()
    {
        // The list filter is SQL, the single-note rule is NoteAccess.CanRead. For every user, the listed ids must be
        // exactly the team's notes that CanRead allows, so the two rules cannot drift apart.
        InsertNotes(
            ("Bob team", "", Bob, NoteVisibility.Team, At(1), null),
            ("Bob private", "", Bob, NoteVisibility.Private, At(2), null),
            ("Alice team 2", "", Alice, NoteVisibility.Team, At(3), null));

        List<Note> all;
        using (var db = _factory.CreateDbContext())
        {
            all = db.Notes.Where(n => n.TeamId == Apollo).ToList();
        }

        foreach (var user in new[] { Alice, Bob })
        {
            var page = await List(user, "?pageSize=100");
            var listed = page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid());
            listed.Should().BeEquivalentTo(all.Where(n => NoteAccess.CanRead(n, user)).Select(n => n.Id), $"user {user}");
        }
    }

    // ---- ordering ----

    [Fact]
    public async Task ListNotes_Order_IsMostRecentlyUpdatedFirst()
    {
        InsertNotes(
            ("Old", "", Bob, NoteVisibility.Team, At(1), null),
            ("Newest", "", Bob, NoteVisibility.Team, At(30), null),
            ("Middle", "", Bob, NoteVisibility.Team, At(15), null));

        Titles(await List(Bob)).Should().Equal("Newest", "Middle", "Old", "Welcome to Apollo");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ListNotes_SameUpdatedAt_TieBrokenByIdDescendingRegardlessOfInsertOrder(bool highIdFirst)
    {
        // Run with both insertion orders: without an explicit tie-breaker SQLite returns ties in storage (rowid) order,
        // which would satisfy one of these cases by luck but not both. Each note is saved separately because EF may
        // reorder a batch of inserts by key, which made a first version of this test pass without the tie-breaker.
        var low = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var high = Guid.Parse("ffffffff-0000-0000-0000-000000000001");
        var lowNote = ("Low id", "", Bob, NoteVisibility.Team, At(5), (Guid?)low);
        var highNote = ("High id", "", Bob, NoteVisibility.Team, At(5), (Guid?)high);
        InsertNotes(highIdFirst ? highNote : lowNote);
        InsertNotes(highIdFirst ? lowNote : highNote);

        Titles(await List(Bob)).Take(2).Should().Equal("High id", "Low id");
    }

    [Fact]
    public async Task ListNotes_WalkingAllPages_VisitsEveryVisibleNoteExactlyOnce()
    {
        // Many notes share timestamps; without a unique tie-breaker, items could repeat or vanish between pages.
        InsertNotes(Enumerable.Range(0, 23)
            .Select(i => ($"Note {i}", "", Bob, NoteVisibility.Team, At(i % 4), (Guid?)null))
            .ToArray());

        var seen = new List<Guid>();
        for (var pageNumber = 1; pageNumber <= 5; pageNumber++)
        {
            var page = await List(Bob, $"?page={pageNumber}&pageSize=5");
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        }

        seen.Should().HaveCount(24).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task ListNotes_AfterEdit_EditedNoteMovesToTop()
    {
        InsertNotes(("Recent", "", Bob, NoteVisibility.Team, DateTime.UtcNow.AddMinutes(-1), null));
        var edit = new HttpRequestMessage(HttpMethod.Put, $"{ApolloNotes}/{DevSeeder.ApolloWelcomeNoteId}")
        {
            Content = JsonContent.Create(new { title = "Welcome (edited)", body = "" }),
        };
        edit.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        (await ClientFor(Bob).SendAsync(edit)).StatusCode.Should().Be(HttpStatusCode.OK);

        Titles(await List(Bob)).First().Should().Be("Welcome (edited)");
    }

    // ---- paging ----

    [Fact]
    public async Task ListNotes_NoPagingParameters_UsesPageOneSizeTwenty()
    {
        InsertNotes(Enumerable.Range(0, 25).Select(i => ($"N{i}", "", Bob, NoteVisibility.Team, At(i), (Guid?)null)).ToArray());

        var page = await List(Bob);

        page.GetProperty("page").GetInt32().Should().Be(1);
        page.GetProperty("pageSize").GetInt32().Should().Be(20);
        page.GetProperty("items").GetArrayLength().Should().Be(20);
        page.GetProperty("totalCount").GetInt32().Should().Be(26);
    }

    [Fact]
    public async Task ListNotes_LastPartialPage_ReturnsRemainder()
    {
        InsertNotes(Enumerable.Range(0, 6).Select(i => ($"N{i}", "", Bob, NoteVisibility.Team, At(i), (Guid?)null)).ToArray());

        var page = await List(Bob, "?page=3&pageSize=3");

        Titles(page).Should().Equal("Welcome to Apollo");
        page.GetProperty("totalCount").GetInt32().Should().Be(7);
    }

    [Fact]
    public async Task ListNotes_PageBeyondEnd_ReturnsEmptyItemsWithTotal()
    {
        var page = await List(Bob, "?page=50");

        page.GetProperty("items").GetArrayLength().Should().Be(0);
        page.GetProperty("totalCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task ListNotes_PageSize100_IsAllowed()
    {
        var page = await List(Bob, "?pageSize=100");

        page.GetProperty("pageSize").GetInt32().Should().Be(100);
    }

    [Fact]
    public async Task ListNotes_EmptyTeam_ReturnsEmptyPage()
    {
        var page = await (await ClientFor(Bob).GetAsync($"/teams/{Gemini}/notes")).Content.ReadFromJsonAsync<JsonElement>();

        page.GetProperty("items").GetArrayLength().Should().Be(0);
        page.GetProperty("totalCount").GetInt32().Should().Be(0);
    }

    [Theory]
    [InlineData("?page=0", "page")]
    [InlineData("?page=-1", "page")]
    [InlineData("?page=2147483647&pageSize=100", "page")]
    [InlineData("?pageSize=0", "pageSize")]
    [InlineData("?pageSize=101", "pageSize")]
    [InlineData("?pageSize=-5", "pageSize")]
    public async Task ListNotes_OutOfRangePaging_ReturnsValidationProblem(string query, string field)
    {
        var response = await ClientFor(Bob).GetAsync(ApolloNotes + query);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("errors").TryGetProperty(field, out _).Should().BeTrue();
    }

    [Theory]
    [InlineData("?page=abc", "page")]
    [InlineData("?page=", "page")]
    [InlineData("?page=+1", "page")]
    [InlineData("?page=1&page=2", "page")]
    [InlineData("?page=99999999999", "page")]
    [InlineData("?pageSize=1.5", "pageSize")]
    [InlineData("?pageSize=", "pageSize")]
    [InlineData("?pageSize=99999999999", "pageSize")]
    [InlineData("?q=a&q=b", "q")]
    [InlineData("?q=%00zzz", "q")]
    public async Task ListNotes_UnparseableOrRepeatedParameter_ReturnsValidationProblemNamingField(string query, string field)
    {
        var response = await ClientFor(Bob).GetAsync(ApolloNotes + query);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("errors").TryGetProperty(field, out _).Should().BeTrue($"the error for {query} should name '{field}'");
    }

    [Fact]
    public async Task ListNotes_SearchAppliesBeforePaging()
    {
        // Only one note matches; with pageSize=1 it must be the first and only item, whatever its age.
        InsertNotes(
            ("Old match", "needle", Bob, NoteVisibility.Team, At(1), null),
            ("Newer", "", Bob, NoteVisibility.Team, At(40), null),
            ("Newest", "", Bob, NoteVisibility.Team, At(50), null));

        var page = await List(Bob, "?q=needle&pageSize=1");

        Titles(page).Should().Equal("Old match");
        page.GetProperty("totalCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task ListNotes_SearchNonAscii_CaseFoldingIsAsciiOnly()
    {
        // Locks the documented limitation (DESIGN.md, Listing and search): SQLite's LIKE folds ASCII letters only.
        InsertNotes(("Café notes", "", Bob, NoteVisibility.Team, At(1), null));

        Titles(await List(Bob, "?q=" + Uri.EscapeDataString("CAFÉ"))).Should().BeEmpty();
        Titles(await List(Bob, "?q=" + Uri.EscapeDataString("CAFé"))).Should().Equal("Café notes");
    }

    // ---- search ----

    [Fact]
    public async Task ListNotes_SearchMatchesTitleOrBody_CaseInsensitive()
    {
        InsertNotes(
            ("Release PLAN", "", Bob, NoteVisibility.Team, At(1), null),
            ("Groceries", "remember the plan", Bob, NoteVisibility.Team, At(2), null),
            ("Unrelated", "nothing here", Bob, NoteVisibility.Team, At(3), null));

        var page = await List(Bob, "?q=plan");

        Titles(page).Should().Equal("Groceries", "Release PLAN");
        page.GetProperty("totalCount").GetInt32().Should().Be(2);
    }

    [Theory]
    [InlineData("100%", "Discount 100% off")]
    [InlineData("a_b", "file a_b.txt")]
    [InlineData("C:\\temp", "path C:\\temp")]
    public async Task ListNotes_SearchWithLikeWildcards_MatchesLiterally(string q, string expectedTitle)
    {
        InsertNotes(
            ("Discount 100% off", "", Bob, NoteVisibility.Team, At(1), null),
            ("Discount 1000 off", "", Bob, NoteVisibility.Team, At(2), null),
            ("file a_b.txt", "", Bob, NoteVisibility.Team, At(3), null),
            ("file axb.txt", "", Bob, NoteVisibility.Team, At(4), null),
            ("path C:\\temp", "", Bob, NoteVisibility.Team, At(5), null),
            ("path C:temp", "", Bob, NoteVisibility.Team, At(6), null));

        var page = await List(Bob, "?q=" + Uri.EscapeDataString(q));

        Titles(page).Should().Equal(expectedTitle);
    }

    [Fact]
    public async Task ListNotes_SearchPercentAlone_DoesNotMatchEverything()
    {
        var page = await List(Bob, "?q=%25");

        page.GetProperty("totalCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task ListNotes_SearchDoesNotRevealHiddenNotes()
    {
        // "private" appears in Alice's private draft. Bob searching for it must get nothing, including in the count.
        var page = await List(Bob, "?q=private");

        page.GetProperty("totalCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task ListNotes_SearchIsTrimmedAndBlankMeansNoFilter()
    {
        (await List(Bob, "?q=%20%20")).GetProperty("totalCount").GetInt32().Should().Be(1);
        Titles(await List(Bob, "?q=%20welcome%20")).Should().Equal("Welcome to Apollo");
    }

    [Fact]
    public async Task ListNotes_SearchNonAscii_MatchesExactCase()
    {
        // SQLite's built-in case folding is ASCII-only; documented limitation, exact-case non-ASCII still matches.
        InsertNotes(("Café notes", "", Bob, NoteVisibility.Team, At(1), null));

        Titles(await List(Bob, "?q=" + Uri.EscapeDataString("Café"))).Should().Equal("Café notes");
    }

    [Fact]
    public async Task ListNotes_SearchOver200Characters_ReturnsValidationProblem()
    {
        var response = await ClientFor(Bob).GetAsync(ApolloNotes + "?q=" + new string('q', 201));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty("q", out _).Should().BeTrue();
    }

    [Fact]
    public async Task ListNotes_SearchAt200Characters_IsAccepted()
    {
        var response = await ClientFor(Bob).GetAsync(ApolloNotes + "?q=" + new string('q', 200));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- item shape ----

    [Fact]
    public async Task ListNotes_Items_AreSummariesWithoutBody()
    {
        var item = (await List(Bob)).GetProperty("items")[0];

        item.TryGetProperty("body", out _).Should().BeFalse();
        item.GetProperty("id").GetGuid().Should().Be(DevSeeder.ApolloWelcomeNoteId);
        item.GetProperty("authorId").GetGuid().Should().Be(Alice);
        item.GetProperty("visibility").GetString().Should().Be("Team");
        item.GetProperty("version").GetInt32().Should().Be(1);
        item.GetProperty("updatedAt").GetString().Should().EndWith("Z");
        item.GetProperty("createdAt").GetString().Should().EndWith("Z");
    }
}
