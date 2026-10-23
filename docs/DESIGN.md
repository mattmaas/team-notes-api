# Team Notes - Design Decisions and Boundaries

Status labels describe evidence, not confidence: **Proposed**, **Implemented**, **Verified**, **Deferred**. Each API row below is marked individually.

## Goal and scope

A persistent REST backend where members of several small teams create private notes and collaborate on team-visible notes. Priorities: correctness, readable code and a reviewer being able to run the project without an external database.

Implemented so far: schema, migration, development bootstrap, demo header identity, `GET /me/teams`, note create, read, update and delete with optimistic concurrency, and listing with paging and search. All six endpoints of the contract are implemented. User/team administration, real authentication, cross-team sharing, history, soft deletion, tags and live collaboration are out of scope.

## Data model - implemented, persistence tests passing

- User: Guid Id, Name.
- Team: Guid Id, Name.
- Membership: UserId, TeamId, Role (Member or Owner); composite primary key.
- Note: Guid Id, TeamId, AuthorId, Title, Body, Visibility (Private or Team), UTC CreatedAt/UpdatedAt, integer Version starting at 1.

The team contains the note; the author created it. Team, author and visibility are immutable through the API (no endpoint writes them; PUT ignores them, tested). That is an API guarantee, not a database one: entities have public setters.

Database safeguards:
- Required properties and foreign keys connect membership to user/team and note to user/team. All deletes use RESTRICT, never cascade.
- CHECK constraints cover allowed roles/visibility, version >= 1, title length 1..200 after SQLite space trimming, body length <= 50,000. SQLite does not enforce HasMaxLength.
- Enums are stored as strings for readable data independent of ordinal numbering.
- Index (TeamId, UpdatedAt) serves the list query's team filter; Membership(TeamId) supports team lookups. See "Listing and search" for how far the index goes.
- Version drives optimistic concurrency. API writes use `ExecuteUpdate`/`ExecuteDelete` with `Version = @expected` in the WHERE clause and `Version = Version + 1` in the same statement (see HTTP concurrency below). `Version` is also an EF concurrency token, which protects tracked `SaveChanges` writes (persistence tests) but is not what protects the API: bulk statements bypass the change tracker.

### Validation at the HTTP boundary - verified for create and update

Database constraints are a backstop, not the HTTP validator. SQLite length counts Unicode code points (and stops at NUL), while .NET string.Length counts UTF-16 code units, and SQLite trim() only strips ASCII spaces. `NoteValidation.Validate` (shared by create and update) therefore applies the documented limits in UTF-16 units (stricter, so a valid request never trips a constraint), trims Unicode whitespace with .NET `Trim()`, rejects NUL, and requires `body` to be present (an empty string is allowed). Title is stored trimmed; body is stored exactly as sent. Violations return 400 ProblemDetails with an `errors` entry naming the field. Unknown visibility names (e.g. `"Public"`), numeric values and malformed JSON return 400 before the handler runs, because the enum converter rejects integers. Enum names are matched case-insensitively (`"team"` is accepted as `Team`), which is the System.Text.Json default and is harmless. A missing or wrong `Content-Type` returns 415, and bodies over Kestrel's 30 MB limit return 413; neither reaches the handler. Author membership of the team is enforced by the endpoint, not by a composite foreign key.

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
| GET | /teams/{teamId}/notes | Visible notes as summaries, newest first, paged, optional literal search | Verified |
| POST | /teams/{teamId}/notes | Create; 201, Location and strong ETag `"1"` | Verified |
| GET | /teams/{teamId}/notes/{noteId} | Visible note and ETag; `If-None-Match` with the current ETag returns 304 (checked after access, so never for hidden notes) | Verified |
| PUT | /teams/{teamId}/notes/{noteId} | Replace title/body; If-Match required; 200 + new ETag | Verified |
| DELETE | /teams/{teamId}/notes/{noteId} | Hard delete; If-Match required; 204 | Verified |

