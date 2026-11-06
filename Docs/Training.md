# Training

Vertex is a test bed for how far a local AI can go, and learning is part of the point. He learns in three ways today, and a fourth is planned.

| How | What it holds | Where |
|---|---|---|
| **In the moment** | What works against monsters (`DangerLearner`), blocks and moves that failed just now, what each biome reads | `worlds/<world>.danger.jsonl`; the rest in memory while he runs |
| **Long-term memory** | Jobs he finished and lessons he worked out, searched by meaning (RAG) and put in the prompt | `%APPDATA%\Vertex\memory` |
| **A trained adapter** | Which tool to call and how, and saying only what happened: a LoRA on the language model | `%APPDATA%\Vertex\adapters\current` |
| **Nightly learning (planned)** | The day's experience trained into the adapter, with a gate before he wakes up on it | See the end of this page |

The adapter changes the model itself, on your own GPU, with [SpawnDev.ILGPU.ML](https://github.com/LostBeard/SpawnDev.ILGPU.ML) doing the training. Nothing leaves the PC.

## The adapter in use

With an adapter in `adapters\current` and `LanguageAdapter` on in `settings.json` (the default), the host loads it and gives the model tool names only: what each tool is for and how to call it are in the adapter. With no adapter, or the setting off, he uses the plain model with the full tool list. An installer does not include an adapter.

Measured on an RTX 2060 12 GB, llama3.1:8b: v2.2 on 2026-11-05 (SpawnDev.AI.Server 1.4.1-local.3), the plain model on 2026-11-04 (1.4.1-local.2):

| | Plain, names only | Plain, full list | **v2.2, names only** |
|---|---|---|---|
| `ToolChoiceTests`, strict, 5 runs of 20 | 59/100 | 93/100 | **100/100** |
| Prompt size | 2,097 tokens | 3,771 tokens | about 1,900 to 2,100 tokens |
| First word, median | 682 ms | 1,175 ms | 778 ms (Vertex was sharing the card); 722 ms on AI.Server 1.4.2, one run |
| Decode, median | 26.7 tok/s | 24.4 tok/s | 25.2 tok/s |
| Honesty, 20 events it has never seen | 17/20 | | **20/20** |
| Honesty, 58 heldout lines, sentences not in training, exact | 0/58 | | **58/58** (only 5 distinct sentences) |

A cost: what he says about game events is plainer ("I just got a bow!"). That is what the targets ask for.

## Making the data

`Vertex.Tests/Training/TrainingSetGeneration.cs`, `[Explicit]`, with Vertex stopped because it needs the GPU. Output goes to `VERTEX_TRAINING_OUT`, not the repo.

- **Tool choice (`Synthetic`).** Distillation: the plain model with the full tool list is the teacher, and each reply is kept only if it passes that request's strict check, then the same messages are rendered with tool names only. When the teacher gets a training line wrong it is asked again with the tool named (`source: synthetic-v2-hinted`), and the hint is never in the line's prompt. Dev lines are never hinted: a failed one stays for its expected tool with `target_usable: false`. Nothing is hand-written. Every reply goes to `synthetic-replies.jsonl`.
- **Batches.** `VERTEX_SYNTH_BATCH` adds what a gate showed was missing: `continue21` ("carry on with the build", selling everything), `history22` (the same with a build in the earlier turns, planting a farm). `VERTEX_SYNTH_TOOLS` limits a run to some tools, to look at why they fail. None of the wording may match a `ToolChoiceTests` case (a test checks).
- **Honesty (`Honesty`).** Real game-event turns from his own log, rendered names-only, with the target replaced by the sentence true to the event (`HonestyScoring.Correct`). Heldout goes by sentence, not by session, and "I died." always trains, because nothing else teaches it.
- **`NewEventsScored`** runs 20 events the set has never held and scores them with `HonestyScoring.Honest`. `VERTEX_ADAPTER` picks the adapter. It is the honesty check that says most.

## Training

The trainer is `tools/TrainLoraJsonl` in SpawnDev.ILGPU.ML. Stop Vertex first: it takes about 11 GB of the card.

```
dotnet run --project tools/TrainLoraJsonl -c Release --no-build -- <llama3.1:8b gguf> "<data files, comma separated>" --out <folder> --eot 128009 --epochs 1 --rank 16 --lr 2e-4 --accum 8 --checkpoint --no-dev-loss --max-len 2560 --progress 5 --save-every 25 --targets attn_q,attn_k,attn_v,attn_output
```

It saves a checkpoint every 25 steps and ends with an evaluation, base against adapter, in `generation_eval.jsonl` and `train_log.json`.

Speed, `tools/TrainLoraProbe` at 1,600 tokens, RTX 2060 12 GB, 2026-11-05, every change exact (bit-identical or gradient-checked):

| ML commit | Seconds a step | VRAM |
|---|---|---|
| 08b95dd | 31.5 | 9.8 GB |
| 39d5fc8 | 10.45 | 10.9 GB |
| 37aa625 | 5.6 | 11.0 GB |
| 490e839 | 4.65 | 11.0 GB |
| a18c2fa, with the long-reduction V7 path the default | 4.47 | 11.1 GB |

v2.2 took 15,899 s (4.4 hours) on 39d5fc8, 10.6 s a line. At 4.47 s a v2.2-sized run should take about 1.9 hours; that is an estimate, no such run has been made. This PC has 16 GB of slow system memory, too little for activation offloading.

## Before an adapter goes live

1. `ToolChoiceTests` 5 times with `VERTEX_TOOLS_NAMES_ONLY=1` and `VERTEX_ADAPTER=<folder>`: at least 93/100 strict (what the plain model scores on the full list).
2. First word sooner than the full prompt's.
3. `NewEventsScored` above the plain model, and the heldout honesty lines. Read what he actually said: a scorer can pass nonsense.

Read the training lines too. v2.1 learned "I What hurt you: arrow from skeleton 11.5 hearts (6 times)." from a bug in the targets, and scored 25/25 on heldout lines that were mostly word for word in training.

| Adapter | Lines | Strict tool choice | New events | Notes |
|---|---|---|---|---|
| v1 | 755 | 94/100 | not run | no honesty lines; not live |
| v2.1 | 1,225 | 92/100 | 19/20 | garbled targets; "keep building" failed every run: no line had a history; not live |
| v2.2 | 1,499 | 100/100 | 20/20 | **live** |

## What may be trained on

- **Directed at Vertex:** may train him locally.
- **Anything shipped** (a published adapter, data, a package): none of it.
- **Speech not directed at him**, such as someone talking to friends while playing: used for nothing, in training or in memory.

Today `TrainingLog` is stricter than this: it writes no turn holding anything a person said ([Privacy-and-Safety.md](Privacy-and-Safety.md)), so all of the training data is game events and made-up requests. Chat training, when it is built, goes in a separate local-only file so that it cannot reach a shipped set.

## Nightly learning (planned, not built)

A brain does at night what it cannot do while in use, and training takes the whole card anyway. Each night he would log off when nobody is around, train on the day's events added to everything before, score the new adapter with the checks above, and wake up with it only if it passes; otherwise he keeps yesterday's.

What was published on this (read 2026-11-05, the first three from their abstracts):

- A nightly reflect, synthesize and LoRA pipeline kept 80.4% of knowledge against 36.8% for compaction; a preprint with no quality or safety gates ([2605.24657](https://arxiv.org/abs/2605.24657)).
- XENON corrects its knowledge from successes and failures in Minecraft, the game being the judge, and a 7B model with it beat agents on much larger models ([2505.24157](https://arxiv.org/abs/2505.24157), ICLR 2026).
- ETO pairs a good try with a failed one. Reportedly the gains stop after 1 or 2 rounds, and it does better after training on successes first ([2403.02502](https://arxiv.org/abs/2403.02502), ACL 2024).
- From a search, not checked by hand: self-correction with no outside feedback often makes models worse ([2310.01798](https://arxiv.org/abs/2310.01798)), models favour their own output ([2404.13076](https://arxiv.org/abs/2404.13076)), and training only on self-generated data collapses where keeping real data does not ([2410.16713](https://arxiv.org/abs/2410.16713)).

So: **the game judges, never he himself.** A claim the log does not support does not become a training line. Keep everything from before in the mix. No published standard for the gate before the swap was found; ours is new ground.

The training log says which adapter produced each line (`adapter`), so he cannot learn from his own output by accident; a nightly run has to use it. Long-term memory holds no people or conversations yet, only game experience, and directed speech belongs there. Chat has no log entry to judge it by, so game events come first, then corrections directed at him.
