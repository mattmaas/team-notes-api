# Team Notes - Design Decisions and Boundaries

Status labels describe evidence, not confidence: **Proposed**, **Implemented**, **Verified**, **Deferred**. Each API row below is marked individually.

## Goal and scope

A persistent REST backend where members of several small teams create private notes and collaborate on team-visible notes. Priorities: correctness, readable code and a reviewer being able to run the project without an external database.

Implemented so far: schema, migration, development bootstrap, demo header identity, `GET /me/teams`, note create and read. Update, delete, listing/search and HTTP concurrency remain proposed. User/team administration, real authentication, cross-team sharing, history, soft deletion, tags and live collaboration are out of scope.

## Data model - implemented, persistence tests passing

- User: Guid Id, Name.
- Team: Guid Id, Name.
- Membership: UserId, TeamId, Role (Member or Owner); composite primary key.
- Note: Guid Id, TeamId, AuthorId, Title, Body, Visibility (Private or Team), UTC CreatedAt/UpdatedAt, integer Version starting at 1.

The team contains the note; the author created it. Immutability of team/author/visibility is an upcoming API contract, not a database guarantee: entities currently have public setters.

Database safeguards:
- Required properties and foreign keys connect membership to user/team and note to user/team. All deletes use RESTRICT, never cascade.
- CHECK constraints cover allowed roles/visibility, version >= 1, title length 1..200 after SQLite space trimming, body length <= 50,000. SQLite does not enforce HasMaxLength.
- Enums are stored as strings for readable data independent of ordinal numbering.
- Index (TeamId, UpdatedAt) supports team listing; Membership(TeamId) supports team lookups. Stable listing also needs an Id tie-breaker in the future query.
- Version is an EF concurrency token: a tracked save compares the original version in its SQL predicate. Writers must still increment it explicitly. This is not an automatic SQL counter or a completed HTTP If-Match implementation.

### Validation at the HTTP boundary - verified for create

Database constraints are a backstop, not the HTTP validator. SQLite length counts Unicode code points (and stops at NUL), while .NET string.Length counts UTF-16 code units, and SQLite trim() only strips ASCII spaces. `CreateNoteRequest.Validate` therefore applies the documented limits in UTF-16 units (stricter, so a valid request never trips a constraint), trims Unicode whitespace with .NET `Trim()`, rejects NUL, and requires `body` to be present (an empty string is allowed). Title is stored trimmed; body is stored exactly as sent. Violations return 400 ProblemDetails with an `errors` entry naming the field. Unknown visibility names (e.g. `"Public"`), numeric values and malformed JSON return 400 before the handler runs, because the enum converter rejects integers. Enum names are matched case-insensitively (`"team"` is accepted as `Team`), which is the System.Text.Json default and is harmless. A missing or wrong `Content-Type` returns 415, and bodies over Kestrel's 30 MB limit return 413; neither reaches the handler. Author membership of the team is enforced by the endpoint, not by a composite foreign key.

Timestamps: SQLite drops DateTimeKind, so `NoteResponse.From` marks stored values as UTC. A test reads a note back through GET and asserts both timestamps end in `Z`.

## Demo bootstrap - verified

Startup requires BOTH the Development environment and Demo:SeedData=true. Production ignores the seed flag even if accidentally enabled. Configuration flags cannot remove demo users already present in a reused database; deployments need a separate database.

Bootstrap runs only if no users exist and inserts the entire fixture in one transaction. Once any user exists it does nothing: no topping up, no overwriting edits and no resurrecting deleted demo notes. This deliberately does not repair an existing or manually corrupted database. A persistent seed marker is unnecessary for this single-instance demo; multi-instance startup is not supported and needs deployment coordination.

| User (Id prefix) | Apollo | Gemini |
|---|---|---|
| Alice (11111111) | Owner | Not a member |
| Bob (22222222) | Member | Member |
| Carol (33333333) | Not a member | Owner |

Two Apollo notes initially belong to Alice: one team-visible and one private.

## Identity - implemented, verified

`DemoHeaderAuthenticationHandler` is a standard ASP.NET Core authentication scheme. It reads `X-User-Id` only when the environment is Development AND `Demo:HeaderAuth` is true. Missing header → no identity; malformed, repeated or unknown user id → authentication failure; both yield 401 ProblemDetails with `WWW-Authenticate: DemoHeader` on protected endpoints. Only the canonical hyphenated GUID format is accepted (no braces or 32-hex form). Outside Development the handler authenticates nobody, so every protected endpoint fails closed with 401. `/health` stays anonymous; Swagger is only mapped in Development (tested to be 404 in Production).

