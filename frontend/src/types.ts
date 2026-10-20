export type ProviderId='codex'|'antigravity'|'gemini'|'claude';
export interface Selection {provider:ProviderId;model:string|null;reasoningEffort?:string|null}
export interface AiConfiguration {planning:Selection|null;implementation:Selection|null}
export interface Project {id:string;name:string;repositoryPath:string;baseBranch:string;validationArguments:string[];aiDefaults:AiConfiguration|null;version:number;defaultRuleIds?:string[]}
export interface Plan {text:string;revision:number;baseCommit:string|null;problem?:string|null}
export interface WorkTask {id:string;projectId:string;title:string;description:string;acceptanceCriteria:string;status:string;plan:Plan|null;version:number;createdAt:string;ai:AiConfiguration|null;revisionInstructions?:string|null;rules?:Rule[];taskRules?:string|null;policy?:ExecutionPolicy;workflow?:Workflow;sourceDiscussionId?:string|null;sourceDraftId?:string|null;autoReviewAttempt?:number;stepResults?:WorkflowStepResult[];activeStepId?:string|null}
export interface ModelOption {id:string;displayName:string;reasoningEfforts:string[];defaultReasoningEffort:string|null;isDefault:boolean}
export interface Provider {id:ProviderId;installed:boolean;executable:string|null;models:string[];modelsVerified:boolean;modelDetails?:ModelOption[]|null;configuredDefaultModel?:string|null;modelCatalogState?:string|null;modelCatalogMessage?:string|null}
export interface RuntimeSnapshot {startedAt:string;updatedAt:string;stage:string;lastActivityAt:string;lastOutputAt:string|null;process:string|null;processRunning:boolean;outputCharacters:number;recent:{at:string;text:string}[];currentWork?:string|null}
export interface Run {id:string;taskId:string;kind:'Planning'|'Implementation';status:string;worktreePath:string|null;baseCommit:string|null;resultCommit:string|null;error:string|null;startedAt:string;finishedAt:string|null;selection:Selection|null;aiReviews?:AiReview[];activity?:RuntimeSnapshot|null}
export interface PlanRevision extends Plan {id:string;source:string;createdAt:string;runId:string|null}
export interface TaskEvent {sequence:number;kind:string;message:string;createdAt:string}
export interface Review {id:string;decision:string;comments:string|null;resultCommit:string;createdAt:string}
export interface Merge {id:string;state:string;expectedTargetCommit:string;resultCommit:string;error:string|null;startedAt:string}
export interface Artifact {path:string;bytes:number}
export interface ArtifactPage {path:string;text:string;offset:number;nextOffset:number;hasMore:boolean}
export interface Validation {phase:string;exitCode:number;total:number;failed:number;evidencePath:string;stepId?:string|null}
export interface Diff {baseCommit:string;resultCommit:string;patch:string}
export interface GitState {branch:string;headCommit:string;clean:boolean}
export interface Diagnostic {version:string|null;loginState:string;executionState:string;message:string;errorCode?:string|null;exitCode?:number|null}
export interface LoginSession {id:string;provider:string;state:string;message:string}
export interface BaseData {projects:Project[];tasks:WorkTask[];providers:Provider[]}
export interface DetailData {task:WorkTask;runs:Run[];plans:PlanRevision[];events:TaskEvent[];reviews:Review[];merges:Merge[]}
export interface Rule {id:string;name:string;content:string;stages:string[];version:number;archived?:boolean;requiresTdd?:boolean}
export interface ExecutionPolicy {tdd:boolean;allowedPaths:string[]}
export type WorkflowStepKind='Implementation'|'Review'|'Validation'|'Approval';
export interface WorkflowStep {id:string;name:string;kind:WorkflowStepKind;selection:Selection|null;instructions:string;allowedPaths:string[];waitForApproval:boolean;acceptanceCriteria?:string|null}
export interface WorkflowStepResult {stepId:string;name:string;kind:WorkflowStepKind;state:string;runId:string;resultCommit:string|null;summary:string|null;selection:Selection|null;startedAt:string;finishedAt:string|null}
export interface Workflow {planningEnabled:boolean;reviewers:Selection[];maxReviewRetries:number;steps?:WorkflowStep[]|null}
export interface AiReview {id:string;attempt:number;stage:number;selection:Selection;resultCommit:string;verdict:string;summary:string;createdAt:string}
export interface DiscussionDraft {id:string;title:string;description:string;acceptanceCriteria:string;taskRules?:string|null;allowedPaths?:string[];ruleIds?:string[];openQuestions?:string[];createdTaskId?:string|null;sourceVersion:number}
export interface TaskSeed extends DiscussionDraft {discussionId:string}
export interface DiscussionMessage {id:string;role:string;content:string;createdAt:string;selection?:Selection|null;baseCommit?:string|null}
export interface Discussion {id:string;projectId:string;title:string;selection:Selection;rules:Rule[];messages:DiscussionMessage[];drafts:DiscussionDraft[];version:number;updatedAt:string;activeOperation:string|null;error:string|null}
