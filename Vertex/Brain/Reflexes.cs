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
    private int _avoidRepeats;
    private long _nextSwatTick, _nextCoverTick, _nextRangedTick;
    private long _lastAvoidTick;
    private bool _cooling;
    private long _nextCoolTick;

    /// <summary>His temperature as the server shows it, or null when it shows none (most servers).</summary>
    public Func<double?>? Temperature { get; set; }
    /// <summary>The temperature from which he goes to cool off: starts at the plugin's first ill effect on its product page (no healing at +50), and the brain lowers it when heat is seen to hurt below that.</summary>
    public double HotAt { get; set; } = 50;
    /// <summary>How far below <see cref="HotAt"/> counts as cool.</summary>
    public const double CoolBy = 6;
    /// <summary>In the heat, the health (half hearts) at which he stops working and goes to cool off.</summary>
    public const float RetreatAtHealth = 10;
    /// <summary>The health (half hearts) he heals to before going back.</summary>
    public const float HealTo = 18;

    private long _nextFireTick, _lastFireOutTick = long.MinValue / 2;
    private int _fireReturns;
    private bool _puttingOut;

    /// <summary>How close a monster may be, idle, before he walks away from it (blocks).</summary>
    public const double AvoidWithin = 8;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Health (half hearts) at or below which, with a monster close, he takes cover underground. 10 to start; the brain raises it after deaths in which he had not dug in (<see cref="ExperienceLog.CoverBelow"/>):
    /// it is learned from what happened to him, not fixed.
    /// </summary>
    public int CoverBelow { get; set; } = 10;

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
        client.Damaged += OnDamaged;
    }

    private DateTime _lastMonsterHit = DateTime.MinValue;

    private void OnDamaged(DamageInfo damage)
    {
        var id = damage.CauseEntityId >= 0 ? damage.CauseEntityId : damage.DirectEntityId;
        if (id >= 0 && _client.Entities.TryGetValue(id, out var who) && Skills.Hostile.Contains(who.Name)) _lastMonsterHit = DateTime.UtcNow;
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
            }, quiet: Learner != null);
            return;
        }
        if (_busy || _tick % 6 != 0) return;
        var hurt = c.Health < _lastHealth - 0.01f;
        _lastHealth = c.Health;

        // Too hot (a server's temperature plugin): the heat takes a heart every 6 seconds or so and he has work to do in it, as a player does: he stays at it while his health holds, then goes where it is cool, heals, and comes back
        // to what he was doing. Going at the first high reading he would never reach a tree on a hot island; staying in the water beside it he burned down to nothing (2026-11-03).
        if (!_cooling && _tick >= _nextCoolTick && Temperature?.Invoke() is double hot && hot >= HotAt && c.Health <= RetreatAtHealth && !HandlesMonsters(_tasks.Current))
        {
            _cooling = true;
            var target = HotAt - CoolBy;
            Decided?.Invoke($"too hot ({hot:0.#} degrees) and down to {c.Health / 2:0.#} hearts: going where it is under {target:0.#} to heal");
            _tasks.Interrupt("cooling off", async ct =>
            {
                try { return await _skills.CoolOffAsync(() => Temperature?.Invoke(), target, HealTo, ct); }
                finally { _cooling = false; _nextCoolTick = _tick + 100; }
            }, quiet: true);
            return;
        }

        // On fire: into the water, whatever he was doing (he goes back to it after).
        // Unless water does not help: a server plugin can set him alight from his temperature, over and over (RealisticSeasons at +65 C). Live 2026-11-03 this reflex then restarted his job every few seconds, so he
        // never reached a tree. Alight again within 20 seconds of the water, twice: he stops running to it for a minute and gets on with things.
        if (c.OnFire && !_puttingOut && _tick >= _nextFireTick && _skills.NearestWater(24) != null)
        {
            _fireReturns = _tick - _lastFireOutTick < 400 ? _fireReturns + 1 : 0;
            if (_fireReturns >= 2)
            {
                _fireReturns = 0;
                _nextFireTick = _tick + 1200;
                Decided?.Invoke("the fire keeps coming back after the water: leaving it for a minute");
            }
            else
            {
                _nextFireTick = _tick + 40;
                _puttingOut = true;
                _tasks.Interrupt("putting out the fire", async ct =>
                {
                    try { return await _skills.PutOutFireAsync(ct); }
                    finally { _puttingOut = false; _lastFireOutTick = _tick; }
                }, quiet: true);
                return;
            }
        }

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

        // Monsters, by what has worked for him. When this is on it replaces the fixed rungs below (dig in at so many hearts, charge archers, step away, fight back).
        if (Learner is { } learner && c.GameMode == GameMode.Survival)
        {
            CloseEpisodeIfDue();
            if (TryDecideDanger(learner, hurt)) return;
        }

        // Losing: five hearts or fewer with a monster close (a flier counts: they killed him in 70 seconds): dig in and wait it out. He fights or flees on the ordinary rules until then.
        if (Learner == null && !_tasks.IsBusy && c.Health <= CoverBelow && c.GameMode == GameMode.Survival && _tick >= _nextCoverTick && (_skills.NearestHostile(10) != null || _skills.NearestFlier(10) != null))
        {
            _nextCoverTick = _tick + 600;
            _tasks.Start("taking cover underground", ct => _skills.TakeCoverAsync(ct));
            return;
        }

        // Shot at from a distance (skeletons: TJ, 2026-11-03): walking away in the open gives the archer a clear shot, so he either closes in and hits it (with a weapon and the health for it) or gets out of
        // its sight underground. Checked every 5 seconds; not while a job runs.
        if (Learner == null && !_tasks.IsBusy && _tick >= _nextRangedTick && c.GameMode == GameMode.Survival && _skills.NearestRanged(26) is { } shooter)
        {
            _nextRangedTick = _tick + 100;
            var kind = shooter.Name.Bare();
            if (_skills.HasWeapon && c.Health >= 10)
                _tasks.Start($"fighting the {kind} that is shooting at me", ct => _skills.AttackAsync(kind, ct));
            else if (_tick >= _nextCoverTick)
            {
                _nextCoverTick = _tick + 600;
                _tasks.Start("taking cover from an archer", ct => _skills.TakeCoverAsync(ct));
            }
            return;
        }

        // A phantom (or other flier) that comes down within reach: hit it. Walking away from it gets nowhere, and he stood there while they bit him (TJ, 2026-11-03: "birds attacking him and it does not
        // bother him"). Not while a job runs (jobs have their own care), a swing every 0.7 seconds with the best weapon he carries.
        if (!_tasks.IsBusy && _tick >= _nextSwatTick && _skills.NearestFlier(3.3) is { } bird)
        {
            _nextSwatTick = _tick + 14;
            Run(async () => { await _skills.EquipWeaponAsync(CancellationToken.None); await c.AttackAsync(bird); });
            return;
        }

        // Idle with a monster close: do not stand there (live on AubsCraft, 2026-11-01, he stood among four creepers). Creepers are always walked away from; any other monster gets the
        // same fight-or-flight rule as one that hurt us. Not while a job runs (jobs have their own care), and at most once every 10 seconds.
        if (Learner == null && !_tasks.IsBusy && _tick >= _nextAvoidTick && _skills.NearestHostile(AvoidWithin) is { } threat)
        {
            // Stepping away that keeps being needed is not working: live 2026-11-03 15:48 to 15:52 and beyond, a spider and three zombies at 1 to 2 blocks, "could not get away from the monsters"
            // every 10 seconds, nothing ever fought back, and he went round in circles. A third time in a minute, he turns on them (creepers are never fought: they blow up).
            if (_tick - _lastAvoidTick > 1200) _avoidRepeats = 0;
            _lastAvoidTick = _tick;
            _nextAvoidTick = _tick + 200;
            if (++_avoidRepeats >= 3 && threat.Name != "minecraft:creeper")
            {
                _avoidRepeats = 0;
                var kind = threat.Name.Bare();
                _tasks.Start($"fighting the {kind.Replace('_', ' ')} that kept following me", ct => _skills.AttackAsync(kind, ct));
                return;
            }
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
        if (hurt && _tasks.Current is { } doing && doing.StartsWith("taking cover")) hurt = false;      // digging in is the answer to being hurt: a fight started now would cancel it half way down (the archer test)
        if (hurt && Learner == null)
        {
            var (x, y, z) = c.Position;
            // On foot only: a phantom's swoop is neither something to run from nor, from the ground, something he can chase (the fight skill walks to its target, and one in the air cannot be walked to).
            var attacker = c.Entities.Values.Where(e => Skills.IsThreatOnFoot(e.Name) && e.DistanceTo(x, y, z) < 6).OrderBy(e => e.DistanceTo(x, y, z)).FirstOrDefault();
            if (attacker == null) return;
            Respond(attacker, "attacked me");
        }
    }

    // ---------------------------------------------------------------- monsters, by what has worked (DangerLearner)

    /// <summary>When set, what he does about monsters is chosen by this from what has happened to him, instead of the fixed rungs. Null: the fixed rungs (no learning, deterministic).</summary>
    public DangerLearner? Learner { get; set; }

    /// <summary>A choice made ("what, against what, the odds"), for the log.</summary>
    public event Action<string>? Decided;

    private sealed class Episode
    {
        public required DangerSituation Situation;
        public required DangerAction Action;
        public required float HealthBefore;
        public required DateTime Started;
        public DateTime? JobEnded;
        public bool JobWorked = true;
    }

    private Episode? _episode;
    private readonly object _episodeLock = new();
    private bool _dangerJob;
    private long _nextDangerTick;
    private readonly Dictionary<DangerAction, long> _armBlockedUntil = [];

    /// <summary>Jobs that deal with monsters themselves: hurting him during those is no reason to stop them.</summary>
    private static bool HandlesMonsters(string? job) => job != null && (job.Contains("fight", StringComparison.OrdinalIgnoreCase) || job.Contains("guard", StringComparison.OrdinalIgnoreCase)
        || job.Contains("protect", StringComparison.OrdinalIgnoreCase) || job.Contains("shoot", StringComparison.OrdinalIgnoreCase) || job.Contains("attack", StringComparison.OrdinalIgnoreCase)
        || job.Contains("fire") || job.Contains("creeper"));

    /// <summary>Is there something to decide about, and if so decide and start it. True when a job was started.</summary>
    private bool TryDecideDanger(DangerLearner learner, bool hurt)
    {
        if (_dangerJob || _tick < _nextDangerTick) return false;
        var c = _client;
        var (x, y, z) = c.Position;
        var close = c.Entities.Values.Where(e => Skills.IsThreatOnFoot(e.Name) && e.DistanceTo(x, y, z) < 12).OrderBy(e => e.DistanceTo(x, y, z)).ToList();
        var archer = _skills.NearestRanged(26);
        var melee = close.FirstOrDefault(e => e.DistanceTo(x, y, z) < AvoidWithin);
        if (archer == null && melee == null) return false;

        // On another job: only being hurt BY a monster stops it (one over there is the job's own care). Any fall in health used to count, so in the heat every tick with a mob anywhere near restarted his job, and in 8 minutes he
        // never finished chopping one tree (2026-11-04). It goes back to the job afterwards.
        var busy = _tasks.IsBusy;
        var hitByMonster = DateTime.UtcNow - _lastMonsterHit < TimeSpan.FromSeconds(2);
        if (busy && (HandlesMonsters(_tasks.Current) || !hitByMonster)) return false;

        var foe = melee != null && (archer == null || melee.DistanceTo(x, y, z) < 5) ? melee : archer!;
        var foeClass = close.Count(e => e.DistanceTo(x, y, z) < 10) >= 3 ? "swarm" : foe.Name == "minecraft:creeper" ? "creeper" : Skills.Ranged.Contains(foe.Name) ? "archer" : "melee";
        var situation = new DangerSituation(foeClass, DangerSituation.KitOf(_skills.BestWeaponName), DangerSituation.HealthOf(c.Health));

        // The shield: what is never worth learning by dying. Never hit a creeper (it blows up); a choice that would fail at once is not on offer.
        var legal = new List<DangerAction> { DangerAction.Flee };
        if (foe.Name != "minecraft:creeper" && c.Health >= 6 && foeClass != "creeper") legal.Add(DangerAction.Fight);
        if (_skills.DigInProblem() == null) legal.Add(DangerAction.DigIn);
        if (_skills.FindOpenWater() != null) legal.Add(DangerAction.SwimOut);
        legal.RemoveAll(a => _armBlockedUntil.GetValueOrDefault(a) > _tick);
        if (legal.Count == 0) legal.Add(DangerAction.Flee);

        var action = learner.Choose(situation, legal);
        Decided?.Invoke($"{DangerLearner.Doing(action)} against {foe.Name.Bare().Replace('_', ' ')} at {foe.DistanceTo(x, y, z):0} blocks ({situation}); odds {string.Join(", ", legal.Select(a => $"{DangerLearner.Doing(a)} {learner.Expected(situation, a):0.00}"))}");

        var episode = new Episode { Situation = situation, Action = action, HealthBefore = c.Health, Started = DateTime.UtcNow };
        lock (_episodeLock) { CloseEpisode(learner, _episode, died: false); _episode = episode; }
        _dangerJob = true;
        _nextDangerTick = _tick + 20;
        var kind = foe.Name.Bare();
        var (description, work) = action switch
        {
            DangerAction.Fight => ($"fighting the {kind.Replace('_', ' ')}", (Func<CancellationToken, Task<(bool, string)>>)(ct => _skills.AttackAsync(kind, ct))),
            DangerAction.DigIn => ("taking cover underground", ct => _skills.TakeCoverAsync(ct)),
            DangerAction.SwimOut => ("swimming out to open water", ct => _skills.SwimOutAsync(ct)),
            _ => ("stepping away from monsters", ct => _skills.StepAwayFromMonstersAsync(AvoidWithin + 2, ct)),
        };
        Func<CancellationToken, Task<(bool, string)>> wrapped = async ct =>
        {
            _dangerJob = true;
            var started = DateTime.UtcNow;
            try
            {
                var result = await work(ct);
                episode.JobWorked = result.Item1;
                // Failed straight away: it never had a chance, so it teaches nothing about the action. Not on offer for half a minute; he decides again at once.
                if (!result.Item1 && DateTime.UtcNow - started < TimeSpan.FromSeconds(2))
                {
                    _armBlockedUntil[action] = _tick + 600;
                    _nextDangerTick = _tick;
                    lock (_episodeLock) if (_episode == episode) _episode = null;
                }
                return result;
            }
            finally
            {
                episode.JobEnded = DateTime.UtcNow;
                _dangerJob = false;
            }
        };
        if (busy) _tasks.Interrupt(description, wrapped, quiet: true);
        else _tasks.StartReflex(description, wrapped);
        return true;
    }

    /// <summary>A choice is judged a few seconds after its job ended (what happened next is part of the result), or after two and a half minutes whatever it is doing.</summary>
    private void CloseEpisodeIfDue()
    {
        if (Learner is not { } learner) return;
        lock (_episodeLock)
        {
            if (_episode is not { } e) return;
            var now = DateTime.UtcNow;
            if ((e.JobEnded is { } ended && now - ended > TimeSpan.FromSeconds(4)) || now - e.Started > TimeSpan.FromSeconds(150))
            {
                CloseEpisode(learner, e, died: false);
                _episode = null;
            }
        }
    }

    private void CloseEpisode(DangerLearner learner, Episode? e, bool died)
    {
        if (e == null) return;
        var c = _client;
        var (x, y, z) = c.Position;
        var after = died ? 0 : c.Health;
        var stillClose = !died && c.Entities.Values.Any(m => Skills.IsThreatOnFoot(m.Name) && m.DistanceTo(x, y, z) < 6);
        var reward = DangerLearner.Reward(died, e.HealthBefore, after, stillClose, e.JobWorked);
        learner.Record(e.Situation, e.Action, reward, died, e.HealthBefore, after, (DateTime.UtcNow - e.Started).TotalSeconds);
        Decided?.Invoke($"result: {DangerLearner.Doing(e.Action)} ({e.Situation}) scored {reward:0.00}{(died ? ": I died" : $", health {e.HealthBefore / 2:0.#} to {after / 2:0.#} hearts")}; now expected {learner.Expected(e.Situation, e.Action):0.00}");
    }

    /// <summary>He died: whatever he had chose last gets the blame.</summary>
    public void NoteDeath()
    {
        _dangerJob = false;
        if (Learner is not { } learner) return;
        lock (_episodeLock)
        {
            CloseEpisode(learner, _episode, died: true);
            _episode = null;
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
        _client.Damaged -= OnDamaged;
        (Policy as IDisposable)?.Dispose();
    }
}
