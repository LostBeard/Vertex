# SpawnDev.Minecraft

A Minecraft **Java Edition client library** for .NET: it connects to a server as an ordinary player (protocol 770, Minecraft 1.21.5), keeps a model of the
world, moves with vanilla physics, and exposes the things a player can do as `async` calls. It is what Vertex plays through, and it knows nothing about
language models, so anything that wants a Minecraft body can use it. No server plugin is needed.

```csharp
var client = new MinecraftClient(GameProfile.Offline("Builder"), new ClientOptions());
await client.ConnectAsync("127.0.0.1", 25565, ct);        // login, configuration, first position

var (x, y, z) = client.Position;
await client.GoToAsync(Goal.Near(x + 20, y, z, 2), null, ct);   // A*-style pathfinding: walks, climbs, swims, jumps, opens doors
await client.DigAsync(x + 22, y, z, ct);                        // the right tool, the real dig time
await client.CraftItemAsync("minecraft:wooden_pickaxe", 1, ct); // real container clicks, sub-crafts missing parts
```

| Area | What it does |
|---|---|
| Connection | Offline and online-mode login (Microsoft accounts through a device code, signed chat), compression, encryption, configuration phase, keep-alive. |
| World | Chunks, blocks, entities and their metadata, inventory and containers, held in memory as the server sends them. Block ids and packet ids come from the vanilla server's own data reports (`tools/DataGen`), never typed by hand. |
| Body | Vanilla-accurate movement physics (what Paper's movement checks accept), swimming, climbing, sneaking, attack cooldown and critical hits, eating, bows. |
| Pathfinding | Plans over walk, jump, climb, swim, drop and dig moves; never digs blocks that look like a player's build unless told it may; a goal can be a place, a block to reach, or a distance. |
| Actions | Dig and place with the right tool and reach, build a plan of blocks bottom-up (`BuildAsync`), craft (inventory grid and crafting table), smelt, store and take items, trade with villagers (`OpenTradesAsync`, `TradeAsync`), equip armor, sleep, drop and give. |
| Mods | `MinecraftClient.Fabric`: learning a Fabric server's modded blocks (from a modpack's `.mrpack`). |

## Tests

They run against a real Paper server started locally (see [../Docs/Testing.md](../Docs/Testing.md)): nothing in the protocol, physics or actions is mocked.
The protocol tables are regenerated, never edited: `dotnet run --project ../tools/DataGen`.

## Limits

Java Edition only (not Bedrock, so not Minecraft Education Edition itself); protocol 770 / 1.21.5; the physics follows the vanilla client, so a server that changes
movement rules (a heavily modified fork) may disagree.
