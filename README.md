# OpenHarness

Markdown-driven QA regression testing for AI agents. Point it at a folder of `.md`
prompt files, run them through an agent (with your MCP tools and `agent.md` system
prompt), and capture a full chat log — prompt, response, **tool calls with inputs and
outputs**, token usage, and cost — for every test. Completion is then validated by
reviewing those chat logs.

The agent runtime is the **Microsoft Agent Framework** (`Microsoft.Agents.AI`), talking to
OpenRouter via an `IChatClient`. There is a single .NET project that serves both the CLI and
the web UI.

## Requirements

- .NET SDK 9.0+
- An OpenRouter API key (a `.env` file in the project root is auto-loaded):

```
OPENROUTER_API_KEY=sk-or-v1-...
```

## Usage

All commands run through the .NET project:

```bash
dotnet run --project dotnet/OpenHarness.Api -- list                 # list discovered tests
dotnet run --project dotnet/OpenHarness.Api -- run                  # run every test
dotnet run --project dotnet/OpenHarness.Api -- run -f tests/x.md    # run specific file(s)
dotnet run --project dotnet/OpenHarness.Api -- run --output junit --report report.xml
dotnet run --project dotnet/OpenHarness.Api -- init                 # scaffold tests/ + config
dotnet run --project dotnet/OpenHarness.Api -- issues list          # tracked failures
dotnet run --project dotnet/OpenHarness.Api -- serve                # web UI (default with no command)
```

`run` options: `[test-dir]`, `-f/--file <path>` (repeatable), `-o/--output console|json|markdown|junit`,
`--report <path>`, `--model <model>`. `issues` subcommands: `list [-a/--all]`, `show <id>`,
`resolve <id>`, `summary`.

For CI you can build once and run without rebuilding:

```bash
dotnet build dotnet/OpenHarness.Api/OpenHarness.Api.csproj -c Release
dotnet run --project dotnet/OpenHarness.Api -c Release --no-build -- run --output junit --report report.xml
```

## Configuration — `open-agent-qa.json`

```json
{
  "open-agent-qa": {
    "provider": "openrouter",
    "model": "deepseek/deepseek-v4-flash",
    "agentsDir": "./Agents",
    "agent": "VibeUE-Unreal",
    "testDir": "./tests",
    "temperature": 0.1,
    "maxSteps": 500,
    "parallel": 3
  }
}
```

## Agents

Each folder under `Agents/` is a self-contained agent:

```
Agents/
  VibeUE-Unreal/      # drives Unreal via VibeUE's MCP endpoint (was TestAgent/)
    agent.md          # system prompt
    skills/           # *.md appended to the system prompt (sorted)
    mcp.json          # this agent's MCP servers
    agent.json        # overrides: description, testDir, setupScript, ...
    clean-env.ps1     # this agent's "spin up clean instance" script
  Sample/             # copy-me starter; runs with no MCP tools
    agent.md  skills/  mcp.json  agent.json  tests/
```

The **active agent** supplies the system prompt, skills, MCP tools, and may override the test directory
and setup script (via `testDir`/`setupScript` in its `agent.json`). Pick it from the **selector in the
top bar** of the web UI — the choice is saved to `.env` as `OPENAGENTQA_AGENT`. Resolution order:
`OPENAGENTQA_AGENT` env var → config `agent` → first agent folder. To make your own agent, copy
`Agents/Sample/` and edit it.

`setupScript` (optional, per-agent in `agent.json`) is a PowerShell script you run **on demand** to prepare a clean testing
environment — via the **"Spin up clean instance"** button on the web UI's Run page. It is not run
automatically. It runs via `pwsh` (falling back to `powershell` on Windows) with
`-NoProfile -ExecutionPolicy Bypass -File`; its output is **streamed live** to the UI line-by-line as
it runs (so you see progress during a long build), ending in a `[DONE]`/`[ERROR]` line — a non-zero
exit is reported as a failure.

## Test file format

A test is a markdown file. Two shapes are supported:

- **Freeform** — the whole file is the prompt (used by most suites).
- **Structured** — optional YAML frontmatter plus a `## Prompt` section. Only the
  `## Prompt` body is sent to the agent; `## Expected` / `## Assert` are kept for the
  reviewer and are **never** sent to the agent. Frontmatter `config: { model, temperature }`
  overrides config for that test.

```markdown
---
name: Empty prompt handling
config:
  temperature: 0
---

## Prompt
Reply with exactly "OK" and nothing else.

## Expected
The response should be "OK".

## Assert
- must_include: ["OK"]
```

## Prompt caching & cost

The system prompt (`agent.md` + skills + boilerplate) is large and identical across every
test, so it is the primary caching target.

- **Stable prefix.** The system prompt is built once per run and skill files load in sorted
  order, so the cacheable prefix is byte-stable across runs and parallel workers. Don't put
  per-test data into `agent.md`/skills — it busts the cache.
- **Measured.** Each result records cached input tokens and the OpenRouter-billed cost (USD,
  net of cache discounts). The reports show per-test cached tokens + cost and a run total
  with cache-hit rate.
- **Auto-caching providers** (DeepSeek, OpenAI, Grok via OpenRouter) cache the stable prefix
  automatically. **Explicit-breakpoint providers** (Anthropic, some Gemini) do not cache via
  OpenRouter unless `cache_control` breakpoints are added — if you switch `model` to one of
  these, the cache-hit rate will read 0% until breakpoints are added.
- **Cost capture.** Requests opt into OpenRouter's billed cost via `usage: { include: true }`;
  a request pipeline policy
  ([OpenRouterCostPolicy](dotnet/OpenHarness.Api/OpenRouterCostPolicy.cs)) injects the flag and
  reads `usage.cost` off the raw response, since the Agent Framework usage abstraction has no
  slot for a USD cost.

## Output

Per run, written under `.harness/`:

- `logs/*.json` — one file per test (prompt, response, tool trace, tokens, cost, error).
- `reports/report-*.json` — full run report.
- `runs/<id>/` — `chat.jsonl`, per-prompt artifacts, `report.json`, downloadable zip (web UI).
- `issues/ISSUE-*.json` — tracked failures.
