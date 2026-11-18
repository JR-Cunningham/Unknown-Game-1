using System.Numerics;
using Raylib_cs;

namespace PixelLegion;

sealed partial class Game
{
    static readonly Color Bg = Teams.Col(36, 28, 20);              // dark oak around the map
    static readonly Color Frame = Teams.Col(92, 66, 38);
    static readonly Color FrameLight = Teams.Col(150, 112, 64);
    static readonly Color White = Teams.Col(255, 255, 255);
    static readonly Color Ink = Teams.Col(58, 38, 20);              // text on parchment
    static readonly Color InkDim = Teams.Col(120, 94, 62);
    static readonly Color Parchment = Teams.Col(232, 214, 168, 235);
    static readonly Color ParchmentEdge = Teams.Col(120, 84, 44);
    static readonly Color GoldCol = Teams.Col(255, 208, 50);
    static readonly Color GoldInk = Teams.Col(150, 104, 10);
    static readonly Color Outline = Teams.Col(18, 14, 10, 220);
    static readonly Color NeutralBanner = Teams.Col(225, 222, 210);
    static readonly Color HpBack = Teams.Col(60, 10, 10, 220);

    public void Draw()
    {
        Raylib.ClearBackground(Bg);

        Raylib.BeginMode2D(cam);
        DrawWorld();
        Raylib.EndMode2D();

        DrawDragBox();
        DrawHud();
        DrawSlider();
        DrawMinimap();
        if (gameOver) DrawGameOver();
    }

    // ------------------------------------------------------------------ world

    readonly List<Unit> drawList = new();
    readonly Dictionary<int, (Vector2 sum, int n)> silhouettes = new();

    void DrawWorld()
    {
        EnsureDetailArt();
        // carved wooden frame around the map
        float f = 14;
        Raylib.DrawRectangleRec(new Rectangle(-f, -f, Config.WorldW + 2 * f, Config.WorldH + 2 * f), Frame);
        Raylib.DrawRectangleLinesEx(new Rectangle(-f, -f, Config.WorldW + 2 * f, Config.WorldH + 2 * f), 3, FrameLight);

        Raylib.DrawTexturePro(terrain.Texture,
            new Rectangle(0, 0, terrain.TexW, terrain.TexH),
            new Rectangle(0, 0, Config.WorldW, Config.WorldH),
            Vector2.Zero, 0, White);

        Vector2 tl = Raylib.GetScreenToWorld2D(Vector2.Zero, cam);
        Vector2 br = Raylib.GetScreenToWorld2D(new(Raylib.GetScreenWidth(), Raylib.GetScreenHeight()), cam);
        bool Visible(Vector2 p, float pad) => p.X > tl.X - pad && p.X < br.X + pad && p.Y > tl.Y - pad && p.Y < br.Y + pad;

        tracks.Draw(tl, br);
        DrawWaterfalls(Visible);
        DrawTaverns(Visible);
        DrawWindmills(Visible);
        DrawWaterWheels(Visible);

        foreach (var c in cities)
            if (Visible(c.Pos, c.Radius + 40)) DrawCastle(c);

        DrawArcherRanges();

        // fog of war goes over the land; what is seen right now is drawn on top of it
        Raylib.DrawTexturePro(fog.Texture, new Rectangle(0, 0, fog.W, fog.H),
            new Rectangle(0, 0, fog.W * Config.FogCell, fog.H * Config.FogCell), Vector2.Zero, 0, White);

        DrawOrders();
        // sort what is on screen: our pixels and clearly-seen enemies are drawn exactly; enemies on distant
        // raised ground are only a blur of colour, gathered per fog cell
        drawList.Clear();
        silhouettes.Clear();
        foreach (var u in units)
        {
            if (!Visible(u.Pos, 4)) continue;
            byte v = u.Team == Teams.Player ? FogOfWar.Clear : fog.VisAt(u.Pos);
            if (v == FogOfWar.Clear) drawList.Add(u);
            else if (v == FogOfWar.Silhouette)
            {
                int key = fog.Index(u.Pos) * 8 + u.Team;
                silhouettes.TryGetValue(key, out var s);
                silhouettes[key] = (s.sum + u.Pos, s.n + 1);
            }
        }
        foreach (var (key, s) in silhouettes)
        {
            Color col = Teams.Lerp(Teams.Colors[key % 8], Teams.Col(140, 136, 128), 0.3f);
            Vector2 at = s.sum / s.n;
            float r = 4f + MathF.Sqrt(s.n) * 1.8f;
            Raylib.DrawCircleV(at, r * 1.7f, Teams.WithAlpha(col, 45));
            Raylib.DrawCircleV(at, r * 1.15f, Teams.WithAlpha(col, 75));
            Raylib.DrawCircleV(at, r * 0.65f, Teams.WithAlpha(col, 110));
        }

        // pixels: dark outline pass first so dense blobs read as a mass of coloured dots (skipped when too
        // zoomed out to see it)
        float half = Config.UnitSize / 2f;
        if (cam.Zoom > 0.6f)
        {
            var shadow = Teams.Col(0, 0, 0, 60);
            foreach (var u in drawList)
                DrawUnitShape(u.Kind, u.Pos + new Vector2(1f, 1.1f), u.Heading, 0.5f, shadow);
            foreach (var u in drawList)
                DrawUnitShape(u.Kind, u.Pos, u.Heading, 1f, Outline);
        }

        foreach (var u in drawList)
        {
            Color col = Teams.Colors[u.Team];
            int rank = u.Rank;
            if (rank > 0) col = Teams.Shade(col, 1f - 0.1f * rank);    // veterans darken with each rank
            float hurt = 1f - u.Hp / u.MaxHp;
            if (hurt > 0) col = Teams.Lerp(col, Teams.Col(50, 10, 10), hurt * 0.55f);
            if (u.Selected) col = Teams.Lerp(col, White, 0.45f);
            if (u.HitFlash > 0) col = White;
            DrawUnitShape(u.Kind, u.Pos, u.Heading, 0f, col);
            if (cam.Zoom >= 1.4f) DrawUnitDetail(u, col);

            if (u.RankFlash > 0)
            {
                float t = u.RankFlash / 0.8f;
                Raylib.DrawRing(u.Pos, 3 + (1 - t) * 6, 3.8f + (1 - t) * 6, 0, 360, 16, Teams.WithAlpha(GoldCol, (int)(255 * t)));
            }

            // the group's leader wears a knight's great helm: a steel bucket with a visor slit and a plume
            if (u.IsLeader && cam.Zoom >= 0.9f)
            {
                Vector2 p = u.Pos + new Vector2(0, -4.4f);
                Color steel = Teams.Col(158, 164, 172), shine = Teams.Col(214, 220, 226), shadowSteel = Teams.Col(98, 102, 112);
                Raylib.DrawRectangleV(p + new Vector2(-2.3f, -2.3f), new(4.6f, 4.8f), Outline);
                Raylib.DrawRectangleV(p + new Vector2(-1.8f, -1.8f), new(3.6f, 3.8f), steel);
                Raylib.DrawRectangleV(p + new Vector2(-1.8f, -1.8f), new(3.6f, 0.7f), shine);               // rounded crown
                Raylib.DrawRectangleV(p + new Vector2(-1.8f, -1.1f), new(0.8f, 2.6f), shine);               // lit side
                Raylib.DrawRectangleV(p + new Vector2(1.0f, -1.1f), new(0.8f, 2.9f), shadowSteel);          // shaded side
                Raylib.DrawRectangleV(p + new Vector2(-1.4f, -0.1f), new(2.8f, 0.6f), Teams.Col(26, 24, 26)); // visor slit
                Raylib.DrawRectangleV(p + new Vector2(-0.3f, 0.7f), new(0.6f, 1.2f), shadowSteel);          // nasal ridge
                Raylib.DrawRectangleV(p + new Vector2(-0.6f, -3.6f), new(1.2f, 1.9f), Teams.Lerp(Teams.Colors[u.Team], White, 0.25f)); // plume
            }
        }

        foreach (var cv in caravans)
            if (Visible(cv.Pos, 10) && fog.IsClear(cv.Pos)) DrawCaravan(cv);

        wildlife.Draw(tl, br, fog, time, cam.Zoom);

        foreach (var a in arrows)
        {
            if (!Visible(a.Pos, 6) || !fog.IsClear(a.Pos)) continue;
            Raylib.DrawLineEx(a.Pos - a.Dir * 3.5f, a.Pos, 0.7f, Teams.Col(70, 46, 22));
            Raylib.DrawRectangleV(a.Pos - new Vector2(0.4f, 0.4f), new(0.8f, 0.8f), Teams.Col(220, 220, 210));
        }

        // health bars on wounded pixels once zoomed in enough to read them
        if (cam.Zoom >= 2f)
        {
            foreach (var u in drawList)
            {
                float maxHp = u.MaxHp;
                if (u.Hp >= maxHp) continue;
                float frac = Math.Clamp(u.Hp / maxHp, 0, 1);
                var origin = u.Pos + new Vector2(-2.5f, -half - 2.6f);
                Raylib.DrawRectangleV(origin, new(5, 1.1f), HpBack);
                Color fill = frac > 0.5f ? Teams.Lerp(Teams.Col(240, 200, 40), Teams.Col(70, 210, 70), (frac - 0.5f) * 2)
                                         : Teams.Lerp(Teams.Col(220, 40, 30), Teams.Col(240, 200, 40), frac * 2);
                Raylib.DrawRectangleV(origin, new(5 * frac, 1.1f), fill);
            }
        }
        DrawLinePreview();

        if (orderMarkerTime > 0)
        {
            float t = orderMarkerTime / 0.8f;
            Raylib.DrawRing(orderMarker, 3 + (1 - t) * 10, 4.5f + (1 - t) * 10, 0, 360, 32, Teams.WithAlpha(White, (int)(220 * t)));
        }
    }

