using SpawnDev.Minecraft.Data;
using SpawnDev.Minecraft.Pathing;
using SpawnDev.Minecraft.Protocol;
using CB = SpawnDev.Minecraft.Protocol.PacketIds;

namespace SpawnDev.Minecraft;

/// <summary>What touching a block taught us: its collision box (none = nothing to bump into), and which faces we measured.</summary>
/// <summary>What touching a block taught us: its collision box (none = nothing to bump into), whether it holds you once
/// you are in it (a cobweb), and how many faces we measured.</summary>
public sealed record BlockProbe(int RawState, Box[] Shape, int FacesMeasured, bool Sticky = false);

// Learning a block's shape by touch, with the server as the referee. Vanilla's server re-runs every move with real
// collisions (ServerGamePacketListenerImpl.handleMovePlayer: player.move, then isPlayerCollidingWithAnythingNew) and
// sends a correction when our body would overlap a block. So a move it accepts says "your body fits there", and
// moving in small steps toward a block finds where it starts.
public sealed partial class MinecraftClient
{
    /// <summary>How long to wait for the server to answer a probe (it corrects a bad move as it handles it).</summary>
    public int ProbeTicks { get; set; } = 3;

    /// <summary>
    /// Sends one move to x,y,z (physics must be paused) and watches for a correction. True: accepted, we are there
    /// now. False: the server put us back (the move would overlap a block, or was not a move it would make).
    /// </summary>
    public async Task<bool> ProbeMoveAsync(double x, double y, double z, bool onGround, CancellationToken ct = default)
    {
        var before = TeleportCount;
        using (var move = new PacketWriter(CB.Play.Serverbound.MovePlayerPos))
        {
            move.WriteDouble(x).WriteDouble(y).WriteDouble(z).WriteByte(onGround ? (byte)1 : (byte)0);
            lock (_positionLock)
            {
                _body.X = x; _body.Y = y; _body.Z = z;
                _body.VelX = _body.VelY = _body.VelZ = 0;
                _body.OnGround = onGround;
            }
            (_sentX, _sentY, _sentZ, _sentOnGround) = (x, y, z, onGround);
            await SendAsync(move, ct);
        }
        await WaitTicksAsync(ProbeTicks, ct);
        return TeleportCount == before;
    }

