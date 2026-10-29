using System.Text.Json;
using DevFlow.Core;
using DevFlow.Infrastructure;
using DevFlow.Runners;
namespace DevFlow.Api;
public interface IAgentCatalog { Task<AgentCandidate[]> ReadAsync(AiSelection primary,CancellationToken ct); }
public sealed class AgentCatalog(CliDiagnostics diagnostics,CliQuotaReader quotas,SqliteTaskStore store):IAgentCatalog
{
    private readonly SemaphoreSlim gate=new(1,1);
    private DateTimeOffset lastQuotaAttempt;
    public async Task<AgentCandidate[]> ReadAsync(AiSelection primary,CancellationToken ct)
    {
        var providers=await diagnostics.ListProvidersAsync(ct);await gate.WaitAsync(ct);
        try{
            var saved=await store.ReadAgentQuotasAsync(ct);var codex=saved.FirstOrDefault(q=>q.Provider=="codex");
            if(providers.Any(p=>p.Id=="codex"&&p.Installed)&&lastQuotaAttempt<DateTimeOffset.UtcNow.AddMinutes(-2)&&(codex is null||codex.ObservedAt<DateTimeOffset.UtcNow.AddMinutes(codex.Source=="UserReported"?-10:-2))){
                lastQuotaAttempt=DateTimeOffset.UtcNow;
                try{if(await quotas.ReadAsync(ct) is {} quota)await store.SaveAgentQuotaAsync(quota,ct);}
                catch(Exception ex)when(ex is IOException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception or JsonException or KeyNotFoundException){if(ct.IsCancellationRequested)throw;}
            }
            saved=await store.ReadAgentQuotasAsync(ct);var tasks=await store.ListTasksAsync(ct);
            return providers.Where(p=>p.Installed).SelectMany(p=>new[]{(string?)null}.Concat(p.Models).Distinct().Select(model=>new AgentCandidate(new(p.Id,model),saved.FirstOrDefault(q=>q.Provider==p.Id),tasks.Count(t=>(t.Status is DevFlow.Core.TaskStatus.Running or DevFlow.Core.TaskStatus.Queued)&&t.Workflow?.Steps?.Any(s=>s.Selection?.Provider==p.Id)==true))))
                .Append(new(primary,saved.FirstOrDefault(q=>q.Provider==primary.Provider))).DistinctBy(c=>(c.Selection.Provider,c.Selection.Model)).ToArray();
        }finally{gate.Release();}
    }
}
