# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

OpenHarness is markdown-driven QA regression testing for AI agents. It points at a folder of `.md`
prompt files, runs each through an agent (configured with MCP tools + an `agent.md` system prompt),
and captures a full chat log — prompt, response, tool calls with inputs/outputs, token usage, and
cost — per test. The actual pass/fail judgement is done by a human/reviewer reading those logs; the
harness only marks a test "failed" when the agent run *errors* (throws), not when the output is wrong.

A single .NET 9 web project ([dotnet/OpenHarness.Api/](dotnet/OpenHarness.Api/)) serves **both** the
headless CLI and the web UI. The agent runtime is the **Microsoft Agent Framework**
(`Microsoft.Agents.AI` → `ChatClientAgent`), talking to OpenRouter through an OpenAI `IChatClient`.
MCP tools come from the official `ModelContextProtocol` SDK.

## Commands

All commands run through the one project. Requires .NET SDK 9.0+ and `OPENROUTER_API_KEY` (a `.env`
file in the workspace root is auto-loaded by [HarnessConfiguration.LoadDotEnv](dotnet/OpenHarness.Api/HarnessConfiguration.cs)).

```bash
dotnet run --project dotnet/OpenHarness.Api -- list                 # list discovered tests
dotnet run --project dotnet/OpenHarness.Api -- run                  # run every test
dotnet run --project dotnet/OpenHarness.Api -- run -f tests/x.md    # run specific file(s); repeatable
dotnet run --project dotnet/OpenHarness.Api -- run tests/umg        # positional dir runs all *.md under it
dotnet run --project dotnet/OpenHarness.Api -- run -o junit --report report.xml
dotnet run --project dotnet/OpenHarness.Api -- run --model deepseek/deepseek-v4-flash
dotnet run --project dotnet/OpenHarness.Api -- init                 # scaffold tests/ + config
dotnet run --project dotnet/OpenHarness.Api -- issues list [-a]     # tracked failures (-a includes resolved)
dotnet run --project dotnet/OpenHarness.Api -- issues show <id> | resolve <id> | summary
dotnet run --project dotnet/OpenHarness.Api                         # no args ⇒ "serve" the web UI
```

`run` output modes (`-o`): `console` (default), `json`, `markdown`, `junit`. Build-once for CI:

```bash
dotnet build dotnet/OpenHarness.Api/OpenHarness.Api.csproj -c Release
dotnet run --project dotnet/OpenHarness.Api -c Release --no-build -- run --output junit --report report.xml
```

