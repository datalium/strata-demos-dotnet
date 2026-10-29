# CLAUDE.md

Guidance for Claude Code when working in this repository.

## What this repo is

.NET demo suite for [Datalium Strata](https://github.com/datalium/strata) — runnable examples of
the framework, numbered in rough order of complexity (`demos/01-...`
simplest, higher numbers closer to real-world). See the root
[README.md](README.md) for prerequisites and wiring, and
[demos/README.md](demos/README.md) for the current list.

The demos build from Strata's **published NuGet packages** (`Datalium.Strata`,
`.Storage`, `.AI`, `.Hosting`), not from a source checkout. Versions are set
once, in `Directory.Packages.props` (central package management; Strata's
four share `$(StrataVersion)`), and a demo's `.csproj` has versionless
`<PackageReference>`s. `nuget.config` restores from nuget.org only. The demos
target Strata **0.9** (`0.9.0-preview.1`); `CHANGELOG.md` in
[`datalium/strata`](https://github.com/datalium/strata) says what changed
between versions. The `strata` CLI (demo 14) is a GitHub Release binary of
`datalium/strata`, found through `STRATA_BIN` or the `PATH`.

The Rust twin is the sibling repo `strata-demos-rust` (demos 01–29,
on 0.9). Demos 09 and 12 hand a thread to it, so
don't change what crosses the wire there (thread ids, state field names,
default paths, mode names) without changing the twin too.

## Conventions to follow

- **Verify the actual API before writing demo code.** The design docs in
  `datalium/strata` (`design/`, `CLAUDE.md`) describe intent; the real type
  names, signatures, and error-handling shape (e.g. `Compile()` throws rather
  than returning a `Result`) can differ or have moved on. Read the relevant
  source in `dotnet/src/Datalium.Strata` (and `.Storage`, `.SourceGenerators`)
  of [`datalium/strata`](https://github.com/datalium/strata) at the tag the
  demos use (`v0.9.0-preview.1`), and prefer an existing working example
  (`examples/dotnet-agent` there) over inferring from prose. What the demos
  compile against is the published package, not `main`.
- **Every demo must actually build and run before it's considered done.**
  `dotnet run --project demos/<demo>/<demo>.csproj` and confirm the real
  output, plus `dotnet build Strata.Demos.slnx --configuration Release` for
  the whole solution — not just the new demo in isolation.
- **A demo needs a purpose beyond "it compiles."** Each one should
  demonstrate something specific about the framework (a spec number, an API
  surface, a gotcha) — not just restate the previous demo with different
  names. If a demo would be trivial or redundant, say so rather than padding
  the list.
- **No live LLM calls, except by explicit request.** Strata's own reference
  example (`examples/dotnet-agent`) calls no real LLM provider — Strata is
  orchestration infrastructure (graph, tool dispatch, streaming,
  checkpoints, history), and the "intelligence" is bring-your-own. Keep demo
  tool/node logic deterministic (string matching, arithmetic, etc.) so demos
  run offline with no API keys, unless the user explicitly asks to wire in a
  real provider — see [`03-llm-tool-calling`](demos/03-llm-tool-calling) and
  [`06-token-streaming`](demos/06-token-streaming), the two demos that do
  (an OpenAI `IChatClient` from `Microsoft.Extensions.AI.OpenAI` passed to
  `Datalium.Strata.AI`, gated on `OPENAI_API_KEY`). Keep that pattern
  contained to demos that exist specifically to show it off; don't let it
  creep into otherwise-offline demos. A demo about `Datalium.Strata.AI`
  itself (agents, model routes, memory) uses a small scripted `IChatClient`
  or embedding generator written in the demo, and its README says so.
- **Each demo gets its own `README.md`** (see
  `demos/01-hello-agent/README.md` for the shape): what it demonstrates, what
  it deliberately doesn't cover, how to run it, expected output. Also add a
  row to `demos/README.md`'s table and a `<Project Path="..." />` entry under
  the `/demos/` folder in `Strata.Demos.slnx`.
- **`[StrataTool]`-annotated methods need only `Datalium.Strata`.** The
  source generator ships inside that package as an analyzer, so a direct
  `<PackageReference Include="Datalium.Strata" />` is enough. Don't add a
  separate generator reference. (Analyzers don't flow to a project through
  another project's reference, so each demo with tools references
  `Datalium.Strata` itself.)
- **A new package version goes in `Directory.Packages.props`**, never in a
  demo's `.csproj`. To move every demo to another Strata release, change
  `StrataVersion` there.
- **Ask before scope decisions that aren't purely mechanical** — e.g. whether
  a new demo should introduce a new external dependency, call a live service,
  or otherwise expand beyond what a "runnable, offline, self-contained demo"
  implies. Don't assume.

## Commands

```bash
dotnet restore Strata.Demos.slnx
dotnet build Strata.Demos.slnx --configuration Release --no-restore
dotnet run --project demos/<demo-folder>/<demo>.csproj
dotnet format Strata.Demos.slnx --verify-no-changes
dotnet test Strata.Demos.slnx --configuration Release --no-build
```
