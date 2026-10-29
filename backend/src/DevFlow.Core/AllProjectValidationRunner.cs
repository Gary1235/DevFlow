namespace DevFlow.Core;

// Simple tasks use the project's saved validation profiles, without per-task setup.
public sealed class AllProjectValidationRunner(IValidationRunner inner) : IValidationRunner, IValidationAttemptRunner
{
    // Validation, supervisor guidance and optional review have nested retry loops.
    // Allocate artifacts across all of them so a new loop cannot overwrite an earlier one.
    private int invocation;
    public IValidationRunner ForAttempt(int value) => this;
    private IValidationRunner Next() => inner is IValidationAttemptRunner attempts
        ? attempts.ForAttempt(Interlocked.Increment(ref invocation)) : inner;
    public async Task<ValidationEvidence> ValidateAsync(ProjectDefinition project, Workspace workspace, Guid runId, TddPhase phase, CancellationToken ct)
    {
        if(project.ValidationProfiles is not {Length:>0} profiles) {
            return await Next().ValidateAsync(project,workspace,runId,phase,ct);
        }
        var evidence=new List<(ValidationProfile Profile,ValidationEvidence Evidence)>();
        for(var i=0;i<profiles.Length;i++){
            ct.ThrowIfCancellationRequested();
            var result=await Next().ValidateAsync(project with{DefaultValidationProfileId=profiles[i].Id},workspace,runId,phase,ct);
            if(result.Kind=="DotnetTest"&&result.ExitCode==0&&result.Total==0)
                result=result with{ExitCode=1,Error="驗證沒有產生逐項測試證據。",FailureKind="NoTestEvidence"};
            evidence.Add((profiles[i],result));
        }
        var failures=evidence.Where(e=>e.Evidence.ExitCode!=0||e.Evidence.Failed!=0).ToArray();
        return new(failures.Length==0?0:failures[0].Evidence.ExitCode==0?1:failures[0].Evidence.ExitCode,
            evidence.Sum(e=>e.Evidence.Total),evidence.Sum(e=>e.Evidence.Failed),
            evidence.SelectMany(e=>e.Evidence.PassedTests.Select(t=>e.Profile.Id+"::"+t)).ToArray(),
            evidence.SelectMany(e=>e.Evidence.FailedTests.Select(t=>e.Profile.Id+"::"+t)).ToArray(),"Command",
            failures.Length==0?null:string.Join("\n",failures.Select(e=>"["+e.Profile.Name+"] "+e.Evidence.Error)),
            failures.Length==0?null:failures[0].Evidence.FailureKind);
    }
}
