# Antigravity Behavior Overrides

## Response Style
- **Strict Brevity:** Be extremely concise. Avoid conversational fluff, explanations of standard code patterns, and lengthy introductory or concluding prose.
- **Artifact Control:** Only generate `task.md` or `implementation_plan.md` artifacts for heavy structural or architectural modifications. Do not create them for simple line edits or quick syntax adjustments.

## Local Offloading Strategy
- For lightweight tasks, simple refactors, documentation additions, unit tests, or basic formatting, do not invoke deep cloud reasoning. Defer to the local Ollama instance tool.