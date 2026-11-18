using System.Numerics;
using Raylib_cs;

namespace PixelLegion;

static class Config
{
    public const float WorldW = 6000f;
    public const float WorldH = 4000f;

    public const float UnitSize = 3f;
    public const float SeparationRadius = 4.4f;
    public const float SeparationStrength = 30f;
    public const float FormationSpread = 2.6f;   // sunflower-spiral packing factor
    public const float Speed = 30f;              // base walking speed (infantry/archers on flat open ground)
    public const float BlobSlowdown = 0.075f;    // marching speed lost per doubling of the blob's size
    public const float MinBlobSpeed = 0.45f;     // ...but a huge host never drops below this fraction
    public const float RegenInCity = 1.0f;        // hp/s inside a friendly castle
    public const float RegenResting = 0.2f;       // hp/s after a few seconds out of combat
    public const float CaptureRate = 0.07f;       // base capture progress per second
    public const float AiThinkInterval = 1.6f;    // seconds between AI decisions (plus some jitter)
    public const float RestDelay = 4f;
    public const float CastleDefense = 0.6f;      // damage taken while inside a castle your side owns
    public const float TownDefense = 0.85f;       // towns: weaker walls...
    public const int TownIncomePerSize = 2;       // ...but twice the gold of a castle
    public const float TownShare = 0.4f;          // fraction of settlements that are towns
    public const float PavedBonus = 1.6f;         // speed multiplier on paved roads
    public const float DirtBonus = 1.3f;          // ...and on dirt tracks
    public const float MaxRoadLength = 1400f;     // settlements further apart than this are not linked

    // high ground: damage multiplier = 1 + heightDiff * HighGroundDamage (clamped), plus extra reach
    public const float HighGroundDamage = 35f;
    public const float HighGroundRange = 240f;
    public const float MaxHighGroundReach = 4f;
    public const float MaxArcherHighGroundReach = 20f;
    public const float UphillSlow = 280f;          // speed loss per unit of slope
    public const float LinkDistance = 10f;       // max gap between pixels in the same "blob"

    // formations
    public const float LineSpacing = 5f;         // gap between pixels in a battle line
    public const float LineHoldRadius = 12f;     // line troops only break ranks for enemies this close to their slot
    public const float BraceOneSide = 0.8f;      // damage taken with one shoulder-to-shoulder neighbour
    public const float BraceBothSides = 0.65f;   // ...with neighbours on both sides

    // cavalry
    public const float CavalryOpenGround = 2.0f; // speed multiplier on open ground (forest/water/crags: same as infantry)
    public const float ChargeSpeed = 48f;        // moving faster than this when striking = a charge
    public const float ChargeBonus = 1.5f;

    // archers
    public const float ArrowSpeed = 170f;

    // fog of war
    public const int FogCell = 32;               // world units per fog cell
    public const float SightRange = 300f;        // clear sight on flat ground
    public const float HillSightBonus = 1.3f;    // extra sight per unit of height above the plains
    public const float SilhouetteFactor = 2.2f;  // raised ground stays visible (as a colour blur) out to this x sight
    public const int ForestSightCells = 2;       // this many forest cells in a row block sight

    // veterancy
    public const float VetDamagePerRank = 0.12f;
    public const float VetHpPerRank = 0.10f;

    // settlements: lots of villages and watchtowers, a few towns and castles, a couple of cities and fortresses
    public static readonly (SettlementType type, int count)[] SettlementMix =
    {
        (SettlementType.Fortress, 2), (SettlementType.City, 2), (SettlementType.Castle, 5),
        (SettlementType.Town, 7), (SettlementType.Village, 14), (SettlementType.Watchtower, 8),
    };

    // walls
    public const float AttackerArrowsVsWalls = 0.3f;  // arrows shot at troops sheltering behind intact walls
    public const float DefenderArrowBonus = 1.8f;     // arrows shot from behind your own walls
    public const float DefenderArrowReach = 15f;
    public const int AiCount = 3;
    public const int StartGold = 40;
    public const int AiStartGold = 30;
    public const float AiIncomeFactor = 0.45f;   // rival houses start with lots of land, so they earn less from each holding
    public const int AiArmyBase = 40, AiArmyPerHolding = 12;  // cap on each rival's army size

    // trade caravans
    public const float CaravanSpeed = 18f;
    public const float CaravanInterval = 3.5f;   // seconds between new caravans setting out (map-wide)
    public const int MaxCaravans = 18;
    public const float CaravanHp = 20f;
    public const float CaravanMinTrip = 400f, CaravanMaxTrip = 2600f;

