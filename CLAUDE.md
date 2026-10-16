# CLAUDE.md - Vertex

Vertex is a C# AI that plays Minecraft Java Edition with Aubriella (and anyone else) as a normal client: no server
plugins. Global rules: `D:\users\tj\Projects\AGENTS.md`. This file holds Vertex specifics only.

## Ground rules

- **Protocol version 770 (Minecraft 1.21.5).** Packet ids come from the vanilla server's own data report
  (`tools/DataGen` -> `SpawnDev.Minecraft/Protocol/PacketIds.g.cs`). Never hand-type a packet id; regenerate.
  Field layouts: Mojang's packet codecs; minecraft-data `proto.yml` (1.21.5) is the readable cross-reference.
- **No server plugins, ever.** Vertex must work on a stock server. Simple Voice Chat is used only where the server
  already has it, and Vertex must play fine without it.
- **Physics must match vanilla.** Paper's movement checks kick or rubber-band anything else. Port behaviour from the
  vanilla client code paths (cross-check prismarine-physics), and test against a real Paper server.
- **Tests run against REAL servers** (`Vertex.Tests/Live`): Paper 1.21.5 build 114 + Temurin 25, offline mode,
  bound to 127.0.0.1, killed with the test host (Job Object). Shared download cache with the AubsCraft tests
  (`%LOCALAPPDATA%\AubsCraft.Tests`) so nothing is downloaded twice.
- **Never connect tests to production** (mc.spawndev.com). Production play is TJ's call, with an account he picks.
- **Bulk data stays pooled.** Frames come from `ArrayPool`; chunk data is parsed in place into the world model,
  never copied into intermediate managed arrays first.
- **Accounts:** tokens are cached encrypted (DPAPI, current user). Never log a token, never commit one.

## Docs

`README.md`, `Docs/` (Skills, Architecture, Reachy, Privacy-and-Safety, Testing) and `SpawnDev.Minecraft/README.md` describe what Vertex does today. When a skill, a setting, a
default, a number measured in `PLAN.md` or a port changes, update the page that states it in the same commit. Measured numbers carry their date and hardware.
