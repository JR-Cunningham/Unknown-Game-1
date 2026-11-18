using System.Numerics;
using Raylib_cs;

namespace PixelLegion;

sealed partial class Game
{
    const float MinimapW = 240f, MinimapH = 160f, MinimapMargin = 12f;

    static Rectangle MinimapRect() => new(
        Raylib.GetScreenWidth() - MinimapW - MinimapMargin,
        Raylib.GetScreenHeight() - MinimapH - MinimapMargin,
        MinimapW, MinimapH);

    static bool Ctrl => Raylib.IsKeyDown(KeyboardKey.LeftControl) || Raylib.IsKeyDown(KeyboardKey.RightControl);
    static bool Shift => Raylib.IsKeyDown(KeyboardKey.LeftShift) || Raylib.IsKeyDown(KeyboardKey.RightShift);

    void HandleCamera(float dt)
    {
        // pan: arrow keys, or drag with the middle mouse button
        Vector2 pan = Vector2.Zero;
        // pan: WASD or the arrow keys (WASD is ignored while Ctrl is held, so Ctrl+A still selects all)
        bool wasd = !Ctrl;
        if (Raylib.IsKeyDown(KeyboardKey.Up) || (wasd && Raylib.IsKeyDown(KeyboardKey.W))) pan.Y -= 1;
        if (Raylib.IsKeyDown(KeyboardKey.Down) || (wasd && Raylib.IsKeyDown(KeyboardKey.S))) pan.Y += 1;
        if (Raylib.IsKeyDown(KeyboardKey.Left) || (wasd && Raylib.IsKeyDown(KeyboardKey.A))) pan.X -= 1;
        if (Raylib.IsKeyDown(KeyboardKey.Right) || (wasd && Raylib.IsKeyDown(KeyboardKey.D))) pan.X += 1;
        cam.Target += pan * (700f / cam.Zoom) * dt;
        if (Raylib.IsMouseButtonDown(MouseButton.Middle)) cam.Target -= Raylib.GetMouseDelta() / cam.Zoom;

        Vector2 mouse = Raylib.GetMousePosition();
        float wheel = Raylib.GetMouseWheelMove();
        if (wheel != 0 && !Ctrl)
        {
            Vector2 before = Raylib.GetScreenToWorld2D(mouse, cam);
            cam.Zoom = Math.Clamp(cam.Zoom * (1f + 0.12f * wheel), 0.18f, 8f);
            Vector2 after = Raylib.GetScreenToWorld2D(mouse, cam);
            cam.Target += before - after;
        }

        cam.Target = new Vector2(Math.Clamp(cam.Target.X, 0, Config.WorldW), Math.Clamp(cam.Target.Y, 0, Config.WorldH));
    }

    void HandleInput(float dt)
    {
        Vector2 mouse = Raylib.GetMousePosition();
        Vector2 world = Raylib.GetScreenToWorld2D(mouse, cam);
        var mini = MinimapRect();
        bool overMinimap = Raylib.CheckCollisionPointRec(mouse, mini);

        // Ctrl + wheel: split slider (5% steps)
        float wheel = Raylib.GetMouseWheelMove();
        if (wheel != 0 && Ctrl)
        {
            splitFraction = MathF.Round((splitFraction + MathF.Sign(wheel) * 0.05f) * 20f) / 20f;
            splitFraction = Math.Clamp(splitFraction, 0.05f, 1f);
        }

        // left mouse: click = select blob, drag = box select, minimap = move camera
        if (Raylib.IsMouseButtonPressed(MouseButton.Left))
        {
            if (overMinimap) minimapDrag = true;
            else { dragging = true; dragStart = mouse; }
        }
        if (minimapDrag && Raylib.IsMouseButtonDown(MouseButton.Left))
        {
            cam.Target = new Vector2(
                (mouse.X - mini.X) / mini.Width * Config.WorldW,
                (mouse.Y - mini.Y) / mini.Height * Config.WorldH);
        }
        if (Raylib.IsMouseButtonReleased(MouseButton.Left))
        {
            if (minimapDrag) minimapDrag = false;
            else if (dragging)
            {
                dragging = false;
                if (Vector2.Distance(mouse, dragStart) < 5f) ClickSelect(world);
                else BoxSelect(Raylib.GetScreenToWorld2D(dragStart, cam), world);
            }
        }

        // right mouse: click = move (Ctrl = only the slider's share), drag = draw a battle line,
        // Shift = keep the current formation (drag to turn it to face the drag direction)
        if (Raylib.IsMouseButtonPressed(MouseButton.Right))
        {
            Vector2 miniWorld = new((mouse.X - mini.X) / mini.Width * Config.WorldW,
                                    (mouse.Y - mini.Y) / mini.Height * Config.WorldH);
            if (overMinimap)
            {
                if (Shift) ShiftOrder(miniWorld, null);
                else IssueOrder(miniWorld, null, Ctrl);
            }
            else
            {
                rightDragging = true;
                rightShift = Shift;
                rightStartScreen = mouse;
                rightStartWorld = world;
            }
        }
        if (rightDragging && Raylib.IsMouseButtonReleased(MouseButton.Right))
        {
            rightDragging = false;
            Vector2? end = IsLineDrag(mouse) ? world : null;
            if (rightShift) ShiftOrder(rightStartWorld, end);
            else IssueOrder(rightStartWorld, end, Ctrl);
        }

        // 1/2/3: split the selection by type into its own group
        if (Raylib.IsKeyPressed(KeyboardKey.One)) SelectKind(UnitKind.Infantry);
        if (Raylib.IsKeyPressed(KeyboardKey.Two)) SelectKind(UnitKind.Archer);
        if (Raylib.IsKeyPressed(KeyboardKey.Three)) SelectKind(UnitKind.Cavalry);

        if (Raylib.IsKeyPressed(KeyboardKey.R)) RegroupAll();
        if (Raylib.IsKeyPressed(KeyboardKey.F) && teamCount[Teams.Player] > 0) cam.Target = blobCenter[Teams.Player];
        if (Ctrl && Raylib.IsKeyPressed(KeyboardKey.A)) // select everything
            foreach (var u in units) u.Selected = u.Team == Teams.Player;

        // Z/X/C: recruit into the selected group (hold to keep recruiting)
        HandleRecruitKey(KeyboardKey.Z, UnitKind.Infantry, dt);
        HandleRecruitKey(KeyboardKey.X, UnitKind.Archer, dt);
        HandleRecruitKey(KeyboardKey.C, UnitKind.Cavalry, dt);
    }

