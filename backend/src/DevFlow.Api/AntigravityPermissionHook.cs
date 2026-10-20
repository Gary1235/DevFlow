using DevFlow.Runners;

namespace DevFlow.Api;

public static class AntigravityPermissionHook
{
    public static Task<NativePermissionHook> CreateAsync(DiscussionOperations.Operation operation, string workspace, string auditFile, CancellationToken ct) =>
        NativePermissionHook.CreateAsync(operation, workspace, auditFile, ct, typeof(AntigravityPermissionHook).Assembly.Location);

    public static Task RunClientAsync(string pipeName) => NativePermissionHook.RunClientAsync(pipeName);
}
