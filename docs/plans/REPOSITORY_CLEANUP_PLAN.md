# Repository Cleanup Plan

**Status:** In progress. Phases 0–4 are complete. Phases 5–6 remain.

## Goal

Make the repository easier to navigate, safer to clone and build, and
reproducible to test and publish. Keep runtime behavior unchanged during
structural cleanup. Remove files only after confirming they are unused and
preserve local configuration before changing what is tracked.

## Initial inventory (before cleanup changes)

- The solution contains four projects: the WPF application, its test project,
  the isolated video-player process, and `BenchmarkSuite1`.
- The tracked source is primarily C# (140 files), with a static web client in
  `WebAssets`. `Services`, `RemotePlay.Tests`, and `Helpers` are already
  organized into directories.
- Several application windows and `MainWindow` partial classes remain at the
  repository root. There was no root `README.md` or `docs` directory; `docs/`
  now contains this cleanup plan.
- The root contains a mixture of guides, implementation plans, a backlog,
  build/maintenance scripts, and possible scratch artifacts.
- At baseline, `.gitignore` ignored `remoteplay.json` and `*.pubxml`, while
  the root config was still tracked. Phase 1 anchored the ignore rule to the
  repository root and removed the local config from tracking without deleting
  the working copy.
- `build/Publish-RemotePlay.ps1` refers to named publish profiles that are not
  present in the repository. The SDK consequently warns that the profile was
  not found.
- The working tree was clean during this inventory.

## Principles and safeguards

1. Establish a passing build/test baseline before structural changes.
2. Do not delete a file solely because it is old, empty, duplicated, or
   unreferenced by a text search; check project inclusion, runtime loading,
   scripts, and user workflows first.
3. Do not overwrite or discard local configuration. Replace tracked
   machine-specific configuration with a sanitized example only after
   confirming how defaults are loaded.
4. Move cohesive groups in small batches. Keep namespaces and resource paths
   stable where possible, and test each move before continuing.
5. Keep user-facing documentation and active follow-up plans; archive
   completed plans instead of erasing project history.
6. Keep generated `bin`/`obj` outputs out of Git. Do not clear local caches as
   part of repository cleanup unless separately requested.

## Phased plan

### Phase 0 — Baseline and classification

- [x] Record the current branch and working-tree status before cleanup.
- [x] Build the solution and run the .NET and browser regression tests; record
  exact commands and results below.
- [x] Inventory tracked files by project and source type; identify tracked
  generated outputs, databases, logs, and local configuration for follow-up.
- [x] Classify known candidates as **keep**, **move**, **archive**, or **remove
  after verification**. Do not combine uncertain file deletion with code
  moves.

#### Phase 0 execution record — 2026-10-18

- Branch: `master`, tracking `origin/master`. The only untracked path was
  `docs/`, containing this cleanup plan; no application source was modified.
- Toolchain: .NET SDK `10.0.402`; Deno `2.9.4`. No `global.json` or
  `Directory.Packages.props` is present. The test project uses VSTest with
  xUnit `2.9.2` and `Microsoft.NET.Test.Sdk` `17.12.0`.
- Commands and results:

  | Command | Result |
  | --- | --- |
  | `dotnet build .\RemotePlay.slnx --configuration Release` | Passed; all four solution projects built. Two WFAC010 high-DPI warnings from `app.manifest`. |
  | `dotnet test .\RemotePlay.Tests\RemotePlay.Tests.csproj --configuration Release --no-build --no-restore --verbosity minimal` | Passed: 715 passed, 0 failed, 0 skipped. |
  | `deno test --allow-read=WebAssets/app-live-tv.js .\RemotePlay.Tests\WebAssets\app-live-tv.browser.test.mjs` | Passed: 10 passed, 0 failed. |

- The VS Code test tool separately reported a project-build failure without
  diagnostics. The direct VSTest command above ran the successfully built
  Release test assembly and passed all 715 tests.
- Tracked inventory: 203 files, including 140 C# files, 14 JavaScript files,
  and 10 XAML files across four projects. No compiled executables, DLLs,
  PDBs, or database files were found in the tracked inventory.
