# WebFilter v2 — working rules

Family web filter for Windows home PCs: a local agent on every PC (forced browser extension + WFP app rules + time limits) managed from one Hub PC over the home network. This is a rewrite; the legacy DNS-based code is frozen in tag `legacy-v1` and must not be reused.

## Source of truth

| Question | Document |
|---|---|
| Scope, architecture, milestones, decisions | [docs/MVP-PLAN.md](docs/MVP-PLAN.md) |
| Exact filtering / time / sync semantics | [tests/simulation/README.md](tests/simulation/README.md) + the scenario files |
| How behavior is verified at each layer | [docs/SIMULATION-PLAN.md](docs/SIMULATION-PLAN.md) |
| Why the legacy version failed | [docs/legacy/](docs/legacy/) — history only |

If code and a scenario disagree, the scenario wins until the user decides otherwise.

## Commands

```bash
dotnet build WebFilter.slnx
dotnet test WebFilter.slnx
dotnet build WebFilter.slnx -c Simulation      # enables SimulationHooks (#if SIMULATION)
cd tests/simulation/tools && npm install && npm run validate
```

## Architecture rules (non-negotiable)

1. Only `WebFilter.Service` opens SQLite. The admin UI and the extension go through named pipes.
2. Caller identity comes from the pipe client's token (and verified client image path), never from message content.
3. Filtering decisions are made on the device. The Hub is a management plane only; agents keep enforcing when it is unreachable.
4. Fail closed for non-admin accounts when the service is unavailable.
5. All WFP calls live in `src/WebFilter.Wfp.Native` (C++, official headers). No hand-declared WFP structs in C#.
6. Hub ↔ agent DTOs are platform-neutral (`accountId` is an opaque string, no SIDs or Windows concepts) so an Android agent can join later.
7. `SimulationHooks` exist only under `#if SIMULATION`; Release binaries must not contain them.
8. Reason codes are a closed list shared by code, schema, plan and scenarios. Adding one means updating all four.

## How to change behavior

- Scenario first: any change to filtering, time, YouTube, app or sync semantics updates `tests/simulation/scenarios/**` and `tests/simulation/README.md` in the same change, and `npm run validate` passes.
- Stay inside MVP-PLAN §2.2. Anything in §2.3 (out of scope) needs the user's explicit approval.
- A milestone is done only when its exit criteria pass in the real setting (installed service, real child account, VM lab where required). A console run is never "done".
- Elevated or system-changing tests (policies, WFP, service install) run in the Hyper-V lab, not on the dev machine.

## Conventions

- .NET 10 (LTS), C# latest, nullable on, warnings as errors (`Directory.Build.props`), file-scoped namespaces.
- WPF admin: MVVM with CommunityToolkit.Mvvm, no logic in code-behind. UI is Hebrew RTL with strings in `.resx`.
- Code, identifiers, comments and commit messages in English. User-facing docs in Hebrew.
- Branches: develop on `v2` (or feature branches off it). Do not touch `main` or `legacy-v1`. Do not push without asking.
- The repo folder is owned by another Windows account; it is registered in git's global `safe.directory`.

## Status

M0 in progress. Done: legacy snapshot + tag `legacy-v1`, branch `v2`, solution skeleton, plan, simulation catalog (61 engine scenarios). Next: spikes S1–S6 (S1 needs the user's Chrome Web Store and Microsoft Partner Center developer accounts).