    /// <summary>Where the player's marching troops are headed: each pixel's destination as a faint silhouette on
    /// the ground, and the squad's route as a dashed line with an arrowhead.</summary>
    void DrawOrders()
    {
        foreach (var sq in squads)
        {
            if (sq.Done || sq.Members.Count == 0 || sq.Members[0].Team != Teams.Player) continue;
            bool selected = sq.Members.Any(u => u.Selected);
            Color line = Teams.WithAlpha(Teams.Lerp(Teams.Colors[Teams.Player], White, 0.5f), selected ? 190 : 100);
            Color ghost = Teams.WithAlpha(Teams.Lerp(Teams.Colors[Teams.Player], Teams.Col(20, 30, 60), 0.4f), selected ? 90 : 50);

            foreach (var u in sq.Members) DrawUnitShape(u.Kind, u.Target, sq.Goal - sq.Anchor is var d && d.LengthSquared() > 1 ? Vector2.Normalize(d) : u.Heading, 0f, ghost);

            // route: from the anchor along the remaining path, dashed
            Vector2 prev = sq.Anchor;
            for (int k = sq.PathIndex; k < sq.Path.Count; k++)
            {
                Vector2 p = sq.Path[k];
                if (k % 2 == 0) Raylib.DrawLineEx(prev, p, 1.4f, line);
                prev = p;
            }
            if (sq.Path.Count >= 2)
            {
                Vector2 end = sq.Path[^1], dir = end - sq.Path[^2];
                if (dir.LengthSquared() > 0.01f)
                {
                    dir = Vector2.Normalize(dir);
                    Vector2 side = new(-dir.Y, dir.X);
                    Vector2 a = end + dir * 5, b = end - dir * 3 + side * 4, c = end - dir * 3 - side * 4;
                    float cross = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
                    if (cross > 0) (b, c) = (c, b);
                    Raylib.DrawTriangle(a, b, c, line);
                }
            }
        }
    }

    // small sprites, made once
    Texture2D[] caravanTex = Array.Empty<Texture2D>();
    Texture2D tavernTex, windmillTex, millHouseTex;

    void EnsureDetailArt()
    {
        if (caravanTex.Length > 0) return;
        caravanTex = CastleArt.BuildCaravan();
        tavernTex = CastleArt.BuildTavern(7);
        windmillTex = CastleArt.BuildWindmill(11);
        millHouseTex = CastleArt.BuildMillHouse(13);
    }

