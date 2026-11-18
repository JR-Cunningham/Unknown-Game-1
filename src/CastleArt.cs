using System.Numerics;
using Raylib_cs;

namespace PixelLegion;

/// <summary>A generated settlement sprite.</summary>
sealed class CastleSprite : IDisposable
{
    public Texture2D Texture;
    public int W, H;
    public Vector2 FlagBase;   // sprite pixel where the banner pole stands
    public float WallRadius;   // sprite pixels from the centre to where the stone walls stand (0 = no walls)
    public void Dispose() => Raylib.UnloadTexture(Texture);
}

/// <summary>Procedural pixel art for settlements, drawn at a fine pixel scale so houses get tiled roofs, timber
/// frames, windows and chimneys, and keeps get masonry, battlements and arrow slits. Walled places are laid out
/// round a circular courtyard whose edge is exactly where the game draws its stone walls.</summary>
static class CastleArt
{
    public const float Scale = 1.3f; // world units per sprite pixel

    static Color C(int r, int g, int b, int a = 255) => Teams.Col(r, g, b, a);
    static Color Shade(Color c, float f) => C(
        Math.Clamp((int)(c.R * f), 0, 255), Math.Clamp((int)(c.G * f), 0, 255), Math.Clamp((int)(c.B * f), 0, 255), c.A);
    static float Sq(float v) => v * v;

    /// <summary>A canvas with a "solid" mask for buildings (outlined and shadowed at the end).</summary>
    sealed class Canvas
    {
        public readonly int W, H;
        public readonly Color[,] Px;
        public readonly bool[,] Solid;
        public readonly Random Rng;

        public Canvas(int w, int h, int seed)
        {
            W = w; H = h;
            Px = new Color[w, h];
            Solid = new bool[w, h];
            Rng = new Random(seed);
        }

        public bool In(int x, int y) => x >= 0 && y >= 0 && x < W && y < H;
        public void Set(int x, int y, Color c) { if (In(x, y)) Px[x, y] = c; }
        public void Build(int x, int y, Color c) { if (In(x, y)) { Px[x, y] = c; Solid[x, y] = true; } }
        public float J(float spread = 0.12f) => 1f - spread / 2 + (float)Rng.NextDouble() * spread;

        public bool Free(int x0, int y0, int w, int h)
        {
            for (int y = y0; y < y0 + h; y++)
                for (int x = x0; x < x0 + w; x++)
                    if (!In(x, y) || Solid[x, y]) return false;
            return true;
        }

        /// <summary>Crisp dark outline round every building, then a soft drop shadow.</summary>
        public void Finish()
        {
            var outline = C(44, 34, 26);
            var edge = new List<(int, int)>();
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    if (Solid[x, y]) continue;
                    if ((In(x + 1, y) && Solid[x + 1, y]) || (In(x - 1, y) && Solid[x - 1, y]) ||
                        (In(x, y + 1) && Solid[x, y + 1]) || (In(x, y - 1) && Solid[x, y - 1]))
                        edge.Add((x, y));
                }
            foreach (var (x, y) in edge) Px[x, y] = outline;
            var shadow = C(0, 0, 0, 70);
            for (int y = H - 3; y >= 0; y--)
                for (int x = W - 3; x >= 0; x--)
                    if (Px[x, y].A > 0 && Px[x + 2, y + 2].A == 0) Px[x + 2, y + 2] = shadow;
        }

        public CastleSprite Upload(Vector2 flag, float wallRadius)
        {
            var flat = new Color[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    flat[y * W + x] = Px[x, y];
            var tex = Terrain.TextureFromPixels(flat, W, H, TextureFilter.Point);
            return new CastleSprite { Texture = tex, W = W, H = H, FlagBase = flag, WallRadius = wallRadius };
        }
    }

    // ------------------------------------------------------------------ shared building painters

    static readonly Color[] RoofColors =
        { C(168, 74, 52), C(196, 158, 80), C(112, 108, 120), C(138, 92, 58), C(182, 96, 64), C(90, 98, 110) };
    static readonly Color[] PlasterColors = { C(226, 214, 184), C(212, 196, 160), C(232, 226, 206), C(204, 186, 150) };

