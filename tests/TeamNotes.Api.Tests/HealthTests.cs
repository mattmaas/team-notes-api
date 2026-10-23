using System.Net;
using FluentAssertions;

namespace TeamNotes.Api.Tests;

public class HealthTests
{
    [Fact]
    public async Task GetHealth_ServiceRunning_ReturnsOk()
    {
        using var factory = new TeamNotesFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
