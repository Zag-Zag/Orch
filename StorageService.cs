using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Orch.Models;

namespace Orch.Services;

public class StorageService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public FactoryConfig? LoadFactory(string path)
    {
        if (!File.Exists(path)) return null;
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<FactoryConfig>(json, JsonOpts);
    }

    public void SaveFactory(FactoryConfig config, string path)
    {
        config.CreatedAt = DateTime.Now; // update timestamp
        var json = JsonSerializer.Serialize(config, JsonOpts);
        File.WriteAllText(path, json);
    }

    public string GetFactoryPath(string workDir) =>
        Path.Combine(workDir, ".fabrik");

    public string GetPlanPath(string workDir) =>
        Path.Combine(workDir, ".plane");

    public Plan? LoadPlan(string path)
    {
        if (!File.Exists(path)) return null;
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Plan>(json, JsonOpts);
    }

    public void SavePlan(Plan plan, string path)
    {
        plan.UpdatedAt = DateTime.Now;
        var json = JsonSerializer.Serialize(plan, JsonOpts);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// Saves iteration history alongside the factory.
    /// </summary>
    public string GetHistoryPath(string workDir) =>
        Path.Combine(workDir, ".history.json");

    public List<IterationRecord> LoadHistory(string path)
    {
        if (!File.Exists(path)) return new();
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<IterationRecord>>(json, JsonOpts) ?? new();
    }

    public void SaveHistory(List<IterationRecord> history, string path)
    {
        var json = JsonSerializer.Serialize(history, JsonOpts);
        File.WriteAllText(path, json);
    }
}