    /// <summary>An ox-drawn wagon facing the way it travels, with a pennant in its owner's colours.</summary>
    void DrawCaravan(Caravan cv)
    {
        const float b = CastleArt.DetailScale;
        int facing = MathF.Abs(cv.Heading.X) >= MathF.Abs(cv.Heading.Y) ? (cv.Heading.X >= 0 ? 0 : 1) : (cv.Heading.Y < 0 ? 2 : 3);
        var tex = caravanTex[facing];
        Vector2 pos = new(MathF.Round(cv.Pos.X / b) * b, MathF.Round(cv.Pos.Y / b) * b);
        Raylib.DrawTextureEx(tex, pos - new Vector2(tex.Width, tex.Height) * b / 2 + new Vector2(b, b), 0, b, Teams.Col(0, 0, 0, 70));
        Raylib.DrawTextureEx(tex, pos - new Vector2(tex.Width, tex.Height) * b / 2, 0, b, White);
        // pennant on a pole at the back of the wagon
        Vector2 back = pos - cv.Heading * 6f;
        Raylib.DrawRectangleV(back - new Vector2(0.3f, 4.5f), new Vector2(0.65f, 4.5f), Teams.Col(70, 48, 28));
        Raylib.DrawRectangleV(back + new Vector2(0.35f, -4.5f), new Vector2(2.6f, 1.6f), cv.Team >= 0 ? Teams.Colors[cv.Team] : White);
        if (cv.Hp < Config.CaravanHp && cam.Zoom >= 1.5f)
        {
            float frac = Math.Clamp(cv.Hp / Config.CaravanHp, 0, 1);
            Raylib.DrawRectangleV(pos + new Vector2(-5, -8), new(10, 1.2f), HpBack);
            Raylib.DrawRectangleV(pos + new Vector2(-5, -8), new(10 * frac, 1.2f), GoldCol);
        }
    }

    /// <summary>Pixel-art shading on top of a unit's shape: a lit top-left corner and a shaded lower edge.</summary>
    static void DrawUnitDetail(Unit u, Color col)
    {
        Color light = Teams.Lerp(col, Teams.Col(255, 255, 255), 0.4f), dark = Teams.Shade(col, 0.68f);
        Vector2 p = u.Pos;
        switch (u.Kind)
        {
            case UnitKind.Infantry:
                Raylib.DrawRectangleV(p - new Vector2(1.5f, 1.5f), new(1.1f, 1.1f), light);
                Raylib.DrawRectangleV(p + new Vector2(-1.5f, 0.75f), new(3f, 0.75f), dark);
                break;
            case UnitKind.Archer:
                Raylib.DrawRectangleV(p - new Vector2(1.1f, 1.2f), new(0.9f, 0.9f), light);
                Raylib.DrawRectangleV(p + new Vector2(-0.9f, 0.6f), new(1.8f, 0.7f), dark);
                break;
            case UnitKind.Cavalry:
                Raylib.DrawRectangleV(p + u.Heading * 1.2f - new Vector2(0.45f, 0.45f), new(0.9f, 0.9f), light);
                Raylib.DrawRectangleV(p - u.Heading * 0.9f - new Vector2(0.5f, 0.5f), new(1f, 1f), dark);
                break;
        }
    }
    /// <summary>Infantry are squares, archers circles, cavalry triangles pointing where they ride.
    /// grow &gt; 0 draws a slightly larger copy (used for the dark outline).</summary>
    static void DrawUnitShape(UnitKind kind, Vector2 pos, Vector2 heading, float grow, Color col)
    {
        switch (kind)
        {
            case UnitKind.Archer:
                Raylib.DrawPoly(pos, 8, 1.7f + grow, 0, col);
                break;
            case UnitKind.Cavalry:
            {
                Vector2 side = new(-heading.Y, heading.X);
                Vector2 tip = pos + heading * (2.6f + grow * 1.3f);
                Vector2 back = pos - heading * (1.8f + grow * 0.8f);
                Vector2 l = back + side * (2.0f + grow), r = back - side * (2.0f + grow);
                // raylib wants counter-clockwise winding on screen
                float cross = (l.X - tip.X) * (r.Y - tip.Y) - (l.Y - tip.Y) * (r.X - tip.X);
                if (cross > 0) (l, r) = (r, l);
                Raylib.DrawTriangle(tip, l, r, col);
                break;
            }
            default:
                float h = Config.UnitSize / 2f + grow;
                Raylib.DrawRectangleV(pos - new Vector2(h, h), new(h * 2, h * 2), col);
                break;
        }
    }

    /// <summary>Ghost pixels showing where a right-drag will put the troops: a new battle line, or with Shift
    /// the current formation moved and turned to face the drag.</summary>
    void DrawLinePreview()
    {
        Vector2 mouse = Raylib.GetMousePosition();
        if (!rightDragging || !IsLineDrag(mouse)) return;

        var sel = units.Where(u => u.Selected && u.Team == Teams.Player).ToList();
        Vector2 end = Raylib.GetScreenToWorld2D(mouse, cam);
        Color c = Teams.Colors[Teams.Player];

        IEnumerable<Vector2> ghosts;
        if (rightShift)
        {
            // facing arrow
            Vector2 d = end - rightStartWorld;
            Vector2 dir = Vector2.Normalize(d);
            Vector2 side = new(-dir.Y, dir.X);
            Raylib.DrawLineEx(rightStartWorld, end, 1.2f, Teams.WithAlpha(White, 170));
            Raylib.DrawTriangle(end + dir * 4, end - dir * 2 + side * 3, end - dir * 2 - side * 3, Teams.WithAlpha(White, 170));
            Raylib.DrawTriangle(end + dir * 4, end - dir * 2 - side * 3, end - dir * 2 + side * 3, Teams.WithAlpha(White, 170));
            ghosts = ShiftPreview(sel, rightStartWorld, end);
        }
        else
        {
            Raylib.DrawLineEx(rightStartWorld, end, 1.2f, Teams.WithAlpha(White, 150));
            if (sel.Count == 0) return;
            var squad = OrderSquad(sel, (rightStartWorld + end) / 2, Ctrl);
            int archers = squad.Count(u => u.Kind == UnitKind.Archer);
            ghosts = LineFormation(squad.Count, rightStartWorld, end, Centroid(squad)).Slots(squad.Count - archers, archers);
        }

        foreach (var s in ghosts)
        {
            Raylib.DrawRectangleV(s - new Vector2(1.5f, 1.5f), new(3, 3), Teams.WithAlpha(c, 120));
            Raylib.DrawRectangleLinesEx(new Rectangle(s.X - 1.5f, s.Y - 1.5f, 3, 3), 0.5f, Teams.WithAlpha(White, 160));
        }
    }

    static readonly Color RoadEdge = Teams.Col(104, 82, 52);
    static readonly Color RoadFill = Teams.Col(178, 152, 104);

    static readonly Color PavedEdge = Teams.Col(92, 88, 82), PavedFill = Teams.Col(170, 164, 152), PavedSet = Teams.Col(140, 134, 124);
    static readonly Color DirtRut = Teams.Col(138, 110, 72);