There is **no unit-test suite** — "tests" here means the agent prompt files under `tests/`. CI
([.github/workflows/test.yml](.github/workflows/test.yml)) builds, runs all prompt files, and feeds
the JUnit report to a test-reporter (with `continue-on-error`, so agent failures don't fail the build).

## How a run flows

1. [Program.cs](dotnet/OpenHarness.Api/Program.cs) is the entrypoint. It first calls `FindWorkspace`,
   which walks **up** from the current directory looking for `open-agent-qa.json` — that file's
   directory is the workspace root for everything (config, tests, `.harness/` output). If `args[0]`
   is `run`/`list`/`init`/`issues` it dispatches to [Cli.cs](dotnet/OpenHarness.Api/Cli.cs) and exits;
   otherwise it builds an ASP.NET host, serves the static UI from `ui/`, and exposes the `/api/*`
   endpoints + a SignalR `/hubs/runs` hub.
2. Both paths share the same DI singletons: `HarnessConfiguration` → `AgentRuntime` → `RunCoordinator`.
3. [RunCoordinator](dotnet/OpenHarness.Api/RunCoordinator.cs) discovers/accepts the test files, creates
   a `RunJob`, and runs tests via `Parallel.ForEachAsync` (degree = config `parallel`, clamped 1–20).
   Each test is parsed, sent to `AgentRuntime.RunAsync`, and its result + artifacts written to disk.
   The CLI uses `RunToCompletionAsync`; the web UI uses `Start` (fire-and-forget) + SignalR progress.
4. [AgentRuntime](dotnet/OpenHarness.Api/AgentRuntime.cs) is the single agent entrypoint. Per call it
   connects every configured MCP server, collects their tools, builds a `ChatClientAgent` over an
   OpenRouter `IChatClient` with `.UseFunctionInvocation()`, runs the message loop, then reconstructs
   the tool transcript from the response message history (pairing `FunctionCallContent` with
   `FunctionResultContent` by `CallId` in `ExtractTraces`).

## Configuration

- **`open-agent-qa.json`** (workspace root; loaded by [HarnessConfiguration](dotnet/OpenHarness.Api/HarnessConfiguration.cs)).
  Keys live under an `open-agent-qa` object: `provider`, `model`, `agentsDir` (default `./Agents`),
  `agent` (default active agent), `testDir` (global default), `temperature`, `maxSteps`, `agentTimeoutMs`,
  `parallel`, and optional global `setupScript`. A `.open-agent-qa.json` fallback name is also accepted.
  Note `maxSteps` and `tracker` appear in config but aren't wired into the C# `HarnessConfig` model.
- **Agents** (`Agents/<Name>/`, since the TestAgent→Agents refactor). Each folder is a self-contained
  agent: `agent.md` (system prompt), `skills/` (`*.md` appended sorted), `mcp.json` (its MCP servers),
  and optional `agent.json` with overrides — `description`, `agentMd`, `skillsDir`, `mcp`, `testDir`,
  `setupScript` (paths relative to the agent folder, or absolute). `Agents/VibeUE-Unreal/` is the Unreal
  agent (was `TestAgent/`); `Agents/Sample/` is a copy-me starter that runs with no MCP. The **active
  agent** supplies the system prompt, skills, MCP servers, and may override `testDir`/`setupScript`.
  Resolution order: `OPENAGENTQA_AGENT` env var → config `agent` → first folder. The UI's top-bar
  selector (`/api/agents`, `POST /api/agents/active`) persists the choice to `.env` via
  `HarnessConfiguration.SetActiveAgent` (writes the env var + upserts `.env`). `HarnessConfig` exposes the
  resolved `ActiveAgent`, and `AgentConfig`/`McpServers`/`TestDir`/`SetupScript` are all agent-resolved.
- **`setupScript`** (optional, path relative to workspace) — a PowerShell script run **on demand** to
  prepare a clean testing environment. It is **not** run automatically; the user triggers it from the
  web UI's "Spin up clean instance" button on the Run page (→ `POST /api/setup/run`). It runs via
  `pwsh` (falling back to `powershell` on Windows) with `-NoProfile -ExecutionPolicy Bypass -File`.
  `RunCoordinator.RunSetupScriptAsync(Func<string,Task> onLine, …)` **streams** each stdout/stderr line
  through `onLine` as the script runs (the script can build for minutes, so live progress matters); the
  endpoint writes those lines to a `text/plain` chunked response (flushing per line) and the UI's
  `spinUpCleanInstance` reads the stream and appends them live. Because the body has already started, the
  status stays `200` and success/failure is conveyed by a terminal sentinel line — `[DONE] … (exit N)` or
  `[ERROR] <reason>` — which the UI keys off of. The method returns a `SetupScriptResult` (exit code +
  ok) and never throws. The active agent's script is [Agents/VibeUE-Unreal/clean-env.ps1](Agents/VibeUE-Unreal/clean-env.ps1):
  stop editor → `git pull` VibeUE in `QAClean` → robocopy `/MIR` `QAClean`→`QAActive` → run VibeUE's
  `BuildAndLaunchGame.ps1` from `QAActive`.
- **MCP servers** are read from the **active agent's** `mcp.json` (`Agents/<active>/mcp.json`,
  `LoadMcpServers(path)`), under an `mcpServers` (or `servers`) object. Each server is `stdio` (default:
  `command`/`args`/`env`) or `http` (`type: "http"` + `url`). VibeUE-Unreal targets Unreal's HTTP MCP.
- **Skills** follow the [Agent Skills spec](https://agentskills.io/specification): each skill is a folder
  under `skills/` with a `SKILL.md` (YAML frontmatter — `name` matching the folder, `description` of
  what+when — then Markdown instructions). They are served through **MAF's native `AgentSkillsProvider`**
  (an `AIContextProvider`), wired up in [AgentRuntime.cs](dotnet/OpenHarness.Api/AgentRuntime.cs): when the
  active agent's `skills/` has any `SKILL.md`, the agent is built with `ChatClientAgentOptions {
  AIContextProviders = [new AgentSkillsProvider(skillsDir)] }`. This gives **progressive disclosure** — the
  skill names+descriptions are advertised in context, and the model pulls a full `SKILL.md` on demand via
  the `load_skill` tool (so a skill only applies if the model loads it; `load_skill` calls show up as tool
  steps in the report, untimed since they aren't `TimedFunction`-wrapped). `AgentSkillsProvider` is an
  experimental MAF API (`MAAI001`, suppressed at the call site). `DiscoverSkillFiles`/`ListSkillNames`
  (legacy flat `*.md` still discovered) power the `/api/agents` + `/api/chat/info` listings and the
  "attach the provider?" check.
- **System prompt** (`LoadInstructions`) = the active agent's `agent.md` + a fixed boilerplate line.
  Skills are **not** inlined here anymore (the provider handles them). The `## Expected`/`## Assert` test
  sections never enter the prompt.

