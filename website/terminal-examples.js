/* Actual CLI output from a synthetic snapshot. See ASSETS.md. */
window.CODERIM_TERMINAL_EXAMPLES = [
  {
    "id": "limits",
    "command": "coderim limits --provider codex",
    "output": "Codex · Plus                             READY\n────────────────────────────────────────────────\nSession           68.0% left\n██████████████░░░░░░\n  resets in 2h 33m\nWeekly            82.0% left\n████████████████░░░░\n  resets in 1d 23h\nUpdated 0s ago"
  },
  {
    "id": "tokens",
    "command": "coderim tokens --provider claude --period week",
    "output": "Claude Code · Pro                        READY\n────────────────────────────────────────────────\n\nThis Week ·…  86,200 tokens [ready]\nBreakdown     Input 68,960 · Cached input 34,480\n               · Output 17,240\nUpdated 0s ago"
  },
  {
    "id": "json",
    "command": "coderim usage --provider codex --format json --pretty",
    "output": "{\n  \"generatedAt\" : \"2026-09-28T02:55:46Z\",\n  \"providers\" : [\n    {\n      \"enabled\" : true,\n      \"fidelity\" : \"official\",\n      \"id\" : \"codex\",\n      \"limits\" : {\n        \"headlineID\" : \"session\",\n        \"staleAfterSeconds\" : 300,\n        \"state\" : \"ready\",\n        \"updatedAt\" : \"2026-09-28T02:55:46Z\",\n        \"windows\" : [\n          {\n            \"durationMinutes\" : 300,\n            \"id\" : \"session\",\n            \"name\" : \"Session\",\n            \"resetsAt\" : \"2026-09-28T05:29:46Z\",\n            \"usedPercent\" : 32\n          },\n          {\n            \"durationMinutes\" : 10080,\n            \"id\" : \"weekly\",\n            \"name\" : \"Weekly\",\n            \"resetsAt\" : \"2026-09-30T02:55:46Z\",\n            \"usedPercent\" : 18\n          }\n        ]\n      },\n      \"localUsage\" : {\n        \"periodsAsOf\" : \"2026-09-28T02:55:46Z\",\n        \"scope\" : \"this-mac\",\n        \"state\" : \"ready\",\n        \"timeZoneIdentifier\" : \"Asia\\/Seoul\",\n        \"totals\" : {\n          \"all-time\" : {\n            \"cachedInputTokens\" : 49920,\n            \"inputTokens\" : 99840,\n            \"outputTokens\" : 24960,\n            \"totalTokens\" : 124800\n          },\n          \"month\" : {\n            \"cachedInputTokens\" : 49920,\n            \"inputTokens\" : 99840,\n            \"outputTokens\" : 24960,\n            \"totalTokens\" : 124800\n          },\n          \"today\" : {\n            \"cachedInputTokens\" : 49920,\n            \"inputTokens\" : 99840,\n            \"outputTokens\" : 24960,\n            \"totalTokens\" : 124800\n          },\n          \"week\" : {\n            \"cachedInputTokens\" : 49920,\n            \"inputTokens\" : 99840,\n            \"outputTokens\" : 24960,\n            \"totalTokens\" : 124800\n          }\n        },\n        \"updatedAt\" : \"2026-09-28T02:55:46Z\"\n      },\n      \"name\" : \"Codex\",\n      \"plan\" : \"Plus\"\n    }\n  ],\n  \"schemaVersion\" : 1\n}"
  }
];
