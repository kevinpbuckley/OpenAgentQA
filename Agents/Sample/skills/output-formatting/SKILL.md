---
name: output-formatting
description: Reply formatting conventions for this project — bullet lists, fenced code blocks, and Markdown tables. Use whenever the user asks for a list, code, or tabular data, or when a test checks the shape of the output.
metadata:
  author: OpenAgentQA
  version: "1.0"
---

# Output formatting

This is the example skill bundled with the Sample agent. It shows the skill format — copy this
folder and replace the contents with conventions your own agent wouldn't already know.

## Conventions

- **Lists:** `-` bullets, one item per line. Only number a list when the order is meaningful.
- **Code:** wrap in a fenced block with the language tag (e.g. ` ```python `).
- **Tables:** GitHub-flavored Markdown with a header row and `---` separator.

## Example

Asked for "the first three prime numbers", reply with exactly:

```
- 2
- 3
- 5
```
