# Minecraft skills

What Vertex can do in a Minecraft Java world, what each skill needs in its pockets, and what it will not do. Skills are the code in
`Vertex/Brain/Skills*.cs`; the language model reaches them through tools (`Vertex/Brain/GameTools.cs`). Every skill has a test against a
real Paper 1.21.5 server (`Vertex.Tests/Live*.cs`); [Testing.md](Testing.md) says how to run them.

You never have to say a tool name. Say what you want in your own words; a small classifier (the "instinct", see
[Architecture.md](Architecture.md)) and the language model choose. The examples below are wordings that are tested.

## Moving and staying

| Say | Tool | Notes |
|---|---|---|
| "follow me" | `follow_player` | Keeps up: walks, climbs, swims, jumps, opens doors and gates and closes them behind it. |
| "come here" | `come_here` | Walks to where you are. |
| "go to 100 64 -20", "go to the mountain" | `go_to` | Coordinates, or a place you named. |
| "stay here", "wait for me", "don't move" | `stay` | Really stays: it does not wander off to be useful until it is asked something else. |
| "stop" | `stop` | Cancels the current task. A bare "stop!" is also caught instantly, without the language model. |
| "remember this is home", "go home", "forget home" | `mark_place`, `go_to_place`, `forget_place` | Named places are kept per server. |
| "go explore" | `explore` | Walks out, comes back and tells you what it found; villages become places it remembers. It reports only what a player could have SEEN (a clear line from the eyes, within 48 blocks): ore buried in rock or an animal behind a wall is not reported, because the server sends all of that to every client. |

Walking never breaks a player build (planks, glass, doors, bricks and the like): it only digs natural blocks, and only when there is
no other way.

## Gathering and carrying

| Say | Tool | Notes |
|---|---|---|
| "get me ten wood", "mine 10 iron", "get some sand" | `collect` | Wood (any logs), stone and cobblestone, coal, iron, gold, copper, diamond, redstone, sand, leaves, dirt, or any block name. Uses the best tool it has. |
| "bring me ten wood" | `give` | If it has none it fetches first (any block it can collect), then walks to you and drops them. |
| "give me your diamonds", "give Aubri the carrots" | `give` | Walks over and drops items. |
| "put your stuff in the chest", "get bread from the chest" | `store_items`, `take_items` | Keeps its tools, armor, food, torches and bow. |
| "what do you have?" | `inventory` | |

Safety in the mines: it will not mine ore that touches lava (it says why), and it picks up the drop from a sealed pocket an ore left
behind. It digs toward a drop only right after mining ore, and only close by.

## Building

`build_shape` builds a named shape near you, with the doorway or gate facing you. It uses the wood or stone it carries most of unless you
name a material ("a stone castle"). It uses windows (glass), a door and a torch only if it carries them. **If it is short of wood or
cobblestone it gets it first**: it cuts logs and makes planks of them (and the fences and gate for a pen), or mines cobblestone with a
pickaxe, then builds. With no trees or stone within reach it says so instead of building half a house. If no material was named and the
block it carries most of is short and is not wood or cobblestone (5 dirt for a house that needs 71), it builds from wood instead; a material
you name is never swapped, so "build it of dirt" still says how much dirt is missing.

| Shape | Also called | Default size | Notes |
|---|---|---|---|
| house | cabin, hut, home, cottage, shelter | 5 x 5, walls 3 high | Gable roof when it stays within reach, a door, windows, a torch inside. |
| platform | floor, deck, foundation, patio | 7 x 7 | |
| pyramid | mound | 9 x 9 | Up to 11 wide (6 layers: as high as it can reach from the ground). |
| castle | fort, fortress, keep, stronghold | 13 x 13 | Curtain wall, a tower at each corner, battlements, a 3-wide gate, a torch. |
| pen | corral, paddock, enclosure | 7 x 7 | One layer of fence with a fence gate, facing across its side. For animals. |

"Keep building where you left off" (shape `continue`) finishes the last build: blocks already standing are left alone, the rest are
placed. Nothing is built on top of an existing build, and a spot must be flat and clear.

Reach: Vertex places blocks from the ground, so a build is at most about five blocks high.

### Free-form building (advanced, off by default)

`build_script` (switch `AdvancedBuildTools` on the brain) lets the language model design something itself as a list of commands,
coordinates relative to the build's corner (x east, y up from the ground, z south):

```
fill x1 y1 z1 x2 y2 z2 MATERIAL      hollow x1 y1 z1 x2 y2 z2 MATERIAL     walls x1 y1 z1 x2 y2 z2 MATERIAL
floor x1 z1 x2 z2 y MATERIAL         roof flat|gable|pyramid x1 z1 x2 z2 y MATERIAL     line x1 y1 z1 x2 y2 z2 MATERIAL
block x y z MATERIAL                 door x y z north|south|east|west     gate x y z north|south|east|west
torch x y z                          clear x1 y1 z1 x2 y2 z2
```

