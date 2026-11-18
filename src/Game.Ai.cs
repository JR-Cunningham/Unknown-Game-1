using System.Numerics;

namespace PixelLegion;

/// <summary>Rival houses. Each has a personality (see <see cref="Teams.Personalities"/>):
/// Horde - one big blob that grabs whatever it can beat and hunts weaker armies.
/// Raider - splits into small, cavalry-heavy warbands that raid weak settlements, pick off stragglers and
///          scatter from anything stronger.
/// Warden - garrisons its settlements, rushes to defend them, and only expands with a strike force it can spare.</summary>
sealed partial class Game
{
    readonly City?[] aiStrike = new City?[Teams.Count];
    readonly Dictionary<int, Vector2>[] raiderTargets =
        Enumerable.Range(0, Teams.Count).Select(_ => new Dictionary<int, Vector2>()).ToArray();
    readonly int[] nextBand = new int[Teams.Count];

    const int RaiderBandSize = 10;
    const int MaxRaiderBands = 12;

    void ResetAi()
    {
        Array.Clear(aiTarget);
        Array.Clear(aiStrike);
        Array.Clear(nextBand);
        foreach (var d in raiderTargets) d.Clear();
    }

    void UpdateAi(int team, float dt)
    {
        aiTimer[team] -= dt;
        aiReissue[team] -= dt;
        if (aiTimer[team] > 0) return;
        aiTimer[team] = Config.AiThinkInterval + (float)rng.NextDouble() * 0.8f;

        var personality = Teams.Personalities[team];
        int cap = ArmyCap(team);
        for (int guard = 0; guard < 40 && teamCount[team] < cap &&
             Recruit(team, AiPickKind(team, personality), AiSpawnPoint(team, personality)); guard++) { }

        var mine = units.Where(u => u.Team == team && !u.Dead).ToList();
        if (mine.Count == 0) return;

        // pixels posted to a settlement that has since fallen are free again
        foreach (var u in mine)
            if (u.Post != null && u.Post.Owner != team) { u.Post = null; u.Home = null; }

        if (personality == Personality.Warden) { UpdateWarden(team, mine); return; }

        // horde and raiders leave a small guard in each holding and campaign with everything else
        KeepGarrisons(team, mine, personality == Personality.Raider ? 2 : 3);
        var field = mine.Where(u => u.Post == null).ToList();
        if (field.Count == 0) return;
        if (personality == Personality.Raider) UpdateRaider(team, field);
        else UpdateHorde(team, field);
    }

    int ArmyCap(int team) => Config.AiArmyBase + Config.AiArmyPerHolding * cities.Count(c => c.Owner == team);

    /// <summary>A house whose army is (nearly) as big as it can support grows restless and takes on tougher fights
    /// rather than sitting on its gold.</summary>
    bool Restless(int team) => teamCount[team] >= ArmyCap(team) * 0.9f;

    /// <summary>Tops up every holding to a minimum guard (more for bigger settlements) from the field army.</summary>
    void KeepGarrisons(int team, List<Unit> mine, int perCity)
    {
        foreach (var c in cities)
        {
            if (c.Owner != team) continue;
            int need = perCity + c.Size - mine.Count(u => u.Post == c);
            if (need <= 0) continue;
            var take = mine.Where(u => u.Post == null).OrderBy(u => Vector2.DistanceSquared(u.Pos, c.Pos)).Take(need).ToList();
            if (take.Count == 0) return;
            foreach (var u in take) { u.Post = c; u.Band = -1; }
            MoveGroup(take, c.Pos, forced: false);
            foreach (var u in take) u.Home = c;
        }
    }

