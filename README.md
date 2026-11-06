# Vertex

**A companion that lives on your PC and plays Minecraft with you.** Vertex is an AI character you talk to out loud. At home
it can be the voice and eyes of a [Reachy Mini](https://www.pollen-robotics.com/reachy-mini/) robot; in the game it joins a
normal Minecraft Java Edition server as a regular player, hears you over proximity voice chat, and plays: follows you, gathers,
mines, builds, farms, trades, fishes, fights, and tells you what it is doing. Its character (name, personality, voice, play style)
is yours to pick, and you can switch characters while talking to it.

Named by Aubriella, who it was built to play with.

**Built with Llama.** Vertex thinks with Meta's Llama 3.1 8B Instruct. Llama 3.1 is licensed under the Llama 3.1 Community License, Copyright © Meta Platforms, Inc.
All Rights Reserved. Use of the model must follow Meta's [Acceptable Use Policy](https://llama.meta.com/llama3_1/use-policy). See [NOTICE](NOTICE).

> **Status: early, playable.** Vertex joins servers (Microsoft accounts, signed chat), moves with vanilla physics, pathfinds, digs,
> builds, crafts, farms, trades, fights, hears and speaks in proximity voice, lives in a Reachy Mini (hearing, speaking, seeing,
> body language) and takes requests through a language model that runs on your own GPU. It makes the ground fit a job (levels a building site, digs down to ore, walks to find animals and water), takes
> cover from archers and swarms, and learns from what happens to it: what works against each monster is scored from real outcomes, deaths and big losses become lessons, and it goes back for its things after a death. From nothing it can reach an iron pickaxe and sword in about 15 minutes on a live server, usually dying a few times in half an hour; a bed that holds is still ahead. The language model itself is trained for it, on your own GPU ([Docs/Training.md](Docs/Training.md)).
> [PLAN.md](PLAN.md) starts with where it stands and what is next.

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
- **Safe for children, useful for others.** Vertex was made for a child and her family, and a skill has to be safe to say
  wrong (no griefing, no lava, no building on top of someone's build). The same things that make it good for a family (local, private,
  no plugins, teachable) make it a candidate for clubs and classrooms.
- **Characters.** A character is a name, a personality, a voice and a play style. Make your own, switch in game.
- **A companion that learns.** Vertex is a test bed for how advanced a local AI can get, and being able to learn is part of that: from what happens in the game, in a memory it searches, and in the model's own weights, trained locally. Daily training at night is the next step.

## Running it

Vertex runs on Windows with an NVIDIA GPU (CUDA; it falls back to OpenCL/CPU, slowly). A 12 GB card carries the whole stack
(language model, speech, voices and faces together); it runs live on an RTX 2060 12 GB, and [Docs/Architecture.md](Docs/Architecture.md) says which card each measurement is from.

**Installer (the easy way, for another PC).** `installer\build-installer.bat` publishes a self-contained build (no .NET needed on the target) and
makes `installer\Output\VertexSetup-<version>.exe` with [Inno Setup](https://jrsoftware.org/isinfo.php) 6.1+. Setup installs for the current user
(no administrator), adds a Start menu entry, optionally starts Vertex with Windows, and warns when there is no NVIDIA driver. It installs Vertex only, not models
(Ollama's installer does not install models either): Vertex fetches its language model itself, the first time it is run (below). Uninstall keeps the models and
asks before deleting what Vertex remembers (`%APPDATA%\Vertex`: people, voices, faces, characters).

**Updating a PC that already has Vertex:** build the installer again and run it on that PC. It asks a running Vertex to quit (`Vertex.exe quit`: Vertex stops properly, so the robot
is parked and the game left), replaces the program and keeps the settings, the people Vertex knows and the models. Silent form: `VertexSetup-<version>.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART`
(add `/TASKS=autostart` to keep or turn on starting with Windows). A Vertex older than the `quit` command has to be closed first (tray icon, Quit), or setup stops with exit code 1 and installs nothing.
Start it again afterwards (`Vertex.exe --background`, or sign out and in).

By hand instead:

1. The language model. A new PC has none, and **Vertex installs it itself**: the **Start** tab says so and has an **Install** button (or run `vertex install-models`). It
   downloads `llama3.1:8b` (4.9 GB, every file checked against its SHA-256, resumable if stopped) from models.spawndev.com, then from registry.ollama.ai if that fails,
   into the Ollama model cache; Ollama does not have to be installed. If the cache already holds models (from Ollama or an earlier install) Vertex uses those.
   `vertex install-models --list` shows the other models that can be installed. The speech, voice and face models download on first use.
2. Start `Vertex.exe`. It lives in the notification area (the tray); a second launch opens the control panel at
   http://localhost:5280 (also reachable from other devices on your network). The panel has five tabs:
   **Start** (what to turn on), **Robot** (the Reachy Mini and its listening, seeing and speaking settings),
   **People** (who Vertex knows, and removing them), **Minecraft** (server, account, modpack) and **Character**. The panel also works from a tablet or phone on the
   same network (`http://<this PC's address>:5280`): type to Vertex there, and set **Typing as** to the name Vertex should use for what is typed. **Play on his own** (Minecraft tab, off by default): when nobody has needed Vertex for 5 minutes and he has no goal, he chooses ONE small project (build something little, explore up to about 40 blocks, gather, fish, farm) and does it with the same skills and rules as any request. Only in survival, by day, healthy and fed; talking to him, "stay" or a goal he already has outranks it, and he waits 3 minutes between projects and does not repeat his last few. If the connection to the server drops (a restart, a network problem) Vertex goes back by himself: after 15 s, 30 s, 1 min, 2 min, then every 5 minutes, with "rejoining" on the status; not after a ban, a kick, a whitelist or sign-in refusal, and never after you press Leave or Stop. Every connection that ends logs how it looked just before ("disconnected detail": last packet each way, keep-alive timing). **Start Vertex when this app starts** (Start tab) makes a PC that is Vertex's own come up running by itself, without anyone pressing Start (off by default). **Stop** keeps the models
   loaded for two minutes (a quick Start is fast), then gives the GPU back so the PC can play games; the tray's **Quit** frees everything at once.
3. For Minecraft: pick the server, sign in with the Microsoft account Vertex should play as (a device code appears), choose a
   character, press **Start playing**.
4. Talk to it: say its name ("Vertex, follow me", "Vertex, build me a little house"), in proximity voice (Simple Voice Chat) or in chat,
   or to the robot in the room. You can also type to it on the panel.

Command line: `vertex install-models [model | --list]`, `vertex login`, `vertex play <host> [--character Pixel] [--model llama3.1:8b] [--offline Name]`, `vertex ping <host>`.

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
other way. Nor does it take logs or stone out of a building for materials: a block within 3 of anything someone built is left alone.

## The robot

Vertex can live in a Reachy Mini: it hears through the robot's microphone array, speaks through its speaker, turns toward
whoever is talking, recognises the people it has been introduced to by voice and by face, greets someone who comes back after a
long absence, and acts out stage directions ("*antennas perk up*", "*head tilts*"). It parks the robot safely when it stops.
Setup, settings and limits: [Docs/Reachy.md](Docs/Reachy.md).

## Built on

| Library | Used for |
|---|---|
| [SpawnDev.ILGPU](https://github.com/LostBeard/SpawnDev.ILGPU) | GPU compute (CUDA, OpenCL, CPU) |
| [SpawnDev.ILGPU.ML](https://github.com/LostBeard/SpawnDev.ILGPU.ML) | GGUF language model inference and LoRA training, Parakeet speech recognition, speaker, face and end-of-turn models, Kokoro voices |
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
| [Docs/Training.md](Docs/Training.md) | How it learns: the trained adapter, making the data, the gate before it goes live, nightly learning |
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