    // footprints
    public const int TrackChunk = 256;           // world units (= texels) per track chunk
    public const float TrackStep = 3f;           // a pixel leaves a print every this many units walked
    public const int TrackInk = 22;              // darkness added per print (overlapping prints add up)
    public const float TrackFadeInterval = 0.6f; // every this many seconds, all prints fade by one step
}

enum UnitKind { Infantry, Archer, Cavalry }

/// <summary>Per-type stats. DamageTaken multiplies incoming damage (archers are squishy).</summary>
readonly record struct UnitStats(
    string Name, float Hp, float Damage, float Range, float Cooldown, float Aggro, float DamageTaken, int Cost);

static class Stats
{
    public static readonly UnitStats Infantry = new("Infantry", 10f, 2.0f, 6f, 0.9f, 40f, 1.00f, 5);
    public static readonly UnitStats Archer = new("Archer", 6f, 1.6f, 50f, 2.0f, 65f, 1.25f, 7);
    public static readonly UnitStats Cavalry = new("Cavalry", 14f, 2.6f, 6f, 1.05f, 45f, 0.90f, 12);

    public static UnitStats Of(UnitKind k) => k switch
    {
        UnitKind.Archer => Archer,
        UnitKind.Cavalry => Cavalry,
        _ => Infantry,
    };

    /// <summary>Kills needed for veterancy ranks 1-4.</summary>
    public static readonly int[] RankKills = { 1, 3, 6, 10 };
    public static readonly string[] RankNames = { "Recruit", "Blooded", "Veteran", "Elite", "Champion" };
}

enum Personality { Player, Horde, Raider, Warden }

static class Teams
{
    /// <summary>Horde: one big blob that attacks. Raider: small warbands that raid and run.
    /// Warden: holds and garrisons its settlements, expands carefully.</summary>
    public static readonly Personality[] Personalities =
        { Personality.Player, Personality.Horde, Personality.Raider, Personality.Warden, Personality.Player };

    public const int Player = 0;
    public const int Neutral = 4;
    public const int Count = 5;

    public static readonly Color[] Colors =
    {
        Col(70, 175, 255),   // player - azure
        Col(230, 45, 45),    // crimson
        Col(255, 135, 20),   // ember
        Col(185, 80, 245),   // violet
        Col(128, 122, 112),  // neutral garrisons - iron grey
    };

    public static readonly string[] Names = { "House Azure (you)", "Crimson Horde", "Ember Raiders", "Violet Wardens", "Free Garrison" };

    public static Color Col(int r, int g, int b, int a = 255) => new((byte)r, (byte)g, (byte)b, (byte)a);

    public static Color WithAlpha(Color c, int a) => Col(c.R, c.G, c.B, a);

    public static Color Lerp(Color a, Color b, float t) => Col(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t),
        (int)(a.B + (b.B - a.B) * t), (int)(a.A + (b.A - a.A) * t));

    public static Color Shade(Color c, float f) => Col(
        Math.Clamp((int)(c.R * f), 0, 255), Math.Clamp((int)(c.G * f), 0, 255), Math.Clamp((int)(c.B * f), 0, 255), c.A);
}

sealed class Unit
{
    public UnitKind Kind;
    public Vector2 Pos, Vel, Target;
    public Vector2 Heading = new(0, -1);   // which way it last moved (cavalry triangles point this way)
    public int Team;
    public float Hp;
    public float AttackCd;
    public float SinceHurt = 99f;  // seconds since last damaged
    public float HitFlash, RankFlash;
    public int Kills;
    public float Brace = 1f;       // incoming damage multiplier from shoulder-to-shoulder neighbours
    public bool Selected, Dead;
    public bool Forced;            // on a move order: don't chase enemies until arrival
    public float ForcedTimeout;
    public City? Home;             // neutral garrisons stay leashed to their city
    public Formation? Form;        // battle line this pixel belongs to
    public bool IsLeader;          // wears the hat: the most veteran pixel of its group
    public City? BlockedBy;        // the enemy wall it was stopped by last step (and will hack at)
    public int BlockedSeg;
    public Squad? Squad;           // the group it is marching with (one shared route per order)
    public Unit? Enemy;            // cached nearest enemy (re-searched a few times a second)
    public float SearchTimer;
    public int Band = -1;          // AI: raider warband id / warden strike force (1)
    public City? Post;             // AI: warden garrison posting
    public int Stamp;              // scratch marker for cluster flood fills
    public Vector2 LastPrint;      // where it last left a footprint

    public UnitStats S => Stats.Of(Kind);

    public int Rank => Kills >= Stats.RankKills[3] ? 4 : Kills >= Stats.RankKills[2] ? 3
                     : Kills >= Stats.RankKills[1] ? 2 : Kills >= Stats.RankKills[0] ? 1 : 0;

