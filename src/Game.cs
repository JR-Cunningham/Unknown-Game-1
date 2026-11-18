using System.Numerics;
using Raylib_cs;

namespace PixelLegion;

sealed partial class Game
{
    static readonly string[] CityNames =
    {
        "Ashford", "Wyvernhold", "Dunmere", "Ravenscar", "Thornbury", "Oakhelm", "Blackmoor", "Highcliff",
        "Stonewick", "Elderglen", "Greyhaven", "Wolfsden", "Marrowgate", "Kingsbridge", "Holloway",
        "Briarwood", "Coldharbour", "Fallowmere", "Redkeep", "Larkspur", "Ironford", "Gallowmere",
        "Swanmoor", "Hawksbury", "Oxbury", "Stagmere", "Thistlewood", "Rookhaven", "Dunwall", "Pennick",
        "Ambergate", "Crowmarsh", "Saltcombe", "Wychford", "Eastmere", "Brackenholt",
    };

    readonly Random rng = new();
    Terrain terrain = null!;
    Wildlife wildlife = null!;
    readonly List<Vector2> taverns = new();
    FogOfWar fog = null!;
    readonly List<Unit> units = new();
    readonly List<City> cities = new();
    readonly List<Caravan> caravans = new();
    Tracks tracks = null!;
    readonly List<Arrow> arrows = new();
    readonly List<Formation> formations = new();
    readonly SpatialGrid grid = new(Config.WorldW, Config.WorldH, 16f);
    readonly List<Unit> scratch = new();
    readonly List<Unit> scratch2 = new();

    readonly int[] gold = new int[Teams.Count];
    readonly int[] teamCount = new int[Teams.Count];
    readonly Vector2[] blobCenter = new Vector2[Teams.Count];   // centroid of each team's largest blob
    readonly float[] aiTimer = new float[Teams.Count];
    readonly float[] aiReissue = new float[Teams.Count];
    readonly Vector2?[] aiTarget = new Vector2?[Teams.Count];
    float blobTimer;

    Camera2D cam;
    float splitFraction = 0.5f;
    bool dragging, minimapDrag;
    Vector2 dragStart;
    Vector2 orderMarker;
    float orderMarkerTime;
    int stampCounter;
    float recruitRepeat;
    bool gameOver, victory;
    float time;
    string? toast;
    float toastTime;

    public Game() => Reset();


    // ------------------------------------------------------------------ setup

    void Reset()
    {
        terrain?.Dispose();
        foreach (var c in cities) c.Sprite.Dispose();
        terrain = new Terrain(rng.Next());
        fog?.Dispose();
        fog = new FogOfWar(terrain);
        tracks?.Dispose();
        tracks = new Tracks();

        units.Clear();
        cities.Clear();
        caravans.Clear();
        arrows.Clear();
        formations.Clear();
        squads.Clear();
        flowCache.Clear();
        wallCells.Clear();
        Array.Clear(gold);
        ResetAi();
        gameOver = victory = false;
        time = 0;
        toast = null;
        caravanTimer = 2f;

        // 1. settlement sites, biggest first. Fortresses sit up in the mountains, cities on low ground by the river,
        //    watchtowers on rises; everyone keeps a respectful distance scaled to their size.
        var names = CityNames.OrderBy(_ => rng.Next()).ToList();
        int nameIndex = 0;
        foreach (var (type, count) in Config.SettlementMix)
            for (int k = 0; k < count; k++)
            {
                float foot = Footprint(type);
                bool Fits(Vector2 p)
                {
                    float h = terrain.HeightAt(p);
                    if (!terrain.OnMainland(p) || h < Terrain.SandLevel + 0.02f) return false;
                    for (int a = 0; a < 8; a++)
                    {
                        var q = p + new Vector2(MathF.Cos(a * 0.785f), MathF.Sin(a * 0.785f)) * foot * 0.6f;
                        if (terrain.IsAnyWater(q)) return false;
                    }
                    float keepOff = (foot * 0.8f + 20f) * (foot * 0.8f + 20f);
                    if (terrain.HasRiver && terrain.RiverPath.Any(r => Vector2.DistanceSquared(r, p) < keepOff)) return false;
                    return cities.All(c => Vector2.Distance(c.Pos, p) > Footprint(c.Type) + foot + 140f);
                }
                bool Suits(Vector2 p)
                {
                    float h = terrain.HeightAt(p);
                    return type switch
                    {
                        SettlementType.Fortress => h > Terrain.HillLevel - 0.06f && h < Terrain.SnowLevel,
                        SettlementType.City => h < 0.5f && (!terrain.HasRiver || terrain.RiverPath.Any(r => Vector2.DistanceSquared(r, p) < 320f * 320f)),
                        SettlementType.Watchtower => h > 0.42f && h < Terrain.RockLevel,
                        _ => h < Terrain.RockLevel,
                    };
                }
                Vector2? pos = TryPlace(p => Fits(p) && Suits(p), 2500) ?? TryPlace(Fits, 2500);
                if (pos == null) continue;
                cities.Add(new City { Pos = pos.Value, Type = type, Name = names[nameIndex++ % names.Count] });
            }

        // 2. capitals: the rival houses rule from big seats spread far apart; you hold a castle far from all of them
        var big = cities.Where(c => c.Type is SettlementType.Fortress or SettlementType.City or SettlementType.Castle).ToList();
        // a cramped map may not fit enough great seats: promote the biggest of the rest to castles
        foreach (var c in cities.Where(c => !big.Contains(c)).OrderByDescending(c => Footprint(c.Type)).Take(Math.Max(0, Config.AiCount + 1 - big.Count)).ToList())
        {
            c.Type = SettlementType.Castle;
            big.Add(c);
        }
        var aiCaps = new List<City> { big[rng.Next(big.Count)] };
        while (aiCaps.Count < Config.AiCount)
            aiCaps.Add(big.Where(c => !aiCaps.Contains(c)).OrderByDescending(c => aiCaps.Min(k => Vector2.Distance(k.Pos, c.Pos))).First());
        var playerCap = cities.Where(c => c.Type == SettlementType.Castle && !aiCaps.Contains(c))
                              .OrderByDescending(c => aiCaps.Min(k => Vector2.Distance(k.Pos, c.Pos))).FirstOrDefault()
                        ?? cities.Where(c => !aiCaps.Contains(c)).OrderByDescending(c => aiCaps.Min(k => Vector2.Distance(k.Pos, c.Pos))).First();
        playerCap.Type = SettlementType.Castle;
        var capitals = new List<City> { playerCap };
        capitals.AddRange(aiCaps);

        // 3. every other settlement belongs to a rival house - there is no neutral land. The houses take turns
        //    claiming the nearest unclaimed settlement to their realm, so territories are compact and even.
        for (int t = 0; t < capitals.Count; t++) capitals[t].Owner = t;
        var unclaimed = cities.Where(c => !capitals.Contains(c)).ToList();
        for (int turn = 0; unclaimed.Count > 0; turn++)
        {
            int t = 1 + turn % Config.AiCount;
            var realm = cities.Where(c => c.Owner == t).ToList();
            var pick = unclaimed.OrderBy(c => realm.Min(r => Vector2.Distance(r.Pos, c.Pos))).First();
            pick.Owner = t;
            unclaimed.Remove(pick);
        }
        foreach (var c in cities)
        {
            c.CaptureTeam = c.Owner;
            c.Capture = 1f;
            int seedArt = rng.Next();
            c.Sprite = c.Type switch
            {
                SettlementType.Watchtower => CastleArt.BuildTower(seedArt),
                SettlementType.Village => CastleArt.BuildTown(0, seedArt),
                SettlementType.Town => CastleArt.BuildTown(2, seedArt),
                SettlementType.City => CastleArt.BuildTown(4, seedArt),
                SettlementType.Castle => CastleArt.Build(1 + rng.Next(2), seedArt),
                _ => CastleArt.Build(4, seedArt),
            };
            c.Radius = MathF.Max(c.Sprite.W, c.Sprite.H) * CastleArt.Scale / 2f + 4f;
            c.Walls = c.Type switch
            {
                // walls stand exactly on the edge of the courtyard the sprite was drawn round
                SettlementType.Castle => new Walls(c.Sprite.WallRadius * CastleArt.Scale, 14, 50f),
                SettlementType.Fortress => new Walls(c.Sprite.WallRadius * CastleArt.Scale, 20, 110f),
                SettlementType.City => new Walls(c.Sprite.WallRadius * CastleArt.Scale, 16, 70f),   // only the inner core is walled
                _ => null,
            };
        }
        capitals[0].KnownOwner = Teams.Player;

        BuildRoads();
        EnsureBridge();
        PlaceGates();
        foreach (var c in cities)
            if (c.IsTown) terrain.AddFarmland(c.Pos, c.Radius * 0.85f, c.Radius + (c.Type == SettlementType.City ? 220f : 150f), rng.Next());
        terrain.Repaint();
        BuildSettlementMap();
        PlaceTaverns();
        PlaceMills();
        wildlife = new Wildlife(terrain, cities, rng);
        // a full route map to every settlement, built in parallel: caravans and most marches reuse these for free
        Parallel.ForEach(cities, c => c.Flow = terrain.BuildFlow(c.Pos, Array.Empty<Vector2>(), full: true));
        // 4. garrisons, manned by the owner
        foreach (var c in cities)
        {
            int n = c.Type switch
            {
                SettlementType.Watchtower or SettlementType.Village => 3,
                SettlementType.Town => 6,
                SettlementType.City => 16,
                SettlementType.Castle => 9,
                _ => 22,
            };
            if (capitals.Contains(c)) n += 6;
            for (int g = 0; g < n; g++)
            {
                var spot = terrain.NearestLand(c.Pos + Spiral(g) * 1.6f);
                var kind = g % 3 == 2 ? UnitKind.Archer : UnitKind.Infantry;
                var u = Unit.Make(kind, c.Owner, spot, spot);
                u.Home = c;
                if (c.Owner != Teams.Player) u.Post = c;
                units.Add(u);
            }
        }

        // 5. your field warband, camped just outside the keep toward the middle of the map
        var home = capitals[0];
        Vector2 toMiddle = Vector2.Normalize(new Vector2(Config.WorldW / 2, Config.WorldH / 2) - home.Pos);
        Vector2 camp = terrain.NearestLand(home.Pos + toMiddle * (home.Radius + 50f));
        for (int i = 0; i < 22; i++)
        {
            var kind = i < 12 ? UnitKind.Infantry : i < 18 ? UnitKind.Archer : UnitKind.Cavalry;
            var spot = terrain.NearestLand(camp + Spiral(i));
            var u = Unit.Make(kind, Teams.Player, spot, spot);
            u.Selected = true;
            units.Add(u);
        }

        gold[Teams.Player] = Config.StartGold;
        for (int t = 1; t <= Config.AiCount; t++) { gold[t] = Config.AiStartGold; blobCenter[t] = capitals[t].Pos; }
        blobCenter[Teams.Player] = camp;

        cam = new Camera2D
        {
            Target = camp,
            Offset = new Vector2(Raylib.GetScreenWidth() / 2f, Raylib.GetScreenHeight() / 2f),
            Rotation = 0,
            Zoom = 1.6f,
        };
        RebuildGrid();
        RemoveDead();
        RecomputeBlobs();
        fog.Update(0, units, cities, force: true);
        ShowToast("Hold your keep. Take their settlements and raid their caravans!");
    }
    static float Footprint(SettlementType t) => t switch
    {
        SettlementType.Watchtower => 30f,
        SettlementType.Village => 45f,
        SettlementType.Town => 85f,
        SettlementType.City => 130f,
        SettlementType.Castle => 70f,
        _ => 105f,
    };

