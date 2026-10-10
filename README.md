# Vertex

**An AI that plays Minecraft with you.** Vertex joins a normal Minecraft Java Edition server as a regular player,
talks with you over proximity voice chat, and plays the game: follows you, gathers, mines, builds, crafts, fights,
and explains what it is doing. Its character (name, personality, play style) is yours to pick, and you can switch
characters in game.

Named by Aubriella, who it was built to play with.

> **Status: early development.** Vertex can sign in with a Microsoft account, join online-mode servers, stay
> connected and chat (signed). It does not move or play yet. See [PLAN.md](PLAN.md) for the roadmap and what works today.

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
