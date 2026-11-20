# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Screenbox is a UWP media player for Windows 10 1903+/Windows 11/Xbox, built on LibVLCSharp, CommunityToolkit.Mvvm, and .NET (`net11.0-windows10.0.26100.0`, set in `Directory.Build.props`). Canonical agent guidelines live in `.github/copilot-instructions.md`, `.github/instructions/dotnet-uwp.instructions.md` (applies to `**/*.cs`), and `.github/instructions/uwp-xaml.instructions.md` (applies to `**/*.xaml`). Read the relevant one before editing C# or XAML. `docs/PROJECT_STRUCTURE.md` has the full architecture write-up.

## Build & test

**Never use `dotnet build`.** UWP/CsWinRT tooling requires MSBuild from Visual Studio 2026 (v18). Locate it with:

```pwsh
$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -version "[18.0,19.0)" -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe
# fallback: "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
```

```pwsh
dotnet tool restore                                                                  # XAML Styler + LottieGen
& $msbuild Screenbox.slnx -t:restore -p:Platform=x64 -p:Configuration=Debug          # restore solution
& $msbuild Screenbox.Core/Screenbox.Core.csproj /t:Build /p:Configuration=Debug /p:Platform=x64 /m
& $msbuild Screenbox/Screenbox.csproj /t:Build /p:Configuration=Debug /p:Platform=x64 /m   # app (/t:Restore first if needed)
```

Platforms are `x86`, `x64`, `arm64` only — no AnyCPU.

**Tests** (`Screenbox.Core.Tests`, TUnit, runs as an exe):

```pwsh
dotnet run --project Screenbox.Core.Tests/Screenbox.Core.Tests.csproj                # full suite
dotnet run --project Screenbox.Core.Tests/Screenbox.Core.Tests.csproj -- --treenode-filter "/*/*/QueueServiceTests/*"   # one class
dotnet run --project Screenbox.Core.Tests/Screenbox.Core.Tests.csproj -- --treenode-filter "/*/*/QueueServiceTests/MethodName"  # one test
```

CI builds with `msbuild Screenbox.Core.Tests\Screenbox.Core.Tests.csproj -p:Configuration=Release -p:Platform=x64 -restore` and runs the resulting `Screenbox.Core.Tests.exe`.

**XAML lint** (CI-enforced): `pwsh ./scripts/lint-xaml.ps1` runs XAML Styler over every `.xaml` and fails on any `git diff`. Format a file with `dotnet tool run xstyler -f <path>.xaml`. Run it on every XAML file you touch.

