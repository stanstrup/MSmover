# Contributing

## Commit messages decide the version number

Releases are cut by [semantic-release](https://semantic-release.gitbook.io/) from the commit
history on `main`. There is no manual tagging, no version to bump by hand, and no changelog to
edit — all three are derived from what the commits say. That only works if the commits say it in a
form a machine can read, so they follow
[Conventional Commits](https://www.conventionalcommits.org/) and are linted in CI.

```
<type>(<optional scope>): <subject>

<optional body>

<optional footer>
```

### Types and what they do

| Type | Release | Changelog section |
|---|---|---|
| `feat` | **minor** — 0.3.0 → 0.4.0 | Features |
| `fix` | **patch** — 0.3.0 → 0.3.1 | Bug fixes |
| `perf` | patch | Performance |
| `refactor` | patch | Refactoring |
| `docs` | patch | Documentation |
| `build` | patch | Build and packaging |
| `test` | none | hidden |
| `ci` | none | hidden |
| `chore` | none | hidden |

> [!NOTE]
> Versioning starts in the 0.x range on purpose. semantic-release would otherwise cut `1.0.0` as
> its first release, and MSmover has not yet been validated on a real instrument over a real
> acquisition run — 0.x says that honestly. A `v0.0.0` tag anchors the sequence; the first release
> is `0.1.0`. When you are satisfied it has earned it, tag `v1.0.0` by hand once and
> semantic-release will carry on from there.

A **breaking change** forces a major release. Mark it with `!` after the type, and explain it in a
`BREAKING CHANGE:` footer:

```
feat(config)!: store rules as an array instead of a map

BREAKING CHANGE: config.json written by 0.x is not read by 1.0. Delete it and
re-create your rules, or convert it with the script in build/.
```

### Scopes

Optional, but keep to the list in `commitlint.config.js`:

`core` `app` `transfer` `detection` `naming` `symlink` `config` `logging` `docs` `build` `ci`
`deps` `release`

### Examples

```
feat(symlink): add a tool to clear symbolic links from a source folder
fix(transfer): keep the source when the destination hash cannot be read back
docs(regex): explain the capture-group patterns piece by piece
perf(transfer): hash the source during the copy instead of in a second pass
ci: run the form smoke tests on pull requests
chore(deps): bump System.IO.Hashing to 8.0.1
```

The subject is what appears in the changelog and in the release notes. Write it so it reads as a
sentence completing "this commit will…", lower case, no full stop, under 100 characters. Put the
reasoning in the body — the body is where "why" belongs, and it is worth writing.

### Checking before you push

```powershell
npx commitlint --last --verbose      # lint the commit you just made
npm run release:dry                  # what would be released, and as what version
```

## Building

```powershell
dotnet test tests\MSmover.Core.Tests\MSmover.Core.Tests.csproj
powershell -File build\package.ps1 -Version 0.0.0-local
powershell -File build\clean.ps1 -WhatIf     # see what build output has accumulated
```

`package.ps1` produces both the portable executable and the NSIS installer in `release\`, with a
SHA-256 checksum for each. It needs [NSIS](https://nsis.sourceforge.io) on the PATH; without it,
pass `-SkipInstaller` to build the portable executable only.

### Which executable am I running?

A working tree ends up with several copies of the application, because a self-contained publish is
~70 MB and `bin`, `obj`, `publish` and `release` each keep one. `build\clean.ps1` removes them all
(and never touches `%APPDATA%\MSmover`).

Only release builds carry a real version number. Anything built any other way reports
**`0.0.0-dev`** in the title bar and in Settings → About, so a development build cannot be mistaken
for a release in a bug report. If a screenshot says `0.0.0-dev`, it was not built by the release
pipeline.

> [!CAUTION]
> Every copy reads the same `%APPDATA%\MSmover\config.json`. Launching an old build from a `bin`
> folder will happily run your real rules against real data. Check the title bar version before
> assuming which one you started, and keep a rule in dry run while developing.

## Tests

| File | Covers |
|---|---|
| `PathMapperTests` | filename → destination path, and every rejection verdict |
| `StabilityGateTests` | the completion checks, including a simulated acquisition holding a file open |
| `TransferEngineTests` | the safety invariants: corrupted copies, cancellation, pre-existing targets |
| `SymlinkOrderingTests` | link-then-delete ordering, via the `CreateSymlink` seam |
| `SymlinkCleanerTests` | the cleanup tool never deletes anything that is not a reparse point |
| `DocumentationExamplesTests` | every pattern and template printed in the cookbooks |
| `FormSmokeTests` | every window constructs on an STA thread |

Two rules worth keeping:

- **Anything printed in the cookbooks gets a test.** Documentation that drifts from behaviour is
  worse than no documentation, and this is cheap to enforce.
- **Anything that can delete a source file gets a failure-path test**, not just a success one. The
  invariant is that a source file is never deleted unless a verified copy exists at the
  destination; tests exist to make breaking that loud.

## Documentation

The site is [docfx](https://dotnet.github.io/docfx/): hand-written articles under `docs/articles`
and `docs/cookbooks`, plus a reference generated from the `///` comments in `MSmover.Core`.

```powershell
dotnet tool restore
dotnet docfx docs\docfx.json --serve     # http://localhost:8080
```

It deploys to GitHub Pages on every push to `main`, with `--warningsAsErrors`, so a dead link or a
broken cross-reference fails the build.