- Phase 1 review candidates at baseline:
  - `crash_stderr.txt` was tracked but empty.
  - `remoteplay.json` was tracked despite the `.gitignore` rule.
  - `_patch_xaml.py` had an obsolete absolute path and no repository
    references; `fix_header.csx` was empty.
  - Root `styles.css` differed from `WebAssets/styles.css`; application
    references found during this audit targeted the web asset.
  - `Clear-RplinkContents.ps1` was a maintained utility at the root.
  - There is no root `README.md`; top-level docs mix active plans, completed
    plans, guides, and a suggestions backlog.
  - The publish script references profiles absent from the repository.

Phase 0 baseline is recorded. Phase 1 findings and dispositions are below.

### Phase 1 — Repository-root hygiene

- [x] Removed `_patch_xaml.py` after confirming it targeted an obsolete
  absolute path and had no repository references.
- [x] Removed the empty, unused `fix_header.csx` and `crash_stderr.txt`.
- [x] Removed the unreferenced root `styles.css`; application and test
  references target `WebAssets/styles.css`.
- [x] Moved the maintained `.rplink` utility to
  `scripts/maintenance/Clear-RplinkContents.ps1` and updated its examples.
- [x] Confirmed the root `remoteplay.json` is not a seed file: runtime loads
  configuration only from the user-data path in `AppPaths.ConfigFile` and its
  backup. Added a sanitized `remoteplay.example.json` using current supported
  settings, removed the root local config from Git tracking while preserving
  the working copy, and excluded that local file from project items.

Phase 1 is complete.

Verification:

- `dotnet build .\RemotePlay.slnx --configuration Release --no-restore
  --verbosity minimal` succeeded; it reported one existing WFAC010 high-DPI
  warning.
- `AppConfigServiceTests` and `AppPathsTests`: 14 passed, 0 failed.
- The relocated utility passed a smoke test: `-WhatIf` preserved test files,
  and the normal run cleared files at the root and in a hidden subfolder.
- `git diff --check` passed.

### Phase 2 — Documentation structure

- [x] Add a root `README.md` covering purpose, supported environment,
  prerequisites, build/test/run/publish commands, and links to deeper docs.
- [x] Create `docs/` sections such as `user-guide/`, `development/`,
  `testing/`, `plans/`, and `archive/`; choose the smallest structure that
  fits the existing documents.
- [x] Move the certificate-help guide and video-player testing guide into
  appropriate documentation sections, updating relative links.
- [x] Classify each top-level plan before moving it. Preserve active work:
  `VIDEO_LOCAL_PLAYBACK_PLAN.md` still records device-validation follow-ups.
  Archive plans only after their remaining checks and status are reviewed.
- [x] Separate the completed entries in `SUGGESTIONS.md` from the active
  backlog, or replace it with the project's chosen issue-tracking workflow.

#### Phase 2 execution record — 2026-10-18

- Added the root `README.md` with the supported Windows x64 environment,
  prerequisites, build/test/run/publish commands, and links to the guides and
  plan areas.
- Created `docs/user-guide/`, `docs/testing/`, `docs/plans/`, and
  `docs/archive/`. No `docs/development/` section was needed: setup and
  developer commands are covered by the README, and there was no separate
  development guide to move.
- Moved the certificate-help and isolated-player testing guides into their
  respective sections.
- Reviewed plan statuses and outstanding checks. `LIVE_TV_IMPROVEMENT_PLAN.md`
  remains active for real-provider timing and playback validation;
  `VIDEO_LOCAL_PLAYBACK_PLAN.md` remains active for progressive HTTP tests and
  target-device/browser validation; this cleanup plan and the pending
  suggestions remain active.
- Archived `PODCASTS_IMPLEMENTATION_PLAN.md` after its implementation phases
  and documented acceptance checks were complete; its note that production
  timing comparisons remain qualitative is preserved. Archived
  `WEB_PLAYER_UI_PLAN.md` after its eight verified phases, retaining its
  browser-automation limitations.
