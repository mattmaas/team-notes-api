using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using TeamNotes.Api.Data;

namespace TeamNotes.Api.Tests;

/// <summary>Conditional GET: If-None-Match with the current ETag returns 304, checked only after access.</summary>
public class ConditionalGetTests : IDisposable
{
    private static readonly string WelcomeUrl = $"/teams/{DevSeeder.ApolloTeamId}/notes/{DevSeeder.ApolloWelcomeNoteId}";
    private static readonly string AlicePrivateUrl = $"/teams/{DevSeeder.ApolloTeamId}/notes/{DevSeeder.AlicePrivateNoteId}";

    private readonly TeamNotesFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<HttpResponseMessage> Get(Guid user, string url, string? ifNoneMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("X-User-Id", user.ToString());
        if (ifNoneMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        }

        return await _factory.CreateClient().SendAsync(request);
    }

    [Theory]
    [InlineData("\"1\"")]
    [InlineData("W/\"1\"")]
    [InlineData("\"7\", \"1\"")]
    [InlineData("*")]
    public async Task GetNote_IfNoneMatchCurrentVersion_ReturnsNotModifiedWithETagAndNoBody(string ifNoneMatch)
    {
        var response = await Get(DevSeeder.BobId, WelcomeUrl, ifNoneMatch);

        response.StatusCode.Should().Be(HttpStatusCode.NotModified);
        response.Headers.ETag!.Tag.Should().Be("\"1\"");
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task GetNote_IfNoneMatchOldVersion_ReturnsFullNote()
    {
        var edit = new HttpRequestMessage(HttpMethod.Put, WelcomeUrl) { Content = JsonContent.Create(new { title = "Edited", body = "" }) };
        edit.Headers.TryAddWithoutValidation("X-User-Id", DevSeeder.BobId.ToString());
        edit.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        (await _factory.CreateClient().SendAsync(edit)).StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await Get(DevSeeder.BobId, WelcomeUrl, "\"1\"");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.ETag!.Tag.Should().Be("\"2\"");
    }

    [Fact]
    public async Task GetNote_IfNoneMatchOnHiddenNote_ReturnsNotFoundNot304()
    {
        // A 304 would confirm the private note exists and its version; access is checked first.
        var response = await Get(DevSeeder.BobId, AlicePrivateUrl, "\"1\"");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetNote_IfNoneMatchNonMember_ReturnsNotFound()
    {
        var response = await Get(DevSeeder.CarolId, WelcomeUrl, "*");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
