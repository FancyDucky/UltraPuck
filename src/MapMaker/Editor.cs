using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using CP99Online.Maps;

namespace UltraPuckMapMaker
{
    internal enum Tool { Select, Floor, Wall, Hole, Unwall, RedGoal, BlueGoal, RedSpawn, BlueSpawn, Boost, Bumper, Spike }

    internal enum ShapeMode { Rectangle, Circle, Arc, Polygon }

    /// <summary>The map being edited: selection, undo / redo (whole-map snapshots) and files.</summary>
    internal sealed class Editor
    {
        public MapData Map = NewMap();
        public string FilePath;
        public bool Dirty;
        /// <summary>Bumped on every change (the canvas rebuilds the geometry when it moves).</summary>
        public int Version;

        public object Selected; // MapShape or MapObject
        public Tool Tool = Tool.Floor;
        public ShapeMode Mode = ShapeMode.Rectangle;
        public int Color; // 0-11
        public float Corner; // corner radius for new rectangles
        public float Thickness = 1f; // width of new arcs
        public float Snap = 0.5f;

        public event Action Changed;          // the map changed
        public event Action SelectionChanged; // what's selected changed
        public event Action ToolChanged;

        private readonly List<string> _undo = new List<string>();
        private readonly List<string> _redo = new List<string>();
        private string _committed;

        public Editor() { _committed = Map.Write(); }

        public static MapData NewMap()
        {
            var m = MapData.Parse(
                "UltraPuck map 1\n" +
                "name New map\ntagline \nbackground 1\nplayers 2 4\ndifficulty 1\n" +
                "floor 0 rect -12 -7 12 7\n" +
                "goal 1 -10.5 0\ngoal 2 10.5 0\n" +
                "spawn 1 -6 2\nspawn 1 -6 -2\nspawn 2 6 2\nspawn 2 6 -2\n");
            return m;
        }

        public bool CanUndo { get { return _undo.Count > 0; } }
        public bool CanRedo { get { return _redo.Count > 0; } }

        public string Title
        {
            get
            {
                var name = FilePath != null ? Path.GetFileNameWithoutExtension(FilePath) : (Map.Name ?? "New map");
                return name + (Dirty ? " *" : string.Empty) + " - UltraPuck Map Maker";
            }
        }

        /// <summary>Records the map as it is now as one undo step (call after changing it).</summary>
        public void Commit()
        {
            var now = Map.Write();
            Version++;
            if (now != _committed)
            {
                _undo.Add(_committed);
                if (_undo.Count > 300) _undo.RemoveAt(0);
                _redo.Clear();
                _committed = now;
                Dirty = true;
            }
            if (Changed != null) Changed();
        }

        /// <summary>Redraw for a change that isn't finished yet (dragging); Commit when it is.</summary>
        public void Touch()
        {
            Version++;
            if (Changed != null) Changed();
        }

        /// <summary>Throws away unfinished changes (e.g. a drag cancelled with Escape).</summary>
        public void Revert()
        {
            Restore(_committed);
        }

        public void Undo()
        {
            if (_undo.Count == 0) return;
            _redo.Add(_committed);
            _committed = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            Dirty = true;
            Restore(_committed);
        }

        public void Redo()
        {
            if (_redo.Count == 0) return;
            _undo.Add(_committed);
            _committed = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            Dirty = true;
            Restore(_committed);
        }

        private void Restore(string text)
        {
            // keep the same item selected if it still exists
            int shape = Selected is MapShape ? Map.Shapes.IndexOf((MapShape)Selected) : -1;
            int obj = Selected is MapObject ? Map.Objects.IndexOf((MapObject)Selected) : -1;
            Map = MapData.Parse(text);
            Selected = shape >= 0 && shape < Map.Shapes.Count ? (object)Map.Shapes[shape]
                : obj >= 0 && obj < Map.Objects.Count ? Map.Objects[obj] : null;
            Version++;
            if (Changed != null) Changed();
            if (SelectionChanged != null) SelectionChanged();
        }

