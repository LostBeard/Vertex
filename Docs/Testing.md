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
| GPU release (`GpuReleaseTests`, `[Category("GPU")]`) | Stop keeps the models for a grace period, then the card's own memory counter (nvidia-smi) goes back to idle; a Start inside the grace period is quick and cancels the release | the GPU to itself; measured 2026-11-01 on an RTX 2060: 619 MiB idle, 11,285 MiB with Vertex on, 619 MiB after the release |
| Connection and chat safety (real Paper) | `LiveSlashSafetyTests` (what Vertex says is chat, never a server command, even from an operator account), `LiveDisconnectTraceTests` (a connection that ends says how it looked), `QuitCommandTests` (`Vertex.exe quit` against a real second instance) | Java as for the other live suites; ports 25976 and 25978 |
| Rejoin (`HostRejoinTests`, `[Category("GPU")]`; `RejoinPolicyTests` is plain) | a real server goes away under a running Vertex, comes back, and he rejoins by himself; a Leave while he waits keeps him out; which disconnect reasons are worth retrying | the GPU to itself (~4 min, port 25980); stop a running Vertex first (`Vertex.exe quit`, and wait: it needs ~30 s to stop properly) |
| Model install (`[Category("Models")]`) | `ModelSetupTests`: Vertex installs its own language model, in the library and through the dashboard host (progress, stop and resume, a clear error) | the internet; they install `all-minilm:22m` (about 46 MB) into a throwaway cache, from the real hosts (models.spawndev.com does not have it, so they also prove the fall-back to registry.ollama.ai) |
| Explicit | `DecodeSpeedTests`, `DevServerScenarioTests`, `ModelSetupTests.TheDefaultModel_InstallsFromModelsSpawndevCom_Alone` | run by name; the second needs the AubsCraft dev server on localhost (`VERTEX_DEV_RCON_PASSWORD`); the last downloads the 4.9 GB default model from models.spawndev.com alone and needs about 6 GB free in the temp folder |

Live tests start their own server on **a unique port per suite** (25932 voice, 25952 trading, 25954 building, 25956 farming, 25958 animals, 25960 tool choice, 25962
companion, 25964 fighting, 25966 mining, 25968 shield, 25970 fishing, 25972 gathering, 25974 end to end): two suites never share one, but do not run the same suite twice at once.

Things that have cost time:

- **Minecraft player names are at most 16 characters.** A longer name is kicked silently ("disconnected before spawning") and the test fails with no clue.
- **Do not run anything else on the GPU or CPU during a GPU test.** A model run while another test runs shows 90-second timeouts that are not failures of the code.
- **A GPU test that hangs can leave its Paper server running** and holding its log file; the next run then fails to start. Look for a stray `java.exe` on the suite's port.
- **Do not kill `java.exe` blindly:** other projects (the AubsCraft dev server) run theirs.
- **Compare models and builds with the tool-choice test run ALONE, never inside a full `TestCategory=GPU` run.** Measured 2026-11-01 on an RTX 2060 12 GB: alone it takes 352 to 421 s and
  passes on every build tried; inside a full GPU run it took 1,226 s (and 1,645 s) and got more than two of 18 wrong. Not caused by the speech tests running just before it or by GPU memory
  (351 s right after them, 8.1 to 9.5 GB of 12.3 GB in use); the cause is not found.
- **A GPU older than Ampere (sm_80) needs SpawnDev.ILGPU.ML 5.8.1 or later:** 5.8.0 failed every ONNX Conv on an RTX 2060 (sm_75) with CUDA_ERROR_INVALID_PTX and nothing could hear.

## The tool-choice test

`ToolChoiceTests` asks the real model 18 things a child might say and checks the first tool it calls and what its arguments contain (the test stops each task as soon as it
starts: it tests the choice, not the doing). It is the measure behind most of [Architecture.md](Architecture.md): run it alone, it takes about four minutes.

| Switch | Effect |
|---|---|
| `VERTEX_TEST_MODEL=qwen3:8b` | the model to use, for every suite that loads the language model (`TestModel.cs`; a prefix of an Ollama model name; default `llama3.1`) |
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
