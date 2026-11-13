using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using CP99Online.Maps;

namespace UltraPuckMapMaker
{
    internal sealed class MainForm : Form
    {
        private readonly Editor _ed = new Editor();
        private readonly MapCanvas _canvas;
        private readonly ListBox _layers = new ListBox();
        private readonly PropertyGrid _props = new PropertyGrid();
        private readonly ToolStripStatusLabel _coords = new ToolStripStatusLabel();
        private readonly ToolStripStatusLabel _hint = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        private readonly Dictionary<Tool, ToolStripButton> _toolButtons = new Dictionary<Tool, ToolStripButton>();
        private readonly Dictionary<ShapeMode, ToolStripButton> _modeButtons = new Dictionary<ShapeMode, ToolStripButton>();
        private readonly Dictionary<TileShape, ToolStripButton> _tileButtons = new Dictionary<TileShape, ToolStripButton>();
        private readonly ToolStripButton[] _swatches = new ToolStripButton[MapData.Colors];
        private ToolStripMenuItem _undoItem, _redoItem;
        private bool _syncing;
        private string _testControls = "auto";
        private int _testBots;

        public MainForm(string open)
        {
            Text = "UltraPuck Map Maker";
            Width = 1280; Height = 800;
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            Font = new Font("Segoe UI", 9f);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            _canvas = new MapCanvas(_ed) { Dock = DockStyle.Fill };
            _canvas.Status += s => _coords.Text = s;

            // ---- right side: layers + properties
            var right = new SplitContainer { Dock = DockStyle.Right, Width = 340, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.None };
            Load += (s, e) => right.SplitterDistance = (int)(right.Height * 0.45f);
            var layersBox = new GroupBox { Text = "Shapes (painted top to bottom) and objects", Dock = DockStyle.Fill, Padding = new Padding(6) };
            _layers.Dock = DockStyle.Fill;
            _layers.IntegralHeight = false;
            _layers.HorizontalScrollbar = true;
            _layers.SelectedIndexChanged += (s, e) =>
            {
                if (_syncing) return;
                var item = _layers.SelectedItem as LayerItem;
                _ed.Select(item != null ? item.Ref : null);
            };
            var layerButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 34, FlowDirection = FlowDirection.LeftToRight };
            layerButtons.Controls.Add(SmallButton("Earlier", "Paint the selected shape earlier, so later shapes cover it ( [ )", () => _ed.Reorder(-1)));
            layerButtons.Controls.Add(SmallButton("Later", "Paint the selected shape later, over earlier ones ( ] )", () => _ed.Reorder(1)));
            layerButtons.Controls.Add(SmallButton("Duplicate", "Copy the selection (Ctrl+D)", _ed.DuplicateSelected));
            layerButtons.Controls.Add(SmallButton("Delete", "Delete the selection (Del)", _ed.DeleteSelected));
            layersBox.Controls.Add(_layers);
            layersBox.Controls.Add(layerButtons);
            right.Panel1.Controls.Add(layersBox);
            var propsBox = new GroupBox { Text = "Properties", Dock = DockStyle.Fill, Padding = new Padding(6) };
            _props.Dock = DockStyle.Fill;
            _props.ToolbarVisible = false;
            _props.HelpVisible = true;
            _props.PropertySort = PropertySort.Categorized;
            _props.PropertyValueChanged += (s, e) => _ed.Commit();
            propsBox.Controls.Add(_props);
            right.Panel2.Controls.Add(propsBox);

            var status = new StatusStrip();
            _coords.Width = 160; _coords.AutoSize = false; _coords.TextAlign = ContentAlignment.MiddleLeft;
            status.Items.Add(_coords);
            status.Items.Add(_hint);

            // order matters for docking: fill first, then the edges
            Controls.Add(_canvas);
            Controls.Add(right);
            Controls.Add(ObjectStrip());
            Controls.Add(ToolStripMain());
            Controls.Add(BuildMenu());
            Controls.Add(status);
            MainMenuStrip = (MenuStrip)Controls.OfType<MenuStrip>().First();

            _ed.Changed += OnChanged;
            _ed.SelectionChanged += OnSelection;
            _ed.ToolChanged += OnTool;
            OnTool(); OnChanged(); OnSelection();

            Shown += (s, e) =>
            {
                if (open != null) Open(open);
                _canvas.ZoomToFit();
                _canvas.Focus();
                if (_shot != null) RunShot();
            };
        }