        public void Select(object item)
        {
            if (item == Selected) return;
            Selected = item;
            if (SelectionChanged != null) SelectionChanged();
        }

        public void SetTool(Tool t)
        {
            Tool = t;
            if (ToolChanged != null) ToolChanged();
        }

        public void SetMode(ShapeMode m)
        {
            Mode = m;
            if (ToolChanged != null) ToolChanged();
        }

        public void SetColor(int c)
        {
            Color = c;
            if (ToolChanged != null) ToolChanged();
        }

        // ------------------------------------------------------------------ editing

        public void DeleteSelected()
        {
            if (Selected is MapShape) Map.Shapes.Remove((MapShape)Selected);
            else if (Selected is MapObject) Map.Objects.Remove((MapObject)Selected);
            else return;
            Select(null);
            Commit();
        }

        public void DuplicateSelected()
        {
            if (Selected is MapShape)
            {
                var s = ((MapShape)Selected).Clone();
                Move(s, 1f, -1f);
                Map.Shapes.Add(s);
                Select(s);
            }
            else if (Selected is MapObject)
            {
                var o = ((MapObject)Selected).Clone();
                o.X += 1f; o.Y -= 1f;
                Map.Objects.Add(o);
                Select(o);
            }
            else return;
            Commit();
        }

        /// <summary>Moves a shape earlier (-1) or later (+1) in paint order.</summary>
        public void Reorder(int dir)
        {
            var s = Selected as MapShape;
            if (s == null) return;
            var i = Map.Shapes.IndexOf(s);
            var j = i + dir;
            if (i < 0 || j < 0 || j >= Map.Shapes.Count) return;
            Map.Shapes.RemoveAt(i);
            Map.Shapes.Insert(j, s);
            Commit();
        }

        public static void Move(MapShape s, float dx, float dy)
        {
            var p = s.P;
            switch (s.Kind)
            {
                case ShapeKind.Rect: p[0] += dx; p[1] += dy; p[2] += dx; p[3] += dy; break;
                case ShapeKind.Circle:
                case ShapeKind.Arc: p[0] += dx; p[1] += dy; break;
                default: for (var i = 0; i + 1 < p.Length; i += 2) { p[i] += dx; p[i + 1] += dy; } break;
            }
            for (var i = 0; i < s.Keeps.Count; i++)
            {
                var k = s.Keeps[i];
                k.V += k.Side == KeepSide.Left || k.Side == KeepSide.Right ? dx : dy;
                s.Keeps[i] = k;
            }
        }

        public void Mirror(KeepSide source)
        {
            Map.Mirror(source);
            Select(null);
            Commit();
        }

        // ------------------------------------------------------------------ files

        public void New()
        {
            Map = NewMap();
            FilePath = null;
            ResetHistory();
        }

        public void Open(string path)
        {
            Map = MapData.Parse(File.ReadAllText(path, Encoding.UTF8));
            FilePath = path;
            ResetHistory();
        }

        public void Save(string path)
        {
            File.WriteAllText(path, Map.Write(), new UTF8Encoding(false));
            FilePath = path;
            Dirty = false;
            if (Changed != null) Changed();
        }

        private void ResetHistory()
        {
            _undo.Clear(); _redo.Clear();
            _committed = Map.Write();
            Dirty = false;
            Selected = null;
            Version++;
            if (Changed != null) Changed();
            if (SelectionChanged != null) SelectionChanged();
        }

