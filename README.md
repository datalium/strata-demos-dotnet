# Datalium Strata Demos - .NET

.NET demo suite for Datalium Strata — runnable agentic AI examples from minimal to real-world, built from the published `Datalium.Strata` **0.9** packages (`0.9.0-preview.1`) on [nuget.org](https://www.nuget.org/packages/Datalium.Strata). Strata's code and design docs live in [`datalium/strata`](https://github.com/datalium/strata); its docs site is [stratamesh.dev](https://stratamesh.dev).

## Prerequisites

- [.NET SDK 10](https://dotnet.microsoft.com/download) or later
- The `strata` CLI (optional) — only [demo 14](demos/14-graph-ci-gate) needs it, and demos [09](demos/09-cross-language-interop) and [12](demos/12-pluggable-storage) show it reading their checkpoints. It is a Rust binary, not a NuGet package: download `strata-linux-x64`, `strata-macos-x64` or `strata-windows-x64.exe` from the [`v0.9.0-preview.1` release](https://github.com/datalium/strata/releases/tag/v0.9.0-preview.1) (check it against `SHA256SUMS.txt`), and put it on your `PATH` as `strata` or point `STRATA_BIN` at it
- [Rust](https://rustup.rs) (optional) — only for the Rust halves of the hand-offs in demos [09](demos/09-cross-language-interop) and [12](demos/12-pluggable-storage), which live in the Rust twin repo
- [Docker](https://docs.docker.com/get-docker/) (optional) — only [demo 12](demos/12-pluggable-storage) needs it, for a throwaway Postgres and Redis (`docker compose up -d --wait`, see [`docker-compose.yml`](docker-compose.yml)); every other demo runs with no external services
- An `OPENAI_API_KEY` (optional) — only demos [03](demos/03-llm-tool-calling) and [06](demos/06-token-streaming) call a real model. They can use a free local model instead, such as Ollama, with `OPENAI_BASE_URL` and no key (see their READMEs)

Strata comes from NuGet, so this repo is all you need to clone:

```bash
git clone https://github.com/datalium/strata-demos-dotnet.git
cd strata-demos-dotnet
dotnet build Strata.Demos.slnx
```

## Structure

```
strata-demos-dotnet/
├── Strata.Demos.slnx         # solution
├── Directory.Packages.props  # every package version, Strata's included (central package management)
├── nuget.config              # restore from nuget.org only
└── demos/
    ├── 01-hello-agent/
    ├── 02-tool-calling/
    └── ...
```

Demos are numbered in rough order of complexity (`01-` simplest, higher numbers more advanced / closer to real-world). `01`–`14` cover the core engine (graphs, tools, streaming, interrupts, validation, recovery, storage, parallel groups, subgraphs, the CLI); `15`–`29` cover what Strata gained on the way to 0.9 (async nodes and cancellation, loop limits, joins and maps, waits and timers, durable execution, leases, graph identity, agents, model routes, long-term memory, the tool registry, observability, hosting, the scheduler). See [demos/README.md](demos/README.md) for the current list.

**Note on LLMs:** Strata is orchestration infrastructure — the graph engine, tool-calling dispatch, streaming, checkpoints, history — and its `Datalium.Strata.AI` package runs model calls and agents over any `Microsoft.Extensions.AI` `IChatClient` you give it. Every demo runs offline with no API keys, except [`03-llm-tool-calling`](demos/03-llm-tool-calling) and [`06-token-streaming`](demos/06-token-streaming), which wire a real OpenAI client into `Datalium.Strata.AI` (an agent deciding which tool to call, and a model answer streamed token by token) and need `OPENAI_API_KEY` set, or `OPENAI_BASE_URL` pointing at a local OpenAI-compatible server such as Ollama. The other AI demos (`23`–`25`) use small scripted stand-ins for the model and the embedder, written in the demo, so they are deterministic.

## Dependencies on Strata

The demos use Strata's published NuGet packages: `Datalium.Strata`, `Datalium.Strata.Storage`, `Datalium.Strata.AI` and `Datalium.Strata.Hosting`. The repo uses [central package management](https://learn.microsoft.com/nuget/consume-packages/central-package-management): every version is set once, in [`Directory.Packages.props`](Directory.Packages.props), and Strata's four share one property:

```xml
<StrataVersion>0.9.0-preview.1</StrataVersion>
```

A demo's `.csproj` names the packages it needs, without a version:

```xml
<PackageReference Include="Datalium.Strata" />
<PackageReference Include="Datalium.Strata.Storage" />
```

The `[StrataTool]` source generator ships inside the `Datalium.Strata` package, so a demo with tool methods (e.g. [demo 02](demos/02-tool-calling/02-tool-calling.csproj)) needs no other reference. [`nuget.config`](nuget.config) restores from nuget.org only, so the build doesn't depend on any feed configured on your machine.

## Building and running

```bash
dotnet restore Strata.Demos.slnx
dotnet build Strata.Demos.slnx --configuration Release --no-restore
dotnet run --project demos/01-hello-agent/01-hello-agent.csproj
dotnet format Strata.Demos.slnx --verify-no-changes
dotnet test Strata.Demos.slnx --configuration Release --no-build
```

## Adding a new demo

1. Create the project under `demos/NN-demo-name/`
2. Add a `<Project Path="demos/NN-demo-name/NN-demo-name.csproj" />` entry to the `/demos/` folder in `Strata.Demos.slnx`
3. Add a versionless `<PackageReference Include="Datalium.Strata" />` (plus `.Storage`, `.AI` or `.Hosting` as needed); a package not yet used gets a `<PackageVersion>` in `Directory.Packages.props` (see [Dependencies on Strata](#dependencies-on-strata))
4. Add an entry to [demos/README.md](demos/README.md)

## Related

- [datalium/strata](https://github.com/datalium/strata) — the core framework (code, design docs, `CHANGELOG.md`, the `strata` CLI)
- [strata-demos-rust](https://github.com/datalium/strata-demos-rust) — the Rust twin of this repo: demos `01`–`29`, on 0.9, same numbers and stories