        // ------------------------------------------------------------------ self-test screenshots
        // "UltraPuck Map Maker.exe" --shot out.png [map] [mirror left|right|below|above|both] [select n] [save path]
        // opens off-screen without taking focus, does the steps, saves a picture of the window and exits.

        private string _shot;
        private string[] _shotSteps;

        public MainForm(string open, string shot, string[] steps) : this(open)
        {
            _shot = shot;
            _shotSteps = steps;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-20000, -20000);
            ShowInTaskbar = false;
        }

        protected override bool ShowWithoutActivation { get { return _shot != null; } }

        private void RunShot()
        {
            for (var i = 0; i + 1 < _shotSteps.Length; i += 2)
            {
                var a = _shotSteps[i]; var v = _shotSteps[i + 1];
                if (a == "mirror")
                {
                    if (v == "both") { _ed.Map.Mirror(KeepSide.Left); _ed.Mirror(KeepSide.Below); }
                    else _ed.Mirror(v == "left" ? KeepSide.Left : v == "right" ? KeepSide.Right : v == "below" ? KeepSide.Below : KeepSide.Above);
                }
                else if (a == "select")
                {
                    var n = int.Parse(v);
                    _ed.SetTool(Tool.Select);
                    _ed.Select(n < _ed.Map.Shapes.Count ? (object)_ed.Map.Shapes[n] : _ed.Map.Objects[n - _ed.Map.Shapes.Count]);
                }
                else if (a == "save") _ed.Save(v);
                else if (a == "tool") _ed.SetTool((Tool)Enum.Parse(typeof(Tool), v, true));
                else if (a == "mode") _ed.SetMode((ShapeMode)Enum.Parse(typeof(ShapeMode), v, true));
                else if (a == "color") PickColor(int.Parse(v));
                else if (a == "corner") _ed.Corner = float.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
                else if (a == "drag" || a == "click")
                {
                    var n = Nums(v);
                    _canvas.Simulate(new V2(n[0], n[1]), a == "drag" ? new V2(n[2], n[3]) : new V2(n[0], n[1]));
                }
                else if (a == "undo") _ed.Undo();
                else if (a == "testgame")
                {
                    // testgame <controls>,<bots>: writes the test request without starting or focusing the game
                    var tv = v.Split(',');
                    _testControls = tv[0]; _testBots = int.Parse(tv[1]);
                    TestInGame(false);
                }
                else if (a == "mods") _canvas.SimMods = v == "shift" ? Keys.Shift : v == "ctrl" ? Keys.Control : Keys.None;
                else if (a == "tileshape") _ed.SetTileShape((TileShape)int.Parse(v));
                else if (a == "controlsshot")
                {
                    // a picture of the Controls window, opened off-screen
                    using (var f = new ControlsForm { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000) })
                    {
                        f.Show();
                        Application.DoEvents();
                        using (var bmp = new Bitmap(f.Width, f.Height))
                        {
                            f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                            bmp.Save(v, System.Drawing.Imaging.ImageFormat.Png);
                        }
                    }
                }
                else if (a == "width") _ed.Thickness = float.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
                else if (a == "arc")
                {
                    // arc x0,y0,x1,y1,bendx,bendy: drag the ends, click where the middle goes
                    var n = Nums(v);
                    _canvas.Simulate(new V2(n[0], n[1]), new V2(n[2], n[3]));
                    _canvas.Simulate(new V2(n[4], n[5]), new V2(n[4], n[5]));
                }
                else if (a == "poly")
                {
                    // poly x,y,x,y,...: click each corner, then the first again
                    var n = Nums(v);
                    for (var k = 0; k + 1 < n.Length; k += 2) _canvas.Simulate(new V2(n[k], n[k + 1]), new V2(n[k], n[k + 1]));
                    _canvas.Simulate(new V2(n[0], n[1]), new V2(n[0], n[1]));
                }
                else if (a == "handle")
                {
                    // handle i,x,y: drag handle i of the selected shape to x,y
                    var n = Nums(v);
                    var at = _canvas.HandlePoints()[(int)n[0]];
                    _canvas.Simulate(at, new V2(n[1], n[2]));
                }
                else if (a == "dblhandle") _canvas.SimulateDoubleClick(_canvas.HandlePoints()[int.Parse(v)]);
            }
            _canvas.ZoomToFit();
            var t = new Timer { Interval = 600 };
            t.Tick += (s, e) =>
            {
                t.Stop();
                using (var bmp = new Bitmap(Width, Height))
                {
                    DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                    bmp.Save(_shot, System.Drawing.Imaging.ImageFormat.Png);
                }
                _ed.Dirty = false;
                Close();
            };
            t.Start();
        }

        private static float[] Nums(string v)
        {
            return v.Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        }

        // ------------------------------------------------------------------ building the UI

        private sealed class LayerItem
        {
            public object Ref; public string Text;
            public override string ToString() { return Text; }
        }

        private static Button SmallButton(string text, string tip, Action click)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(2), Padding = new Padding(4, 1, 4, 1) };
            b.Click += (s, e) => click();
            new ToolTip().SetToolTip(b, tip);
            return b;
        }

        private MenuStrip BuildMenu()
        {
            var menu = new MenuStrip();
            var file = new ToolStripMenuItem("&File");
            file.DropDownItems.Add(Item("&New", Keys.Control | Keys.N, () => { if (ConfirmDiscard()) { _ed.New(); _canvas.ZoomToFit(); } }));
            file.DropDownItems.Add(Item("&Open...", Keys.Control | Keys.O, () => OpenDialog(Paths.MapsFolder)));
            file.DropDownItems.Add(Item("Open an &Arena Creator map...", Keys.None, OpenOldMap));
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(Item("&Save", Keys.Control | Keys.S, () => Save(false)));
            file.DropDownItems.Add(Item("Save &As...", Keys.Control | Keys.Shift | Keys.S, () => Save(true)));
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(Item("&Test in game", Keys.F5, () => TestInGame(true)));
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(Item("Show the maps &folder", Keys.None, () =>
            {
                Directory.CreateDirectory(Paths.MapsFolder);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + Paths.MapsFolder + "\"");
            }));
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(Item("E&xit", Keys.None, Close));

            var edit = new ToolStripMenuItem("&Edit");
            _undoItem = Item("&Undo", Keys.Control | Keys.Z, _ed.Undo);
            _redoItem = Item("&Redo", Keys.Control | Keys.Y, _ed.Redo);
            edit.DropDownItems.Add(_undoItem);
            edit.DropDownItems.Add(_redoItem);
            edit.DropDownItems.Add(new ToolStripSeparator());
            edit.DropDownItems.Add(Item("&Duplicate", Keys.Control | Keys.D, _ed.DuplicateSelected));
            edit.DropDownItems.Add(Item("De&lete", Keys.None, _ed.DeleteSelected, "Del"));
            edit.DropDownItems.Add(Item("Paint &earlier", Keys.None, () => _ed.Reorder(-1), "["));
            edit.DropDownItems.Add(Item("Paint la&ter", Keys.None, () => _ed.Reorder(1), "]"));
            edit.DropDownItems.Add(new ToolStripSeparator());
            edit.DropDownItems.Add(Item("&Map settings", Keys.None, () => _ed.Select(null), "Esc"));

            var mirror = new ToolStripMenuItem("&Mirror");
            mirror.DropDownItems.Add(Item("Left half onto the right", Keys.None, () => _ed.Mirror(KeepSide.Left)));
            mirror.DropDownItems.Add(Item("Right half onto the left", Keys.None, () => _ed.Mirror(KeepSide.Right)));
            mirror.DropDownItems.Add(new ToolStripSeparator());
            mirror.DropDownItems.Add(Item("Bottom half onto the top", Keys.None, () => _ed.Mirror(KeepSide.Below)));
            mirror.DropDownItems.Add(Item("Top half onto the bottom", Keys.None, () => _ed.Mirror(KeepSide.Above)));
            mirror.DropDownItems.Add(new ToolStripSeparator());
            mirror.DropDownItems.Add(Item("Bottom-left quarter onto all four (both)", Keys.None, () => { _ed.Map.Mirror(KeepSide.Left); _ed.Mirror(KeepSide.Below); }));

            var view = new ToolStripMenuItem("&View");
            view.DropDownItems.Add(Item("Zoom to &fit", Keys.None, _canvas.ZoomToFit, "Home"));

            var help = new ToolStripMenuItem("&Help");
            help.DropDownItems.Add(Item("&Controls", Keys.F1, ShowControls));

            menu.Items.AddRange(new ToolStripItem[] { file, edit, mirror, view, help });
            return menu;
        }

        private static ToolStripMenuItem Item(string text, Keys keys, Action click, string keyText = null)
        {
            var i = new ToolStripMenuItem(text);
            if (keys != Keys.None) i.ShortcutKeys = keys;
            if (keyText != null) i.ShortcutKeyDisplayString = keyText;
            i.Click += (s, e) => click();
            return i;
        }

        private ToolStrip ToolStripMain()
        {
            var ts = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(4, 2, 4, 2), ImageScalingSize = new Size(16, 16) };
            ts.Items.Add(ToolButton(Tool.Select, "Select", "V", "Select and drag shapes and objects. Arrow keys nudge, Del deletes."));
            ts.Items.Add(new ToolStripSeparator());
            ts.Items.Add(ToolButton(Tool.Floor, "Floor", "F", "Drag to paint floor in the chosen colour (removes walls there)."));
            ts.Items.Add(ToolButton(Tool.Wall, "Wall", "W", "Drag to paint walls in the chosen colour (with floor under them)."));
            ts.Items.Add(ToolButton(Tool.Hole, "Hole", "H", "Drag to cut a hole: no floor, no walls, objects removed."));
            ts.Items.Add(ToolButton(Tool.Unwall, "Remove wall", "E", "Drag to remove walls but keep the floor."));
            ts.Items.Add(new ToolStripSeparator());
            ts.Items.Add(new ToolStripLabel("Shape:"));
            ts.Items.Add(ModeButton(ShapeMode.Tile, "Tile", "T", "Click or drag to paint single tiles (square or diagonal: pick below, Q turns it). Shift+drag fills an area, Ctrl+drag the opposite (hole / remove wall)."));
            ts.Items.Add(ModeButton(ShapeMode.Rectangle, "Rectangle", "R", "Drag from corner to corner."));
            ts.Items.Add(ModeButton(ShapeMode.Circle, "Circle", "C", "Drag from the centre outwards."));
            ts.Items.Add(ModeButton(ShapeMode.Arc, "Arc", "A", "A curved band: drag from one end to the other, then move the mouse to bend it and click."));
            ts.Items.Add(ModeButton(ShapeMode.Polygon, "Polygon", "P", "Click each corner; click the first corner (or press Space) to finish. Backspace takes back a corner."));
            ts.Items.Add(new ToolStripLabel("  Rounded corners:"));
            var corner = new NumericUpDown { Width = 56, DecimalPlaces = 2, Increment = 0.5m, Minimum = 0, Maximum = 50 };
            corner.ValueChanged += (s, e) => _ed.Corner = (float)corner.Value;
            ts.Items.Add(new ToolStripControlHost(corner) { ToolTipText = "Corner radius for new rectangles (0 = square corners)." });
            ts.Items.Add(new ToolStripLabel("  Arc width:"));
            var width = new NumericUpDown { Width = 56, DecimalPlaces = 2, Increment = 0.25m, Minimum = 0.1m, Maximum = 20, Value = 1 };
            width.ValueChanged += (s, e) => _ed.Thickness = (float)width.Value;
            ts.Items.Add(new ToolStripControlHost(width) { ToolTipText = "How wide new arcs are." });
            ts.Items.Add(new ToolStripSeparator());
            ts.Items.Add(new ToolStripLabel("Snap:"));
            var snap = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70, ToolTipText = "Grid snapping. Hold Alt while dragging to place freely." };
            snap.Items.AddRange(new object[] { "1", "0.5", "0.25", "Off" });
            snap.SelectedIndex = 1;
            snap.SelectedIndexChanged += (s, e) => _ed.Snap = snap.SelectedIndex == 0 ? 1f : snap.SelectedIndex == 1 ? 0.5f : snap.SelectedIndex == 2 ? 0.25f : 0f;
            ts.Items.Add(snap);
            ts.Items.Add(new ToolStripSeparator());
            var mirrorLr = new ToolStripButton("Mirror L→R") { ToolTipText = "Copy the left half onto the right, mirrored (red and blue swap). More in the Mirror menu." };
            mirrorLr.Click += (s, e) => _ed.Mirror(KeepSide.Left);
            ts.Items.Add(mirrorLr);
            var mirrorBt = new ToolStripButton("Mirror B→T") { ToolTipText = "Copy the bottom half onto the top, mirrored. More in the Mirror menu." };
            mirrorBt.Click += (s, e) => _ed.Mirror(KeepSide.Below);
            ts.Items.Add(mirrorBt);
            ts.Items.Add(new ToolStripSeparator());
            ts.Items.Add(TestButton());
            return ts;
        }

        private ToolStripSplitButton TestButton()
        {
            var b = new ToolStripSplitButton("\u25B6 Test in game")
            {
                ToolTipText = "Play this map in ClusterPuck 99 right away (F5). Starts the game if it isn't running; "
                    + "if it is, the map loads there. The arrow picks controls and bots.",
                Font = new Font(Font, FontStyle.Bold),
            };
            b.ButtonClick += (s, e) => TestInGame(true);
            var controls = new[] { "auto", "keyboard", "controller" };
            var controlNames = new[] { "Controls: controller if one is plugged in, else keyboard", "Controls: keyboard and mouse", "Controls: controller" };
            var bots = new[] { 0, 1, 2, 3 };
            var botNames = new[] { "Alone (no bots)", "1 against 1 bot", "2 against 2 (a bot with you)", "3 against 3" };
            var controlItems = new ToolStripMenuItem[controls.Length];
            var botItems = new ToolStripMenuItem[bots.Length];
            Action sync = () =>
            {
                for (var i = 0; i < controls.Length; i++) controlItems[i].Checked = controls[i] == _testControls;
                for (var i = 0; i < bots.Length; i++) botItems[i].Checked = bots[i] == _testBots;
            };
            for (var i = 0; i < controls.Length; i++)
            {
                var c = controls[i];
                controlItems[i] = new ToolStripMenuItem(controlNames[i]);
                controlItems[i].Click += (s, e) => { _testControls = c; sync(); };
                b.DropDownItems.Add(controlItems[i]);
            }
            b.DropDownItems.Add(new ToolStripSeparator());
            for (var i = 0; i < bots.Length; i++)
            {
                var n = bots[i];
                botItems[i] = new ToolStripMenuItem(botNames[i]);
                botItems[i].Click += (s, e) => { _testBots = n; sync(); };
                b.DropDownItems.Add(botItems[i]);
            }
            sync();
            return b;
        }

        private void TestInGame(bool launch)
        {
            _canvas.CancelDrag();
            var blocking = _ed.Check(_canvas.Geometry).Where(i => i.Contains("no floor") || i.Contains("has no goal")).ToArray();
            if (blocking.Length > 0 && launch)
            {
                var r = MessageBox.Show(this, "Before you test:\n\n- " + string.Join("\n- ", blocking) + "\n\nTest anyway?", "UltraPuck Map Maker", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) return;
            }
            try
            {
                _hint.Text = GameTest.Run(_ed.Map, _testControls, _testBots, launch);
            }
            catch (Exception e)
            {
                MessageBox.Show(this, "Can't test in the game:\n" + e.Message, "UltraPuck Map Maker", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private ToolStrip ObjectStrip()
        {
            var ts = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(4, 2, 4, 2), ImageScalingSize = new Size(16, 16) };
            ts.Items.Add(new ToolStripLabel("Place:"));
            ts.Items.Add(ToolButton(Tool.RedGoal, "Red goal", null, "Click to place a goal red defends.", Swatch(Editor.Red, true)));
            ts.Items.Add(ToolButton(Tool.BlueGoal, "Blue goal", null, "Click to place a goal blue defends.", Swatch(Editor.Blue, true)));
            ts.Items.Add(ToolButton(Tool.RedSpawn, "Red spawn", null, "Click to place a red start position.", Ring(Editor.Red)));
            ts.Items.Add(ToolButton(Tool.BlueSpawn, "Blue spawn", null, "Click to place a blue start position.", Ring(Editor.Blue)));
            ts.Items.Add(ToolButton(Tool.Boost, "Boost", null, "Click to place a speed boost pad.", Swatch(Color.FromArgb(110, 230, 90), false)));
            ts.Items.Add(ToolButton(Tool.Bumper, "Bumper", null, "Click to place a bumper.", Swatch(Color.FromArgb(232, 188, 40), false)));
            ts.Items.Add(ToolButton(Tool.Spike, "Spikes", null, "Click to place spikes.", Swatch(Color.FromArgb(90, 90, 98), false)));
            ts.Items.Add(new ToolStripSeparator());
            ts.Items.Add(new ToolStripLabel("Colour:"));
            for (var c = 0; c < MapData.Colors; c++)
            {
                var color = c;
                var b = new ToolStripButton { DisplayStyle = ToolStripItemDisplayStyle.Image, Image = Swatch(Editor.Palette[c], true), ToolTipText = Editor.ColorNames[c] + " (walls are drawn darker)" };
                b.Click += (s, e) => PickColor(color);
                _swatches[c] = b;
                ts.Items.Add(b);
            }
            ts.Items.Add(new ToolStripSeparator());
            ts.Items.Add(new ToolStripLabel("Tile:"));
            var tileNames = new[] { "Full square", "Triangle, bottom left", "Triangle, bottom right", "Triangle, top right", "Triangle, top left" };
            for (var i = 0; i < 5; i++)
            {
                var t = (TileShape)i;
                var b = new ToolStripButton { DisplayStyle = ToolStripItemDisplayStyle.Image, Image = TileIcon(t), ToolTipText = tileNames[i] + " (Q turns the triangle)" };
                b.Click += (s, e) => { _ed.SetTileShape(t); if (_ed.Mode != ShapeMode.Tile) _ed.SetMode(ShapeMode.Tile); };
                _tileButtons[t] = b;
                ts.Items.Add(b);
            }
            ts.Items.Add(new ToolStripSeparator());
            var controls = new ToolStripButton("? Controls") { ToolTipText = "Every control in one list (F1)." };
            controls.Click += (s, e) => ShowControls();
            ts.Items.Add(controls);
            return ts;
        }

        private ToolStripButton ToolButton(Tool t, string text, string key, string tip, Image image = null)
        {
            var b = new ToolStripButton(text) { ToolTipText = tip + (key != null ? " (" + key + ")" : string.Empty), Image = image, DisplayStyle = image != null ? ToolStripItemDisplayStyle.ImageAndText : ToolStripItemDisplayStyle.Text };
            b.Click += (s, e) => _ed.SetTool(t);
            _toolButtons[t] = b;
            return b;
        }

        private ToolStripButton ModeButton(ShapeMode m, string text, string key, string tip)
        {
            var b = new ToolStripButton(text) { ToolTipText = tip + " (" + key + ")" };
            b.Click += (s, e) => _ed.SetMode(m);
            _modeButtons[m] = b;
            return b;
        }

        private static Image Swatch(Color c, bool square)
        {
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            using (var b = new SolidBrush(c))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                if (square) { g.FillRectangle(b, 1, 1, 14, 14); g.DrawRectangle(Pens.DimGray, 1, 1, 13, 13); }
                else g.FillEllipse(b, 1, 1, 14, 14);
            }
            return bmp;
        }

        private static Image TileIcon(TileShape t)
        {
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            using (var b = new SolidBrush(Color.FromArgb(90, 90, 96)))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                Point a = new Point(2, 14), r = new Point(14, 14), tr = new Point(14, 2), tl = new Point(2, 2);
                Point[] pts;
                switch (t)
                {
                    case TileShape.BottomLeft: pts = new[] { a, r, tl }; break;
                    case TileShape.BottomRight: pts = new[] { a, r, tr }; break;
                    case TileShape.TopRight: pts = new[] { r, tr, tl }; break;
                    case TileShape.TopLeft: pts = new[] { a, tr, tl }; break;
                    default: pts = new[] { a, r, tr, tl }; break;
                }
                g.FillPolygon(b, pts);
                g.DrawRectangle(Pens.DarkGray, 2, 2, 12, 12);
            }
            return bmp;
        }

        private static Image Ring(Color c)
        {
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            using (var p = new Pen(c, 3f))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.DrawEllipse(p, 2, 2, 11, 11);
            }
            return bmp;
        }

        // ------------------------------------------------------------------ reacting to the editor

        private void OnChanged()
        {
            Text = _ed.Title;
            _undoItem.Enabled = _ed.CanUndo;
            _redoItem.Enabled = _ed.CanRedo;
            RefreshLayers();
            if (_props.SelectedObject != null && !_props.ContainsFocus) _props.Refresh();
            if (_ed.Selected == null && !(_props.SelectedObject is MapProps)) OnSelection();
        }

        private void RefreshLayers()
        {
            _syncing = true;
            _layers.BeginUpdate();
            var top = _layers.TopIndex;
            _layers.Items.Clear();
            foreach (var s in _ed.Map.Shapes) _layers.Items.Add(new LayerItem { Ref = s, Text = Editor.Describe(s) });
            foreach (var o in _ed.Map.Objects) _layers.Items.Add(new LayerItem { Ref = o, Text = Editor.Describe(o) + " at " + MapData.Num(o.X) + ", " + MapData.Num(o.Y) });
            SelectInList();
            if (top < _layers.Items.Count) _layers.TopIndex = top;
            _layers.EndUpdate();
            _syncing = false;
        }

        private void SelectInList()
        {
            for (var i = 0; i < _layers.Items.Count; i++)
                if (((LayerItem)_layers.Items[i]).Ref == _ed.Selected) { _layers.SelectedIndex = i; return; }
            _layers.SelectedIndex = -1;
        }

        private void OnSelection()
        {
            _syncing = true;
            SelectInList();
            _syncing = false;
            var sel = _ed.Selected;
            if (sel is MapShape) _props.SelectedObject = ShapeProps.For((MapShape)sel);
            else if (sel is MapObject) _props.SelectedObject = ObjectProps.For((MapObject)sel);
            else _props.SelectedObject = new MapProps(_ed.Map);
            UpdateHint();
            _canvas.Invalidate();
        }

        private void OnTool()
        {
            foreach (var kv in _toolButtons) kv.Value.Checked = kv.Key == _ed.Tool;
            foreach (var kv in _modeButtons) kv.Value.Checked = kv.Key == _ed.Mode;
            foreach (var kv in _tileButtons) kv.Value.Checked = kv.Key == _ed.TileShape;
            for (var c = 0; c < _swatches.Length; c++) _swatches[c].Checked = c == _ed.Color;
            UpdateHint();
        }

        private void UpdateHint()
        {
            string h;
            switch (_ed.Tool)
            {
                case Tool.Select:
                    if (_ed.Selected is MapShape)
                    {
                        var kind = ((MapShape)_ed.Selected).Kind;
                        h = "Drag the handles to resize, drag inside to move. "
                            + (kind == ShapeKind.Rect ? "The round handle rounds the corners. "
                              : kind == ShapeKind.Arc ? "The middle handle bends it, the round one sets its width. "
                              : kind == ShapeKind.Poly ? "Drag a side's dot to add a corner; double-click a corner to remove it. " : string.Empty)
                            + "Del deletes, Ctrl+D copies.";
                    }
                    else h = _ed.Selected != null ? "Drag to move, arrow keys to nudge, Del to delete, Ctrl+D to copy. Click empty space for map settings."
                        : "Click a shape or object to select it. Right-drag pans, wheel zooms.";
                    break;
                case Tool.Floor: case Tool.Wall: case Tool.Hole: case Tool.Unwall:
                    switch (_ed.Mode)
                    {
                        case ShapeMode.Circle: h = "Drag from the centre out."; break;
                        case ShapeMode.Arc: h = "Drag from one end to the other, then move the mouse to bend it and click. Width is in the toolbar."; break;
                        case ShapeMode.Polygon: h = "Click each corner. Click the first corner or press Space to finish; Backspace takes one back, Esc cancels."; break;
                        case ShapeMode.Tile: h = "Click or drag to paint tiles (Q turns diagonals). Shift+drag: an area. Ctrl+drag: an area of " + (_ed.Tool == Tool.Floor ? "hole" : _ed.Tool == Tool.Hole ? "floor" : _ed.Tool == Tool.Wall ? "remove wall" : "wall") + "."; break;
                        default: h = "Drag from corner to corner."; break;
                    }
                    if (_ed.Mode != ShapeMode.Tile) h += " Ctrl: " + (_ed.Tool == Tool.Floor ? "hole" : _ed.Tool == Tool.Hole ? "floor" : _ed.Tool == Tool.Wall ? "remove wall" : "wall") + " instead. Alt: no snapping.";
                    h += " Right-drag pans, wheel zooms.";
                    break;
                default: h = "Click to place. Right-drag pans, wheel zooms."; break;
            }
            _hint.Text = h;
        }

        private void PickColor(int c)
        {
            _ed.SetColor(c);
            var s = _ed.Selected as MapShape;
            if (s != null && (s.Op == MapOp.Floor || s.Op == MapOp.Wall) && s.Color != c) { s.Color = c; _ed.Commit(); _props.Refresh(); }
            if (_ed.Tool != Tool.Floor && _ed.Tool != Tool.Wall && _ed.Selected == null) _ed.SetTool(Tool.Floor);
        }

        // ------------------------------------------------------------------ keys

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            var typing = _props.ContainsFocus || ActiveControl is TextBoxBase || (ActiveControl is NumericUpDown) || (ActiveControl is ComboBox);
            if (!typing)
            {
                switch (keyData)
                {
                    case Keys.V: _ed.SetTool(Tool.Select); return true;
                    case Keys.F: _ed.SetTool(Tool.Floor); return true;
                    case Keys.W: _ed.SetTool(Tool.Wall); return true;
                    case Keys.H: _ed.SetTool(Tool.Hole); return true;
                    case Keys.E: _ed.SetTool(Tool.Unwall); return true;
                    case Keys.R: _ed.SetMode(ShapeMode.Rectangle); return true;
                    case Keys.C: _ed.SetMode(ShapeMode.Circle); return true;
                    case Keys.A: _ed.SetMode(ShapeMode.Arc); return true;
                    case Keys.P: _ed.SetMode(ShapeMode.Polygon); return true;
                    case Keys.T: _ed.SetMode(ShapeMode.Tile); return true;
                    case Keys.Q:
                        _ed.SetTileShape(_ed.TileShape == TileShape.TopLeft ? TileShape.Full : (TileShape)((int)_ed.TileShape + 1));
                        if (_ed.Mode != ShapeMode.Tile) _ed.SetMode(ShapeMode.Tile);
                        return true;
                    case Keys.Space: if (_canvas.FinishPending()) return true; break;
                    case Keys.Back: if (_canvas.RemoveLastPoint()) return true; break;
                    case Keys.Delete: _ed.DeleteSelected(); return true;
                    case Keys.OemOpenBrackets: _ed.Reorder(-1); return true;
                    case Keys.OemCloseBrackets: _ed.Reorder(1); return true;
                    case Keys.Home: _canvas.ZoomToFit(); return true;
                    case Keys.Escape:
                        if (!_canvas.CancelDrag()) _ed.Select(null);
                        return true;
                    case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down:
                        if (_canvas.Focused && _canvas.Nudge(keyData)) return true;
                        break;
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ------------------------------------------------------------------ files

        private bool ConfirmDiscard()
        {
            if (!_ed.Dirty) return true;
            var r = MessageBox.Show(this, "Save the changes to " + (_ed.Map.Name ?? "this map") + " first?", "UltraPuck Map Maker", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return false;
            if (r == DialogResult.Yes) return Save(false);
            return true;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!ConfirmDiscard()) e.Cancel = true;
            base.OnFormClosing(e);
        }

        private void OpenDialog(string folder)
        {
            if (!ConfirmDiscard()) return;
            using (var d = new OpenFileDialog { Filter = "UltraPuck maps (*.upmap)|*.upmap|All files|*.*", InitialDirectory = folder })
                if (d.ShowDialog(this) == DialogResult.OK) Open(d.FileName);
        }

        private void OpenOldMap()
        {
            var folder = Paths.OldMapsFolder;
            if (!Directory.Exists(folder) || Directory.GetFiles(folder, "*.upmap").Length == 0)
            {
                MessageBox.Show(this, "Your Arena Creator maps are copied here when you open the arena list in the game with UltraPuck installed:\n\n" + folder
                    + "\n\nStart the game, go to Play > the arena list once, then try again.", "No Arena Creator maps yet", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            OpenDialog(folder);
            // opening one of these makes a new map: Save As puts it in the main maps folder
            if (_ed.FilePath != null && string.Equals(Path.GetDirectoryName(_ed.FilePath), folder, StringComparison.OrdinalIgnoreCase))
            {
                _ed.FilePath = null;
                _ed.Dirty = true;
                Text = _ed.Title;
            }
        }

        private void Open(string path)
        {
            try
            {
                _ed.Open(path);
                _canvas.ZoomToFit();
            }
            catch (Exception e)
            {
                MessageBox.Show(this, "Can't open " + Path.GetFileName(path) + ":\n" + e.Message, "UltraPuck Map Maker", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private bool Save(bool saveAs)
        {
            var issues = _ed.Check(_canvas.Geometry);
            if (issues.Count > 0)
            {
                var r = MessageBox.Show(this, "Before you save:\n\n- " + string.Join("\n- ", issues.ToArray()) + "\n\nSave anyway?", "UltraPuck Map Maker", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) return false;
            }
            var path = _ed.FilePath;
            if (saveAs || path == null)
            {
                Directory.CreateDirectory(Paths.MapsFolder);
                using (var d = new SaveFileDialog
                {
                    Filter = "UltraPuck maps (*.upmap)|*.upmap",
                    InitialDirectory = path != null ? Path.GetDirectoryName(path) : Paths.MapsFolder,
                    FileName = Paths.SafeFileName(_ed.Map.Name) + ".upmap",
                })
                {
                    if (d.ShowDialog(this) != DialogResult.OK) return false;
                    path = d.FileName;
                }
                // a fresh map takes its name from the file
                if (_ed.Map.Name == "New map") { _ed.Map.Name = Path.GetFileNameWithoutExtension(path); _ed.Commit(); }
            }
            try
            {
                _ed.Save(path);
                return true;
            }
            catch (Exception e)
            {
                MessageBox.Show(this, "Can't save " + path + ":\n" + e.Message, "UltraPuck Map Maker", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        private ControlsForm _controls;

        /// <summary>Help > Controls (F1): every control in one list. Stays open beside the map if you like.</summary>
        private void ShowControls()
        {
            if (_controls != null && !_controls.IsDisposed) { _controls.Activate(); return; }
            _controls = new ControlsForm();
            _controls.Show(this);
        }
    }
}
