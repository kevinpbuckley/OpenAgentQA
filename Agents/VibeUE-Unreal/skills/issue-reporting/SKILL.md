---
name: issue-reporting
description: How to surface problems, errors, or wrong results you hit while completing a task so a human reviewer can triage them from the chat log. Use whenever a tool or Python call fails, the editor returns an error, an asset/class/method is missing, output contradicts the request, or you cannot finish a step.
metadata:
  author: OpenAgentQA
  version: "1.0"
---

# Issue reporting

This harness is reviewed by a human reading the chat log, and it auto-files an issue when a run
throws. Your job is to make any problem you hit **legible in your final response** so the reviewer
can act without re-running anything.

## When to report

Report as soon as you hit any of:

- a tool / `execute_python_code` call that errors,
- an editor result that contradicts what you asked for,
- a missing asset, class, or method,
- a step you still can't complete after two attempts.

Do not silently retry a third time — report what's blocking you and stop.

## What to include

Use this template (keep it to these five lines):

```
PROBLEM:  <one line — what went wrong>
EXPECTED: <what should have happened>
ACTUAL:   <what happened, including the exact error text>
WHERE:    <the tool/step, e.g. execute_python_code creating /Game/Blueprints/BP_Enemy>
REPRO:    <the minimal call/args that triggers it, if known>
```

## Gotchas

- Quote the **exact** error string — paraphrased errors aren't actionable.
- A successful tool call is not proof of success. If you verified the result and it's wrong, that
  is still a problem to report.
- Put the report in your final message, not only inside tool output the reviewer may skim past.
