# Vertex

**A companion that lives on your PC and plays Minecraft with you.** Vertex is an AI character you talk to out loud. At home
it can be the voice and eyes of a [Reachy Mini](https://www.pollen-robotics.com/reachy-mini/) robot; in the game it joins a
normal Minecraft Java Edition server as a regular player, hears you over proximity voice chat, and plays: follows you, gathers,
mines, builds, farms, trades, fishes, fights, and tells you what it is doing. Its character (name, personality, voice, play style)
is yours to pick, and you can switch characters while talking to it.

Named by Aubriella, who it was built to play with.

> **Status: early, playable.** Vertex joins servers (Microsoft accounts, signed chat), moves with vanilla physics, pathfinds, digs,
> builds, crafts, farms, trades, fights, hears and speaks in proximity voice, lives in a Reachy Mini (hearing, speaking, seeing,
> body language) and takes requests through a language model that runs on your own GPU. [PLAN.md](PLAN.md) has what is done and what is next.

## Goals

- **Local first.** Speech recognition, the language model, voice synthesis, speaker and face recognition all run on your own
  GPU through the SpawnDev libraries. No cloud AI service is needed, and what people say and look like stays on your PC
  ([Docs/Privacy-and-Safety.md](Docs/Privacy-and-Safety.md)).
- **Plays on any server, no server plugins.** Vertex speaks the real Minecraft Java protocol as an ordinary client. Nothing has to
  be installed on the server for it to join and play. Voice uses the [Simple Voice Chat](https://modrinth.com/plugin/simple-voice-chat)
  mod/plugin when a server already runs it.
- **Plays well.** It sees the world exactly (blocks, entities and inventory come from the protocol, not from guessing at pixels),
  moves with vanilla-accurate physics, and every skill is tested against a real Paper server.
- **Voice first.** Parakeet speech recognition, an end-of-turn model that tells "finished" from "thinking", and speaker
  identification so it knows who is talking. Text chat always works too.
- **Safe for children, useful for others.** Vertex was made for a six-year-old and her family, and a skill has to be safe to say
  wrong (no griefing, no lava, no building on top of someone's build). The same things that make it good for a family (local, private,
  no plugins, teachable) make it a candidate for clubs and classrooms.
- **Characters.** A character is a name, a personality, a voice and a play style. Make your own, switch in game.

## Running it

Vertex runs on Windows with an NVIDIA GPU (CUDA; it falls back to OpenCL/CPU, slowly). A 12 GB card carries the whole stack
(language model, speech, voices and faces together); the measurements in [Docs/Architecture.md](Docs/Architecture.md) are from an RTX 4070.

1. Install a chat model into the Ollama cache, e.g. `ollama pull llama3.1:8b` (Vertex reads Ollama's model files directly; Ollama
   itself does not need to be running). The speech models download on first use.
2. Start `Vertex.exe`. It lives in the notification area (the tray); a second launch opens the control panel at
   http://localhost:5280 (also reachable from other devices on your network). The panel has five tabs:
   **Start** (what to turn on), **Robot** (the Reachy Mini and its listening, seeing and speaking settings),
   **People** (who Vertex knows, and removing them), **Minecraft** (server, account, modpack) and **Character**.
3. For Minecraft: pick the server, sign in with the Microsoft account Vertex should play as (a device code appears), choose a
   character, press **Start playing**.
4. Talk to it: say its name ("Vertex, follow me", "Vertex, build me a little house"), in proximity voice (Simple Voice Chat) or in chat,
   or to the robot in the room. You can also type to it on the panel.

Command line: `vertex login`, `vertex play <host> [--character Pixel] [--model llama3.1:8b] [--offline Name]`, `vertex ping <host>`.

Characters live in `%APPDATA%\Vertex\characters\*.json` and can be edited on the panel. Built in: Vertex, Pixel, Captain, Bolt.

## What you can ask it (in Minecraft)

Any wording works; a small classifier ("instinct") and the language model pick the tool. The full list, what each skill needs and
what it will not do is in [Docs/Skills.md](Docs/Skills.md).

| Ask | It does |
|---|---|
| "follow me", "come here", "go to 100 64 -20", "stay here" | walks, climbs, swims, jumps, opens doors and gates; waits where it is |
| "get some wood", "mine 10 iron", "bring me ten wood" | finds, walks to, mines and picks up blocks with the best tool it has; "bring me" fetches first, then hands it over |
| "build a house", "a stone castle", "a pen for the cows", "keep building" | builds a named shape near you from what it carries; short of wood or cobblestone it cuts logs (and makes planks) or mines first |
| "make a wheat farm", "harvest the crops" | tills, waters and sows a field; picks ripe crops and replants |
| "breed the cows", "shear the sheep", "milk the cow" | feeds animals so babies are born, shears, milks (also across a fence) |
| "what does the villager sell?", "sell my wheat", "buy bread" | opens the villager's trades and does them |
| "let's go fishing" | walks to open water, casts, waits for the bite, reels in |
| "protect me", "shoot the zombie", "fight the spiders" | guards you; shoots creepers and skeletons from range with a bow; hits a lit creeper once and runs; raises a shield between swings |
| "put some torches around" | lights an area on a grid |
| "make a wooden pickaxe", "smelt the iron" | crafts with real container clicks (making missing parts first), runs a furnace |
| "put your stuff in the chest", "go to bed", "eat something" | stores and takes items, sleeps through the night, eats (also by reflex when hungry) |
| "remember this is home", "go home", "go explore" | remembers named places per server, walks back, explores and reports |
| "be Pixel" | switches character (personality and voice) |

Urgent, simple commands ("stop!", "follow me", "come here", "protect me") are matched in milliseconds by a small sentence
embedding model (all-MiniLM-L6-v2) so Vertex acts at once; the language model then only words its reply.

Walking somewhere never breaks player builds (planks, glass, doors, bricks...): it only digs natural blocks, and only when there is no
other way.

## The robot

Vertex can live in a Reachy Mini: it hears through the robot's microphone array, speaks through its speaker, turns toward
whoever is talking, recognises the people it has been introduced to by voice and by face, greets someone who comes back after a
long absence, and acts out stage directions ("*antennas perk up*", "*head tilts*"). It parks the robot safely when it stops.
Setup, settings and limits: [Docs/Reachy.md](Docs/Reachy.md).

## Built on

| Library | Used for |
|---|---|
| [SpawnDev.ILGPU](https://github.com/LostBeard/SpawnDev.ILGPU) | GPU compute (CUDA, OpenCL, CPU) |
| [SpawnDev.ILGPU.ML](https://github.com/LostBeard/SpawnDev.ILGPU.ML) | GGUF language model inference, Parakeet speech recognition, speaker, face and end-of-turn models, Kokoro voices |
| [SpawnDev.AI](https://github.com/LostBeard/SpawnDev.AI) | The chat engine, tool calling, and the speech/speaker/turn engines |
| [SpawnDev.Reachy](https://github.com/LostBeard/SpawnDev.Reachy) | The Reachy Mini: robot control, WebRTC audio and camera |
| **SpawnDev.Minecraft** (this repo) | Minecraft Java protocol client: connection, login, world, physics, crafting, trading |

Vertex is also how those libraries get tested and improved: speed and correctness problems found here are fixed in the library
(see "What the speed work found" in [Docs/Architecture.md](Docs/Architecture.md)).

## Documentation

| | |
|---|---|
| [Docs/Skills.md](Docs/Skills.md) | Every Minecraft skill: wording, requirements, safety, limits, the build-script language |
| [Docs/Architecture.md](Docs/Architecture.md) | Companion core, bodies and voice channels; how a request becomes an action; speed, with numbers |
| [Docs/Reachy.md](Docs/Reachy.md) | The Reachy Mini: setup, what it does, settings, limits |
| [Docs/Privacy-and-Safety.md](Docs/Privacy-and-Safety.md) | What is kept, where, for how long; what Vertex will and will not do in a world |
| [Docs/Testing.md](Docs/Testing.md) | How the tests work (real servers, GPU tests, benchmarks) and the switches for A/B runs |
| [PLAN.md](PLAN.md) | Milestones, decisions and measured results |

## Repository layout

| Folder | What |
|---|---|
| `SpawnDev.Minecraft/` | Reusable Minecraft Java Edition client library (protocol 770, Minecraft 1.21.5). |
| `SpawnDev.Minecraft.VoiceChat/` | The Simple Voice Chat protocol, for proximity voice in game. |
| `Vertex/` | The Windows app: companion core, game brain and skills, speech, the robot, the control panel. |
| `Vertex.Tests/` | Tests. They run against REAL Paper servers started locally (no mocks), and the real language model on the GPU. |
| `tools/DataGen/` | Regenerates protocol tables from the vanilla server's own data reports. |
| `tools/ModDump/` | Reads a modpack's blocks. |
| `tools/ReachyDoa/` | Tries the robot's direction-of-arrival reading. |

## License

MIT. See [LICENSE](LICENSE). Minecraft is a trademark of Mojang Studios; Vertex is not affiliated with Mojang or Microsoft.
You need your own Minecraft Java Edition account for Vertex to play on online-mode servers.
