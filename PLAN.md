# Vertex roadmap

Target: Minecraft Java 1.21.5 (protocol 770), the version the AubsCraft servers run. Other client versions reach
those servers through the ViaVersion plugins on the proxy; Vertex itself speaks 770.

Every milestone is tested against REAL servers started locally (Paper 1.21.5 build 114, Velocity), never mocks.

## 2026-10-12 PIVOT (TJ): a responsive companion first, Minecraft second

The first real session (TJ on AubsCraft, 10-12) showed Vertex is too slow to be useful: "Hi, Vertex" got its answer
5.5 minutes later. TJ's direction: get the AI and speech-to-speech working well WITHOUT Minecraft in the middle,
through Aubs's Reachy Mini as Vertex's out-of-game avatar. Vertex becomes a tray app (start with Windows) whose
control panel opens in the system browser. Minecraft stays: it is one place Vertex can be, and Reachy can still be
his ears and voice while he is in game.

Work order: M9 (responsive) gates everything after it. M10-M13 below; M1-M8 (the game) are done and stay as they
are. Prior art to build on, not rewrite: SpawnDev.Reachy (`RoseAudioLink` desktop WebRTC to the robot both ways,
`ReachyBody`/`ReachyPresence`/`GestureClassifier`/`SpokenText`), its Rose app (`RoseTray` tray + Run key,
`RoseConversation` sentence-by-sentence speech with serialized playback and gestures, park recipe), the SpawnDev.AI
demo's hands-free work (Silero VAD endpointing, warm passes, prepared voices). Rose answered in about 1 s from the
end of a question with Ollama + sherpa Whisper + KokoroSharp; Vertex runs on our own stack (SpawnDev.AI,
SpawnDev.ILGPU.ML), so that 1 s is the bar our libraries have to meet. Library defects are fixed in the library.

### Defects seen live 2026-10-12 (all must be fixed and covered by a test)
- [x] **GPU memory overcommit (root cause of the minutes-long replies).** LLM + Whisper + Kokoro + MiniLM in one
  process reached 11.1 GB dedicated + 1.4 GB spilled into system RAM on the 12 GB RTX 4070. Each ILGPU.ML
  `BufferPool` keeps idle buffers and frees them only when an allocation FAILS (`AllocateWithReclaim`), but the
  Windows driver's default CUDA sysmem fallback never fails an allocation: it spills to system RAM, so nothing is
  ever reclaimed and the LLM decodes over PCIe (stack: `GgufGenerator` decode + Whisper decode both live, GPU 100%).
  Reclaim is also per pool, so one model's idle buffers cannot make room for another's.
  FIXED 2026-10-12 in the libraries (SpawnDev.ILGPU e4594a3 5.3.6-local.1: reclaim BEFORE the driver spills, across
  every holder; SpawnDev.ILGPU.ML 5.3.7-local.1..3: pools as reclaimers, captures pin their pools, Return by identity,
  outputs that live until the next run). Gate: AiSessionTests, all four models, 18 turns, free VRAM 0.5-0.7 GB throughout.
- [x] **One slow turn deafens Vertex.** The brain handles events strictly one at a time, so new speech queues
  behind a stalled generation and nothing can cancel it (TJ: "not hearing anything else I say").
  FIXED: per-turn cancellation, barge-in by name or "stop", 60 s turn timeout, a speaker's waiting lines merged into one
  turn (BrainTests CalledByNameWhileAnswering..., ATurnThatTakesTooLong...).
- [x] **Every reply after an interrupted one was token soup** (found by the barge-in tests). The GGUF generator's
  record of what its KV cache holds claimed rows that were never written: a generation cancelled before its prefill
  recorded the whole prompt (also in the released 5.3.6), and one aborted by an exception kept the old record.
  FIXED in SpawnDev.ILGPU.ML 5.3.7-local.4/5 (Qwen25_PrefixCache_ACancelledGenerationDoesNotPoisonTheNext).
