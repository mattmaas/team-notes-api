# Team Notes - Design Decisions and Boundaries

Status labels describe evidence, not confidence: **Proposed**, **Implemented**, **Verified**, **Deferred**. API/authentication work remains proposed until its own implementation and HTTP tests exist.

## Goal and scope

A persistent REST backend where members of several small teams create private notes and collaborate on team-visible notes. Priorities: correctness, readable code and a reviewer being able to run the project without an external database.

Implemented so far: project baseline, schema, initial migration, development bootstrap and persistence tests. User/team administration, real authentication, cross-team sharing, history, soft deletion, tags and live collaboration are out of scope.

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

### Validation boundaries still to implement

Database constraints are a backstop, not the HTTP validator. SQLite length counts Unicode code points (and stops at NUL), while .NET string.Length counts UTF-16 code units; SQLite trim() does not reject every Unicode whitespace character. API validation must enforce documented UTF-16 limits, reject blank Unicode-whitespace titles and reject NUL characters. Names are seeded, not accepted by an administration API. Membership of a note's author in that team is an API authorization rule, not a composite database foreign key.

SQLite stores DateTime without preserving DateTimeKind. API serialization must normalize materialized timestamps to UTC and test the Z suffix; choosing DateTime alone does not prove that behavior.

## Demo bootstrap - verified

Startup requires BOTH the Development environment and Demo:SeedData=true. Production ignores the seed flag even if accidentally enabled. Configuration flags cannot remove demo users already present in a reused database; deployments need a separate database.

Bootstrap runs only if no users exist and inserts the entire fixture in one transaction. Once any user exists it does nothing: no topping up, no overwriting edits and no resurrecting deleted demo notes. This deliberately does not repair an existing or manually corrupted database. A persistent seed marker is unnecessary for this single-instance demo; multi-instance startup is not supported and needs deployment coordination.

| User (Id prefix) | Apollo | Gemini |
|---|---|---|
| Alice (11111111) | Owner | Not a member |
| Bob (22222222) | Member | Member |
| Carol (33333333) | Not a member | Owner |

Two Apollo notes initially belong to Alice: one team-visible and one private. Header identity remains unimplemented; a configuration flag by itself is not authentication.

## API contract - proposed

| Method | Route | Behavior |
|---|---|---|
| GET | /me/teams | Caller teams and roles |
| GET | /teams/{teamId}/notes | Visible notes, bounded paging, optional literal search |
| POST | /teams/{teamId}/notes | Create; 201, Location and ETag |
| GET | /teams/{teamId}/notes/{noteId} | Visible note and ETag |
| PUT | /teams/{teamId}/notes/{noteId} | Replace title/body; If-Match required |
| DELETE | /teams/{teamId}/notes/{noteId} | Hard delete; If-Match required |

Every note operation requires current team membership. Wrong team/note pairing returns 404. Missing/invisible resources return 404; a visible resource with a forbidden operation returns 403.

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
| D5 | X-User-Id demo identity behind an opt-in; proposed | Deliberate impersonation for local review, NOT secure authentication. Must be Development-only; production needs a real scheme. Health can remain public. |
| D6 | Guid identifiers and UTC DateTime; partly implemented | Opaque IDs, not access control; seeds are public/predictable. SQLite sorts DateTime; UTC materialization/JSON still needs tests. |
| D7 | Swagger plus .http examples; proposed | Reviewer usability, without building a frontend. |
| D8 | Localhost HTTP without HTTPS redirect; implemented | No trusted dev certificate needed. Not an internet deployment configuration. |
| D9 | GitHub Actions test workflow; proposed | Independent reproducibility evidence, first optional extra. |
| D10 | Development AND explicit seed flag; verified | A production flag mistake cannot create demo users. Auth flag is only configuration so far, not implemented behavior. |
| D11 | Isolated real SQLite files; verified | Same migrations/SQL constraints and two sequential in-process hosts over one SQLite file. EF InMemory cannot test this; in-memory SQLite is suitable for other relational tests but cannot demonstrate restart durability. |
| D12 | Delete RESTRICT; schema implemented, referenced-team deletion tested | Avoid silently cascading shared work. Other user/team deletion lifecycle rules remain out of scope. |
| D13 | Transactional fresh-database bootstrap; verified for environment/existing data/deletion/rollback | Supersedes per-row repair: that policy resurrected deleted demo notes. Empty-users guard is intentionally simple; no multi-instance guarantee. A trigger-induced note insert failure proves users, teams and memberships roll back too. |
| D14 | Caller-owned test contexts, per-factory Pooling=False; verified by passing tests | No leaked DI scopes or global ClearAllPools affecting parallel tests. Direct test contexts mirror SQLite options; HTTP tests must exercise actual DI registration as it evolves. |