A plan is checked first (size limit, height, materials it carries, nothing floating) and anything wrong is refused with a message
the model can read. It is off by default because an 8B model writes coordinates much less reliably than it picks a named shape; the
named shapes are tested block for block, and adding more of them is the safer way to grow.

## Farming and animals

| Say | Tool | Needs | Notes |
|---|---|---|---|
| "make a wheat farm", "plant carrots" | `farm` (plant) | seeds, carrots or potatoes; a hoe (it crafts a wooden one if it can); a water bucket for a proper field | Tills, waters (a source in the middle keeps the soil wet) and sows a 3 to 9 wide field, one seed per block. Without a bucket it says the soil may dry out. |
| "harvest the crops" | `farm` (harvest) | | Picks ripe crops and replants them. |
| "breed the cows" | `animals` (breed) | two adults and their food (wheat for cows and sheep, carrots for pigs, seeds for chickens...) | Feeds both, waits for the baby. Works across a fence. |
| "shear the sheep" | `animals` (shear) | shears | |
| "milk the cow" | `animals` (milk) | a bucket | |

## Trading

"What does the villager sell?", "sell all my wheat", "buy some bread" (`trade`): walks to the nearest villager or wandering trader,
opens the trades, and does them with real container clicks. With no arguments it lists the offers.

## Fishing

"Let's go fishing" (`fish`): needs a fishing rod. It finds open water within 16 blocks, stands at the edge facing it, casts (adjusting
the cast if the bobber lands on land), waits up to about 90 seconds for the bite, reels in, and reports what it caught.

## Fighting and safety

| Say | Tool | Notes |
|---|---|---|
| "protect me", "guard me" | `guard` | Fights monsters within 10 blocks of you, never chasing far from you; backs off when hurt but keeps following. With a bow and arrows it shoots creepers and skeletons from range. |
| "fight the zombies" | `attack` | Melee, with the best weapon it has. |
| "shoot that zombie" | `shoot` | Needs a bow and arrows. It draws, aims with the arrow's real flight and leads a moving target; closer than five blocks it uses melee. |
| "put torches around", "it is too dark" | `light_up` | Torches on a grid. |
| "eat something" | `eat` | Also automatic when hungry. |
| "smelt the iron", "make a pickaxe" | `smelt`, `craft` | Real furnace and crafting-table clicks. `craft` gets anything from nothing (`ObtainAsync`): it reads the game's recipes and gathers, digs with the pickaxe it takes, hunts, smelts and makes the tools on the way, and says what stopped it. |
| "go to bed" | `sleep` | Sleeps in the nearest bed. |

Built in, without being asked:
- A **shield** in the inventory goes to the off hand for any fight, and is raised while an enemy approaches and between swings.
- A creeper whose **fuse is lit** is hit once and then run from until the fuse stops (and again if it follows).
- It **backs off at three hearts** instead of fighting on.
- **Reflexes** run without waiting for the language model: eating when hungry and idle, getting away from a lit creeper, coming up for air under
  water (straight up if the water is open; under a roof, to the nearest air he can swim to, and with none in reach, up through the roof), wearing better armor
  as soon as it has it, and fight-or-flight when a mob hurts it (a small trained policy, with a hand-written rule behind it).
- **A bow and arrows** are on his list after stone tools (string from spiders, arrows made from flint, a stick and a feather). With them, shooting is one of the things he chooses between in danger (from 6 blocks and with 3 hearts or more), and he finds out
  what it is worth the same way as the others: from what comes of it.
- **Other people's buildings:** he leaves them standing. A block within 3 of anything built that he did not place himself (planks, doors, glass, stairs, torches and the like) is not collected and not dug through on the way anywhere. That holds for villages too. What he placed
  himself he remembers only until he restarts, so after a restart his own work counts as someone else's.
- **After a death with tools on him** he goes back for his things first, within the 4.5 minutes they are sure to last, digging down to them if he must.
- **Heat** (a server temperature plugin shown in the action bar): a high number alone means nothing to him. He believes heat is dangerous from the first time it hurts him, and stops after 20 seconds of high readings with no hurt. While he believes it,
  he learns what each biome reads by being in it (the middle of its last 40 readings), his paths go round biomes he knows to be too hot, at up to five times the distance, and cross when there is no way round; burning, he heads for the nearest place
  well inside a cool biome, sooner the further that is, and does not run to water for the fire. On AubsCraft (RealisticSeasons 1.0.18): temperature = season + biome + time + altitude + shelter, still shown; TJ turned heat damage off on 2026-11-04
  (`extremeHeatThreshold` 40 to 100), so none of this comes into play there unless that changes.
