using System.Text.Json;

namespace OpenHarness.Api;

public sealed class HarnessConfiguration(string workspace)
{
    public const string ActiveAgentEnvVar = "OPENAGENTQA_AGENT";
    public string Workspace { get; } = workspace;

    public HarnessConfig Load()
    {
        LoadDotEnv();
        var root = ReadConfigRoot();

        var agentsDir = GetString(root, "agentsDir") ?? "./Agents";
        var globalTestDir = GetString(root, "testDir") ?? "./tests";
        var globalSetupScript = GetString(root, "setupScript");

        // The active agent (env var wins, then config, then the first agent folder) supplies the
        // system prompt, skills, MCP servers, and may override the global test dir / setup script.
        var activeAgent = ResolveActiveAgent(root, agentsDir);
        var agentDir = activeAgent.Length > 0 ? Path.GetFullPath(Path.Combine(Workspace, agentsDir, activeAgent)) : null;
        var meta = agentDir is not null ? ReadAgentMeta(agentDir) : new AgentMeta();

        AgentConfig agentConfig;
        IReadOnlyList<McpServerConfig> mcpServers;
        if (agentDir is not null && Directory.Exists(agentDir))
        {
            agentConfig = new AgentConfig(
                ResolveUnder(agentDir, meta.AgentMd ?? "agent.md"),
                ResolveUnder(agentDir, meta.SkillsDir ?? "skills"));
            mcpServers = LoadMcpServers(ResolveUnder(agentDir, meta.Mcp ?? "mcp.json"));
        }
        else
        {
            agentConfig = new AgentConfig(null, null);
            mcpServers = [];
        }

        var testDir = meta.TestDir is not null && agentDir is not null ? ResolveUnder(agentDir, meta.TestDir) : globalTestDir;
        var setupScript = meta.SetupScript is not null && agentDir is not null ? ResolveUnder(agentDir, meta.SetupScript) : globalSetupScript;

        return new HarnessConfig(
            GetString(root, "provider") ?? "openrouter",
            GetString(root, "model") ?? "openai/gpt-4o",
            testDir,
            root.TryGetProperty("temperature", out var temperature) ? temperature.GetDouble() : 0.1,
            root.TryGetProperty("maxSteps", out var maxSteps) ? maxSteps.GetInt32() : 20,
            root.TryGetProperty("agentTimeoutMs", out var timeout) ? timeout.GetInt32() : 120000,
            root.TryGetProperty("parallel", out var parallel) ? parallel.GetInt32() : 1,
            agentConfig,
            mcpServers,
            setupScript,
            activeAgent,
            agentsDir);
    }

    /// <summary>The agents under <c>agentsDir</c>, plus which one is active.</summary>
    public AgentList ListAgents()
    {
        LoadDotEnv();
        var root = ReadConfigRoot();
        var agentsDir = GetString(root, "agentsDir") ?? "./Agents";
        var active = ResolveActiveAgent(root, agentsDir);
        var dir = Path.GetFullPath(Path.Combine(Workspace, agentsDir));
        if (!Directory.Exists(dir)) return new AgentList(active, []);

        var agents = Directory.EnumerateDirectories(dir).Order().Select(path =>
        {
            var name = Path.GetFileName(path);
            var meta = ReadAgentMeta(path);
            var skillsDir = ResolveUnder(path, meta.SkillsDir ?? "skills");
            var mcpPath = ResolveUnder(path, meta.Mcp ?? "mcp.json");
            return new AgentInfo(
                name,
                meta.Description,
                meta.TestDir is not null ? ResolveUnder(path, meta.TestDir) : null,
                DiscoverSkillFiles(skillsDir).Count,
                LoadMcpServers(mcpPath).Count,
                meta.SetupScript is not null);
        }).ToList();
        return new AgentList(active, agents);
    }

    /// <summary>Persist the active agent to <c>.env</c> (and the process env) so it survives restarts.</summary>
    public void SetActiveAgent(string name)
    {
        Environment.SetEnvironmentVariable(ActiveAgentEnvVar, name);
        UpsertDotEnv(ActiveAgentEnvVar, name);
    }

