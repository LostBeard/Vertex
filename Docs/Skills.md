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
| "go explore" | `explore` | Walks out, comes back and tells you what it found; villages become places it remembers. |

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
pickaxe, then builds. With no trees or stone within reach it says so instead of building half a house.

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
| "smelt the iron", "make a pickaxe" | `smelt`, `craft` | Real furnace and crafting-table clicks; missing parts are made first. |
| "go to bed" | `sleep` | Sleeps in the nearest bed. |

Built in, without being asked:
- A **shield** in the inventory goes to the off hand for any fight, and is raised while an enemy approaches and between swings.
- A creeper whose **fuse is lit** is hit once and then run from until the fuse stops (and again if it follows).
- It **backs off at three hearts** instead of fighting on.
- **Reflexes** run without waiting for the language model: eating when hungry and idle, getting away from a lit creeper, coming up for air under
  water, wearing better armor as soon as it has it, and fight-or-flight when a mob hurts it (a small trained policy, with a hand-written rule behind it).

## Remembering

"Remember that I love pink axolotls" (`remember`), "your goal is to find a village" (`set_goal`, worked on whenever nobody needs it,
`finish_goal` to end it), "be Pixel" (`switch_character`). Things it learns about each person are kept per person.
