using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DevFlow.Tests;

public sealed class DesktopApiTests
{
    [Fact]
    public async Task Desktop_secret_protects_health_static_ui_and_mutations()
    {
        var directory = Path.Combine(Path.GetTempPath(), "devflow-desktop-api-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
                b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DevFlow:DatabasePath"] = Path.Combine(directory, "desktop.db"),
                    ["DevFlow:DesktopToken"] = "desktop-session-secret"
                })));
            using var client = factory.CreateClient();
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/health")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/assets/app.js")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/tasks", null)).StatusCode);
            client.DefaultRequestHeaders.Add("X-DevFlow-Desktop", "wrong");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/projects")).StatusCode);
            client.DefaultRequestHeaders.Remove("X-DevFlow-Desktop");
            client.DefaultRequestHeaders.Add("X-DevFlow-Desktop", "desktop-session-secret");
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/projects")).StatusCode);
        }
        finally { Directory.Delete(directory, true); }
    }
}
