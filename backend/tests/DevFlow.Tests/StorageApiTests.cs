using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DevFlow.Tests;

public sealed class StorageApiTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "devflow-storage-api-" + Guid.NewGuid());
    private WebApplicationFactory<Program> factory = null!;
    private HttpClient client = null!;
    public Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["DevFlow:DatabasePath"] = Path.Combine(root, "state.db"), ["DevFlow:ArtifactsRoot"] = Path.Combine(root, "artifacts"), ["DevFlow:WorktreeRoot"] = Path.Combine(root, "worktrees")
        })));
        client = factory.CreateClient(); return Task.CompletedTask;
    }
    public async Task DisposeAsync() { client.Dispose(); await factory.DisposeAsync(); Directory.Delete(root, true); }
    [Fact] public async Task Local_action_guards_inventory_preview_and_cleanup_without_deleting_records()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/storage")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/storage/preview", new { olderThanDays=30 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/storage/cleanup", new { previewId=Guid.NewGuid(), keys=new[]{"unknown"} })).StatusCode);
        client.DefaultRequestHeaders.Add("X-DevFlow-Action", "1");
        var overview = await client.GetFromJsonAsync<JsonElement>("/api/storage");
        Assert.Equal(Path.Combine(root, "state.db"), overview.GetProperty("categories")[0].GetProperty("path").GetString());
        var preview = await client.PostAsJsonAsync("/api/storage/preview", new { olderThanDays=30, artifacts=true, worktrees=true });
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Empty((await preview.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/storage/cleanup", new { previewId=Guid.NewGuid(), keys=new[]{"unknown"} })).StatusCode);
        client.DefaultRequestHeaders.Add("Origin", "https://other.example");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/storage")).StatusCode);
        Assert.True(File.Exists(Path.Combine(root, "state.db")));
    }
}
