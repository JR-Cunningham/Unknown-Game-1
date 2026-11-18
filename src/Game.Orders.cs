using System.Numerics;

namespace PixelLegion;

sealed partial class Game
{
    // ------------------------------------------------------------------ basic moves

    /// <summary>Moves a group into a round blob around the target, with the archers in the rear half (away from
    /// the direction of travel) so the infantry stand between them and whatever is ahead. Breaks any battle lines.</summary>
    void MoveGroup(List<Unit> group, Vector2 target, bool forced)
    {
        if (group.Count == 0) return;
        target = terrain.NearestLand(ClampWorld(target));
        Vector2 facing = target - Centroid(group);
        facing = facing.LengthSquared() > 1f ? Vector2.Normalize(facing) : new Vector2(0, -1);

        var melee = group.Where(u => u.Kind != UnitKind.Archer).ToList();
        var archers = group.Where(u => u.Kind == UnitKind.Archer).ToList();
        var slots = Enumerable.Range(0, group.Count).Select(Spiral)
                              .OrderByDescending(o => Vector2.Dot(o, facing)).ToList();
        AssignNearest(melee, slots.Take(melee.Count).ToList(), target);
        AssignNearest(archers, slots.Skip(melee.Count).ToList(), target);

        foreach (var u in group)
        {
            LeaveFormation(u);
            u.Home = null;
            u.Forced = forced;
            u.ForcedTimeout = Vector2.Distance(u.Pos, u.Target) / Config.Speed * 2f + 2f;
        }
        StartSquad(group, target);
    }

    /// <summary>Closest pixels take the innermost slots.</summary>
    static void AssignNearest(List<Unit> group, List<Vector2> offsets, Vector2 target)
    {
        group.Sort((a, b) => Vector2.DistanceSquared(a.Pos, target).CompareTo(Vector2.DistanceSquared(b.Pos, target)));
        offsets.Sort((a, b) => a.LengthSquared().CompareTo(b.LengthSquared()));
        for (int i = 0; i < group.Count; i++) group[i].Target = ClampWorld(target + offsets[i]);
    }

    static void SetForced(Unit u)
    {
        u.Forced = true;
        u.ForcedTimeout = Vector2.Distance(u.Pos, u.Target) / Config.Speed * 2f + 2f;
    }

    // ------------------------------------------------------------------ recruiting

    bool Recruit(int team, UnitKind kind, Vector2? spawnAt = null)
    {
        int cost = Stats.Of(kind).Cost;
        if (gold[team] < cost) return false;

        Vector2 at;
        if (spawnAt is Vector2 s) at = s;
        else if (teamCount[team] > 0) at = blobCenter[team];
        else
        {
            var city = cities.FirstOrDefault(c => c.Owner == team);
            if (city == null) return false;
            at = city.Pos;
        }

        gold[team] -= cost;
        var target = terrain.NearestLand(ClampWorld(at + Spiral(teamCount[team] % 200)));
        units.Add(Unit.Make(kind, team, terrain.NearestLand(ClampWorld(at + RandomDir() * 2f)), target));
        teamCount[team]++;
        return true;
    }

    /// <summary>Player recruiting: the new pixel appears inside the selected group and joins it (including its
    /// battle line, if it has one). With nothing selected it falls back to the central blob.</summary>
    bool RecruitIntoSelection(UnitKind kind)
    {
        var sel = units.Where(u => u.Selected && u.Team == Teams.Player).ToList();
        if (sel.Count == 0) return Recruit(Teams.Player, kind);
        int cost = Stats.Of(kind).Cost;
        if (gold[Teams.Player] < cost) return false;
        gold[Teams.Player] -= cost;

        Vector2 targetCenter = Vector2.Zero;
        foreach (var s in sel) targetCenter += s.Target;
        targetCenter /= sel.Count;

        var host = sel[rng.Next(sel.Count)];
        var u = Unit.Make(kind, Teams.Player, ClampWorld(host.Pos + RandomDir() * 2f), ClampWorld(targetCenter + Spiral(sel.Count)));
        u.Selected = true;
        u.Forced = host.Forced;
        u.ForcedTimeout = host.ForcedTimeout;
        if (host.Squad != null) { u.Squad = host.Squad; host.Squad.Members.Add(u); }
        if (host.Form != null)
        {
            u.Form = host.Form;
            host.Form.Members.Add(u);
            host.Form.Dirty = true;
        }
        units.Add(u);
        teamCount[Teams.Player]++;
        return true;
    }

