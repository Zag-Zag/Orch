using Orch.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Orch.Services;

public class OrchestratorService
{
    private readonly StorageService _storage;
    private readonly string _workDir;
    private FactoryConfig? _factory;
    private Plan? _plan;
    private List<IterationRecord> _history;
    private int _iterationCounter;

    public FactoryStatus Status { get; private set; } = FactoryStatus.Idle;
    public List<IterationRecord> History => _history;
    public Plan? Plan => _plan;

    public event Action<string>? OnLog;
    public event Action<IterationRecord>? OnIterationAdded;
    public event Action<FactoryStatus>? OnStatusChanged;
    public event Action<string>? OnStreamChunk;
    public event Action<AgentConfig>? OnAgentActive;
    public event Action<PlanItem>? OnPlanItemActive;

    private CancellationTokenSource _cts = new();
    private const int MaxRetries = 3;
    private const int RetryDelayMs = 3000;

    public OrchestratorService(StorageService storage, string workDir)
    {
        _storage = storage;
        _workDir = workDir;
        _history = new();
    }

    public void LoadFactory()
    {
        var path = _storage.GetFactoryPath(_workDir);
        _factory = _storage.LoadFactory(path);
        if (_factory != null) { _factory.WorkingDirectory = _workDir; Log($"Factory loaded: {_factory.Name} ({_factory.Agents.Count} agents)"); }
        var planPath = _storage.GetPlanPath(_workDir);
        _plan = _storage.LoadPlan(planPath);
        if (_plan != null) Log($"Plan loaded: {_plan.ProjectName} ({_plan.Items.Count} items)");
        var histPath = _storage.GetHistoryPath(_workDir);
        _history = _storage.LoadHistory(histPath);
        _iterationCounter = _history.Count > 0 ? _history.Max(h => h.IterationNumber) : 0;
    }

    public void SetFactory(FactoryConfig factory) { _factory = factory; _factory.WorkingDirectory = _workDir; }
    public void SetPlan(Plan plan) => _plan = plan;

    public void SaveAll()
    {
        if (_factory == null) return;
        _storage.SaveFactory(_factory, _storage.GetFactoryPath(_workDir));
        if (_plan != null) _storage.SavePlan(_plan, _storage.GetPlanPath(_workDir));
        _storage.SaveHistory(_history, _storage.GetHistoryPath(_workDir));
    }

    // ==================== PUBLIC API ====================

    public async Task<Plan?> GenerateGlobalPlanAsync(string tz)
    {
        var clinePath = FindClinePath();
        if (clinePath == null)
        {
            Log("Auto-Plan: Cline not found. Cannot generate plan without AI.");
            return null;
        }

        Log($"Auto-Plan: Cline found ({clinePath}). Requesting plan...");
        OnStreamChunk?.Invoke("Requesting plan from Cline...\n");
        var fullPrompt = BuildPlanCreationPrompt(tz);
        var result = await RunClineWithRetryAsync(clinePath, fullPrompt, autoApprove: true, thinking: false);

        var cleanResult = StripAnsi(result);
        var plan = ParsePlanFromOutput(cleanResult, "Project");
        plan.OriginalTz = tz;
        Log($"Auto-Plan: {plan.Items.Count} items generated.");
        return plan;
    }