    /// <summary>A house in 3/4 view: tiled roof with ridge and eaves, a timber-framed front with door and
    /// windows, and sometimes a chimney.</summary>
    static void House(Canvas cv, int x0, int y0, int w, int h, Color roof, Color plaster, bool chimney)
    {
        int roofH = h / 2 + 1, wallH = h - roofH;
        Color timber = C(92, 62, 38);
        for (int y = 0; y < roofH; y++)
            for (int x = 0; x < w; x++)
            {
                Color c = roof;
                if (y == 0) c = Shade(roof, 1.25f);                       // ridge catches the light
                else if (y == roofH - 1) c = Shade(roof, 0.62f);          // eaves in shadow
                else if (y % 2 == 0) c = Shade(roof, 0.86f);              // rows of tiles
                if (y % 2 == 1 && (x + y / 2) % 3 == 0) c = Shade(c, 0.9f); // staggered tile joints
                if (x == 0) c = Shade(c, 1.1f);
                if (x == w - 1) c = Shade(c, 0.8f);
                cv.Build(x0 + x, y0 + y, c);
            }
        for (int y = 0; y < wallH; y++)
            for (int x = 0; x < w; x++)
            {
                Color c = Shade(plaster, x >= w - 2 ? 0.86f : 1f);
                if (x == 0 || x == w - 1 || (w > 8 && x == w / 2) || y == 0) c = timber;   // timber frame
                cv.Build(x0 + x, y0 + roofH + y, c);
            }
        // door and windows
        int door = w / 2 - 1 + (w % 2);
        for (int y = Math.Max(1, wallH - 3); y < wallH; y++) { cv.Build(x0 + door, y0 + roofH + y, C(74, 48, 28)); cv.Build(x0 + door + 1, y0 + roofH + y, C(60, 38, 22)); }
        if (wallH >= 4)
            foreach (int wx in new[] { 2, w - 4 })
                if (wx > 0 && wx + 1 < w - 1 && Math.Abs(wx - door) > 1)
                {
                    cv.Build(x0 + wx, y0 + roofH + 1, C(250, 214, 120));
                    cv.Build(x0 + wx + 1, y0 + roofH + 1, C(214, 172, 90));
                    cv.Build(x0 + wx, y0 + roofH + 2, C(214, 172, 90));
                    cv.Build(x0 + wx + 1, y0 + roofH + 2, C(170, 130, 70));
                }
        if (chimney)
            for (int y = -2; y < 2; y++)
            {
                cv.Build(x0 + w - 3, y0 + y, C(132, 122, 112));
                cv.Build(x0 + w - 2, y0 + y, C(104, 96, 88));
            }
    }

    /// <summary>Stone masonry: staggered blocks with mortar lines.</summary>
    static Color Masonry(Color stone, int x, int y, float jitter)
    {
        bool mortar = y % 3 == 2 || (x + (y / 3 % 2) * 2) % 4 == 3;
        return mortar ? Shade(stone, 0.72f) : Shade(stone, jitter);
    }

