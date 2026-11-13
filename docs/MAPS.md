# UltraPuck maps

UltraPuck plays its own map files (`.upmap`) as custom arenas, with smooth curved walls, round holes and any
shapes, and sends them to everyone in an online match automatically.

## Where maps live

- **`UltraPuck Maps`** in the game folder: every `.upmap` here shows up in the arena list under **Custom**, above the
  game's own Arena Creator arenas. The list is read when the arena menu opens.
- **`UltraPuck Maps/Downloaded`**: maps you got from a host in an online match are saved here (they aren't listed;
  move one up a folder to keep playing it).
- **`UltraPuck Maps/From Arena Creator`**: copies of the maps you made in the game's Arena Creator, refreshed each
  time the arena menu opens, so the map maker can open them.
- **`UltraPuck Maps/Testing`**: the map maker's Test in game puts the map being tested here (not listed).

Arena Creator maps still work as before, and now online too: UltraPuck builds them with the same code as `.upmap`
files.

## UltraPuck Map Maker

A Windows app for making maps, installed with UltraPuck in the game folder as
`UltraPuck Map Maker/UltraPuck Map Maker.exe` (source in `src/MapMaker`). It uses the game's own geometry code, so
the map looks the same in the app as in the game.

- **Drawing.** Floor (F), Wall (W), Hole (H) and Remove wall (E). Pick a colour and a shape:
  - Tile (T, the default): click or drag to paint single tiles, like the game's Arena Creator. Pick a full square or
    a corner triangle on the second toolbar row (Q turns the triangle). Shift+drag fills an area of whole tiles;
    Ctrl+drag fills an area with the opposite: floor and hole swap, so do wall and remove wall.
  - Rectangle (R): drag corner to corner, with optional rounded corners.
  - Circle (C): drag from the centre out.
  - Arc (A): drag from one end to the other, move the mouse to bend it, click. Its width is in the toolbar.
  - Polygon (P): click each corner, then the first corner again (or Space). Backspace takes back a corner.
  - Hold Ctrl with any shape to draw the opposite (a hole instead of floor, and so on).

  Snapping is to half a tile by default; hold Alt to place freely.
