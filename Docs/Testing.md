# Testing

Vertex has no mocks of the world. The tests that matter run against a **real Paper 1.21.5 server** started locally (offline mode, bound to 127.0.0.1, killed with the
test host) and, where the language model is involved, the **real model on the GPU**. A skill is not done until a live test has failed without it and passed with it.

## Running them

```
dotnet build Vertex.Tests/Vertex.Tests.csproj -c Release -o /tmp/vt           # build to its own folder: Vertex.exe locks the normal output
dotnet test /tmp/vt/Vertex.Tests.dll                                           # everything that is not Explicit
dotnet test /tmp/vt/Vertex.Tests.dll --filter "TestCategory!=GPU&Name!~Live"   # unit tests only (seconds)
dotnet test /tmp/vt/Vertex.Tests.dll --filter "Name~LiveBuildTests"            # one live suite
```

| Kind | Where | Needs |
|---|---|---|
| Unit tests | `VertexUnitTests`, `BuildScriptTests`, `BuildShapesTests`, `BallisticsTests`, `InstinctTests`, `IdlePolicyTests`, ... | nothing |
| Live (real Paper) | `Live*Tests` (trading, building, farming, animals, fishing, fighting, shield, mining, gathering, voice, companion) | Java is downloaded and cached next to the AubsCraft tests; the Simple Voice Chat plugin jar (found from `VERTEX_VOICECHAT_JAR`, the AubsCraft repo, or the AubsCraft test servers) for the voice test |
| GPU (`[Category("GPU")]`) | `ToolChoiceTests`, `BrainEndToEndTests`, `CompanionTests`, the speech tests | the GPU to itself, the models in the Ollama cache |
| Explicit | `DecodeSpeedTests`, `DevServerScenarioTests` | run by name; the second needs the AubsCraft dev server on localhost (`VERTEX_DEV_RCON_PASSWORD`) |

Live tests start their own server on **a unique port per suite** (25932 voice, 25952 trading, 25954 building, 25956 farming, 25958 animals, 25960 tool choice, 25962
companion, 25964 fighting, 25966 mining, 25968 shield, 25970 fishing, 25972 gathering, 25974 end to end): two suites never share one, but do not run the same suite twice at once.

Things that have cost time:

- **Minecraft player names are at most 16 characters.** A longer name is kicked silently ("disconnected before spawning") and the test fails with no clue.
- **Do not run anything else on the GPU or CPU during a GPU test.** A model run while another test runs shows 90-second timeouts that are not failures of the code.
- **A GPU test that hangs can leave its Paper server running** and holding its log file; the next run then fails to start. Look for a stray `java.exe` on the suite's port.
- **Do not kill `java.exe` blindly:** other projects (the AubsCraft dev server) run theirs.

## The tool-choice test

`ToolChoiceTests` asks the real model 18 things a child might say and checks the first tool it calls and what its arguments contain (the test stops each task as soon as it
starts: it tests the choice, not the doing). It is the measure behind most of [Architecture.md](Architecture.md): run it alone, it takes about four minutes.

| Switch | Effect |
|---|---|
| `VERTEX_TEST_MODEL=qwen3:8b` | the model to use (a prefix of an Ollama model name; default `llama3.1`) |
| `VERTEX_TEST_INSTINCT=0` | no instinct hint (the A/B) |
| `VERTEX_FULL_TOOLS=1` | full tool definitions instead of the compact ones |
| `VERTEX_FAST_KERNELS=0` or `VERTEX_NO_FK=warp,dp4a,grouped,lastpos` | the library's fast kernels off, all or some |
| `GGUF_ATTN_SPLITKV=0`, `GGUF_GEMM_FAST=0` | the split-KV decode kernel / the fast prefill GEMM off (read by `SpawnDev.ILGPU.ML`) |
| `ML_PER_OP_SYNC=1` | a sync after every graph op: true per-op GPU timing |

The test logs a line for each client the server disconnects, since a dropped test client looks exactly like a model that went silent.

## Benchmarks

`DecodeSpeedTests` (Explicit, GPU): decode speed by sampling setting; where a decode step and a prefill spend their time (true per-op timing); and the cost of a
long context (first read, then a second turn on the same prefix, then a third). Use it before and after any change to the language-model path.

## The dev server scenarios

`DevServerScenarioTests` run Vertex against a local stock Paper server another tool keeps for the viewer (a cabin, and a field with a fenced pen), then check what the
**server** says about the blocks and entities over RCON (`execute if block ...`, entity counts), so the check is the server's world, not what Vertex believes. They set the
server to peaceful difficulty while they run and put it back.
