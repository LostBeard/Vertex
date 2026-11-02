using SpawnDev.Minecraft;
using SpawnDev.Minecraft.Entities;

namespace Vertex.Brain;

/// <summary>
/// Vertex's "System One": fast decisions every few ticks that never wait for the language model (TJ: "fast
/// decision making is important in the game"). Eat when hungry, fight back or run when hurt, come up for air.
/// Fight or flight when a mob hurts us goes through a policy: the hand-written rule, or a small network trained
/// on real fights (LearnedReflexPolicy, SpawnDev.ILGPU.ML) when one ships.
/// </summary>
public sealed class Reflexes : IDisposable
{
    private readonly MinecraftClient _client;
    private readonly Skills _skills;
    private readonly TaskRunner _tasks;
    private readonly Func<PlayStyle> _style;
    private float _lastHealth = 20;
    private int _tick;
    private bool _busy;
    private int _escaping = -1;
    private int _waterHistory;
    private long _nextAvoidTick, _nextLeaveWaterTick, _nextLandTick, _nextPatchTick;
    private int _tinyPatchLooks, _patchFailures;

    /// <summary>How close a monster may be, idle, before he walks away from it (blocks).</summary>
    public const double AvoidWithin = 8;

    public bool Enabled { get; set; } = true;

    /// <summary>Decides fight or flight when hurt (the hand-written rule unless a trained policy is set).</summary>
    public IReflexPolicy Policy { get; set; } = RuleReflexPolicy.Instance;

    public Reflexes(VertexBrain brain) : this(brain.Client, brain.Skills, brain.Tasks, () => brain.Character.Style) { }

    /// <summary>Reflexes without a language model (tests, or a body with no brain).</summary>
    public Reflexes(MinecraftClient client, Skills skills, TaskRunner tasks, Func<PlayStyle> style)
    {
        _client = client;
        _skills = skills;
        _tasks = tasks;
        _style = style;
        client.Tick += OnTick;
    }