    UnitKind AiPickKind(int team, Personality p)
    {
        // infantry / archer / cavalry mix per personality
        var (inf, arch) = p switch
        {
            Personality.Raider => (0.35, 0.60),  // 35% inf, 25% archers, 40% cavalry
            Personality.Warden => (0.45, 0.90),  // 45% inf, 45% archers, 10% cavalry
            _ => (0.60, 0.85),                   // 60% inf, 25% archers, 15% cavalry
        };
        double roll = rng.NextDouble();
        var kind = roll < inf ? UnitKind.Infantry : roll < arch ? UnitKind.Archer : UnitKind.Cavalry;
        return gold[team] >= Stats.Of(kind).Cost ? kind : UnitKind.Infantry;
    }

    Vector2? AiSpawnPoint(int team, Personality p)
    {
        if (p == Personality.Warden)
        {
            var owned = cities.Where(c => c.Owner == team).ToList();
            if (owned.Count > 0) return owned[rng.Next(owned.Count)].Pos;
        }
        return null; // default: the main blob
    }

    int EnemiesNear(Vector2 p, float r, int team)
    {
        grid.Query(p, r, scratch);
        int n = 0;
        foreach (var u in scratch) if (u.Team != team) n++;
        return n;
    }

    /// <summary>A rival caravan worth ambushing: not trading with us, within reach.</summary>
    Caravan? NearestPrey(int team, Vector2 from, float maxDist)
    {
        Caravan? best = null;
        float bestD = maxDist * maxDist;
        foreach (var cv in caravans)
        {
            if (cv.From.Owner == team || cv.To.Owner == team) continue;
            float d = Vector2.DistanceSquared(from, cv.Pos);
            if (d < bestD) { bestD = d; best = cv; }
        }
        return best;
    }
    // ------------------------------------------------------------------ Horde

    void UpdateHorde(int team, List<Unit> mine)
    {
        bool restless = Restless(team);
        Vector2 center = blobCenter[team];
        int army = mine.Count;
        Vector2? best = null;
        float bestScore = float.MaxValue;
        void Consider(Vector2 p, float score)
        {
            if (score < bestScore) { bestScore = score; best = p; }
        }

        foreach (var c in cities)
        {
            float d = Vector2.Distance(center, c.Pos);
            if (c.Owner == team)
            {
                if (c.CaptureTeam != team && c.Capture < 1f) Consider(c.Pos, d * 0.5f);
                continue;
            }
            int defenders = EnemiesNear(c.Pos, c.Radius + 20f, team);
            if (army >= defenders * (restless ? 0.8f : 1.4f) + 3) Consider(c.Pos, d / (0.6f + 0.4f * c.Income));
        }

        for (int t = 0; t <= Config.AiCount; t++)
        {
            if (t == team || teamCount[t] == 0) continue;
            float d = Vector2.Distance(center, blobCenter[t]);
            if (d < 900f && army > teamCount[t] * 1.5f + 2) Consider(blobCenter[t], d * 0.8f);
        }

        // nothing worth attacking yet: mass at the nearest holding and grow
        if (best == null)
        {
            var rally = cities.Where(c => c.Owner == team).OrderBy(c => Vector2.DistanceSquared(c.Pos, center)).FirstOrDefault();
            if (rally == null) return;
            best = terrain.NearestLand(rally.Pos + Vector2.Normalize(new Vector2(Config.WorldW / 2, Config.WorldH / 2) - rally.Pos) * (rally.Radius + 60f));
        }
        bool newTarget = aiTarget[team] == null || Vector2.Distance(aiTarget[team]!.Value, best.Value) > 40f;
        if (newTarget || aiReissue[team] <= 0)
        {
            aiTarget[team] = best;
            aiReissue[team] = 8f;
            MoveGroup(mine, best.Value, forced: false);
        }
    }

    // ------------------------------------------------------------------ Raider (guerrilla warbands)

