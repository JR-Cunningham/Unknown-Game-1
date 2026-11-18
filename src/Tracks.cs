using System.Numerics;
using Raylib_cs;

namespace PixelLegion;

/// <summary>Footprints. Every pixel stamps its own shape into the ground as it walks; overlapping prints add up,
/// so a well-used route turns dark. All prints slowly fade back to the bare map.
/// The map is split into chunks that only exist (and only cost memory) where something has walked.</summary>
sealed class Tracks : IDisposable
{
    const int S = Config.TrackChunk;

    sealed class Chunk
    {
        public int X, Y;
        public readonly byte[] Ink = new byte[S * S];
        public Texture2D Tex;
        public bool HasTex, Dirty;
    }

    readonly int cw, ch;
    readonly Chunk?[] chunks;
    readonly List<Chunk> active = new();
    readonly Color[] upload = new Color[S * S];
    float fadeTimer, uploadTimer;

    public Tracks()
    {
        cw = (int)MathF.Ceiling(Config.WorldW / S);
        ch = (int)MathF.Ceiling(Config.WorldH / S);
        chunks = new Chunk?[cw * ch];
    }

    public void Dispose()
    {
        foreach (var c in active) if (c.HasTex) Raylib.UnloadTexture(c.Tex);
        active.Clear();
    }

    void Add(int x, int y, int amount)
    {
        if (x < 0 || y < 0 || x >= cw * S || y >= ch * S) return;
        int ci = (y / S) * cw + x / S;
        var c = chunks[ci];
        if (c == null)
        {
            c = chunks[ci] = new Chunk { X = x / S, Y = y / S };
            active.Add(c);
        }
        int i = (y % S) * S + x % S;
        c.Ink[i] = (byte)Math.Min(255, c.Ink[i] + amount);
        c.Dirty = true;
    }

    /// <summary>Presses the unit's shape into the ground: a square, a round print, or a hoof-shaped triangle.</summary>
    public void Stamp(UnitKind kind, Vector2 pos, Vector2 heading)
    {
        int px = (int)MathF.Floor(pos.X), py = (int)MathF.Floor(pos.Y);
        int ink = Config.TrackInk;
        switch (kind)
        {
            case UnitKind.Archer:
                Add(px, py, ink); Add(px + 1, py, ink); Add(px - 1, py, ink); Add(px, py + 1, ink); Add(px, py - 1, ink);
                break;
            case UnitKind.Cavalry:
            {
                Vector2 side = new(-heading.Y, heading.X);
                Vector2 a = pos + heading * 2.4f, b = pos - heading * 1.6f + side * 1.8f, c = pos - heading * 1.6f - side * 1.8f;
                for (int dy = -3; dy <= 3; dy++)
                    for (int dx = -3; dx <= 3; dx++)
                    {
                        var p = new Vector2(px + dx + 0.5f, py + dy + 0.5f);
                        if (InTriangle(p, a, b, c)) Add(px + dx, py + dy, ink);
                    }
                break;
            }
            default:
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        Add(px + dx, py + dy, ink);
                break;
        }
    }

    /// <summary>Two wheel ruts either side of a caravan.</summary>
    public void Ruts(Vector2 pos, Vector2 heading)
    {
        Vector2 side = new(-heading.Y, heading.X);
        foreach (var p in new[] { pos + side * 2f, pos - side * 2f })
            Add((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y), Config.TrackInk);
    }

    static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float Cross(Vector2 u, Vector2 v, Vector2 w) => (v.X - u.X) * (w.Y - u.Y) - (v.Y - u.Y) * (w.X - u.X);
        float d1 = Cross(a, b, p), d2 = Cross(b, c, p), d3 = Cross(c, a, p);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }

    public void Update(float dt)
    {
        fadeTimer -= dt;
        if (fadeTimer <= 0)
        {
            fadeTimer = Config.TrackFadeInterval;
            for (int k = active.Count - 1; k >= 0; k--)
            {
                var c = active[k];
                int max = 0;
                var ink = c.Ink;
                for (int i = 0; i < ink.Length; i++)
                {
                    if (ink[i] == 0) continue;
                    ink[i]--;
                    if (ink[i] > max) max = ink[i];
                }
                c.Dirty = true;
                if (max == 0)
                {
                    // fully faded: free it
                    if (c.HasTex) Raylib.UnloadTexture(c.Tex);
                    chunks[c.Y * cw + c.X] = null;
                    active.RemoveAt(k);
                }
            }
        }

        uploadTimer -= dt;
        if (uploadTimer > 0) return;
        uploadTimer = 0.1f;
        foreach (var c in active)
        {
            if (!c.Dirty) continue;
            c.Dirty = false;
            for (int i = 0; i < upload.Length; i++)
            {
                byte v = c.Ink[i];
                upload[i] = v == 0 ? default : Teams.Col(58, 42, 26, Math.Min(200, 30 + v * 3 / 4));
            }
            if (!c.HasTex)
            {
                c.Tex = Terrain.TextureFromPixels(upload, S, S, TextureFilter.Point);
                c.HasTex = true;
            }
            else Terrain.UpdateTexture(c.Tex, upload);
        }
    }

    public void Draw(Vector2 viewMin, Vector2 viewMax)
    {
        foreach (var c in active)
        {
            if (!c.HasTex) continue;
            float x = c.X * S, y = c.Y * S;
            if (x > viewMax.X || y > viewMax.Y || x + S < viewMin.X || y + S < viewMin.Y) continue;
            Raylib.DrawTextureV(c.Tex, new Vector2(x, y), Color.White);
        }
    }
}
