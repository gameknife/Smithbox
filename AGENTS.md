# Repository Guidelines

## Project Structure & Module Organization
`Smithbox.sln` is the root solution. Main app entry points live in `src/Smithbox` and `src/Smithbox.Program`. Shared/runtime assets and native dependencies are in `src/Smithbox.Data`. Supporting libraries are vendored under `src/Andre`, `src/Havok`, and `src/Veldrid`. Tests are in `src/Smithbox.Tests`. Notes and release docs are in `Documentation`.

## Build, Test, and Development Commands
- `dotnet restore /p:Configuration=Release-win` restores NuGet packages using the CI configuration.
- `dotnet build Smithbox.sln --configuration Release-win --no-restore` builds the full solution.
- `dotnet run --project src/Smithbox/Smithbox.csproj --configuration Debug-win` launches the desktop app locally.
- `dotnet test src/Smithbox.Tests/Smithbox.Tests.csproj --configuration Debug-win` runs unit tests.
- `dotnet test src/Smithbox.Tests/Smithbox.Tests.csproj --collect:"XPlat Code Coverage"` collects coverage via `coverlet.collector`.
- `dotnet publish src/Smithbox/Smithbox.csproj --configuration Release-win -o deploy` produces a distributable build.
- `git submodule update --init --recursive` initializes external submodules when needed.

## Coding Style & Naming Conventions
Follow `.editorconfig`: 4-space indentation, CRLF line endings, braces required, and block-scoped namespaces. Prefer explicit types over `var` unless type is obvious. Keep `using` directives outside namespaces. Use PascalCase for types/methods/properties and prefix interfaces with `I`.

## Testing Guidelines
Testing uses xUnit with `Moq`; coverage is collected with Coverlet. Place tests in `src/Smithbox.Tests` and name files/classes by subject (for example, `ProjectManagerTests`). Use descriptive method names such as `SetupFolders_CreatesRequiredDirectories`. Add or update tests for bug fixes and new behavior; no strict coverage threshold is enforced in CI.

## Commit & Pull Request Guidelines
Recent history favors short, scope-first subjects (for example, `Map Editor - Duplicate Action`, `Entity`). Use imperative, focused commit messages and avoid vague subjects like `Commit`. For pull requests, include:
- a concise change summary and affected modules,
- linked issue(s) when applicable,
- local validation steps/command output,
- screenshots or clips for UI/editor-facing changes.

## Security & Configuration Tips
Do not commit personal paths, extracted game files, or secrets. Keep large generated artifacts out of source control unless explicitly required for release packaging.