    /// <summary>Absolute path to the active agent's mcp.json (used by the config endpoints).</summary>
    public string ActiveAgentMcpPath()
    {
        var root = ReadConfigRoot();
        var agentsDir = GetString(root, "agentsDir") ?? "./Agents";
        var active = ResolveActiveAgent(root, agentsDir);
        var agentDir = Path.GetFullPath(Path.Combine(Workspace, agentsDir, active));
        return ResolveUnder(agentDir, ReadAgentMeta(agentDir).Mcp ?? "mcp.json");
    }

    public IReadOnlyList<McpServerConfig> LoadMcpServers(string path)
    {
        if (!File.Exists(path)) return [];
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return [];
        if (!root.TryGetProperty("mcpServers", out var servers) && !root.TryGetProperty("servers", out servers)) return [];
        if (servers.ValueKind != JsonValueKind.Object) return [];

        var result = new List<McpServerConfig>();
        foreach (var property in servers.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object) continue;
            var value = property.Value;
            var args = value.TryGetProperty("args", out var argsValue) && argsValue.ValueKind == JsonValueKind.Array
                ? argsValue.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToList() : [];
            var env = new Dictionary<string, string>();
            if (value.TryGetProperty("env", out var envValue) && envValue.ValueKind == JsonValueKind.Object)
                foreach (var entry in envValue.EnumerateObject()) env[entry.Name] = entry.Value.GetString() ?? string.Empty;
            result.Add(new McpServerConfig(
                property.Name,
                value.TryGetProperty("type", out var type) ? type.GetString() : null,
                value.TryGetProperty("url", out var url) ? url.GetString() : null,
                value.TryGetProperty("command", out var command) ? command.GetString() : null,
                args, env,
                value.TryGetProperty("timeoutMs", out var requestTimeout) ? requestTimeout.GetInt32() : 60000));
        }
        return result;
    }

    /// <summary>
    /// The base system prompt: the active agent's <c>agent.md</c> + a fixed boilerplate line. Skills
    /// are NOT inlined here — they are served by MAF's <c>AgentSkillsProvider</c> (progressive
    /// disclosure: name+description advertised, full <c>SKILL.md</c> loaded on demand via <c>load_skill</c>).
    /// </summary>
    public string LoadInstructions(HarnessConfig config)
    {
        var sections = new List<string>();
        if (!string.IsNullOrWhiteSpace(config.AgentConfig.AgentMd))
        {
            var path = Path.GetFullPath(Path.Combine(Workspace, config.AgentConfig.AgentMd));
            if (File.Exists(path)) sections.Add(File.ReadAllText(path));
        }
        sections.Add("You are an AI agent being tested. Complete the user's task accurately using the available MCP tools.");
        return string.Join("\n\n", sections);
    }

    /// <summary>The active agent's skills directory (absolute), or null when none is configured.</summary>
    public string? ActiveSkillsDir(HarnessConfig config) =>
        string.IsNullOrWhiteSpace(config.AgentConfig.SkillsDir)
            ? null : Path.GetFullPath(Path.Combine(Workspace, config.AgentConfig.SkillsDir));

    /// <summary>
    /// Skill files in a skills directory, in deterministic order. Follows the Agent Skills spec
    /// (each skill is a folder with a <c>SKILL.md</c>); also accepts legacy flat <c>*.md</c> files.
    /// </summary>
    public IReadOnlyList<string> DiscoverSkillFiles(string skillsDir)
    {
        if (!Directory.Exists(skillsDir)) return [];
        var files = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(skillsDir).Order())
        {
            var skillMd = Path.Combine(dir, "SKILL.md");
            if (File.Exists(skillMd)) files.Add(skillMd);
        }
        files.AddRange(Directory.EnumerateFiles(skillsDir, "*.md").Order());
        return files;
    }

    /// <summary>Display names of the skills in a directory (the skill folder name, or the file name for legacy flat skills).</summary>
    public IReadOnlyList<string> ListSkillNames(string skillsDir) =>
        DiscoverSkillFiles(skillsDir)
            .Select(file => SkillNameFor(file))
            .ToList();

    /// <summary>
    /// The skills the agent advertises (name + description from each SKILL.md). This is the
    /// "what was available" set — pairing it with which skills actually loaded lets the
    /// downstream analyzer see skills that were advertised but never triggered.
    /// </summary>
    public IReadOnlyList<SkillInfo> AdvertisedSkills(string skillsDir)
    {
        var result = new List<SkillInfo>();
        foreach (var file in DiscoverSkillFiles(skillsDir))
        {
            var (name, description) = ReadSkillMeta(file, SkillNameFor(file));
            result.Add(new SkillInfo(name, description));
        }
        return result;
    }

    private static string SkillNameFor(string file) =>
        Path.GetFileName(file).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(Path.GetDirectoryName(file))!
            : Path.GetFileNameWithoutExtension(file);

    /// <summary>Parse the <c>name</c> + <c>description</c> from a SKILL.md's YAML frontmatter (single-line values).</summary>
    private static (string Name, string? Description) ReadSkillMeta(string file, string fallbackName)
    {
        try
        {
            var text = File.ReadAllText(file);
            if (!text.StartsWith("---", StringComparison.Ordinal)) return (fallbackName, null);
            var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (end < 0) return (fallbackName, null);
            string? name = null, description = null;
            foreach (var rawLine in text[3..end].Split('\n'))
            {
                var line = rawLine.Trim();
                if (name is null && line.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                    name = line[5..].Trim().Trim('"', '\'');
                else if (description is null && line.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                    description = line[12..].Trim().Trim('"', '\'');
            }
            return (string.IsNullOrWhiteSpace(name) ? fallbackName : name!, description);
        }
        catch { return (fallbackName, null); }
    }

    private JsonElement ReadConfigRoot()
    {
        var configPath = File.Exists(Path.Combine(Workspace, "open-agent-qa.json"))
            ? Path.Combine(Workspace, "open-agent-qa.json")
            : Path.Combine(Workspace, ".open-agent-qa.json");
        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        var root = document.RootElement.TryGetProperty("open-agent-qa", out var nested) ? nested : document.RootElement;
        return root.Clone();
    }

    private string ResolveActiveAgent(JsonElement root, string agentsDir)
    {
        var dir = Path.GetFullPath(Path.Combine(Workspace, agentsDir));
        var available = Directory.Exists(dir)
            ? Directory.EnumerateDirectories(dir).Select(Path.GetFileName).Where(n => n is not null).Cast<string>().Order().ToList()
            : [];
        string? Match(string? name) => name is null ? null
            : available.FirstOrDefault(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return Match(Environment.GetEnvironmentVariable(ActiveAgentEnvVar))
            ?? Match(GetString(root, "agent"))
            ?? available.FirstOrDefault()
            ?? string.Empty;
    }

    private AgentMeta ReadAgentMeta(string agentDir)
    {
        var path = Path.Combine(agentDir, "agent.json");
        if (!File.Exists(path)) return new AgentMeta();
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var r = document.RootElement;
            return new AgentMeta
            {
                Name = GetString(r, "name"),
                Description = GetString(r, "description"),
                AgentMd = GetString(r, "agentMd"),
                SkillsDir = GetString(r, "skillsDir"),
                Mcp = GetString(r, "mcp"),
                TestDir = GetString(r, "testDir"),
                SetupScript = GetString(r, "setupScript"),
            };
        }
        catch { return new AgentMeta(); }
    }

    private static string ResolveUnder(string baseDir, string path) => Path.GetFullPath(Path.Combine(baseDir, path));

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private void LoadDotEnv()
    {
        var path = Path.Combine(Workspace, ".env");
        if (!File.Exists(path)) return;
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var index = line.IndexOf('=');
            if (index < 1) continue;
            var key = line[..index].Trim();
            var value = line[(index + 1)..].Trim().Trim('\"', '\'');
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key))) Environment.SetEnvironmentVariable(key, value);
        }
    }

    private void UpsertDotEnv(string key, string value)
    {
        var path = Path.Combine(Workspace, ".env");
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        var replaced = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith('#')) continue;
            var eq = lines[i].IndexOf('=');
            if (eq > 0 && lines[i][..eq].Trim().Equals(key, StringComparison.Ordinal))
            {
                lines[i] = $"{key}={value}";
                replaced = true;
                break;
            }
        }
        if (!replaced) lines.Add($"{key}={value}");
        File.WriteAllLines(path, lines);
    }

    private sealed class AgentMeta
    {
        public string? Name { get; init; }
        public string? Description { get; init; }
        public string? AgentMd { get; init; }
        public string? SkillsDir { get; init; }
        public string? Mcp { get; init; }
        public string? TestDir { get; init; }
        public string? SetupScript { get; init; }
    }
}
