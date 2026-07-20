# maxheadroom
An homage to Max Headroom to demonstrate an early use of a formative Avatar Agent SDK. It is intended solely for non-commercial and demonstration purposes. It is not affiliated with, or endorsed by, the Max Headroom copyright holders.

## Built with Codex 5.6

Codex 5.6 was a core development partner in building Max. It accelerated implementation, debugging, documentation, test and benchmark development, and the Docker, CI, and release workflows. Agent work was kept on dedicated topic branches and delivered through pull requests, which made it practical to run several short, reviewable build-test-iterate cycles while preserving a clear human review point. See [benchmarking](https://github.com/sipsorcery/maxheadroom/blob/bench-data/history.md) for the initial attempts at using Codex 5.6 (and other coding agents) to solve latency challenges with the Avatar pipeline. 

The most distinctive part of that workflow is that Codex also built a separate, deployable companion service: [Codex Code Agent](https://github.com/sipsorcery/codeagent-codex). The service runs alongside Max (in a Kubernetes cluster), wraps the official Codex TypeScript SDK in an authenticated, durable task API, and manages repository checkout, Codex thread state, follow-up feedback, testing, topic branches, pushes, and pull requests using a dedicated GitHub identity.

Max's developer mode embeds a client for that service directly in the Max UI. A change can therefore be requested from Max itself, followed as Codex works on the `maxheadroom` repository, refined by continuing the same task, and then reviewed as a pull request. That closed loop: use Max, spot an improvement, ask Codex to implement it, test the result, and send the next instruction without leaving Max; made rapid iteration possible. The benchmark tooling added with Codex, including WebRTC media, speech-to-text, and LLM latency coverage, provided objective feedback while those iterations were underway.

The in-app integration and its security model are described in the [Max implementation README](src/Max/README.md#coding-agent-chat-poc).
