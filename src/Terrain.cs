using System.Numerics;
using Raylib_cs;

namespace PixelLegion;

/// <summary>A road: dirt tracks between minor places, paved roads to the big ones.</summary>
sealed class Road
{
    public List<Vector2> Points = new();
    public bool Paved;
}

/// <summary>Procedural heightmap rendered as a medieval-style terrain map, with an optional river (cut into the
/// land, crossed by bridges), swamps, forests, and a navigation grid. Height is 0..1; below
/// <see cref="WaterLevel"/> is water.</summary>
sealed class Terrain : IDisposable
{
    public const int Cell = 4;                 // world units per terrain texel
    public const float DeepLevel = 0.17f;
    public const float WaterLevel = 0.25f;
    public const float SandLevel = 0.256f;     // just a thin strip of shore; low plains stay green
    public const float HillLevel = 0.66f;
    public const float RockLevel = 0.78f;
    public const float SnowLevel = 0.90f;

    public readonly int W, H;
    readonly float[] height;
    readonly bool[] forest, swamp, riverTex, stream;
    readonly byte[] roadTex;   // painted roads: 1 dirt edge, 2 dirt, 3 paved edge, 4 paved, 5 bridge deck, 6 bridge rail
    readonly byte[] farmTex;   // crop fields: 0 none, 1-5 crop (+10 = furrow), 6 hedge, 7 hedgerow tree
    readonly int seed;
    public Texture2D Texture { get; private set; }

    public bool HasRiver { get; private set; }
    public readonly List<Vector2> Waterfalls = new();

    public Terrain(int seed)
    {
        this.seed = seed;
        W = (int)(Config.WorldW / Cell);
        H = (int)(Config.WorldH / Cell);
        height = new float[W * H];
        forest = new bool[W * H];
        swamp = new bool[W * H];
        riverTex = new bool[W * H];
        stream = new bool[W * H * 4];
        roadTex = new byte[W * H * 4];
        farmTex = new byte[W * H * 4];
        Generate();
        CarveRiver();
        TraceStreams();
        MarkSwamps();
        // the map is painted (Repaint) once roads and farmland are laid out

        NW = (int)(Config.WorldW / NavCell);
        NH = (int)(Config.WorldH / NavCell);
        nav = new bool[NW * NH];
        mainland = new bool[NW * NH];
        riverNav = new bool[NW * NH];
        bridge = new bool[NW * NH];
        roadKind = new byte[NW * NH];
        roadSnap = new Vector2[NW * NH];
        roadSnapDist = new float[NW * NH];
        navCost = new float[NW * NH];
        navHeight = new float[NW * NH];
        BuildNav();
    }

    public void Dispose() => Raylib.UnloadTexture(Texture);

    // ------------------------------------------------------------------ navigation (water is impassable)

    public const int NavCell = 16;              // world units per navigation cell
    public readonly int NW, NH;
    readonly bool[] nav;                        // walkable AND on the mainland (islands are cut off)
    readonly bool[] mainland;                   // the main landmass, counting river cells (bridgeable)
    readonly bool[] riverNav, bridge;
    readonly byte[] roadKind;                   // 0 none, 1 dirt track, 2 paved road
    readonly Vector2[] roadSnap;                // nearest point on the drawn road line, for road cells
    readonly float[] roadSnapDist;
    readonly float[] navCost;                   // time to cross a cell, relative to open ground
    readonly float[] navHeight;
    public readonly List<Road> Roads = new();

    public int NavIndex(Vector2 p) =>
        Math.Clamp((int)(p.Y / NavCell), 0, NH - 1) * NW + Math.Clamp((int)(p.X / NavCell), 0, NW - 1);

    public bool Walkable(int i) => nav[i];

    public Vector2 NavCenter(int i) => new((i % NW + 0.5f) * NavCell, (i / NW + 0.5f) * NavCell);

    /// <summary>Where to steer for in cell i: on a road, the road line itself; elsewhere the cell centre.</summary>
    public Vector2 SteerPoint(int i) => roadKind[i] > 0 ? roadSnap[i] : NavCenter(i);

    /// <summary>Dry land connected to the main continent - where things may be placed.</summary>
    public bool OnMainland(Vector2 p) => nav[NavIndex(p)] && !IsWater(p);

    public int RoadKindAt(Vector2 p) => roadKind[NavIndex(p)];
    public bool RoadAt(Vector2 p) => roadKind[NavIndex(p)] > 0;
    public bool BridgeAt(Vector2 p) => bridge[NavIndex(p)];
    public bool AnyBridge => Array.IndexOf(bridge, true) >= 0;

