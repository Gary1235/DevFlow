using DevFlow.Core;
namespace DevFlow.Contracts;
public sealed record CreateProjectRequest(string Name, string RepositoryPath, string BaseBranch, string[] ValidationArguments, AiConfiguration? AiDefaults = null);
public sealed record CreateTaskRequest(Guid ProjectId, string Title, string Description, string AcceptanceCriteria, bool AutoPlan = false,
    AiSelection? Planning = null, AiSelection? Implementation = null, Guid[]? RuleIds = null,
    string? TaskRules = null, ExecutionPolicy? Policy = null, WorkflowDefinition? Workflow = null,
    Guid? SourceDiscussionId = null, Guid? SourceDraftId = null, bool AcknowledgeOpenQuestions = false);
public sealed record SetProjectAiRequest(AiConfiguration AiDefaults, int ExpectedVersion);
public sealed record SetPlanRequest(string Text, int? ExpectedVersion = null);
public sealed record ReviewTaskRequest(Guid RunId, string ResultCommit, string Decision, int ExpectedVersion, string? Comments = null);
public sealed record RetryTaskRequest(int ExpectedVersion, string? Instructions = null, string Scope = "Resume");
public sealed record MergeTaskRequest(Guid RunId, string ResultCommit, string ExpectedTargetCommit, int ExpectedVersion);
public sealed record GeneratePlanRequest(int ExpectedVersion);
public sealed record RestartTaskRequest(int ExpectedVersion);
public sealed record ApprovePlanRequest(int ExpectedVersion, int PlanRevision);
public sealed record SetWorkflowRequest(int ExpectedVersion, WorkflowDefinition Workflow, AiSelection? PlanningSelection = null);
public sealed record ContinueWorkflowRequest(int ExpectedVersion, string? Instructions = null);
public sealed record SetTaskModelRequest(int ExpectedVersion, string Stage, AiSelection Selection);
