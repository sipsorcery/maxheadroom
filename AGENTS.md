# Codex development workflow

- Work only on `codex/*` branches in this clone. Use a per-issue task branch
  targeting `codex/development`; never target `master` directly.
- Build from the standalone repository root with
  `docker build -f src/Max/Dockerfile -t sipsorcery/webrtc-max-headroom:codex-<git-short-sha> .`.
- Publish immutable `codex-<git-short-sha>` tags; never overwrite production or
  user-managed tags.
- Deployment changes belong in `C:/dev/doconfigsync-codex` on branch
  `codex/maxheadroom-deployment`.
- Do not modify or deploy from `C:/dev/maxheadroom` or `C:/dev/doconfigsync`.

## GitHub machine identity

- Run every `gh` command with the Codex-only configuration directory:
  `$env:GH_CONFIG_DIR = Join-Path $env:USERPROFILE '.codex\gh\sipsorcery-codex'`.
- Remove process-level `GH_TOKEN` and `GITHUB_TOKEN` overrides before using the
  stored isolated credentials. Never change or use the default `gh` profile for
  repository writes.
- Immediately before every GitHub write, verify that
  `gh api user --jq .login` returns `sipsorcery-codex`.
- Use Git author `Sipsorcery Codex <aaron+codex@sipsorcery.com>` for commits.