- [x] **Tool arguments came back with mangled escapes** (a fact wrapped in the escape for U+00A2, a goal "for e"):
  SpawnDev.AI wrote the tool list into the prompt with every quote as a U+0027 / U+0022 escape and the model copied it.
  FIXED in SpawnDev.AI.Server 1.1.2-local.1 (ToolDefinitionTests).
- [ ] **Whisper tiny mishears**: "Hi, birdtakes", "Can you hear me for text?", "for your tax".
- [ ] **A greeting became a job**: "Hi, Vertex" -> "I'm on my way over" with no tool call (the re-ask round), then
  `come_here`; after the player left, three `look_at` rounds (~45 s each) on a player it "can't see".
- [ ] **Leave/Start keeps the loaded AI**, so a fresh start never gets fresh GPU memory (and a driver setting
  change needs a full restart to apply).
- [x] **Two models ran on the GPU at the same time** (found while diagnosing: Whisper decoding on the ears' thread
  while the LLM decoded on the brain's). Besides contention, a model recording a CUDA graph (Kokoro) makes any
  allocation from another thread an uncatchable access violation. Fix: one GPU lane for every model call, with
  speech to text allowed in between the LLM's tokens. DONE: `GpuLane` (VertexUnitTests GpuLane_*).

### Architecture (decided 2026-10-12)
- **Companion core + bodies + voice channels.** The core is game-independent: conversation and turns, characters,
  memory of people, ears (speech to text), mouth (voice), the GPU lane. A **body** is where Vertex is and what it
  can do there: Minecraft (world state, game tools, skills, reflexes) or the Reachy Mini (robot state, gestures).
  A **voice channel** carries audio both ways: Simple Voice Chat in game, the robot's microphone array and speaker,
  and text (game chat, the control panel). Several can be live at once (M13).
- **Order:** Step 1 makes the current (Minecraft) brain responsive and measurable; Step 2 splits the core out of
  `VertexBrain`; then the tray, the Reachy body, and both at once.
- **Vertex.exe is a Windows app**: no arguments = the tray (`--background` from the Run key: icon only); `ui` = the
  control panel in a console; `play`/`join` open their own console window (they read typed lines); `login`,
  `logout`, `ping` print to the terminal they are run from.

## M15 - Minecraft capabilities (TJ 2026-10-15: "building skills, farming, companion skills, trading with villagers, fighting"; Geordi's AubsCraft to help test)
The companion and the robot are in good shape (speech, faces, the link); Minecraft is next. Every item gets a test against a REAL Paper server (the Live tests), red-checked, before it is
called done; nothing here is claimed from a model run alone. Players are children too (Aubs): a skill must be safe to say wrong ("don't dig under my house", no griefing, no lava).

Order (chosen for what each unlocks, and how deterministically it can be tested):
1. **[x] Villager trading** (done 2026-10-15: `Trade`, `OpenTradesAsync`, `TradeAsync`, skills `trade` / `villager_trades`, LiveTradingTests 7/7 on Paper). Find a villager; interact (open_window merchant + merchant_offers); read the offers (buy A/B, sell, uses, max uses, price multiplier, demand, XP); select a
   trade and take the result (container clicks); policies: "sell my wheat for emeralds", "buy bread", "what does the librarian sell?", "get the best price", restock waiting. Test: /summon a
   villager with fixed Offers NBT (deterministic), plus level-up and out-of-stock cases.
2. **[x] Custom building** (done 2026-10-15: BuildScript, BuildShapes house/platform/pyramid/castle, `build_shape` / `build_script` / `continue_building`, LiveBuildTests, BuildScriptTests, BuildShapesTests; open: scaffolding for builds above 6 high, stairs and slabs, digging and pools, gathering the materials itself). Original idea: the LLM writes a construction script, not coordinates. A small plan language (fill / hollow box / line / ring / floor / roof gable+flat / door / window / stairs / fence / torch),
   validated and bounded (size, height, materials owned, not on a player's build), executed by the builder: bottom-up order so every block has something to click against, orientation states (stairs,
   doors, torches), gather-or-ask for materials, resume after an interruption. The existing house / wall / tower / bridge become scripts. Test: builds compared block for block against the plan on Paper.
3. **[x] Farming** (done 2026-10-15: `plant_field` till+sow+water, `breed_animals`, `shear_sheep`, `milk_cow`, LiveFarmingTests 4/4 and LiveAnimalTests 4/4 on Paper; open: bone meal, sugar cane / melon / pumpkin / nether wart, eggs, putting the harvest in a chest by itself). The whole cycle: till and plant a new field (hoe, seeds, water source / irrigation, fence), bone meal, more crops (sugar cane, melon, pumpkin, nether wart), animals (breed with the
   right food, shear, milk, eggs, slaughter for meat), put the harvest in a chest. Today it only harvests ripe crops and replants.
4. **Companion skills**: stay / wait here / come / heel; branch and strip mining with torches and ore-vein following; light up an area (torches in the dark so nothing spawns); keep a chest tidy;
   collect drops after we mine; carry and hand over; avoid lava, drops and cobwebs on the way; danger awareness at night and underground.
5. **Fighting**: ranged (bow, crossbow, charge and release), shield (block while the enemy swings), kiting skeletons, creeper-first target order, healing (golden apple, potion), fire and lava, fighting
   from a pillar. The learned fight/flee policy (ReflexPolicy) stays the first decision; new situations get new training rows.

With Geordi (AubsCraft, VoxelEngine, GameUI): his live viewer is the best way to SEE what Vertex builds (a screenshot of the server's world against the plan), and his plugin can be the ground truth
for assertions (blocks and entities as the SERVER has them, not as Vertex believes). To agree with him: a dev or test world on the AubsCraft server stack (never production without TJ's say), whether
Vertex's position and goal can be a marker in the viewer, and a way to ask the plugin "what is at x y z".

## M9 - Responsive (the gate for everything below)
- [ ] Per-turn timeline, logged and shown on the control panel: end of speech -> transcript -> first token ->
  first sentence -> first audio -> playback start, plus per-model GPU memory. Every number below is measured
  with it, never guessed. 10-12: the timeline is in (one "timing:" line per turn with the engine's prompt/reuse/TTFT);
  per-model GPU memory on the panel is still to do
- [x] One GPU memory budget for the whole process (SpawnDev.ILGPU.ML): idle buffers released before the driver
  spills, across every model on the accelerator, not only the pool that is allocating. Gate: a long session
  with all models loaded never spills (shared GPU memory stays ~0) and turn 20 is as fast as turn 1
- [x] Never deaf: hearing runs independently of thinking; new speech addressed to Vertex cancels (barge-in) or
  queues behind the current turn by policy, a stalled turn times out visibly, and an in-flight generation can be
  cancelled
- [x] Streamed replies: speak the first sentence while the rest generates (serialized playback, Rose's pattern).
  Verified through game chat in BrainTests; the voice side's timing waits for a live session
- [x] The system prompt + tool list prefix is computed once and reused across turns (no full re-prefill per turn).
  Game state moved out of the system prompt, history trimmed in chunks, prefix primed at start: reused 2,729 of ~2,870
  tokens, first token ~1.2 s (it was 17 s for the first turn, 2.4 s once the window slid)
- [ ] Speech recognition: pick the Whisper model by measured accuracy vs latency on recorded utterances (tiny
  fails, see above); Silero VAD endpointing for open microphones
- [ ] Talk vs act: greetings and chat get words, not jobs; no tool rounds on a player who is not there
- [ ] Generation speed: ~6 tokens/s with the ~3,000-token tool prompt (MEASURED 10-12: a tool call of ~30 tokens costs
  5-6 s before the first word). The largest remaining cost; it is in SpawnDev.ILGPU.ML's GGUF decode (Ollama runs this
  model several times faster on the same card)
- [ ] Target: about 1 s from the end of a question to the start of the spoken answer (Rose's measured number)

## M14 - Speech stack (TJ 10-14: shared with the SpawnDev.AI demo and future work; ranks above the game)
Speech-to-speech quality, responsiveness, competency and SPEAKER IDENTIFICATION come first; Minecraft is second. Research and
numbers: memory `proj-speech-stack-research-2026-10-14`. Everything here is a LIBRARY (ILGPU.ML pipeline, then SpawnDev.AI engine)
before it is a Vertex feature.
- [x] Research the 2026 state of the art online (ASR, speaker embeddings, full-duplex, TTS); Parakeet TDT and ReDimNet2 chosen to measure
- [x] `ParakeetTdtPipeline` (ILGPU.ML; managed / TypedArray / device overloads; GPU resident; parity with onnxruntime). MEASURED on LibriSpeech
  (60 utts): WER 3.75% vs Whisper tiny 11.53%, base.en 7.51%, small.en 7.51%; median 168 ms vs 177 / 260 / 633 ms on an idle GPU
- [x] `SpeakerEmbeddingPipeline` (ReDimNet2-B6). MEASURED (40 speakers): closed-set 99.2%, EER 1.21%, open-set 98.9% recognised / 0.0% wrong name
- [x] Parakeet and speaker pipelines: regression tests in PlaywrightMultiTest (ML 9133f6da: CUDA/OpenCL, real clips, onnxruntime reference). Open: the
  int8 Parakeet export crashes the encoder with a CUDA illegal memory access (Slice node 182, `encoder-model.int8.onnx`) - a library bug to fix; fp16
  weights (honoured only since ML 5.3.9-local.2; before, the opt-in did nothing for external-data models): the SAME 3.75% WER, 197 ms vs 163 ms, 2,012 vs 3,012 MiB loaded (Vertex uses them).
  int8 (ConvInteger fixed in ML): 3.75%, 222 ms, 3.6 GB loaded, but a 652 MB download vs 2.4 GB. Browser feasibility (WebGPU, 1.2 GB fp16 / 652 MB int8) not yet measured
- [x] Speaker embedding speed and parity: 1,114 ms -> 64 ms (GroupNorm was 91%: two-pass kernel; a small-constant broadcast Add expanded on the host) and
  cosine 1.000000 vs onnxruntime (FusedAttention register path was wrong for head dim 48). Further: Conv is now 57% of the 71 ms
- [ ] Parakeet speed: encoder + decode overheads (22 joint steps of ~3.4 ms each), int8 / fp16-weight variants measured for accuracy and speed
- [x] SpawnDev.AI 1.2.0-local.2: `AiSpeechEngine` model family (Parakeet), `AiSpeakerEngine`, `SpeakerRegistry` (5 tests on real embeddings). NOT released: the
  chain ML -> AI -> Vertex needs TJ's sign-off, the full ML PMT, a CHANGELOG entry and a source push first
- [x] Vertex on Parakeet (fp16 weights) + speaker model; Vertex GPU suite 35/35 on it. Open: speech-to-text time with the LLM generating (the 3.1 s Whisper
  figure was contention, not the model: measure Parakeet the same way)
- [x] Vertex knows who is speaking on the robot's microphone: `SpeakerTracker` (guests, introductions as a learning window, never takes over a known voice,
  continuity for clips too short to identify), tools `my_name_is` / `next_voice_is`, voices in %APPDATA%\Vertex\voices.json. Tests: 7 tracker tests (each guard
  red-checked) and a GPU test with 12 real clips of 3 speakers, all named correctly. Open: in-game proximity voice (Simple Voice Chat) identification, a live
  try on the robot with the family
- [ ] A real bench: TJ's voice and the household through the robot's microphone, with reference text (WER) and labels (speaker ID)
- [x] End of turn (10-14): Smart Turn v3.2 (pipecat-ai, BSD-2) decides from the audio whether the speaker is DONE. Library: ILGPU.ML `SmartTurnPipeline` + the 8 s
  mel (`WhisperMelPreprocessor(windowSamples, alignToEnd, normalizeWaveform)`; also fixed the Whisper mel's SYMMETRIC Hann window, periodic is the reference:
  1.5e-7 vs 1.8e-2..4e-2 against the HF extractor, WER unchanged); AI `AiTurnEngine`; Vertex `TurnAssembler` (VAD closes a segment after 250 ms; the model
  says finished -> out now, not finished -> held up to 1.2 s of SILENCE for the speaker to carry on, never losing speech if the model fails) wired into
  `MicrophoneEars`, setting `ReachySmartTurn` (default on, a checkbox). MEASURED on an idle RTX 4070: model 6 ms median; last spoken sample -> transcript
  median 490 ms (plain rule) vs 162 ms; a sentence with a 0.7 s pause stays ONE utterance (plain rule: two). Whole sentences judged finished 11/12, cut at
  a pause judged unfinished 8/12 (12 LibriSpeech clips). 11 assembler tests (each guard red-checked). Open: the 1/12 misjudged whole sentence costs the full
  1.2 s hold (a hold scaled by how unfinished the model thinks it is would cap that); calibrate threshold/hold on the household's real voices
- [x] First live session on the robot (10-14, TJ at home alone), what it taught and what was built (all red-checked, 86e4a15 .. d412ad9 + db6cffc):
  hearing: the name is matched however the recogniser writes it (Parakeet wrote "Rutax" / "Per text"; `WakeWord`, 0 false positives on 314 lines) and put right before the model sees it;
  `NoiseGate` (clicks, thumps, hiss, spectral flatness > 0.1 on short sounds: the "Yeah." / "Ha ha." / "No." / "Okay." phantoms of a silent room) with a rescue so a loud close
  "Vertex." is not thrown out as a click; a bare "Vertex." perks the antennas and waits (no spoken answer); an unidentified voice continues a conversation with anyone for 45 s;
  introductions ("I'm Todd") handled in code (the 8B model did not call the tool); the model's made-up lines for the friend ("Todd says (out loud): ... a castle?") are cut;
  robot rules (questions are answered, no "I'm on my way": 0/6 -> 6/6 and 4/5 -> 0/5 measured). Turn-taking: Smart Turn + `TurnAssembler` (hold while the speech continues, never
  lose speech), a transcript with no closing punctuation is held, a statement waits 700 ms and a continuation before the first sound replaces the half-formed answer (both lines
  answered once), the ears are judged by WHEN audio was recorded (a sentence said before the mute is kept), and the voice detector runs on the CPU (3.4 ms per 32 ms frame) so it
  hears while the language model prefills.
- [ ] STREAMING recognition (TJ 10-14: "Alexa does real-time STT on my 6 year old phone ... it even corrects what it thought it heard based on the words i say next"). Chosen:
  NVIDIA Nemotron Speech Streaming 0.6B (cache-aware FastConformer + RNN-T, NVIDIA Open Model License, LibriSpeech clean WER 2.46% at 560 ms, 2.56% at 160 ms, 2.80% at 80 ms,
  punctuation + capitalisation built in), sherpa-onnx export `csukuangfj2/sherpa-onnx-nemotron-speech-streaming-en-0.6b-{80,160,560,1120}ms-2026-04-25` (+ int8). CONTRACT
  (160 ms export, in D:/users/tj/Projects/_speech-research/nemotron160): encoder in `audio_signal [1,128,25]` (25 mel frames per step, shifted by 16 = 160 ms), `length`,
  `cache_last_channel [1,24,70,1024]`, `cache_last_time [1,24,1024,8]`, `cache_last_channel_len [1]` (all zero at start); out `outputs [1,1024,T']`, the three next caches;
  decoder = 2-layer LSTM (640), states `[2,1,640]` x2; joiner -> `[1,T,U,1025]`, blank = 1024; features 128-bin mel, hann, no normalisation (`normalize_type` empty), 8x subsampling.
  Encoder ops all standard (Conv, MatMul, LayerNormalization, Softmax, Slice, Where, Pad, Split, Tile). Two passes like Google/Alexa and RealtimeSTT: the streaming pass shows words
  live (wake word, "stopped mid-thought", early start) and Parakeet re-decodes the finished turn (it corrects "bulltrap" -> "Minecraft"). Reference: sherpa-onnx on CPU
  (`_speech-research/sherpa_stream_bench.py`). Next: `StreamingTranscriber` in ILGPU.ML + harness vs onnxruntime/sherpa, WER + per-chunk latency on the 60 clips, then Vertex.
- [ ] The slow terms are LLM first word (1.2-2.2 s) and Kokoro first audio (1.9 s warm), not STT (170 ms): profile and fix them in ILGPU.ML
- [ ] Streaming recognition (cache-aware Nemotron streaming FastConformer): text while the person is still talking, to cut time to first word
- [ ] TTS: evaluate Qwen3-TTS, Chatterbox-Turbo, Supertonic, CosyVoice2 against Kokoro (quality, time to first audio, licence), streaming output
- [ ] Barge-in and echo with a live robot (its own AEC alone is untested)

## M10 - Vertex without Minecraft
- [x] Split the brain: a companion core (conversation, characters, memory, ears, mouth, per-person memory) that
  runs with no game; Minecraft becomes one world it can join, plugging in its tools, skills and reflexes while
  connected (today `VertexBrain` requires a `MinecraftClient`). 10-12 code: `Brain/Companion.cs` (turns, barge-in,
  prompt, streaming speech, priming, characters) + `IBody` hooks (IsAddressed, OnFriendSpoke, FastActAsync,
  HandleEventAsync, Act) + `IVoiceChannel.AnswersEverything/HeardThrough`; `VertexBrain` is now the Minecraft body
  (a facade keeps its old members); `Core/MinecraftChannel.cs` (SVC ears + chat); `Mouth` speaks into channels
  (render ahead, play in order, ears closed for the whole reply). Bodies' tools come and go with them
  (`AiToolRegistry.Unregister`, SpawnDev.AI 1.1.1). Green: MouthTests 6/6 (CPU), CompanionTests 2/2 (GPU, no game),
  BrainTests 6/6 + HostTests.Start_Join 1/1 (no regression)
- [ ] Talk to Vertex from the control panel with no game and no robot (text in, text + voice out)
- [ ] Memory and characters shared between Reachy and the game (one Vertex, not two)

## M11 - Tray app
- [ ] Vertex.exe lives in the notification area: a status dot (off / listening / thinking / speaking, like Rose)
  and a menu: open the control panel in the system browser, start/stop listening, join/leave Minecraft, start with
  Windows (HKCU Run key), quit (parks the robot and leaves the game first). 10-12: `VertexTray` in; checked
  running with --background, panel answering, single instance; the icon and menu are for TJ to see
- [x] One instance only; a second launch opens the control panel instead (a --background one opens nothing)
- [x] Control panel stays the Blazor Server dashboard at http://localhost:5280

## M12 - Reachy Mini avatar
- [x] Connect to the robot (SpawnDev.Reachy: REST control + `RoseAudioLink` WebRTC; robot on daemon 1.10.0).
  LIVE 2026-10-12: `RobotTests.WakesSpeaksAndParks` green on the real robot (wake, a spoken reply, park, motors off)
- [x] Hear through the robot's microphone array (Silero VAD -> Whisper) and speak through its speaker (Rose's exact
  speaker recipe, every clip's play logged). LIVE: the microphone heard the room ("and see our rope", 300 ms to text)
- [x] Body language: `ReachyPresence` (listening / thinking / speaking), stage directions in replies acted out
  (`SpokenText` + `ReachyBody`: "*antennas perk up*" -> Perk, "*head tilts*" -> Tilt, LIVE), wake on start, park on
  exit with TJ's recipe (home -> wait -> goto_sleep -> wait -> motors off). `Reachy/RobotBody.cs`; panel sections for
  the robot and Minecraft (each joins/leaves while Vertex stays on)
- [ ] Never hears itself: ears closed for the whole reply (MouthTests, red-checked); the robot's AEC alone is untested
- [x] Who it answers: the room is not always talking to Vertex (the first live run answered speech that sounded like a
  video). Default: say "Vertex" to start, then the 45 s conversation window; panel switch "answer everything". Names
  as Whisper writes them (`Companion.VertexHeardAs`, measured: "birdtakes")
- [x] 🔴 GPU fault after one turn, FIXED in SpawnDev.ILGPU.ML 5.3.7: two sentences with the same token count shared a
  Kokoro executor whose node fold reused the first one's audio length; the vocoder's Pad wrote past its buffer
  (CUDA illegal address, every model dead). Found live on the robot, reproduced in MicrophoneTests
  (`AllModelsUnderMemoryPressure_StayHealthy`) and in ML (`Pipeline_Kokoro_SameTokenCount_...`)
- Proven on this robot by Rose (carry over, do not relearn): `play_sound` only QUEUES and returns, so render ahead
  and play strictly one clip after another (wait for each clip's duration, log start/end); mute the ears for the whole
  reply, not per sentence; face tracking and gestures take turns (watch while listening, perform while speaking);
  motors must be enabled or every move is silently accepted and ignored; WAV format is never validated by the daemon
  (wrong format = silence or wrong pitch, judge by ear); measure what Whisper hears for names, never match spelling.

## M13 - Robot and game at once
- [ ] Listen to the Reachy microphone and in-game proximity voice at the same time; every heard line carries its
  source; a switch per source on the control panel
- [ ] Answer where it was asked (robot speaker for the room, proximity voice for the game), with a setting for both
- [ ] The same person heard twice (in the room and in game) is one utterance, not two turns

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
- [x] Per-player memory, long-running goals (2026-10-11): players kept by UUID (first met, visits, facts about
  them, what Vertex did for them), shown in the prompt for whoever is around; greets a player who comes back to
  play by name. Goals (set_goal / finish_goal) persist per world; when nobody needs Vertex for a minute it takes
  the next step on its own, and after each step decides whether the goal is reached. Real-model runs taught it
  three things: an 8B model decorates tool arguments ("§7me§f", U+00A0, quotes: cleaned for every tool), names
  the player in a fact instead of "about" (matched by name), and says "I remember" without calling remember
  (caught like "on my way")
- [x] Reflexes ("System One"): eat, fight back or flee, surface for air, every few ticks without the LLM
- [x] A learned fast policy for reflexes (SpawnDev.ILGPU.ML), 2026-10-11: fight or flight when a mob hurts us is a
  10-32-32-2 TrainableModel (ReflexPolicy.cs) trained on real fights on local Paper (Vertex.Tests ReflexTraining,
  explicit): each situation (mob kind and count, health, armor, sword) played twice, fighting and fleeing, 20 s
  from the first hit; data in Vertex.Tests/Training/reflex-fights.jsonl. Trained on ILGPU's CPU accelerator (tiny),
  runs on the brain's accelerator; bravery moves the bar. Ships only if it beats the hand-written rule on fights it
  never saw (5-fold CV). 103 situations (two batches): learned 1.00 vs rule -1.15 mean score (always fight -0.72,
  always flee -8.89, best possible 1.45). They chose differently in 25: the network was better in 10, the rule in
  1, 14 ties (sign test p ~ 0.012). The first batch alone (56) was 7 vs 3, within noise; the second firmed it up.
  Also learned:
  Vertex's fleeing (12 blocks, stop) is often worse than fighting, vindicators above all; skeletons are the exception

## M7 - App
- [x] Dashboard (Vertex.exe, Blazor Server at http://localhost:5280): accounts, server, characters, model, voice,
  live status and transcript

## M8 - Modded servers
- [x] Fabric registry sync (fabric:registry/sync/direct + complete) - joins AubsCraft's "spooky" Fabric server
  (tested locally with its exact mod set)
- [x] Modded blocks with no data become a solid "unknown" block (physics/pathing treat them as walls); modded entity
  types named from the synced registry; Puzzles Lib mod spawns (Mutant Monsters) decoded; undecodable packets are
  skipped and counted instead of dropping the connection (zero on vanilla, asserted)
- [x] Real shapes/hardness for modded blocks (Fabric does not sync them), 2026-10-11: `tools/ModDump` runs the
  mods' own code. A dump written with Mojang names (like DataGen's) is compiled against Paper's jar, remapped to
  Fabric intermediary with the loader's own tiny-remapper (Mojang's server mappings + intermediary meet on the
  official names; the mappings are downloaded, never committed), and run once in a throwaway copy of the server.
  Its vanilla half must equal our table state for state (it does: 27,914) before the modded half is trusted.
  At runtime the synced block registry + that catalog give the server's exact state numbering, and the server's
  block tags put modded doors in wooden_doors: on spooky, all 345 modded blocks (3,430 states) are known and
  Vertex walks through a Spooky Doors door. A block without data, and everything after it, stays a solid unknown.
  Re-run the tool when the mod set changes.
- [x] Learns a server's modded blocks the way a player gets them, from its modpack (2026-10-11, TJ's idea): at
  join Vertex names the blocks it has no data for. With a modpack (setting; AubsCraft's PC pack by default on
  mc.spawndev.com) it downloads the pack's mods (hash-checked, cached), runs the dump mod it ships in a throwaway
  Fabric server (Fabric's launcher and a Temurin JDK, downloaded once), saves the result for that world, and
  rejoins knowing them. Verified: the AubsCraft pack teaches the same 345 blocks the server's own jars do
- [x] Learn the shape of blocks no modpack covers by touch, like a person would (2026-10-11): chunks keep raw
  state ids; next to an unknown block Vertex sends small moves toward it from each open side and over it, and
  the server (which re-runs every move with real collisions) accepts or corrects each one: a binary search per
  face. Verified against the mods' real shapes: doormat 1/16 high, wall plate, full cube, small pumpkin (bounding
  box), and cobwebs, which accept you and then hold you (sticky: pathing avoids them). Learned shapes apply at
  once to loaded blocks, are kept per world with the server's block-list fingerprint, and an idle Vertex studies
  unknown blocks nearby on its own (`learn_blocks` too). Modpack learning now applies without a rejoin as well
- [x] More of a block by experience (2026-10-11, catalog off, verified against the mods' real data):
  - doors and gates by right-clicking: a door-shaped unknown block (thin plate) is clicked with an inert hand, the
    server switches both halves, the new state is felt, and a "blocks one way / lets through" pair is a learned
    doorway; then it is put back as it was. Pathing opens it, walks through and shuts it behind (spooky_oak_door:
    the state it calls open is the mod's open=true). The probe now feels a block and an unknown one above it as
    one column (a door's lower half was unmeasurable before)
  - the name guides it before touching: only when the server's block names or tags say it has unknown doors or
    gates does a thin plate get clicked (otherwise it is a decoration)
  - hardness with the server as the clock: START then STOP at once; vanilla's "delayed destroy" finishes the dig
    on the tick the block's hardness says (dirt 15-16 ticks, vanilla math 14; candy cane block 21-23 vs 20)
  - names from experience: what breaking drops, or what placing a modded block item made; a learned name can be
    asked for ("collect candy cane block" mines mcwholidays:blue_candy_cane_block, server-timed)
  - all of it kept per world with the block-list fingerprint (one LearnedState per raw state id)
- [x] Modded items and mob sizes (2026-10-11): items resolve through the server's synced item registry (a modded
  item read as air before), and the dump now writes the mods' items with their default components through the
  game's own codecs (stack size, durability, tool rules, food: one parser for vanilla and modded) and their mobs'
  sizes (a mutant zombie is 1.8 x 3.2, not 0.6 x 1.8). Spooky: 366 items, 14 mobs; the modpack path dumps the same.
  The dump finds the server's registries by type (a direct registryAccess() call kept its Mojang name in the remap)
