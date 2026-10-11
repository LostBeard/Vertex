# Vertex

**An AI that plays Minecraft with you.** Vertex joins a normal Minecraft Java Edition server as a regular player,
talks with you over proximity voice chat, and plays the game: follows you, gathers, mines, builds, crafts, fights,
and explains what it is doing. Its character (name, personality, play style) is yours to pick, and you can switch
characters in game.

Named by Aubriella, who it was built to play with.

> **Status: early, playable.** Vertex joins servers (Microsoft accounts, signed chat), moves with vanilla physics,
> pathfinds, digs, builds up, crafts, fights, hears and speaks in proximity voice, and takes requests through a local
> language model. See [PLAN.md](PLAN.md) for what is done and what is next.

## Goals

- **Plays on any server, no server plugins.** Vertex speaks the real Minecraft Java protocol as an ordinary client.
  Nothing has to be installed on the server for it to join and play. Voice uses the
  [Simple Voice Chat](https://modrinth.com/plugin/simple-voice-chat) mod/plugin when a server already runs it.
- **Plays well.** It sees the world exactly (blocks, entities, inventory come from the protocol, not from guessing
  at pixels), moves with vanilla-accurate physics, and plans with a local LLM.
- **Voice first.** Hears you through Simple Voice Chat (speech-to-text on your GPU), answers in its own voice
  (text-to-speech on your GPU). Text chat always works too.
- **Characters.** A character is a name, a personality (system prompt), a voice and a play style. Make your own,
  switch between them in game.
- **Everything local.** Speech, language model and planning run on your own GPU through the SpawnDev libraries.
  No cloud AI service required.

## Running it

Vertex runs on Windows with an NVIDIA GPU (CUDA; it falls back to OpenCL/CPU, slowly).

1. Install a chat model into the Ollama cache, e.g. `ollama pull llama3.1:8b` (Vertex reads Ollama's model files
   directly; Ollama itself does not need to be running). The speech models download on first use.
2. `Vertex.exe` opens the dashboard at http://localhost:5280 (also reachable from other devices on your network).
   Pick the server, sign in with the Microsoft account Vertex should play as (a device code appears), choose a
   character, press **Start playing**.
3. Talk to it in game: say its name ("Vertex, follow me", "Vertex, get some wood", "Vertex, make a wooden pickaxe",
   "Vertex, be Pixel"), in proximity voice (Simple Voice Chat) or in chat. You can also type to it on the dashboard.

What you can ask it (any wording; the language model picks the tool):

| Ask | It does |
|---|---|
| "follow me", "come here", "go to 100 64 -20" | walks, climbs, swims, jumps, opens doors and gates (and closes them behind it) |
| "get some wood", "mine 10 stone", "find iron" | finds, walks to, mines and picks up blocks, with the best tool it has |
| "make a wooden pickaxe", "craft torches" | crafts with real container clicks, making the parts it is missing first |
| "smelt the iron", "cook the beef" | builds a furnace if needed, fuels it, waits, takes the output |
| "protect me", "fight the zombies" | guards a player, fighting monsters that come near them |
| "put your stuff in the chest", "get bread from the chest" | stores and takes items (keeps its tools, armor, food) |
| "build us a house" | finds a flat spot near you and builds a small house (walls, windows, roof, torch, a door facing you) |
| "go to bed" | sleeps in the nearest bed (the night passes when everyone sleeps) |
| "give me your diamonds", "eat something" | walks over and drops items; eats when hungry (also by reflex) |
| "remember this is home", "go home" | remembers named places per server and walks back to them |
| "harvest the farm" | picks the ripe crops (leaves the rest growing) and replants them |
| "go explore to the north" | walks out, comes back and tells you what it found (villages are remembered as places) |
| "be Pixel" | switches character (personality and voice) |

Urgent, simple commands ("stop!", "follow me", "come here", "protect me") are matched by a small sentence
embedding model (all-MiniLM-L6-v2 on your GPU through SpawnDev.ILGPU.ML) in milliseconds, so Vertex acts at once;
the language model then only words its reply. Anything else, or anything negated ("don't follow me"), goes to the
language model.

Walking somewhere never breaks player builds (planks, glass, doors, bricks...): it only digs natural blocks, and
only when there is no other way.

Command line: `vertex login`, `vertex play <host> [--character Pixel] [--model llama3.1:8b] [--offline Name]`,
`vertex ping <host>`.

Characters live in `%APPDATA%\Vertex\characters\*.json` (name, personality, voice, play style) and can be edited
on the dashboard. Built in: Vertex, Pixel, Captain, Bolt.

## Built on

| Library | Used for |
|---|---|
| [SpawnDev.ILGPU](https://github.com/LostBeard/SpawnDev.ILGPU) | GPU compute (CUDA, OpenCL, CPU) |
| [SpawnDev.ILGPU.ML](https://github.com/LostBeard/SpawnDev.ILGPU.ML) | Whisper speech-to-text, Kokoro text-to-speech, GGUF LLM inference |
| [SpawnDev.AI](https://github.com/LostBeard/SpawnDev.AI) | LLM chat engine and tool calling |
| **SpawnDev.Minecraft** (this repo) | Minecraft Java protocol client: connection, login, world, physics |

## Repository layout

| Folder | What |
|---|---|
| `SpawnDev.Minecraft/` | Reusable Minecraft Java Edition client library (protocol 770, Minecraft 1.21.5). |
| `Vertex/` | The Windows app: brain, characters, voice. |
| `Vertex.Tests/` | Tests. They run against REAL Paper servers started locally (no mocks). |
| `tools/DataGen/` | Regenerates protocol tables from the vanilla server's own data reports. |

## License

MIT. See [LICENSE](LICENSE). Minecraft is a trademark of Mojang Studios; Vertex is not affiliated with Mojang or
Microsoft. You need your own Minecraft Java Edition account for Vertex to play on online-mode servers.
