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
| Self-directed play (`SelfDirectedPlayTests`, `[Category("GPU")]`; `SelfDirectedPolicyTests` is plain) | with it on and nobody around he chooses a project and starts it with a tool (measured: 3 of 3 runs, "build a house" twice and "explore north 40 blocks"); with it off nothing starts for 45 s; in creative he never chooses | the GPU to itself (~4 min, port 25982); stop a running Vertex first |
| Honest reports (`HonestReportTests`, `[Category("GPU")]`; `OutcomeReportTests` is plain) | a job fails, the real model is told, and what a listening friend reads in chat must not claim it was done (4 attempts per run). Red-checked 2026-11-01 on an RTX 2060: with the old "you finished X. Result: ..." wording all 4 attempts said "I finished planting a field of carrots"; with the new wording 4 of 4 said it did not work | the GPU to itself (~3 min, port 25984); stop a running Vertex first |
| Explore tells only what could be seen (`LiveExploreHonestyTests`, real Paper, no GPU) | a sky platform with gold buried in it, a wolf shut in a stone box, diamond ore and a cow in plain view: only the diamonds and the cow are reported. Red-checked 2026-11-01: before the line-of-sight check he reported "gold, diamonds, 1 cow, 3 rabbits, 1 wolf" (the gold and the wolf could not be seen; the rabbits were animals the server told him about, position not checked) | Java as for the other live suites; port 25986 |
| Idle safety (`LiveSafetyReflexTests`, real Paper, no GPU; `SelfDirectedPlayTests.InWater_...`, GPU) | idle with an unlit creeper or zombie 4.5 blocks away (no AI) he walks to 9.5+ blocks away and does not attack it, even at bravery 1.0; idle in a 9 by 9 pool he walks out onto dry land; in water he chooses no project of his own, on land he does. Four creepers around him (one each side) and he ends 9.5+ blocks from the nearest (measured 9 s; the old straight-line flee also got out of this flat ring, in 24 s, one run each, so it does not reproduce the failure seen live on 2026-11-02, cause not known). `HonestReportTests.AFailedReflexJob_IsNotAnnouncedToHisFriends` (GPU): a reflex job that fails is logged and not spoken; red-checked 2026-11-02 (with the quiet handling off he said "I tried stepping away from the monsters, but it didn't work"). Each starts with a control (reflexes off: he stays put). Red-checked 2026-11-01: with the reflexes disabled all three live tests fail (stayed 5.0 blocks from the monster, stayed in the water); with the project gate disabled the water test reproduces the live event (he chose "build a platform" and failed) | Java as for the other live suites; port 25988 (GPU one: port 25982 and the GPU to itself) |
| Flight (`LiveSafetyReflexTests`, real Paper with allow-flight) | the server's own message is reproduced (abilities: may fly + flying, in survival): hovering 50 blocks up with the reflexes off, and with them on he flies down and lands on the floor with full health (no fall damage); flying in a 3-block-deep pool he does not rise and ends on dry land. Red-checked 2026-11-02: with the landing and the jump guard removed (the code as it was live) he rose from the water to 22.9 blocks up and stayed there flying (the live log showed a climb from 62 to 86). Found on the way: the leave-water reflex counted 8 in-water looks IN A ROW and never fired for someone bobbing at the surface; it now counts 8 of the last 12 | Java as for the other live suites; port 25988 |
| Long-term memory (`VertexMemoryTests`: real embedding model and files, CPU and CUDA variants; `LongTermMemoryBrainTests`, GPU, real Paper and model; `MemoryTextTests` plain) | what is recorded comes back by meaning and only in its own world; it survives a restart; forgetting a person removes them from the store, searches and files; paused learning and a blocked person store nothing; the recall cut-off sits between questions about stored things and small talk; a finished job is written by the brain and, after a restart, a friend's question about it is answered in the first person from the log (5 of 5 on 2026-11-02, RTX 2060), and without a log he does not know it (control). Timings (RTX 2060, warm): record 36 ms, recall 8-9 ms on CUDA; about 2.4 s per embedding on the CPU accelerator (reported to Data). Two finds: a tool that asks for the GPU lane from inside the chat turn waits on itself forever (the run sat 54 minutes), and a free-text note beside the friend's message was ignored or parroted by the 8B model (3 of 5 and 2 of 3), where a tool result is used | the GPU to itself for the brain tests (~7 min, port 25990); stop a running Vertex first |
| Rejoin (`HostRejoinTests`, `[Category("GPU")]`; `RejoinPolicyTests` is plain) | a real server goes away under a running Vertex, comes back, and he rejoins by himself; a Leave while he waits keeps him out; which disconnect reasons are worth retrying; a FIRST join that fails (server not there yet) is retried by the same schedule and he gets in when it appears (red-checked 2026-11-02: with the retry off he never scheduled one) | the GPU to itself (~4 min, port 25980); stop a running Vertex first (`Vertex.exe quit`, and wait: it needs ~30 s to stop properly) |
| Model install (`[Category("Models")]`) | `ModelSetupTests`: Vertex installs its own language model, in the library and through the dashboard host (progress, stop and resume, a clear error) | the internet; they install `all-minilm:22m` (about 46 MB) into a throwaway cache, from the real hosts (models.spawndev.com does not have it, so they also prove the fall-back to registry.ollama.ai) |
| Explicit | `DecodeSpeedTests`, `DevServerScenarioTests`, `ModelSetupTests.TheDefaultModel_InstallsFromModelsSpawndevCom_Alone` | run by name; the second needs the AubsCraft dev server on localhost (`VERTEX_DEV_RCON_PASSWORD`); the last downloads the 4.9 GB default model from models.spawndev.com alone and needs about 6 GB free in the temp folder |