- **Objects.** Click to place goals, spawns, boosts, bumpers and spikes. A selected boost's Power sets how hard it
  launches you (100% is the game's normal pad).
- **Select (V).** Click to select, drag to move, arrow keys nudge, Del deletes and Ctrl+D copies. The Properties panel
  edits exact numbers. Click empty space to get the map's name, tagline, players, difficulty and background.
- **Handles.** A selected shape has handles to resize it. The round handle rounds a rectangle's corners or sets an
  arc's width; an arc's middle handle bends it and its end handles move the ends. On a polygon, drag the dot in the
  middle of a side to add a corner there, and double-click a corner to remove it.
- **Paint order.** The list on the right is the paint order; Earlier / Later (`[` / `]`) change it.
- **Mirror.** Left onto right, right onto left, bottom onto top, top onto bottom, or the bottom-left quarter onto all
  four. Red and blue goals and spawns swap across the line between the teams.
- **Controls (F1).** Help > Controls, or the ? Controls button, opens a list of every control, grouped by what you're
  doing.
- **Undo and checks.** Ctrl+Z / Ctrl+Y undo and redo. Before saving, the app warns about a missing goal or spawn,
  objects off the floor and the like.
- **Files.** Save goes to `UltraPuck Maps`. File > Open an Arena Creator map imports the game's old maps.
- **Test in game (F5).** Plays the map in ClusterPuck 99 straight away, without saving it first: it starts the game
  (skipping the intro), or loads the map in the game that's already running, from the menus or a match. It's an
  endless practice match on the map; pause to leave. Edit and press F5 again to reload it. The arrow next to the
  button picks the controls (a controller if one is plugged in, keyboard, or controller) and bots (none, 1 against 1,
  2 against 2 or 3 against 3). It doesn't work during an online game. The game starts through Steam, so Steam Input
  works (PlayStation and other pads), and plays with one controller: the last one you pressed a button on, else an
  Xbox-style one (Steam's copy of a PlayStation pad), else the first.

  How it works: the map maker writes the map and `Testing/request.txt` (`map <path>`, `controls auto|keyboard|controller`,
  `bots <n>`); UltraPuck checks for that file twice a second, deletes it and starts the match.

## Online

When the host starts a match on a custom arena, the map goes to every player before the arena loads (a few KB) and
the match starts once everyone has built it, like any other arena. Players don't need the map beforehand.

## Bots

Custom arenas get a grid of pathfinding points, so bots can play on them. The game's "no AI on custom arenas"
warning is gone and bots are no longer removed.

## The file format

Plain text, one item per line. `#` starts a comment line. Units are Arena Creator tiles (a player is a bit under
one tile wide). The origin is the middle of the map, x goes right, y goes up. Angles are in degrees,
counter-clockwise from the right.

```
UltraPuck map 1
name Curve Rink
tagline Round walls, two holes and a centre circle
background 1
players 2 6
difficulty 2
floor 0 rect -14 -8 14 8 3
floor 8 circle 0 0 2.5
wall 9 arc -11 0 3 95 265 1
hole circle 0 5.5 1.2
wall 4 circle -5.5 0 0.8
goal 1 -11.5 0
goal 2 11.5 0
spawn 1 -7 2.5
spawn 2 7 2.5
boost -3 3
bumper 3 3
spike 0 -3
```

### Header

| Line | Meaning |
| --- | --- |
| `UltraPuck map 1` | Required first line (format version 1). |
| `name <text>` | Name in the arena list. |
| `tagline <text>` | Text under the preview. |
| `background <n>` | Background gradient, as in the Arena Creator (0 and up). |
| `players <min> <max>` | Recommended players. |
| `difficulty <n>` | Stars shown in the arena list (0-3, more says "Good luck"). |

### Shapes

Shapes are applied in file order and later ones paint over earlier ones.

| Line | Meaning |
| --- | --- |
| `floor <colour> <shape>` | Floor of a colour. Removes any wall there. |
| `wall <colour> <shape>` | Wall of a colour, with floor under it. |
| `hole <shape>` | Removes floor and walls (falling hazard). |
| `unwall <shape>` | Removes walls and keeps the floor. |

Colours are the Arena Creator's 12: 0 white, 1 light grey, 2 dark grey, 3 black, 4 red, 5 orange, 6 yellow, 7 green,
8 cyan, 9 blue, 10 purple, 11 brown. Walls are drawn a shade darker.

| Shape | Numbers |
| --- | --- |
| `rect x0 y0 x1 y1 [r]` | A rectangle between two corners; `r` rounds its corners. |
| `circle x y r` | A circle. |
| `poly x1 y1 x2 y2 x3 y3 ...` | A polygon, 3 or more corners. |
| `arc x y r start end thickness` | A band `thickness` wide along a circle of radius `r`, counter-clockwise from angle `start` to `end`, with round ends. Use 0 360 for a full ring. |
| `tiles x y s x y s ...` | Single tiles: each `x y s` is the 1 x 1 square from (x, y) to (x + 1, y + 1). `s` is 0 for the whole square, or the corner triangle with the right angle at 1 bottom left, 2 bottom right, 3 top right, 4 top left. |

### Objects

| Line | Meaning |
| --- | --- |
| `goal <team> x y` | A goal; team 1 (red by default) defends the team 1 goal. |
| `spawn <team> x y` | Where that team's players start. Without one, a team starts in the middle. |
| `boost x y [power p]` | Speed boost pad; `power` is how hard it launches you, in % of the game's own pad (default 100). |
| `bumper x y` | Bumper. |
| `spike x y` | Spikes. |

Objects don't make floor, so put floor under them.

### Keeping one side (mirroring)

Any shape line can end with one or more `keep <side> <value>`, which limits the shape to one side of a line.
`keep left 0` keeps only the part where x ≤ 0; the other sides are `right` (x ≥ value), `below` (y ≤ value) and
`above` (y ≥ value). The map maker's Mirror writes these: each original keeps its own half and its mirrored copy keeps
the other, so curves stay exact.

```
floor 0 rect -13 -8 1 8 3 keep left 0
floor 0 rect -1 -8 13 8 3 keep right 0
```

## How it's built

The game builds each shape as a signed distance field on a 1/8-tile grid. Marching squares traces the outlines, with
crossings refined on the exact shapes and sharp corners rebuilt, so straight edges stay straight and corners stay
square. From that it makes:

- the mesh, in the Arena Creator's materials;
- one solid polygon collider for the walls (what players and the puck bounce off; anything pushed into a wall is
  pushed back out the nearest side);
- an edge collider for the floor's border (the forcefield that keeps the puck in);
- box colliders filling the ground and a 2-tile forcefield ring.

The code is in `src/CP99Online/Maps`. `MapData.cs` and `MapGeometry.cs` are plain C#, shared with the map maker.
