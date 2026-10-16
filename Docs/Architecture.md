# Architecture

## The shape of it

Vertex is a **companion core** with **bodies** and **voice channels**.

- The **core** (`Vertex/Brain/Companion.cs`) is independent of any game: conversation and turns, characters, memory of people, the
  ears (speech to text), the mouth (voice), and one GPU lane that every model call goes through.
- A **body** is where Vertex is and what it can do there: Minecraft (`VertexBrain`: world state, game tools, skills, reflexes) or the
  Reachy Mini (`Reachy/RobotBody.cs`: gestures, parking). A body's tools join the language model's while it is connected and leave with it.
- A **voice channel** carries audio both ways: Simple Voice Chat in the game, the robot's microphone array and speaker, and text
  (game chat, the control panel). Several can be live at once.
- `SpawnDev.Minecraft` is the Minecraft Java protocol client (1.21.5, protocol 770): connection, login, world, physics, pathfinding,
  crafting, trading. It is a reusable library and knows nothing of the language model.

## How a request becomes an action

```
speech or text
  -> ears: voice activity, Smart Turn (is the speaker done?), Parakeet speech to text, who is speaking (ReDimNet2)
  -> wake word / conversation window (is it for Vertex?)
  -> System One (FastIntents): urgent simple commands ("stop", "follow me") are matched by a small embedding model and acted on at once
  -> Instinct: a classifier guesses which tool the sentence wants (under a millisecond)
  -> language model (llama3.1:8b, local): the prompt has the character, the tools, the state of the world, and the instinct's hint
  -> a tool call -> TaskRunner runs the skill (walks, digs, builds ...) while Vertex keeps talking and listening
  -> task ends -> a short spoken report, written from the result
  -> reflexes run underneath all of it (hunger, creepers, lava) and can interrupt a task
```

### Why tool choice is engineered the way it is

An 8-billion-parameter model picks the right tool most of the time and does something odd the rest. What is done about that,
each measured on the 18-sentence test (`ToolChoiceTests`) with the real model:

- **One tool per intent, few arguments.** 36 tools were too many for the model to choose among reliably: related ones were merged
  (`build_shape`, `trade`, `farm`, `animals` each take an action), and `build_shape` takes a single argument (the material rides in the
  same words as the shape: "stone castle"), because an optional argument written as an empty string followed by a stray comma used to
  make the whole call unreadable.
- **The instinct hint.** `Brain/Instinct.cs` is a classifier trained at start-up on `InstinctData` (hundreds of phrasings per tool, in a
  child's words and a speech recogniser's): hashed words and letter triples into a softmax, 0.09 ms per guess. When it is at least
  50% sure it gives the language model a note ("your first instinct is the tool X; call it unless that is clearly wrong"), and when it is 70%
  sure the sentence is only talk it says so. The model decides; the hint never acts by itself. Held out from its training, it names the
  right tool for all 18 test sentences; with the hint the real model went from 15 to 17 right to 18 and 17.