    /// <summary>Hotkeys 1/2/3: narrow the selection to one unit type. They become their own group - they leave any
    /// battle line without disturbing it, and keep their current spots until given an order.
    /// With nothing selected, picks every pixel of that type.</summary>
    void SelectKind(UnitKind kind)
    {
        var sel = units.Where(u => u.Selected && u.Team == Teams.Player).ToList();
        var pool = sel.Count > 0 ? sel : units.Where(u => u.Team == Teams.Player).ToList();
        var picked = pool.Where(u => u.Kind == kind).ToList();
        if (picked.Count == 0)
        {
            ShowToast($"No {Stats.Of(kind).Name.ToLower()} in {(sel.Count > 0 ? "the selection" : "your army")}.");
            return;
        }
        foreach (var u in sel) u.Selected = false;
        foreach (var u in picked)
        {
            u.Selected = true;
            if (u.Form != null)
            {
                u.Form.Members.Remove(u); // quiet detach: the rest of the line keeps its shape
                u.Form = null;
            }
        }
        formations.RemoveAll(f => f.Members.Count == 0);
    }

    // ------------------------------------------------------------------ battle lines

    void LeaveFormation(Unit u)
    {
        if (u.Form == null) return;
        u.Form.Dirty = true;
        u.Form = null;
    }

    /// <summary>Lays out a line from a to b, facing away from where the troops are coming from.</summary>
    static Formation LineFormation(int n, Vector2 a, Vector2 b, Vector2 from)
    {
        Vector2 d = b - a;
        float len = d.Length();
        Vector2 dir = len > 0.01f ? d / len : Vector2.UnitX;
        Vector2 back = new(-dir.Y, dir.X);
        if (Vector2.Dot(from - a, back) < 0) back = -back;
        return new Formation
        {
            Center = (a + b) / 2,
            Facing = -back,
            Cols = Math.Clamp((int)(len / Config.LineSpacing) + 1, 1, Math.Max(1, n)),
        };
    }

    /// <summary>Puts the group into a new battle line between a and b.</summary>
    void LineGroup(List<Unit> group, Vector2 a, Vector2 b)
    {
        if (group.Count == 0) return;
        var f = LineFormation(group.Count, ClampWorld(a), ClampWorld(b), Centroid(group));
        foreach (var u in group)
        {
            LeaveFormation(u);
            u.Form = f;
        }
        f.Members.AddRange(group);
        formations.Add(f);
        AssignFormation(f, forced: true);
        StartSquad(group, f.Center);
    }

    /// <summary>Hands out slots: melee ranks in front, archer ranks behind them. Within each block pixels are
    /// matched to slots by where they stand (or are heading) so their paths don't cross.</summary>
    void AssignFormation(Formation f, bool forced)
    {
        f.Dirty = false;
        var melee = f.Members.Where(u => u.Kind != UnitKind.Archer).ToList();
        var archers = f.Members.Where(u => u.Kind == UnitKind.Archer).ToList();
        int meleeCols = Math.Clamp(f.Cols, 1, Math.Max(1, melee.Count));
        AssignBlock(f, melee, meleeCols, 0, forced);
        AssignBlock(f, archers, Math.Clamp(f.Cols, 1, Math.Max(1, archers.Count)),
                    Formation.Ranks(melee.Count, meleeCols), forced);
    }