    /// <summary>A square keep in 3/4 view: crenellated roof platform, masonry front face, arrow slits, door.
    /// Returns the flag position (top middle).</summary>
    static Vector2 Keep(Canvas cv, int x0, int y0, int k, int faceH, bool stone)
    {
        Color top = stone ? C(176, 170, 158) : C(158, 112, 66);
        Color face = stone ? C(146, 140, 130) : C(118, 80, 46);
        for (int y = 0; y < k; y++)
            for (int x = 0; x < k; x++)
            {
                bool rim = x == 0 || y == 0 || x == k - 1 || y == k - 1;
                Color c = rim ? ((x + y) % 2 == 0 ? Shade(top, 1.15f) : Shade(top, 0.7f))   // merlons and crenels
                              : Shade(top, stone ? 0.82f : 0.9f);
                if (!stone && !rim && x % 3 == 0) c = Shade(c, 0.85f);
                cv.Build(x0 + x, y0 + y, c);
            }
        for (int y = 0; y < faceH; y++)
            for (int x = 0; x < k; x++)
            {
                Color c = stone ? Masonry(face, x, y, cv.J(0.1f)) : ((x % 3 == 0) ? Shade(face, 0.8f) : face);
                if (x == k - 1) c = Shade(c, 0.85f);
                cv.Build(x0 + x, y0 + k + y, c);
            }
        // arrow slits and door
        for (int sx = 2; sx < k - 2; sx += 4)
        {
            cv.Build(x0 + sx, y0 + k + 2, C(30, 24, 20));
            cv.Build(x0 + sx, y0 + k + 3, C(30, 24, 20));
        }
        int d = k / 2 - 1;
        for (int y = faceH - 3; y < faceH; y++) { cv.Build(x0 + d, y0 + k + y, C(64, 42, 24)); cv.Build(x0 + d + 1, y0 + k + y, C(52, 34, 20)); }
        return new Vector2(x0 + k / 2f, y0);
    }

    // ------------------------------------------------------------------ castles and fortresses

    /// <summary>A walled castle: circular courtyard (the game draws the stone walls on its edge), a moat or ditch
    /// outside, a motte with a palisade and keep, a great hall, huts, stores and a well inside.
    /// size 1-2 = castle, 4 = mountain fortress.</summary>
    public static CastleSprite Build(int size, int seed)
    {
        int rw = 30 + size * 9;
        int W = 2 * (rw + 8), H = W;
        var cv = new Canvas(W, H, seed);
        var rng = cv.Rng;
        float cx = W / 2f, cy = H / 2f;
        bool stone = size >= 2;
        bool moat = rng.Next(3) > 0;
        int side = rng.Next(2) == 0 ? -1 : 1;

        Color grass = C(118, 140, 74), earth = C(150, 132, 92), water = C(70, 106, 130);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float d = MathF.Sqrt(Sq(x + 0.5f - cx) + Sq(y + 0.5f - cy));
                if (d > rw + 7) continue;
                if (d > rw + 3)
                {
                    // moat with ripples, or a dry ditch
                    Color c = moat ? water : C(96, 80, 58);
                    if (moat && (x + y * 2) % 7 == 0) c = Shade(c, 1.2f);
                    if (d > rw + 6.2f) c = Shade(c, 0.75f);
                    cv.Set(x, y, Shade(c, cv.J(0.08f)));
                }
                else if (d > rw) cv.Set(x, y, Shade(grass, cv.J()));              // grass berm under the wall
                else
                {
                    // trodden courtyard with patches of grass
                    bool tuft = rng.Next(12) == 0;
                    cv.Set(x, y, tuft ? Shade(grass, cv.J()) : Shade(earth, cv.J(0.14f)));
                }
            }