### Unreal/HTTP MCP specifics (in [AgentRuntime.cs](dotnet/OpenHarness.Api/AgentRuntime.cs))
- HTTP transport is forced to `HttpTransportMode.StreamableHttp` — Unreal rejects the SDK's AutoDetect
  SSE probe with 405.
- `ContentLengthHandler` rewrites request bodies to send `Content-Length` instead of chunked encoding,
  which Epic's server requires.
- `McpConnection` serializes connects per-server-name through a `SemaphoreSlim` gate so parallel workers
  don't hammer the same server concurrently.

## Test file format ([PromptParser.cs](dotnet/OpenHarness.Api/PromptParser.cs))

A test is one markdown file, in either shape:
- **Freeform** — no frontmatter, no `## Prompt`; the whole file is the prompt (most suites under `tests/`).
- **Structured** — optional YAML frontmatter (`name`, `config: { model, temperature }`) + a `## Prompt`
  section. **Only the `## Prompt` body is sent to the agent.** `## Expected` / `## Assert` (or
  `## Assertions`) are parsed and retained for the reviewer but **never** sent to the agent under test.
  Per-test `config.model`/`temperature` override the global config for that one test.

## Prompt caching & cost (the reason the system prompt must stay byte-stable)

The system prompt (`agent.md` + skills + boilerplate) is large and identical across every test, so it
is the primary cache target. **Skills load in sorted order and the prefix is built once per run**, so
it is byte-stable across runs and parallel workers — do **not** inject per-test data into
`agent.md`/skills or you bust the cache. OpenRouter only returns usage details (incl. `cost`) when the
request opts in with `usage.include = true`; since `Microsoft.Extensions.AI` has no slot for a USD cost,
[OpenRouterCostPolicy](dotnet/OpenHarness.Api/OpenRouterCostPolicy.cs) is a request-pipeline policy that
injects the flag and reads the usage block off **each** raw completion response, recording one
`LlmCallUsage` (tokens + cost) per tool-loop HTTP call into an `AsyncLocal` queue (scoped via
`Accumulate`). **BYOK caveat:** under bring-your-own-key OpenRouter doesn't bill, so `usage.cost` is 0 —
the real spend is in `usage.cost_details.upstream_inference_cost`. The policy uses `cost` when non-zero
and falls back to the upstream cost, so cost is correct for both billed and BYOK setups. Auto-caching
providers (DeepSeek, OpenAI, Grok via OpenRouter) cache automatically; explicit-breakpoint providers
(Anthropic, some Gemini) won't cache via OpenRouter without `cache_control` breakpoints (cache-hit 0%).

## Output — everything lands under `.harness/` (gitignored)

- `runs/<stamp>-<id>/` — the per-run directory: `run.json` (job state), `report.json`, `chat.jsonl`,
  `system.log`, `issues.json`, `manifest.json`, and `prompts/<safe-name>-<hash>/` per-prompt artifacts.
  Served/zipped by the web UI; artifact access is path-allowlisted (`RunCoordinator.GetRunArtifact`).
- `logs/*.json` — one file per test result (full prompt/response/trace/tokens/cost/error).
- `reports/report-*.json` — full run report.
- `issues/ISSUE-*.json` — file-backed issue tracker ([IssueStore](dotnet/OpenHarness.Api/IssueStore.cs)),
  one JSON per issue with a `.sequence` counter. An issue is auto-created when a test errors **and** no
  open issue already exists for that test name (`HasOpenForTest`).

Each chat entry (`chat.jsonl`, both the run-level and per-prompt copies) and each `TestResult` carries
`startedAt`/`completedAt`/`durationMs` and the `tokenUsage` block (`prompt`/`completion`/`total`/`cached`/`cost`)
so per-test latency and token spend are visible without cross-referencing — the web UI's Run-results and
Logs detail rows render both via `formatTokens`.