Every note operation requires current team membership. Non-member, unknown team, unknown note, hidden private note and wrong team/note pairing return 404, so ids cannot be probed. Tests assert the status for each case and byte-identical bodies for hidden versus missing notes; an independent probe also found the bodies identical for the other cases. Membership is checked before validation, so a non-member posting an invalid body also gets 404, not field errors. A visible resource with a forbidden operation returns 403 ProblemDetails: today that is only a delete by a member who is neither the author nor a team owner.

The create body carries only title, body and visibility; the update body only title and body. Id, author, team, version, timestamps (and, on update, visibility) come from the server; tests send forged values to both and check they are ignored. Access rules live in `NoteAccess` (`CanRead`, `CanEdit`, `CanDelete`), so endpoints and the list query cannot drift apart. The list query uses `NoteAccess.ReadableBy`, the same rule as `CanRead` written as an expression EF translates to SQL; a test checks that list results equal what `CanRead` allows.

| Operation | Private note | Team-visible note |
|---|---|---|
| Read | Author | Any member |
| Edit | Author | Any member |
| Delete | Author | Author or team owner |

Owners cannot override private-note access. Visibility defaults to Private and is immutable through the API in this version. Caller identity, author, timestamps and versions are never trusted from request-body metadata.

### Listing and search - verified

`GET /teams/{teamId}/notes?page=1&pageSize=20&q=text` returns `{ items, page, pageSize, totalCount }`.

**Hidden notes are filtered before paging.** The visibility rule is in the SQL WHERE clause of both the COUNT and the page query, so private notes never reach COUNT, OFFSET or LIMIT. If filtering ran after paging, a page could come back short or empty and `totalCount` would reveal how many private notes exist. The SQL captured during review:

```sql
SELECT ... FROM Notes
WHERE TeamId = @team AND (Visibility = 'Team' OR AuthorId = @me)
  AND (Title LIKE @pattern ESCAPE '\' OR Body LIKE @pattern ESCAPE '\')
ORDER BY UpdatedAt DESC, Id DESC
LIMIT @size OFFSET @offset
```

**Order.** Most recently updated first, then `Id` descending. `UpdatedAt` alone is not unique (many notes can share a timestamp), and without a unique last key the database may return ties in any order, so walking pages could repeat or skip notes. A test walks every page over notes with shared timestamps and checks each appears exactly once. The review confirmed that EF's stored text forms sort correctly: `DateTime` text sorts chronologically and uppercase Guid text sorts the same way as `Guid.CompareTo`.

**Paging.** `page` starts at 1. `pageSize` is 1 to 100, default 20. Anything else returns 400 with the field named. Values are never clamped, because a client asking for 500 and silently getting 100 would believe it had everything. A page past the end returns an empty `items` list with the real `totalCount`, not 404, so clients can tell "no more" from "no access". Parameters are parsed by hand rather than by minimal-API binding, because binding failures (`page=abc`, empty values, overflow, repeats) produced a bare 400 without naming the field.

**Search (`q`).** Trimmed; blank means no filter; at most 200 characters; NUL rejected, because SQLite stops reading a LIKE pattern at NUL and the filter would silently match everything. It matches a substring of title or body *literally*: `%`, `_` and the escape character `\` are escaped, so `100%` finds "100%" and not "1000". The pattern is a bound parameter, so user text is never SQL (a `'; DROP TABLE` probe matched literally). Search runs inside the same WHERE, before paging, so it cannot surface hidden notes or count them.

**Items are summaries without the body.** A page of 100 notes could otherwise be 5 MB. Clients fetch one note for its body and ETag; each summary includes `version`, so a client can tell whether a cached copy is stale.

