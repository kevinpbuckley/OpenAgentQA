# OpenAgentQA

Markdown-driven QA regression testing for AI agents. Point it at a folder of `.md` prompt files, run
each through an agent — configured with a **system prompt**, **skills**, and **MCP tools** — and capture
the full session: the back-and-forth conversation, every tool call with inputs/outputs, per-step token
usage, cost, and timing. You then review those logs to judge whether the agent did the right thing.

It's built for iterating on the things that actually shape agent behavior: **does my system prompt hold
up across cases? do my skills trigger when they should? do my MCP tools get called correctly?**

A single .NET 9 app serves both a **CLI** and a **web UI**, on the **Microsoft Agent Framework**
(`Microsoft.Agents.AI`) talking to **OpenRouter**, with tools via the **Model Context Protocol** SDK.
(The project lives in `dotnet/OpenHarness.Api/`.)

## How it works (the mental model)

- A **test** is one `.md` file. Its prompt is sent to the **active agent**, and the whole run is logged.
- The harness only marks a test **failed when the agent run *errors*** (throws). It does **not**
  auto-grade output correctness — the `## Expected` / `## Assert` sections you write are kept for **you,
  the reviewer**, and are never sent to the agent. You judge pass/fail by reading the run report.
- Everything that shapes behavior lives in an **agent** (a folder under `Agents/`): the system prompt,
  skills, and MCP tools. Swap or edit the agent, re-run the same tests, compare.

## Requirements

- .NET SDK 9.0+
- An OpenRouter API key. Copy `.env.example` to `.env` and fill it in (auto-loaded from the project root):

```
OPENROUTER_API_KEY=sk-or-v1-...
```

## Quick start

```bash
# 1. run the web UI (no command = serve)
dotnet run --project dotnet/OpenHarness.Api          # → http://localhost:5000

# or drive it headless:
dotnet run --project dotnet/OpenHarness.Api -- list  # list discovered tests
dotnet run --project dotnet/OpenHarness.Api -- run   # run them, print a report
```

Out of the box the **Sample** agent is active (no MCP tools, one example skill, a `hello-world` test) so
you can run something immediately. Then copy `Agents/Sample/` to make your own.

## The testing workflow

1. **Pick or create an agent** (`Agents/<name>/`) — this is what you're testing.
2. **Configure it**: write the system prompt (`agent.md`), add skills (`skills/`), wire MCP tools (`mcp.json`).
3. **Write tests** — `.md` prompt files that exercise the behavior you care about.
4. **Run** them (web UI or CLI).
5. **Review** the run report — read the conversation, check tool calls, tokens, and cost; flag issues.
6. Tweak the prompt/skill/tools and re-run to compare.

The web UI's **Test Harness** page also lets you **chat with the active agent live** — handy for
iterating on a prompt/skill/tool before committing it to a test file. It shows the loaded `agent.md`,
the discovered skills, and **whether each MCP server connected and which tools it exposes**.

## Agents

Each folder under `Agents/` is a self-contained agent:

```
Agents/
  Sample/                          # copy-me starter; runs with no MCP tools
    agent.md                       # the system prompt
    agent.json                     # metadata + overrides (description, testDir, setupScript, ...)
    mcp.json                       # this agent's MCP servers (tools)
    skills/                        # Agent Skills — each a folder with a SKILL.md
      output-formatting/
        SKILL.md
    tests/                         # this agent's prompt files
  <your-agent>/                    # copy Sample/ and customize
```

The **active agent** supplies the system prompt, skills, and MCP tools, and can override the test
directory and setup script. Pick it from the **selector in the top bar** of the web UI; the choice is
saved to `.env` as `OPENAGENTQA_AGENT`. Resolution order: `OPENAGENTQA_AGENT` env var → `agent` in
`open-agent-qa.json` → first agent folder.

`agent.json` (all fields optional; paths are relative to the agent folder, or absolute):

```json
{
  "name": "my-agent",
  "description": "What this agent is for.",
  "agentMd": "agent.md",
  "skillsDir": "skills",
  "mcp": "mcp.json",
  "testDir": "tests",          // overrides the global testDir for this agent
  "setupScript": "clean-env.ps1"
}
```

> The `VibeUE-Unreal` agent (drives Unreal Engine via the VibeUE plugin against a local UE project) is
> **git-ignored** — it's environment-specific, so it isn't part of this repo. Bring your own.

### Testing a system prompt

The system prompt = the agent's `agent.md` + a fixed boilerplate line. Edit `agent.md`, then run your
tests and read the responses. To compare two prompts, keep two agents (or branch `agent.md`) and run the
same test directory against each. Per-test overrides (`config.model` / `config.temperature` in a test's
frontmatter) let you check prompt robustness across models/temperatures.