        /// <summary>Problems worth a warning before saving (the game still loads the map).</summary>
        public List<string> Check(MapGeometry geo)
        {
            var issues = new List<string>();
            var floor = false;
            foreach (var s in Map.Shapes) if (s.Op == MapOp.Floor || s.Op == MapOp.Wall) floor = true;
            if (!floor) issues.Add("The map has no floor.");
            for (var team = 1; team <= 2; team++)
            {
                bool goal = false, spawn = false;
                foreach (var o in Map.Objects)
                {
                    if (o.Team != team) continue;
                    if (o.Kind == ObjKind.Goal) goal = true;
                    if (o.Kind == ObjKind.Spawn) spawn = true;
                }
                var name = team == 1 ? "Red" : "Blue";
                if (!goal) issues.Add(name + " team has no goal.");
                if (!spawn) issues.Add(name + " team has no spawn point (they'll start in the middle).");
            }
            if (geo != null)
            {
                foreach (var o in Map.Objects)
                {
                    if (geo.FloorDistance(o.X, o.Y) > -0.05f) issues.Add(Describe(o) + " at " + o.X + ", " + o.Y + " isn't on the floor.");
                    else if (geo.WallDistance(o.X, o.Y) < 0.3f && o.Kind != ObjKind.Goal) issues.Add(Describe(o) + " at " + o.X + ", " + o.Y + " is in or touching a wall.");
                }
            }
            if (Map.MinPlayers > Map.MaxPlayers) issues.Add("Minimum players is more than maximum players.");
            return issues;
        }

        // ------------------------------------------------------------------ names

        public static readonly string[] ColorNames = { "White", "Light grey", "Dark grey", "Black", "Red", "Orange", "Yellow", "Green", "Cyan", "Blue", "Purple", "Brown" };

        /// <summary>Roughly the game's tile colours.</summary>
        public static readonly Color[] Palette =
        {
            System.Drawing.Color.FromArgb(226, 226, 222), System.Drawing.Color.FromArgb(176, 176, 172), System.Drawing.Color.FromArgb(108, 108, 106),
            System.Drawing.Color.FromArgb(44, 44, 46), System.Drawing.Color.FromArgb(196, 62, 56), System.Drawing.Color.FromArgb(226, 138, 52),
            System.Drawing.Color.FromArgb(228, 200, 64), System.Drawing.Color.FromArgb(82, 166, 80), System.Drawing.Color.FromArgb(70, 164, 170),
            System.Drawing.Color.FromArgb(56, 84, 186), System.Drawing.Color.FromArgb(130, 74, 148), System.Drawing.Color.FromArgb(128, 88, 54),
        };

        public static Color WallColor(int c)
        {
            var p = Palette[c];
            return System.Drawing.Color.FromArgb((int)(p.R * 0.62f), (int)(p.G * 0.62f), (int)(p.B * 0.62f));
        }

        public static readonly Color Red = System.Drawing.Color.FromArgb(222, 64, 64);
        public static readonly Color Blue = System.Drawing.Color.FromArgb(64, 116, 226);

        public static string Describe(MapObject o)
        {
            var team = o.Team == 2 ? "Blue" : "Red";
            switch (o.Kind)
            {
                case ObjKind.Goal: return team + " goal";
                case ObjKind.Spawn: return team + " spawn";
                case ObjKind.Boost: return "Speed boost";
                case ObjKind.Bumper: return "Bumper";
                default: return "Spikes";
            }
        }

        public static string Describe(MapShape s)
        {
            string what;
            switch (s.Op)
            {
                case MapOp.Floor: what = ColorNames[s.Color] + " floor"; break;
                case MapOp.Wall: what = ColorNames[s.Color] + " wall"; break;
                case MapOp.Hole: what = "Hole"; break;
                default: what = "Remove wall"; break;
            }
            var p = s.P;
            string shape;
            switch (s.Kind)
            {
                case ShapeKind.Rect: shape = "rectangle " + N(Math.Abs(p[2] - p[0])) + " x " + N(Math.Abs(p[3] - p[1])) + " at " + N((p[0] + p[2]) / 2) + ", " + N((p[1] + p[3]) / 2); break;
                case ShapeKind.Circle: shape = "circle r " + N(p[2]) + " at " + N(p[0]) + ", " + N(p[1]); break;
                case ShapeKind.Arc: shape = "arc r " + N(p[2]) + " at " + N(p[0]) + ", " + N(p[1]); break;
                default: shape = "polygon, " + p.Length / 2 + " corners"; break;
            }
            return what + ", " + shape + (s.Keeps.Count > 0 ? " (mirrored half)" : string.Empty);
        }

        private static string N(float f) { return MapData.Num(f); }
    }
}