    void AssignBlock(Formation f, List<Unit> group, int cols, int firstRank, bool forced)
    {
        int n = group.Count;
        if (n == 0) return;
        Vector2 side = f.Side;
        Vector2 Key(Unit u) => forced ? u.Pos : u.Target;

        group.Sort((p, q) => Vector2.Dot(Key(p) - f.Center, side).CompareTo(Vector2.Dot(Key(q) - f.Center, side)));
        int idx = 0;
        for (int c = 0; c < cols; c++)
        {
            int size = Formation.ColumnSize(n, cols, c);
            var column = group.GetRange(idx, size);
            idx += size;
            column.Sort((p, q) => Vector2.Dot(Key(q) - f.Center, f.Facing).CompareTo(Vector2.Dot(Key(p) - f.Center, f.Facing)));
            for (int r = 0; r < column.Count; r++)
            {
                var u = column[r];
                u.Target = ClampWorld(f.Slot(cols, c, firstRank + r));
                if (forced) SetForced(u);
            }
        }
    }

    /// <summary>Lines that lost or gained pixels close ranks so they stay shoulder to shoulder.</summary>
    void UpdateFormations()
    {
        foreach (var f in formations)
        {
            if (!f.Dirty) continue;
            f.Members.RemoveAll(m => m.Dead || m.Form != f);
            AssignFormation(f, forced: false);
        }
        formations.RemoveAll(f => f.Members.Count == 0);
    }

    // ------------------------------------------------------------------ shift: move / turn keeping the formation

    static Vector2 Rotate(Vector2 v, float angle)
    {
        float c = MathF.Cos(angle), s = MathF.Sin(angle);
        return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }

    static float Angle(Vector2 v) => MathF.Atan2(v.Y, v.X);

    /// <summary>Works out a Shift order: the pivot of the selection, the formations moving as a whole, and the
    /// rotation needed so the group faces along the drag (0 for a plain Shift+click).</summary>
    (Vector2 pivot, List<Formation> whole, float rot) PlanShiftOrder(List<Unit> sel, Vector2 click, Vector2? dragEnd)
    {
        Vector2 pivot = Vector2.Zero;
        foreach (var u in sel) pivot += u.Target;
        pivot /= sel.Count;

        var whole = sel.Where(u => u.Form != null).Select(u => u.Form!).Distinct()
                       .Where(f => f.Members.All(m => m.Selected || m.Dead))
                       .OrderByDescending(f => f.Members.Count).ToList();

        float rot = 0;
        if (dragEnd is Vector2 end && Vector2.DistanceSquared(end, click) > 1f)
        {
            Vector2 oldFacing = whole.Count > 0 ? whole[0].Facing
                              : Vector2.DistanceSquared(click, pivot) > 1f ? Vector2.Normalize(click - pivot)
                              : new Vector2(0, -1);
            rot = Angle(end - click) - Angle(oldFacing);
        }
        return (pivot, whole, rot);
    }

    /// <summary>Shift+right click: move the selection keeping its current shape. Shift+right drag: also turn it
    /// to face the drag direction.</summary>
    void ShiftOrder(Vector2 click, Vector2? dragEnd)
    {
        var sel = units.Where(u => u.Selected && u.Team == Teams.Player).ToList();
        if (sel.Count == 0) return;
        click = terrain.NearestLand(ClampWorld(click));
        var (pivot, whole, rot) = PlanShiftOrder(sel, click, dragEnd);

        foreach (var u in sel)
        {
            // pixels pulled out of a partly-selected line keep their spot in the shape but leave that line
            if (u.Form != null && !whole.Contains(u.Form)) LeaveFormation(u);
            if (u.Form != null) continue;
            u.Target = ClampWorld(click + Rotate(u.Target - pivot, rot));
            SetForced(u);
        }
        foreach (var f in whole)
        {
            f.Center = click + Rotate(f.Center - pivot, rot);
            f.Facing = Vector2.Normalize(Rotate(f.Facing, rot));
            f.Members.RemoveAll(m => m.Dead);
            AssignFormation(f, forced: true);
        }

        StartSquad(sel, click);
        orderMarker = click;
        orderMarkerTime = 0.8f;
    }

    /// <summary>Where each selected pixel would end up, for the drag preview.</summary>
    List<Vector2> ShiftPreview(List<Unit> sel, Vector2 click, Vector2? dragEnd)
    {
        var result = new List<Vector2>(sel.Count);
        if (sel.Count == 0) return result;
        var (pivot, _, rot) = PlanShiftOrder(sel, click, dragEnd);
        foreach (var u in sel) result.Add(click + Rotate(u.Target - pivot, rot));
        return result;
    }
}
