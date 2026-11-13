using System;
using System.Drawing;
using System.Windows.Forms;

namespace UltraPuckMapMaker
{
    /// <summary>Help > Controls (F1): every control in one list, grouped by what you're doing.</summary>
    internal sealed class ControlsForm : Form
    {
        // group title, then rows of { what you want to do, how }
        private static readonly object[] Sections =
        {
            "Pick what to paint", new[]
            {
                new[] { "Floor", "F" },
                new[] { "Wall (always has floor under it)", "W" },
                new[] { "Hole (removes floor, walls and objects)", "H" },
                new[] { "Remove wall (keeps the floor)", "E" },
                new[] { "Colour", "Click a colour on the second toolbar row" },
            },
            "Tiles (T): the default", new[]
            {
                new[] { "Paint tiles one square at a time", "Click, or click and drag" },
                new[] { "Fill a whole area", "Hold Shift and drag" },
                new[] { "Fill an area with the opposite (floor ↔ hole, wall ↔ remove wall)", "Hold Ctrl and drag" },
                new[] { "Square or corner triangle", "Click a Tile button on the second row, or press Q to turn it" },
            },
            "Other shapes", new[]
            {
                new[] { "Rectangle", "R, then drag from corner to corner" },
                new[] { "Rounded rectangle", "Set Rounded corners in the toolbar first" },
                new[] { "Circle", "C, then drag from the centre out" },
                new[] { "Arc (a curved wall)", "A, drag from one end to the other, move the mouse to bend it, click" },
                new[] { "Polygon (any shape)", "P, click each corner, then Space (or click the first corner)" },
                new[] { "Take back the last polygon corner", "Backspace" },
                new[] { "Draw the opposite (a hole instead of floor...)", "Hold Ctrl while drawing" },
                new[] { "Place freely, without snapping to the grid", "Hold Alt" },
                new[] { "Cancel what you're drawing", "Esc" },
            },
            "Goals, spawns and the rest", new[]
            {
                new[] { "Place a goal, spawn, boost, bumper or spikes", "Pick it on the second row, then click the map" },
                new[] { "Make a boost stronger or weaker", "Select it, then change Power in Properties" },
            },
            "Selecting and changing things", new[]
            {
                new[] { "Select tool", "V" },
                new[] { "Select something", "Click it, or click it in the list on the right" },
                new[] { "Move it", "Drag it, or use the arrow keys" },
                new[] { "Resize it", "Drag the white square handles" },
                new[] { "Round a rectangle's corners / change an arc's width", "Drag the yellow round handle" },
                new[] { "Bend an arc", "Drag the handle in its middle" },
                new[] { "Add a polygon corner", "Drag the small dot in the middle of a side" },
                new[] { "Remove a polygon corner", "Double-click it" },
                new[] { "Exact numbers", "Properties panel (bottom right)" },
                new[] { "Copy / delete", "Ctrl+D / Del" },
                new[] { "Paint earlier / later (what covers what)", "[ / ]" },
                new[] { "Map name, players and background", "Click empty space, or press Esc" },
                new[] { "Undo / redo", "Ctrl+Z / Ctrl+Y" },
            },
            "Moving around", new[]
            {
                new[] { "Pan", "Drag with the right or middle mouse button" },
                new[] { "Zoom", "Mouse wheel" },
                new[] { "See the whole map", "Home" },
            },
            "Symmetry", new[]
            {
                new[] { "Copy one half onto the other, mirrored", "Mirror L→R / Mirror B→T, more in the Mirror menu" },
            },
            "Saving and playing", new[]
            {
                new[] { "Try it in the game right now", "F5 (▶ Test in game). The arrow next to it picks controller and bots" },
                new[] { "Save", "Ctrl+S (it goes in the game's UltraPuck Maps folder)" },
                new[] { "Play it for real", "Save, then pick it under Custom in the game's arena list" },
                new[] { "New / open", "Ctrl+N / Ctrl+O" },
                new[] { "This list", "F1" },
            },
        };

        public ControlsForm()
        {
            Text = "Controls - UltraPuck Map Maker";
            Font = new Font("Segoe UI", 9.5f);
            Width = 860; Height = 720;
            MinimumSize = new Size(520, 360);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.F1) Close(); };

            var list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                ShowGroups = true,
                GridLines = false,
                BorderStyle = BorderStyle.None,
            };
            list.Columns.Add("To do this", 400);
            list.Columns.Add("Do this", 420);
            for (var i = 0; i + 1 < Sections.Length; i += 2)
            {
                var group = new ListViewGroup((string)Sections[i]);
                list.Groups.Add(group);
                foreach (var row in (string[][])Sections[i + 1])
                    list.Items.Add(new ListViewItem(row, group));
            }
            // the second column takes whatever width is left
            list.Resize += (s, e) => { list.Columns[1].Width = Math.Max(200, list.ClientSize.Width - list.Columns[0].Width - 4); };

            var note = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 30,
                Padding = new Padding(8, 0, 8, 0),
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "Hover over any toolbar button for a reminder. Esc or F1 closes this window.",
                ForeColor = SystemColors.GrayText,
            };
            Controls.Add(list);
            Controls.Add(note);
        }
    }
}