    public async Task RunProductionCycleAsync()
    {
        if (_factory == null) { Log("ERROR: No factory configured."); return; }
        var sm = _factory.Agents.FirstOrDefault(a => a.Role == AgentRole.SuperManager);
        var executors = _factory.Agents.Where(a => a.Role == AgentRole.Executor).ToList();
        var judges = _factory.Agents.Where(a => a.Role == AgentRole.Judge).ToList();

        if (sm == null) { Log("ERROR: No SuperManager agent."); return; }
        if (!executors.Any()) { Log("ERROR: No Executor agent."); return; }

        SetStatus(FactoryStatus.Running);

        if (_plan == null || !_plan.Items.Any())
        {
            Log("No plan found. Creating plan first...");
            await CreatePlanAsync();
            if (_plan == null || !_plan.Items.Any()) { SetStatus(FactoryStatus.Failed); return; }
        }

        var executor = executors[0];
        var pendingItems = _plan.Items.Where(i => i.Status == PlanItemStatus.Pending).ToList();
        var startItem = pendingItems.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.Adjustments))
                     ?? pendingItems.FirstOrDefault();
        bool started = false;

        foreach (var item in pendingItems)
        {
            if (!started && item != startItem) continue;
            started = true;
            if (_cts.Token.IsCancellationRequested) break;
            item.Status = PlanItemStatus.InProgress; SaveAll();
            OnPlanItemActive?.Invoke(item);
            Log($"--- Processing item: {item.Title} ---");
            if (!string.IsNullOrWhiteSpace(item.Adjustments))
            {
                Log($"  Remarks: {item.Adjustments}");
                item.Adjustments = null;
            }

            var smTz = await SmCreateTzAsync(sm, item, _plan.OriginalTz);
            if (_cts.Token.IsCancellationRequested) break;

            var approved = false;
            var feedback = "";
            while (!approved && !_cts.Token.IsCancellationRequested)
            {
                var execPrompt = string.IsNullOrEmpty(feedback) ? smTz : feedback;
                var execResult = await ExecutorImplementAsync(executor, execPrompt);

                if (_cts.Token.IsCancellationRequested) break;
                approved = true;
                var newFeedback = new StringBuilder();
                foreach (var judge in judges)
                {
                    if (_cts.Token.IsCancellationRequested) break;
                    var verdict = await JudgeReviewAsync(judge, smTz);
                    if (!verdict.IsApproved) { approved = false; newFeedback.AppendLine($"[Judge {judge.Name}]: {verdict.ToExecute}"); }
                }
                if (!approved) { feedback = newFeedback.ToString(); Log("Judge(s) rejected. Feedback sent back to Executor."); }
                else { item.Status = PlanItemStatus.Done; item.Result = execResult; Log($"Item '{item.Title}' APPROVED."); }
            }

            if (_cts.Token.IsCancellationRequested) break;
            if (!approved) { item.Status = PlanItemStatus.Pending; item.Adjustments = "Interrupted"; }
            SaveAll();
        }

        var allDone = _plan.Items.All(i => i.Status == PlanItemStatus.Done);
        SetStatus(allDone ? FactoryStatus.Completed : FactoryStatus.Idle);
        Log(allDone ? "All plan items completed." : "Production cycle paused/interrupted.");
    }

    public void Stop() { _cts.Cancel(); _cts = new CancellationTokenSource(); SetStatus(FactoryStatus.Idle); Log("Production cycle stopped."); }

    // ==================== AGENT STEPS ====================

    private async Task<string> SmCreateTzAsync(AgentConfig sm, PlanItem item, string originalTz)
    {
        var prompt = $"ORIGINAL TZ: {originalTz}\n\n" +
                     $"PLAN ITEM: {item.Title}\n" +
                     $"DESCRIPTION: {item.Description}\n\n" +
                     "Create a CONCISE Technical Specification (TZ) for this plan item.\n\n" +
                     "CRITICAL RULES:\n" +
                     "- Describe WHAT the result must be, NOT how to implement it.\n" +
                     "- NO code snippets, NO pseudo-code, NO algorithms.\n" +
                     "- NO code analysis. Do NOT mention files, classes, methods.\n" +
                     "- The form is a simple dialog with directory listing and [..] for parent navigation.\n" +
                     "  Do NOT describe UI elements that don't exist (dropdowns, drive buttons, drive links).\n" +
                     "- Maximum 4 sections. Maximum 300 words.\n\n" +
                     "Output ONLY the TZ text.";
        return await RunAgentWithRetryAsync(sm, prompt, $"TZ: {item.Title}");
    }

    private async Task<string> ExecutorImplementAsync(AgentConfig executor, string tz)
    {
        var prompt = $"TECHNICAL SPECIFICATION:\n{tz}\n\nImplement the above specification. Output the result with a summary.";
        return await RunAgentWithRetryAsync(executor, prompt, "Implementation", thinking: false);
    }

    private async Task<JudgeVerdict> JudgeReviewAsync(AgentConfig judge, string tz)
    {
        var subRolesDesc = string.Join(", ", judge.JudgeSubRoles.Select(sr => sr switch
        {
            JudgeSubRole.TzCompliance => "TZ Compliance",
            JudgeSubRole.CodingStandards => "Coding Standards",
            JudgeSubRole.ErrorCheck => "Error/Build Check",
            JudgeSubRole.CodeQuality => "Code Quality",
            _ => sr.ToString()
        }));

        var cleanTz = StripThinkingBlocks(tz);

        var prompt = $@"INDEPENDENT CODE REVIEW — YOU ARE A JUDGE. VERIFY COMPLIANCE WITH TZ.

YOUR JUDGE ROLES: {subRolesDesc}

TECHNICAL SPECIFICATION (TZ):
{cleanTz}

INSTRUCTIONS:
1. Read ALL source files in the working directory yourself. Do NOT trust any summary.
2. Build the project. If build fails → immediately mark as build failure.
3. Run ALL tests. If ANY test fails → immediately mark as test failure.
4. For EACH requirement in the TZ, verify it is FULLY implemented.
5. Check ALL edge cases explicitly mentioned in the TZ.
6. Check coding standards: naming, structure, error handling patterns.
7. Check code quality: efficiency, duplication, coupling, complexity.
8. If UNSURE about any aspect → report it as UNCERTAINTY, not as a defect.
9. Be EXACT and SPECIFIC: which file, which line, what requirement is violated.

--- OUTPUT FORMAT (MANDATORY) ---
You MUST output a STRICT JSON object and NOTHING ELSE.
No markdown fences, no backticks, no extra text before or after.

If ALL checks pass:
{{
  ""verdict"": ""APPROVED"",
  ""summary"": ""Brief summary confirming all requirements are met."",
  ""annotated_tz"": """",
  ""issues"": []
}}

If ANY check fails:
{{
  ""verdict"": ""REJECTED"",
  ""summary"": ""Brief summary of what is wrong and how many issues found."",
  ""annotated_tz"": ""Full TZ text with [PASS]/[FAIL]/[WARN]/[UNTESTED] markers inserted at EACH requirement and EACH edge case. For each [FAIL], append: 'JUDGE NOTE: <specific evidence, file, line>'."",
  ""issues"": [
    {{
      ""id"": ""issue-1"",
      ""requirement_ref"": ""2.a — Смена диска"",
      ""severity"": ""BLOCKER"",
      ""file"": ""Program.cs"",
      ""line"": 598,
      ""evidence"": ""GetDrives() is never called. No drive entries appear in the list at root.""
    }}
  ]
}}

The 'annotated_tz' field MUST be the COMPLETE original TZ text with markers inserted.
The 'issues' array MUST list every individual finding with exact file and line.";

        var result = await RunAgentWithRetryAsync(judge, prompt, "Judge Review", thinking: false);
        return ParseVerdict(result, () => JudgeReviewAsync(judge, tz));
    }

    private async Task<string> RunAgentWithRetryAsync(AgentConfig agent, string prompt, string description, bool thinking = true)
    {
        for (int retry = 0; retry < MaxRetries; retry++)
        {
            if (_cts.Token.IsCancellationRequested) break;

            try
            {
                Log($"[{agent.Role}/{agent.Name}] Starting: {description}" + (retry > 0 ? $" (retry {retry + 1}/{MaxRetries})" : ""));
                OnAgentActive?.Invoke(agent);
                OnStreamChunk?.Invoke($"[{agent.Role}/{agent.Name}] Working...\n");

                var record = new IterationRecord
                {
                    IterationNumber = ++_iterationCounter,
                    AgentId = agent.Id,
                    AgentName = agent.Name,
                    Role = agent.Role,
                    Input = prompt.Length > 500 ? prompt[..500] + "..." : prompt,
                    Timestamp = DateTime.Now,
                    IsStreaming = true
                };
                OnIterationAdded?.Invoke(record);
                _history.Add(record);

                var result = await RunAgentAsync(agent, prompt, thinking);

                record.Output = StripAnsi(result);
                record.FullChat = result;
                record.IsStreaming = false;
                Log($"[{agent.Role}/{agent.Name}] Completed: {description}");
                OnStreamChunk?.Invoke(record.Output + "\n--- DONE ---\n");
                OnIterationAdded?.Invoke(record);
                return record.Output;
            }
            catch (Exception ex)
            {
                Log($"[{agent.Role}/{agent.Name}] Failed (retry {retry + 1}/{MaxRetries}): {ex.Message}");
                if (retry < MaxRetries - 1)
                    await Task.Delay(RetryDelayMs, _cts.Token);
                else
                    throw;
            }
        }

        throw new OperationCanceledException(_cts.Token);
    }

    private async Task<string> RunAgentAsync(AgentConfig agent, string prompt, bool thinking = true)
    {
        var fullPrompt = $"{agent.SystemPrompt}\n\n---\n\nTASK:\n{prompt}";
        var clinePath = FindClinePath();

        for (int retry = 0; retry < MaxRetries; retry++)
        {
            Log($"Cline: {clinePath}" + (retry > 0 ? $" (retry {retry + 1}/{MaxRetries})" : ""));
            var result = await RunClineAsync(clinePath, fullPrompt, thinking: thinking);

            if (!result.StartsWith("CLINE_ERROR:"))
            {
                if (agent.PreserveContext)
                {
                    var contextFile = Path.Combine(_workDir, $".orch_{agent.Id}_context.json");
                    await File.WriteAllTextAsync(contextFile, JsonSerializer.Serialize(new { lastOutput = result, timestamp = DateTime.Now }));
                }
                return result;
            }

            Log($"Cline error on retry {retry + 1}/{MaxRetries}: {result}");
            if (retry < MaxRetries - 1)
                await Task.Delay(RetryDelayMs, _cts.Token);
        }

        throw new Exception($"Cline failed after {MaxRetries} retries.");
    }

    // ==================== JUDGE VERDICT PARSING ====================

    private JudgeVerdict ParseVerdict(string rawResponse, Func<Task<JudgeVerdict>>? retry = null)
    {
        var cleaned = StripThinkingBlocks(rawResponse);
        var json = ExtractJsonObject(cleaned);

        if (json == null)
        {
            if (retry != null) { Log("Judge parse error: no JSON, retrying..."); return retry().Result; }
            throw new Exception("PARSE_ERROR: No valid JSON found in Judge response.");
        }

        json = SanitizeJsonString(json);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("verdict", out var verdictProp))
            {
                var verdict = verdictProp.GetString()?.Trim().ToUpperInvariant() ?? "REJECTED";
                bool success = verdict == "APPROVED";
                string answer = root.TryGetProperty("summary", out var sum) ? sum.GetString() ?? "" : "";
                string toExecute = "";

                if (!success)
                {
                    var annotatedTz = root.TryGetProperty("annotated_tz", out var atz) ? atz.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(annotatedTz))
                        toExecute = annotatedTz;
                    else if (root.TryGetProperty("issues", out var issuesArr) && issuesArr.GetArrayLength() > 0)
                        toExecute = BuildAnnotatedTzFromIssues(answer, issuesArr);
                }

                return new JudgeVerdict { Success = success, Answer = answer, ToExecute = toExecute, RawResponse = rawResponse };
            }

            if (root.TryGetProperty("success", out var successProp))
            {
                return new JudgeVerdict
                {
                    Success = successProp.GetBoolean(),
                    Answer = root.TryGetProperty("answer", out var ans) ? ans.GetString() ?? "" : "",
                    ToExecute = root.TryGetProperty("to_execute", out var te) ? te.GetString() ?? "" : "",
                    RawResponse = rawResponse
                };
            }
        }
        catch (JsonException)
        {
            if (retry != null) { Log("Judge parse error: malformed JSON, retrying..."); return retry().Result; }
            throw new Exception("PARSE_ERROR: Malformed JSON in Judge response.");
        }

        if (retry != null) { Log("Judge parse error: unknown format, retrying..."); return retry().Result; }
        throw new Exception("PARSE_ERROR: Unknown JSON format in Judge response.");
    }

    private static string SanitizeJsonString(string json)
    {
        var sb = new StringBuilder(json.Length);
        bool inString = false;
        bool inEscape = false;

        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];

            if (inEscape) { sb.Append(c); inEscape = false; continue; }
            if (c == '\\' && inString) { sb.Append(c); inEscape = true; continue; }
            if (c == '"') { inString = !inString; sb.Append(c); continue; }
            if (inString && (c == '\r' || c == '\n' || c == '\t')) { sb.Append(' '); continue; }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static string? ExtractJsonObject(string text)
    {
        var span = text.AsSpan().Trim();

        if (span.StartsWith("```"))
        {
            var endFence = span[2..].IndexOf("```");
            if (endFence >= 0) span = span[(endFence + 5)..].Trim();
        }
        if (span.EndsWith("```")) span = span[..^3].Trim();

        var candidates = new List<string>();
        int i = 0;

        while (i < span.Length)
        {
            if (span[i] != '{') { i++; continue; }

            int depth = 0, start = i, end = -1;
            bool inString = false, inEscape = false;

            for (int j = i; j < span.Length; j++)
            {
                char c = span[j];
                if (inEscape) { inEscape = false; continue; }
                if (c == '\\' && inString) { inEscape = true; continue; }
                if (c == '"') { inString = !inString; continue; }
                if (inString) continue;
                if (c == '{') depth++;
                else if (c == '}') { depth--; if (depth == 0) { end = j; break; } }
            }

            if (end >= 0) { candidates.Add(span[start..(end + 1)].ToString()); i = end + 1; }
            else i++;
        }

        for (int idx = candidates.Count - 1; idx >= 0; idx--)
        {
            try
            {
                var sanitized = SanitizeJsonString(candidates[idx]);
                using var doc = JsonDocument.Parse(sanitized);
                var root = doc.RootElement;

                bool isVerdict = root.TryGetProperty("verdict", out _) || root.TryGetProperty("success", out _);
                bool isTool = root.TryGetProperty("path", out _) || root.TryGetProperty("files", out _) ||
                              root.TryGetProperty("query", out _) || root.TryGetProperty("pattern", out _);

                if (isVerdict && !isTool) return candidates[idx];
            }
            catch { }
        }

        return null;
    }

    private static string BuildAnnotatedTzFromIssues(string summary, JsonElement issuesArr)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## JUDGE REVIEW — REJECTED");
        sb.AppendLine();
        sb.AppendLine(summary);
        sb.AppendLine();
        sb.AppendLine("### Issues Found:");
        sb.AppendLine();

        int idx = 1;
        foreach (var issue in issuesArr.EnumerateArray())
        {
            var req = issue.TryGetProperty("requirement_ref", out var r) ? r.GetString() ?? "Unknown" : "Unknown";
            var sev = issue.TryGetProperty("severity", out var sv) ? sv.GetString() ?? "WARN" : "WARN";
            var file = issue.TryGetProperty("file", out var f) ? f.GetString() ?? "?" : "?";
            var line = issue.TryGetProperty("line", out var l) ? l.GetInt32().ToString() : "?";
            var evidence = issue.TryGetProperty("evidence", out var e) ? e.GetString() ?? "" : "";

            sb.AppendLine($"[FAIL] {idx}. {req} [{sev}]");
            sb.AppendLine($"    File: {file}, Line: {line}");
            sb.AppendLine($"    Evidence: {evidence}");
            sb.AppendLine();
            idx++;
        }

        sb.AppendLine("---");
        sb.AppendLine("Fix ALL [FAIL] items above. Verify against the original TZ.");
        return sb.ToString();
    }

    private static string StripThinkingBlocks(string text)
    {
        return Regex.Replace(text, @"\[thinking\][\s\S]*?\[/thinking\]", "", RegexOptions.IgnoreCase).Trim();
    }

    // ==================== CLINE ====================

    private async Task<string> RunClineWithRetryAsync(string clinePath, string prompt, bool autoApprove = true, bool thinking = true)
    {
        for (int retry = 0; retry < MaxRetries; retry++)
        {
            var result = await RunClineAsync(clinePath, prompt, autoApprove, thinking);
            if (!result.StartsWith("CLINE_ERROR:")) return result;
            Log($"Cline error on retry {retry + 1}/{MaxRetries}: {result}");
            if (retry < MaxRetries - 1) await Task.Delay(RetryDelayMs, _cts.Token);
        }
        throw new Exception($"Cline failed after {MaxRetries} retries.");
    }

    private async Task<string> RunClineAsync(string clinePath, string prompt, bool autoApprove = true, bool thinking = true)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"orch_prompt_{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(tempFile, prompt, Encoding.UTF8);

        try
        {
            var autoApproveStr = autoApprove ? "true" : "false";
            var thinkingArg = thinking ? "" : " --thinking none";
            var arguments = $"/c type \"{tempFile}\" | \"{clinePath}\" cline --auto-approve {autoApproveStr}{thinkingArg} -c \"{_workDir}\"";

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = arguments,
                WorkingDirectory = _workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            var output = new StringBuilder();
            var error = new StringBuilder();

            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (s, e) => { if (e.Data != null) { output.AppendLine(e.Data); OnStreamChunk?.Invoke(StripAnsi(e.Data) + "\n"); } };
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) { error.AppendLine(e.Data); OnStreamChunk?.Invoke("[ERR] " + StripAnsi(e.Data) + "\n"); } };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var completed = await Task.Run(() => process.WaitForExit(-1), _cts.Token);

            if (!completed) { process.Kill(); return "CLINE_ERROR: Process timed out."; }
            if (process.ExitCode != 0 && error.Length > 0)
            {
                var errStr = StripAnsi(error.ToString());
                return $"CLINE_ERROR: exit {process.ExitCode}: {errStr}";
            }

            return output.ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"CLINE_ERROR: {ex.Message}";
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    // ==================== ANSI STRIP ====================

    private static string StripAnsi(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return Regex.Replace(text, @"\x1b\[[0-9;]*[a-zA-Z]", "");
    }

    // ==================== CLINE FIND ====================

    private string? FindClinePath() => FindNpxPath();

    private static string? FindNpxPath()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c where npx 2>nul",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            proc.WaitForExit(3000);
            if (proc.ExitCode != 0) return null;
            var result = proc.StandardOutput.ReadToEnd().Trim();
            if (string.IsNullOrEmpty(result)) return null;
            var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (!string.IsNullOrEmpty(trimmed) && trimmed.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
                    return trimmed;
            }
            var first = lines[0].Trim();
            return !string.IsNullOrEmpty(first) ? first : null;
        }
        catch { return null; }
    }

    // ==================== PLAN PARSING ====================

    private string BuildPlanCreationPrompt(string tz)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a PLANNING MACHINE. Output a numbered list. NOTHING else.");
        sb.AppendLine();
        sb.AppendLine("TASKS:");
        sb.AppendLine("---");
        sb.AppendLine(tz);
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("FORMAT:");
        sb.AppendLine("N. TITLE — DESCRIPTION");
        sb.AppendLine("  N = 1,2,3...");
        sb.AppendLine("  TITLE = what the task does (3-5 words)");
        sb.AppendLine("  \" — \" = MANDATORY");
        sb.AppendLine("  DESCRIPTION = concrete action + expected result. NO filenames.");
        sb.AppendLine();
        sb.AppendLine("EXAMPLE:");
        sb.AppendLine("1. Add drive switching — show available drives at root, Enter navigates to selected drive.");
        sb.AppendLine("2. Auto-filter history — remove filter buttons, filter history by selected agent role, empty when none selected.");
        sb.AppendLine();
        sb.AppendLine("RULES:");
        sb.AppendLine("- Merge same-feature into ONE item.");
        sb.AppendLine("- Do NOT mention filenames, paths, classes, or methods. This is a business plan, not a code review.");
        sb.AppendLine("- NO \"Implement:\", NO markdown, NO analysis, NO notes.");
        sb.AppendLine("- Output ONCE. Do NOT repeat yourself.");
        sb.AppendLine("- Last item ALWAYS: \"N. Integration verification — build 0 errors 0 warnings, full pipeline SU→EX→JU end-to-end.\"");
        sb.AppendLine("- End with ===END===");
        sb.AppendLine();
        sb.AppendLine("OUTPUT:");
        return sb.ToString();
    }

    private Plan ParsePlanFromOutput(string output, string projectName)
    {
        var plan = new Plan { ProjectName = projectName, OriginalTz = _plan?.OriginalTz ?? "" };
        var lines = output.Split('\n');
        int fallbackNum = 1;
        var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            if (trimmed.Equals("===END===", StringComparison.OrdinalIgnoreCase)) break;
            if (trimmed.StartsWith("---") || trimmed.EndsWith("---")) continue;
            if (trimmed.StartsWith("===") && !trimmed.StartsWith("===END")) continue;
            if (trimmed.StartsWith("//") || trimmed.StartsWith("#")) continue;
            if (trimmed.StartsWith("PLAN:", StringComparison.OrdinalIgnoreCase)) continue;
            if (trimmed.StartsWith("NOTES", StringComparison.OrdinalIgnoreCase)) break;

            var match = Regex.Match(trimmed, @"^(?:\d+[\.\)]\s*)\s*(.+)$");
            if (!match.Success) continue;

            var fullText = match.Groups[1].Value.Trim();
            if (fullText.Length < 3) continue;

            var title = fullText;
            var description = "";
            var dashIdx = title.IndexOf('—');
            if (dashIdx < 0) dashIdx = title.IndexOf(" - ");
            if (dashIdx > 0)
            {
                description = title[(dashIdx + 1)..].Trim().TrimStart('-', '—', ' ');
                title = title[..dashIdx].Trim();
            }
            else
            {
                var colonIdx = title.IndexOf(": ");
                if (colonIdx > 3)
                {
                    description = title[(colonIdx + 2)..].Trim();
                    title = title[..colonIdx].Trim();
                }
                else
                {
                    description = "Implement: " + title;
                }
            }

            var dedupKey = (title + "|" + description).ToLowerInvariant();
            if (!seenTitles.Add(dedupKey)) continue;

            plan.Items.Add(new PlanItem { Number = fallbackNum++, Title = title, Description = description });
        }

        if (plan.Items.Count == 0)
        {
            var meaningful = lines.Select(l => l.Trim()).Where(l => l.Length > 10 && !l.StartsWith("{") && !l.StartsWith("[thin") && !l.StartsWith("[2m")).ToList();
            foreach (var m in meaningful)
            {
                if (!m.Contains("[thinking]"))
                {
                    plan.Items.Add(new PlanItem { Number = 1, Title = m.Length > 200 ? m[..200] : m, Description = m.Length > 500 ? m[..500] : m });
                    break;
                }
            }
            if (plan.Items.Count == 0)
                plan.Items.Add(new PlanItem { Number = 1, Title = "Implement project", Description = output.Length > 500 ? output[..500] : output });
        }

        return plan;
    }

    public async Task CreatePlanAsync()
    {
        if (_factory == null) { Log("ERROR: No factory configured."); return; }
        SetStatus(FactoryStatus.CreatingPlan);
        var sm = _factory.Agents.FirstOrDefault(a => a.Role == AgentRole.SuperManager);
        if (sm == null) { Log("ERROR: No SuperManager agent."); SetStatus(FactoryStatus.Idle); return; }

        var prompt = BuildPlanCreationPrompt(_plan?.OriginalTz ?? "Create a development plan for the project.");
        Log("Requesting plan from SuperManager...");

        try
        {
            var result = await RunAgentWithRetryAsync(sm, prompt, "Create Plan");
            result = StripAnsi(result);
            _plan = ParsePlanFromOutput(result, _plan?.OriginalTz ?? "Project");
            Log($"Plan created with {_plan.Items.Count} items.");
            SaveAll(); SetStatus(FactoryStatus.Idle);
        }
        catch (Exception ex)
        {
            Log($"Plan creation failed after {MaxRetries} retries: {ex.Message}");
            SetStatus(FactoryStatus.Failed);
        }
    }

    private void Log(string msg) => OnLog?.Invoke(msg);
    private void SetStatus(FactoryStatus status) { Status = status; OnStatusChanged?.Invoke(status); }
}