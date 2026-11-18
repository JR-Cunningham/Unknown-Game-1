using System.Numerics;
using Raylib_cs;

namespace PixelLegion;

/// <summary>Little touches of life: deer, rabbits and sheep that graze and bolt from any troops that come near,
/// fish that swim (and now and then jump) in the lakes and river, and flocks of birds that scatter when they
/// fly over an army. Purely decorative.</summary>
sealed class Wildlife
{
    enum Kind { Deer, Rabbit, Sheep }

    sealed class Animal
    {
        public Kind K;
        public Vector2 Pos, Vel, Heading = Vector2.UnitX;
        public float Think;
        public bool Fleeing;
    }

    sealed class Fish
    {
        public Vector2 Pos, Dir;
        public float Speed, Turn, Jump;
        public bool Silver;
    }

    sealed class Flock
    {
        public Vector2 Pos, Vel, WantVel;
        public int Count;
        public float Check, Calm;
    }

    sealed class Boat
    {
        public Vector2 Pos, Dir;
        public float Speed, Turn;
        public int Sail;
    }

    readonly Terrain t;
    readonly Random rng;
    readonly List<Animal> animals = new();
    readonly List<Fish> fish = new();
    readonly List<Flock> flocks = new();
    readonly List<Boat> boats = new();

    bool OpenWater(Vector2 p) => t.HeightAt(p) < Terrain.WaterLevel - 0.03f && !t.RiverAt(p);

    public Wildlife(Terrain terrain, List<City> cities, Random rng)
    {
        t = terrain;
        this.rng = rng;
        var farms = cities.Where(c => c.Type is SettlementType.Village or SettlementType.Town).ToList();

        for (int i = 0; i < 320; i++)
        {
            var kind = i < 110 ? Kind.Deer : i < 230 ? Kind.Rabbit : Kind.Sheep;
            Vector2 p = Vector2.Zero;
            for (int tries = 0; tries < 60; tries++)
            {
                p = kind == Kind.Sheep && farms.Count > 0
                    ? farms[rng.Next(farms.Count)].Pos + Dir() * (90 + (float)rng.NextDouble() * 120)
                    : new Vector2((float)rng.NextDouble() * Config.WorldW, (float)rng.NextDouble() * Config.WorldH);
                if (t.OnMainland(p) && !t.RoadAt(p) && (kind != Kind.Deer || t.ForestAt(p) || rng.Next(3) == 0)) break;
            }
            if (!t.OnMainland(p)) continue;
            animals.Add(new Animal { K = kind, Pos = p, Think = (float)rng.NextDouble() });
        }

        for (int i = 0; i < 260; i++)
            for (int tries = 0; tries < 40; tries++)
            {
                var p = new Vector2((float)rng.NextDouble() * Config.WorldW, (float)rng.NextDouble() * Config.WorldH);
                if (t.HeightAt(p) > Terrain.WaterLevel - 0.012f || t.BridgeAt(p)) continue;
                fish.Add(new Fish { Pos = p, Dir = Dir(), Speed = 4 + (float)rng.NextDouble() * 6, Silver = rng.Next(3) == 0 });
                break;
            }

        for (int i = 0; i < 9; i++) flocks.Add(NewFlock(anywhere: true));

        for (int i = 0; i < 12; i++)
            for (int tries = 0; tries < 200; tries++)
            {
                var p = new Vector2((float)rng.NextDouble() * Config.WorldW, (float)rng.NextDouble() * Config.WorldH);
                if (!OpenWater(p) || !OpenWater(p + new Vector2(20, 0)) || !OpenWater(p - new Vector2(20, 0))) continue;
                boats.Add(new Boat { Pos = p, Dir = Dir(), Speed = 4 + (float)rng.NextDouble() * 4, Sail = rng.Next(3) });
                break;
            }
    }

    Vector2 Dir()
    {
        float a = (float)rng.NextDouble() * MathF.Tau;
        return new Vector2(MathF.Cos(a), MathF.Sin(a));
    }

    Flock NewFlock(bool anywhere)
    {
        // enter from a map edge heading across, or start anywhere at the beginning
        Vector2 pos, dir = Dir();
        if (anywhere) pos = new Vector2((float)rng.NextDouble() * Config.WorldW, (float)rng.NextDouble() * Config.WorldH);
        else
        {
            pos = new Vector2(Config.WorldW / 2, Config.WorldH / 2) - dir * (MathF.Max(Config.WorldW, Config.WorldH) * 0.6f);
            pos += new Vector2(-dir.Y, dir.X) * ((float)rng.NextDouble() - 0.5f) * Config.WorldH;
        }
        var v = dir * (30 + (float)rng.NextDouble() * 15);
        return new Flock { Pos = pos, Vel = v, WantVel = v, Count = 5 + rng.Next(5) };
    }