        // the motte: a shaded grassy mound with its own palisade ring
        float mr = rw * 0.42f, mx = cx + side * rw * 0.4f, my = cy - rw * 0.12f;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float nx = (x + 0.5f - mx) / mr, ny = (y + 0.5f - my) / mr;
                float d = MathF.Sqrt(nx * nx + ny * ny);
                if (d >= 1) continue;
                float light = Math.Clamp(0.55f - 0.45f * nx - 0.6f * ny, 0, 1);
                Color c = Teams.Lerp(C(64, 86, 44), C(150, 172, 94), light);
                if (d > 0.88f && ny > 0) c = C(104, 84, 56);
                cv.Set(x, y, Shade(c, cv.J(0.08f)));
                if (d > 0.68f && d < 0.74f) cv.Build(x, y, (x + y) % 2 == 0 ? C(128, 88, 50) : C(162, 116, 70));  // palisade
            }
        // flying bridge from the yard up to the motte
        int bx0 = (int)(mx - side * mr), bx1 = (int)(mx - side * (mr + rw * 0.18f));
        for (int x = Math.Min(bx0, bx1); x <= Math.Max(bx0, bx1); x++)
            for (int y = 0; y < 3; y++)
                cv.Set(x, (int)my + 4 + y, y == 1 ? C(150, 108, 64) : C(104, 72, 42));

        int k = 10 + size * 2;
        var flag = Keep(cv, (int)(mx - k / 2f), (int)(my - k / 2f) - 4, k, 6 + size, stone);

        // buildings in the bailey: a hall for bigger castles, then huts, a well, stores
        float yx = cx - side * rw * 0.3f;
        bool TryPlace(int w, int h, out int px, out int py)
        {
            for (int t = 0; t < 80; t++)
            {
                px = (int)(yx + ((float)rng.NextDouble() * 2 - 1) * rw * 0.45f) - w / 2;
                py = (int)(cy + ((float)rng.NextDouble() * 2 - 1) * rw * 0.6f) - h / 2;
                bool inYard = Sq(px + w / 2f - cx) + Sq(py + h / 2f - cy) < Sq(rw - Math.Max(w, h) * 0.8f);
                bool offMotte = Sq(px + w / 2f - mx) + Sq(py + h / 2f - my) > Sq(mr + Math.Max(w, h) * 0.7f);
                if (inYard && offMotte && cv.Free(px - 2, py - 2, w + 4, h + 4)) return true;
            }
            px = py = 0;
            return false;
        }
        if (size >= 2 && TryPlace(20, 11, out int hx, out int hy))
            House(cv, hx, hy, 20, 11, stone ? C(110, 106, 118) : RoofColors[0], C(210, 200, 176), true);
        int huts = 2 + size + rng.Next(3);
        for (int i = 0; i < huts; i++)
        {
            int w = 9 + rng.Next(3), h = 8 + rng.Next(2);
            if (TryPlace(w, h, out int ux, out int uy))
                House(cv, ux, uy, w, h, RoofColors[rng.Next(RoofColors.Length)], PlasterColors[rng.Next(PlasterColors.Length)], rng.Next(3) == 0);
        }
        if (TryPlace(5, 5, out int wx, out int wy)) Well(cv, wx, wy);
        for (int i = 0; i < 3; i++)
            if (TryPlace(3, 3, out int sx, out int sy)) Barrel(cv, sx, sy);

        cv.Finish();
        return cv.Upload(flag, rw);
    }

    static void Well(Canvas cv, int x0, int y0)
    {
        for (int y = 0; y < 5; y++)
            for (int x = 0; x < 5; x++)
            {
                bool rim = x == 0 || y == 0 || x == 4 || y == 4;
                if ((x == 0 || x == 4) && (y == 0 || y == 4)) continue;
                cv.Build(x0 + x, y0 + y, rim ? ((x + y) % 2 == 0 ? C(160, 154, 146) : C(124, 118, 110)) : C(52, 84, 104));
            }
    }

    static void Barrel(Canvas cv, int x0, int y0)
    {
        cv.Build(x0 + 1, y0, C(150, 106, 62));
        cv.Build(x0, y0 + 1, C(130, 90, 52)); cv.Build(x0 + 1, y0 + 1, C(110, 76, 44)); cv.Build(x0 + 2, y0 + 1, C(130, 90, 52));
        cv.Build(x0 + 1, y0 + 2, C(96, 66, 38));
    }

    // ------------------------------------------------------------------ villages, towns and cities

    /// <summary>Settlements without a castle. size 0 = a hamlet, 2 = a market town, 4 = a sprawling city whose
    /// inner core is circular (the game rings it with stone walls). Fields with furrows ring the edge; streets,
    /// a market square with stalls and a well, a church with a spire, and houses fill the core.</summary>
    public static CastleSprite BuildTown(int size, int seed)
    {
        int W = 52 + size * 32, H = 44 + size * 26;
        var cv = new Canvas(W, H, seed);
        var rng = cv.Rng;
        float cx = W / 2f, cy = H / 2f;
        bool walled = size >= 4;
        float coreRx = walled ? H * 0.3f : W * 0.3f, coreRy = walled ? H * 0.3f : H * 0.28f;
        float outerRx = W * 0.49f, outerRy = H * 0.47f;
        float E(float x, float y, float rx, float ry) => MathF.Sqrt(Sq((x - cx) / rx) + Sq((y - cy) / ry));

        // 1. fields: hedged plots with furrows, some with haystacks
        Color[] crops = { C(208, 182, 96), C(186, 170, 106), C(128, 154, 74), C(136, 106, 72), C(152, 164, 120) };
        for (int attempt = 0; attempt < 120; attempt++)
        {
            int fw = 12 + rng.Next(10), fh = 8 + rng.Next(7);
            int fx = rng.Next(0, Math.Max(1, W - fw)), fy = rng.Next(0, Math.Max(1, H - fh));
            if (E(fx + fw / 2f, fy + fh / 2f, outerRx, outerRy) > 1f || E(fx + fw / 2f, fy + fh / 2f, coreRx, coreRy) < 1.15f) continue;
            Color crop = crops[rng.Next(crops.Length)];
            bool vertical = rng.Next(2) == 0;
            for (int y = fy; y < fy + fh; y++)
                for (int x = fx; x < fx + fw; x++)
                {
                    if (cv.Px[x, y].A > 0) continue;
                    bool hedge = x == fx || y == fy || x == fx + fw - 1 || y == fy + fh - 1;
                    Color c = hedge ? C(70, 98, 54) : (((vertical ? x : y) % 2 == 0) ? crop : Shade(crop, 0.84f));
                    cv.Set(x, y, Shade(c, cv.J(0.06f)));
                }
            if (rng.Next(3) == 0)
            {
                int hx = fx + 2 + rng.Next(Math.Max(1, fw - 5)), hy = fy + 2 + rng.Next(Math.Max(1, fh - 5));
                cv.Set(hx + 1, hy, C(228, 200, 110)); cv.Set(hx, hy + 1, C(214, 184, 96)); cv.Set(hx + 1, hy + 1, C(196, 166, 84)); cv.Set(hx + 2, hy + 1, C(176, 148, 74));
            }
        }

        // 2. the core: trodden ground
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                if (E(x + 0.5f, y + 0.5f, coreRx + 1, coreRy + 1) < 1f)
                    cv.Set(x, y, Shade(C(158, 142, 100), cv.J(0.14f)));

        // 3. streets: three pixels wide, lighter crown, darker gutters
        void Street(float ax, float ay, float bx, float by)
        {
            int n = (int)(MathF.Sqrt(Sq(bx - ax) + Sq(by - ay)) * 2);
            bool horiz = MathF.Abs(bx - ax) > MathF.Abs(by - ay);
            for (int s = 0; s <= n; s++)
            {
                float t = s / (float)n;
                int x = (int)(ax + (bx - ax) * t), y = (int)(ay + (by - ay) * t);
                for (int o = -1; o <= 1; o++)
                {
                    int px = horiz ? x : x + o, py = horiz ? y + o : y;
                    if (!cv.In(px, py) || cv.Solid[px, py]) continue;
                    cv.Set(px, py, o == 0 ? Shade(C(176, 160, 124), cv.J(0.08f)) : C(130, 112, 80));
                }
            }
        }
        float a1 = (float)rng.NextDouble() * 0.4f - 0.2f, a2 = (float)rng.NextDouble() * 0.4f - 0.2f;
        Street(cx - outerRx, cy - outerRx * a1, cx + outerRx, cy + outerRx * a1);
        Street(cx - outerRy * a2, cy - outerRy, cx + outerRy * a2, cy + outerRy);
        if (size >= 2)
        {
            Street(cx - coreRx * 0.8f, cy - coreRy * 0.5f, cx + coreRx * 0.8f, cy - coreRy * 0.45f);
            Street(cx - coreRx * 0.8f, cy + coreRy * 0.5f, cx + coreRx * 0.8f, cy + coreRy * 0.55f);
        }

        // 4. market square: cobbles, a well and striped stalls
        int sq = 5 + size * 2;
        for (int y = -sq; y <= sq; y++)
            for (int x = -sq - 2; x <= sq + 2; x++)
                if (Sq(x / (sq + 2.5f)) + Sq(y / (sq + 0.5f)) < 1f)
                    cv.Set((int)cx + x, (int)cy + y, ((x + y) % 2 == 0) ? C(176, 166, 140) : C(160, 150, 124));
        Well(cv, (int)cx - 2, (int)cy - 2);
        Color[] awnings = { C(200, 70, 60), C(70, 110, 170), C(220, 180, 70), C(90, 150, 90) };
        for (int s = 0; s < 1 + size; s++)
        {
            int sx = (int)cx + (s % 2 == 0 ? -sq : sq - 4), sy = (int)cy - sq + 2 + (s / 2) * 5;
            Color aw = awnings[rng.Next(awnings.Length)];
            if (!cv.Free(sx, sy, 5, 3)) continue;
            for (int x = 0; x < 5; x++) { cv.Build(sx + x, sy, x % 2 == 0 ? aw : C(236, 230, 214)); cv.Build(sx + x, sy + 1, x % 2 == 0 ? Shade(aw, 0.8f) : C(214, 208, 192)); }
            for (int x = 0; x < 5; x++) cv.Build(sx + x, sy + 2, C(120, 86, 52));
        }

        // 5. church with a spire (not in hamlets); the banner flies from the spire
        var flag = new Vector2(cx, cy - sq - 2);
        if (size > 0)
        {
            int nw = 16 + size * 2, nh = 12, chx = (int)(cx + (rng.Next(2) == 0 ? -1 : 1) * coreRx * 0.45f) - nw / 2, chy = (int)(cy - coreRy * 0.6f) - nh / 2;
            House(cv, chx, chy, nw, nh, C(98, 94, 110), C(184, 178, 166), false);
            for (int wxx = 3; wxx < nw - 3; wxx += 4)   // tall arched windows
            {
                cv.Build(chx + wxx, chy + 8, C(90, 120, 170)); cv.Build(chx + wxx, chy + 9, C(70, 96, 150)); cv.Build(chx + wxx, chy + 10, C(70, 96, 150));
            }
            int tx = chx - 6;
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 6; x++)
                {
                    Color c = Masonry(C(170, 164, 152), x, y, cv.J(0.08f));
                    if (x == 5) c = Shade(c, 0.82f);
                    cv.Build(tx + x, chy - 4 + y, c);
                }
            for (int y = 0; y < 7; y++)   // the spire
                for (int x = y / 3; x < 6 - y / 3; x++)
                    cv.Build(tx + x, chy - 11 + y, x < 3 ? C(112, 106, 120) : C(86, 82, 96));
            cv.Build(tx + 2, chy - 2, C(30, 24, 20)); cv.Build(tx + 3, chy - 2, C(30, 24, 20));
            flag = new Vector2(tx + 3, chy - 11);
        }

        // 6. houses: lots of them, packed along the streets
        int houses = 5 + size * 12;
        int placed = 0;
        for (int attempt = 0; attempt < 1500 && placed < houses; attempt++)
        {
            int hw = 8 + rng.Next(5), hh = 8 + rng.Next(3);
            int hx = (int)(cx + ((float)rng.NextDouble() * 2 - 1) * coreRx) - hw / 2;
            int hy = (int)(cy + ((float)rng.NextDouble() * 2 - 1) * coreRy) - hh / 2;
            if (E(hx + hw / 2f, hy + hh / 2f, coreRx - 2, coreRy - 2) > 1f) continue;
            if (!cv.Free(hx - 1, hy - 1, hw + 2, hh + 2)) continue;
            House(cv, hx, hy, hw, hh, RoofColors[rng.Next(RoofColors.Length)], PlasterColors[rng.Next(PlasterColors.Length)], rng.Next(3) == 0);
            placed++;
        }

        // 7. towns get a palisade round the core (cities get real stone walls from the game instead)
        if (size >= 2 && !walled)
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float e = E(x + 0.5f, y + 0.5f, coreRx * 1.1f, coreRy * 1.1f);
                    if (e >= 0.95f && e < 1f && !cv.Solid[x, y] && (x + y) % 3 != 0)
                        cv.Build(x, y, (x + y) % 2 == 0 ? C(124, 84, 48) : C(162, 114, 66));
                }

        cv.Finish();
        return cv.Upload(flag, walled ? coreRx + 2 : 0);
    }

    // ------------------------------------------------------------------ watchtower

    /// <summary>A lonely watchtower: a masonry (or timber) tower on a trodden mound, ringed with sharpened stakes.</summary>
    public static CastleSprite BuildTower(int seed)
    {
        const int W = 46, H = 46;
        var cv = new Canvas(W, H, seed);
        bool stone = cv.Rng.Next(3) > 0;
        float cx = W / 2f, cy = H / 2f + 2;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float d = MathF.Sqrt(Sq(x + 0.5f - cx) + Sq(y + 0.5f - cy));
                if (d < 20f) cv.Set(x, y, Shade(C(146, 132, 92), cv.J(0.16f)));
                if (d >= 18.4f && d < 20f && (x + y) % 3 != 0) cv.Build(x, y, (x + y) % 2 == 0 ? C(120, 84, 48) : C(160, 114, 66));
            }
        var flag = Keep(cv, (int)cx - 6, (int)cy - 16, 12, 12, stone);
        for (int i = 0; i < 3; i++) Barrel(cv, (int)cx + 8 + (i % 2) * 3, (int)cy + 6 + i * 2);
        cv.Finish();
        return cv.Upload(flag, 0);
    }

    // ------------------------------------------------------------------ small map details

    /// <summary>Small sprites (caravans, inns, mills) at an even finer pixel scale.</summary>
    public const float DetailScale = 0.65f;

    /// <summary>A trade wagon seen from above, facing east: the ox in front with horns and legs, a wagon bed with
    /// wheels at the sides and a hooped canvas cover. Returns east/west/north/south facings.</summary>
    public static Texture2D[] BuildCaravan()
    {
        const int W = 30, H = 14;
        var px = new Color[W, H];
        void S(int x, int y, Color c) { if (x >= 0 && y >= 0 && x < W && y < H) px[x, y] = c; }
        Color wood = C(120, 82, 46), canvas = C(232, 220, 188), hoop = C(196, 180, 146), wheel = C(54, 40, 28);
        foreach (int wx in new[] { 3, 13 })
            for (int x = 0; x < 4; x++) { S(wx + x, 1, wheel); S(wx + x, 12, wheel); }
        for (int y = 2; y < 12; y++)
            for (int x = 1; x < 18; x++)
            {
                bool rim = y == 2 || y == 11 || x == 1 || x == 17;
                Color c = rim ? wood : canvas;
                if (!rim)
                {
                    if (x % 4 == 0) c = hoop;                       // hoops under the canvas
                    if (y <= 4) c = Shade(c, 1.06f);
                    if (y >= 9) c = Shade(c, 0.86f);
                }
                S(x, y, c);
            }
        for (int x = 18; x < 21; x++) S(x, 7, wood);                 // shaft
        for (int y = 4; y < 11; y++) S(21, y, Shade(wood, 0.8f));    // yoke
        Color ox = C(132, 94, 60), oxDark = C(96, 66, 40);
        for (int y = 4; y < 10; y++)
            for (int x = 22; x < 27; x++) S(x, y, y == 4 || x == 22 ? Shade(ox, 1.15f) : y == 9 ? oxDark : ox);
        for (int y = 5; y < 9; y++) for (int x = 27; x < 29; x++) S(x, y, oxDark);       // head
        S(28, 4, C(236, 228, 204)); S(28, 9, C(236, 228, 204));                          // horns
        S(23, 3, oxDark); S(26, 3, oxDark); S(23, 10, oxDark); S(26, 10, oxDark);         // legs
        var outlined = (Color[,])px.Clone();
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                if (px[x, y].A > 0) continue;
                bool edge = (x > 0 && px[x - 1, y].A > 0) || (x < W - 1 && px[x + 1, y].A > 0) || (y > 0 && px[x, y - 1].A > 0) || (y < H - 1 && px[x, y + 1].A > 0);
                if (edge) outlined[x, y] = C(40, 30, 22);
            }
        Texture2D Make(int w, int h, Func<int, int, Color> at)
        {
            var flat = new Color[w * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) flat[y * w + x] = at(x, y);
            return Terrain.TextureFromPixels(flat, w, h, TextureFilter.Point);
        }
        return new[]
        {
            Make(W, H, (x, y) => outlined[x, y]),                    // east
            Make(W, H, (x, y) => outlined[W - 1 - x, y]),            // west
            Make(H, W, (x, y) => outlined[W - 1 - y, x]),            // north
            Make(H, W, (x, y) => outlined[y, x]),                    // south
        };
    }

    /// <summary>A roadside inn: tiled roof with chimneys, lit windows, a bench and barrels, and a hanging sign.</summary>
    public static Texture2D BuildTavern(int seed)
    {
        var cv = new Canvas(34, 30, seed);
        House(cv, 4, 6, 22, 18, C(170, 82, 56), C(226, 212, 178), true);
        for (int y = 3; y < 8; y++) { cv.Build(8, y, C(132, 122, 112)); cv.Build(9, y, C(104, 96, 88)); }
        for (int y = 12; y < 24; y++) cv.Build(29, y, C(86, 60, 36));
        for (int x = 26; x < 31; x++) cv.Build(x, 12, C(86, 60, 36));
        for (int y = 13; y < 17; y++) for (int x = 26; x < 30; x++) cv.Build(x, y, C(214, 170, 74));
        cv.Build(27, 14, C(120, 80, 36)); cv.Build(28, 14, C(120, 80, 36)); cv.Build(27, 15, C(120, 80, 36)); cv.Build(28, 15, C(240, 236, 220));
        for (int x = 6; x < 12; x++) cv.Build(x, 25, C(120, 86, 52));   // bench
        Barrel(cv, 20, 25); Barrel(cv, 23, 25);
        cv.Finish();
        return cv.Upload(Vector2.Zero, 0).Texture;
    }

    /// <summary>A windmill tower (the sails turn at runtime): a whitewashed tower shaded as a cylinder, with a
    /// pointed cap, a window and a door.</summary>
    public static Texture2D BuildWindmill(int seed)
    {
        var cv = new Canvas(20, 34, seed);
        for (int y = 8; y < 32; y++)
        {
            int half = 5 + (y - 8) / 6;
            for (int x = 10 - half; x < 10 + half; x++)
            {
                float t = (x - (10 - half)) / (float)(2 * half);
                Color c = Teams.Lerp(C(240, 234, 216), C(170, 160, 142), t);
                if (y % 6 == 0) c = Shade(c, 0.9f);
                cv.Build(x, y, c);
            }
        }
        for (int y = 2; y < 9; y++)
        {
            int half = 1 + (y - 2) * 6 / 7;
            for (int x = 10 - half; x < 10 + half; x++) cv.Build(x, y, x < 10 ? C(150, 82, 56) : C(118, 62, 42));
        }
        for (int y = 27; y < 32; y++) { cv.Build(9, y, C(74, 50, 30)); cv.Build(10, y, C(60, 40, 24)); }
        cv.Build(9, 18, C(60, 76, 96)); cv.Build(10, 18, C(60, 76, 96));
        cv.Finish();
        return cv.Upload(Vector2.Zero, 0).Texture;
    }

    /// <summary>The mill house beside a water wheel.</summary>
    public static Texture2D BuildMillHouse(int seed)
    {
        var cv = new Canvas(24, 22, seed);
        House(cv, 2, 3, 18, 16, C(150, 76, 52), C(218, 204, 172), true);
        cv.Finish();
        return cv.Upload(Vector2.Zero, 0).Texture;
    }
}