    static bool IsMajor(City c) => c.Type is SettlementType.City or SettlementType.Town or SettlementType.Fortress;

    Vector2? TryPlace(Func<Vector2, bool> ok, int tries)
    {
        for (int i = 0; i < tries; i++)
        {
            var p = new Vector2(180 + (float)rng.NextDouble() * (Config.WorldW - 360), 180 + (float)rng.NextDouble() * (Config.WorldH - 360));
            if (ok(p)) return p;
        }
        return null;
    }

    Vector2 RandomPos(float margin, Func<Vector2, bool> ok)
    {
        Vector2 p = default;
        for (int attempt = 0; attempt < 500; attempt++)
        {
            p = new Vector2(
                margin + (float)rng.NextDouble() * (Config.WorldW - margin * 2),
                margin + (float)rng.NextDouble() * (Config.WorldH - margin * 2));
            if (ok(p)) return p;
        }
        return p;
    }

    Vector2 RandomDir()
    {
        float a = (float)rng.NextDouble() * MathF.Tau;
        return new Vector2(MathF.Cos(a), MathF.Sin(a));
    }

    /// <summary>A river must never cut the realm in two: if no road happened to cross it, build a bridge near its
    /// middle and run tracks from each end to the nearest settlement.</summary>
    void EnsureBridge()
    {
        if (!terrain.HasRiver || terrain.AnyBridge) return;
        if (terrain.FindBridgeSite() is not var (a, b)) return;
        terrain.AddRoad(new List<Vector2> { a, b }, paved: false);
        foreach (var end in new[] { a, b })
        {
            var near = cities.OrderBy(c => Vector2.DistanceSquared(c.Pos, end)).FirstOrDefault();
            if (near == null) continue;
            var path = terrain.FindRoadPath(end, near.Pos);
            if (path != null) terrain.AddRoad(path, paved: false);
        }
    }

    /// <summary>A gatehouse wherever a road meets a wall. A walled place with no road gets one gate, facing the
    /// nearest road (or the middle of the map).</summary>
    void PlaceGates()
    {
        foreach (var c in cities)
        {
            if (c.Walls is not Walls w) continue;
            foreach (var road in terrain.Roads)
                for (int k = 1; k < road.Points.Count; k++)
                {
                    Vector2 a = road.Points[k - 1], b = road.Points[k];
                    float da = Vector2.Distance(a, c.Pos) - w.Radius, db = Vector2.Distance(b, c.Pos) - w.Radius;
                    if (MathF.Sign(da) == MathF.Sign(db)) continue;
                    w.MakeGate(w.SegmentAt(c.Pos, Vector2.Lerp(a, b, da / (da - db))));
                }
            if (!w.Gate.Any(g => g))
            {
                var toward = terrain.Roads.SelectMany(r => r.Points).OrderBy(p => Vector2.DistanceSquared(p, c.Pos))
                                     .FirstOrDefault(p => Vector2.Distance(p, c.Pos) > w.Radius + 20f);
                if (toward == default) toward = new Vector2(Config.WorldW / 2, Config.WorldH / 2);
                w.MakeGate(w.SegmentAt(c.Pos, toward));
            }
        }
    }

    // windmills on open ground near farming settlements; water wheels on the river bank by settlements near it
    readonly List<Vector2> windmills = new();
    readonly List<(Vector2 pos, Vector2 across)> waterWheels = new();