- Split the five completed suggestion reports (#1, #3, #4, #6, and #10) into
  `docs/archive/COMPLETED_SUGGESTIONS.md`; the five pending suggestions remain
  in `docs/plans/SUGGESTIONS.md`.
- Updated source-file and cross-plan links for the new locations. A local-link
  check resolved all 46 Markdown links across the README and documentation;
  staged and unstaged `git diff --check` both passed. No build or test run was
  needed for these documentation-only changes.

### Phase 3 — Application source layout

- [x] Keep application entry-point and project metadata at the root.
- [x] Move the `MainWindow` XAML/code-behind and its partial classes together
  into a dedicated UI/window area. Group `DarkMessageBox`, `FindLinksDialog`,
  `RenameDialog`, `SplashWindow`, and `VideoTextOverlay` by their actual UI
  roles.
- [x] Before each move, check `x:Class`, `StartupUri`, resource URIs,
  `Application` resources, code-behind namespaces, and project compile/page
  items. Preserve namespaces unless a namespace change is explicitly part of
  the batch.
- [x] Keep the isolated `RemotePlay.VideoPlayer` project and
  `RemotePlay.Tests` project clearly separated. Retain `BenchmarkSuite1` if
  it is still used; otherwise document its disposition before removal.
- [x] Review `Services`, `Models`, `Helpers`, `Abstractions`, `Controls`,
  `ViewModels`, `Infrastructure`, and `DependencyInjection` for inconsistent
  placement. Move only files whose responsibility and namespace clearly fit
  another existing area.
- [x] Keep `WebAssets` together unless the server asset loader, service
  worker, and browser tests are updated in the same change. Its filenames
  and paths are runtime contracts.

#### Phase 3 execution record — 2026-10-18

- Kept `App.xaml`, `App.xaml.cs`, `AssemblyInfo.cs`, `RemotePlay.csproj`, and
  `RemotePlay.slnx` at the root. Moved the main window and its four partials,
  plus `SplashWindow`, to `UI/Windows/`; moved the three dialogs to
  `UI/Dialogs/`; and moved `VideoTextOverlay` to `UI/Overlays/`.
- Preserved all `RemotePlay` code-behind namespaces and XAML `x:Class` values.
  The main app has no `StartupUri`; `App.xaml.cs` creates `SplashWindow` and
  resolves `MainWindow` through dependency injection. Checked resource URIs
  and project item declarations; the SDK default compile/page globs cover the
  new locations, and no moved resource URI needed updating.
- Updated source-path references in the regression tests, testing guide, and
  archived suggestion notes. Kept `WebAssets` unchanged.
- Reviewed the existing source-folder boundaries; no other file clearly
  warranted a move. Retained `BenchmarkSuite1`, which remains a solution
  project with BenchmarkDotNet benchmarks.
- Verified the Release solution build across all four projects and the full
  .NET test suite (719 passed, 0 failed).

### Phase 4 — Build, repository, and developer tooling

- [x] Make publishing reproducible without a machine-local `.pubxml`: either
  provide safe, documented profiles or pass the intended configuration,
  runtime identifier, deployment mode, and output directory explicitly from
  the publish script.
- [x] Make the publish script fail on a nonzero `dotnet publish` exit code and
  verify the expected application, isolated-player, and version outputs.

#### Phase 4 publishing verification — 2026-10-18

- `build/Publish-RemotePlay.ps1` now maps `PortableWinX64` and
  `SelfContainedWinX64` to explicit Release, `win-x64`, deployment-mode, and
  output-directory settings. It cleans the prior generated Release outputs,
  checks the publish exit code, and verifies the application, isolated-player,
  LibVLC, and version files.
- Both application projects configure `VideoLAN.LibVLC.Windows` for x64 only;
  its default `AnyCPU` settings otherwise copy x86, x64, and ARM64 runtimes.
- Verified `.\build\Publish-RemotePlay.ps1 -Profile PortableWinX64` with version
  `4.0.1`: each of the two LibVLC runtime directories contains only `win-x64`
  (425 files, 100.9 MB), and the expected executables and version file exist.

- [x] Review `.gitignore` against the actual project layout. Keep generated
  artifacts ignored, and ensure local configuration, logs, and test output
  cannot be accidentally added.
- [x] Add a concise `.editorconfig` and choose a consistent formatter/linter
  workflow only after checking compatibility with the current C# and web
  code.
- [x] Review floating package versions (currently including `NAudio` and
  `TagLibSharp`) for reproducible restores. Pin or centralize them only after
  confirming the resolved versions and running the full test suite.
- [x] Decide whether to add a minimal CI workflow for restore, build, .NET
  tests, and browser regression tests; no tracked GitHub Actions workflow was
  found in the initial inventory.
- [x] Update VS Code tasks to expose the verified build, test, and publish
  commands if that improves the normal developer workflow.

#### Phase 4 developer tooling verification — 2026-10-18

- Audited `.gitignore` against generated output and confirmed its existing
  coverage for `bin/`, `obj/`, test-result directories, logs, and the
  machine-local `remoteplay.json`. Added ignore rules for standalone `.trx`
  reports, `.dmp` crash dumps, and `crash_stderr.txt`. `git check-ignore`
  confirmed representative paths are ignored; none of those local artifacts
  are tracked.
- `dotnet list package` resolved `NAudio` to `2.4.0` and `TagLibSharp` to
  `2.3.0`; pinned those exact versions in `RemotePlay.csproj`. The solution
  restore and full .NET test suite passed after pinning.
- Added `.editorconfig` defaults for C#, web/config, XML/XAML, and Markdown.
  Compatibility checks found existing baseline issues: `dotnet format
  whitespace --verify-no-changes` reported 1,735 whitespace diagnostics,
  `deno fmt --check` reported 20 of 22 files not formatted, and `deno lint`
  reported 609 findings. The chosen tools are `dotnet format whitespace` for
  C# and Deno's `fmt`/`lint` for web code, but repository-wide format/lint gates
  are deferred until the existing baseline can be cleaned without mixing broad
  formatting changes into this phase.
- Added `.github/workflows/ci.yml` for Windows restore, Release build, .NET
  tests, and browser regression tests on pushes to `master`, pull requests, and
  manual dispatch. The corresponding commands passed locally; the hosted
  workflow will run after it is pushed.
- Expanded `.vscode/tasks.json` with Release build, both test suites, and
  portable and self-contained x64 publish tasks. Parsed the task file and ran
  the build and test tasks successfully. The publish script and portable
  profile were already verified above.
- Verification: solution restore succeeded; the Release build succeeded with
  the existing `WFAC010` high-DPI manifest warning; all 719 .NET tests and all
  10 browser regression tests passed; ignore checks and `git diff --check`
  passed.

### Phase 5 — Dead-code and asset audit

- [ ] Search for unused C# types, XAML resources, JavaScript modules, CSS
  selectors, and bundled assets. Check reflection, dynamic loading, routes,
  service-worker caching, and test fixtures before removal.
- [ ] Remove unused code/assets in separate, reviewable batches. Preserve
  movie-card click/play behavior, grid/list behavior, music queue behavior,
  podcast discovery, and video playback/control paths.
- [ ] Keep third-party and geographic data assets when referenced; document
  their source and purpose rather than removing them based only on size.
- [ ] Re-run focused tests after each removal batch so regressions can be
  attributed to a specific change.

### Phase 6 — Final verification and repository review

- [ ] From a clean checkout, restore and build the solution in Release.
- [ ] Run all .NET tests and browser regression tests; resolve failures caused
  by cleanup without broadening scope to unrelated behavior changes.
- [ ] Publish using the documented command and verify the runtime identifier,
  version metadata, web assets, native dependencies, and isolated-player
  executable.
- [ ] Check documentation links, formatting, project references, XAML/resource
  loading, and `git diff --check`.
- [ ] Confirm the final root contains only the solution/project entry points,
  repository essentials, and concise onboarding documentation.
- [ ] Confirm no unapproved local configuration, generated output, crash log,
  scratch tool, or obsolete plan remains tracked.

## Decisions and scope

- Completed implementation plans are preserved under `docs/archive/`.
  Plans with outstanding manual, provider, or target-device checks stay under
  `docs/plans/`.
- Phase 3 file moves preserve existing namespaces; broad namespace cleanup is
  outside the scope of those moves.
- Phase 4 includes `.editorconfig`, exact pins for the resolved floating
  packages, Windows CI, and VS Code tasks. Repository-wide format/lint gates
  remain deferred until the existing baseline can be normalized safely.

## Definition of done

- The repository has an obvious entry point, a navigable source/documentation
  layout, and reproducible build/test/publish instructions.
- No empty or one-off artifacts remain tracked without a documented purpose.
- Local configuration is preserved locally and represented safely in the
  repository.
- File moves and removals introduce no runtime, packaging, or test regressions.
- A clean checkout builds, tests, and publishes using the documented workflow.
