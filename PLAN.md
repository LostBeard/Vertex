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

## M2 - World model
- [ ] Block state table from the vanilla data report (`tools/DataGen`)
- [ ] Chunk sections (paletted containers), block updates, section updates, unload
- [ ] Entities (spawn, move, remove, metadata), players (tab list), health, food, XP
- [ ] Inventory and containers

## M3 - Body
- [ ] Vanilla-accurate player physics (gravity, drag, collision, step-up, jump, sprint, swim, ladders)
  so Paper's movement checks never rubber-band or kick it
- [ ] Pathfinding (A*, walk/jump/fall/swim/pillar/dig-through)
- [ ] Dig (correct break time per tool/enchant/effect), place, use, attack, eat, craft, smelt, chests

## M4 - Accounts (pulled ahead: done 2026-10-10 except signed commands)
- [x] Microsoft account sign-in: live.com device code under a public Minecraft title client id (no app
  registration), ECDSA-signed Xbox device/title/user tokens, XSTS, Minecraft token; cached with DPAPI,
  silent refresh. `vertex login`.
- [x] Online-mode login (session server join) - live test against a local online-mode Paper with a real account
- [x] Signed chat (Mojang profile key, chat session, last-seen signatures) for `enforce-secure-profile=true`
  (Paper's default and AubsCraft's). Waits for the server's INITIALIZE_CHAT before the first signed message.
- [ ] Signed commands (`chat_command_signed`) for /msg, /me, /say with enforce-secure-profile

## M5 - Voice
- [ ] Simple Voice Chat client: secret handshake over the plugin channel, encrypted UDP, Opus
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