Live tests start their own server on **a unique port per suite** (25932 voice, 25952 trading, 25954 building, 25956 farming, 25958 animals, 25960 tool choice, 25962
companion, 25964 fighting, 25966 mining, 25968 shield, 25970 fishing, 25972 gathering, 25974 end to end): two suites never share one, but do not run the same suite twice at once.

Things that have cost time:

- **Minecraft player names are at most 16 characters.** A longer name is kicked silently ("disconnected before spawning") and the test fails with no clue.
- **Do not run anything else on the GPU or CPU during a GPU test.** A model run while another test runs shows 90-second timeouts that are not failures of the code.
- **A GPU test that hangs can leave its Paper server running** and holding its log file; the next run then fails to start. Look for a stray `java.exe` on the suite's port.
- **Do not kill `java.exe` blindly:** other projects (the AubsCraft dev server) run theirs.
- **Compare models and builds with the tool-choice test run ALONE, not inside a full `TestCategory=GPU` run.** Measured 2026-11-01 on an RTX 2060 12 GB: alone it takes 352 to 421 s and
  passes on every build tried. Inside a full GPU run it took 1,226 s (and 1,645 s with gemma4) and got more than two of 18 wrong on ML 5.8.0 / 5.8.1-local.1, and did NOT recur on ML 5.8.1-local.3
  with AI.Server 1.4.0-local.9 (353 s, passed, in a 41-minute full run). It was not the speech tests just before it, not GPU memory (351 s right after them, 8.1 to 9.5 GB of 12.3 GB), and not
  thermal throttling (a 10-second nvidia-smi sampler over the 41 minutes: no thermal or hardware slowdown flag ever active, 74 C peak, clock 1785 to 1950 MHz of 2145; the software power cap
  showed in 72 of 247 samples, which is the card at its 184 W limit). The cause of the slow runs is not known; the newer ML/AI packages no longer show it.
- **A GPU older than Ampere (sm_80) needs SpawnDev.ILGPU.ML 5.8.1 or later:** 5.8.0 failed every ONNX Conv on an RTX 2060 (sm_75) with CUDA_ERROR_INVALID_PTX and nothing could hear.

## The tool-choice test

`ToolChoiceTests` asks the real model 18 things a child might say and checks the first tool it calls and what its arguments contain (the test stops each task as soon as it
starts: it tests the choice, not the doing). It is the measure behind most of [Architecture.md](Architecture.md): run it alone, it takes about four to seven minutes.

**Where its time goes** (measured 2026-11-02, RTX 2060 12 GB, SpawnDev.AI.Server 1.4.0-local.15, llama3.1:8b on the batch decoder, GPU otherwise idle, the test took 7 m 24 s; every
reply logs one `reply:` line from the engine's own timers, the LAST generation pass of each call): 32 replies, prompts of 3,105 to 5,081 tokens (median 4,431). Prefill: median
2.7 s per reply, about 1,570 prompt tokens a second; the first reply of the process took 32 s (kernels compiled on first use); all 32 summed 161 s, 129 s without that first one.
Decode: 2,255 tokens in 125 s, median 17.7 tokens a second (16.1 to 18.9), at about 4,400 tokens of context. So prefill and decode cost about the same. This is NOT comparable with
the 54 tokens a second Data measured on a 4070 on the same package: that was at a prompt of about 40 tokens, and every decode step reads the whole context's cache, so decode slows with
context on any card. Data is measuring the 4070 at 4,400 tokens to separate the context cost from the card; until then there is no claim that the 2060 is slow.

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