    void HandleRecruitKey(KeyboardKey key, UnitKind kind, float dt)
    {
        if (Raylib.IsKeyPressed(key))
        {
            var st = Stats.Of(kind);
            if (!RecruitIntoSelection(kind)) ShowToast($"Need {st.Cost} gold to recruit {st.Name.ToLower()}.");
            recruitRepeat = 0.35f;
        }
        else if (Raylib.IsKeyDown(key))
        {
            recruitRepeat -= dt;
            if (recruitRepeat <= 0)
            {
                RecruitIntoSelection(kind);
                recruitRepeat = 0.06f;
            }
        }
    }

    bool rightDragging, rightShift;
    Vector2 rightStartScreen, rightStartWorld;

    bool IsLineDrag(Vector2 mouse) => Vector2.Distance(mouse, rightStartScreen) > 8f;

    /// <summary>How many pixels an order will move: all selected, or the slider's share with Ctrl held.</summary>
    int OrderCount(int selected, bool split) =>
        selected == 0 ? 0 : split ? Math.Clamp((int)MathF.Round(selected * splitFraction), 1, selected) : selected;

    void ClickSelect(Vector2 world)
    {
        if (!Shift) foreach (var u in units) u.Selected = false;

        Unit? best = null;
        float pick = MathF.Max(6f, 14f / cam.Zoom);
        float bestD2 = pick * pick;
        foreach (var u in units)
        {
            if (u.Team != Teams.Player) continue;
            float d2 = Vector2.DistanceSquared(u.Pos, world);
            if (d2 < bestD2) { bestD2 = d2; best = u; }
        }
        if (best == null) return;

        foreach (var u in FloodBlob(best, ++stampCounter)) u.Selected = true;
    }

    void BoxSelect(Vector2 a, Vector2 b)
    {
        Vector2 min = Vector2.Min(a, b), max = Vector2.Max(a, b);
        foreach (var u in units)
        {
            if (u.Team != Teams.Player) continue;
            bool inside = u.Pos.X >= min.X && u.Pos.X <= max.X && u.Pos.Y >= min.Y && u.Pos.Y <= max.Y;
            if (inside) u.Selected = true;
            else if (!Shift) u.Selected = false;
        }
    }

    /// <summary>Moves the selection to a point (lineEnd null) or into a line from target to lineEnd.
    /// With split, only the slider's share moves (the pixels nearest the destination), and that
    /// detached squad becomes the new selection.</summary>
    void IssueOrder(Vector2 target, Vector2? lineEnd, bool split)
    {
        var sel = units.Where(u => u.Selected && u.Team == Teams.Player).ToList();
        if (sel.Count == 0) return;

        Vector2 dest = lineEnd is Vector2 e ? (target + e) / 2 : target;
        var squad = OrderSquad(sel, dest, split);

        if (split)
        {
            foreach (var u in sel) u.Selected = false;
            foreach (var u in squad) u.Selected = true;
        }

        if (lineEnd is Vector2 end) LineGroup(squad, target, end);
        else MoveGroup(squad, target, forced: true);
        orderMarker = dest;
        orderMarkerTime = 0.8f;
    }

    /// <summary>The pixels an order will move: all selected, or with split the slider's share nearest the destination.</summary>
    List<Unit> OrderSquad(List<Unit> sel, Vector2 dest, bool split)
    {
        int n = OrderCount(sel.Count, split);
        var sorted = sel.OrderBy(u => Vector2.DistanceSquared(u.Pos, dest)).ToList();
        return sorted.GetRange(0, n);
    }

    /// <summary>Hotkey R: every pixel returns to the largest (central) blob.</summary>
    void RegroupAll()
    {
        var central = LargestBlob(Teams.Player);
        if (central.Count == 0) return;
        Vector2 c = Centroid(central);
        blobCenter[Teams.Player] = c;

        var all = units.Where(u => u.Team == Teams.Player).ToList();
        MoveGroup(all, c, forced: true);
        foreach (var u in all) u.Selected = true;
        orderMarker = c;
        orderMarkerTime = 0.8f;
    }
}
