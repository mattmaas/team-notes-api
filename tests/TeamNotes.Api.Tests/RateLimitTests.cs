using System.Net;
using FluentAssertions;
using TeamNotes.Api.Data;

namespace TeamNotes.Api.Tests;

/// <summary>Per-user fixed-window rate limit on API endpoints: over the limit is 429 with Retry-After.</summary>
public class RateLimitTests
{
    private static HttpClient ClientFor(TeamNotesFactory factory, Guid userId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId.ToString());
        return client;
    }

    [Fact]
    public async Task GetMyTeams_OverPerUserLimit_ReturnsTooManyRequestsWithRetryAfter()
    {
        using var factory = new TeamNotesFactory(rateLimitPermits: 3);
        var bob = ClientFor(factory, DevSeeder.BobId);

        for (var i = 0; i < 3; i++)
        {
            (await bob.GetAsync("/me/teams")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var limited = await bob.GetAsync("/me/teams");

        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.Should().NotBeNull();
        limited.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task RateLimit_IsPerUser_OneUserDoesNotBlockAnother()
    {
        using var factory = new TeamNotesFactory(rateLimitPermits: 2);
        var bob = ClientFor(factory, DevSeeder.BobId);
        var alice = ClientFor(factory, DevSeeder.AliceId);

        await bob.GetAsync("/me/teams");
        await bob.GetAsync("/me/teams");
        (await bob.GetAsync("/me/teams")).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        (await alice.GetAsync("/me/teams")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RateLimit_AppliesToNoteRoutes()
    {
        using var factory = new TeamNotesFactory(rateLimitPermits: 1);
        var bob = ClientFor(factory, DevSeeder.BobId);
        var url = $"/teams/{DevSeeder.ApolloTeamId}/notes";

        (await bob.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await bob.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Health_IsNotRateLimited()
    {
        using var factory = new TeamNotesFactory(rateLimitPermits: 1);
        var client = factory.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            (await client.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }
}