**Known limits, accepted at this scale:**
- *Case-insensitive for ASCII only.* SQLite's LIKE folds `A-Z` but not `É`/`é`; a test locks that. Fixing it needs an ICU build of SQLite or a normalised search column. Rejected for now: per-row `lower()` in .NET does not run in SQL.
- *Substring search is a scan.* The team filter uses `IX_Notes_TeamId_UpdatedAt`, but `LIKE '%...%'` cannot use an index, and it reads bodies of up to 50,000 characters. The review measured 30 to 70 ms per request with 20,000 notes in one team. Next step if it matters: SQLite FTS5, or a database with real full-text search.
- *The Id tie-breaker costs a sort step.* With `Id` in ORDER BY, SQLite sorts the team's matching rows in a temporary B-tree instead of reading the index in order. An index on `(TeamId, UpdatedAt DESC, Id DESC)` would avoid it. Not added: no measured need, and every index slows writes.
- *Offset paging.* Deep pages cost more, and a note edited while you page moves to the top, so another note can shift between pages. Cursor (keyset) paging on `(UpdatedAt, Id)` fixes both; offset paging was chosen because it is simpler to use and explain, and teams are small.
- *Count and page are two statements.* A note created between them can make `totalCount` differ from the items by one. Both statements use the same visibility filter, so this never leaks hidden notes. A transaction would close the gap at the cost of holding a read lock.

### HTTP concurrency - verified

ETags are strong and carry the version: `"3"`. Writes must send exactly one in `If-Match`.

| Check, in order | PUT | DELETE |
|---|---|---|
| 1. Member of team, note exists under that team, caller can see it | else 404 | else 404 |
| 2. Caller may perform the operation | anyone who can see can edit | else 403 |
| 3. If-Match present | else 428 | else 428 |
| 4. If-Match is one strong canonical tag: `"1"`, not `1`, `W/"1"`, `*`, lists, `"01"`, `"0"`, negatives or overflow | else 400 | else 400 |
| 5. Body valid | else 400 | not applicable |
| 6. One conditional SQL statement: `WHERE Id AND TeamId AND Version = expected` | 0 rows: 412 with current ETag, or 404 if deleted meanwhile | same |

Why this order: visibility first so 428, 412 and 403 never reveal a hidden note (tested: a non-member without If-Match gets 404, not 428). Permission before precondition, so a request that can never succeed says why. Precondition before validation, because a client without a version has to re-read anyway.

Why the precondition lives in the SQL: checking the version in C# and then writing unconditionally leaves a window in which two writers holding the same version both pass the check. A mutant built that way fails the simultaneous-edit tests intermittently (both outcomes appeared over 16 runs): that is the lost update. With the version in the WHERE clause SQLite serialises the two statements and the second matches zero rows. The SQL captured during review is a single `UPDATE ... SET Version = Version + 1 ... WHERE Id = @id AND TeamId = @team AND Version = @expected`.

The 200 response is built from what this request wrote (version = expected + 1), not from a re-read. A re-read can return a later writer's content and version, or nothing if the note was deleted in between; the review reproduced this (4 of 1200 responses under load). A test uses a SQLite trigger to make that interleaving deterministic.