    private void OnTick()
    {
        if (!Enabled || ++_tick % 2 != 0) return;
        var c = _client;

        // A creeper about to explode beats everything: run now (checked every 2 ticks; the fuse is 30), then go
        // back to whatever we were doing.
        if (_escaping < 0 && _skills.LitCreeper() is { } creeper)
        {
            var id = _escaping = creeper.Id;
            _tasks.Interrupt("getting away from a creeper", async ct =>
            {
                try { return await _skills.EscapeCreeperAsync(id, ct); }
                finally { _escaping = -1; }
            });
            return;
        }
        if (_busy || _tick % 6 != 0) return;
        var hurt = c.Health < _lastHealth - 0.01f;
        _lastHealth = c.Health;

        // Come up for air: under water and not on a job that is steering.
        var body = c.BodySnapshot();
        // (Not while flying: there "jump" is "up", and it carried him 24 blocks into the air on 2026-11-02.)
        if (body.InWater && body.WaterHeight > 1.7 && !_tasks.IsBusy && !c.Flying) c.Controls.Jump = true;
        else if (!_tasks.IsBusy && c.Controls.Jump && (!body.InWater || c.Flying)) c.Controls.Jump = false;

        // Flying in survival (a plugin left /fly on): nothing he does works in the air, so land first.
        if (c.Flying && c.GameMode == GameMode.Survival && !_tasks.IsBusy && _tick >= _nextLandTick)
        {
            _nextLandTick = _tick + 100;
            _tasks.StartReflex("landing", ct => _skills.LandAsync(ct));
            return;
        }

        // Idle with a monster close: do not stand there (live on AubsCraft, 2026-11-01, he stood among four creepers). Creepers are always walked away from; any other monster gets the
        // same fight-or-flight rule as one that hurt us. Not while a job runs (jobs have their own care), and at most once every 10 seconds.
        if (!_tasks.IsBusy && _tick >= _nextAvoidTick && _skills.NearestHostile(AvoidWithin) != null)
        {
            _nextAvoidTick = _tick + 200;
            _tasks.StartReflex("stepping away from monsters", ct => _skills.StepAwayFromMonstersAsync(AvoidWithin + 2, ct));
            return;
        }

        // Standing in water with nothing to do: get out of it.
        // Counted over the last 12 looks (3.6 s), not in a row: afloat at the surface of a river the in-water reading flickers as he bobs, and a count that restarts at every flicker
        // never reaches its goal.
        _waterHistory = ((_waterHistory << 1) | (body.InWater ? 1 : 0)) & 0xFFF;
        if (!_tasks.IsBusy && int.PopCount(_waterHistory) >= 8 && _tick >= _nextLeaveWaterTick)
        {
            _nextLeaveWaterTick = _tick + 200;
            _tasks.StartReflex("getting out of the water", ct => _skills.LeaveWaterAsync(ct));
            return;
        }

        // Stranded on a tiny patch (an island, a pillar, a pit): not a case anyone wrote a rule for, so he TRIES to get off it (look around, swim, dig, climb) and what came of it becomes a lesson. First
        // two tries are quiet; if those fail, the next is an ordinary job, spoken about: being stuck is news.
        if (!_tasks.IsBusy && !c.Flying && c.OnGround && _tick % 60 == 0 && _tick >= _nextPatchTick)
        {
            _tinyPatchLooks = _skills.WalkablePatch(Skills.TinyPatch + 1) is { Count: > 0 and < Skills.TinyPatch } ? _tinyPatchLooks + 1 : 0;
            if (_tinyPatchLooks >= 2)   // seen twice, 3 seconds apart: a real predicament, not a step off a ledge
            {
                _tinyPatchLooks = 0;
                _nextPatchTick = _tick + 600;
                if (_patchFailures < 2) _tasks.StartReflex("getting off a tiny patch of land", async ct => { var r = await _skills.GetOffATinyPatchAsync(ct); _patchFailures = r.Item1 ? 0 : _patchFailures + 1; return r; });
                else _tasks.Start("getting off a tiny patch of land", async ct => { var r = await _skills.GetOffATinyPatchAsync(ct); _patchFailures = r.Item1 ? 0 : _patchFailures + 1; return r; });
                return;
            }
        }

        // Wear better armor as soon as we have it (idle, so clicks do not fight a crafting or chest job).
        if (!_tasks.IsBusy && _tick % 60 == 0 && c.HasBetterArmor)
        {
            Run(async () => await c.EquipBestArmorAsync(CancellationToken.None));
            return;
        }

        // Eat when hungry and idle (eating mid-dig would cancel the dig).
        if (c.Food <= 14 && !_tasks.IsBusy && _skills.HasFood)
        {
            Run(async () => await _skills.EatAsync(CancellationToken.None));
            return;
        }

        // Hit by a mob: brave characters fight it, careful ones back away from it.
        if (hurt)
        {
            var (x, y, z) = c.Position;
            var attacker = c.Entities.Values.Where(e => Skills.Hostile.Contains(e.Name) && e.DistanceTo(x, y, z) < 6).OrderBy(e => e.DistanceTo(x, y, z)).FirstOrDefault();
            if (attacker == null) return;
            Respond(attacker, "attacked me");
        }
    }

    /// <summary>
    /// A monster hurt us: always run from a creeper (hitting one makes it blow up), otherwise whatever the policy says for this character (a brave one fights back). A monster that merely
    /// wandered close is never fought: the idle reflex only steps away from it.
    /// </summary>
    private void Respond(Entity attacker, string why)
    {
        var features = ReflexFeatures.From(_client, attacker);
        var bravery = _style().Bravery;
        var flee = attacker.Name == "minecraft:creeper";
        Run(async () =>
        {
            var action = flee ? ReflexAction.Flee : await Policy.DecideAsync(features, bravery);
            if (action == ReflexAction.Fight)
                _tasks.Start($"fighting a {attacker.Name.Bare()} that {why}", ct => _skills.AttackAsync(attacker.Name.Bare(), ct));
            else
                _tasks.Start("running away from a monster", ct => _skills.FleeFromAsync(attacker, ct));
        });
    }

    private void Run(Func<Task> work)
    {
        _busy = true;
        _ = Task.Run(async () =>
        {
            try { await work(); }
            catch { /* a reflex that fails is retried on a later tick */ }
            finally { _busy = false; }
        });
    }

    public void Dispose()
    {
        _client.Tick -= OnTick;
        (Policy as IDisposable)?.Dispose();
    }
}
