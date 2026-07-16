# Codex development branch

- Work only on branch `codex/development` in this clone.
- Build from the standalone repository root with
  `docker build -f src/Max/Dockerfile -t sipsorcery/webrtc-max-headroom:codex-<git-short-sha> .`.
- Publish immutable `codex-<git-short-sha>` tags; never overwrite production or
  user-managed tags.
- Deployment changes belong in `C:/dev/doconfigsync-codex` on branch
  `codex/maxheadroom-deployment`.
- Do not modify or deploy from `C:/dev/maxheadroom` or `C:/dev/doconfigsync`.
