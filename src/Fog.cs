using System.Numerics;
using Raylib_cs;

namespace PixelLegion;

/// <summary>Fog of war for the player, on a coarse grid recomputed a few times a second.
/// Each cell is Hidden, Silhouette (raised ground seen from afar - enemies show only as a blur of colour) or Clear.
/// Hills block sight, forests block it after a couple of cells and hide what is inside them, high viewers see
/// further and high targets are easier to spot. Explored cells stay see-through (you remember the terrain) but
/// show nothing that is currently there.</summary>
sealed class FogOfWar : IDisposable
{
    public const byte Hidden = 0, Silhouette = 1, Clear = 2;
    const int C = Config.FogCell;

    public readonly int W, H;
    readonly float[] height;
    readonly bool[] forest;
    readonly byte[] vis;
    readonly bool[] explored;
    readonly float[] alpha;        // displayed fog opacity, eased toward its target so edges don't pop
    readonly Color[] pixels;
    readonly float[] viewerRange;
    readonly int[] viewerStamp;
    readonly List<int> viewers = new();
    int stamp;
    float timer;
    public Texture2D Texture { get; }

    static readonly Color FogColor = Teams.Col(22, 18, 13);

    public FogOfWar(Terrain t)
    {
        W = (int)MathF.Ceiling(Config.WorldW / C);
        H = (int)MathF.Ceiling(Config.WorldH / C);
        int n = W * H;
        height = new float[n];
        forest = new bool[n];
        vis = new byte[n];
        explored = new bool[n];
        alpha = new float[n];
        pixels = new Color[n];
        viewerRange = new float[n];
        viewerStamp = new int[n];
        Array.Fill(alpha, 1f);

        for (int i = 0; i < n; i++)
        {
            Vector2 c = new((i % W + 0.5f) * C, (i / W + 0.5f) * C);
            float h = 0;
            int trees = 0;
            for (int k = 0; k < 4; k++)
            {
                var p = c + new Vector2(k % 2 == 0 ? -8 : 8, k < 2 ? -8 : 8);
                h += t.HeightAt(p);
                if (t.ForestAt(p)) trees++;
            }
            height[i] = h / 4;
            forest[i] = trees >= 3;
        }
        Texture = Terrain.TextureFromPixels(pixels, W, H, TextureFilter.Bilinear);
    }

    public void Dispose() => Raylib.UnloadTexture(Texture);

    public int Index(Vector2 p) => Math.Clamp((int)(p.Y / C), 0, H - 1) * W + Math.Clamp((int)(p.X / C), 0, W - 1);
    public byte VisAt(Vector2 p) => vis[Index(p)];
    public bool IsClear(Vector2 p) => vis[Index(p)] == Clear;
    public bool IsExplored(Vector2 p) => explored[Index(p)];

    public void Update(float dt, List<Unit> units, List<City> cities, bool force = false)
    {
        timer -= dt;
        if (timer <= 0 || force)
        {
            timer = 0.25f;
            Recompute(units, cities);
        }

        // ease the displayed fog toward its target and upload it
        float step = force ? 1f : MathF.Min(1f, dt * 4f);
        for (int i = 0; i < alpha.Length; i++)
        {
            float target = vis[i] == Clear ? 0f : vis[i] == Silhouette ? 0.3f : explored[i] ? 0.55f : 1f;
            alpha[i] += (target - alpha[i]) * step;
            pixels[i] = Teams.WithAlpha(FogColor, (int)(alpha[i] * 255));
        }
        Terrain.UpdateTexture(Texture, pixels);
    }

    void Recompute(List<Unit> units, List<City> cities)
    {
        Array.Clear(vis);
        stamp++;
        viewers.Clear();

        void AddViewer(Vector2 p, float range)
        {
            int i = Index(p);
            if (viewerStamp[i] != stamp)
            {
                viewerStamp[i] = stamp;
                viewerRange[i] = range;
                viewers.Add(i);
            }
            else viewerRange[i] = MathF.Max(viewerRange[i], range);
        }

        foreach (var u in units)
            if (u.Team == Teams.Player && !u.Dead)
                AddViewer(u.Pos, Config.SightRange * (u.Kind == UnitKind.Cavalry ? 1.15f : 1f)); // riders scout a bit further
        foreach (var c in cities)
            if (c.Owner == Teams.Player) AddViewer(c.Pos, c.Radius + 150f);

        foreach (int v in viewers)
        {
            int vx = v % W, vy = v / W;
            if (IsInterior(vx, vy, v)) continue; // a viewer surrounded by others adds nothing new
            float hv = height[v] + 0.02f;       // eye height
            float rc = viewerRange[v] * (1f + Config.HillSightBonus * MathF.Max(0f, hv - 0.3f));
            float rf = rc * Config.SilhouetteFactor;
            int r = (int)MathF.Ceiling(rf / C);

            for (int dy = -r; dy <= r; dy++)
            {
                int ty = vy + dy;
                if (ty < 0 || ty >= H) continue;
                for (int dx = -r; dx <= r; dx++)
                {
                    int tx = vx + dx;
                    if (tx < 0 || tx >= W) continue;
                    int t = ty * W + tx;
                    if (vis[t] == Clear) continue;

                    if (Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1) { vis[t] = Clear; explored[t] = true; continue; }

                    float d = MathF.Sqrt(dx * dx + dy * dy) * C;
                    if (d > rf) continue;
                    float ht = height[t];
                    // things standing higher than us are easier to make out
                    bool inClearRange = d <= rc * (1f + 2.5f * MathF.Max(0f, ht - hv));

                    if (inClearRange)
                    {
                        if (forest[t] && d > MathF.Max(C * 1.5f, rc * 0.4f)) continue; // can't see into deep woods
                    }
                    else
                    {
                        if (vis[t] != Hidden || forest[t]) continue;
                        if (!(ht > hv + 0.03f || ht > Terrain.HillLevel)) continue; // only raised ground shows from afar
                    }

                    if (!LineOfSight(vx, vy, hv, dx, dy, ht)) continue;
                    vis[t] = inClearRange ? Clear : Silhouette;
                    explored[t] = true;
                }
            }
        }

        foreach (var c in cities)
            if (vis[Index(c.Pos)] != Hidden) c.KnownOwner = c.Owner;
    }

    bool IsInterior(int x, int y, int v)
    {
        if (x <= 0 || y <= 0 || x >= W - 1 || y >= H - 1) return false;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int n = (y + dy) * W + x + dx;
                if (viewerStamp[n] != stamp || height[n] < height[v]) return false;
            }
        return true;
    }

    /// <summary>Walks the sight line: blocked by ground rising above it, or by too much forest in the way.</summary>
    bool LineOfSight(int vx, int vy, float hv, int dx, int dy, float ht)
    {
        int n = Math.Max(Math.Abs(dx), Math.Abs(dy));
        int trees = 0;
        for (int k = 1; k < n; k++)
        {
            float f = k / (float)n;
            int sx = vx + (int)MathF.Round(dx * f), sy = vy + (int)MathF.Round(dy * f);
            int s = sy * W + sx;
            float line = hv + (ht + 0.01f - hv) * f;
            if (height[s] > line + 0.008f) return false;
            if (forest[s] && ++trees >= Config.ForestSightCells) return false;
        }
        return true;
    }
}
