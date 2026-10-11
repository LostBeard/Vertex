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

## M3 - Body (done 2026-10-10)
- [x] Vanilla-accurate player physics (gravity, drag, collision, step-up, jump, sprint, swim, ladders)
  so Paper's movement checks never rubber-band or kick it
- [x] Pathfinding (A*, walk/jump/fall/swim/pillar/dig-through, ladders up and down: 2026-10-11 a ladder's
  full-height plate had made every ladder block a wall, so nothing could climb)
- [x] Dig (correct break time per tool/enchant/effect), place, use, attack, eat, chests, creative flight
- [x] Placing shrinks the held stack at once, as vanilla's client predicts (the server's slot update comes at the
  end of its tick; without it a build clicked with an emptied stack and the block never appeared)
- [x] Craft: recipe book from the data pack, real container clicks, recursive planning, crafting table
- [x] Smelt (furnace): crafts and places one if needed, picks the least-wasted fuel

## M4 - Accounts (pulled ahead: done 2026-10-10, signed commands 2026-10-11)
- [x] Microsoft account sign-in: live.com device code under a public Minecraft title client id (no app
  registration), ECDSA-signed Xbox device/title/user tokens, XSTS, Minecraft token; cached with DPAPI,
  silent refresh. `vertex login`.
- [x] Online-mode login (session server join) - live test against a local online-mode Paper with a real account
- [x] Signed chat (Mojang profile key, chat session, last-seen signatures) for `enforce-secure-profile=true`
  (Paper's default and AubsCraft's). Waits for the server's INITIALIZE_CHAT before the first signed message.
- [x] Signed commands (`chat_command_signed`) for /msg, /me, /say with enforce-secure-profile: the server's
  command tree (commands packet, argument types from the game's own registry report), a Brigadier walk that finds
  the `minecraft:message` arguments exactly as SignableCommand.of does (through /tell -> /msg, but not past
  `execute ... run`), each signed over its own text. Without it Paper refused them ("invalid command signature").
  Same day: a /msg to ourselves arrives twice with one signature and counts once (vanilla), else a kick

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
- [x] Guard a player (fights monsters within 10 blocks of them, leash 16)
- [x] Store and take items (chests, barrels), sleep in a bed (pose tracked from our own entity data)
- [x] Doors and fence gates: opened on the way, closed behind
- [x] Never digs through player builds while walking (planks, glass, bricks, doors...)
- [x] Named places per server ("remember this is home", "go home"), long-distance travel
- [x] Creeper reflex: a lit fuse (entity data 16/18) interrupts any job, sprint 8+ blocks away, then resume the job
- [x] Wears the best armor it carries (armor + toughness from the item's own modifiers)
- [x] Build a house (5x5 or 7x7 by materials): flat-site search, walls, glass windows, roof, torch, door facing the asker, built from inside
- [x] Explore a direction and report villages, animals, rare ores (remembers villages)
- [x] System One for speech: all-MiniLM-L6-v2 (SpawnDev.ILGPU.ML) matches urgent commands in milliseconds and acts before the LLM speaks
- [x] Farm: harvest ripe wheat/carrots/potatoes/beetroot, leave unripe, replant
- [x] Critical hits: jump as the charge completes, hit on the way down (x1.5, measured on Paper)
- [x] More structures: walls (straight, up steps, dips filled to the top; or a pen around a player with a gate
  and door), a 3x3 lookout tower (pillars up its core, ladder up the outside, crafts missing ladders, climbs down),
  bridges across water, lava or ravines to ground on the far side
- [x] Characters: name, personality (system prompt), voice, play style knobs; switch in game by voice or chat
- [x] Memory: remembered facts (persisted)
- [ ] Per-player memory, long-running goals
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