**Assets**: icon fonts from `assets/fonts/` → `pwsh ./scripts/Generate-Fonts.ps1 -Publish`; Lottie JSON in `assets/animations/` → `pwsh ./scripts/ConvertFrom-Lottie.ps1` (generates C# into `Screenbox/Assets/Animations/`).

## Architecture

Projects: `Screenbox` (UWP app: pages, controls, dialogs, localization, DI wiring in `App.xaml.cs`), `Screenbox.Core` (view models, services, contexts, coordinators, playback, models — no XAML), `Screenbox.UI` (shared templated controls/theme resources), `Screenbox.VideoView` (video surface), `Screenbox.Lively` (Lively Wallpaper integration), `Screenbox.Core.Tests`.

Core DI registrations are in `Screenbox.Core/Common/ServiceHelpers.cs` (`PopulateCoreServices`); app-only services (telemetry, navigation, dialogs) are added in `Screenbox/App.xaml.cs`. `NavigationService` maps ViewModel types to Page types there.

Layer roles and allowed dependencies (see `docs/PROJECT_STRUCTURE.md` → Architecture Rules):

- **Views** (XAML + minimal code-behind) → ViewModels, Contexts (read-only). `Ioc.Default.GetRequiredService<T>()` only allowed in code-behind to resolve a page's ViewModel.
- **ViewModels** (`ObservableObject`/`ObservableRecipient`, `[ObservableProperty] partial` props, `[RelayCommand]`) → Services (by interface), Contexts, Factories. Talk to other VMs via `Messenger` (types in `Screenbox.Core/Messages/`), not direct references, except parent/child.
- **Contexts** (`Screenbox.Core/Contexts/`) — singleton observable state holders, single source of truth (e.g. `PlayQueueContext`, `LibraryContext`, `PlayerContext`, settings contexts). Written by services/coordinators, observed by VMs; must never call services or coordinators.
- **Coordinators** (`Screenbox.Core/Coordinators/`) — stateful singletons owning watchers/timers/subscriptions, implement `IDisposable`, exposed via interface. `PlayQueueCoordinator` is the only writer of `PlayQueueContext` and handles `PlayMediaMessage`/`SetQueueMessage`/`ClearQueueMessage`/`QueueRequestMessage`. `LibraryCoordinator` drives library scans and pushes incremental `MusicLibrary`/`VideosLibrary` snapshots into `LibraryContext` via `IProgress<T>`.
- **Services** (`Screenbox.Core/Services/`) — stateless; return data instead of writing Contexts. Always registered against an interface.
- **Playback** (`Screenbox.Core/Playback/`) — `IMediaPlayer`/`IPlaybackItem` abstractions over LibVLC (`VlcMediaPlayer`, `VlcPlaybackItem`, track/chapter lists).

`MediaViewModel`, `AlbumViewModel`, `ArtistViewModel` are deliberately *observable domain entities* used across all layers (contexts, services, search results); they must not reference XAML types. `MusicLibrary`/`VideosLibrary` are immutable snapshots replaced wholesale on refresh.

### Persistence (SQLite `screenbox.db` in LocalFolder, `DatabaseService.*.cs`)

- `library_folders`, `media_records`, `playback_progress` = rebuildable cache. For library refreshes, prefer replacing rows for the affected media type over incremental stale-row cleanup.
- `playlists`, `playlist_items` = **durable user data**. Preserve them in schema changes/recovery; prefer per-table recovery/migration over recreating the DB file.
- JSON uses source-generated `CoreJsonContext` (`Screenbox.Core/Models/Serialization/`). Register new DTOs with `[JsonSerializable]` there; never use reflection-based `JsonSerializer` (projects are AOT/trim-analyzed: `IsAotCompatible`, `DisableRuntimeMarshalling`).

### Testing constraints

- Tests run on plain .NET without an app package, so `ApplicationData.Current` etc. are unavailable. Keep such WinRT calls in small separate methods that tests can bypass (e.g. `DatabaseService.GetUwpFolderPath()` only runs when `DbFolderPath` is empty).
- Database tests set `DatabaseService.DbFolderPath` to a temp directory (`Helpers/TestDirectoryFixture.cs`). Test doubles live in `Screenbox.Core.Tests/Helpers/`.
- Anything instantiating XAML types can't run in this project — it needs a UWP Unit Test App with `[UITestMethod]`.
- No `// Arrange / Act / Assert` comments; match nearby test naming (`Method_Condition_Result`).

## Conventions

- C# latest (`LangVersion latest`), nullable enabled; use `is null`/`is not null`. File-scoped namespaces, `_camelCase` private fields, XML doc comments on public APIs, `nameof` over string literals, pattern matching/switch expressions. Follow `.editorconfig`.
- XAML: `{x:Bind}` with explicit `Mode`, `x:DataType` on templates, `{ThemeResource}` for theme-aware colors, `{StaticResource}` for value types; prefer `x:Bind` functions over new converters unless reusable.
- Localization: all user-facing strings go in `Screenbox/Strings/en-US/*.resw` only (other locales come from Crowdin). Access via ReswPlus-generated strongly-typed classes **from the View layer only — never from ViewModels**.
- Git: Conventional Commits for commits and PR titles; PR descriptions cover context, motivation, impact and link issues. With `gh` on Windows always pass `--body-file`, never inline `--body`.