## Dependencies and security audit

Pinned packages: EF Core SQLite/Design 8.0.11, Swashbuckle 6.6.2, Mvc.Testing 8.0.11, FluentAssertions 6.12.2 plus scaffold test tooling. Each was added for persistence/migrations, browser documentation or real HTTP tests; no new packages in the persistence audit.

OPEN - dependency remediation required before delivery: `dotnet list TeamNotes.sln package --vulnerable --include-transitive` reported High advisories for SQLitePCLRaw.lib.e_sqlite3 2.1.6 (GHSA-2m69-gcr7-jv3q) and test-project transitive System.Net.Http 4.3.0 / System.Text.RegularExpressions 4.3.0. No vulnerabilities were suppressed. SQLite advisory covers versions <=2.1.11, so a token patch bump may not resolve it. Track a compatible dependency upgrade/native-provider decision separately; do not imply pinned equals secure. The service accepts no raw SQL, but that is not proof the native dependency is safe. No deployment before this review item is resolved.

## Verification evidence

| Stage | Command/check | Result |
|---|---|---|
| Initial persistence commit d445828 | dotnet test TeamNotes.sln | 13 passed; did not prove development gating or deleted-note preservation |
| Audit tests before fixes | dotnet test TeamNotes.sln | 3 failed, 19 passed: production seeding, existing-user bootstrap, deleted-note resurrection |
| Audit fixes | dotnet test TeamNotes.sln | 22 passed, 0 failed |
| Additional independent literal boundary and rollback tests | dotnet test TeamNotes.sln | 24 passed, 0 failed; title 200/201 and body 50000/50001; seed failure rolls back all four tables |
| Audit build | dotnet build TeamNotes.sln -warnaserror | 0 warnings, 0 errors; not a dependency security clearance |
| Independent mutation: original d445828 seeder/startup with new tests, isolated copy | Three seed regressions | All 3 fail as expected |
| Independent mutation: remove Version.IsConcurrencyToken(), isolated copy | Stale-version test | Fails as expected; unprotected lost write detected |
| Real-process smoke checks | Development startup and GET /health | Startup succeeds; automated two-host tests, not health alone, prove persisted edits/deletions |

Corrected duplicate-membership expectation is based on the EF/SQLite boundary: a fresh context has no tracked duplicate, so SQLite rejects the primary key; EF wraps it in DbUpdateException. Test now checks SQLite constraint code 19 / extended PRIMARYKEY code 1555 and verifies existing Owner role is unchanged. The stale-write test verifies the winning title and version remain persisted. No assertions were relaxed to accept data corruption.

## Review limits / next checks

- Authentication, authorization, HTTP validation/ETags and UTC JSON are not implemented yet; no claim of service completion.
- Seed rollback is now fault-injection tested. Real power-loss, disk-full and migration-failure recovery are not exercised.
- API tests must cover Unicode/NUL input, wrong-team access, hidden notes, precondition order and simultaneous HTTP writes.
- SQLite is a single-instance choice, not a backup or multi-instance strategy. Backups, schema upgrade testing on existing deployments, disk-full/busy behavior and retention belong to operational hardening.
- Startup migration failures propagate rather than starting a misleadingly healthy service. Unrecoverable manually inconsistent databases require explicit repair, not automatic resets.

## Change log

- Baseline 767cb09: separate repository, run/test skeleton.
- Persistence d445828: schema, initial migration, bootstrap and 13 tests.
- Persistence audit: corrected fixture ownership, explicit Development gating, deletion-preserving bootstrap, stronger assertions, independent boundary values, rollback fault injection and 24 tests. Corrects earlier overclaims about seeding, authentication and concurrency. Dependency audit remains open as noted above.
