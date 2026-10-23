# How this was built

A short record of the working process, kept current as the project moves. Design decisions themselves live in [DESIGN.md](DESIGN.md).

## Loop per slice

1. **Agree the contract first.** Each slice starts from rules written in DESIGN.md (status codes, access matrix, limits), so tests can be written from the spec instead of from the code.
2. **Tests at the HTTP seam.** Integration tests drive the real app through `WebApplicationFactory` against a real SQLite file per test. Expected values come from the spec or seeded fixture, not from production constants.
3. **Implement the smallest thing that passes.**
4. **Prove the tests can fail.** For each important rule, a deliberately broken copy of the code (in a temp folder, never the repo) must fail at least one test. This has caught real gaps twice: an auth test that passed for the wrong reason, and an ordering claim nothing tested.
5. **Independent review.** A second AI agent reviews the diff read-only, often with real HTTP probes, on two axes: security/standards and spec/docs. Findings are fixed, rejected with a reason, or deferred in DESIGN.md.
6. **Docs match evidence.** DESIGN.md marks each item Proposed / Implemented / Verified, and the verification table records the command and result. Overclaims found in review are corrected, not left.
7. **One commit per logical change.** Conventional commit messages explain *why*. Every commit builds and passes its tests.

## Commands

```bash
dotnet test                                                  # all tests
dotnet build -warnaserror                                    # warnings fail the build
dotnet list package --vulnerable --include-transitive        # dependency advisories
```

## Use of AI

AI agents wrote most of the code and tests under my direction. I chose the scope, contract and trade-offs, and reviewed every change. Practices I insisted on because AI output needs them:

- No claim of "done" without a quoted, passing test run.
- Mutation checks, so a green suite is not taken as proof the tests mean anything.
- An independent second-agent review of each slice, instead of the authoring agent grading its own work.
- Recorded rejections: suggestions I did not adopt are noted with the reason (e.g. treating header whitespace as invalid, which HTTP strips before the app sees it).

## History

| Commit | Slice |
|---|---|
| 767cb09 | Scaffold, tooling, design doc |
| d445828 | Schema, migration, demo seed |
| db84b40 | Review fixes: seed is bootstrap-only and Development-gated |
| 6564212 | Dependency advisories cleared on the .NET 8 line |
| ac91935 | Demo identity, team listing, note create/read |
