# Pixel Legion

A medieval war of pixels. Hold your keep, build an army, raid caravans and take the realm from three rival houses.

## Run

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```
dotnet run -c Release
```

(`dotnet run` works too, but the Release build runs noticeably faster with very large armies.)

## Controls

| Input | Action |
|---|---|
| Left click | Select the whole blob of pixels under the cursor (Shift adds) |
| Left drag | Box select (Shift adds) |
| Ctrl + A | Select all your pixels |
| 1 / 2 / 3 | Narrow the selection to its infantry / archers / cavalry (everything of that type if nothing is selected). They become their own group, leaving any battle line without disturbing it |
| Right click | Move the whole selection there (as a blob, archers at the back) |
| Right drag | Draw a battle line; infantry and cavalry in front, archers in the ranks behind |
| Ctrl + right click / drag | Same, but only the slider's share moves (the pixels nearest the target); that squad becomes the selection |
| Shift + right click | Move the selection keeping its current formation |
| Shift + right drag | Move it and turn it to face the drag direction |
| Ctrl + mouse wheel | Change the split slider (5% to 100%) |
| Z / X / C | Recruit infantry / archer / cavalry into the selected group (or the main blob if nothing is selected); hold to keep recruiting |
| R | Regroup: every pixel returns to the central (largest) blob |
| F | Jump camera to your main blob |
| WASD / arrow keys / middle-mouse drag | Pan |
| Mouse wheel | Zoom; click or drag the minimap to jump |

While your troops march, their destination shows as faint silhouettes on the ground with a dashed line along the route they will take. Hover over a settlement to see its type, income and defence.

## Starting out

There is no neutral land. You begin with one keep, a small garrison and a warband of 22; every other settlement belongs to one of the three rival houses, each holding a territory around its capital. Gold comes only from the settlements you hold and from caravans, so you grow by taking land and trade from your rivals.

## Units

| Unit | Shape | Cost | HP | Notes |
|---|---|---|---|---|
| Infantry | square | 5g | 10 | Solid melee all-rounder |
| Archer | circle | 7g | 6 | Shoots arrows at range (~50), takes 25% extra damage, backs away from melee; high ground adds a lot of range |
| Cavalry | triangle | 12g | 14 | 2x speed on open ground, same as infantry in forest/crags; charging hits 1.5x harder |

- **Leaders:** every group of three or more has a leader wearing a knight's great helm: its most veteran pixel. When groups merge, one of their leaders keeps the hat (the more veteran one, or chosen by lot if they are level).
- **Big blobs march slowly.** Each doubling of a group's size costs some marching speed (down to under half speed for a vast host), so small detachments are quicker.
- Your archers' range is shaded around each band: a capsule along a battle line, a circle around a blob.

## Settlements

| Type | Gold/s | Garrison | Notes |
|---|---|---|---|
| Watchtower | 1 | 3 | Tiny tower on a rise; good lookout |
| Village | 2 | 3 | A handful of cottages and fields; no defences |
| Town | 4 | 6 | Church, market, palisade |
| City | 9 | 16 | Sprawling; its inner core is ringed by stone walls. Usually by the river |
| Castle | 2 | 9 | Motte and bailey with stone walls |
| Mountain fortress | 3 | 22 | Massive, high in the mountains, with thick walls |

There are lots of villages and watchtowers, a few towns and castles, and only a couple of cities and fortresses. Hover over one to see its income, defences and wall state.

## Walls and sieges

- Castles, fortresses and the inner core of cities are ringed by stone walls, with a **gatehouse wherever a road meets the wall**. Gates and breaches are the only ways in or out: the owner's troops walk round to a gate; enemies must batter down a section or a gate (gates are tougher) and then pour through the breach. Archers barely scratch stone.
- Idle archers inside their own walls take up posts along the wall-walk, all the way round.
- Nobody can fight through an intact wall or gate: only arrows fly over it. When an enemy comes at the walls, the defending infantry and cavalry gather just inside the section under attack and hold there until a section falls; then they rush to meet the attackers at the breach.
- **Archers on their own walls are deadly:** +80% damage and extra reach. **Arrows shot at troops sheltering behind intact walls do only 30%.**
- Breached sections show as rubble. Walls slowly mend when no enemy is about.

## The map