This is impersonation for local review, not security. Because it is a real authentication scheme that produces a `NameIdentifier` claim, endpoints depend only on `ClaimsPrincipal`. Swapping in JWT bearer/OIDC changes registration, not endpoints. The real migration work (token validation, mapping `sub` to users, provisioning) is listed under future work rather than claimed as a one-line change.

## API contract

| Method | Route | Behavior | Status |
|---|---|---|---|
| GET | /me/teams | Caller teams and roles, ordered by name | Verified |
| GET | /teams/{teamId}/notes | Visible notes, bounded paging, optional literal search | Proposed |
| POST | /teams/{teamId}/notes | Create; 201, Location and strong ETag `"1"` | Verified |
| GET | /teams/{teamId}/notes/{noteId} | Visible note and ETag | Verified |
| PUT | /teams/{teamId}/notes/{noteId} | Replace title/body; If-Match required | Proposed |
| DELETE | /teams/{teamId}/notes/{noteId} | Hard delete; If-Match required | Proposed |

Every note operation requires current team membership. Non-member, unknown team, unknown note, hidden private note and wrong team/note pairing return 404, so ids cannot be probed. Tests assert the status for each case and byte-identical bodies for hidden versus missing notes; an independent probe also found the bodies identical for the other cases. Membership is checked before validation, so a non-member posting an invalid body also gets 404, not field errors. A visible resource with a forbidden operation will return 403 (delete by a non-author member, proposed). Routes that are not built yet answer 405 with an `Allow` header for the verbs that exist; that goes away as they are implemented.

The request body carries only title, body and visibility. Id, author, team, version and timestamps come from the server; a test posts forged `id`/`authorId`/`teamId`/`version` and checks they are ignored. Visibility rules live in one function, `NoteAccess.CanRead`, so the coming list query and single reads cannot drift apart.

| Operation | Private note | Team-visible note |
|---|---|---|
| Read | Author | Any member |
| Edit | Author | Any member |
| Delete | Author | Author or team owner |

Owners cannot override private-note access. Visibility defaults to Private and is immutable through the API in this version. Caller identity, author, timestamps and versions are never trusted from request-body metadata.

### HTTP concurrency - proposed

Authorize before evaluating preconditions. Support one strong quoted version ETag. Missing If-Match returns 428, malformed or unsupported form 400, stale value 412. Atomic conditional update/delete prevents lost writes. A read followed by a write is safe only if the WRITE includes the expected version predicate; an unconditional write is not.

## Decision record

