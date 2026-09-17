# Contributing

CodeRim favors small, reviewable changes that preserve token-accounting correctness and local privacy.

Before opening a pull request:

```bash
swift test
for test_script in Tests/Scripts/*_tests.zsh; do "$test_script"; done
Scripts/release.sh
```

Never commit real Codex session files. Parser fixtures must contain synthetic token metadata only, with no prompts, responses, source code, terminal output, or credentials.

Pull requests should explain the accounting semantics affected, tests performed, performance impact, privacy impact, and rollback path.