    void PlaceMills()
    {
        windmills.Clear();
        waterWheels.Clear();
        foreach (var c in cities.Where(c => c.IsTown))
        {
            if (rng.Next(3) > 0)
                for (int tries = 0; tries < 40; tries++)
                {
                    Vector2 p = c.Pos + RandomDir() * (c.Radius + 30f + (float)rng.NextDouble() * 80f);
                    if (!terrain.OnMainland(p) || terrain.RoadAt(p) || terrain.ForestAt(p) || terrain.SwampAt(p)) continue;
                    if (windmills.Any(m => Vector2.Distance(m, p) < 120f) || taverns.Any(t => Vector2.Distance(t, p) < 40f)) continue;
                    windmills.Add(p);
                    break;
                }
            if (!terrain.HasRiver) continue;
            int bestK = -1;
            float bestD = 300f * 300f;
            for (int k = 2; k < terrain.RiverPath.Count - 2; k++)
            {
                float d = Vector2.DistanceSquared(terrain.RiverPath[k], c.Pos);
                if (d < bestD) { bestD = d; bestK = k; }
            }
            if (bestK < 0) continue;
            Vector2 along = Vector2.Normalize(terrain.RiverPath[bestK + 2] - terrain.RiverPath[bestK - 2]);
            Vector2 across = new(-along.Y, along.X);
            if (Vector2.Dot(c.Pos - terrain.RiverPath[bestK], across) < 0) across = -across;   // on the settlement's bank
            for (float off = 8f; off < 24f; off += 2f)
            {
                Vector2 p = terrain.RiverPath[bestK] + across * off;
                if (!terrain.IsAnyWater(p))
                {
                    if (waterWheels.All(w => Vector2.Distance(w.pos, p) > 150f)) waterWheels.Add((p, across));
                    break;
                }
            }
        }
    }

    /// <summary>Roadside taverns every so often along the longer roads.</summary>
    void PlaceTaverns()
    {
        taverns.Clear();
        foreach (var road in terrain.Roads)
        {
            float walked = 0;
            for (int k = 1; k < road.Points.Count; k++)
            {
                walked += Vector2.Distance(road.Points[k - 1], road.Points[k]);
                if (walked < 650f) continue;
                walked = 0;
                Vector2 dir = Vector2.Normalize(road.Points[k] - road.Points[k - 1]);
                Vector2 spot = road.Points[k] + new Vector2(-dir.Y, dir.X) * 16f;
                if (!terrain.OnMainland(spot) || terrain.RoadAt(spot)) continue;
                if (cities.Any(c => Vector2.Distance(c.Pos, spot) < c.Radius + 60f)) continue;
                if (taverns.Any(t => Vector2.Distance(t, spot) < 400f)) continue;
                taverns.Add(spot);
            }
        }
    }

    /// <summary>Links settlements with roads along a minimum spanning tree, skipping very long links,
    /// so most (not all) settlements end up on the road network.</summary>
    void BuildRoads()
    {
        int n = cities.Count;
        var inTree = new bool[n];
        var bestDist = Enumerable.Repeat(float.MaxValue, n).ToArray();
        var parent = Enumerable.Repeat(-1, n).ToArray();
        bestDist[0] = 0;
        for (int k = 0; k < n; k++)
        {
            int u = -1;
            for (int i = 0; i < n; i++)
                if (!inTree[i] && (u < 0 || bestDist[i] < bestDist[u])) u = i;
            inTree[u] = true;
            if (parent[u] >= 0 && bestDist[u] <= Config.MaxRoadLength)
            {
                var a = cities[parent[u]];
                var b = cities[u];
                var path = terrain.FindRoadPath(a.Pos, b.Pos);
                if (path != null) terrain.AddRoad(path, paved: IsMajor(a) || IsMajor(b));
            }
            for (int v = 0; v < n; v++)
            {
                float d = Vector2.Distance(cities[u].Pos, cities[v].Pos);
                if (!inTree[v] && d < bestDist[v]) { bestDist[v] = d; parent[v] = u; }
            }
        }
    }

    static Vector2 ClampWorld(Vector2 p) =>
        new(Math.Clamp(p.X, 2, Config.WorldW - 2), Math.Clamp(p.Y, 2, Config.WorldH - 2));

    /// <summary>Sunflower-spiral offsets: packs N pixels into a round blob.</summary>
    static Vector2 Spiral(int i)
    {
        if (i == 0) return Vector2.Zero;
        float r = Config.FormationSpread * MathF.Sqrt(i);
        float a = i * 2.39996f;
        return new Vector2(MathF.Cos(a) * r, MathF.Sin(a) * r);
    }

    void ShowToast(string msg)
    {
        toast = msg;
        toastTime = 3.5f;
    }

    // ------------------------------------------------------------------ update

    public void Update(float dt)
    {
        cam.Offset = new Vector2(Raylib.GetScreenWidth() / 2f, Raylib.GetScreenHeight() / 2f);
        HandleCamera(dt);

        if (gameOver)
        {
            if (Raylib.IsKeyPressed(KeyboardKey.Enter)) Reset();
            return;
        }

        time += dt;
        toastTime -= dt;
        orderMarkerTime -= dt;

        RebuildGrid();
        blobTimer -= dt;
        if (blobTimer <= 0) { RecomputeBlobs(); blobTimer = 0.5f; }

        HandleInput(dt);
        for (int t = 1; t <= Config.AiCount; t++) UpdateAi(t, dt);
        UpdateSquads(dt);
        UpdateUnits(dt);
        fog.Update(dt, units, cities);
        UpdateArrows(dt);
        UpdateCaravans(dt);
        wildlife.Update(dt, grid, scratch);
        tracks.Update(dt);
        UpdateCities(dt);
        RemoveDead();
        UpdateFormations();
        CheckEnd();
    }

    void RebuildGrid() => grid.Build(units);
    void RemoveDead()
    {
        foreach (var u in units)
            if (u.Dead && u.Form != null) u.Form.Dirty = true;
        units.RemoveAll(u => u.Dead);
        Array.Clear(teamCount);
        foreach (var u in units) teamCount[u.Team]++;
    }

    // ------------------------------------------------------------------ blobs (clusters)