| ID | Decision/status | Why / alternative |
|---|---|---|
| D1 | Separate submission repository; implemented | Clean real history without unrelated assessment files. A scratch-repo branch does not hide earlier private history. |
| D2 | One .NET 8 minimal API project and one test project; implemented | No repository wrappers or extra assemblies without a concrete benefit. |
| D3 | SQLite/EF Core, checked-in migration, startup Migrate(); verified | Durable local data and no DB-server installation. Reject in-memory-only storage. Migrate applies schema changes, never a reset; future destructive migrations still need review, backups and deployment discipline. |
| D4 | Local dotnet-ef 8.0.11; version verified | Tool works with target runtime; machine-wide v10 was not assumed usable. |
| D5 | X-User-Id demo identity as a real auth scheme, Development AND flag; verified | Deliberate impersonation for local review, NOT secure authentication. Fails closed (401) outside Development; tested by seeding in Development then restarting the same database as Production. Health stays public. Rejected: middleware that sets a user id (bypasses ASP.NET authorization); fake JWTs (look secure, are not). |
| D6 | Guid identifiers and UTC DateTime; verified | Opaque IDs, not access control; seeds are public/predictable. SQLite sorts DateTime; responses mark values UTC and a test checks the `Z` suffix after a database round trip. |
| D7 | Swagger (Development) plus checked-in requests.http; implemented | Two ways for a reviewer to exercise the API without a frontend. Swagger declares the X-User-Id header as an API key so the UI can send it. |
| D8 | Localhost HTTP without HTTPS redirect; implemented | No trusted dev certificate needed. Not an internet deployment configuration. |
| D9 | GitHub Actions test workflow; proposed | Independent reproducibility evidence, first optional extra. |
| D10 | Development AND explicit flag for both demo features (seed data, header identity); verified | A production flag mistake cannot create demo users or enable impersonation. Residual risk: `appsettings.Development.json` turns both on, so a server accidentally started as Development is impersonable. Deployment must never set that environment. |
| D11 | Isolated real SQLite files; verified | Same migrations/SQL constraints and two sequential in-process hosts over one SQLite file. EF InMemory cannot test this; in-memory SQLite is suitable for other relational tests but cannot demonstrate restart durability. |
| D12 | Delete RESTRICT; schema implemented, referenced-team deletion tested | Avoid silently cascading shared work. Other user/team deletion lifecycle rules remain out of scope. |
| D13 | Transactional fresh-database bootstrap; verified for environment/existing data/deletion/rollback | Supersedes per-row repair: that policy resurrected deleted demo notes. Empty-users guard is intentionally simple; no multi-instance guarantee. A trigger-induced note insert failure proves users, teams and memberships roll back too. |
| D14 | Caller-owned test contexts, per-factory Pooling=False; verified by passing tests | No leaked DI scopes or global ClearAllPools affecting parallel tests. Direct test contexts mirror SQLite options; HTTP tests must exercise actual DI registration as it evolves. |
| D15 | Remediate advisories by moving to the latest .NET 8 servicing packages plus a runtime engine-version test; verified | Smallest fix that stays on .NET 8 and supported dependency combinations. The test also catches a future downgrade. Rejected: suppressions, EF 9/10, manual native-provider pinning. |
| D16 | 404 for every invisible or unknown resource, including non-member team access; verified | Hides whether a team or note exists. Hidden and missing notes return byte-identical responses. Cost: a user who mistypes a team id gets the same answer as an outsider; acceptable for opaque ids. |
| D17 | Typed minimal-API handlers in one static `NoteEndpoints` class; validation on the request record; no service layer yet | Few endpoints and EF already acts as unit of work. A service class with one caller would only forward calls. Revisit if rules start being reused across endpoints. Rejected: controllers, MediatR, FluentValidation (a new package for five rules). |

## Dependencies and security audit

Pinned packages, all on the .NET 8 line: EF Core SQLite/Design 8.0.32, Mvc.Testing 8.0.32, Swashbuckle 6.6.2, FluentAssertions 6.12.2 (kept below 8.x for licensing), xunit 2.9.3, xunit.runner.visualstudio 2.8.2, Microsoft.NET.Test.Sdk 17.14.1, coverlet.collector 6.0.4. Each exists for persistence/migrations, browser documentation or real HTTP tests.

RESOLVED - the audit had reported High advisories for SQLitePCLRaw.lib.e_sqlite3 2.1.6 (GHSA-2m69-gcr7-jv3q, CVE-2025-6965: SQLite engines before 3.50.2) and for System.Net.Http 4.3.0 / System.Text.RegularExpressions 4.3.0, which reached the test project through xunit 2.5.3 → NETStandard.Library 1.6.1.
- EF Core 8.0.11 → 8.0.32 (latest 8.0 servicing patch) brings SQLitePCLRaw 2.1.13. The advisory text lists `<= 2.1.11`, so the fix is verified at runtime, not inferred from version numbers: `DependencyTests` asserts `sqlite_version() >= 3.50.2`. Against the old 8.0.11 packages (in an isolated copy) the test fails with engine 3.41.2.
- Test tooling moved to xunit 2.9.3 and its matching 2.x runner, which no longer pull in NETStandard.Library 1.6.1. Same-major updates only: xunit v3 and runner 3.x/4.x were rejected because they are a migration, not a fix.
- Rejected: hand-pinning `SQLitePCLRaw.*` 3.x beside EF Core 8, an unsupported mix; moving to EF Core 9/10, which goes beyond the agreed .NET 8 target; suppressing advisories.
- Result: `dotnet list TeamNotes.sln package --vulnerable --include-transitive` reports no vulnerable packages for either project. This is only as current as the NuGet advisory feed; CI should re-run it.

## Verification evidence

