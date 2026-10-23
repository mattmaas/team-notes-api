# Team Notes API

[![CI](https://github.com/mattmaas/team-notes-api/actions/workflows/ci.yml/badge.svg)](https://github.com/mattmaas/team-notes-api/actions/workflows/ci.yml)

A REST backend for notes shared among several small teams. Members of a team keep private notes and collaborate on team notes, without silently overwriting each other's edits. Built with .NET 8 minimal APIs, EF Core and SQLite; no external services are needed to run it.

**Docs:** [design decisions and contract](docs/DESIGN.md) · [how it was built and verified](docs/PROCESS.md) · [runnable requests](requests.http)

## Run

Prerequisite: any .NET 8 SDK (`global.json` accepts 8.0.100 or newer and keeps a newer major version from being used). Nothing else: the SQLite database file (`teamnotes.db`) is created and migrated on first start, and demo users and notes are seeded.

```bash
dotnet run --project src/TeamNotes.Api --launch-profile http
# http://localhost:5080  ·  Swagger UI at http://localhost:5080/swagger
```

The `http` profile sets `ASPNETCORE_ENVIRONMENT=Development`, which is what enables the demo identity header and seed data. Started any other way (e.g. `--no-launch-profile` or as Production), every note endpoint returns 401 by design. To start over with fresh demo data, stop the app and delete `src/TeamNotes.Api/teamnotes.db`.

## Test

```bash
dotnet test
```

176 tests: 152 drive the real app over HTTP in-process, each against its own SQLite file, so migrations, constraints and the generated SQL are the ones production uses; 23 test the schema, constraints and seeding directly through EF; 1 checks the bundled SQLite engine is a patched version.

CI ([`.github/workflows/ci.yml`](.github/workflows/ci.yml)) runs the same checks on Ubuntu and Windows for every push and pull request: restore with a dependency audit that fails on any known-vulnerable package, a warnings-as-errors build, and the tests. To run the same gate locally:

```bash
dotnet restore -p:NuGetAudit=true -p:NuGetAuditMode=all -p:NuGetAuditLevel=low -warnaserror:NU1901,NU1902,NU1903,NU1904
dotnet build --no-restore -warnaserror
dotnet test --no-build
```

## API overview

All endpoints:

| Method | Route | Result |
|---|---|---|
| GET | `/health` | 200, no identity needed |
| GET | `/me/teams` | Your teams and your role in each |
| GET | `/teams/{teamId}/notes?page=1&pageSize=20&q=text` | Notes you can see, newest first: `{ items, page, pageSize, totalCount }`. Items are summaries (no body). `pageSize` 1–100; `q` is a literal, case-insensitive (ASCII) match on title or body |
| POST | `/teams/{teamId}/notes` | 201 with `Location` and `ETag`; body `{ "title", "body", "visibility": "Private" \| "Team" }` (default Private) |
| GET | `/teams/{teamId}/notes/{noteId}` | 200 with `ETag`, or 404 if it doesn't exist or you can't see it. Send `If-None-Match: "<version>"` to get 304 (no body) when unchanged |
| PUT | `/teams/{teamId}/notes/{noteId}` | Replace `{ "title", "body" }`; requires `If-Match: "<version>"`; 200 with the new `ETag` |
| DELETE | `/teams/{teamId}/notes/{noteId}` | Requires `If-Match`; 204. Only the author, or a team owner for team notes; other members get 403 |

**Rate limit.** Each user gets 120 API requests per minute (configurable under `RateLimit`); over that is **429** with `Retry-After`. `/health` is not limited.

**Concurrent edits.** Every note carries a version, returned as its `ETag`. Writes must echo it in `If-Match`. If a teammate saved first you get **412** with the current `ETag`, so nobody silently overwrites anyone. Missing `If-Match` is **428**; anything other than one strong tag like `"3"` is **400**.

**Demo identity (Development only).** Send `X-User-Id` with a seeded user. This is impersonation for local review, not authentication; outside Development every protected endpoint returns 401.

| User | Id | Apollo | Gemini |
|---|---|---|---|
| Alice | `11111111-1111-1111-1111-111111111111` | Owner | – |
| Bob | `22222222-2222-2222-2222-222222222222` | Member | Member |
| Carol | `33333333-3333-3333-3333-333333333333` | – | Owner |

Try it: open [`requests.http`](requests.http) in VS Code (REST Client) or Visual Studio, or use Swagger at `http://localhost:5080/swagger` (click **Authorize** and paste a user id).

Access rules: you must be a member of the team. Team notes are visible to all members; private notes only to their author (team owners get no override). Anything you can't see returns 404, never 403, so ids can't be probed. Lists and search apply the same rule inside the database query, so page sizes and `totalCount` never reveal private notes. Full contract in [docs/DESIGN.md](docs/DESIGN.md).

## Scope

**In:** teams and memberships (seeded), private and team notes, create/read/update/delete, listing with paging and search, optimistic concurrency, validation, persistence, tests, CI.

**Deliberately out:** real authentication (a Development-only header stands in, see below), managing users, teams and memberships through the API, sharing a note across teams or with individuals, note history and undo, tags and attachments, real-time collaboration. Each would be a real feature with its own rules, not a quick addition; the list below says which I'd do first.

## Design choices I spent the most time on

Full reasoning, rejected alternatives and evidence for every decision are in [docs/DESIGN.md](docs/DESIGN.md). These three took the most thought.

### 1. Sharing model: the team is the boundary, and anything you can't see doesn't exist

Every note belongs to one team and is either **Private** (author only) or **Team** (all members). Members can edit team notes; deleting is limited to the author or a team owner, because a delete is permanent and an edit is versioned. Team owners get **no** override on private notes: "private" has to mean private.

Every way of not having access answers the same **404**: not a member, unknown team, unknown note, someone else's private note, or a note requested under the wrong team. A 403 would confirm the note exists. The same rule is applied **inside the SQL WHERE clause** for lists and search, before counting and paging, so page sizes and `totalCount` can't leak how many private notes a team has. Filtering after paging was the easy version and it leaks.

*Trade-off:* one team per note is much simpler than per-note sharing lists, but you can't share a note with another team or with one person. A user who mistypes a team id gets the same answer as an outsider; acceptable with opaque ids.

### 2. Concurrent edits: optimistic concurrency, enforced by the database

The notes are shared, so two teammates editing at once is the normal case, and last-write-wins silently throws one edit away. Each note has a version, exposed as a strong `ETag`; `PUT` and `DELETE` require `If-Match`. Missing is **428**, malformed is **400**, stale is **412** with the current ETag so the client can re-read and retry.

The part I was careful with is *where* the check happens. Reading the note, comparing versions in C#, then writing leaves a window in which two requests holding the same version both pass. The check and the increment are one SQL statement (`UPDATE ... WHERE Id = @id AND Version = @expected`), so the database serialises them and exactly one wins. I proved the race is real: a version of the code with the check in C# fails the simultaneous-edit tests intermittently, and the real code passes them every time (10 parallel writers: one 200, nine 412s).

*Trade-off:* clients must send `If-Match`, which is more work than a plain `PUT`. I chose that over accepting `*` or no header, because an optional check is a check people skip. Rejected: pessimistic locks (no fit for disconnected HTTP clients) and timestamps (one-second resolution).

### 3. Runnable with nothing installed, without pretending to be secure

Reviewers should be able to clone and run. **SQLite** with checked-in EF migrations gives real persistence, constraints and transactions with no server. **Identity** is an `X-User-Id` header naming a seeded user. It is impersonation, so it is built to fail closed: it only works when the environment is Development *and* a config flag is on, and is otherwise a 401 for everything. It is implemented as a real ASP.NET Core authentication scheme, so endpoints only see a `ClaimsPrincipal`; switching to JWT/OIDC changes registration, not endpoints. Demo data is seeded only into a fresh database, so restarts never undo user edits or bring back deleted notes.

*Trade-off:* SQLite means a single instance: no horizontal scaling, and running migrations on startup only makes sense with one instance. Header identity is a demo shortcut, not a security model, and it's labelled as one everywhere.

## What I'd change with more time

In the order I'd do them:

1. **Real authentication.** JWT bearer from an OIDC provider, mapping the token's `sub` to a user and provisioning on first sign-in. The endpoints don't change, but token validation, user mapping and provisioning are real work, not a one-liner.
2. **Membership management.** Invite and remove members, change roles. This needs rules I deliberately haven't invented yet: the last owner can't leave, and what happens to a removed member's private notes. It also means moving the membership check into the conditional write, which is safe today only because nothing changes memberships.
3. **Note history.** Keep previous versions on every update and delete. That turns 412 conflicts into "see what changed and merge" and makes delete recoverable; soft delete alone would only do half of that.
4. **Postgres for real deployment.** Multiple instances, full-text search (instead of `LIKE '%q%'`, which is ASCII-only case-insensitive and scans the team's notes), and migrations run by the deploy pipeline rather than at startup.
5. **Cursor-based paging** on `(UpdatedAt, Id)`, so deep pages stay fast and an edit while someone is paging can't shift notes between pages.
6. **Operational basics:** structured logging with request ids, a shared rate-limit store for multiple instances (per-user limiting exists, in memory), request size limits tighter than Kestrel's 30 MB default, and a container image.

What I'd **stop** doing: writing `.http` walkthroughs by hand. The scenarios already exist as integration tests, and with more time I'd generate API docs from the OpenAPI spec instead of maintaining a second copy of the examples.

**Known limits today** (details and measurements in DESIGN.md): search case folding is ASCII-only (pinned by a test); search scans the team's notes (measured at about 30–70 ms with 20,000 notes in one team); offset paging can shift notes between pages during concurrent edits.

## How I used AI

The brief allows AI tools, and I used them for throughout development.

**Tools:**
- My own interview-helper app, only for its screen OCR: it captured the brief's text so I could give it to the coding agent word for word, and I closed it afterwards.
- I used AiderDesk as the agent harness (Models of various strength were used depending on the subagent task difficulty, including Claude Sonnet, Claude Opus, and OpenAis GPT Sol models).
- Sub-agents were used for implementation, test runs and code reviews, and reusable skills helped for questioning requirements, test-first work, edge cases, security review and commit hygiene.
- Web search and the NuGet/GitHub advisory APIs for the SQLite vulnerability fix; `gh` and GitHub Actions to publish and run CI.

**What I decided and steered.** Before any code, I approved the scope and core design: one team per note, private vs team notes, 404 rather than 403 for anything you can't see, If-Match concurrency (428/400/412), SQLite, and a Development-only identity header. I asked for a hostile review of the agent's first plan. It found that plan let team owners edit and delete members' private notes and left delete without a concurrency check, and I took the fixes.

After that I let the agent make implementation-level decisions itself, on condition that each one was logged with the alternative it rejected. I read the evidence after each slice rather than every diff before commit. I redirected it to audit its own tests ("did you cheat?"), to document the process as it went, to stop at a checkpoint before stretch goals, and to run the published repo the way a reviewer would.

**Other techniques I used:**
- Nothing counted as done without a passing test run in the output.
- **Mutation checks:** the agent broke each important rule in a throwaway copy and confirmed a test failed. This caught four gaps, such as a tie-breaker test that passed without the tie-breaker because EF reordered inserts.
- A review agent in a fresh context checked the API slices and the final README with real HTTP calls and the actual SQL. It found real bugs, such as an update response that could show another writer's state under load. It's the same kind of model, so I treat it as a second look, not independent proof.
- The test audit found that deleted demo notes came back after a restart and that "Development-only" seeding wasn't actually restricted. Both were fixed with regression tests.
- Docs claim only what was verified. A number written from memory was wrong and was corrected from tool output.

**Where the AI went wrong.**
- The dedicated review agent sent back its own prompt instead of a review several times, and the Aider edit tool twice made no changes, so I switched to other agents rather than trust silence.
- A set of shell commands it gave me ran in the wrong folder and committed to a scratch repo (local and harmless, but it's why every step ends with a state check).
- And several tests passed for the wrong reason until the mutation checks exposed them.

The process is written up in [docs/PROCESS.md](docs/PROCESS.md).

## Commit history

Each commit is one slice that builds and passes its tests; the messages explain why. `git log --oneline` reads in build order:

1. Scaffold, then schema and migration, then a fix from auditing that slice's tests.
2. Dependency upgrade for a SQLite security advisory.
3. Identity and note create/read; then update/delete with concurrency; then listing and search.
4. CI, then this README.