- **Say it and do it.** A small model sometimes says "I'm on my way" and calls nothing. Such a reply (an "I'm ...ing", "I am ...ing", "is being
  ...ed", an "I can't find any ..." said without trying) gets one retry that tells the model nothing happened; if it still calls no tool, what it
  said is not kept in the history, because a history of claims without calls teaches it to keep making them.
- **Game updates are not conversation.** "You finished building a house" events used to be turns of the chat history, and the model began
  answering requests by *reporting* results instead of calling tools (11 of 18 right). They are now a short note in the state block.
- **Short prompts.** Tool definitions are shown in a compact form (the first sentence of each description, each argument's description cut to
  60 characters; `GameTool.Compact`). The prompt went from about 4,500 to 3,200 tokens. Removing the argument descriptions entirely cost
  accuracy (the model called `trade` with empty arguments), so they are cut, not removed.

## Speed

Measured on an RTX 4070 (12 GB), llama3.1:8b Q4_K_M, the real Vertex prompt, with nothing else using the GPU. A turn is: read the new
words, write a tool call, run it, write a one-sentence report.

| | 2026-10-16 morning | After the work below |
|---|---|---|
| Average time for a turn | 6.5 s | 2.8 s |
| Decode speed in real turns | 4.7 tokens/s | 29 tokens/s |
| Decode at a short context | 12 tokens/s | 52 tokens/s (Ollama on the same GPU: 88) |
| Decode at a 4,800-token context | 12.7 tokens/s | 32 tokens/s |
| First read of a 3,159-token prompt (start-up) | 31 s (81 s with grouped attention) | 13 s |
| A 165-token read on top of a cached prompt | 5.3 s | 2.8 s |
| The 18-prompt tool-choice test | 11 minutes | 3.6 minutes |

### What the speed work found

Everything here is in the libraries, not in Vertex, so every app that uses them gets it:

- **Four fast kernels were off by default** in `SpawnDev.ILGPU.ML` (a vectorised and a dp4a int8 Q4_K decode matrix-vector kernel, grouped
  attention, last-position-only logits); the library's own Ollama-style example turned them on and nobody else did. `VertexAi.EnableFastKernels`
  sets them. Decode: 12 to 52 tokens/s at a short context. Switches for an A/B: `VERTEX_NO_FK=warp,dp4a,grouped,lastpos`.
- **Attention is the cost of a long context.** The grouped kernel was twice as slow as the register kernel when given many queries (a prefill),
  so it now serves only calls of four queries or fewer; and a new split-key-value ("flash-decoding") kernel spreads a decode step's keys across
  many blocks: 0.25 ms against 0.51 per layer at 3,165 keys. A tiled flash kernel for prefill was written, was correct, was no faster than the
  register kernel and was removed.
- **Prompt reading is matrix multiplies.** A 128 x 128-tile Q4_K GEMM with an 8 x 8 register tile per thread (identical output) took the
  matrix multiplies in a real prefill from 3.4 s to 1.5 s. ILGPU cannot lower a 64-element local array indexed in a hot loop, so the tile is
  64 scalar locals.
- **Measure the right thing.** A per-op profile without a sync after every op blames the next op for the previous one's GPU time. `ML_PER_OP_SYNC=1`
  (or `GraphExecutor.PerOpSync`) gives true per-op attribution; and Ollama's own 8 tokens/s on this GPU was a false baseline until its context was
  set to 4096 (its default 131,072-token context spills the model to the CPU).
- **Other fixes the work turned up:** the `mistral3` architecture (Ministral 3) now loads (it still needs its own chat template); a tool call with a
  trailing comma or a missing closing brace is parsed instead of dropped.

What is still slow: the Q6_K layers still use the older prefill GEMM; there is no tensor-core path; and the tile loop does not overlap its loads
with the multiply. Details and the order they would be done in are in [PLAN.md](../PLAN.md).

### Models tried

On this GPU, same 18 sentences: llama3.1:8b 16 to 18 right at about 3 s a turn (the one in use); qwen3:8b 16 right, slower; gemma4:12b 12 right, twice as
slow; qwen3:14b does not fit with the context (its first prompt read took 185 s); ministral-3:8b loads after the `mistral3` fix and then answers in the
wrong chat format.

## GPU memory

One 12 GB card runs the language model (4.9 GB), Parakeet (2 GB), Kokoro, the sentence encoder, the speaker and face models and the streaming
recogniser. Model calls take turns through `GpuLane` (speech recognition may run between the language model's tokens). When memory ran
short the driver spilled to system RAM silently and replies took minutes; the libraries now reclaim idle buffers across every model before the
driver spills.

## Libraries and their versions

Vertex is built on local-feed builds of `SpawnDev.ILGPU.ML` (5.3.11-local.6), `SpawnDev.AI.Server` and `SpawnDev.Reachy` while the chain is
verified; the released versions on nuget.org lag behind. Local builds live in a folder feed on the development machine (never copy a package
into it: add it with `nuget add`), and a library defect found here is fixed in the library and covered by a test there.