- **Idle safety** (added 2026-11-01 after he stood in a river among four creepers): with no job running, a hostile mob within 8 blocks makes him
  walk away from it (always away: a brave character fights one that hurt it, it does not pick fights with whatever wanders by; he heads for ground
  at least 10 blocks from the middle of all the monsters near him), and standing in water for about 2.5 seconds makes him walk to the nearest dry
  ground within 16 blocks. Each can start once per 10 seconds. These are quiet jobs: logged, never announced by voice or chat, not remembered,
  and not counted as being given something to do (a "stay here" stays in force). He also chooses no project of his own while he is in water, flying, or a
  hostile mob is within 16 blocks. "In water" is judged over the last 3.6 seconds (8 of 12 looks), not in a row, because afloat at a river's surface
  the reading flickers.
- **Landing.** If the server says he is flying while he is in survival (a plugin such as EssentialsX left /fly on for the account: his login line was
  "Set fly mode enabled", 2026-11-02), he lands first, because walking, building and the escape reflexes all assume ground: he holds sneak, which flies
  down at about 4 blocks a second (a flying player takes no fall damage in vanilla; the live case has not been seen, and the test server cannot be told he is flying, so a test shows only the descent and the end of the flight), and flight ends when his feet touch ground (in water he ends it himself). The come-up-for-air
  reflex does not hold "jump" while flying: in flight jump means up, and it had taken him from the water at spawn into the air. Not done: landing over
  lava. In creative and spectator he keeps flying.

## What he feels and learns

- **Pain.** The server's damage event and death packet say what hurt him and who did it; every hurt is a `hurt:` line in the log, and a death lesson lists them. `generic` damage with no source and a bare "died" are not vanilla: on a server with plugins, look there first.
- **Monsters, by what has worked** (`DangerLearner`, on in the app, off in tests that want the fixed rules): he picks fight, run, dig in or swim out from a belief per situation (foe, weapon, health) that every outcome moves, and the outcomes are kept in `worlds/<world>.danger.jsonl`. Never hitting a creeper, and not offering what would fail at once, stay outside the learning.
- **Getting on in the world** (`Milestones`): with nothing else to do he works up from what he has now: wooden tools, stone tools, food, a bed (it moves where he comes back to), iron tools and armor, shield, diamond pickaxe. A failed step waits while he goes on to the next.
- **Heat** (a server with RealisticSeasons): he reads his temperature off the action bar and keeps a map of his readings. He keeps working while his health holds, retreats at 5 hearts to somewhere cool, heals, and goes back.
- **Fire:** into the water, unless it keeps coming back within 20 seconds, when he leaves it for a minute.

## Looking around and trying things

TJ, 2026-11-02, after Vertex swam out into a lake and said he could not see land (the shore was behind him): "he did not think to look around ... being aware of surroundings is a large
part of existing in a world", "being able and willing to swim across water is needed", "we can't account for every situation: he needs to be able to learn and try things", and, standing
on a one-block island with water on three sides and a three-high dirt wall on the fourth, "he has no clue what to do ... when he is playing alone is the best time to try new things".

- **`look_around`** (also "what do you see?", "where is the land?"): the ground around him in the eight compass directions out to 96 blocks, from the chunks he already has: for each
  direction, where there is land, water, lava, a drop or a rise (and where the world he knows ends), then the nearest of each. It reads only the loaded terrain, like a player's view
  across it; trees are not counted as ground.
- **He is told without asking.** When it matters, one line is added to the status he is given with every reply: in the water, how deep and where the nearest land is (or that there is none
  within 96 blocks); lava within 10 blocks; a drop of 4 blocks or more within 6; a tiny patch of land. On ordinary ground nothing is added, so a normal moment costs nothing in the prompt.