    void BuildNav()
    {
        var walk = new bool[NW * NH];
        for (int i = 0; i < walk.Length; i++)
        {
            Vector2 c = NavCenter(i);
            navHeight[i] = HeightAt(c);
            walk[i] = navHeight[i] >= WaterLevel + 0.01f;
            // a cell touched by the river is river (it can only be crossed by a bridge)
            riverNav[i] = RiverTexAt(c) || RiverTexAt(c + new Vector2(-6, -6)) || RiverTexAt(c + new Vector2(6, 6)) ||
                          RiverTexAt(c + new Vector2(6, -6)) || RiverTexAt(c + new Vector2(-6, 6));
            navCost[i] = SwampTexAt(c) ? 1.7f : ForestAt(c) ? 1.25f : 1f;
        }

        // the largest landmass, treating the river as bridgeable so both banks count
        var comp = new int[walk.Length];
        Array.Fill(comp, -1);
        int bestComp = -1, bestSize = 0, id = 0;
        var queue = new Queue<int>();
        bool Passable(int j) => walk[j] || riverNav[j];
        for (int s = 0; s < walk.Length; s++)
        {
            if (!Passable(s) || comp[s] >= 0) continue;
            int size = 0;
            comp[s] = id;
            queue.Enqueue(s);
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                size++;
                int x = i % NW, y = i / NW;
                foreach (var (dx, dy) in Orthogonal)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= NW || ny >= NH) continue;
                    int j = ny * NW + nx;
                    if (!Passable(j) || comp[j] >= 0) continue;
                    comp[j] = id;
                    queue.Enqueue(j);
                }
            }
            if (size > bestSize) { bestSize = size; bestComp = id; }
            id++;
        }
        for (int i = 0; i < nav.Length; i++)
        {
            mainland[i] = comp[i] == bestComp;
            nav[i] = mainland[i] && walk[i] && !riverNav[i];
        }
    }

    static readonly (int, int)[] Orthogonal = { (1, 0), (-1, 0), (0, 1), (0, -1) };

    /// <summary>Nearest walkable cell to i (breadth-first), or -1.</summary>
    int NearestWalkable(int start)
    {
        if (nav[start]) return start;
        var seen = new HashSet<int> { start };
        var queue = new Queue<int>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            if (nav[i]) return i;
            int x = i % NW, y = i / NW;
            foreach (var (dx, dy) in Orthogonal)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= NW || ny >= NH) continue;
                int j = ny * NW + nx;
                if (seen.Add(j)) queue.Enqueue(j);
            }
        }
        return -1;
    }

    /// <summary>Snaps a point to the nearest reachable land.</summary>
    public Vector2 NearestLand(Vector2 p)
    {
        if (OnMainland(p)) return p;
        int i = NearestWalkable(NavIndex(p));
        return i < 0 ? p : NavCenter(i);
    }

    /// <summary>Dijkstra outward from the goal. Stops a little after every start cell has been reached, so
    /// short moves stay cheap. Roads are fast (paved most of all), forests and swamps slow.</summary>
    public FlowField BuildFlow(Vector2 goal, IEnumerable<Vector2> starts, bool full = false)
    {
        var dist = new float[NW * NH];
        Array.Fill(dist, float.PositiveInfinity);
        int g = NearestWalkable(NavIndex(goal));
        if (g < 0) return new FlowField(this, dist, goal);

        var needed = new HashSet<int>();
        foreach (var s in starts)
        {
            int i = NavIndex(s);
            if (nav[i]) needed.Add(i);
        }
        int remaining = needed.Count;
        float limit = full ? float.PositiveInfinity : remaining == 0 ? 40f : float.PositiveInfinity;

        var pq = new PriorityQueue<int, float>();
        dist[g] = 0;
        pq.Enqueue(g, 0);
        while (pq.TryDequeue(out int i, out float d))
        {
            if (d > dist[i]) continue;
            if (d > limit) break;
            if (!full && remaining > 0 && needed.Remove(i) && --remaining == 0) limit = d + 25f;

            int x = i % NW, y = i / NW;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= NW || ny >= NH) continue;
                    int j = ny * NW + nx;
                    if (!nav[j]) continue;
                    bool diag = dx != 0 && dy != 0;
                    if (diag && (!nav[y * NW + nx] || !nav[ny * NW + x])) continue; // no corner cutting
                    float nd = d + (diag ? 1.4142f : 1f) * (navCost[i] + navCost[j]) * 0.5f;
                    if (nd < dist[j])
                    {
                        dist[j] = nd;
                        pq.Enqueue(j, nd);
                    }
                }
        }
        return new FlowField(this, dist, NavCenter(g));
    }

    // ------------------------------------------------------------------ roads and bridges

    /// <summary>A* road route between two points: avoids lakes, prefers gentle slopes, likes to merge with roads
    /// that already exist, and will cross the river (building a bridge) only when it has to.</summary>
    public List<Vector2>? FindRoadPath(Vector2 a, Vector2 b)
    {
        int s = NearestWalkable(NavIndex(a)), e = NearestWalkable(NavIndex(b));
        if (s < 0 || e < 0) return null;
        var gScore = new float[NW * NH];
        Array.Fill(gScore, float.PositiveInfinity);
        var came = new int[NW * NH];
        Array.Fill(came, -1);
        int ex = e % NW, ey = e / NW;
        float Heur(int i) => MathF.Sqrt(Sq(i % NW - ex) + Sq(i / NW - ey)) * 0.35f;
        bool Pass(int j) => nav[j] || (riverNav[j] && mainland[j]);

        var pq = new PriorityQueue<int, float>();
        gScore[s] = 0;
        pq.Enqueue(s, Heur(s));
        while (pq.TryDequeue(out int i, out _))
        {
            if (i == e) break;
            int x = i % NW, y = i / NW;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= NW || ny >= NH) continue;
                    int j = ny * NW + nx;
                    if (!Pass(j)) continue;
                    bool diag = dx != 0 && dy != 0;
                    if (diag && (!Pass(y * NW + nx) || !Pass(ny * NW + x))) continue;
                    float step = (diag ? 1.4142f : 1f) * (1f + 40f * MathF.Abs(navHeight[j] - navHeight[i])) * navCost[j];
                    if (roadKind[j] > 0) step *= 0.35f;
                    else if (riverNav[j] && !bridge[j]) step *= 12f;   // bridges are costly: build few of them
                    float ng = gScore[i] + step;
                    if (ng < gScore[j])
                    {
                        gScore[j] = ng;
                        came[j] = i;
                        pq.Enqueue(j, ng + Heur(j));
                    }
                }
        }
        if (came[e] < 0 && s != e) return null;

        var path = new List<Vector2>();
        for (int i = e; i >= 0; i = came[i]) path.Add(NavCenter(i));
        path.Reverse();
        path[0] = a;
        path[^1] = b;
        return path;
    }

    /// <summary>Smooths a road, records it for drawing and marks its cells as fast. Where it crosses the river,
    /// those cells become a bridge.</summary>
    public void AddRoad(List<Vector2> pts, bool paved)
    {
        for (int pass = 0; pass < 2; pass++) pts = Chaikin(pts);
        Roads.Add(new Road { Points = pts, Paved = paved });
        byte kind = (byte)(paved ? 2 : 1);
        PaintRoad(pts, paved);
        for (int k = 1; k < pts.Count; k++)
        {
            Vector2 a = pts[k - 1], b = pts[k];
            int steps = Math.Max(1, (int)(Vector2.Distance(a, b) / 3f));
            for (int s = 0; s <= steps; s++)
            {
                Vector2 p = Vector2.Lerp(a, b, s / (float)steps);
                int i = NavIndex(p);
                if (!mainland[i]) continue;
                if (riverNav[i]) { bridge[i] = true; nav[i] = true; }
                if (!nav[i]) continue;
                if (roadKind[i] == 0) roadSnapDist[i] = float.MaxValue;
                roadKind[i] = Math.Max(roadKind[i], kind);
                navCost[i] = roadKind[i] == 2 ? 1f / Config.PavedBonus : 1f / Config.DirtBonus;
                float d = Vector2.DistanceSquared(p, NavCenter(i));
                if (d < roadSnapDist[i]) { roadSnapDist[i] = d; roadSnap[i] = p; }
            }
        }
    }

    /// <summary>If the river has no crossing yet, finds a good spot near its middle (land on both banks) and
    /// returns the two bank points for a bridge.</summary>
    public (Vector2 a, Vector2 b)? FindBridgeSite()
    {
        if (!HasRiver || RiverPath.Count < 20) return null;
        int mid = RiverPath.Count / 2;
        for (int off = 0; off < RiverPath.Count / 2; off += 3)
            foreach (int k in new[] { mid + off, mid - off })
            {
                if (k < 2 || k >= RiverPath.Count - 2) continue;
                Vector2 along = Vector2.Normalize(RiverPath[k + 2] - RiverPath[k - 2]);
                Vector2 across = new(-along.Y, along.X);
                Vector2 a = RiverPath[k] + across * 40f, b = RiverPath[k] - across * 40f;
                if (OnMainland(a) && OnMainland(b)) return (a, b);
            }
        return null;
    }

    public static List<Vector2> Chaikin(List<Vector2> p)
    {
        if (p.Count < 3) return p;
        var r = new List<Vector2>(p.Count * 2) { p[0] };
        for (int i = 0; i < p.Count - 1; i++)
        {
            r.Add(Vector2.Lerp(p[i], p[i + 1], 0.25f));
            r.Add(Vector2.Lerp(p[i], p[i + 1], 0.75f));
        }
        r.Add(p[^1]);
        return r;
    }

    static float Sq(float v) => v * v;

    static float SmoothStep(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3 - 2 * t);
    }

    // ------------------------------------------------------------------ queries

    float Raw(int x, int y) => height[Math.Clamp(y, 0, H - 1) * W + Math.Clamp(x, 0, W - 1)];

    int Tex(Vector2 p) => Math.Clamp((int)(p.Y / Cell), 0, H - 1) * W + Math.Clamp((int)(p.X / Cell), 0, W - 1);

    /// <summary>Bilinear height at a world position.</summary>
    public float HeightAt(Vector2 p)
    {
        float fx = p.X / Cell - 0.5f, fy = p.Y / Cell - 0.5f;
        int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
        float tx = fx - x0, ty = fy - y0;
        float a = Raw(x0, y0), b = Raw(x0 + 1, y0), c = Raw(x0, y0 + 1), d = Raw(x0 + 1, y0 + 1);
        return (a + (b - a) * tx) + ((c + (d - c) * tx) - (a + (b - a) * tx)) * ty;
    }

    /// <summary>Water you can't walk on (a bridge makes river water walkable).</summary>
    public bool IsWater(Vector2 p) => HeightAt(p) < WaterLevel && !bridge[NavIndex(p)];
    public bool IsAnyWater(Vector2 p) => HeightAt(p) < WaterLevel;

    public bool ForestAt(Vector2 p) => forest[Tex(p)];
    bool SwampTexAt(Vector2 p) => swamp[Tex(p)];
    public bool SwampAt(Vector2 p) => swamp[Tex(p)];
    bool RiverTexAt(Vector2 p) => riverTex[Tex(p)];
    public bool RiverAt(Vector2 p) => riverTex[Tex(p)];

    /// <summary>Displayed elevation in feet, for the HUD.</summary>
    public static int Feet(float h) => (int)MathF.Round(MathF.Max(0, h - WaterLevel) / (1 - WaterLevel) * 3000f);

    public string Describe(Vector2 p)
    {
        float h = HeightAt(p);
        if (BridgeAt(p) && RiverAt(p)) return "Bridge";
        if (RiverAt(p) && h < WaterLevel) return "River";
        if (h < DeepLevel) return "Deep water";
        if (h < WaterLevel) return "Shallows";
        int road = RoadKindAt(p);
        if (road == 2) return "Paved road";
        if (road == 1) return "Dirt track";
        if (SwampAt(p)) return "Swamp";
        if (h < SandLevel) return "Shore";
        if (h >= SnowLevel) return "Snowy peak";
        if (h >= RockLevel) return "Crags";
        if (ForestAt(p)) return "Forest";
        if (h >= HillLevel) return "Hills";
        return "Meadow";
    }

    // ------------------------------------------------------------------ generation

    static float Hash(int x, int y, int s)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + s * 1442695041);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / 16777215f;
        }
    }

    /// <summary>Perlin-style gradient noise in 0..1. Unlike value noise it has no flat, grid-aligned patches,
    /// so very broad features don't show straight lines.</summary>
    static float GradientNoise(float x, float y, int s)
    {
        int xi = (int)MathF.Floor(x), yi = (int)MathF.Floor(y);
        float fx = x - xi, fy = y - yi;
        float u = fx * fx * fx * (fx * (fx * 6 - 15) + 10), v = fy * fy * fy * (fy * (fy * 6 - 15) + 10);
        float G(int ix, int iy, float dx, float dy)
        {
            float a = Hash(ix, iy, s) * MathF.Tau;
            return MathF.Cos(a) * dx + MathF.Sin(a) * dy;
        }
        float n00 = G(xi, yi, fx, fy), n10 = G(xi + 1, yi, fx - 1, fy);
        float n01 = G(xi, yi + 1, fx, fy - 1), n11 = G(xi + 1, yi + 1, fx - 1, fy - 1);
        float top = n00 + (n10 - n00) * u, bot = n01 + (n11 - n01) * u;
        return Math.Clamp(0.5f + (top + (bot - top) * v) * 0.75f, 0f, 1f);
    }

    static float Fbm(float x, float y, int s, int octaves)
    {
        float sum = 0, amp = 0.5f, norm = 0;
        for (int o = 0; o < octaves; o++)
        {
            sum += GradientNoise(x, y, s + o * 101) * amp;
            norm += amp;
            amp *= 0.5f;
            x *= 2.03f;
            y *= 2.03f;
        }
        return sum / norm;
    }

    void Generate()
    {
        // rows are independent, so generate them in parallel
        Parallel.For(0, H, y =>
        {
            for (int x = 0; x < W; x++)
            {
                // domain warp bends everything into long, natural-looking shapes
                float wx = x + 140f * (Fbm(x / 420f, y / 420f, seed + 11, 3) - 0.5f);
                float wy = y + 140f * (Fbm(x / 420f, y / 420f, seed + 12, 3) - 0.5f);

                // continental base: very broad, so plains and basins stretch a long way with little change
                float continent = Fbm(wx / 650f, wy / 650f, seed, 4);
                // mountain zones: only some regions carry ranges
                float mountains = SmoothStep(0.5f, 0.68f, Fbm(wx / 900f, wy / 900f, seed + 3, 3));
                // long sharp ridgelines inside those zones
                float ridge = 1f - MathF.Abs(2f * Fbm(wx / 300f, wy / 300f, seed + 7, 4) - 1f);
                ridge = ridge * ridge * ridge;
                // gentle detail, rougher in the mountains than on the plains
                float detail = Fbm(x / 70f, y / 70f, seed + 5, 3) - 0.5f;

                height[y * W + x] = continent * 0.62f
                                  + mountains * (0.12f + ridge * 0.95f)
                                  + detail * (0.03f + 0.08f * mountains)
                                  + (Fbm(wx / 220f, wy / 220f, seed + 21, 3) - 0.5f) * 0.10f; // gentle rolling ground
            }
        });
        float min = float.MaxValue, max = float.MinValue;
        foreach (float h in height) { min = MathF.Min(min, h); max = MathF.Max(max, h); }

        // normalise, then remap so ~13% of the map sits under WaterLevel
        for (int i = 0; i < height.Length; i++) height[i] = (height[i] - min) / (max - min);
        var sorted = (float[])height.Clone();
        Array.Sort(sorted);
        float wl = sorted[(int)(sorted.Length * 0.13f)];
        for (int i = 0; i < height.Length; i++)
        {
            float h = height[i];
            height[i] = h < wl ? WaterLevel * h / wl : WaterLevel + (1 - WaterLevel) * MathF.Pow((h - wl) / (1 - wl), 1.15f);
        }

        Parallel.For(0, H, y =>
        {
            for (int x = 0; x < W; x++)
            {
                float h = height[y * W + x];
                float m = Fbm(x / 90f, y / 90f, seed + 31, 5);
                forest[y * W + x] = h > SandLevel + 0.03f && h < RockLevel && m > 0.56f;
            }
        });
    }

    // ------------------------------------------------------------------ river

    public readonly List<Vector2> RiverPath = new();   // world points down the river's middle

    /// <summary>Most maps get a river: it rises in the high ground and runs down to a lake or the map edge,
    /// meandering as it goes. It is cut into the land, and waterfalls mark the steepest drops.</summary>
    void CarveRiver()
    {
        var r = new Random(seed ^ 0x2545F491);
        if (r.NextDouble() < 0.2) return; // some realms have no river
        for (int attempt = 0; attempt < 16; attempt++)
        {
            int sx = 0, sy = 0;
            float best = 0;
            for (int k = 0; k < 400; k++)
            {
                int x = 60 + r.Next(W - 120), y = 60 + r.Next(H - 120);
                float h = Raw(x, y);
                if (h > best && h < SnowLevel) { best = h; sx = x; sy = y; }
            }
            if (best < HillLevel - 0.08f) continue;
            var path = TraceRiver(sx, sy, r);
            if (path == null) continue;
            Carve(path);
            HasRiver = true;
            return;
        }
    }

    List<(int x, int y)>? TraceRiver(int x, int y, Random r)
    {
        var path = new List<(int, int)>();
        var visited = new HashSet<int>();
        float a = (float)r.NextDouble() * MathF.Tau;
        float dirX = MathF.Cos(a), dirY = MathF.Sin(a);
        float p1 = (float)r.NextDouble() * 10f, p2 = (float)r.NextDouble() * 10f;
        for (int step = 0; step < 9000; step++)
        {
            // the river's preferred heading swings from side to side as it goes, so it meanders
            float swing = 0.75f * MathF.Sin(step / 38f + p1) + 0.35f * MathF.Sin(step / 13f + p2);
            float cs = MathF.Cos(swing), sn = MathF.Sin(swing);
            float prefX = dirX * cs - dirY * sn, prefY = dirX * sn + dirY * cs;
            path.Add((x, y));
            visited.Add(y * W + x);
            if (x < 3 || y < 3 || x >= W - 3 || y >= H - 3) return path.Count > 350 ? path : null;
            if (Raw(x, y) < WaterLevel && path.Count > 40) return path.Count > 250 ? path : null;

            int bx = -1, by = -1;
            float bestScore = float.MaxValue, bdx = 0, bdy = 0;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = x + dx, ny = y + dy;
                    if (visited.Contains(ny * W + nx)) continue;
                    float len = MathF.Sqrt(dx * dx + dy * dy);
                    float ndx = dx / len, ndy = dy / len;
                    // downhill, keep flowing the same way, with a little wobble for meanders
                    float score = Raw(nx, ny) - (ndx * prefX + ndy * prefY) * 0.008f
                                + (Hash(nx / 6, ny / 6, seed + 77) - 0.5f) * 0.006f;
                    if (score < bestScore) { bestScore = score; bx = nx; by = ny; bdx = ndx; bdy = ndy; }
                }
            if (bx < 0) return null;
            dirX = dirX * 0.97f + bdx * 0.03f;
            dirY = dirY * 0.97f + bdy * 0.03f;
            float l = MathF.Sqrt(dirX * dirX + dirY * dirY);
            dirX /= l; dirY /= l;
            x = bx; y = by;
        }
        return null;
    }

    void Carve(List<(int x, int y)> rawPath)
    {
        // smooth the cell-by-cell trace into flowing curves, then resample it finely
        var pts = rawPath.Where((_, i) => i % 4 == 0).Select(p => new Vector2(p.x + 0.5f, p.y + 0.5f)).ToList();
        pts.Add(new Vector2(rawPath[^1].x + 0.5f, rawPath[^1].y + 0.5f));
        for (int pass = 0; pass < 3; pass++) pts = Chaikin(pts);
        var path = new List<Vector2>();
        for (int k = 1; k < pts.Count; k++)
        {
            float len = Vector2.Distance(pts[k - 1], pts[k]);
            int n = Math.Max(1, (int)(len / 0.7f));
            for (int s = 0; s < n; s++) path.Add(Vector2.Lerp(pts[k - 1], pts[k], s / (float)n));
        }

        var original = path.Select(p => Raw((int)p.X, (int)p.Y)).ToList();
        int lastFall = -100;
        for (int k = 0; k < path.Count; k++)
        {
            Vector2 c = path[k];
            int x = (int)c.X, y = (int)c.Y;
            float t = k / (float)path.Count;
            float rr = 1.2f + t * 2.4f;   // widens downstream
            int reach = (int)rr + 4;
            for (int dy = -reach; dy <= reach; dy++)
                for (int dx = -reach; dx <= reach; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
                    int i = ny * W + nx;
                    float d = Vector2.Distance(new Vector2(nx + 0.5f, ny + 0.5f), c);
                    if (d <= rr)
                    {
                        height[i] = MathF.Min(height[i], WaterLevel - 0.03f - 0.01f * t);
                        riverTex[i] = true;
                        forest[i] = false;
                    }
                    else if (d <= rr + 3)
                    {
                        height[i] = MathF.Min(height[i], WaterLevel + 0.006f + (d - rr) * 0.008f);
                        forest[i] = false;
                    }
                }
            if (k % 5 == 0) RiverPath.Add(c * Cell);
            // a steep drop in the original ground becomes a waterfall
            if (k >= 17 && original[k - 17] - original[k] > 0.05f && k - lastFall > 60)
            {
                Waterfalls.Add(c * Cell);
                lastFall = k;
            }
        }
    }

    /// <summary>Low, wet, flat ground just above the water turns to swamp in places.</summary>
    void MarkSwamps()
    {
        Parallel.For(0, H, y =>
        {
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                float h = height[i];
                if (h < WaterLevel || h > WaterLevel + 0.016f || forest[i]) continue;
                float slope = MathF.Abs(Raw(x + 1, y) - Raw(x - 1, y)) + MathF.Abs(Raw(x, y + 1) - Raw(x, y - 1));
                swamp[i] = slope < 0.008f && Fbm(x / 60f, y / 60f, seed + 41, 3) > 0.57f;
            }
        });
    }

    // ------------------------------------------------------------------ painted details (at twice the map resolution)

    /// <summary>World units per pixel of the painted map: twice as fine as the height grid.</summary>
    public const int TexCell = 2;
    public int TexW => W * 2;
    public int TexH => H * 2;

    /// <summary>Paints a road into the map: paved roads get kerbs and rows of setts, dirt tracks get two wheel
    /// ruts with grass between; over the river the road becomes a bridge with planks (or stone) and rails.</summary>
    void PaintRoad(List<Vector2> pts, bool paved)
    {
        int TW = TexW, TH = TexH;
        float fill = paved ? 3.0f : 1.9f, edge = paved ? 4.0f : 2.9f;
        for (int k = 1; k < pts.Count; k++)
        {
            Vector2 a = pts[k - 1] / TexCell, b = pts[k] / TexCell;
            Vector2 dir = b - a;
            if (dir.LengthSquared() < 0.0001f) continue;
            dir = Vector2.Normalize(dir);
            int n = Math.Max(1, (int)(Vector2.Distance(a, b) / 0.5f));
            for (int s = 0; s <= n; s++)
            {
                Vector2 p = Vector2.Lerp(a, b, s / (float)n);
                int cx = (int)p.X, cy = (int)p.Y;
                for (int dy = -5; dy <= 5; dy++)
                    for (int dx = -5; dx <= 5; dx++)
                    {
                        int x = cx + dx, y = cy + dy;
                        if (x < 0 || y < 0 || x >= TW || y >= TH) continue;
                        int i = y * TW + x;
                        Vector2 t = new(x + 0.5f, y + 0.5f);
                        float d = Vector2.Distance(t, p);
                        int hi = (y / 2) * W + x / 2;
                        bool overRiver = riverTex[hi] && height[hi] < WaterLevel;
                        byte v;
                        if (overRiver)
                        {
                            bool plank = ((int)MathF.Floor(Vector2.Dot(t, dir)) & 1) == 0;
                            v = d <= 3.6f ? (paved ? (byte)9 : plank ? (byte)5 : (byte)8) : d <= 4.8f ? (paved ? (byte)10 : (byte)6) : (byte)0;
                        }
                        else if (d <= fill)
                            v = paved ? (byte)4 : (d >= 0.7f && d <= 1.4f ? (byte)7 : (byte)2);   // dirt: ruts either side
                        else v = d <= edge ? (byte)(paved ? 3 : 1) : (byte)0;
                        if (v == 0) continue;
                        int Rank(byte r) => r switch { 9 or 5 or 8 => 9, 10 or 6 => 8, 4 => 6, 7 => 5, 2 => 5, 3 => 3, 1 => 2, _ => 0 };
                        if (Rank(v) > Rank(roadTex[i])) roadTex[i] = v;
                    }
            }
        }
    }

    /// <summary>Patchwork fields around a village or town: hedged plots with one-pixel furrows, thinning out with
    /// distance. Only on open, gentle, dry ground.</summary>
    public void AddFarmland(Vector2 center, float inner, float outer, int fieldSeed)
    {
        int TW = TexW, TH = TexH;
        float ang = Hash(fieldSeed, 3, 9) * MathF.PI;
        float ca = MathF.Cos(ang), sa = MathF.Sin(ang);
        int x0 = Math.Max(0, (int)((center.X - outer) / TexCell)), x1 = Math.Min(TW - 1, (int)((center.X + outer) / TexCell));
        int y0 = Math.Max(0, (int)((center.Y - outer) / TexCell)), y1 = Math.Min(TH - 1, (int)((center.Y + outer) / TexCell));
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                int hi = (y / 2) * W + x / 2;
                Vector2 wp = new((x + 0.5f) * TexCell, (y + 0.5f) * TexCell);
                float d = Vector2.Distance(wp, center);
                if (d < inner || d > outer) continue;
                if (height[hi] < WaterLevel + 0.01f || height[hi] > HillLevel || forest[hi] || swamp[hi] || riverTex[hi]) continue;
                int hx = x / 2, hy = y / 2;
                float slope = MathF.Abs(Raw(hx + 1, hy) - Raw(hx - 1, hy)) + MathF.Abs(Raw(hx, hy + 1) - Raw(hx, hy - 1));
                if (slope > 0.03f) continue;
                float u = (x * ca + y * sa) / 14f, v = (-x * sa + y * ca) / 10f;
                int fu = (int)MathF.Floor(u), fv = (int)MathF.Floor(v);
                float keep = 1f - (d - inner) / (outer - inner);
                if (Hash(fu, fv, fieldSeed) > keep * 1.1f) continue;
                bool hedge = u - fu < 0.08f || v - fv < 0.11f;
                if (hedge) { farmTex[y * TW + x] = (byte)(Hash(x, y, fieldSeed + 5) < 0.2f ? 7 : 6); continue; }   // hedge, the odd hedgerow tree
                byte crop = (byte)(1 + (int)(Hash(fu, fv, fieldSeed + 1) * 4.99f));
                bool furrow = ((int)MathF.Floor((Hash(fu, fv, fieldSeed + 2) < 0.5f ? u * 14f : v * 10f)) & 1) == 0;
                farmTex[y * TW + x] = (byte)(crop + (furrow ? 10 : 0));
            }
    }

    /// <summary>Small streams: trickle downhill from the high ground until they reach water or a hollow.
    /// They are only painted on (fordable), not obstacles.</summary>
    void TraceStreams()
    {
        var r = new Random(seed ^ 0x51ED27);
        int made = 0, TW = TexW;
        for (int attempt = 0; attempt < 60 && made < 10; attempt++)
        {
            int x = 40 + r.Next(W - 80), y = 40 + r.Next(H - 80);
            float h0 = Raw(x, y);
            if (h0 < 0.45f || h0 > RockLevel) continue;
            var path = new List<Vector2>();
            var seen = new HashSet<int>();
            for (int step = 0; step < 500; step++)
            {
                int i = y * W + x;
                if (height[i] < WaterLevel) break;
                path.Add(new Vector2(x + 0.5f, y + 0.5f));
                seen.Add(i);
                int bx = -1, by = -1;
                float best = float.MaxValue;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = x + dx, ny = y + dy;
                        if (nx < 1 || ny < 1 || nx >= W - 1 || ny >= H - 1 || seen.Contains(ny * W + nx)) continue;
                        float s = Raw(nx, ny) + (Hash(nx, ny, seed + 61) - 0.5f) * 0.004f;
                        if (s < best) { best = s; bx = nx; by = ny; }
                    }
                if (bx < 0 || best > height[i] + 0.004f) break; // a hollow: it soaks away
                x = bx; y = by;
            }
            if (path.Count < 40) continue;
            // smooth it and paint a thin wiggling line at the fine resolution
            var pts = Chaikin(Chaikin(path.Where((_, k) => k % 3 == 0).ToList()));
            for (int k = 1; k < pts.Count; k++)
            {
                Vector2 a = pts[k - 1] * 2, b = pts[k] * 2;
                int n = Math.Max(1, (int)(Vector2.Distance(a, b) * 2));
                for (int s = 0; s <= n; s++)
                {
                    Vector2 p = Vector2.Lerp(a, b, s / (float)n);
                    int px = (int)p.X, py = (int)p.Y;
                    if (px >= 0 && py >= 0 && px < TW && py < TexH) stream[py * TW + px] = true;
                }
            }
            made++;
        }
    }

    /// <summary>Renders the map once roads and farmland have been laid out.</summary>
    public void Repaint()
    {
        if (Texture.Id != 0) Raylib.UnloadTexture(Texture);
        Texture = BuildTexture();
    }

    // ------------------------------------------------------------------ texture

    static Color C(int r, int g, int b) => Teams.Col(r, g, b);

    static Color Mix(Color a, Color b, float t) => Teams.Lerp(a, b, Math.Clamp(t, 0, 1));

    static Color Shade(Color c, float f) => Teams.Col(
        Math.Clamp((int)(c.R * f), 0, 255), Math.Clamp((int)(c.G * f), 0, 255), Math.Clamp((int)(c.B * f), 0, 255));

    /// <summary>Height at a fine map pixel (bilinear between height-grid points).</summary>
    float HT(int x, int y)
    {
        float fx = (x + 0.5f) / 2f - 0.5f, fy = (y + 0.5f) / 2f - 0.5f;
        int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
        float tx = fx - x0, ty = fy - y0;
        float a = Raw(x0, y0), b = Raw(x0 + 1, y0), c = Raw(x0, y0 + 1), d = Raw(x0 + 1, y0 + 1);
        float top = a + (b - a) * tx, bot = c + (d - c) * tx;
        return top + (bot - top) * ty;
    }

    /// <summary>Tree crowns: each 5x5 cell may hold a tree; the texel takes the crown of the front-most tree that
    /// covers it, lit from the top-left. Returns false if no crown covers it.</summary>
    bool TreeAt(int x, int y, int cellSize, float chance, Func<int, int, bool> grows, out Color col)
    {
        col = default;
        int bestY = int.MinValue;
        int cx0 = x / cellSize, cy0 = y / cellSize;
        for (int cy = cy0 - 1; cy <= cy0 + 1; cy++)
            for (int cx = cx0 - 1; cx <= cx0 + 1; cx++)
            {
                if (Hash(cx, cy, seed + 91) > chance) continue;
                float tx = cx * cellSize + 1 + Hash(cx, cy, seed + 92) * (cellSize - 2);
                float ty = cy * cellSize + 1 + Hash(cx, cy, seed + 93) * (cellSize - 2);
                if (!grows((int)tx, (int)ty)) continue;
                float r = 2.0f + Hash(cx, cy, seed + 94) * 1.4f;
                float dx = x + 0.5f - tx, dy = y + 0.5f - ty;
                if (dx * dx + dy * dy > r * r || (int)ty < bestY) continue;
                bestY = (int)ty;
                float light = -(dx + dy) / (r * 1.6f);                    // lit from the top-left
                Color baseCol = Hash(cx, cy, seed + 95) < 0.3f ? C(58, 92, 50) : Hash(cx, cy, seed + 95) < 0.6f ? C(70, 104, 54) : C(84, 112, 58);
                col = Shade(baseCol, 1f + light * 0.45f);
                if (dx < -r * 0.3f && dy < -r * 0.3f && dx > -r * 0.75f) col = Shade(baseCol, 1.3f); // highlight
            }
        return bestY != int.MinValue;
    }

    Texture2D BuildTexture()
    {
        int TW = TexW, TH = TexH;
        Color deep = C(46, 72, 98), shallow = C(78, 112, 132), sand = C(204, 186, 134), river = C(84, 124, 148);
        Color meadowLo = C(116, 140, 74), meadowHi = C(142, 148, 84), hills = C(150, 134, 92);
        Color rock = C(128, 118, 108), snow = C(234, 232, 224);
        Color undergrowth = C(40, 62, 36);
        Color bog = C(92, 104, 64), puddle = C(66, 88, 84), reeds = C(150, 140, 80);
        Color parchment = C(214, 196, 150);

        var pixels = new Color[TW * TH];
        Parallel.For(0, TH, y =>
        {
            for (int x = 0; x < TW; x++)
            {
                int i = y * TW + x, hi = (y / 2) * W + x / 2;
                float h = HT(x, y);
                bool water = h < WaterLevel;
                Color c;
                if (water)
                {
                    if (riverTex[hi])
                    {
                        c = river;
                        if (Hash(x / 3, y, seed + 13) < 0.1f) c = Shade(c, 1.16f);           // ripples of current
                    }
                    else
                    {
                        c = h < DeepLevel ? deep : Mix(deep, shallow, (h - DeepLevel) / (WaterLevel - DeepLevel));
                        if ((x + y * 3) % 13 == 0 && Hash(x / 4, y, seed + 5) < 0.3f) c = Shade(c, 1.2f); // little waves
                    }
                }
                else
                {
                    bool shore = HT(x + 5, y) < WaterLevel || HT(x - 5, y) < WaterLevel || HT(x, y + 5) < WaterLevel || HT(x, y - 5) < WaterLevel;
                    if (h < SandLevel && shore) c = Shade(sand, 0.94f + Hash(x, y, seed + 21) * 0.1f);
                    else if (h < HillLevel) c = Mix(meadowLo, meadowHi, MathF.Max(0f, h - SandLevel) / (HillLevel - SandLevel));
                    else if (h < RockLevel) c = Mix(hills, rock, (h - HillLevel) / (RockLevel - HillLevel));
                    else if (h < SnowLevel) c = Hash(x / 2, y / 2, seed + 23) < 0.15f ? Shade(rock, 0.85f) : rock;   // scree
                    else c = Hash(x, y, seed + 24) < 0.03f ? C(255, 255, 255) : snow;                               // sparkle

                    if (forest[hi])
                        c = TreeAt(x, y, 5, 0.9f, (tx, ty) => forest[Math.Clamp(ty / 2, 0, H - 1) * W + Math.Clamp(tx / 2, 0, W - 1)], out var tree)
                            ? tree : undergrowth;
                    else if (h > SandLevel + 0.02f && h < HillLevel && farmTex[i] == 0 && roadTex[i] == 0 &&
                             TreeAt(x, y, 11, 0.05f, (tx, ty) => true, out var lone))
                        c = lone;                                                                           // lone trees
                    if (swamp[hi])
                    {
                        float n = Hash(x / 2, y / 2, seed + 17);
                        c = n < 0.25f ? puddle : bog;
                        if (Hash(x, y, seed + 18) > 0.93f || (Hash(x, y - 1, seed + 18) > 0.93f)) c = reeds; // reed tufts
                    }
                    int f = farmTex[i];
                    if (f > 0)
                    {
                        int crop = f % 10;
                        Color cc = crop switch
                        {
                            1 => C(208, 182, 96), 2 => C(186, 170, 106), 3 => C(128, 154, 74), 4 => C(136, 106, 72),
                            5 => C(152, 164, 120), 6 => C(66, 94, 52), _ => C(52, 82, 44),
                        };
                        c = f >= 10 ? Shade(cc, 0.84f) : cc;   // furrows
                    }
                    if (stream[i]) c = Hash(x, y, seed + 25) < 0.3f ? C(130, 168, 186) : C(96, 136, 160);

                    // hill shading, light from the top-left
                    float dx = HT(x + 2, y) - HT(x - 2, y);
                    float dy = HT(x, y + 2) - HT(x, y - 2);
                    c = Shade(c, Math.Clamp(1f - (dx + dy) * 22f, 0.6f, 1.35f));

                    // contour lines, like an old survey map
                    int band = (int)(h / 0.05f);
                    if (f == 0 && (band != (int)(HT(x + 1, y) / 0.05f) || band != (int)(HT(x, y + 1) / 0.05f)))
                        c = Shade(c, 0.82f);

                    c = Shade(c, 0.97f + Hash(x, y, seed + 3) * 0.06f);       // speckle
                }

                // coastline ink
                if (!water && (HT(x + 1, y) < WaterLevel || HT(x - 1, y) < WaterLevel || HT(x, y + 1) < WaterLevel || HT(x, y - 1) < WaterLevel))
                    c = C(70, 60, 44);

                // roads and bridges
                switch (roadTex[i])
                {
                    case 1: c = Mix(c, C(118, 94, 62), 0.55f); break;                                          // soft verge
                    case 2: c = Shade(C(174, 148, 102), 0.92f + Hash(x, y, seed + 71) * 0.14f); break;         // packed earth
                    case 7: c = C(132, 106, 70); break;                                                        // wheel ruts
                    case 3: c = C(96, 90, 82); break;                                                          // kerb
                    case 4:                                                                                    // setts in staggered rows
                        bool joint = y % 3 == 0 || (x + (y / 3 % 2) * 2) % 4 == 0;
                        c = joint ? C(126, 120, 112) : Shade(C(170, 164, 152), 0.92f + Hash(x / 4, y / 3, seed + 73) * 0.16f);
                        break;
                    case 5: c = C(150, 110, 66); break;                                                        // planks
                    case 8: c = C(124, 88, 52); break;
                    case 6: c = C(76, 52, 30); break;                                                          // rails
                    case 9: c = Shade(C(160, 154, 144), 0.94f + Hash(x / 2, y / 2, seed + 74) * 0.1f); break;  // stone bridge
                    case 10: c = C(108, 102, 94); break;                                                       // parapets
                }

                c = Mix(c, parchment, 0.12f); // aged-map tint
                pixels[i] = c;
            }
        });
        return TextureFromPixels(pixels, TW, TH, TextureFilter.Point);
    }

    /// <summary>Uploads a pixel array straight to the GPU in one go.</summary>
    public static unsafe Texture2D TextureFromPixels(Color[] pixels, int w, int h, TextureFilter filter)
    {
        Texture2D tex;
        fixed (Color* p = pixels)
        {
            var img = new Image { Data = p, Width = w, Height = h, Mipmaps = 1, Format = PixelFormat.UncompressedR8G8B8A8 };
            tex = Raylib.LoadTextureFromImage(img);
        }
        Raylib.SetTextureFilter(tex, filter);
        return tex;
    }

    public static unsafe void UpdateTexture(Texture2D tex, Color[] pixels)
    {
        fixed (Color* p = pixels) Raylib.UpdateTexture(tex, p);
    }
}