### Testing skills

Skills follow the [Agent Skills spec](https://agentskills.io/specification): each skill is a folder under
`skills/` with a `SKILL.md` — YAML frontmatter (`name`, matching the folder; `description` of *what it
does and when to use it*) then Markdown instructions:

```markdown
---
name: output-formatting
description: Reply formatting conventions — lists, code blocks, tables. Use when the user asks for a list, code, or tabular data.
---

# Output formatting
- Lists: `-` bullets, one per line.
- Code: fenced block with a language tag.
...
```

Skills are served through the Microsoft Agent Framework's native **`AgentSkillsProvider`**, which means
**progressive disclosure**: the harness advertises each skill's name + description, and the agent loads a
skill's full `SKILL.md` **on demand** via a `load_skill` tool (so a skill applies only when the agent
decides it's relevant). Optional `references/` and `assets/` files are loaded on demand too. (Skill
`scripts/` execution isn't enabled in this harness — a skill should be self-contained instructions.)

**To test a skill:** write a prompt whose task *should* trigger the skill, run it, and check the run
report — you'll see whether the agent called `load_skill` and whether it followed the skill's guidance.
A skill that never triggers usually means its `description` isn't specific about *when* to use it. (If you
want a rule applied unconditionally instead of on-demand, put it directly in `agent.md`.)

### Testing MCP tools

Give an agent tools by listing MCP servers in its `mcp.json` (standard MCP format — `stdio` by default,
or `http`):

```json
{
  "mcpServers": {
    "filesystem": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "."] },
    "remote":     { "type": "http", "url": "http://127.0.0.1:8000/mcp" }
  }
}
```

The harness connects every configured server at run time and exposes their tools to the agent.

**To verify tools are wired up:** open the web UI's **Test Harness** page — it lists each MCP server with
its connection status (connected / failed) and the tools it reported. Then **test tool usage** by writing
a prompt that requires a tool and reviewing the run report: each tool call appears as a collapsible step
showing the exact input, the output the server returned, and how long it took. This is the core of
catching regressions — you see *what the agent actually called and what came back*.

## Writing tests

A test is a markdown file, in either shape:

- **Freeform** — the whole file is the prompt.
- **Structured** — optional YAML frontmatter + a `## Prompt` section. **Only the `## Prompt` body is sent
  to the agent**; `## Expected` / `## Assert` are kept for *your* review and never reach the agent.
  Frontmatter `config: { model, temperature }` overrides config for that one test.

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

Organize tests in subfolders (scanned recursively) by category.

## Running tests

