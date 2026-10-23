# Team Notes API

A small REST backend for notes shared among several small teams. Built with .NET 8 minimal APIs, EF Core and SQLite.

> Status: work in progress. Sections marked _TODO_ are filled in as features are implemented and verified.

## Run

Prerequisite: .NET 8 SDK (pinned in `global.json`).

```bash
dotnet run --project src/TeamNotes.Api --launch-profile http
# http://localhost:5080; profile sets ASPNETCORE_ENVIRONMENT=Development
```

## Test

```bash
dotnet test
```

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
| GET | `/teams/{teamId}/notes/{noteId}` | 200 with `ETag`, or 404 if it doesn't exist or you can't see it |
| PUT | `/teams/{teamId}/notes/{noteId}` | Replace `{ "title", "body" }`; requires `If-Match: "<version>"`; 200 with the new `ETag` |
| DELETE | `/teams/{teamId}/notes/{noteId}` | Requires `If-Match`; 204. Only the author, or a team owner for team notes; other members get 403 |

**Concurrent edits.** Every note carries a version, returned as its `ETag`. Writes must echo it in `If-Match`. If a teammate saved first you get **412** with the current `ETag`, so nobody silently overwrites anyone. Missing `If-Match` is **428**; anything other than one strong tag like `"3"` is **400**.

**Demo identity (Development only).** Send `X-User-Id` with a seeded user. This is impersonation for local review, not authentication; outside Development every protected endpoint returns 401.

| User | Id | Apollo | Gemini |
|---|---|---|---|
| Alice | `11111111-1111-1111-1111-111111111111` | Owner | – |
| Bob | `22222222-2222-2222-2222-222222222222` | Member | Member |
| Carol | `33333333-3333-3333-3333-333333333333` | – | Owner |

Try it: open [`requests.http`](requests.http) in VS Code (REST Client) or Visual Studio, or use Swagger at `http://localhost:5080/swagger` (click **Authorize** and paste a user id).

Access rules: you must be a member of the team. Team notes are visible to all members; private notes only to their author (team owners get no override). Anything you can't see returns 404, never 403, so ids can't be probed. Lists and search apply the same rule inside the database query, so page sizes and `totalCount` never reveal private notes. Full contract in [docs/DESIGN.md](docs/DESIGN.md).

## Design choices I spent the most time on

_TODO_ (summary here; full reasoning in [docs/DESIGN.md](docs/DESIGN.md))

## What I'd change with more time

_TODO_

## How I used AI

_TODO_
