using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using DevFlow.Infrastructure;
using Xunit;
namespace DevFlow.Tests;

public sealed class FolderBrowserApiTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "devflow-folder-api-" + Guid.NewGuid());
    private WebApplicationFactory<Program> factory = null!;
    private HttpClient client = null!;
    private readonly Picker picker = new();
    private sealed class Picker : IFolderPicker
    {
        public int Calls;
        public string? InitialPath;
        public string? Result;
        public Task<string?> PickAsync(string? initialPath, CancellationToken ct)
        { Calls++; InitialPath = initialPath; return Task.FromResult(Result); }
    }
    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(root, "選取的專案"));
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["DevFlow:DatabasePath"] = Path.Combine(root, "api.db") }));
            b.ConfigureServices(s => { s.RemoveAll<IFolderPicker>(); s.AddSingleton<IFolderPicker>(picker); });
        });
        client = factory.CreateClient(); return Task.CompletedTask;
    }
    public async Task DisposeAsync() { client.Dispose(); await factory.DisposeAsync(); Directory.Delete(root, true); }
    [Fact] public async Task Browsing_returns_full_folder_paths_and_requires_a_local_explicit_action()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/projects/folders", new { path = root })).StatusCode);
        client.DefaultRequestHeaders.Add("X-DevFlow-Action", "1");
        var response = await client.PostAsJsonAsync("/api/projects/folders", new { path = root });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var listing = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(root, listing.GetProperty("path").GetString());
        Assert.Equal(Path.Combine(root, "選取的專案"), listing.GetProperty("folders")[0].GetProperty("path").GetString());
        client.DefaultRequestHeaders.Add("Origin", "https://other.example");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/projects/folders", new { path = root })).StatusCode);
    }
    [Fact] public async Task Native_selection_checks_origin_before_opening_and_returns_selection_or_cancellation()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/projects/pick-folder", new { path = root })).StatusCode);
        Assert.Equal(0, picker.Calls);
        client.DefaultRequestHeaders.Add("X-DevFlow-Action", "1");
        picker.Result = Path.Combine(root, "選取的專案");
        var selected = await client.PostAsJsonAsync("/api/projects/pick-folder", new { path = root });
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        Assert.Equal(picker.Result, (await selected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("path").GetString());
        Assert.Equal(root, picker.InitialPath);
        picker.Result = null;
        var cancelled = await client.PostAsJsonAsync("/api/projects/pick-folder", new { path = root });
        Assert.Equal(JsonValueKind.Null, (await cancelled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("path").ValueKind);
        client.DefaultRequestHeaders.Add("Origin", "https://other.example");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/projects/pick-folder", new { path = root })).StatusCode);
        Assert.Equal(2, picker.Calls);
    }
}