/// <summary>Travel-time map toward one goal. A unit looks at the cells around it and heads for the one
/// closest to the goal; on roads it steers for the road line itself, so traffic follows the road.</summary>
sealed class FlowField
{
    readonly Terrain t;
    readonly float[] dist;
    public readonly Vector2 Goal;

    public FlowField(Terrain t, float[] dist, Vector2 goal)
    {
        this.t = t;
        this.dist = dist;
        Goal = goal;
    }

    /// <summary>True if this field already knows the way from p (its search reached p's cell).</summary>
    public bool Covers(Vector2 p) => !float.IsPositiveInfinity(dist[t.NavIndex(p)]);

    public Vector2 Next(Vector2 pos)
    {
        int cx = Math.Clamp((int)(pos.X / Terrain.NavCell), 0, t.NW - 1);
        int cy = Math.Clamp((int)(pos.Y / Terrain.NavCell), 0, t.NH - 1);
        float best = dist[cy * t.NW + cx];
        int bi = -1;
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = cx + dx, ny = cy + dy;
                if (nx < 0 || ny < 0 || nx >= t.NW || ny >= t.NH) continue;
                int j = ny * t.NW + nx;
                if (!t.Walkable(j) || dist[j] >= best) continue;
                // a two-cell hop must not jump over water
                if ((Math.Abs(dx) == 2 || Math.Abs(dy) == 2) && !t.Walkable((cy + dy / 2) * t.NW + cx + dx / 2)) continue;
                best = dist[j];
                bi = j;
            }
        return bi < 0 ? Goal : t.SteerPoint(bi);
    }
}