- **Getting out of the water follows what he sees**, however far (40 blocks by the nearest dry spot; beyond that, the nearest land in the look, up to 96): a 56 block swim in the test.
  Swimming is allowed and wanted; "stepping away from monsters" ends on dry land, not in the middle of a lake, and says so when there is nowhere to go.
  The shore is the nearest one he can plan a way to (up to 12 are tried, a bank he cannot climb counts once for the 6 blocks round it), and one he failed to reach is not tried again for 5
  minutes. A swimmer can pull himself out onto a block level with the water, not onto one a whole block above it (vanilla's water jump reaches about 0.6), so the path finder no longer plans a
  jump out of the water's surface onto such a bank. Live 2026-11-02 he jumped at one forever: now, with a bank 3 blocks off and a level shore 12 blocks off, he swims to the shore (test, 22 s).
  While swimming his head stays put: within half a block of the next step he neither aims at it nor pushes toward it (it had swung the head up to 180 degrees every 14 ticks, 76 times in a
  13 block swim; now 0).
- **Stranded on a tiny patch** (fewer than 12 blocks he can walk to: an island, a pillar, a pit): idle for 3 seconds, he looks, picks the nearest other land, and TRIES to get there with
  everything he can do (swim, dig through soft blocks, climb), a few candidates, nearest first. What he did and whether it worked is written to his memory as a LESSON ("I was stuck on a
  tiny patch of land (1 block)... I got off by going N to land 2 blocks away, digging through 2 blocks. That works."), where the recall tool can find it. The first two tries are quiet; a
  third becomes an ordinary job he talks about.
- **Trying new things alone** (with "Play on his own" on): when it is time to choose a project of his own, he first checks for an experiment he has no lesson for yet and can set up right here. The
  first one is **swimming across a pond**: water 6 to 30 blocks wide within 24 blocks, a shore he can see on the far side, no lava on the way. He swims over, and the lesson ("I tried swimming
  across 12 blocks of water to the S shore. It worked, in 24 seconds, and it cost me no health.") is stored with the tags `experiment`, `exp:swim-across` and `worked` or `failed`. He looks
  for that tag (an exact lookup, not a guess from similar words) before an experiment and does not repeat one he has a lesson for, on this server, across restarts.
- **Learning from what happens to him** (`ExperienceLog`, from 2026-11-03, after phantoms killed him 70 seconds after god mode went off). Once a second he records where he is, what is near, what he is doing,
  the time, the weather and his health. A **death** becomes a lesson in his own words, from facts he saw only ("I died: HereticSpawn was slain by Phantom. It was night, at x y z. In the last 14 seconds my
  health went from 10 hearts to none. Near me: 3 phantoms. I was doing: stepping away from monsters. I had not taken cover. I had a weapon. I had food."); a **big loss he got through** (4 hearts or more
  in a few seconds, then 5 seconds with no further drop) becomes one too. They are stored with tags (`death`, `night`, `no-cover`, `death:phantom`...). They are used in two ways: the last three go into
  the prompt when he chooses a project of his own ("What has hurt you before... plan so it does not happen again"), and **how early he takes cover is learned**: he digs in at 5 hearts, and 1 heart higher
  for each death in the last day in which he had not dug in, up to 8 hearts (the log says "learned from 1 death today without digging in: I now take cover at 6 hearts"). This is a first step, not
  machine learning yet: the next ones are choosing between fight, flight, eating and digging in from the logged outcomes, and training the fight-or-flight network on real episodes.
- **Using what he learned.** Stranded on a tiny patch, he asks his memory BEFORE trying: the lessons for that situation (tag `exp:tiny-patch`) carry the ways that worked or failed (`worked:swim`,
  `failed:dig`; ways are swim, dig, climb). Each candidate shore is planned without moving, and the order of tries moves toward a way that worked and away from one that failed; with no earlier
  lessons it stays nearest first. It is only the ORDER: nothing is ruled out, a way that failed is still tried if the others do not work. When the order changed he says why in the lesson
  ("I remembered that swimming worked before, so I tried that first."). Tested on an island with 2 blocks of dirt wall to the north and 6 blocks of water to a shore in the south: no lessons,
  north (digging); a lesson that digging failed and one that swimming worked, south (swimming). Not done yet: lessons choosing among other predicaments (only the tiny patch asks), and more
  experiments than the pond.

## Remembering

"Remember that I love pink axolotls" (`remember`), "your goal is to find a village" (`set_goal`, worked on whenever nobody needs it,
`finish_goal` to end it), "be Pixel" (`switch_character`). Things it learns about each person are kept per person.

**His own log (long-term memory).** Every job he finishes or fails (not a reflex's small job) is written to a log on this PC with what he did, the
result, where (coordinates) and which server, and for whom when it was for a player. Ask about the past ("what did you build earlier?", "did you ever
find a village?") and the `recall` tool looks in the log by meaning and he answers from what it says, in the first person, with how long ago. It
is the Memory switch on the Minecraft tab. Measured 2026-11-02 on an RTX 2060: recording a memory takes 36 ms and a lookup 8-9 ms once warm (first
use 280 ms and 242 ms). He answered from it in 5 of 5 tries with the real model, and without a log he did not know (control). What is shown is cut at a
similarity of 0.30 (set from 16 test questions: questions about what was stored scored 0.351 to 0.753, small talk 0.084 to 0.240). The People tab lists
everything he remembers and deletes one entry or all of them for good (files rewritten at once; tested with real files; the page itself was only checked to render, no click test).
Not done yet: episodes from what is said to him, lessons from failed goals, deleting everything about one person in one go.