    public void Update(float dt, SpatialGrid grid, List<Unit> scratch)
    {
        foreach (var a in animals)
        {
            a.Think -= dt;
            if (a.Think <= 0)
            {
                a.Think = 0.25f + (float)rng.NextDouble() * 0.2f;
                float scare = a.K == Kind.Sheep ? 35f : 55f;
                grid.Query(a.Pos, scare, scratch);
                if (scratch.Count > 0)
                {
                    // bolt directly away from the nearest soldier
                    var near = scratch.OrderBy(u => Vector2.DistanceSquared(u.Pos, a.Pos)).First();
                    Vector2 away = a.Pos - near.Pos;
                    away = away.LengthSquared() > 0.01f ? Vector2.Normalize(away) : Dir();
                    float speed = a.K switch { Kind.Deer => 62f, Kind.Rabbit => 52f, _ => 26f };
                    a.Vel = away * speed;
                    a.Fleeing = true;
                }
                else if (a.Fleeing || rng.NextDouble() < 0.15)
                {
                    // graze: amble a little, or stand still
                    a.Fleeing = false;
                    float speed = a.K switch { Kind.Deer => 7f, Kind.Rabbit => 10f, _ => 3f };
                    a.Vel = rng.Next(2) == 0 ? Vector2.Zero : Dir() * speed;
                }
            }
            Vector2 next = a.Pos + a.Vel * dt;
            if (t.OnMainland(next)) a.Pos = next;
            else a.Vel = -a.Vel * 0.5f;
            if (a.Vel.LengthSquared() > 1f) a.Heading = Vector2.Normalize(a.Vel);
        }

        foreach (var f in fish)
        {
            f.Turn -= dt;
            if (f.Turn <= 0)
            {
                f.Turn = 1.5f + (float)rng.NextDouble() * 3f;
                f.Dir = Vector2.Normalize(f.Dir + Dir() * 0.8f);
            }
            Vector2 next = f.Pos + f.Dir * f.Speed * dt;
            if (t.HeightAt(next) < Terrain.WaterLevel - 0.008f) f.Pos = next;
            else f.Dir = -f.Dir;
            if (f.Jump > 0) f.Jump -= dt;
            else if (rng.NextDouble() < dt * 0.03) f.Jump = 0.6f;
        }

        // birds: a steady glide; an army below makes the flock veer away, turning smoothly
        for (int i = 0; i < flocks.Count; i++)
        {
            var fl = flocks[i];
            fl.Check -= dt;
            fl.Calm -= dt;
            if (fl.Check <= 0)
            {
                fl.Check = 0.5f;
                grid.Query(fl.Pos, 80f, scratch);
                if (scratch.Count > 0 && fl.Calm <= 0)
                {
                    float turn = (rng.Next(2) == 0 ? -1f : 1f) * (0.6f + (float)rng.NextDouble() * 0.5f);
                    float c = MathF.Cos(turn), s = MathF.Sin(turn);
                    fl.WantVel = new Vector2(fl.Vel.X * c - fl.Vel.Y * s, fl.Vel.X * s + fl.Vel.Y * c);
                    fl.Calm = 4f;
                }
            }
            fl.Vel = Vector2.Lerp(fl.Vel, fl.WantVel, MathF.Min(1f, dt * 0.8f));
            fl.Pos += fl.Vel * dt;
            if (fl.Pos.X < -400 || fl.Pos.Y < -400 || fl.Pos.X > Config.WorldW + 400 || fl.Pos.Y > Config.WorldH + 400)
                flocks[i] = NewFlock(anywhere: false);
        }

        // boats: drift across the lakes, turning away from the shore
        foreach (var b in boats)
        {
            b.Turn -= dt;
            if (b.Turn <= 0)
            {
                b.Turn = 4f + (float)rng.NextDouble() * 6f;
                b.Dir = Vector2.Normalize(b.Dir + Dir() * 0.5f);
            }
            if (!OpenWater(b.Pos + b.Dir * 16f)) b.Dir = Vector2.Normalize(-b.Dir + Dir() * 0.4f);
            Vector2 next = b.Pos + b.Dir * b.Speed * dt;
            if (OpenWater(next)) b.Pos = next;
        }
    }
    /// <summary>Animals and fish are drawn only where you can see clearly right now; birds too.</summary>
    public void Draw(Vector2 tl, Vector2 br, FogOfWar fog, float time, float zoom)
    {
        bool Show(Vector2 p) => p.X > tl.X - 10 && p.X < br.X + 10 && p.Y > tl.Y - 10 && p.Y < br.Y + 10 && fog.IsClear(p);
        float deg(Vector2 h) => MathF.Atan2(h.Y, h.X) * 180f / MathF.PI;

        if (zoom >= 1f)
            foreach (var f in fish)
            {
                if (!Show(f.Pos)) continue;
                Color col = f.Silver ? Teams.Col(178, 196, 206, 210) : Teams.Col(226, 140, 64, 200);
                Vector2 at = f.Pos;
                if (f.Jump > 0)
                {
                    float k = 1f - f.Jump / 0.6f;               // 0..1 through the leap
                    at += new Vector2(0, -MathF.Sin(k * MathF.PI) * 5f) + f.Dir * k * 4f;
                    if (k > 0.85f) Raylib.DrawRing(f.Pos + f.Dir * 4f, 1.5f, 2.2f, 0, 360, 10, Teams.Col(235, 245, 250, 180));
                }
                Raylib.DrawRectanglePro(new Rectangle(at.X, at.Y, 2.4f, 1.1f), new Vector2(1.2f, 0.55f), deg(f.Dir), col);
            }

        foreach (var a in animals)
        {
            if (!Show(a.Pos)) continue;
            switch (a.K)
            {
                case Kind.Deer:
                    Raylib.DrawRectanglePro(new Rectangle(a.Pos.X, a.Pos.Y, 3f, 1.8f), new Vector2(1.5f, 0.9f), deg(a.Heading), Teams.Col(128, 88, 52));
                    Raylib.DrawRectangleV(a.Pos - a.Heading * 1.8f - new Vector2(0.4f, 0.4f), new(0.8f, 0.8f), Teams.Col(236, 228, 214));
                    Raylib.DrawRectangleV(a.Pos + a.Heading * 1.8f - new Vector2(0.5f, 0.5f), new(1f, 1f), Teams.Col(96, 64, 38));
                    break;
                case Kind.Rabbit:
                    Raylib.DrawRectangleV(a.Pos - new Vector2(0.7f, 0.7f), new(1.4f, 1.4f), Teams.Col(152, 138, 118));
                    if (a.Fleeing) Raylib.DrawRectangleV(a.Pos - a.Heading * 0.9f - new Vector2(0.3f, 0.3f), new(0.6f, 0.6f), Teams.Col(240, 240, 236));
                    break;
                default:
                    Raylib.DrawRectanglePro(new Rectangle(a.Pos.X, a.Pos.Y, 2.6f, 2f), new Vector2(1.3f, 1f), deg(a.Heading), Teams.Col(234, 232, 222));
                    Raylib.DrawRectangleV(a.Pos + a.Heading * 1.6f - new Vector2(0.5f, 0.5f), new(1f, 1f), Teams.Col(40, 36, 34));
                    break;
            }
        }

        // boats: a little hull, a sail in one of a few colours, and a wake
        foreach (var b in boats)
        {
            if (!Show(b.Pos)) continue;
            Vector2 side = new(-b.Dir.Y, b.Dir.X);
            float d = deg(b.Dir);
            Raylib.DrawLineEx(b.Pos - b.Dir * 4f, b.Pos - b.Dir * 10f, 1.2f, Teams.Col(220, 232, 238, 90));    // wake
            Raylib.DrawRectanglePro(new Rectangle(b.Pos.X + 1, b.Pos.Y + 1, 7f, 3f), new Vector2(3.5f, 1.5f), d, Teams.Col(0, 0, 0, 60));
            Raylib.DrawRectanglePro(new Rectangle(b.Pos.X, b.Pos.Y, 7f, 3f), new Vector2(3.5f, 1.5f), d, Teams.Col(96, 64, 38));
            Raylib.DrawRectanglePro(new Rectangle(b.Pos.X, b.Pos.Y, 5f, 1.4f), new Vector2(2.5f, 0.7f), d, Teams.Col(136, 96, 58));
            Color sail = b.Sail switch { 0 => Teams.Col(236, 230, 214), 1 => Teams.Col(196, 80, 64), _ => Teams.Col(214, 196, 120) };
            Vector2 mast = b.Pos + b.Dir * 0.5f;
            Raylib.DrawRectangleV(mast + side * 0.2f - new Vector2(1.6f, 4.6f), new(3.2f, 3.6f), sail);              // sail
            Raylib.DrawRectangleV(mast - new Vector2(0.3f, 5f), new(0.6f, 5f), Teams.Col(70, 48, 28));               // mast
        }

        // birds: just their shadows passing over the land, in a V
        foreach (var fl in flocks)
        {
            Vector2 dir = Vector2.Normalize(fl.Vel), side = new(-dir.Y, dir.X);
            for (int i = 0; i < fl.Count; i++)
            {
                int rank = (i + 1) / 2;
                float sideSign = i % 2 == 0 ? 1 : -1;
                Vector2 p = fl.Pos - dir * rank * 7f + side * sideSign * rank * 5f;
                if (!Show(p)) continue;
                Color shadow = Teams.Col(20, 22, 18, 70);
                Raylib.DrawLineEx(p, p - dir * 1.8f + side * 2f, 1f, shadow);
                Raylib.DrawLineEx(p, p - dir * 1.8f - side * 2f, 1f, shadow);
            }
        }    }
}