| Stage | Command/check | Result |
|---|---|---|
| Initial persistence commit d445828 | dotnet test TeamNotes.sln | 13 passed; did not prove development gating or deleted-note preservation |
| Audit tests before fixes | dotnet test TeamNotes.sln | 3 failed, 19 passed: production seeding, existing-user bootstrap, deleted-note resurrection |
| Audit fixes | dotnet test TeamNotes.sln | 22 passed, 0 failed |
| Additional independent literal boundary and rollback tests | dotnet test TeamNotes.sln | 24 passed, 0 failed; title 200/201 and body 50000/50001; seed failure rolls back all four tables |
| Dependency remediation | vulnerability audit; dotnet test; build -warnaserror | No vulnerable packages; 25 passed; 0 warnings/errors. Engine-version test fails on old packages (3.41.2) in an isolated copy |
| API slice: identity, /me/teams, create, read | dotnet test TeamNotes.sln | 63 passed, 0 failed |
| API slice independent review (worker agent, read-only plus real HTTP probes) | Production fail-closed, error leakage, 500 hunting, contract vs docs | No security blockers. No 500s from null/empty/array bodies, wrong types, 2 MB/35 MB payloads; injected exception returns a generic 500 in Development and Production. Found: `/me/teams` ordering untested (order-reversal mutant passed), lenient GUID formats accepted, no WWW-Authenticate, missing tests for repeated header / 401 shape / body NUL / Swagger off in Production, three doc overclaims |
| Review fixes | dotnet test; mutants | 72 passed. Order-reversal mutant now fails 2 tests; lenient `Guid.TryParse` mutant fails 2 tests. Whitespace around the header is not tested as invalid: HTTP strips it before the handler sees it |
| API slice mutation checks (isolated copies) | remove CanRead / owner override / skip membership / ignore team id on GET / ignore environment in auth / default Team visibility / drop UTC kind | Each mutant fails at least one test. The environment-check mutant was first missed: the original test had no seeded user. Fixed by seeding in Development and restarting as Production. |
| API slice real process | dotnet run Development, fresh DB, PowerShell requests | 401 without header; Bob's teams listed; create 201 with ETag "1" and Location; Bob → Alice's private note 404; invalid title 400 ProblemDetails; Swagger JSON 200 |
| Audit build | dotnet build TeamNotes.sln -warnaserror | 0 warnings, 0 errors; not a dependency security clearance |
| Independent mutation: original d445828 seeder/startup with new tests, isolated copy | Three seed regressions | All 3 fail as expected |
| Independent mutation: remove Version.IsConcurrencyToken(), isolated copy | Stale-version test | Fails as expected; unprotected lost write detected |
| Real-process smoke checks | Development startup and GET /health | Startup succeeds; automated two-host tests, not health alone, prove persisted edits/deletions |

Corrected duplicate-membership expectation is based on the EF/SQLite boundary: a fresh context has no tracked duplicate, so SQLite rejects the primary key; EF wraps it in DbUpdateException. Test now checks SQLite constraint code 19 / extended PRIMARYKEY code 1555 and verifies existing Owner role is unchanged. The stale-write test verifies the winning title and version remain persisted. No assertions were relaxed to accept data corruption.

## Review limits / next checks

- Update, delete, listing/search and HTTP concurrency (If-Match, 428/400/412) are not implemented yet; no claim of service completion.
- Seed rollback is now fault-injection tested. Real power-loss, disk-full and migration-failure recovery are not exercised.
- Still to test with the next endpoints: precondition order, simultaneous HTTP writes, hidden notes excluded from list results and pagination.
- Request body size relies on Kestrel's default 30 MB limit; the 50,000-character body limit is enforced after parsing. A tighter per-endpoint body limit is a hardening item.
- SQLite is a single-instance choice, not a backup or multi-instance strategy. Backups, schema upgrade testing on existing deployments, disk-full/busy behavior and retention belong to operational hardening.
- Startup migration failures propagate rather than starting a misleadingly healthy service. Unrecoverable manually inconsistent databases require explicit repair, not automatic resets.

## Change log

- Baseline 767cb09: separate repository, run/test skeleton.
- Persistence d445828: schema, initial migration, bootstrap and 13 tests.
- Persistence audit: corrected fixture ownership, explicit Development gating, deletion-preserving bootstrap, stronger assertions, independent boundary values, rollback fault injection and 24 tests. Corrects earlier overclaims about seeding, authentication and concurrency. Dependency audit remains open as noted above.
- Dependency remediation: EF Core / Mvc.Testing 8.0.32, xunit 2.9.3 tooling, engine-version regression test; audit clean.
- API slice 1: demo header authentication scheme, /me/teams, note create/read with membership and visibility, HTTP validation, UTC JSON, ProblemDetails, Swagger, requests.http; 72 tests after independent review fixes.