    void UpdateRaider(int team, List<Unit> mine)
    {
        // sort pixels into warbands. Band size grows with the army so there are never more than MaxRaiderBands.
        int bandSize = Math.Max(RaiderBandSize, mine.Count / MaxRaiderBands + 1);
        var bands = mine.Where(u => u.Band >= 0).GroupBy(u => u.Band).ToDictionary(g => g.Key, g => g.ToList());
        var sums = bands.ToDictionary(b => b.Key, b => b.Value.Aggregate(Vector2.Zero, (s, u) => s + u.Pos));
        foreach (var u in mine.Where(u => u.Band < 0))
        {
            // join the nearest band with room (any band at all once the cap is reached)
            bool full = bands.Count >= MaxRaiderBands;
            int bestBand = -1;
            float bestD2 = full ? float.MaxValue : 500f * 500f;
            foreach (var (id, list) in bands)
            {
                if (!full && list.Count >= bandSize + 2) continue;
                float d2 = Vector2.DistanceSquared(sums[id] / list.Count, u.Pos);
                if (d2 < bestD2) { bestD2 = d2; bestBand = id; }
            }
            if (bestBand < 0)
            {
                bestBand = nextBand[team]++;
                bands[bestBand] = new List<Unit>();
                sums[bestBand] = Vector2.Zero;
            }
            u.Band = bestBand;
            bands[bestBand].Add(u);
            sums[bestBand] += u.Pos;
        }

        // oversized bands split in two (while there is room for more bands) so the house keeps spreading out
        foreach (var id in bands.Keys.ToList())
        {
            var list = bands[id];
            if (list.Count < bandSize * 2 || bands.Count >= MaxRaiderBands) continue;
            int newId = nextBand[team]++;
            var half = list.Skip(list.Count / 2).ToList();
            foreach (var u in half) u.Band = newId;
            bands[id] = list.Take(list.Count / 2).ToList();
            bands[newId] = half;
        }
        var targets = raiderTargets[team];
        foreach (var id in targets.Keys.Where(k => !bands.ContainsKey(k)).ToList()) targets.Remove(id);

        foreach (var (id, band) in bands)
        {
            Vector2 c = Centroid(band);
            int n = band.Count;

            // threat check: who is around us?
            grid.Query(c, 220f, scratch);
            int threat = 0;
            Vector2 threatSum = Vector2.Zero;
            foreach (var e in scratch)
                if (e.Team != team) { threat++; threatSum += e.Pos; }

            Vector2 goal;
            bool forced = false;
            if (threat > n * 1.1f + 1)
            {
                // too strong: scatter away, ignoring fights on the way
                Vector2 away = c - threatSum / threat;
                away = away.LengthSquared() > 0.01f ? Vector2.Normalize(away) : RandomDir();
                goal = terrain.NearestLand(ClampWorld(c + away * 320f));
                forced = true;
            }
            else if (threat > 0)
                goal = threatSum / threat;                                  // pick off the weaker group
            else
                // raid a weak settlement, else ambush a passing caravan (aim ahead of it), else hold
                goal = RaidTarget(team, c, n)
                       ?? (NearestPrey(team, c, 1200f) is Caravan cv ? cv.Pos + cv.Heading * 60f : c);

            bool changed = !targets.TryGetValue(id, out var old) || Vector2.Distance(old, goal) > 50f;
            if (changed)
            {
                targets[id] = goal;
                MoveGroup(band, goal, forced);
            }
        }
    }

    /// <summary>Nearest settlement this band can take or neutralise; rivals' holdings are preferred.</summary>
    Vector2? RaidTarget(int team, Vector2 from, int bandSize)
    {
        Vector2? best = null;
        float bestScore = 1800f;
        foreach (var s in cities)
        {
            if (s.Owner == team) continue;
            int defenders = EnemiesNear(s.Pos, s.Radius + 20f, team);
            if (defenders > bandSize * (Restless(team) ? 1.2f : 0.7f)) continue;
            float score = Vector2.Distance(from, s.Pos) * (s.Owner >= 0 ? 0.6f : 1f) / (s.IsTown ? 1.3f : 1f);
            if (score < bestScore) { bestScore = score; best = s.Pos; }
        }
        return best;
    }

