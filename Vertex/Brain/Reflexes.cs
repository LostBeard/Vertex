namespace Vertex.Brain;

/// <summary>
/// Vertex's "System One": fast decisions every few ticks that never wait for the language model (TJ: "fast
/// decision making is important in the game"). Eat when hungry, fight back or run when hurt, come up for air.
/// Hand-written rules for now; this is the layer a small learned policy (SpawnDev.ILGPU.ML) can replace.
/// </summary>
public sealed class Reflexes : IDisposable
{
    private readonly VertexBrain _brain;
    private float _lastHealth = 20;
    private int _tick;
    private bool _busy;

    public bool Enabled { get; set; } = true;

    public Reflexes(VertexBrain brain)
    {
        _brain = brain;
        brain.Client.Tick += OnTick;
    }

    private void OnTick()
    {
        if (!Enabled || _busy || ++_tick % 5 != 0) return;
        var c = _brain.Client;
        var hurt = c.Health < _lastHealth - 0.01f;
        _lastHealth = c.Health;

        // Come up for air: under water and not on a job that is steering.
        var body = c.BodySnapshot();
        if (body.InWater && body.WaterHeight > 1.7 && !_brain.Tasks.IsBusy) c.Controls.Jump = true;
        else if (!_brain.Tasks.IsBusy && c.Controls.Jump && !body.InWater) c.Controls.Jump = false;

        // Eat when hungry and idle (eating mid-dig would cancel the dig).
        if (c.Food <= 14 && !_brain.Tasks.IsBusy && _brain.Skills.HasFood)
        {
            Run(async () => await _brain.Skills.EatAsync(CancellationToken.None));
            return;
        }

        // Hit by a mob: brave characters fight it, careful ones back away from it.
        if (hurt)
        {
            var (x, y, z) = c.Position;
            var attacker = c.Entities.Values.Where(e => Skills.Hostile.Contains(e.Name) && e.DistanceTo(x, y, z) < 6).OrderBy(e => e.DistanceTo(x, y, z)).FirstOrDefault();
            if (attacker == null) return;
            if (_brain.Character.Style.Bravery >= 0.5 && c.Health > 8)
                _brain.Tasks.Start($"fighting a {attacker.Name.Replace("minecraft:", "")} that attacked me", ct => _brain.Skills.AttackAsync(attacker.Name.Replace("minecraft:", ""), ct));
            else
                _brain.Tasks.Start("running away from a monster", async ct =>
                {
                    var (px, py, pz) = c.Position;
                    double dx = px - attacker.X, dz = pz - attacker.Z;
                    var len = Math.Max(0.1, Math.Sqrt(dx * dx + dz * dz));
                    var ok = await c.GoToAsync(SpawnDev.Minecraft.Pathing.Goal.Near(px + dx / len * 12, py, pz + dz / len * 12, 3), null, ct);
                    return (ok, ok ? "I got away from the monster." : "I couldn't get away from the monster!");
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

    public void Dispose() => _brain.Client.Tick -= OnTick;
}