    /// <summary>Roadside inns, with smoke curling from the chimney.</summary>
    void DrawTaverns(Func<Vector2, float, bool> visible)
    {
        const float b = CastleArt.DetailScale;
        foreach (var p in taverns)
        {
            if (!visible(p, 30)) continue;
            Vector2 topLeft = p - new Vector2(17, 15) * b;
            Raylib.DrawTextureEx(tavernTex, topLeft, 0, b, White);
            for (int i = 0; i < 4; i++)
            {
                float t = (time * 0.35f + i / 4f) % 1f;
                Vector2 puff = topLeft + new Vector2(8.5f, 2) * b + new Vector2(MathF.Sin(t * 6 + i) * 1.4f, -t * 14f);
                puff = new Vector2(MathF.Round(puff.X / b) * b, MathF.Round(puff.Y / b) * b);
                float s = b * (1 + (int)(t * 3));
                Raylib.DrawRectangleV(puff, new Vector2(s, s), Teams.Col(206, 202, 196, (int)(160 * (1 - t))));
            }
        }
    }
    /// <summary>Foaming water where the river drops steeply.</summary>
    void DrawWaterfalls(Func<Vector2, float, bool> visible)
    {
        foreach (var w in terrain.Waterfalls)
        {
            if (!visible(w, 30)) continue;
            for (int i = 0; i < 14; i++)
            {
                float t = (time * 1.6f + i * 0.37f) % 1f;
                float x = MathF.Sin(i * 12.9898f) * 7f;
                var a = w + new Vector2(x, -8 + t * 16f);
                Raylib.DrawLineEx(a, a + new Vector2(0, 3f), 1f, Teams.Col(236, 244, 250, (int)(220 * (1 - t))));
            }
            Raylib.DrawCircleV(w + new Vector2(0, 8), 7f + MathF.Sin(time * 3) * 1f, Teams.Col(230, 240, 248, 70));
        }
    }

    // archers of each player band (a battle line, or a loose blob), refreshed with the blob cache
    readonly List<(List<Unit> archers, Formation? line)> archerBands = new();

    void RefreshArcherBands()
    {
        archerBands.Clear();
        foreach (var f in formations)
        {
            var a = f.Members.Where(u => u.Team == Teams.Player && u.Kind == UnitKind.Archer).ToList();
            if (a.Count > 0) archerBands.Add((a, f));
        }
        int stamp = ++stampCounter;
        foreach (var u in units)
        {
            if (u.Team != Teams.Player || u.Kind != UnitKind.Archer || u.Form != null || u.Stamp == stamp) continue;
            var loose = FloodBlob(u, stamp).Where(b => b.Kind == UnitKind.Archer && b.Form == null).ToList();
            if (loose.Count > 0) archerBands.Add((loose, null));
        }
    }

    /// <summary>Shades how far each band's archers can shoot: a capsule along a battle line, a circle round a blob.</summary>
    void DrawArcherRanges()
    {
        float range = Stats.Archer.Range;
        Color col = Teams.Colors[Teams.Player];
        foreach (var (archers, line) in archerBands)
        {
            var live = archers.Where(u => !u.Dead).ToList();
            if (live.Count == 0) continue;
            Vector2 c = Centroid(live);
            if (line != null)
            {
                float min = float.MaxValue, max = float.MinValue;
                foreach (var u in live)
                {
                    float s = Vector2.Dot(u.Pos - c, line.Side);
                    min = MathF.Min(min, s);
                    max = MathF.Max(max, s);
                }
                DrawCapsule(c + line.Side * min, c + line.Side * max, range, col);
            }
            else
            {
                float spread = live.Max(u => Vector2.Distance(u.Pos, c));
                DrawCapsule(c, c, range + spread, col);
            }
        }
    }

    static void DrawCapsule(Vector2 a, Vector2 b, float r, Color col)
    {
        Vector2 d = b - a;
        Vector2 dir = d.LengthSquared() > 0.01f ? Vector2.Normalize(d) : Vector2.UnitX;
        Vector2 n = new(-dir.Y, dir.X);
        const int seg = 18;
        var pts = new List<Vector2>(seg * 2 + 2);
        for (int i = 0; i <= seg; i++)
        {
            float t = -MathF.PI / 2 + MathF.PI * i / seg;
            pts.Add(b + (dir * MathF.Cos(t) + n * MathF.Sin(t)) * r);
        }
        for (int i = 0; i <= seg; i++)
        {
            float t = MathF.PI / 2 + MathF.PI * i / seg;
            pts.Add(a + (dir * MathF.Cos(t) + n * MathF.Sin(t)) * r);
        }

        Vector2 mid = (a + b) / 2;
        Color fill = Teams.WithAlpha(col, 18), edge = Teams.WithAlpha(col, 130);
        for (int i = 0; i < pts.Count; i++)
        {
            Vector2 p = pts[i], q = pts[(i + 1) % pts.Count];
            float cross = (p.X - mid.X) * (q.Y - mid.Y) - (p.Y - mid.Y) * (q.X - mid.X);
            if (cross > 0) Raylib.DrawTriangle(mid, q, p, fill);
            else Raylib.DrawTriangle(mid, p, q, fill);
            if (i % 2 == 0) Raylib.DrawLineEx(p, q, 0.8f, edge); // dashed outline
        }
    }

    // ------------------------------------------------------------------ pixel-art walls

    /// <summary>One block of a wall ring, on the castle sprite's own pixel grid.
    /// Layer: 0 wall-walk, 1 parapet (merlons/crenels), 2 outer face, 3 tower top, 4 tower face.</summary>
    readonly record struct WallCell(Vector2 Pos, int Seg, byte Layer, float Noise, bool GateMid, bool Alt);

    readonly Dictionary<City, List<WallCell>> wallCells = new();

