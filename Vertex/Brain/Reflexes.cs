using SpawnDev.Minecraft;

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
        if (body.InWater && body.WaterHeight > 1.7 && !_tasks.IsBusy) c.Controls.Jump = true;
        else if (!_tasks.IsBusy && c.Controls.Jump && !body.InWater) c.Controls.Jump = false;

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
            var features = ReflexFeatures.From(c, attacker);
            var bravery = _style().Bravery;
            Run(async () =>
            {
                var action = await Policy.DecideAsync(features, bravery);
                if (action == ReflexAction.Fight)
                    _tasks.Start($"fighting a {attacker.Name.Bare()} that attacked me", ct => _skills.AttackAsync(attacker.Name.Bare(), ct));
                else
                    _tasks.Start("running away from a monster", ct => _skills.FleeFromAsync(attacker, ct));
            });
        }
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
