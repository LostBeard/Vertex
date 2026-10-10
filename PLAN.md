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

## M3 - Body (done 2026-10-10 except crafting/smelting, which land with the M6 skills)
- [x] Vanilla-accurate player physics (gravity, drag, collision, step-up, jump, sprint, swim, ladders)
  so Paper's movement checks never rubber-band or kick it
- [x] Pathfinding (A*, walk/jump/fall/swim/pillar/dig-through)
- [x] Dig (correct break time per tool/enchant/effect), place, use, attack, eat, chests, creative flight
- [ ] Craft, smelt (with the M6 skill library)

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
- [ ] Speech-to-text (Whisper on SpawnDev.ILGPU.ML) per speaking player, voice activity detection
- [ ] Text-to-speech (Kokoro on SpawnDev.ILGPU.ML) back into proximity voice
- [ ] Falls back to text chat on servers without Simple Voice Chat

## M6 - Brain
- [ ] SpawnDev.AI LLM with tool calling over a skill library (follow, go to, gather, mine, build, craft, fight,
  give, guard, explore)
- [ ] Characters: name, personality (system prompt), voice, play style knobs; switch in game by voice or chat
- [ ] Memory: per-player facts, places, ongoing goals

## M7 - App
- [ ] Windows app: accounts, servers, characters, live status and log

## M8 - Modded servers
- [ ] Fabric registry sync so Vertex can join Fabric servers that need it (AubsCraft "spooky")
