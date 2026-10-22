using System.Text.Json;
using DevFlow.Api;
using DevFlow.Core;
using DevFlow.Infrastructure;
using Xunit;

namespace DevFlow.Tests;

public sealed class DiscussionPermissionRuleTests
{
    [Fact] public async Task Default_policy_automatically_reads_across_providers_but_never_expands_discussion_to_writing()
    {
        var root = Path.Combine(Path.GetTempPath(), "devflow-discussion-policy-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try {
            var store = new SqliteTaskStore(Path.Combine(root, "test.db")); await store.InitializeAsync();
            var project = new ProjectDefinition(Guid.NewGuid(), "Test", root, "main", ["test"]); await store.AddProjectAsync(project);
            var discussion = new Discussion(Guid.NewGuid(), project.Id, "Test", new("codex"), [], [], [], 0, DateTimeOffset.UtcNow); await store.AddDiscussionAsync(discussion, default);
            foreach (var provider in new[] { "codex", "claude", "gemini", "antigravity" }) {
                using var operation = new DiscussionOperations.Operation(Guid.NewGuid(), new(provider), default);
                operation.ConfigurePermissions(store, project.Id, discussion.Id);
                Assert.True(await operation.RequestPermissionAsync("read_file", JsonSerializer.SerializeToElement(new { Path = "src/component.json" }), root, default));
                Assert.Equal("AllowedByPolicy", Assert.Single(operation.Permissions).State);
                Assert.True(await operation.RequestPermissionAsync("read_file",JsonSerializer.SerializeToElement(new{Path=".env"}),root,default));
                Assert.Equal("AllowedByPolicy",operation.Permissions.Last().State);
                var pending = operation.RequestPermissionAsync("write_file", JsonSerializer.SerializeToElement(new { Path = "src/component.ts" }), root, default);
                Assert.False(pending.IsCompleted); Assert.True(operation.Permissions.Last().Risk.FixedBoundary);
                operation.ReplyPermission(operation.Permissions.Last().Id, false); Assert.False(await pending);
            }
        } finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("Discussion")]
    
    public async Task Discussion_rules_are_reused_across_providers_with_the_selected_scope(string scope)
    {
        var root = Path.Combine(Path.GetTempPath(), "devflow-discussion-rule-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var store = new SqliteTaskStore(Path.Combine(root,"test.db")); await store.InitializeAsync();
            await store.SavePermissionPolicyAsync(new(AutomationEnabled: false), 0);
            var project = new ProjectDefinition(Guid.NewGuid(),"Test",root,"main",["test"]);
            await store.AddProjectAsync(project);
            var discussion = new Discussion(Guid.NewGuid(),project.Id,"Test",new("codex"),[],[],[],0,DateTimeOffset.UtcNow);
            await store.AddDiscussionAsync(discussion,default);
            using var operation = new DiscussionOperations.Operation(Guid.NewGuid(),new("codex"),default);
            operation.ConfigurePermissions(store,project.Id,discussion.Id);
            var args = JsonSerializer.SerializeToElement(new { Path=Path.Combine(root,"first.ts") });
            var pending = operation.RequestPermissionAsync("read_file",args,root,default);
            var request = Assert.Single(operation.Permissions);
            Assert.False(pending.IsCompleted);
            Assert.Throws<ArgumentException>(()=>operation.ReplyPermission(request.Id,true,new("Project","GeneralRead",".")));
            operation.ReplyPermission(request.Id,true,new(scope,"GeneralRead",".","*"));
            Assert.True(await pending);
            var saved = Assert.Single(await store.ListPermissionRulesAsync());
            Assert.Equal(scope=="Discussion"?discussion.Id:(Guid?)null,saved.DiscussionId);
            foreach(var provider in new[]{"codex","claude","gemini","antigravity"})
            {
                using var next = new DiscussionOperations.Operation(Guid.NewGuid(),new(provider),default);
                next.ConfigurePermissions(store,project.Id,discussion.Id);
                Assert.True(await next.RequestPermissionAsync("read_file",args,root,default));
                Assert.Equal("AllowedByRule",Assert.Single(next.Permissions).State);
            }
            Assert.Equal(scope=="Project",await store.MatchDiscussionRuleAsync(project.Id,Guid.NewGuid(),"claude","read_file",args,root,default) is not null);
            var sensitive = operation.RequestPermissionAsync("read_file",JsonSerializer.SerializeToElement(new{Path=Path.Combine(root,".env")}),root,default);
            var sensitiveRequest=operation.Permissions.Last();Assert.False(sensitive.IsCompleted);Assert.False(sensitiveRequest.Risk.BatchAllowed);
            Assert.Throws<ArgumentException>(()=>operation.ReplyPermission(sensitiveRequest.Id,true,new("Discussion","GeneralRead",".")));
            operation.ReplyPermission(sensitiveRequest.Id,false);Assert.False(await sensitive);
            using var stopped = new DiscussionOperations.Operation(Guid.NewGuid(),new("gemini"),default);
            stopped.ConfigurePermissions(store,project.Id,discussion.Id);
            var otherArgs = JsonSerializer.SerializeToElement(new { CommandLine="unrecognized-tool",Cwd=root });
            var waiting = stopped.RequestPermissionAsync("run_command",otherArgs,root,default);
            var stale = Assert.Single(stopped.Permissions); stopped.Stop();
            Assert.False(await waiting);
            Assert.Throws<ConflictException>(()=>stopped.ReplyPermission(stale.Id,true,new("Project","CommandPrefix","git status")));
            Assert.Single(await store.ListPermissionRulesAsync());
        }
        finally { Directory.Delete(root,true); }
    }
}