    public float MaxHp => S.Hp * (1f + Config.VetHpPerRank * Rank);

    public static Unit Make(UnitKind kind, int team, Vector2 pos, Vector2 target)
    {
        var u = new Unit { Kind = kind, Team = team, Pos = pos, Target = target, LastPrint = pos };
        u.Hp = u.MaxHp;
        return u;
    }
}

/// <summary>A battle line. Slots run sideways along the front; extra pixels stack in ranks behind.</summary>
sealed class Formation
{
    public Vector2 Center;
    public Vector2 Facing;          // unit vector pointing at the enemy
    public int Cols;               // width of the front in pixels
    public readonly List<Unit> Members = new();
    public bool Dirty;             // membership changed - re-assign slots

    public Vector2 Side => new(-Facing.Y, Facing.X);

    public Vector2 Slot(int cols, int col, int rank) =>
        Center + Side * ((col - (cols - 1) / 2f) * Config.LineSpacing) - Facing * (rank * Config.LineSpacing);

    /// <summary>Number of pixels in column c when n pixels are spread evenly over the columns.</summary>
    public static int ColumnSize(int n, int cols, int c) => n / cols + (c < n % cols ? 1 : 0);

    /// <summary>Ranks used by n pixels over cols columns.</summary>
    public static int Ranks(int n, int cols) => n == 0 ? 0 : (n + cols - 1) / cols;

    /// <summary>Slot positions with melee ranks in front and archer ranks behind (for previews).</summary>
    public IEnumerable<Vector2> Slots(int melee, int archers)
    {
        int mc = Math.Clamp(Cols, 1, Math.Max(1, melee));
        foreach (var s in Block(melee, mc, 0)) yield return s;
        foreach (var s in Block(archers, Math.Clamp(Cols, 1, Math.Max(1, archers)), Ranks(melee, mc))) yield return s;
    }

    IEnumerable<Vector2> Block(int n, int cols, int firstRank)
    {
        if (n <= 0) yield break;
        for (int c = 0; c < cols; c++)
            for (int r = 0; r < ColumnSize(n, cols, c); r++)
                yield return Slot(cols, c, firstRank + r);
    }
}

sealed class Arrow
{
    public Vector2 Pos, Dest, Dir;
    public float Damage;
    public int Team;
    public Unit? Shooter;
    public bool Done;
}

enum SettlementType { Watchtower, Village, Town, City, Castle, Fortress }

/// <summary>A ring of wall segments around a settlement (or a city's inner core). Intact segments stop enemies;
/// they must hack through to get in. Segments slowly mend when left alone.</summary>
sealed class Walls
{
    public float Radius;
    public float MaxHp;
    public float[] Hp = Array.Empty<float>();
    public bool[] Down = Array.Empty<bool>();
    public bool[] Gate = Array.Empty<bool>();     // gatehouses: where roads pass through; only the owner may use them
    public float[] LastHit = Array.Empty<float>();
    public int N => Hp.Length;
    public float SegMax(int s) => Gate[s] ? MaxHp * 1.5f : MaxHp; // gatehouses are the strongest part

    /// <summary>Can this side get through section s? Through a breach, anyone; through a gate, only the owner.</summary>
    public bool Passable(int s, bool owner) => Down[s] || (Gate[s] && owner);

    public float SegmentAngle(int s) => -MathF.PI + (s + 0.5f) * MathF.Tau / N;

    public void MakeGate(int s)
    {
        Gate[s] = true;
        Hp[s] = SegMax(s);
    }

    public Walls(float radius, int segments, float hp)
    {
        Radius = radius;
        MaxHp = hp;
        Hp = Enumerable.Repeat(hp, segments).ToArray();
        Down = new bool[segments];
        Gate = new bool[segments];
        LastHit = Enumerable.Repeat(-99f, segments).ToArray();
    }

    public int SegmentAt(Vector2 center, Vector2 p)
    {
        float a = MathF.Atan2(p.Y - center.Y, p.X - center.X);
        int s = (int)MathF.Floor((a + MathF.PI) / MathF.Tau * N);
        return ((s % N) + N) % N;
    }

    public float IntactFraction => Down.Count(d => !d) / (float)N;
}

sealed class City
{
    public Vector2 Pos;
    public SettlementType Type;
    public int Owner = -1;
    public int CaptureTeam = -1;
    public float Capture;        // 0..1 progress for CaptureTeam
    public float IncomeTimer;
    public string Name = "";
    public CastleSprite Sprite = null!;
    public float Radius;          // capture zone, set from the sprite size
    public int KnownOwner = -1;   // owner as last seen through the fog
    public FlowField? Flow;       // precomputed route here from anywhere on the map
    public Walls? Walls;          // fortresses, castles and the inner core of cities