    // ------------------------------------------------------------------ Warden (defensive)

    void UpdateWarden(int team, List<Unit> mine)
    {
        var owned = cities.Where(c => c.Owner == team).ToList();
        if (owned.Count == 0)
        {
            // nothing to hold yet: behave like a horde until the first settlement falls
            foreach (var u in mine) u.Band = -1;
            UpdateHorde(team, mine);
            return;
        }

        var strikers = mine.Where(u => u.Band == 1).ToList();
        var garrison = mine.Where(u => u.Band != 1).ToList();

        // end a strike once the target is ours or the force is spent
        var strike = aiStrike[team];
        if (strike != null && (strike.Owner == team || strikers.Count < 3))
        {
            foreach (var u in strikers) { u.Band = -1; u.Post = null; garrison.Add(u); }
            strikers.Clear();
            aiStrike[team] = strike = null;
        }

        // 1. defend: a holding under attack pulls in half of every other garrison
        var threatened = owned
            .Select(c => (c, enemies: EnemiesNear(c.Pos, c.Radius + 80f, team)))
            .Where(x => x.enemies > 0 || (x.c.CaptureTeam != team && x.c.Capture < 1f))
            .OrderByDescending(x => x.enemies).Select(x => x.c).FirstOrDefault();
        if (threatened != null)
        {
            var responders = garrison.Where(u => u.Post != threatened).Where((u, i) => i % 2 == 0).ToList();
            foreach (var u in responders) u.Post = threatened;
            if (responders.Count > 0) MoveGroup(responders, threatened.Pos, forced: false);
            foreach (var u in responders) u.Home = threatened;
        }

        // 2. expand: only with troops it can spare after leaving every holding a guard
        if (strike == null && threatened == null)
        {
            bool restless = Restless(team);
            int spare = garrison.Count - owned.Count * (restless ? 3 : 6);
            City? best = null;
            float bestD = 1300f;
            int need = 0;
            foreach (var c in cities)
            {
                if (c.Owner == team) continue;
                float d = owned.Min(o => Vector2.Distance(o.Pos, c.Pos));
                int defenders = EnemiesNear(c.Pos, c.Radius + 20f, team);
                int required = (int)(defenders * (restless ? 1.0f : 1.6f)) + 5;
                if (d < bestD && spare >= required) { bestD = d; best = c; need = required; }
            }
            if (best != null)
            {
                var force = garrison.OrderBy(u => Vector2.DistanceSquared(u.Pos, best.Pos)).Take(Math.Max(need, spare / 2)).ToList();
                foreach (var u in force) { u.Band = 1; u.Post = null; garrison.Remove(u); }
                aiStrike[team] = best;
                MoveGroup(force, best.Pos, forced: false);
            }
        }

        // 3. keep the garrisons balanced across holdings
        if (threatened == null)
        {
            int quota = (int)MathF.Ceiling(garrison.Count / (float)owned.Count);
            var counts = owned.ToDictionary(c => c, c => 0);
            var unposted = new List<Unit>();
            foreach (var u in garrison)
            {
                if (u.Post != null && counts.TryGetValue(u.Post, out int k) && k < quota) counts[u.Post] = k + 1;
                else unposted.Add(u);
            }
            var moves = new Dictionary<City, List<Unit>>();
            foreach (var u in unposted)
            {
                var post = owned.Where(c => counts[c] < quota).OrderBy(c => Vector2.DistanceSquared(c.Pos, u.Pos)).FirstOrDefault()
                           ?? owned[0];
                counts[post]++;
                u.Post = post;
                if (!moves.TryGetValue(post, out var list)) moves[post] = list = new List<Unit>();
                list.Add(u);
            }
            foreach (var (post, list) in moves)
            {
                MoveGroup(list, post.Pos, forced: false);
                foreach (var u in list) u.Home = post;
            }
        }
    }
}