**Capture completeness for the AI fixer (the consumer of results is an AI, not a human).** The harness
captures deterministic *facts* (not analysis — root-causing is left to the downstream AI). `report.json`
carries run-level `advertisedSkills` (name+description of every `SKILL.md` — `HarnessConfiguration.AdvertisedSkills`)
and `availableTools` (server/name/description of every MCP tool that was connected, captured in `AgentRuntime`);
each `TestResult` carries `loadedSkills` (which advertised skills the agent actually `load_skill`'d, from the
trace — `ExtractLoadedSkills`). Advertised-vs-loaded is the key skill signal (a skill present but never loaded =
fix its `description`), and only the harness knows the advertised set at run time, so it must capture it.

**Run-to-run comparison (the scoreboard / reward signal).** [RunComparer](dotnet/OpenHarness.Api/RunComparer.cs)
deterministically diffs two runs' `report.json` (`RunMetrics` + per-test `TestDelta`: fixed/regressed/tool-calls-changed)
so you can tell whether a skill/prompt/tool edit helped without an LLM re-reading two transcripts. Exposed as
CLI `compare <before-id> <after-id>` (prints via `Reports.ConsoleComparison`), `GET /api/runs/{before}/compare/{after}`,
and a Compare panel on the Runs page. The `run` CLI command now prints the run id for this.

**Per-tool-call timing.** Each `ToolCallTrace` also carries `startedAt`/`endedAt`/`durationMs`. These come
from `TimedFunction` (in [AgentRuntime.cs](dotnet/OpenHarness.Api/AgentRuntime.cs)), a `DelegatingAIFunction`
that wraps every MCP tool and records each invocation's clock without touching its name/schema/result.
`ExtractTraces` zips the timings back onto the reconstructed transcript **in order, matched by tool name**
(function invocation is sequential, so order aligns) — old runs without timings just render without them.

**Conversation reconstruction.** `AgentRuntime.BuildConversation` walks the run's message history and
produces a `ConversationTurn[]` (on `TestResult.Conversation`): one turn per assistant LLM call, each
carrying that turn's text, the tool calls it made (results paired by `CallId`, per-call timings matched),
and that single call's `LlmCallUsage` (tokens + cost) — the k-th assistant message gets the k-th captured
usage, since function invocation is sequential. The flat `Trace` is still produced for back-compat.

**Human-readable run report.** The Runs page links each run to a visual report at `#/runs/{id}`
(`renderRunReportPage` in [ui/app.js](ui/app.js)), built entirely from `report.json` (+ `manifest.json` for
per-test raw-artifact links). It renders a run summary (wall-clock time, pass/fail, total tokens, cache-hit
rate, cost, model) and, per test, the **turn-by-turn conversation**: assistant text → its tool calls
(collapsible, with input/output, own duration, and the **gap since the previous call**) → next turn, with
each interaction's token/cost effect shown beneath it (`renderConversationCard`/`toolStepHtml`). Runs from
before this data existed fall back to a flat tool list. The Runs table also has **📁 Open Folder** (→
`POST /api/runs/{id}/open`, opens the dir in the OS file browser server-side) and keeps the raw-JSON links.

**On-disk JSON is camelCase.** `RunCoordinator.JsonOptions`/`ChatJsonOptions` use `JsonNamingPolicy.CamelCase`
so files (`report.json`, `logs/*.json`, `manifest.json`, `chat.jsonl`, `run.json`) deserialize with the same
property names the live ASP.NET API emits and the UI reads. Don't write these artifacts with default
(PascalCase) options — that desyncs file-backed reads (`/api/logs`, `/api/reports/{name}`, the manifest
view) from the live run path, which is exactly the bug that fix corrected. (Older PascalCase files left in
`.harness/` from before the fix will render blank in the UI; just re-run.)

## Conventions in the C# code

- Modern C#/.NET 9 throughout: file-scoped namespaces, primary constructors, collection expressions
  (`[]`), records for all DTOs/config ([Models.cs](dotnet/OpenHarness.Api/Models.cs)), pattern matching.
  Match this style when editing.
- The model under test never sees `## Expected`/`## Assert` — keep that boundary intact when touching
  `PromptParser` or `LoadInstructions`.
- Output to disk uses `lock (job)` for shared files (`system.log`, `chat.jsonl`) because tests run
  concurrently — preserve that when adding writes.