    List<WallCell> WallCellsFor(City c, Walls w)
    {
        if (wallCells.TryGetValue(c, out var cached)) return cached;
        float p = CastleArt.Scale, R = w.Radius;
        Vector2 origin = c.Pos - new Vector2(c.Sprite.W, c.Sprite.H) * p / 2f;   // snap to the sprite's pixels
        int x0 = (int)MathF.Floor((c.Pos.X - R - 4 * p - origin.X) / p), x1 = (int)MathF.Ceiling((c.Pos.X + R + 4 * p - origin.X) / p);
        int y0 = (int)MathF.Floor((c.Pos.Y - R - 4 * p - origin.Y) / p), y1 = (int)MathF.Ceiling((c.Pos.Y + R + 4 * p - origin.Y) / p);
        var list = new List<WallCell>();
        float Noise(int x, int y) => (MathF.Sin(x * 12.9898f + y * 78.233f) * 43758.5453f % 1f + 1f) % 1f;

        // towers at every joint between sections (blocks of 4x3 top + 4x1 face)
        var towerBlocks = new HashSet<(int, int)>();
        for (int s = 0; s < w.N; s++)
        {
            float a = -MathF.PI + s * MathF.Tau / w.N;
            Vector2 tc = c.Pos + new Vector2(MathF.Cos(a), MathF.Sin(a)) * R;
            int tx = (int)MathF.Floor((tc.X - origin.X) / p) - 3, ty = (int)MathF.Floor((tc.Y - origin.Y) / p) - 4;
            for (int y = 0; y < 9; y++)
                for (int x = 0; x < 7; x++)
                {
                    towerBlocks.Add((tx + x, ty + y));
                    bool rim = y == 0 || x == 0 || x == 6 || y == 6;
                    // 7x7 top with a crenellated rim, then a 2-pixel masonry face
                    byte layer = (byte)(y < 7 ? 3 : 4);
                    list.Add(new WallCell(origin + new Vector2(tx + x + 0.5f, ty + y + 0.5f) * p, s, layer,
                                          Noise(tx + x, ty + y), false, rim && (x + y) % 2 == 0));
                }
        }

        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                if (towerBlocks.Contains((x, y))) continue;
                Vector2 cc = origin + new Vector2(x + 0.5f, y + 0.5f) * p;
                float t = Vector2.Distance(cc, c.Pos) - R;
                byte layer;
                if (t >= -p * 2f && t < 0) layer = 0;
                else if (t >= 0 && t < p * 1.5f) layer = 1;
                else if (t >= p * 1.5f && t < p * 3.5f && cc.Y > c.Pos.Y - R * 0.3f) layer = 2;   // face shows on the near side
                else continue;
                int seg = w.SegmentAt(c.Pos, cc);
                float ang = MathF.Atan2(cc.Y - c.Pos.Y, cc.X - c.Pos.X);
                float within = MathF.Abs(MathF.IEEERemainder(ang - w.SegmentAngle(seg), MathF.Tau)) / (MathF.Tau / w.N);
                list.Add(new WallCell(cc, seg, layer, Noise(x, y), within < 0.22f, ((x + y) / 2) % 2 == 0));
            }
        wallCells[c] = list;
        return list;
    }

    static readonly Color StoneTop = Teams.Col(178, 172, 160), StoneWalk = Teams.Col(154, 148, 138);
    static readonly Color StoneFace = Teams.Col(118, 112, 104), StoneDark = Teams.Col(84, 80, 74);
    static readonly Color GateWood = Teams.Col(118, 80, 46), GateIron = Teams.Col(58, 52, 48);

    /// <summary>Walls drawn block by block in the same pixel art as the castles: a wall-walk, crenellated parapet,
    /// darker outer face, towers at the joints and wooden gates in the gatehouses. Damage shows as cracks;
    /// a breach is a scatter of rubble.</summary>
    void DrawWalls(City c, Walls w)
    {
        float p = CastleArt.Scale;
        var cells = WallCellsFor(c, w);
        var size = new Vector2(p, p);
        foreach (var cell in cells)   // drop shadow first
        {
            if (w.Down[cell.Seg]) continue;
            Raylib.DrawRectangleV(cell.Pos - size / 2 + new Vector2(p * 0.6f, p * 0.6f), size, Teams.Col(0, 0, 0, 55));
        }
        foreach (var cell in cells)
        {
            int s = cell.Seg;
            if (w.Down[s])
            {
                if (cell.Layer >= 3 || cell.Noise > 0.45f) continue;                     // rubble
                Raylib.DrawRectangleV(cell.Pos - size / 2, size, cell.Noise < 0.2f ? StoneDark : StoneFace);
                continue;
            }
            Color col = cell.Layer switch
            {
                0 => StoneWalk,
                1 => cell.Alt ? StoneTop : StoneDark,                                   // merlons and crenels
                2 => (cell.Noise < 0.3f ? Teams.Shade(StoneFace, 0.88f) : StoneFace),   // masonry face
                3 => cell.Alt ? Teams.Shade(StoneTop, 1.12f) : (cell.Noise < 0.3f ? Teams.Shade(StoneTop, 0.92f) : StoneTop),
                _ => cell.Noise < 0.3f ? Teams.Shade(StoneFace, 0.88f) : StoneFace,
            };
            if (w.Gate[s] && cell.GateMid && cell.Layer <= 2)
                col = cell.Layer == 2 ? GateIron : (cell.Noise < 0.5f ? GateWood : Teams.Shade(GateWood, 0.82f));
            float health = w.Hp[s] / w.SegMax(s);
            if (cell.Noise < (1f - health) * 0.55f) col = StoneDark;                     // cracks spread as it weakens
            Raylib.DrawRectangleV(cell.Pos - size / 2, size, col);
        }
    }

    // ------------------------------------------------------------------ mills

    /// <summary>Windmills: a whitewashed tower with a pointed cap, and lattice sails with canvas turning in the wind.</summary>
    void DrawWindmills(Func<Vector2, float, bool> visible)
    {
        const float b = CastleArt.DetailScale;
        foreach (var m in windmills)
        {
            if (!visible(m, 40)) continue;
            Vector2 topLeft = m - new Vector2(10, 30) * b;
            Raylib.DrawTextureEx(windmillTex, topLeft, 0, b, White);
            Vector2 hub = topLeft + new Vector2(10, 9) * b;
            float spin = time * 0.9f + m.X * 0.01f;
            for (int arm = 0; arm < 4; arm++)
            {
                float a = spin + arm * MathF.PI / 2;
                Vector2 dir = new(MathF.Cos(a), MathF.Sin(a)), side = new(-dir.Y, dir.X);
                for (int k = 1; k <= 13; k++)
                    for (int w = 0; w <= 3; w++)
                    {
                        if (w > 0 && k < 4) continue;
                        Vector2 q = hub + dir * k * b + side * w * b;
                        q = new Vector2(MathF.Round(q.X / b) * b, MathF.Round(q.Y / b) * b);   // stay on the pixel grid
                        Color col = w == 0 ? Teams.Col(92, 66, 42)                                       // stock
                                  : (k % 3 == 0 || w == 3) ? Teams.Col(120, 92, 62)                      // lattice
                                  : Teams.Col(236, 230, 212);                                            // canvas
                        Raylib.DrawRectangleV(q - new Vector2(b / 2, b / 2), new Vector2(b, b), col);
                    }
            }
            Raylib.DrawRectangleV(hub - new Vector2(b, b), new Vector2(b * 2, b * 2), Teams.Col(56, 40, 26));
        }
    }

    /// <summary>A water mill on the river bank: the mill house, and a paddle wheel turning in the current.</summary>
    void DrawWaterWheels(Func<Vector2, float, bool> visible)
    {
        const float b = CastleArt.DetailScale;
        foreach (var (pos, across) in waterWheels)
        {
            if (!visible(pos, 40)) continue;
            Vector2 house = pos + across * 9f;
            Raylib.DrawTextureEx(millHouseTex, house - new Vector2(12, 11) * b, 0, b, White);
            Vector2 hub = pos - across * 2.5f;
            float spin = time * 1.4f;
            for (int r = 0; r <= 7; r++)                     // rim and spokes on the fine pixel grid
                for (int k = 0; k < 16; k++)
                {
                    float a = spin + k * MathF.Tau / 16;
                    bool spoke = k % 4 == 0;
                    if (r < 7 && !spoke) continue;
                    Vector2 q = hub + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r * b;
                    q = new Vector2(MathF.Round(q.X / b) * b, MathF.Round(q.Y / b) * b);
                    Color col = r == 7 ? (k % 2 == 0 ? Teams.Col(96, 66, 40) : Teams.Col(132, 94, 58)) : Teams.Col(110, 78, 46);
                    Raylib.DrawRectangleV(q - new Vector2(b / 2, b / 2), new Vector2(b, b), col);
                }
            Raylib.DrawRectangleV(hub - new Vector2(b, b), new Vector2(b * 2, b * 2), Teams.Col(58, 42, 28));
            float sp = (time * 2f) % 1f;                    // splashes where the paddles hit the water
            for (int k = 0; k < 3; k++)
                Raylib.DrawRectangleV(hub - across * 5f + new Vector2((sp + k * 0.33f) % 1f * 4 - 2, k * b), new Vector2(b, b), Teams.Col(232, 242, 248, 170));
        }
    }

    void DrawCastle(City c)
    {
        var s = c.Sprite;
        float w = s.W * CastleArt.Scale, h = s.H * CastleArt.Scale;
        Vector2 topLeft = c.Pos - new Vector2(w / 2, h / 2);
        Color owner = c.KnownOwner >= 0 ? Teams.Colors[c.KnownOwner] : NeutralBanner;

        // dashed territory ring in the owner's colours
        const int dashes = 28;
        for (int i = 0; i < dashes; i += 2)
        {
            float a0 = i * 360f / dashes + time * 4f;
            Raylib.DrawRing(c.Pos, c.Radius - 1.2f, c.Radius, a0, a0 + 360f / dashes, 6,
                Teams.WithAlpha(owner, c.KnownOwner >= 0 ? 200 : 110));
        }
        if (fog.IsClear(c.Pos) && c.CaptureTeam >= 0 && c.Capture > 0 && (c.Capture < 1f || c.Owner != c.CaptureTeam))
            Raylib.DrawRing(c.Pos, c.Radius + 2f, c.Radius + 4.5f, -90, -90 + 360 * c.Capture, 64, Teams.Colors[c.CaptureTeam]);

        Raylib.DrawTexturePro(s.Texture, new Rectangle(0, 0, s.W, s.H), new Rectangle(topLeft.X, topLeft.Y, w, h),
            Vector2.Zero, 0, White);
        if (c.Walls != null) DrawWalls(c, c.Walls);

        // banner on the keep, rippling in the wind
        Vector2 pole = topLeft + s.FlagBase * CastleArt.Scale + new Vector2(CastleArt.Scale / 2, 0);
        float px = CastleArt.Scale;
        Raylib.DrawRectangleV(pole - new Vector2(px / 2, px * 7), new(px * 0.6f, px * 7), Teams.Col(60, 42, 26));
        for (int col = 0; col < 5; col++)
        {
            float wave = MathF.Round(MathF.Sin(time * 5f + col * 0.9f)) * px * 0.5f;
            int rows = col == 4 ? 2 : 3;  // swallow-tail
            for (int row = 0; row < rows; row++)
            {
                Color fc = row == 0 ? Teams.Lerp(owner, White, 0.25f) : owner;
                Raylib.DrawRectangleV(pole + new Vector2(px * 0.6f + col * px * 0.8f, -px * 7 + row * px + wave),
                    new(px * 0.8f, px), fc);
            }
        }

        // name scroll beneath the castle
        string label = $"{c.Name}  +{c.Income}g";
        const int fs = 14;
        int lw = Raylib.MeasureText(label, fs);
        var r = new Rectangle(c.Pos.X - lw / 2f - 4, c.Pos.Y + h / 2 + 2, lw + 8, fs + 4);
        Raylib.DrawRectangleRec(r, Parchment);
        Raylib.DrawRectangleLinesEx(r, 1, ParchmentEdge);
        Raylib.DrawRectangleV(new(r.X - 2, r.Y + 2), new(2, fs), Teams.WithAlpha(owner, 255));
        Raylib.DrawRectangleV(new(r.X + r.Width, r.Y + 2), new(2, fs), Teams.WithAlpha(owner, 255));
        Raylib.DrawText(label, (int)(r.X + 4), (int)(r.Y + 2), fs, Ink);
    }

    // ------------------------------------------------------------------ UI

    static void ParchmentPanel(int x, int y, int w, int h)
    {
        Raylib.DrawRectangle(x + 3, y + 3, w, h, Teams.Col(0, 0, 0, 90));
        Raylib.DrawRectangle(x, y, w, h, Parchment);
        Raylib.DrawRectangleLinesEx(new Rectangle(x, y, w, h), 2, ParchmentEdge);
        Raylib.DrawRectangleLinesEx(new Rectangle(x + 4, y + 4, w - 8, h - 8), 1, Teams.WithAlpha(ParchmentEdge, 110));
    }

    void DrawDragBox()
    {
        if (!dragging) return;
        Vector2 m = Raylib.GetMousePosition();
        if (Vector2.Distance(m, dragStart) < 5f) return;
        Vector2 min = Vector2.Min(m, dragStart), max = Vector2.Max(m, dragStart);
        var r = new Rectangle(min.X, min.Y, max.X - min.X, max.Y - min.Y);
        Raylib.DrawRectangleRec(r, Teams.WithAlpha(Teams.Colors[0], 35));
        Raylib.DrawRectangleLinesEx(r, 1, Teams.Colors[0]);
    }

    void DrawHud()
    {
        int selected = 0, veterans = 0, braced = 0;
        float hpSum = 0, hpMax = 0;
        var kinds = new int[3];
        foreach (var u in units)
        {
            if (u.Selected)
            {
                selected++;
                if (u.Brace < 1f) braced++;
            }
            if (u.Team != Teams.Player) continue;
            hpSum += u.Hp;
            hpMax += u.MaxHp;
            kinds[(int)u.Kind]++;
            if (u.Rank > 0) veterans++;
        }
        int myCities = cities.Count(c => c.Owner == Teams.Player);
        int income = cities.Where(c => c.Owner == Teams.Player).Sum(c => c.Income);
        int health = hpMax > 0 ? (int)MathF.Round(hpSum / hpMax * 100) : 0;

        ParchmentPanel(10, 10, 270, 268);
        Raylib.DrawText("House Azure", 22, 20, 20, Ink);
        Raylib.DrawRectangle(22, 42, 246, 1, ParchmentEdge);
        Raylib.DrawCircle(30, 59, 7, GoldInk);
        Raylib.DrawCircle(30, 59, 5.5f, GoldCol);
        Raylib.DrawText($"{gold[0]} gold", 44, 50, 20, Ink);
        Raylib.DrawText($"+{income}/s", 200, 54, 14, GoldInk);
        Raylib.DrawText($"Army: {teamCount[0]}  ({kinds[0]} inf, {kinds[1]} arch, {kinds[2]} cav)", 22, 76, 14, Ink);
        Raylib.DrawText($"Health: {health}%", 22, 94, 14, health > 60 ? Ink : Teams.Col(160, 30, 20));
        Raylib.DrawText($"Veterans: {veterans}", 150, 94, 14, Ink);
        Raylib.DrawText($"Holdings: {myCities}/{cities.Count}", 22, 112, 14, Ink);
        Raylib.DrawText($"Selected: {selected}" + (braced > 0 ? $" ({braced} braced)" : ""), 150, 112, 14, Ink);

        int ry = 134;
        foreach (var (key, kind) in new[] { ("Z", UnitKind.Infantry), ("X", UnitKind.Archer), ("C", UnitKind.Cavalry) })
        {
            var st = Stats.Of(kind);
            bool afford = gold[0] >= st.Cost;
            DrawUnitShape(kind, new Vector2(28, ry + 6), new Vector2(1, 0), 1.5f, Outline);
            DrawUnitShape(kind, new Vector2(28, ry + 6), new Vector2(1, 0), 0.5f, afford ? Teams.Colors[0] : InkDim);
            Raylib.DrawText($"[{key}] {st.Name} - {st.Cost}g", 40, ry, 14, afford ? Ink : InkDim);
            ry += 18;
        }

        // rival houses
        int y = 196;
        for (int t = 1; t <= Config.AiCount; t++)
        {
            int ec = cities.Count(c => c.KnownOwner == t);
            int seen = 0;
            foreach (var u in units) if (u.Team == t && fog.IsClear(u.Pos)) seen++;
            bool alive = IsAlive(t);
            Raylib.DrawRectangle(22, y + 2, 8, 8, alive ? Teams.Colors[t] : InkDim);
            Raylib.DrawRectangleLines(22, y + 2, 8, 8, Ink);
            string s = alive ? $"{Teams.Names[t]}: {ec} held, {seen} seen" : $"{Teams.Names[t]}: fallen";
            Raylib.DrawText(s, 36, y, 13, alive ? Ink : InkDim);
            y += 17;
        }

        // terrain readout under the cursor - this is where high ground matters
        Vector2 world = Raylib.GetScreenToWorld2D(Raylib.GetMousePosition(), cam);
        DrawSettlementTooltip(world);
        if (world.X >= 0 && world.Y >= 0 && world.X < Config.WorldW && world.Y < Config.WorldH)
        {
            float h = terrain.HeightAt(world);
            string info = $"{terrain.Describe(world)}  -  {Terrain.Feet(h)} ft";
            int sw = Raylib.GetScreenWidth();
            int w = Raylib.MeasureText(info, 14) + 20;
            ParchmentPanel(sw - w - 100, 10, w, 26);
            Raylib.DrawText(info, sw - w - 90, 16, 14, Ink);
        }

        string[] help =
        {
            "LMB: select blob   Drag: box   Ctrl+A: all",
            "RMB: move all    RMB drag: battle line",
            "Ctrl+RMB: move split    Ctrl+Wheel: split %",
            "Shift+RMB: move, keep formation",
            "Shift+RMB drag: turn to face the drag",
            "1 / 2 / 3: pick infantry / archers / cavalry",
            "Z / X / C: recruit into selection",
            "R: regroup all    F: find army",
            "Arrows / middle-drag: pan    Wheel: zoom",
            "High ground & braced lines win fights",
            "Big blobs march slowly. Raid caravans!",
            "Castles: strong walls. Towns: 2x gold",
            "Roads are fast. Water can't be crossed",
        };
        int hy = Raylib.GetScreenHeight() - help.Length * 15 - 22;
        ParchmentPanel(10, hy - 8, 300, help.Length * 15 + 18);
        for (int i = 0; i < help.Length; i++)
            Raylib.DrawText(help[i], 20, hy + 1 + i * 15, 12, i >= help.Length - 3 ? GoldInk : Ink);

        if (toast != null && toastTime > 0)
        {
            const int fs = 20;
            int w = Raylib.MeasureText(toast, fs);
            int x = (Raylib.GetScreenWidth() - w) / 2;
            int ty = 52 - (int)(MathF.Max(0, toastTime - 3.2f) * 60);
            ParchmentPanel(x - 16, ty, w + 32, 36);
            Raylib.DrawText(toast, x, ty + 9, fs, Ink);
        }

        Raylib.DrawFPS(Raylib.GetScreenWidth() - 84, 14);
    }

    /// <summary>Hovering a settlement shows what it is worth and how well it defends.</summary>
    void DrawSettlementTooltip(Vector2 world)
    {
        var c = cities.FirstOrDefault(s => Vector2.DistanceSquared(s.Pos, world) < s.Radius * s.Radius);
        if (c == null || !fog.IsExplored(c.Pos)) return;
        string[] lines =
        {
            $"{c.Name}",
            $"{c.Kind} (size {c.Size})",
            $"+{c.Income} gold/s",
            c.Walls is Walls cw ? $"Walls: {cw.Down.Count(d => !d)}/{cw.N} sections standing" : $"Defence: {MathF.Round((1 - c.Defense) * 100)}% less damage",
            $"Held by: {(c.KnownOwner >= 0 ? Teams.Names[c.KnownOwner] : "nobody")}" + (fog.IsClear(c.Pos) ? "" : " (last seen)"),
        };
        Vector2 m = Raylib.GetMousePosition();
        int w = lines.Max(l => Raylib.MeasureText(l, 14)) + 24, h = lines.Length * 18 + 14;
        int x = (int)Math.Min(m.X + 18, Raylib.GetScreenWidth() - w - 8), y = (int)Math.Min(m.Y + 18, Raylib.GetScreenHeight() - h - 8);
        ParchmentPanel(x, y, w, h);
        for (int i = 0; i < lines.Length; i++)
            Raylib.DrawText(lines[i], x + 12, y + 8 + i * 18, i == 0 ? 16 : 14,
                i == 0 ? Ink : i == 2 ? GoldInk : InkDim);
    }

    void DrawSlider()
    {
        int sw = Raylib.GetScreenWidth(), sh = Raylib.GetScreenHeight();
        int w = 320, h = 14;
        int x = (sw - w) / 2, y = sh - 62;
        int selected = units.Count(u => u.Selected);
        int sending = OrderCount(selected, split: true);

        ParchmentPanel(x - 14, y - 30, w + 28, h + 44);
        Raylib.DrawText($"Split: {MathF.Round(splitFraction * 100)}%   (Ctrl+RMB sends {sending} of {selected})", x, y - 21, 15,
            Ctrl ? Teams.Col(30, 80, 150) : Ink);
        Raylib.DrawRectangle(x, y, w, h, Teams.Col(120, 92, 58));
        Raylib.DrawRectangle(x, y, (int)(w * splitFraction), h, Teams.Colors[0]);
        Raylib.DrawRectangleLines(x, y, w, h, Ink);
        for (int i = 1; i < 4; i++) Raylib.DrawRectangle(x + w * i / 4, y, 1, h, Teams.Col(58, 38, 20, 120));
        Raylib.DrawText("Ctrl + Wheel", x + w - Raylib.MeasureText("Ctrl + Wheel", 10), y + h + 2, 10, InkDim);
    }

    void DrawMinimap()
    {
        var r = MinimapRect();
        Raylib.DrawRectangle((int)r.X - 4, (int)r.Y - 4, (int)r.Width + 8, (int)r.Height + 8, Frame);
        Raylib.DrawTexturePro(terrain.Texture, new Rectangle(0, 0, terrain.TexW, terrain.TexH), r, Vector2.Zero, 0, White);
        Raylib.DrawRectangleLinesEx(new Rectangle(r.X - 4, r.Y - 4, r.Width + 8, r.Height + 8), 2, FrameLight);
        float sx = r.Width / Config.WorldW, sy = r.Height / Config.WorldH;
        Vector2 ToMini(Vector2 p) => new(r.X + p.X * sx, r.Y + p.Y * sy);

        foreach (var road in terrain.Roads)
            for (int k = 4; k < road.Points.Count; k += 4)
                Raylib.DrawLineV(ToMini(road.Points[k - 4]), ToMini(road.Points[k]), road.Paved ? PavedEdge : RoadEdge);
        Raylib.DrawTexturePro(fog.Texture, new Rectangle(0, 0, fog.W, fog.H),
            new Rectangle(r.X, r.Y, fog.W * Config.FogCell * sx, fog.H * Config.FogCell * sy), Vector2.Zero, 0, White);
        foreach (var c in cities)
        {
            if (!fog.IsExplored(c.Pos)) continue;
            var p = ToMini(c.Pos);
            int s = 3 + c.Size;
            Raylib.DrawRectangle((int)p.X - s / 2 - 1, (int)p.Y - s / 2 - 1, s + 2, s + 2, Outline);
            Raylib.DrawRectangle((int)p.X - s / 2, (int)p.Y - s / 2, s, s, c.KnownOwner >= 0 ? Teams.Colors[c.KnownOwner] : NeutralBanner);
        }
        foreach (var cv in caravans)
            if (fog.IsClear(cv.Pos)) { var p = ToMini(cv.Pos); Raylib.DrawRectangle((int)p.X, (int)p.Y, 2, 2, Teams.Col(228, 214, 176)); }
        foreach (var u in units)
        {
            if (u.Team == Teams.Neutral) continue;
            if (u.Team != Teams.Player && !fog.IsClear(u.Pos)) continue;
            var p = ToMini(u.Pos);
            Raylib.DrawPixel((int)p.X, (int)p.Y, Teams.Colors[u.Team]);
        }

        Vector2 tl = ToMini(Raylib.GetScreenToWorld2D(Vector2.Zero, cam));
        Vector2 br = ToMini(Raylib.GetScreenToWorld2D(new(Raylib.GetScreenWidth(), Raylib.GetScreenHeight()), cam));
        Raylib.DrawRectangleLines((int)tl.X, (int)tl.Y, (int)(br.X - tl.X), (int)(br.Y - tl.Y), White);
    }

    void DrawGameOver()
    {
        int sw = Raylib.GetScreenWidth(), sh = Raylib.GetScreenHeight();
        Raylib.DrawRectangle(0, 0, sw, sh, Teams.Col(0, 0, 0, 150));
        ParchmentPanel(sw / 2 - 260, sh / 2 - 100, 520, 190);
        string title = victory ? "THE REALM IS YOURS" : "YOUR HOUSE HAS FALLEN";
        string sub = victory ? "Every rival banner has been torn down." : "Your last pixel lies on the field.";
        Color col = victory ? Teams.Col(40, 90, 160) : Teams.Col(150, 30, 20);
        Raylib.DrawText(title, (sw - Raylib.MeasureText(title, 36)) / 2, sh / 2 - 70, 36, col);
        Raylib.DrawText(sub, (sw - Raylib.MeasureText(sub, 20)) / 2, sh / 2 - 12, 20, Ink);
        const string again = "Press Enter to ride again";
        Raylib.DrawText(again, (sw - Raylib.MeasureText(again, 18)) / 2, sh / 2 + 34, 18, InkDim);
    }
}