    List<Unit> FloodBlob(Unit start, int stamp)
    {
        var result = new List<Unit>();
        var queue = new Queue<Unit>();
        start.Stamp = stamp;
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var u = queue.Dequeue();
            result.Add(u);
            grid.Query(u.Pos, Config.LinkDistance, scratch2, SpatialGrid.Only(start.Team));
            foreach (var n in scratch2)
            {
                if (n.Stamp == stamp) continue;
                n.Stamp = stamp;
                queue.Enqueue(n);
            }
        }
        return result;
    }

    List<Unit> LargestBlob(int team, bool assignLeaders = false)
    {
        int stamp = ++stampCounter;
        List<Unit> best = new();
        foreach (var u in units)
        {
            if (u.Team != team || u.Dead || u.Stamp == stamp) continue;
            var blob = FloodBlob(u, stamp);
            if (assignLeaders) AssignLeader(blob);
            if (blob.Count > best.Count) best = blob;
        }
        return best;
    }

    /// <summary>Every group of 3+ has exactly one leader (the pixel with the hat): its most veteran member. When
    /// groups merge, one of the existing leaders keeps the hat - the most veteran, by lot if they're level.
    /// A group that loses its leader (split off, or the leader fell) picks a new one the same way.</summary>
    void AssignLeader(List<Unit> blob)
    {
        if (blob.Count < 3)
        {
            foreach (var u in blob) u.IsLeader = false;
            return;
        }
        var leaders = blob.Where(u => u.IsLeader).ToList();
        int topRank = blob.Max(u => u.Rank);
        if (leaders.Count == 1 && leaders[0].Rank >= topRank) return;

        // merged groups choose among their old leaders; otherwise (or if outranked) the most veteran pixel
        var pool = leaders.Count > 1 && leaders.Max(u => u.Rank) >= topRank ? leaders : blob;
        int best = pool.Max(u => u.Rank);
        var top = pool.Where(u => u.Rank == best).ToList();
        var chosen = leaders.FirstOrDefault(l => l.Rank == best && leaders.Count == 1) ?? top[rng.Next(top.Count)];
        foreach (var l in leaders) l.IsLeader = false;
        chosen.IsLeader = true;
    }

    static Vector2 Centroid(IReadOnlyCollection<Unit> list)
    {
        Vector2 sum = Vector2.Zero;
        foreach (var u in list) sum += u.Pos;
        return list.Count > 0 ? sum / list.Count : Vector2.Zero;
    }

    void RecomputeBlobs()
    {
        for (int t = 0; t <= Config.AiCount; t++)
        {
            var blob = LargestBlob(t, assignLeaders: true);
            if (blob.Count > 0) blobCenter[t] = Centroid(blob);
        }
        RefreshArcherBands();
    }

    // ------------------------------------------------------------------ simulation

    float TerrainSpeed(UnitKind kind, Vector2 p)
    {
        float h = terrain.HeightAt(p);
        if (h < Terrain.WaterLevel && !terrain.BridgeAt(p)) return 0.4f;
        int road = terrain.RoadKindAt(p);
        if (road > 0)
            return (road == 2 ? Config.PavedBonus : Config.DirtBonus) * (kind == UnitKind.Cavalry ? Config.CavalryOpenGround : 1f);
        if (terrain.SwampAt(p)) return 0.55f;                 // everyone wades through a swamp
        if (terrain.ForestAt(p)) return 0.8f;                 // horses lose their edge in the trees
        if (kind == UnitKind.Cavalry) return h >= Terrain.RockLevel ? 1f : Config.CavalryOpenGround;
        return 1f;
    }

    void UpdateUnits(float dt)
    {
        float sep = Config.SeparationRadius, sep2 = sep * sep;
        float shoulderR = Config.LineSpacing * 1.5f, shoulder2 = shoulderR * shoulderR;

        for (int i = 0; i < units.Count; i++)
        {
            var u = units[i];
            if (u.Dead) continue;
            var st = u.S;

            // nearest enemy: re-searched a few times a second (enemy buckets only), tracked in between
            u.SearchTimer -= dt;
            if (u.Enemy != null && (u.Enemy.Dead || Vector2.DistanceSquared(u.Enemy.Pos, u.Pos) > st.Aggro * st.Aggro))
                u.Enemy = null;
            if (u.SearchTimer <= 0)
            {
                u.SearchTimer = 0.18f + (float)rng.NextDouble() * 0.12f;
                grid.Query(u.Pos, st.Aggro, scratch, SpatialGrid.Except(u.Team));
                Unit? best = null;
                float bestD2 = float.MaxValue;
                foreach (var n in scratch)
                {
                    float d2 = Vector2.DistanceSquared(u.Pos, n.Pos);
                    if (d2 < bestD2) { bestD2 = d2; best = n; }
                }
                u.Enemy = best;
            }
            Unit? enemy = u.Enemy;
            float enemyD2 = enemy != null ? Vector2.DistanceSquared(u.Pos, enemy.Pos) : float.MaxValue;

            // crowd spacing (and shoulder-to-shoulder bracing in a line) from a small, tight query
            grid.Query(u.Pos, u.Form != null ? shoulderR : sep, scratch);
            Vector2 push = Vector2.Zero;
            int shoulders = 0;
            foreach (var n in scratch)
            {
                if (n == u) continue;
                float d2 = Vector2.DistanceSquared(u.Pos, n.Pos);
                if (d2 < sep2)
                {
                    if (d2 < 0.0001f) push += RandomDir() * 0.5f;
                    else
                    {
                        float d = MathF.Sqrt(d2);
                        push += (u.Pos - n.Pos) / d * (sep - d);
                    }
                }
                if (u.Form != null && n.Form == u.Form && d2 < shoulder2 &&
                    MathF.Abs(Vector2.Dot(n.Pos - u.Pos, u.Form.Facing)) < Config.LineSpacing * 0.5f)
                    shoulders++;
            }
            u.Brace = u.Form == null || u.Forced ? 1f
                    : shoulders >= 2 ? Config.BraceBothSides
                    : shoulders == 1 ? Config.BraceOneSide : 1f;

            float myHeight = terrain.HeightAt(u.Pos);
            u.AttackCd -= dt;
            u.SinceHurt += dt;
            u.HitFlash -= dt;
            u.RankFlash -= dt;

            // combat - higher ground hits harder and reaches further (much further for archers)
            float reach = st.Range;
            bool meleeBlocked = false;
            if (enemy != null)
            {
                float diff = myHeight - terrain.HeightAt(enemy.Pos);
                float maxBonus = u.Kind == UnitKind.Archer ? Config.MaxArcherHighGroundReach : Config.MaxHighGroundReach;
                float bonusScale = u.Kind == UnitKind.Archer ? 3f : 1f;
                reach += Math.Clamp(diff * Config.HighGroundRange * bonusScale, 0f, maxBonus);
                bool onWalls = u.Kind == UnitKind.Archer && BehindOwnWalls(u);
                if (onWalls) reach += Config.DefenderArrowReach;
                // nobody can cut or stab through an intact wall or gate - only arrows fly over it
                meleeBlocked = u.Kind != UnitKind.Archer && WallBetween(u.Pos, enemy.Pos);

                if (enemyD2 <= reach * reach && u.AttackCd <= 0 && !meleeBlocked)
                {
                    float dmg = st.Damage * HighGroundMultiplier(diff) * (1f + Config.VetDamagePerRank * u.Rank);
                    if (u.Kind == UnitKind.Cavalry && u.Vel.Length() > Config.ChargeSpeed) dmg *= Config.ChargeBonus;

                    if (u.Kind == UnitKind.Archer)
                    {
                        // archers on their own walls are deadly; shooting at troops sheltering behind walls is not
                        if (onWalls) dmg *= Config.DefenderArrowBonus;
                        else if (BehindOwnWalls(enemy)) dmg *= Config.AttackerArrowsVsWalls;
                        FireArrow(u, enemy, dmg);
                    }
                    else DealDamage(enemy, dmg, u);
                    u.AttackCd = st.Cooldown * (0.8f + 0.4f * (float)rng.NextDouble());
                }
            }

            // held up at an enemy wall with nobody to fight: hack at it (arrows barely scratch stone)
            if (u.BlockedBy is City besieged && besieged.Walls is Walls bw && besieged.Owner != u.Team &&
                !bw.Down[u.BlockedSeg] && u.AttackCd <= 0 && (enemy == null || enemyD2 > reach * reach || meleeBlocked))
            {
                float wd = st.Damage * (1f + Config.VetDamagePerRank * u.Rank) *
                           (u.Kind == UnitKind.Archer ? 0.25f : u.Kind == UnitKind.Cavalry ? 0.6f : 1f);
                int seg = u.BlockedSeg;
                bw.Hp[seg] -= wd;
                bw.LastHit[seg] = time;
                if (bw.Hp[seg] <= 0)
                {
                    bw.Hp[seg] = 0;
                    bw.Down[seg] = true;
                    if (besieged.Owner == Teams.Player) ShowToast($"The walls of {besieged.Name} have been breached!");
                    else if (u.Team == Teams.Player) ShowToast($"Breach! The walls of {besieged.Name} are down.");
                }
                u.AttackCd = st.Cooldown * (0.8f + 0.4f * (float)rng.NextDouble());
            }

            // healing: fast inside a friendly settlement, slow when resting
            if (u.Hp < st.Hp || u.Rank > 0)
            {
                float maxHp = u.MaxHp;
                if (u.Hp < maxHp)
                {
                    float regen = FriendlySettlement(u) != null ? Config.RegenInCity
                                : u.SinceHurt > Config.RestDelay ? Config.RegenResting : 0f;
                    u.Hp = MathF.Min(maxHp, u.Hp + regen * dt);
                }
            }

            // movement
            Vector2 desired = u.Target;
            bool chasing = false;
            if (enemy != null && !u.Forced)
            {
                bool allowed = u.Home == null || Vector2.Distance(enemy.Pos, u.Home.Pos) < u.Home.Radius + 50f;
                // pixels in a battle line hold their place unless the enemy is right on top of their slot;
                // archers in a line never step out - they shoot from where they stand
                if (u.Form != null)
                    allowed &= u.Kind != UnitKind.Archer &&
                               Vector2.Distance(enemy.Pos, u.Target) < Config.LineHoldRadius + st.Range;
                // defenders behind unbroken walls hold their ground until a section falls
                if (u.Kind != UnitKind.Archer && HoldingWalls(u)) allowed = false;
                if (allowed)
                {
                    chasing = true;
                    float engage = u.Kind == UnitKind.Archer ? reach * 0.85f : st.Range * 0.7f;
                    desired = enemyD2 > engage * engage ? enemy.Pos : u.Pos;
                }
            }

            // archers back away from anything that reaches melee range, letting the infantry take the hit
            if (u.Kind == UnitKind.Archer && enemy != null && u.Form == null && enemyD2 < 12f * 12f && enemyD2 > 0.01f)
            {
                desired = u.Pos + Vector2.Normalize(u.Pos - enemy.Pos) * 12f;
                chasing = true;
            }

            // marching: keep our place relative to the squad's anchor, which follows the one shared route;
            // a straggler (cut off by water, or far behind) follows the route itself to catch up
            if (!chasing && u.Squad is Squad sq && !sq.Done)
            {
                Vector2 spot = sq.Anchor + (u.Target - sq.Goal);
                bool straggler = Vector2.DistanceSquared(spot, u.Pos) > 60f * 60f || terrain.IsWater(spot);
                desired = straggler ? sq.Flow.Next(u.Pos) : spot;
            }

            // walls: going in or out means going round to a gate (ours) or a breach
            desired = WallDetour(u, desired);

            Vector2 to = desired - u.Pos;
            float dist = to.Length();
            Vector2 dv = Vector2.Zero;
            if (dist > 0.5f)
            {
                Vector2 dir = to / dist;
                float slope = (terrain.HeightAt(u.Pos + dir * 4f) - myHeight) / 4f;
                float terrainFactor = Math.Clamp(1f - slope * Config.UphillSlow, 0.45f, 1.25f) * TerrainSpeed(u.Kind, u.Pos);
                float speed = Config.Speed * terrainFactor * (chasing ? 1.1f : 1f) * MathF.Min(1f, dist / (chasing ? 4f : 10f));
                dv = dir * speed;
            }
            dv += push * Config.SeparationStrength;

            u.Vel = Vector2.Lerp(u.Vel, dv, MathF.Min(1f, 8f * dt));
            MoveBlockedByWater(u, dt);
            if (u.Vel.LengthSquared() > 16f) u.Heading = Vector2.Normalize(u.Vel);
            if (Vector2.DistanceSquared(u.Pos, u.LastPrint) >= Config.TrackStep * Config.TrackStep)
            {
                if (!terrain.IsWater(u.Pos)) tracks.Stamp(u.Kind, u.Pos, u.Heading);
                u.LastPrint = u.Pos;
            }

            if (u.Forced)
            {
                u.ForcedTimeout -= dt;
                if (u.ForcedTimeout <= 0 || Vector2.DistanceSquared(u.Pos, u.Target) < 100f) u.Forced = false;
            }
        }
    }

    // ------------------------------------------------------------------ squads

    readonly List<Squad> squads = new();

    /// <summary>Starts a group marching: one route from the group's centre to the goal, shared by everyone.
    /// Each pixel's Target must already be set; it keeps (Target - goal) as its place around the anchor.</summary>
    void StartSquad(List<Unit> group, Vector2 goal)
    {
        if (group.Count == 0) return;
        Vector2 anchor = terrain.NearestLand(Centroid(group));
        var sq = new Squad
        {
            Anchor = anchor,
            Goal = goal,
            Flow = GetFlow(goal, anchor),
            Mounted = group.All(u => u.Kind == UnitKind.Cavalry),
        };
        foreach (var u in group)
        {
            u.Squad = sq;
            sq.Members.Add(u);
        }
        squads.Add(sq);

        // trace the route once, so the player can see where their troops will go
        if (group[0].Team == Teams.Player)
        {
            Vector2 p = anchor;
            sq.Path.Add(p);
            for (int k = 0; k < 800 && Vector2.DistanceSquared(p, goal) > 48f * 48f; k++)
            {
                Vector2 n = sq.Flow.Next(p);
                if (n == p || n == sq.Flow.Goal) break;
                sq.Path.Add(n);
                p = n;
            }
            sq.Path.Add(goal);
            sq.Path = Terrain.Chaikin(Terrain.Chaikin(sq.Path));
        }
    }

    // routes are shared: squads heading for the same spot reuse one flow field while it is fresh
    readonly Dictionary<int, (FlowField flow, float made)> flowCache = new();

    FlowField GetFlow(Vector2 goal, Vector2 from)
    {
        foreach (var c in cities)
            if (c.Flow != null && Vector2.DistanceSquared(goal, c.Pos) < 40f * 40f) return c.Flow;
        int key = terrain.NavIndex(goal);
        if (flowCache.TryGetValue(key, out var hit) && time - hit.made < 20f && hit.flow.Covers(from)) return hit.flow;
        if (flowCache.Count > 64)
            foreach (var k in flowCache.Where(kv => time - kv.Value.made > 20f).Select(kv => kv.Key).ToList()) flowCache.Remove(k);
        var flow = terrain.BuildFlow(goal, new[] { from });
        flowCache[key] = (flow, time);
        return flow;
    }

    /// <summary>Moves each squad's anchor along its route at marching pace, slowing down when the troops lag behind.</summary>
    void UpdateSquads(float dt)
    {
        foreach (var sq in squads)
        {
            sq.Members.RemoveAll(u => u.Dead || u.Squad != sq);
            if (sq.Done || sq.Members.Count == 0) continue;

            Vector2 toGoal = sq.Goal - sq.Anchor;
            float distGoal = toGoal.Length();
            if (distGoal < 2f)
            {
                sq.Anchor = sq.Goal;
                sq.Done = true;
                continue;
            }

            Vector2 next = distGoal < 48f ? sq.Goal : sq.Flow.Next(sq.Anchor);
            Vector2 step = next - sq.Anchor;
            float stepLen = step.Length();
            if (stepLen < 0.01f) { next = sq.Goal; step = toGoal; stepLen = distGoal; }

            // how far behind are the troops? (sampled, so huge squads stay cheap)
            float lag = 0;
            int samples = Math.Min(24, sq.Members.Count), stride = Math.Max(1, sq.Members.Count / samples);
            for (int k = 0, n = 0; n < samples && k < sq.Members.Count; k += stride, n++)
            {
                var m = sq.Members[k];
                lag += Vector2.Distance(m.Pos, sq.Anchor + (m.Target - sq.Goal));
            }
            lag /= samples;

            // the bigger the blob, the slower it marches
            float blob = Math.Clamp(1.1f - Config.BlobSlowdown * MathF.Log2(sq.Members.Count), Config.MinBlobSpeed, 1f);
            float speed = Config.Speed * 0.85f * blob * TerrainSpeed(sq.Mounted ? UnitKind.Cavalry : UnitKind.Infantry, sq.Anchor);
            if (lag > 25f) speed *= Math.Clamp(1f - (lag - 25f) / 50f, 0.1f, 1f);
            sq.Anchor += step / stepLen * MathF.Min(speed * dt, stepLen);
            while (sq.PathIndex < sq.Path.Count - 1 && Vector2.DistanceSquared(sq.Anchor, sq.Path[sq.PathIndex]) < 30f * 30f)
                sq.PathIndex++;
        }
        squads.RemoveAll(sq => sq.Members.Count == 0);
        foreach (var sq in squads)
            if (sq.Done) foreach (var u in sq.Members) if (u.Squad == sq) u.Squad = null;
        squads.RemoveAll(sq => sq.Done);
    }
    /// <summary>Water can't be crossed: a step into water slides along the shore instead.</summary>
    void MoveBlockedByWater(Unit u, float dt)
    {
        Vector2 before = u.Pos;
        MoveOverLand(u, dt);
        u.Pos = StopAtWalls(u, before, u.Pos);
    }

    /// <summary>Intact enemy walls can't be walked through: the pixel is held against the wall (and will hack at
    /// it). Owners pass freely - they know where the gates are.</summary>
    Vector2 StopAtWalls(Unit u, Vector2 from, Vector2 to)
    {
        u.BlockedBy = null;
        var s = settlementAt[terrain.NavIndex(to)] ?? settlementAt[terrain.NavIndex(from)];
        if (s?.Walls is not Walls w) return to;
        const float band = 2.5f;
        float r0 = Vector2.Distance(from, s.Pos), r1 = Vector2.Distance(to, s.Pos);
        bool crossing = (r0 >= w.Radius) != (r1 >= w.Radius) || MathF.Abs(r1 - w.Radius) < band;
        if (!crossing) return to;
        int seg = w.SegmentAt(s.Pos, to);
        if (w.Passable(seg, s.Owner == u.Team)) return to;   // a breach, or our own gate
        if (s.Owner == u.Team) { // our own wall: just can't walk through it (no hacking at it)
            Vector2 d0 = to - s.Pos;
            d0 = d0.LengthSquared() > 0.01f ? Vector2.Normalize(d0) : Vector2.UnitX;
            return s.Pos + d0 * (r0 >= w.Radius ? w.Radius + band : w.Radius - band);
        }
        u.BlockedBy = s;
        u.BlockedSeg = seg;
        Vector2 dir = to - s.Pos;
        dir = dir.LengthSquared() > 0.01f ? Vector2.Normalize(dir) : Vector2.UnitX;
        return s.Pos + dir * (r0 >= w.Radius ? w.Radius + band : w.Radius - band);
    }

    /// <summary>If a pixel's destination is on the other side of a wall from it, steer it to the nearest way
    /// through: its own side's gate, or a breach. Outside, it walks round the wall rather than into it. With no way
    /// through (an intact enemy wall) it heads straight on - and will be stopped at the wall and hack at it.</summary>
    Vector2 WallDetour(Unit u, Vector2 desired)
    {
        var s = settlementAt[terrain.NavIndex(u.Pos)];
        if (s?.Walls is not Walls w) return desired;
        float ru = Vector2.Distance(u.Pos, s.Pos);
        bool inside = ru < w.Radius, wantIn = Vector2.Distance(desired, s.Pos) < w.Radius;
        if (inside == wantIn) return desired;

        bool owner = s.Owner == u.Team;
        int best = -1;
        float bestD = float.MaxValue;
        for (int seg = 0; seg < w.N; seg++)
        {
            if (!w.Passable(seg, owner)) continue;
            float a = w.SegmentAngle(seg);
            Vector2 mid = s.Pos + new Vector2(MathF.Cos(a), MathF.Sin(a)) * w.Radius;
            float d = Vector2.DistanceSquared(mid, u.Pos);
            if (d < bestD) { bestD = d; best = seg; }
        }
        if (best < 0) return desired;

        float ga = w.SegmentAngle(best);
        Vector2 radial = new(MathF.Cos(ga), MathF.Sin(ga));
        Vector2 gateMid = s.Pos + radial * w.Radius;
        Vector2 approach = gateMid + radial * (inside ? -7f : 7f);
        // close to the gate: step through it
        if (Vector2.DistanceSquared(u.Pos, approach) < 8f * 8f || Vector2.DistanceSquared(u.Pos, gateMid) < 9f * 9f)
            return gateMid + radial * (inside ? 12f : -12f);
        if (inside) return approach;   // the inside of the ring is open: walk straight there

        // outside: circle round the wall towards the gate instead of walking into it
        float ua = MathF.Atan2(u.Pos.Y - s.Pos.Y, u.Pos.X - s.Pos.X);
        float delta = MathF.IEEERemainder(ga - ua, MathF.Tau);
        if (MathF.Abs(delta) < 0.45f) return approach;
        float stepA = ua + MathF.Sign(delta) * 0.45f;
        return s.Pos + new Vector2(MathF.Cos(stepA), MathF.Sin(stepA)) * (w.Radius + 14f);
    }

    /// <summary>Idle archers inside their own walls take up posts along the wall-walk, spread all the way round,
    /// so they can shoot at anyone approaching.</summary>
    void ManRamparts(City c, Walls w)
    {
        grid.Query(c.Pos, w.Radius, scratch, SpatialGrid.Only(c.Owner));
        var archers = scratch.Where(u => u.Kind == UnitKind.Archer && !u.Forced && u.Squad == null && u.Form == null).ToList();
        if (archers.Count == 0) return;
        int slots = w.N * 3;
        var free = Enumerable.Range(0, slots).Select(k =>
        {
            float a = -MathF.PI + (k + 0.5f) * MathF.Tau / slots;
            return c.Pos + new Vector2(MathF.Cos(a), MathF.Sin(a)) * (w.Radius - CastleArt.Scale * 1.6f);
        }).ToList();
        // archers already on a post keep it
        foreach (var u in archers.OrderBy(u => free.Min(p => Vector2.DistanceSquared(p, u.Target))))
        {
            if (free.Count == 0) break;
            var post = free.OrderBy(p => Vector2.DistanceSquared(p, u.Target)).First();
            free.Remove(post);
            u.Target = post;
        }
    }

    /// <summary>True if an intact wall section (or a closed gate) stands between two points.</summary>
    bool WallBetween(Vector2 a, Vector2 b)
    {
        var s = settlementAt[terrain.NavIndex(a)] ?? settlementAt[terrain.NavIndex(b)];
        if (s?.Walls is not Walls w) return false;
        bool ina = Vector2.DistanceSquared(a, s.Pos) < w.Radius * w.Radius;
        bool inb = Vector2.DistanceSquared(b, s.Pos) < w.Radius * w.Radius;
        if (ina == inb) return false;
        return !w.Down[w.SegmentAt(s.Pos, (a + b) / 2)];
    }

    /// <summary>Inside its own walls while every section still stands.</summary>
    bool HoldingWalls(Unit u)
    {
        var s = settlementAt[terrain.NavIndex(u.Pos)];
        return s?.Walls is Walls w && s.Owner == u.Team && !w.Down.Any(d => d) &&
               Vector2.DistanceSquared(u.Pos, s.Pos) < w.Radius * w.Radius;
    }

    readonly List<Unit> defenders = new();

    /// <summary>When an enemy comes at the walls, the idle infantry and cavalry inside gather just behind the
    /// section that is under attack (or at a breach, once one opens), ready to meet whoever comes through.</summary>
    void ManBreachPoint(City c, Walls w)
    {
        grid.Query(c.Pos, w.Radius + 90f, scratch, SpatialGrid.Except(c.Owner) & ~SpatialGrid.Only(Teams.Neutral));
        if (scratch.Count == 0) return;
        Vector2 foe = Vector2.Zero;
        foreach (var e in scratch) foe += e.Pos;
        foe /= scratch.Count;

        int seg = w.SegmentAt(c.Pos, foe);
        var breaches = Enumerable.Range(0, w.N).Where(s => w.Down[s]).ToList();
        if (breaches.Count > 0)
            seg = breaches.OrderBy(s => MathF.Abs(MathF.IEEERemainder(w.SegmentAngle(s) - w.SegmentAngle(seg), MathF.Tau))).First();

        grid.Query(c.Pos, w.Radius, defenders, SpatialGrid.Only(c.Owner));
        var troops = defenders.Where(u => u.Kind != UnitKind.Archer && !u.Forced && u.Squad == null && u.Form == null).ToList();
        if (troops.Count == 0) return;
        float a = w.SegmentAngle(seg);
        Vector2 dir = new(MathF.Cos(a), MathF.Sin(a));
        Vector2 post = c.Pos + dir * MathF.Max(6f, w.Radius - 14f - MathF.Sqrt(troops.Count) * 2f);
        troops.Sort((p, q) => Vector2.DistanceSquared(p.Pos, post).CompareTo(Vector2.DistanceSquared(q.Pos, post)));
        for (int i = 0; i < troops.Count; i++) troops[i].Target = post + Spiral(i) * 1.15f;
    }

    /// <summary>True if this pixel stands behind its own side's (mostly intact) walls.</summary>
    bool BehindOwnWalls(Unit u)
    {
        var s = settlementAt[terrain.NavIndex(u.Pos)];
        return s?.Walls is Walls w && s.Owner == u.Team && w.IntactFraction > 0.5f &&
               Vector2.DistanceSquared(u.Pos, s.Pos) < w.Radius * w.Radius;
    }

    void MoveOverLand(Unit u, float dt)
    {
        Vector2 next = ClampWorld(u.Pos + u.Vel * dt);
        if (!terrain.IsWater(next) || terrain.IsWater(u.Pos)) { u.Pos = next; return; }
        var slideX = new Vector2(next.X, u.Pos.Y);
        var slideY = new Vector2(u.Pos.X, next.Y);
        if (!terrain.IsWater(slideX)) { u.Pos = slideX; u.Vel.Y = 0; }
        else if (!terrain.IsWater(slideY)) { u.Pos = slideY; u.Vel.X = 0; }
        else u.Vel = Vector2.Zero;
    }

    static float HighGroundMultiplier(float heightDiff) =>
        Math.Clamp(1f + heightDiff * Config.HighGroundDamage, 0.5f, 2.5f);

    City?[] settlementAt = Array.Empty<City?>();   // nav cell -> settlement whose walls cover it

    void BuildSettlementMap()
    {
        settlementAt = new City?[terrain.NW * terrain.NH];
        for (int i = 0; i < settlementAt.Length; i++)
        {
            Vector2 p = terrain.NavCenter(i);
            foreach (var c in cities)
                if (Vector2.DistanceSquared(p, c.Pos) < c.Radius * c.Radius) { settlementAt[i] = c; break; }
        }
    }

    /// <summary>The settlement whose walls currently protect this pixel (its side owns it), if any.</summary>
    City? FriendlySettlement(Unit u)
    {
        var c = settlementAt[terrain.NavIndex(u.Pos)];
        if (c == null) return null;
        bool friendly = c.Owner == u.Team || (u.Team == Teams.Neutral && u.Home == c);
        return friendly && Vector2.DistanceSquared(u.Pos, c.Pos) < c.Radius * c.Radius ? c : null;
    }
    /// <summary>Applies damage after the target's defences: unit type, braced line, castle walls.</summary>
    void DealDamage(Unit target, float dmg, Unit? attacker)
    {
        if (target.Dead) return;
        dmg *= target.S.DamageTaken * target.Brace;
        if (FriendlySettlement(target) is City walls) dmg *= walls.Defense; // castles protect far better than towns
        target.Hp -= dmg;
        target.SinceHurt = 0;
        target.HitFlash = 0.12f;
        if (target.Hp > 0) return;

        target.Dead = true;
        if (attacker != null && !attacker.Dead) GrantKill(attacker);
    }

    static void GrantKill(Unit u)
    {
        int before = u.Rank;
        u.Kills++;
        if (u.Rank > before)
        {
            u.Hp = MathF.Min(u.MaxHp, u.Hp + u.MaxHp * 0.3f); // a rush of confidence
            u.RankFlash = 0.8f;
        }
    }

    void FireArrow(Unit shooter, Unit target, float dmg)
    {
        float flight = Vector2.Distance(shooter.Pos, target.Pos) / Config.ArrowSpeed;
        // aim where the target is heading, with a little scatter - moving targets can be missed
        Vector2 dest = target.Pos + target.Vel * flight + RandomDir() * (float)rng.NextDouble() * 1.5f;
        Vector2 d = dest - shooter.Pos;
        arrows.Add(new Arrow
        {
            Pos = shooter.Pos, Dest = dest, Damage = dmg, Team = shooter.Team, Shooter = shooter,
            Dir = d.LengthSquared() > 0.01f ? Vector2.Normalize(d) : Vector2.UnitX,
        });
    }

    void UpdateArrows(float dt)
    {
        float step = Config.ArrowSpeed * dt;
        foreach (var a in arrows)
        {
            Vector2 to = a.Dest - a.Pos;
            float d = to.Length();
            if (d > step)
            {
                a.Pos += to / d * step;
                continue;
            }

            a.Pos = a.Dest;
            a.Done = true;
            grid.Query(a.Dest, 2.5f, scratch, SpatialGrid.Except(a.Team));
            Unit? hit = null;
            float best = float.MaxValue;
            foreach (var n in scratch)
            {
                if (n.Team == a.Team || n.Dead) continue;
                float d2 = Vector2.DistanceSquared(n.Pos, a.Dest);
                if (d2 < best) { best = d2; hit = n; }
            }
            if (hit != null) DealDamage(hit, a.Damage, a.Shooter);
        }
        arrows.RemoveAll(a => a.Done);
    }

    // ------------------------------------------------------------------ trade caravans

    float caravanTimer;

    void UpdateCaravans(float dt)
    {
        caravanTimer -= dt;
        if (caravanTimer <= 0)
        {
            caravanTimer = Config.CaravanInterval * (0.6f + 0.8f * (float)rng.NextDouble());
            if (caravans.Count < Config.MaxCaravans) SpawnCaravan();
        }

        foreach (var cv in caravans)
        {
            // travel: along the shared route, quicker on roads
            float toDest = Vector2.Distance(cv.Pos, cv.To.Pos);
            if (toDest < cv.To.Radius * 0.5f)
            {
                cv.Done = true;
                PayCaravan(cv);
                continue;
            }
            Vector2 next = toDest < 60f ? cv.To.Pos : cv.Flow.Next(cv.Pos);
            Vector2 d = next - cv.Pos;
            if (d.LengthSquared() > 0.01f)
            {
                cv.Heading = Vector2.Normalize(d);
                int roadKind = terrain.RoadKindAt(cv.Pos);
                float speed = Config.CaravanSpeed * (roadKind == 2 ? Config.PavedBonus : roadKind == 1 ? Config.DirtBonus
                              : terrain.SwampAt(cv.Pos) ? 0.55f : terrain.ForestAt(cv.Pos) ? 0.8f : 1f);
                cv.Pos += cv.Heading * MathF.Min(speed * dt, d.Length());
            }
            if (Vector2.DistanceSquared(cv.Pos, cv.LastPrint) > 9f)
            {
                tracks.Ruts(cv.Pos, cv.Heading);
                cv.LastPrint = cv.Pos;
            }

            // anyone who isn't a trading partner can fall on it
            int partners = (cv.From.Owner >= 0 ? 1 << cv.From.Owner : 0) | (cv.To.Owner >= 0 ? 1 << cv.To.Owner : 0);
            grid.Query(cv.Pos, 7f, scratch, SpatialGrid.AllTeams & ~partners & ~(1 << Teams.Neutral));
            if (scratch.Count == 0) continue;
            cv.Hp -= scratch.Count * 2.5f * dt;
            if (cv.Hp > 0) continue;

            cv.Done = true;
            int raider = scratch[0].Team;
            gold[raider] += cv.Value;
            if (raider == Teams.Player) ShowToast($"Caravan plundered! +{cv.Value} gold");
            else if (cv.From.Owner == Teams.Player || cv.To.Owner == Teams.Player)
                ShowToast($"Your caravan to {cv.To.Name} was plundered by the {Teams.Names[raider]}!");
        }
        caravans.RemoveAll(cv => cv.Done);
    }

    /// <summary>Gold actually received: rival houses get a reduced share (they start owning most of the map).
    /// Fractions carry over between payments so nothing is lost to rounding.</summary>
    readonly float[] goldFraction = new float[Teams.Count];
    int Earn(int team, int amount)
    {
        if (team == Teams.Player) return amount;
        goldFraction[team] += amount * Config.AiIncomeFactor;
        int whole = (int)goldFraction[team];
        goldFraction[team] -= whole;
        return whole;
    }

    void SpawnCaravan()
    {
        var origins = cities.Where(c => c.Owner >= 0 && c.Owner != Teams.Neutral).ToList();
        if (origins.Count == 0) return;
        var from = origins[rng.Next(origins.Count)];
        var dests = cities.Where(c => c != from)
            .Where(c => { float d = Vector2.Distance(c.Pos, from.Pos); return d > Config.CaravanMinTrip && d < Config.CaravanMaxTrip; })
            .ToList();
        if (dests.Count == 0) return;
        var to = dests[rng.Next(dests.Count)];

        Vector2 start = terrain.NearestLand(from.Pos + RandomDir() * from.Radius * 0.3f);
        var flow = GetFlow(to.Pos, start);
        if (!flow.Covers(start)) return; // no way there
        caravans.Add(new Caravan
        {
            From = from, To = to, Team = from.Owner, Pos = start, LastPrint = start, Flow = flow,
            Value = 8 + (int)(Vector2.Distance(from.Pos, to.Pos) / 80f),
        });
    }

    void PayCaravan(Caravan cv)
    {
        if (cv.From.Owner >= 0) gold[cv.From.Owner] += Earn(cv.From.Owner, cv.Value);
        if (cv.To.Owner >= 0) gold[cv.To.Owner] += Earn(cv.To.Owner, cv.Value);
        if (cv.From.Owner == Teams.Player || cv.To.Owner == Teams.Player)
            ShowToast($"Caravan {cv.From.Name} -> {cv.To.Name} arrived: +{cv.Value} gold");
    }
    readonly int[] cityCounts = new int[Teams.Count];

    float rampartTimer;

    void UpdateCities(float dt)
    {
        rampartTimer -= dt;
        bool manWalls = rampartTimer <= 0;
        if (manWalls) rampartTimer = 1f;
        foreach (var c in cities)
        {
            grid.Query(c.Pos, c.Radius, scratch);
            Array.Clear(cityCounts);
            foreach (var n in scratch) cityCounts[n.Team]++;

            int present = -1, teamsPresent = 0;
            for (int t = 0; t < Teams.Count; t++)
                if (cityCounts[t] > 0) { teamsPresent++; present = t; }

            if (teamsPresent == 1 && present != Teams.Neutral)
            {
                float rate = Config.CaptureRate * (1f + MathF.Min(cityCounts[present], 40) * 0.05f) / (c.IsTown ? c.Size * 0.6f : c.Size);
                if (c.Owner == present)
                {
                    c.CaptureTeam = present;
                    c.Capture = MathF.Min(1f, c.Capture + rate * dt);
                }
                else if (c.CaptureTeam != present && c.Capture > 0)
                {
                    // first wear down the current owner's / capturer's hold
                    c.Capture -= rate * dt;
                    if (c.Capture <= 0)
                    {
                        c.Capture = 0;
                        if (c.Owner == Teams.Player) ShowToast($"{c.Name} has been neutralised!");
                        c.Owner = -1;
                        c.CaptureTeam = present;
                    }
                }
                else
                {
                    c.CaptureTeam = present;
                    c.Capture += rate * dt;
                    if (c.Capture >= 1f)
                    {
                        c.Capture = 1f;
                        c.Owner = present;
                        if (present == Teams.Player) ShowToast($"You captured the {c.Kind.ToLower()} of {c.Name}! (+{c.Income} gold/s)");
                    }
                }
            }
            else if (teamsPresent == 0)
            {
                if (c.Owner >= 0) { c.CaptureTeam = c.Owner; c.Capture = MathF.Min(1f, c.Capture + 0.05f * dt); }
                else c.Capture = MathF.Max(0f, c.Capture - 0.05f * dt);
            }

            // walls slowly mend when no enemy is about and they haven't been struck for a while
            if (c.Walls is Walls walls)
            {
                if (manWalls && c.Owner >= 0) { ManRamparts(c, walls); ManBreachPoint(c, walls); }
                bool contested = false;
                for (int t = 0; t < Teams.Count; t++) if (t != c.Owner && cityCounts[t] > 0) contested = true;
                if (!contested)
                    for (int seg = 0; seg < walls.N; seg++)
                        if (walls.Hp[seg] < walls.SegMax(seg) && time - walls.LastHit[seg] > 12f)
                        {
                            walls.Hp[seg] = MathF.Min(walls.SegMax(seg), walls.Hp[seg] + walls.MaxHp * 0.015f * dt);
                            if (walls.Down[seg] && walls.Hp[seg] >= walls.SegMax(seg) * 0.5f) walls.Down[seg] = false;
                        }
            }

            if (c.Owner >= 0)
            {
                c.IncomeTimer += dt;
                while (c.IncomeTimer >= 1f)
                {
                    c.IncomeTimer -= 1f;
                    gold[c.Owner] += Earn(c.Owner, c.Income);
                }
            }
        }
    }

    bool IsAlive(int team) =>
        teamCount[team] > 0 || cities.Any(c => c.Owner == team) || gold[team] >= Stats.Infantry.Cost;

    void CheckEnd()
    {
        if (!IsAlive(Teams.Player)) { gameOver = true; victory = false; }
        else if (Enumerable.Range(1, Config.AiCount).All(t => !IsAlive(t))) { gameOver = true; victory = true; }
    }
}