Rejected: `*` and weak tags (the client must name the version it edited, otherwise the protection is optional); `If-Unmodified-Since` (one-second resolution); pessimistic locks (no fit for disconnected HTTP clients); last-write-wins (silently loses a teammate's edit). Changing visibility is out of scope; adding it later must use the same conditional write.

## Decision record

| ID | Decision/status | Why / alternative |
|---|---|---|
| D1 | Separate repository for this project; implemented | Clean real history containing only this project. |
| D2 | One .NET 8 minimal API project and one test project; implemented | No repository wrappers or extra assemblies without a concrete benefit. |
| D3 | SQLite/EF Core, checked-in migration, startup Migrate(); verified | Durable local data and no DB-server installation. Reject in-memory-only storage. Migrate applies schema changes, never a reset; future destructive migrations still need review, backups and deployment discipline. |
| D4 | Local dotnet-ef 8.0.11; version verified | Tool works with target runtime; machine-wide v10 was not assumed usable. `global.json` requires .NET 8 (8.0.100+, latestFeature): any 8.0 SDK, never 9+. |
| D5 | X-User-Id demo identity as a real auth scheme, Development AND flag; verified | Deliberate impersonation for local review, NOT secure authentication. Fails closed (401) outside Development; tested by seeding in Development then restarting the same database as Production. Health stays public. Rejected: middleware that sets a user id (bypasses ASP.NET authorization); fake JWTs (look secure, are not). |
| D6 | Guid identifiers and UTC DateTime; verified | Opaque IDs, not access control; seeds are public/predictable. SQLite sorts DateTime; responses mark values UTC and a test checks the `Z` suffix after a database round trip. |
| D7 | Swagger (Development) plus checked-in requests.http; implemented | Two ways for a reviewer to exercise the API without a frontend. Swagger declares the X-User-Id header as an API key so the UI can send it. |
| D8 | Localhost HTTP without HTTPS redirect; implemented | No trusted dev certificate needed. Not an internet deployment configuration. |
| D9 | GitHub Actions CI (`.github/workflows/ci.yml`); verified on GitHub Actions (Ubuntu and Windows green, 176 passed on each) | Gives reviewers evidence independent of my machine. Same commands as the local loop. Runs on Ubuntu and Windows (likely deploy target vs. development machine) on every push and PR, plus weekly so a newly published advisory is caught without a code change. The dependency audit fails the build: NuGet audit warnings NU1901 to NU1904 are errors during restore. `dotnet list package --vulnerable` stays as a readable report only, because it exits 0 even when it finds vulnerabilities (checked against the pre-fix packages). Read-only token, 15-minute timeout, test results uploaded as `.trx`. Rejected: no NuGet cache (needs lock files; restore takes seconds); no coverage gate (a percentage target rewards the wrong tests, and mutation checks are the evidence here); no Docker or deploy stage (out of scope). |
| D10 | Development AND explicit flag for both demo features (seed data, header identity); verified | A production flag mistake cannot create demo users or enable impersonation. Residual risk: `appsettings.Development.json` turns both on, so a server accidentally started as Development is impersonable. Deployment must never set that environment. |
| D11 | Isolated real SQLite files; verified | Same migrations/SQL constraints and two sequential in-process hosts over one SQLite file. EF InMemory cannot test this; in-memory SQLite is suitable for other relational tests but cannot demonstrate restart durability. |
| D12 | Delete RESTRICT; schema implemented, referenced-team deletion tested | Avoid silently cascading shared work. Other user/team deletion lifecycle rules remain out of scope. |
| D13 | Transactional fresh-database bootstrap; verified for environment/existing data/deletion/rollback | Supersedes per-row repair: that policy resurrected deleted demo notes. Empty-users guard is intentionally simple; no multi-instance guarantee. A trigger-induced note insert failure proves users, teams and memberships roll back too. |
| D14 | Caller-owned test contexts, per-factory Pooling=False; verified by passing tests | No leaked DI scopes or global ClearAllPools affecting parallel tests. Direct test contexts mirror SQLite options; HTTP tests must exercise actual DI registration as it evolves. |
| D15 | Remediate advisories by moving to the latest .NET 8 servicing packages plus a runtime engine-version test; verified | Smallest fix that stays on .NET 8 and supported dependency combinations. The test also catches a future downgrade. Rejected: suppressions, EF 9/10, manual native-provider pinning. |
| D16 | 404 for every invisible or unknown resource, including non-member team access; verified | Hides whether a team or note exists. Hidden and missing notes return byte-identical responses. Cost: a user who mistypes a team id gets the same answer as an outsider; acceptable for opaque ids. |
| D17 | Typed minimal-API handlers in one static `NoteEndpoints` class; validation on the request record; no service layer yet | Few endpoints and EF already acts as unit of work. A service class with one caller would only forward calls. Revisit if rules start being reused across endpoints. Rejected: controllers, MediatR, FluentValidation (a new package for five rules). |
| D18 | Optimistic concurrency with a strong version ETag, enforced by one conditional SQL statement per write; verified | Prevents lost updates between teammates without locks. The order 404 → 403 → 428/400 → 400 → 412 hides private notes and gives actionable errors. Rejected alternatives are listed under HTTP concurrency. |
| D19 | Delete narrower than edit: author, or team owner for team notes; verified | Editing is collaboration and versioned; deletion is permanent with no history, so it is limited to the person who wrote the note or the person accountable for the team. Owners still cannot delete private notes they cannot see (404). |
| D20 | `ExecuteUpdate`/`ExecuteDelete` rather than load-modify-`SaveChanges`; implemented | One round trip, and the precondition is guaranteed to be in the statement rather than relying on the tracked entity's original value. Cost: these bulk statements bypass the change tracker and the EF concurrency token, so the version predicate must be written explicitly (mutation-tested). |
| D21 | Visibility filter inside the SQL WHERE, shared with single-note reads via `NoteAccess.ReadableBy`; verified | Page sizes and counts cannot leak private notes. Rejected: loading the team's notes and filtering in .NET (leaks through counts, costs memory). |
| D22 | Offset paging, `page`/`pageSize` 1..100, total order `UpdatedAt DESC, Id DESC`, out-of-range is 400 not clamped; verified | Simple for clients and reviewers. Rejected: cursor paging (better for deep pages and concurrent edits, but more to explain; noted as the next step), clamping (hides client bugs). |
| D23 | Literal substring search with escaped LIKE; ASCII-only case folding accepted; verified | Users expect `100%` to mean 100%. Rejected: raw LIKE (wildcards leak in), full-text search (FTS5 or similar: more setup than small teams need today). |
| D24 | List items are summaries without the body; verified | Bounded response size. Cost: a client needs one more request to read a note's body. |

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
| Update/delete slice: tests first | dotnet test | 39 new tests failed (no routes), 72 existing passed |
| Update/delete slice | dotnet test; build -warnaserror | 111 passed; 0 warnings/errors. Simultaneous-edit tests passed 15/15 repeated runs |
| Update/delete mutants (isolated copies) | no version in UPDATE WHERE; no version in DELETE WHERE; C# check + unconditional SQL; no version increment; skip CanDelete; skip CanEdit; If-Match after validation; If-Match before permission; 412 without ETag | Every mutant fails at least one test. The C#-check mutant fails intermittently, which is the race itself |
| Update/delete real process | dotnet run Development, PowerShell | PUT no If-Match 428; `*` 400; `"1"` 200 ETag "2"; stale 412 ETag "2"; Bob DELETE 403; stale DELETE 412; Alice DELETE `"2"` 204; GET 404 |
| Update/delete independent review (worker agent, read-only) | captured SQL, 30-way PUT burst, 150-round PUT/DELETE race, probes | Single conditional UPDATE/DELETE confirmed; 1 OK + 29 x 412, zero 5xx. Found: success response re-read could return another writer's state or 500 if deleted (fixed: response built from our write, trigger test); `"01"` accepted (fixed); DELETE malformed-If-Match matrix untested (added); missing 401 and title-boundary tests on PUT (added); stale DESIGN line about the EF token (fixed) |
| Review fixes | dotnet test; re-read mutant | 127 passed. Restoring the re-read makes the trigger test fail |
| Listing slice: tests first | dotnet test | 39 new list tests all failed (route missing: 405 instead of a page), 127 existing passed |
| Listing slice | dotnet test; build -warnaserror | 167 passed; 0 warnings/errors |
| Listing mutants (isolated copies) | filter after paging; count before filter; no Id tie-break; ascending; no LIKE escaping; no team filter; owner sees private; clamp instead of 400; search title only; no UTC on list; Skip off-by-one | All 11 fail at least one test. The tie-break mutant first survived: the test inserted both notes in one `SaveChanges`, and EF ordered the inserts so rowid order matched Id order. Fixed by inserting separately, in both orders |
| Listing real process | dotnet run Development, PowerShell | Bob total 1, Alice total 2 (sees her private draft), Carol 404, Bob `q=private` 0, Alice `q=private` 1, `pageSize=101` 400, `q=%` 0 |
| Listing independent review (worker agent, read-only, 20k-note DB) | captured SQL, query plans, timings, injection and edge-case probes | Visibility in both WHEREs, parameterised LIKE ESCAPE, correct ordering of stored DateTime and Guid text, no 500s. Found: unparseable paging values gave 400 without naming the field; repeated `q` merged into "a,b"; NUL in `q` disabled the filter; ASCII-only case folding not documented; createdAt Z untested. All fixed or documented. Scan cost and sort step recorded as known limits |
| Listing review fixes | dotnet test; build -warnaserror | 176 passed; 0 warnings/errors |
| CI audit gate (isolated copy with the old vulnerable packages) | `dotnet list package --vulnerable` vs restore with NU1901-NU1904 as errors | The list command reported the vulnerable SQLite package and exited 0, so it cannot be the gate. Restore with audit warnings as errors exited 1 with `error NU1903`. On the real repo it exits 0 |
| CI workflow rehearsal (fresh clone, workflow commands in order) | restore with audit; build Release -warnaserror; test Release with trx logger; vulnerability report | All exit 0: 0 warnings/errors, 176 passed, trx written, no vulnerable packages. YAML parsed (PyYAML): 4 triggers, 2-OS matrix, 7 steps. Not run on GitHub yet: no remote exists until the repo is pushed, and `act` needs Docker, which is not installed. The first hosted run is the remaining check |
| Final review against the brief (worker agent, read-only; README followed literally in a fresh clone) | build, test, run, probes | Every documented behaviour reproduced; no correctness defects beyond documented limits; repo hygiene clean. Found: draft banner and to-do markers in README, a duplicated line, no commit-history section, overstated test claims ("176 integration" / "four wrong-reason tests" / "each limit covered by a test"), `global.json` pin blocking other 8.0 SDK bands, two membership helpers. All fixed. Test split confirmed from trx: 152 HTTP, 23 persistence, 1 engine check |
| Checkpoint | dotnet test; build -warnaserror; fresh-clone journey | 176 passed; 0 warnings; clone, run with `--launch-profile http`, create, edit, stale 412, list, restart with data kept |
| First hosted CI run (GitHub Actions run 40032898874, commit 7c2d1d3) | push to main | Both jobs succeeded: Ubuntu 176 passed in 5 s, Windows 176 passed; restore audit passed, 0 build warnings, no vulnerable packages. SDK 8.0.426 installed by setup-dotnet. Only annotations: Node.js 20 deprecation for the v4 actions |
| Grader journey (fresh clone of https://github.com/mattmaas/team-notes-api, README followed) | build, run with launch profile, requests, restart, test | 0 warnings; health 200, Swagger 200, no identity 401, create 201 ETag "1", non-member 404, PUT without If-Match 428, edit 200 ETag "2", stale edit 412, list total 2, delete 204, after restart total 1, 176 tests passed |
| Stretch: conditional GET + CI action bump (branch feat/stretch) | dotnet test; mutant | 183 passed (7 new). Moving the 304 check before the access check fails the hidden-note test. CI actions moved to checkout v7, setup-dotnet v6, upload-artifact v7 (Node 24; inputs checked against each action.yml) |
| API slice mutation checks (isolated copies) | remove CanRead / owner override / skip membership / ignore team id on GET / ignore environment in auth / default Team visibility / drop UTC kind | Each mutant fails at least one test. The environment-check mutant was first missed: the original test had no seeded user. Fixed by seeding in Development and restarting as Production. |
| API slice real process | dotnet run Development, fresh DB, PowerShell requests | 401 without header; Bob's teams listed; create 201 with ETag "1" and Location; Bob → Alice's private note 404; invalid title 400 ProblemDetails; Swagger JSON 200 |
| Audit build | dotnet build TeamNotes.sln -warnaserror | 0 warnings, 0 errors; not a dependency security clearance |
| Independent mutation: original d445828 seeder/startup with new tests, isolated copy | Three seed regressions | All 3 fail as expected |
| Independent mutation: remove Version.IsConcurrencyToken(), isolated copy | Stale-version test | Fails as expected; unprotected lost write detected |
| Real-process smoke checks | Development startup and GET /health | Startup succeeds; automated two-host tests, not health alone, prove persisted edits/deletions |

Corrected duplicate-membership expectation is based on the EF/SQLite boundary: a fresh context has no tracked duplicate, so SQLite rejects the primary key; EF wraps it in DbUpdateException. Test now checks SQLite constraint code 19 / extended PRIMARYKEY code 1555 and verifies existing Owner role is unchanged. The stale-write test verifies the winning title and version remain persisted. No assertions were relaxed to accept data corruption.

## Review limits / next checks

- All contract endpoints are implemented. Remaining work is delivery (README, final review), not features.
- Seed rollback is now fault-injection tested. Real power-loss, disk-full and migration-failure recovery are not exercised.
- Listing limits (ASCII-only case folding, scan-based search, offset paging, count/page gap) are described under "Listing and search".
- Ids stored in lowercase (only possible through raw SQL or an import, never through the API) would sort after uppercase ones and break the total order for those rows.
- Membership is checked when a write starts, not inside the conditional statement. No endpoint changes memberships, so it cannot go stale today; adding membership administration would need the membership check folded into the write or a transaction.
- Request body size relies on Kestrel's default 30 MB limit; the 50,000-character body limit is enforced after parsing. A tighter per-endpoint body limit is a hardening item.
- SQLite is a single-instance choice, not a backup or multi-instance strategy. Backups, schema upgrade testing on existing deployments, disk-full/busy behavior and retention belong to operational hardening.
- Startup migration failures propagate rather than starting a misleadingly healthy service. Unrecoverable manually inconsistent databases require explicit repair, not automatic resets.

## Change log

- Baseline 767cb09: separate repository, run/test skeleton.
- Persistence d445828: schema, initial migration, bootstrap and 13 tests.
- Persistence audit: corrected fixture ownership, explicit Development gating, deletion-preserving bootstrap, stronger assertions, independent boundary values, rollback fault injection and 24 tests. Corrects earlier overclaims about seeding, authentication and concurrency. Dependency audit remains open as noted above.
- Dependency remediation: EF Core / Mvc.Testing 8.0.32, xunit 2.9.3 tooling, engine-version regression test; audit clean.
- API slice 1: demo header authentication scheme, /me/teams, note create/read with membership and visibility, HTTP validation, UTC JSON, ProblemDetails, Swagger, requests.http; 72 tests after independent review fixes.
- API slice 2: PUT and DELETE with If-Match (428/400/412) enforced by a single conditional SQL statement, delete permission 403, simultaneous-edit tests; 127 tests.
- API slice 3: listing with visibility filtered in SQL before count and paging, total order with Id tie-break, literal escaped search, field-named 400s for every bad parameter; 176 tests.
- CI: GitHub Actions on Ubuntu and Windows; the dependency audit fails the build; weekly scheduled run.
- Checkpoint before stretch goals: README design choices, future work and AI usage completed; `global.json` relaxed from 8.0.426/latestPatch to 8.0.100/latestFeature, so any .NET 8 SDK builds it (the review reproduced "compatible SDK not found" with the old pin) while .NET 9+ is still refused; one membership helper instead of two.
