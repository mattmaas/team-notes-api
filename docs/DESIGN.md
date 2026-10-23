# Team Notes — Design Decisions and Boundaries

Each decision is labelled **Proposed**, **Implemented**, **Verified** (covered by a passing test) or **Deferred**.

## Goal

A small, persistent REST backend where members of several teams can create private notes and collaborate on team-visible notes. The priorities are correctness, readable code, and a project a reviewer can run with no external infrastructure.

## Data model — Proposed

- `User`: Id (Guid), Name.
- `Team`: Id (Guid), Name.
- `Membership`: UserId, TeamId, Role (`Member` | `Owner`); unique (UserId, TeamId).
- `Note`: Id (Guid), TeamId, AuthorId, Title, Body, Visibility (`Private` | `Team`), CreatedAt, UpdatedAt (UTC `DateTime`), Version (int).

The team is the workspace that contains a note. The author is the user who created it and never changes.

## API contract — Proposed

| Method | Route | Behaviour |
|---|---|---|
| GET | `/me/teams` | Teams the caller belongs to, with the caller's role |
| GET | `/teams/{teamId}/notes` | Visible notes; bounded paging; optional literal search |
| POST | `/teams/{teamId}/notes` | Create; returns 201 + `Location` + `ETag` |
| GET | `/teams/{teamId}/notes/{noteId}` | Returns the note with an `ETag` |
| PUT | `/teams/{teamId}/notes/{noteId}` | Replace title and body; requires `If-Match` |
| DELETE | `/teams/{teamId}/notes/{noteId}` | Hard delete; requires `If-Match` |

## Authorization matrix — Proposed

Every operation requires current membership of the team.

| Operation | Private note | Team note |
|---|---|---|
| Read | Author | Any member |
| Edit | Author | Any member |
| Delete | Author | Author or team Owner |

- Hidden or nonexistent → `404`. Visible but forbidden → `403`.
- Team owners have no override on other members' private notes.
- Visibility is set at creation and cannot be changed in this version. The default is `Private`.

## Concurrency — Proposed

Missing `If-Match` → `428`; malformed → `400`; stale → `412`. The version check and increment happen in a single conditional `UPDATE`/`DELETE`, never as a read followed by a separate write.

## Decision record

| # | Decision | Status | Why | Rejected alternative |
|---|---|---|---|---|
| D1 | New standalone repository | Implemented | Clean, honest commit history; no unrelated material | Branch of a scratch repo |
| D2 | .NET 8 minimal API, single API project + one test project | Implemented | Few endpoints; one more layer would add ceremony without adding value | Core/Api split, controllers, repository wrappers over EF |
| D3 | SQLite via EF Core 8, checked-in migrations applied at startup | Proposed | Data persists and the reviewer installs nothing; migrations give the schema a way to evolve | In-memory store (data lost on restart); Postgres (needs infrastructure); `EnsureCreated` (cannot evolve the schema; kept as the documented fallback) |
| D4 | Local `dotnet-ef` 8.0.11 tool manifest | Verified (`dotnet tool run dotnet-ef --version` → 8.0.11) | Pins tooling to the target runtime; the machine-wide tool is v10 | Relying on globally installed tools |
| D5 | Identity via `X-User-Id` header, enabled only when `Demo:HeaderAuth=true`; otherwise every request gets `401` | Proposed | Lets reviewers try the API without an identity provider, and fails closed when the flag is off | Fake JWTs; no identity at all |
| D6 | Guid IDs; timestamps as UTC `DateTime` | Proposed | IDs can't be guessed or enumerated; SQLite can't order `DateTimeOffset` | Integer IDs; `DateTimeOffset` with a value converter |
| D7 | Swagger UI plus a checked-in `.http` file | Proposed | Two easy ways for a reviewer to try the API | Documentation only |
| D8 | Plain HTTP on localhost:5080, no HTTPS redirect | Implemented | `dotnet run` works without trusting a dev certificate | Template default (HTTPS redirect) |
| D9 | CI: GitHub Actions running `dotnet test` | Proposed (first extra) | Gives reviewers independent evidence that the tests pass | None |

## Packages

| Package | Reason |
|---|---|
| Microsoft.EntityFrameworkCore.Sqlite 8.0.11 | Persistence |
| Microsoft.EntityFrameworkCore.Design 8.0.11 | Migration generation (private asset) |
| Swashbuckle.AspNetCore 6.6.2 | Swagger UI for reviewers |
| Microsoft.AspNetCore.Mvc.Testing 8.0.11 | In-process HTTP integration tests |
| FluentAssertions 6.12.2 | Readable assertions; pinned below 8.x because 8.x has a commercial licence |

## Verification log

| Date | Command | Result |
|---|---|---|

## Change log

| Change | Reason |
|---|---|
| Initial baseline | Repository scaffold |
