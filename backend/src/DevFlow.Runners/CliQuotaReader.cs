using System.Text.Json;
using DevFlow.Core;
namespace DevFlow.Runners;
public sealed class CliQuotaReader(IProcessRunner process,CliRegistry registry,string workingDirectory)
{
    public static ProviderQuota? Parse(JsonElement result,DateTimeOffset now)
    {
        var windows=new List<QuotaWindow>();
        void Read(JsonElement bucket,string name){
            foreach(var key in new[]{"primary","secondary"})if(bucket.TryGetProperty(key,out var window)&&window.ValueKind==JsonValueKind.Object&&
                window.TryGetProperty("usedPercent",out var used)&&used.ValueKind==JsonValueKind.Number&&used.TryGetDouble(out var value)&&double.IsFinite(value)&&value>=0&&value<=100){
                DateTimeOffset? reset=null;
                if(window.TryGetProperty("resetsAt",out var at)&&at.ValueKind==JsonValueKind.Number&&at.TryGetInt64(out var timestamp))try{reset=DateTimeOffset.FromUnixTimeSeconds(timestamp);}catch(ArgumentOutOfRangeException){}
                windows.Add(new(name+"/"+key,100-value,reset));
            }
        }
        if(result.TryGetProperty("rateLimitsByLimitId",out var buckets)&&buckets.ValueKind==JsonValueKind.Object)
            foreach(var bucket in buckets.EnumerateObject())if(bucket.Value.ValueKind==JsonValueKind.Object)Read(bucket.Value,bucket.Name);
        if(windows.Count==0&&result.TryGetProperty("rateLimits",out var legacy)&&legacy.ValueKind==JsonValueKind.Object)Read(legacy,"account");
        return windows.Count==0?null:new("codex",windows.ToArray(),now,"CLI");
    }
    public async Task<ProviderQuota?> ReadAsync(CancellationToken ct)
    {
        if(process is not IInteractiveProcessRunner interactive)return null;
        var command=registry.Require("codex");ProviderQuota? quota=null;
        using var limit=CancellationTokenSource.CreateLinkedTokenSource(ct);limit.CancelAfter(TimeSpan.FromSeconds(12));
        Directory.CreateDirectory(workingDirectory);
        await interactive.RunInteractiveAsync(command.Executable,[..command.PrefixArguments,"app-server","--listen","stdio://"],workingDirectory,async(session,token)=>{
            async Task<JsonElement> Response(int id){
                while(await session.ReadAsync(token) is {} frame){
                    if(frame.TryGetProperty("id",out var responseId)&&responseId.ValueKind==JsonValueKind.Number&&responseId.GetInt32()==id){
                        if(frame.TryGetProperty("error",out _))throw new InvalidOperationException("Quota query unavailable.");return frame.GetProperty("result").Clone();
                    }
                }
                throw new IOException("CLI quota channel closed.");
            }
            await session.SendAsync(new{id=1,method="initialize",@params=new{clientInfo=new{name="devflow",version="0.7"}}},token);await Response(1);
            await session.SendAsync(new{method="initialized",@params=new{}},token);
            await session.SendAsync(new{id=2,method="account/rateLimits/read",@params=new{}},token);
            quota=Parse(await Response(2),DateTimeOffset.UtcNow);
        },limit.Token);
        return quota;
    }
}
