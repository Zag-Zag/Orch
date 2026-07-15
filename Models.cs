using System;
using System.Collections.Generic;
using System.IO;

using System.Text.Json;


namespace Orch.Models;

public enum AgentRole
{
    SuperManager,
    Executor,
    Judge
}

public enum JudgeSubRole
{
    TzCompliance,
    CodingStandards,
    ErrorCheck,
    CodeQuality
}

public class AgentConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public AgentRole Role { get; set; } = AgentRole.Executor;
    public string Name { get; set; } = "";
    public List<JudgeSubRole> JudgeSubRoles { get; set; } = new();
    public bool PreserveContext { get; set; } = false;
    public string SystemPrompt { get; set; } = "";
    public string? InstructionFile { get; set; }
    public bool AnalyzeLastSuperManager { get; set; } = false;
    public string? JudgesStepId { get; set; }
}

public class PlanItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public PlanItemStatus Status { get; set; } = PlanItemStatus.Pending;
    public string? Result { get; set; }
    public string? Adjustments { get; set; }
    public string? ParentItemId { get; set; }
    public string? AssignedAgentId { get; set; }
}

public enum PlanItemStatus
{
    Pending,
    InProgress,
    Done,
    Skipped
}

public class Plan
{
    public string ProjectName { get; set; } = "";
    public string OriginalTz { get; set; } = "";
    public List<PlanItem> Items { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

public class FactoryConfig
{
    public string Name { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public List<AgentConfig> Agents { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public PipelineConfig? Pipeline { get; set; }
}

public class IterationRecord
{
    public int IterationNumber { get; set; }
    public string AgentId { get; set; } = "";
    public string AgentName { get; set; } = "";
    public AgentRole Role { get; set; }
    public string Input { get; set; } = "";
    public string Output { get; set; } = "";
    public string FullChat { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public bool IsStreaming { get; set; }
    public string? PipelineStepId { get; set; }
}

public enum FactoryStatus
{
    Idle,
    CreatingPlan,
    Running,
    Paused,
    Completed,
    Failed
}

public enum PipelineStatus
{
    NotStarted,
    Running,
    AwaitingJudgment,
    Completed,
    Failed
}


/// <summary>Parsed verdict from a Judge agent.</summary>
public class JudgeVerdict
{
    /// <summary>true = APPROVED, false = REJECTED</summary>
    public bool Success { get; set; } = false;
    /// <summary>Plain answer / summary from the Judge.</summary>
    public string Answer { get; set; } = "";
    /// <summary>If REJECTED: exact instructions the Executor must execute to fix.</summary>
    public string ToExecute { get; set; } = "";
    /// <summary>Raw full response for history.</summary>
    public string RawResponse { get; set; } = "";
    public string? JudgedStepId { get; set; }

    public bool IsApproved => Success;
}

public class PipelineStep
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public int Order { get; set; }
    public string AgentId { get; set; } = "";
    public AgentRole Role { get; set; }
    public string? ParentStepId { get; set; }
    public int NestingLevel { get; set; }
    public string? JudgesStepId { get; set; }
    public string Description { get; set; } = "";
}

public class PipelineConfig
{
    public string Name { get; set; } = "";
    public List<PipelineStep> Steps { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

public static class Defaults
{
    private static Dictionary<AgentRole, string>? _loadedPrompts;

    public static Dictionary<AgentRole, string> DefaultSystemPrompts
    {
        get
        {
            if (_loadedPrompts != null) return _loadedPrompts;

            // Try to load from prompts.json in the app directory
            var promptsPath = Path.Combine(AppContext.BaseDirectory, "prompts.json");
            if (File.Exists(promptsPath))
            {
                try
                {
                    var json = File.ReadAllText(promptsPath);
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (loaded != null)
                    {
                        _loadedPrompts = new();
                        foreach (var kvp in loaded)
                        {
                            if (Enum.TryParse<AgentRole>(kvp.Key, true, out var role))
                                _loadedPrompts[role] = kvp.Value;
                        }
                        if (_loadedPrompts.Count == 3) return _loadedPrompts;
                    }
                }
                catch { /* fallback to defaults */ }
            }

            _loadedPrompts = BuiltInPrompts;
            return _loadedPrompts;
        }
        set
        {
            _loadedPrompts = value;
            // Save back to prompts.json
            try
            {
                var dict = new Dictionary<string, string>();
                foreach (var kvp in value)
                    dict[kvp.Key.ToString()] = kvp.Value;
                var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "prompts.json"), json);
            }
            catch { /* best effort */ }
        }
    }

    public static readonly Dictionary<AgentRole, string> BuiltInPrompts = new()
    {
        [AgentRole.SuperManager] = @"You are a SuperManager (SM) agent in an automated software development pipeline.

Your responsibilities:
1. You have an overall development plan. Take the next pending item from the plan.
2. Create a detailed Technical Specification (TЗ) from that plan item.
3. Pass the TЗ to the Executor agent.
4. When the Judge approves the Executor's work, mark the plan item as Done and record the result.
5. If the Judge returns with issues, adjust the TЗ or provide clarifications and send back to Executor.
6. If there is no plan, create one based on the original TЗ first.

CRITICAL RULE — TЗ MUST BE ""WHAT"", NOT ""HOW"":
- TЗ must describe WHAT needs to be done, NOT HOW to implement it.
- DO NOT include code snippets, pseudo-code, algorithms, or implementation details in the TЗ.
- Only describe: requirements, expected behavior, inputs, outputs, constraints, acceptance criteria, edge cases.
- The Executor decides HOW to implement — you only specify WHAT the result must be.
- Exception: you MAY include exact API signatures or interface contracts ONLY when they are externally mandated (e.g., a fixed public API, an existing interface that must be matched).

When building a development PLAN:
- You MAY inspect existing source files in the working directory to understand the current codebase.
- Base your plan on the actual project structure, not assumptions.

Output format: Provide clear, actionable TЗ. Mark progress on plan items. Be concise and direct.",

        [AgentRole.Executor] = @"You are an Executor agent in an automated software development pipeline.

Your responsibilities:
1. Receive a Technical Specification (TЗ) from the SuperManager.
2. Implement exactly what the TЗ specifies.
3. Write clean, working code. Test your implementation.
4. Output the result with a summary of what was done.
5. If you receive feedback from the Judge, fix the issues and resubmit.

Output format: Provide implementation with code, explanation, and test results. Be thorough and precise.",

        [AgentRole.Judge] = @"You are a Judge agent in an automated software development pipeline.

Your responsibilities:
1. Independently verify the implementation against the Technical Specification (ТЗ).
2. Read source files, build the project, and run tests yourself — do NOT trust second-hand summaries.
3. Check for the following aspects based on your assigned sub-roles:
   - ТЗ Compliance: Does the implementation match the specification exactly?
   - Coding Standards: Does the code follow best practices and standards?
   - Error Check: Does the code compile/build without errors? Are there runtime issues?
   - Code Quality: Is the code well-structured, efficient, and maintainable?

CRITICAL — YOU ARE A STRICT JUDGE. YOUR DEFAULT POSITION IS TO REJECT.

VERIFICATION RULES:
1. Read ALL source files in the working directory yourself. Do NOT trust any summary.
2. Build the project yourself. If build fails → REJECT immediately with exact error details.
3. Run ALL tests yourself. If ANY test fails → REJECT immediately with the failing test details.
4. For EACH requirement in the TЗ, verify it is FULLY implemented. Partial implementation → REJECT.
5. Check edge cases: null/empty inputs, boundary values, error conditions, concurrency issues.
6. Check coding standards: naming conventions, structure, documentation, error handling patterns.
7. Check code quality: efficiency, maintainability, code duplication, coupling, complexity.
8. If you are UNSURE about any aspect → REJECT. Only approve if 100% confident.

YOUR SUB-ROLES (only the CHECKED ones on the form apply to you):
- TzCompliance: Every TЗ requirement MUST be implemented exactly as specified. Missing or extra features → REJECT.
- CodingStandards: Code MUST follow language-specific best practices and project conventions. Violations → REJECT.
- ErrorCheck: Build MUST succeed with zero errors and zero warnings. All tests MUST pass. Any failure → REJECT.
- CodeQuality: Code MUST be well-structured, DRY, KISS, with proper separation of concerns. Poor design → REJECT.

If ANY of your assigned sub-roles finds issues → you MUST REJECT.

--- OUTPUT FORMAT (MANDATORY) ---
You MUST output a STRICT JSON object and NOTHING ELSE. No markdown fences, no backticks, no extra text:

APPROVED:
{
  ""success"": true,
  ""answer"": ""Brief summary of the review result."",
  ""to_execute"": """"
}

REJECTED:
{
  ""success"": false,
  ""answer"": ""Brief summary of what is wrong."",
  ""to_execute"": ""Exact, actionable instructions for the Executor to fix the issues. Be specific: which file, which line, what exactly to change.""
}

RULES:
- success: true ONLY if implementation is 100% approved, false otherwise.
- answer: A plain-text summary of your decision.
- to_execute: If success is false — exact, actionable instructions for the Executor. If success is true — empty string.
- Output ONLY the JSON. No quotes, no backticks, no markdown fences, no extra text before or after.
- Do NOT translate field names to Russian — always use ""success"", ""answer"", ""to_execute"".
- Do NOT prepend [thinking] blocks before the JSON.

BE STRICT. Your goal is to catch EVERY defect before it reaches production."
    };

    public static PipelineConfig DefaultPipeline => new()
    {
        Name = "SU-EX-JU (Default)",
        Steps = new()
        {
            new() { Order = 0, Role = AgentRole.SuperManager, Description = "Plan Generation" },
            new() { Order = 1, Role = AgentRole.Executor,  Description = "Execution" },
            new() { Order = 2, Role = AgentRole.Judge,       Description = "Judgment" }
        }
    };

    public static List<JudgeSubRole> DefaultJudgeSubRoles => new()
    {
        JudgeSubRole.TzCompliance,
        JudgeSubRole.CodingStandards,
        JudgeSubRole.ErrorCheck,
        JudgeSubRole.CodeQuality
    };

    /// <summary>
    /// Reset loaded prompts, forcing reload from file or fallback.
    /// </summary>
    public static void ResetPrompts()
    {
        _loadedPrompts = null;
    }
}