    /// <summary>
    /// Measures the collision box of the block at bx,by,bz by touch: from each side we can stand on (level ground,
    /// room for our body, the block above the target clear), moves toward it in a binary search along the line
    /// through its middle; then over its middle from above for its height. Single-box approximation (a fence or
    /// stair comes out as its bounding box). Faces we could not reach stay at the edge of the block (careful, not
    /// optimistic). Null when there is nowhere to stand next to it.
    /// </summary>
    public async Task<BlockProbe?> MeasureBlockAsync(int bx, int by, int bz, CancellationToken ct = default)
    {
        var raw = World.GetRawState(bx, by, bz);
        if (raw < 0 || !Clear(bx, by + 1, bz)) return null;
        var sides = new[] { BlockFace.West, BlockFace.East, BlockFace.North, BlockFace.South }
            .Where(f => { var (sx, _, sz) = Offset((bx, by, bz), f); return Standable(sx, by, sz); }).ToList();
        if (sides.Count == 0) return null;

        double minX = 0, maxX = 1, minZ = 0, maxZ = 1;
        bool hitX = false, hitZ = false, clearX = false, clearZ = false;
        var measured = 0;
        double top = 1;
        var physics = PhysicsEnabled;
        try
        {
            foreach (var side in sides)
            {
                var (sx, _, sz) = Offset((bx, by, bz), side);
                if (!await StandAtAsync(sx, by, sz, ct)) continue;
                // Moving from the side cell's middle toward the target: dx/dz is the direction of travel.
                var (dx, dz) = (bx - sx, bz - sz);
                double cx = sx + 0.5, cz = sz + 0.5;
                // The body's leading edge goes from the shared face (0.3 in from the middle) to the far side of the target.
                double lo = 0, hi = 1.2;
                if (await ProbeMoveAsync(cx + dx * hi, by, cz + dz * hi, true, ct))
                {
                    // Nothing to bump into on this line. But a block can hold you once you are in it (a cobweb: the
                    // server slows the body to a fraction, so a second move inside it comes out short and is corrected).
                    if (!await ProbeMoveAsync(cx + dx * (hi - 0.4), by, cz + dz * (hi - 0.4), true, ct))
                    {
                        await BackOutAsync(cx, by, cz, ct);
                        return new BlockProbe(raw, [], measured + 1, Sticky: true);
                    }
                    if (dx != 0) clearX = true; else clearZ = true;
                    measured++;
                }
                else
                {
                    for (var i = 0; i < 7; i++)
                    {
                        var mid = (lo + hi) / 2;
                        if (await ProbeMoveAsync(cx + dx * mid, by, cz + dz * mid, true, ct)) lo = mid; else hi = mid;
                    }
                    // The leading edge (middle + 0.3) at the last accepted point is the block's near face, measured
                    // in block coordinates from the shared face (0.5 from the side cell's middle).
                    var face = Math.Round((lo + 0.3 - 0.5) * 32) / 32; // distance into the target block, 1/32 resolution
                    switch (side)
                    {
                        case BlockFace.West: minX = Math.Max(minX, face); hitX = true; break;
                        case BlockFace.East: maxX = Math.Min(maxX, 1 - face); hitX = true; break;
                        case BlockFace.North: minZ = Math.Max(minZ, face); hitZ = true; break;
                        default: maxZ = Math.Min(maxZ, 1 - face); hitZ = true; break;
                    }
                    measured++;
                }
                await ProbeMoveAsync(cx, by, cz, true, ct); // back to the middle of the side cell
            }
            if (!hitX && !hitZ && (clearX || clearZ))
                return new BlockProbe(raw, [], measured); // walked through it from every side we tried

            // Height: lift up over the side cell, then over the target's middle; the lowest height that is accepted.
            if (Clear(bx, by + 2, bz) && sides.Count > 0)
            {
                var (sx, _, sz) = Offset((bx, by, bz), sides[0]);
                if (Clear(sx, by + 2, sz))
                {
                    double lo = 0, hi = 1.0;
                    for (var i = 0; i < 7; i++)
                    {
                        var mid = (lo + hi) / 2;
                        var fits = await ProbeMoveAsync(sx + 0.5, by + mid, sz + 0.5, false, ct)
                            && await ProbeMoveAsync(bx + 0.5, by + mid, bz + 0.5, false, ct);
                        if (fits) hi = mid; else lo = mid;
                        await ProbeMoveAsync(sx + 0.5, by + mid, sz + 0.5, false, ct);
                        await ProbeMoveAsync(sx + 0.5, by, sz + 0.5, true, ct);
                    }
                    top = Math.Round(hi * 64) / 64;
                    // Free above its middle at floor level: the box is off to one side (a door's plate), full height.
                    if (top <= 1.0 / 64) top = 1;
                    measured++;
                }
            }
            if (!hitX) (minX, maxX) = (0, 1);
            if (!hitZ) (minZ, maxZ) = (0, 1);
            return new BlockProbe(raw, [new Box(minX, 0, minZ, maxX, top, maxZ)], measured);
        }
        finally
        {
            lock (_positionLock) { _body.VelX = _body.VelY = _body.VelZ = 0; }
            PhysicsEnabled = physics;
        }
    }

    /// <summary>Out of a sticky block in steps short enough that the slowed body still reaches each one.</summary>
    private async Task BackOutAsync(double x, double y, double z, CancellationToken ct)
    {
        for (var i = 0; i < 40; i++)
        {
            var (px, _, pz) = Position;
            var (dx, dz) = (x - px, z - pz);
            var d = Math.Sqrt(dx * dx + dz * dz);
            if (d < 0.01) return;
            var step = Math.Min(d, 0.2);
            await ProbeMoveAsync(px + dx / d * step, y, pz + dz / d * step, true, ct);
        }
    }

    private bool Clear(int x, int y, int z)
    {
        var s = World.GetState(x, y, z);
        return s >= 0 && !World.Data.HasCollision(s);
    }

    /// <summary>A cell we can stand in: room for the body, a full solid block under it.</summary>
    private bool Standable(int x, int y, int z)
    {
        var below = World.GetState(x, y - 1, z);
        return Clear(x, y, z) && Clear(x, y + 1, z) && below > 0 && !World.Data.IsUnknownBlock(below)
            && World.Data.CollisionBoxes(below).Any(b => b.MaxY >= 1.0);
    }

    /// <summary>Walks to a cell, then pauses physics and stands exactly in its middle (the probes start there).</summary>
    private async Task<bool> StandAtAsync(int x, int y, int z, CancellationToken ct)
    {
        PhysicsEnabled = true;
        if (FeetBlock != (x, y, z) && !await GoToAsync(Goal.Block(x, y, z), new PathOptions { MaxNodes = 10_000 }, ct)) return false;
        await WaitUntilAsync(() => OnGround, 20, ct);
        PhysicsEnabled = false;
        return await ProbeMoveAsync(x + 0.5, y, z + 0.5, true, ct);
    }
}