- **Terrain** has broad stretches of open ground with little change in height, and long, high mountain ridges. Low, wet ground turns to **swamp**, which is slow to wade through.
- **Rivers:** most maps have a river running from the mountains to a lake or the map edge, with waterfalls on its steep stretches. Rivers can only be crossed at **bridges**; there is always at least one.
- **Water can't be crossed.** Orders path around lakes and rivers automatically. Each order plans one route for the whole group, and the pixels march in formation around it, keeping to the road where there is one.
- **Roads:** dirt tracks link villages and minor places (1.3x speed); grey paved roads lead to towns, cities and fortresses (1.6x). Roadside taverns stand along the longer roads.
- **Footprints:** every pixel presses its shape into the ground as it walks. Busy routes turn dark, and all tracks slowly fade back to the bare map.
- **Countryside:** patchwork farmland with hedgerows around villages and towns, windmills, water mills on the river, streams running down from the hills, and boats sailing on the lakes.
- **Wildlife:** deer, rabbits and sheep graze and bolt from approaching troops; fish swim (and jump) in the water; the shadows of birds pass over in V formation and veer away from armies.

## Trade caravans

Ox-wagons travel between settlements, mostly along the roads. When one arrives, the owners of **both** the settlement it left and the one it reached are paid. Anyone who is not one of the two trading partners can attack a caravan; destroying it takes its cargo. Caravans are a good target for a fast cavalry band.

## Fog of war

- You only see what your pixels and settlements can see (about 300 units on flat ground).
- **Hills block sight**, and so do forests more than a couple of cells deep; pixels inside a forest can only be spotted up close.
- **High ground matters both ways:** standing on a hill lets you see further, and anything on raised ground is easier to spot.
- **Distant raised ground** stays visible as a silhouette: armies there show only as a blur of their house colour.
- **Places you have been** stay visible as remembered terrain (dimmed), but you can't see who is there now. Settlement banners show the owner as last seen.
- Never-visited land is dark.

## Rival houses

| House | Personality |
|---|---|
| Crimson Horde | One huge blob: takes whatever it can beat and hunts weaker armies |
| Ember Raiders | Guerrilla warbands, cavalry-heavy: raid weakly held settlements, ambush caravans, pick off small groups, scatter from anything stronger |
| Violet Wardens | Defensive: garrisons every holding, rushes to defend them, and only expands with troops it can spare; archer-heavy |

The houses start with far more land than you, so they earn less from each holding and their armies are capped by how much land they hold. A house at its cap grows restless and takes on harder fights.

## Rules

- To capture a settlement, clear its defenders and then stand in its circle with no one else there. It first drains to neutral, then turns to you.
- Pixels fight anything hostile within range. A pixel that is mid-move ignores enemies until it arrives, which lets you retreat.
- Wounded pixels darken and show a health bar when zoomed in. They heal fast inside a friendly settlement and slowly after resting.
- **High ground:** the higher pixel deals up to 2.5x damage and reaches further; the lower one can drop to 0.5x. Uphill is slow, forests slow you a little.
- **Battle lines:** pixels in a line hold their place instead of chasing, and close ranks when someone falls. A pixel with a line-mate beside it takes 20% less damage, or 35% with neighbours on both sides.
- **Veterancy:** kills promote a pixel through four ranks: Blooded (1 kill), Veteran (3), Elite (6), Champion (10). Each rank adds +12% damage and +10% max HP, and the pixel turns a darker shade.

## Code layout

- `src/Entities.cs` - tuning constants, unit stats, personalities, Unit/Formation/Arrow/Caravan/City/Squad, spatial grid
- `src/Terrain.cs` - heightmap, terrain-map texture, navigation grid, flow-field pathfinding, roads
- `src/Fog.cs` - fog of war
- `src/Tracks.cs` - footprints
- `src/Wildlife.cs` - deer, rabbits, sheep, fish and birds
- `src/CastleArt.cs` - procedural pixel-art castles and towns
- `src/Game.cs` - world setup, roads, simulation, combat, arrows, squads, caravans, settlements
- `src/Game.Ai.cs` - the three AI personalities
- `src/Game.Orders.cs` - moves, recruiting, unit-type split, battle lines, shift formation moves
- `src/Game.Input.cs` - camera, selection, order input, hotkeys
- `src/Game.Draw.cs` - rendering, roads, routes, caravans, archer ranges, HUD, tooltips, minimap