**Web UI** (`dotnet run --project dotnet/OpenHarness.Api`, then http://localhost:5000):

- **Run** — select tests, set parallelism, run; live progress; also has **Spin up clean instance**.
- **Runs** — every saved run, each with a **📊 View Report** and **📁 Open Folder**.
- **Logs / Issues / Config** — per-test logs, tracked failures, and settings (incl. the agent picker).

**CLI:**

```bash
dotnet run --project dotnet/OpenHarness.Api -- list                  # list discovered tests
dotnet run --project dotnet/OpenHarness.Api -- run                   # run every test (active agent)
dotnet run --project dotnet/OpenHarness.Api -- run -f tests/x.md     # specific file(s), repeatable
dotnet run --project dotnet/OpenHarness.Api -- run path/to/dir       # all *.md under a dir
dotnet run --project dotnet/OpenHarness.Api -- run --model openai/gpt-4o
dotnet run --project dotnet/OpenHarness.Api -- run -o junit --report report.xml
dotnet run --project dotnet/OpenHarness.Api -- init                  # scaffold Agents/Sample + config
dotnet run --project dotnet/OpenHarness.Api -- issues list [-a]      # tracked failures (-a includes resolved)
dotnet run --project dotnet/OpenHarness.Api -- issues show <id> | resolve <id> | summary
dotnet run --project dotnet/OpenHarness.Api -- compare <before> <after>   # diff two runs (scoreboard)
```

`run -o/--output`: `console` (default), `json`, `markdown`, `junit`. To run a different agent from the
CLI, set the env var: `OPENAGENTQA_AGENT=Sample dotnet run --project dotnet/OpenHarness.Api -- run`.

## Reviewing results

The **Run Report** (`#/runs/<id>` in the web UI, or `report.json`) is the heart of the review:

- A **run summary**: wall-clock time, pass/errored counts, total tokens, cache-hit rate, **cost**, model.
- Per test, the **turn-by-turn conversation** — the prompt, then each assistant turn with the tool calls
  it made (collapsible, showing input/output, each call's duration, and the wait between calls), reading
  like the chat as it happened.
- **Per-interaction tokens and cost** under each turn, so you can see exactly what each step costs
  (cost is captured per call and is correct under BYOK keys, not just OpenRouter-billed credits).
- Links to the raw underlying data (for an AI to act on) and a button to open the run folder.

When a test errors, an issue is auto-filed under `.harness/issues/` (also browsable on the **Issues** page).

### Built for an AI to act on the results

The output is structured so a coding agent can diagnose and fix the agent's skills / `agent.md` / MCP tools:

- **What was in play vs what was used.** Each run's `report.json` records `advertisedSkills` (every
  `SKILL.md`'s name + description) and `availableTools` (every connected MCP tool's name + description);
  each test records `loadedSkills` (which skills the agent actually loaded). A skill **advertised but never
  loaded** is the signal to sharpen its `description`; a tool that errors or gets called repeatedly points
  at the tool. (The harness only captures these facts — the *reasoning* about root cause and fixes is left
  to the AI you feed the results to.)
- **Run-to-run comparison (scoreboard).** After the AI edits files and you re-run, diff the two runs to see
  if it helped — pass/error counts, tool calls, **skill-trigger** counts, tokens, cost, and which tests
  flipped (`fixed`/`regressed`). In the web UI use the **Compare runs** panel on the Runs page, or:
  ```bash
  dotnet run --project dotnet/OpenHarness.Api -- compare <before-run-id> <after-run-id>
  ```
  (`run` prints its run id; the API is `GET /api/runs/{before}/compare/{after}`.)

## Clean test environment (optional)

An agent can define a `setupScript` (PowerShell) in its `agent.json`. The web UI's **"Spin up clean
instance"** button (Run page) runs it on demand — output streams live — to prepare a fresh environment
before a run (e.g. relaunching an app, resetting state). It's never run automatically.

## Configuration — `open-agent-qa.json`

```json
{
  "open-agent-qa": {
    "provider": "openrouter",
    "model": "deepseek/deepseek-v4-flash",
    "agentsDir": "./Agents",
    "agent": "Sample",
    "testDir": "./tests",
    "temperature": 0.1,
    "maxSteps": 500,
    "parallel": 3
  }
}
```

`testDir` here is the default; an agent's `agent.json` `testDir` overrides it. `model`/`temperature` are
the defaults; a test's frontmatter overrides them per test.

## Cost & prompt caching

- **Per-call usage + cost** are captured by a request-pipeline policy
  ([OpenRouterCostPolicy](dotnet/OpenHarness.Api/OpenRouterCostPolicy.cs)) that opts into OpenRouter's
  usage details. **BYOK-aware:** under bring-your-own-key, OpenRouter's `cost` is 0, so the harness reads
  the real spend from `cost_details.upstream_inference_cost`.
- **Caching.** The system prompt (`agent.md` + boilerplate) is byte-stable across runs/workers, so
  providers can cache it. (Skills are *not* inlined into the system prompt — they're advertised and loaded
  on demand by `AgentSkillsProvider` — so they don't affect the cached prefix.) Auto-caching providers
  (DeepSeek, OpenAI, Grok via OpenRouter) cache automatically; explicit-breakpoint providers (Anthropic,
  some Gemini) need `cache_control` breakpoints, so cache-hit reads 0% for them.
- **App attribution.** Requests send OpenRouter app-attribution headers (`HTTP-Referer`,
  `X-OpenRouter-Title`) so usage is tracked under this app; override with `OPENROUTER_HTTP_REFERER` /
  `OPENROUTER_X_TITLE`.

## Output

Everything lands under `.harness/` (git-ignored):

- `runs/<id>/` — `run.json`, `report.json`, `chat.jsonl`, `system.log`, per-prompt artifacts, downloadable zip.
- `logs/*.json` — one file per test result (prompt, conversation, tool trace, tokens, cost, timing, error).
- `reports/report-*.json` — full run reports.
- `issues/ISSUE-*.json` — auto-filed when a test errors.

## CI

The CLI emits JUnit, so any test reporter can consume it:

```bash
dotnet build dotnet/OpenHarness.Api/OpenHarness.Api.csproj -c Release
dotnet run --project dotnet/OpenHarness.Api -c Release --no-build -- run --output junit --report report.xml
```

Note the harness only fails on agent *errors*, so a green CI run means "nothing threw," not "all outputs
correct" — correctness review stays a human step.

## License

MIT © Buckley Builds LLC — see [LICENSE](LICENSE).