    /// <summary>Rough scale 1..4, used for capture time and garrison sizes.</summary>
    public int Size => Type switch
    {
        SettlementType.Watchtower or SettlementType.Village => 1,
        SettlementType.Town or SettlementType.Castle => 2,
        _ => 4,
    };

    public bool IsTown => Type is SettlementType.Village or SettlementType.Town or SettlementType.City;

    public int Income => Type switch
    {
        SettlementType.Watchtower => 1,
        SettlementType.Village => 2,
        SettlementType.Town => 4,
        SettlementType.City => 9,
        SettlementType.Castle => 2,
        _ => 3,
    };

    /// <summary>Damage taken by its own troops inside (on top of any walls).</summary>
    public float Defense => Type switch
    {
        SettlementType.Village => 1f,
        SettlementType.Town or SettlementType.City => 0.85f,
        SettlementType.Fortress => 0.6f,
        _ => 0.7f,
    };

    public string Kind => Type == SettlementType.Fortress ? "Mountain fortress" : Type.ToString();
}
/// <summary>A trade wagon travelling between two settlements. When it arrives, the current owners of both ends
/// get a bonus. Enemy troops that catch it destroy it and take part of the cargo.</summary>
sealed class Caravan
{
    public City From = null!, To = null!;
    public int Team;               // owner of the settlement it left from
    public Vector2 Pos, Heading = Vector2.UnitX;
    public FlowField Flow = null!;
    public float Hp = Config.CaravanHp;
    public int Value;              // gold paid to each end on arrival
    public bool Done;
    public Vector2 LastPrint;
}
/// <summary>A group marching to one order. Only the squad follows the route; each pixel just keeps its place
/// relative to the moving anchor, so a huge army costs one path, not thousands, and doesn't funnel into a clump.</summary>
sealed class Squad
{
    public readonly List<Unit> Members = new();
    public Vector2 Anchor, Goal;
    public FlowField Flow = null!;
    public bool Mounted;           // all cavalry: march at cavalry speed
    public bool Done;
    public List<Vector2> Path = new();  // the route the anchor will take (drawn for the player's squads)
    public int PathIndex;
}

/// <summary>Flat spatial hash rebuilt every frame (counting sort, no per-cell lists). Pixels are bucketed by team
/// as well as cell, so "find an enemy" never wades through a crowd of friends.</summary>
sealed class SpatialGrid
{
    public const int AllTeams = (1 << Teams.Count) - 1;

    readonly float cell;
    readonly int cols, rows, cellCount;
    readonly int[] start, cursor;
    Unit[] items = new Unit[4096];

    public SpatialGrid(float w, float h, float cell)
    {
        this.cell = cell;
        cols = (int)MathF.Ceiling(w / cell);
        rows = (int)MathF.Ceiling(h / cell);
        cellCount = cols * rows;
        start = new int[Teams.Count * cellCount + 1];
        cursor = new int[start.Length];
    }

    int Cx(float x) => Math.Clamp((int)(x / cell), 0, cols - 1);
    int Cy(float y) => Math.Clamp((int)(y / cell), 0, rows - 1);
    int Key(Unit u) => u.Team * cellCount + Cy(u.Pos.Y) * cols + Cx(u.Pos.X);

    public static int Except(int team) => AllTeams & ~(1 << team);
    public static int Only(int team) => 1 << team;

    public void Build(List<Unit> units)
    {
        if (items.Length < units.Count) items = new Unit[units.Count * 2];
        Array.Clear(start);
        foreach (var u in units)
            if (!u.Dead) start[Key(u) + 1]++;
        for (int k = 1; k < start.Length; k++) start[k] += start[k - 1];
        Array.Copy(start, cursor, start.Length);
        foreach (var u in units)
            if (!u.Dead) items[cursor[Key(u)]++] = u;
    }

    public void Query(Vector2 p, float r, List<Unit> results, int teamMask = AllTeams)
    {
        results.Clear();
        int x0 = Cx(p.X - r), x1 = Cx(p.X + r), y0 = Cy(p.Y - r), y1 = Cy(p.Y + r);
        float r2 = r * r;
        for (int t = 0; t < Teams.Count; t++)
        {
            if (((teamMask >> t) & 1) == 0) continue;
            int teamBase = t * cellCount;
            for (int y = y0; y <= y1; y++)
            {
                int row = teamBase + y * cols;
                for (int x = x0; x <= x1; x++)
                {
                    int k = row + x;
                    for (int i = start[k], end = start[k + 1]; i < end; i++)
                    {
                        var u = items[i];
                        if (!u.Dead && Vector2.DistanceSquared(u.Pos, p) <= r2) results.Add(u);
                    }
                }
            }
        }
    }
}