# Vertex roadmap

Target: Minecraft Java 1.21.5 (protocol 770), the version the AubsCraft servers run. Other client versions reach
those servers through the ViaVersion plugins on the proxy; Vertex itself speaks 770.

Every milestone is tested against REAL servers started locally (Paper 1.21.5 build 114, Velocity), never mocks.

## M1 - Connect (done 2026-10-10, 26 tests green against real Paper 1.21.5)
- [x] Wire format: VarInt/VarLong, strings, UUID, packed block position, network NBT
- [x] Framing, zlib compression (live), AES/CFB8 encryption (AES-NI; verified against NIST + a reference
  implementation, not yet against a live online-mode server - that needs M4)
- [x] Server list ping (status)
- [x] Offline-mode login, configuration phase (registry data in full), play
- [x] Keep-alive (red-checked), ping/pong, teleport confirm, player loaded, 20 TPS client tick
- [x] Send and receive chat (player chat between two clients, console /say, /me), kick reason

## M2 - World model (done 2026-10-10)
- [x] Game data from the game itself (`tools/DataGen`): vanilla reports + a Java dump of the Mojang-mapped server
  (hardness, collision boxes, friction, fluids, entity sizes, block items), recipes and tags from the jar
- [x] Chunk sections (paletted containers), block updates, section updates, unload, block entities - checked
  block-for-block against the server (`execute if block`)
- [x] Entities (spawn, move, sync, teleport, remove, metadata, equipment), players (tab list), health, food, XP,
  attributes, time, tags
- [x] Item stacks with data components (ProtoDef over minecraft-data's 1.21.5 types), inventory, containers

## M3 - Body (done 2026-10-10 except smelting)
- [x] Vanilla-accurate player physics (gravity, drag, collision, step-up, jump, sprint, swim, ladders)
  so Paper's movement checks never rubber-band or kick it
- [x] Pathfinding (A*, walk/jump/fall/swim/pillar/dig-through)
- [x] Dig (correct break time per tool/enchant/effect), place, use, attack, eat, chests, creative flight
- [x] Craft: recipe book from the data pack, real container clicks, recursive planning, crafting table
- [ ] Smelt (furnace)

## M4 - Accounts (pulled ahead: done 2026-10-10 except signed commands)
- [x] Microsoft account sign-in: live.com device code under a public Minecraft title client id (no app
  registration), ECDSA-signed Xbox device/title/user tokens, XSTS, Minecraft token; cached with DPAPI,
  silent refresh. `vertex login`.
- [x] Online-mode login (session server join) - live test against a local online-mode Paper with a real account
- [x] Signed chat (Mojang profile key, chat session, last-seen signatures) for `enforce-secure-profile=true`
  (Paper's default and AubsCraft's). Waits for the server's INITIALIZE_CHAT before the first signed message.
- [ ] Signed commands (`chat_command_signed`) for /msg, /me, /say with enforce-secure-profile

## M5 - Voice
- [x] Simple Voice Chat client (SpawnDev.Minecraft.VoiceChat, compatibility 20 = 2.6.x): channel registration,
  secret over the plugin channel, AES-GCM UDP (authenticate, connection check, keep-alive, ping), Opus via
  Concentus, paced 20 ms frames + stop packet, per-speaker decoders, player voice states. Live-tested with the
  real voicechat-bukkit 2.6.16 plugin: one Vertex hears another (pitch verified)
- [x] Speech-to-text (Whisper on SpawnDev.ILGPU.ML) per speaking player; utterances end on the speaker's own
  stop packet (Simple Voice Chat's voice activation) or a silence gap
- [x] Text-to-speech (Kokoro on SpawnDev.ILGPU.ML) back into proximity voice, sentence by sentence; verified
  end to end: one Vertex says a sentence, another transcribes it, through the real plugin
- [x] Falls back to text chat on servers without Simple Voice Chat (replies are also echoed to chat)

## M6 - Brain (working: llama3.1:8b tool calling, Whisper/Kokoro, characters, reflexes; tests on GPU)
- [x] SpawnDev.AI LLM with tool calling over a skill library (follow, come here, go to, collect, craft, give,
  attack, eat, stop, look, inventory, remember, switch character)
- [ ] More skills: build structures, guard a player, explore, smelt, store items in chests, sleep
- [x] Characters: name, personality (system prompt), voice, play style knobs; switch in game by voice or chat
- [x] Memory: remembered facts (persisted)
- [ ] Per-player memory, places, long-running goals
- [x] Reflexes ("System One"): eat, fight back or flee, surface for air, every few ticks without the LLM
- [ ] A learned fast policy for reflexes (SpawnDev.ILGPU.ML)

## M7 - App
- [x] Dashboard (Vertex.exe, Blazor Server at http://localhost:5280): accounts, server, characters, model, voice,
  live status and transcript

## M8 - Modded servers
- [x] Fabric registry sync (fabric:registry/sync/direct + complete) - joins AubsCraft's "spooky" Fabric server
  (tested locally with its exact mod set)
- [x] Modded blocks with no data become a solid "unknown" block (physics/pathing treat them as walls); modded entity
  types named from the synced registry; Puzzles Lib mod spawns (Mutant Monsters) decoded; undecodable packets are
  skipped and counted instead of dropping the connection (zero on vanilla, asserted)
- [ ] Real shapes/hardness for modded blocks (Fabric does not sync them)
