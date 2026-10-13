namespace DevFlow.Contracts;
public sealed record CreateProjectRequest(string Name, string RepositoryPath, string BaseBranch, string[] ValidationArguments);
public sealed record CreateTaskRequest(Guid ProjectId, string Title, string Description, string AcceptanceCriteria);
public sealed record SetPlanRequest(string Text);
