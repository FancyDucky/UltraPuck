using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using CP99Online.Maps;

namespace UltraPuckMapMaker
{
    /// <summary>
    /// The map view: draws the map exactly as the game builds it (same geometry code), plus the grid and objects,
    /// and runs the tools. Pan: right or middle mouse drag. Zoom: mouse wheel. Hold Alt to place without snapping.
    /// </summary>
    internal sealed class MapCanvas : Control
    {
        private readonly Editor _ed;
        private float _scale = 28f;              // pixels per map unit
        private float _cx, _cy;                  // map point at the centre of the view

        // geometry, rebuilt when the map changes
        private int _builtVersion = -1;
        private MapGeometry _geo;
        private string _geoError;
        private GraphicsPath _floorAll, _wallAll;
        private readonly GraphicsPath[] _floorColor = new GraphicsPath[MapData.Colors];
        private readonly GraphicsPath[] _wallColor = new GraphicsPath[MapData.Colors];

        // interaction
        private bool _panning; private Point _panFrom; private float _panCx, _panCy;
        private bool _dragging; private V2 _dragFrom, _dragTo;      // shape tools
        private bool _moving; private V2 _moveFrom; private V2 _moveDone; // select tool
        private V2 _mouse;
        private V2 _hover;                                    // the mouse, snapped
        private bool _arcBending;                             // arc: ends placed, the mouse sets the bend
        private readonly List<V2> _poly = new List<V2>();     // polygon: corners placed so far
        private Tool _pendTool; private ShapeMode _pendMode;  // what the unfinished shape was started with

        // resize handles on the selected shape
        private enum HandleLook { Square, Round, Small }
        private sealed class Grip
        {
            public V2 At; public HandleLook Look;
            public Action<MapShape, V2> Apply;  // moves the handle to a point (applied to the shape as it was)
            public Action<MapShape> Remove;     // double-click (polygon corners)
        }
        private Grip _handle; private float[] _handleOrig; private Point _handleDown; private bool _handleLive;

        public event Action<string> Status;

        public MapCanvas(Editor ed)
        {
            _ed = ed;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = System.Drawing.Color.FromArgb(38, 42, 52);
            _ed.Changed += Invalidate;
            _ed.SelectionChanged += Invalidate;
            _ed.ToolChanged += () =>
            {
                if (Pending && (_ed.Tool != _pendTool || _ed.Mode != _pendMode)) CancelPending();
                Cursor = _ed.Tool == Tool.Select ? Cursors.Default : Cursors.Cross;
                Invalidate();
            };
            Cursor = Cursors.Cross;
        }

        public MapGeometry Geometry { get { EnsureGeometry(); return _geo; } }

        // ------------------------------------------------------------------ view

        private PointF ToScreen(float x, float y) { return new PointF(Width / 2f + (x - _cx) * _scale, Height / 2f - (y - _cy) * _scale); }
        private V2 ToMap(Point p) { return new V2((p.X - Width / 2f) / _scale + _cx, -(p.Y - Height / 2f) / _scale + _cy); }

        private Matrix ViewMatrix()
        {
            var m = new Matrix();
            m.Translate(Width / 2f, Height / 2f);
            m.Scale(_scale, -_scale);
            m.Translate(-_cx, -_cy);
            return m;
        }

        public void ZoomToFit()
        {
            EnsureGeometry();
            float x0 = -12, y0 = -8, x1 = 12, y1 = 8;
            if (_geo != null) { x0 = _geo.MinX; y0 = _geo.MinY; x1 = _geo.MaxX; y1 = _geo.MaxY; }
            foreach (var o in _ed.Map.Objects) { x0 = Math.Min(x0, o.X); x1 = Math.Max(x1, o.X); y0 = Math.Min(y0, o.Y); y1 = Math.Max(y1, o.Y); }
            _cx = (x0 + x1) / 2f; _cy = (y0 + y1) / 2f;
            var w = Math.Max(4f, x1 - x0 + 3f); var h = Math.Max(4f, y1 - y0 + 3f);
            _scale = Math.Max(4f, Math.Min(200f, Math.Min(Width / w, Height / h)));
            Invalidate();
        }

        private float Snap(float v)
        {
            var step = (ModifierKeys & Keys.Alt) != 0 || _ed.Snap <= 0f ? 0.05f : _ed.Snap;
            return (float)Math.Round(v / step) * step;
        }

        private V2 SnapPoint(V2 p) { return new V2(Snap(p.X), Snap(p.Y)); }

        // ------------------------------------------------------------------ geometry

        private void EnsureGeometry()
        {
            if (_builtVersion == _ed.Version) return;
            _builtVersion = _ed.Version;
            Free(ref _floorAll); Free(ref _wallAll);
            for (var c = 0; c < MapData.Colors; c++) { Free(ref _floorColor[c]); Free(ref _wallColor[c]); }
            try
            {
                _geo = MapGeometry.Build(_ed.Map);
                _geoError = null;
            }
            catch (Exception e)
            {
                _geo = null;
                _geoError = "Can't build this map: " + e.Message;
                return;
            }
            _floorAll = PathOf(_geo.FloorOutlines);
            _wallAll = PathOf(_geo.WallOutlines);
            for (var c = 0; c < MapData.Colors; c++)
            {
                var f = _geo.ColorOutlines(false, c);
                if (f.Count > 0) _floorColor[c] = PathOf(f);
                var w = _geo.ColorOutlines(true, c);
                if (w.Count > 0) _wallColor[c] = PathOf(w);
            }
        }

        private static void Free(ref GraphicsPath p) { if (p != null) { p.Dispose(); p = null; } }

        private static GraphicsPath PathOf(List<List<V2>> loops)
        {
            var path = new GraphicsPath(FillMode.Alternate);
            foreach (var loop in loops)
            {
                var pts = new PointF[loop.Count];
                for (var i = 0; i < loop.Count; i++) pts[i] = new PointF(loop[i].X, loop[i].Y);
                path.AddPolygon(pts);
            }
            return path;
        }

        // ------------------------------------------------------------------ drawing

        protected override void OnPaint(PaintEventArgs e)
        {
            EnsureGeometry();
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            DrawGrid(g, false);
            if (_geo != null)
            {
                using (var view = ViewMatrix())
                {
                    var state = g.Save();
                    g.Transform = view;
                    // floor: a base under everything (no seams between colours), then each colour
                    using (var b = new SolidBrush(Editor.Palette[1])) if (_floorAll != null) g.FillPath(b, _floorAll);
                    for (var c = 0; c < MapData.Colors; c++)
                        if (_floorColor[c] != null) using (var b = new SolidBrush(Editor.Palette[c])) g.FillPath(b, _floorColor[c]);
                    using (var pen = new Pen(System.Drawing.Color.FromArgb(150, 20, 20, 24), 0f)) if (_floorAll != null) g.DrawPath(pen, _floorAll);
                    g.Restore(state);

                    DrawGrid(g, true);

                    // walls: a shadow for height, then the tops
                    if (_wallAll != null)
                    {
                        state = g.Save();
                        var shadow = view.Clone();
                        shadow.Translate(0.12f, -0.18f, MatrixOrder.Prepend);
                        g.Transform = shadow;
                        using (var b = new SolidBrush(System.Drawing.Color.FromArgb(120, 0, 0, 0))) g.FillPath(b, _wallAll);
                        g.Transform = view;
                        using (var b = new SolidBrush(Editor.WallColor(3))) g.FillPath(b, _wallAll);
                        for (var c = 0; c < MapData.Colors; c++)
                            if (_wallColor[c] != null) using (var b = new SolidBrush(Editor.WallColor(c))) g.FillPath(b, _wallColor[c]);
                        using (var pen = new Pen(System.Drawing.Color.FromArgb(200, 10, 10, 14), 0f)) g.DrawPath(pen, _wallAll);
                        g.Restore(state);
                        shadow.Dispose();
                    }
                }
            }
            DrawMirrorLines(g);
            foreach (var o in _ed.Map.Objects) DrawObject(g, o, o == _ed.Selected);
            DrawSelection(g);
            DrawToolPreview(g);

            if (_geoError != null)
                using (var f = new Font(Font.FontFamily, 11f)) g.DrawString(_geoError, f, Brushes.OrangeRed, 12, 12);
        }

        private void DrawGrid(Graphics g, bool overMap)
        {
            var tl = ToMap(new Point(0, 0)); var br = ToMap(new Point(Width, Height));
            var step = _scale >= 36f ? 0.5f : _scale >= 12f ? 1f : _scale >= 5f ? 2f : 5f;
            var a = overMap ? 34 : 22;
            using (var minor = new Pen(System.Drawing.Color.FromArgb(a, 255, 255, 255)))
            using (var major = new Pen(System.Drawing.Color.FromArgb(a * 2, 255, 255, 255)))
            {
                for (var x = (float)Math.Floor(tl.X / step) * step; x <= br.X; x += step)
                {
                    var sx = ToScreen(x, 0).X;
                    g.DrawLine(Math.Abs(x % 5f) < 1e-3f ? major : minor, sx, 0, sx, Height);
                }
                for (var y = (float)Math.Floor(br.Y / step) * step; y <= tl.Y; y += step)
                {
                    var sy = ToScreen(0, y).Y;
                    g.DrawLine(Math.Abs(y % 5f) < 1e-3f ? major : minor, 0, sy, Width, sy);
                }
            }
        }

        private void DrawMirrorLines(Graphics g)
        {
            using (var pen = new Pen(System.Drawing.Color.FromArgb(110, 255, 220, 120), 1f) { DashStyle = DashStyle.Dash })
            {
                var o = ToScreen(0, 0);
                g.DrawLine(pen, o.X, 0, o.X, Height);
                g.DrawLine(pen, 0, o.Y, Width, o.Y);
            }
        }

        private void DrawObject(Graphics g, MapObject o, bool selected)
        {
            var p = ToScreen(o.X, o.Y);
            var r = Math.Max(5f, _scale * 0.42f);
            var team = o.Team == 2 ? Editor.Blue : Editor.Red;
            var rect = new RectangleF(p.X - r, p.Y - r, r * 2, r * 2);
            switch (o.Kind)
            {
                case ObjKind.Goal:
                    using (var b = new SolidBrush(team)) g.FillRectangle(b, rect);
                    using (var pen = new Pen(System.Drawing.Color.White, Math.Max(1f, r * 0.14f))) g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
                    Label(g, "G", p, r);
                    break;
                case ObjKind.Spawn:
                    using (var pen = new Pen(team, Math.Max(2f, r * 0.3f))) g.DrawEllipse(pen, rect.X + r * 0.15f, rect.Y + r * 0.15f, r * 1.7f, r * 1.7f);
                    using (var b = new SolidBrush(System.Drawing.Color.FromArgb(200, 255, 255, 255))) g.FillEllipse(b, p.X - r * 0.28f, p.Y - r * 0.28f, r * 0.56f, r * 0.56f);
                    break;
                case ObjKind.Boost:
                    using (var b = new SolidBrush(System.Drawing.Color.FromArgb(40, 44, 40))) g.FillRectangle(b, rect);
                    using (var b = new SolidBrush(System.Drawing.Color.FromArgb(110, 230, 90))) g.FillEllipse(b, p.X - r * 0.6f, p.Y - r * 0.6f, r * 1.2f, r * 1.2f);
                    Label(g, ">>", p, r * 0.8f);
                    break;
                case ObjKind.Bumper:
                    using (var b = new SolidBrush(System.Drawing.Color.FromArgb(232, 188, 40))) g.FillEllipse(b, rect);
                    using (var b = new SolidBrush(System.Drawing.Color.FromArgb(186, 120, 60))) g.FillEllipse(b, p.X - r * 0.4f, p.Y - r * 0.4f, r * 0.8f, r * 0.8f);
                    break;
                default:
                {
                    var pts = new PointF[16];
                    for (var i = 0; i < 16; i++)
                    {
                        var a = i * Math.PI / 8; var rr = i % 2 == 0 ? r * 1.05f : r * 0.45f;
                        pts[i] = new PointF(p.X + (float)Math.Cos(a) * rr, p.Y + (float)Math.Sin(a) * rr);
                    }
                    using (var b = new SolidBrush(System.Drawing.Color.FromArgb(60, 60, 66))) g.FillPolygon(b, pts);
                    using (var pen = new Pen(System.Drawing.Color.FromArgb(170, 170, 180))) g.DrawPolygon(pen, pts);
                    break;
                }
            }
            if (selected)
                using (var pen = new Pen(System.Drawing.Color.Yellow, 2f) { DashStyle = DashStyle.Dash })
                    g.DrawRectangle(pen, rect.X - 4, rect.Y - 4, rect.Width + 8, rect.Height + 8);
        }

        private void Label(Graphics g, string text, PointF p, float r)
        {
            if (r < 7f) return;
            using (var f = new Font(FontFamily.GenericSansSerif, r * 0.8f, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                var size = g.MeasureString(text, f);
                g.DrawString(text, f, Brushes.White, p.X - size.Width / 2f, p.Y - size.Height / 2f);
            }
        }

        private void DrawSelection(Graphics g)
        {
            var s = _ed.Selected as MapShape;
            if (s == null) return;
            using (var path = ShapePath(s))
            using (var view = ViewMatrix())
            {
                path.Transform(view);
                using (var pen = new Pen(System.Drawing.Color.Yellow, 2f) { DashStyle = DashStyle.Dash }) g.DrawPath(pen, path);
            }
            if (_ed.Tool == Tool.Select) DrawHandles(g, s);
        }

        private void DrawHandles(Graphics g, MapShape s)
        {
            using (var dark = new Pen(System.Drawing.Color.FromArgb(20, 20, 24), 1.5f))
            using (var round = new SolidBrush(System.Drawing.Color.FromArgb(255, 214, 64)))
            using (var small = new SolidBrush(System.Drawing.Color.FromArgb(170, 255, 255, 255)))
            {
                foreach (var h in Handles(s))
                {
                    var p = ToScreen(h.At.X, h.At.Y);
                    var r = h.Look == HandleLook.Small ? 3.5f : 5f;
                    var rect = new RectangleF(p.X - r, p.Y - r, 2 * r, 2 * r);
                    if (h.Look == HandleLook.Square) { g.FillRectangle(Brushes.White, rect); g.DrawRectangle(dark, rect.X, rect.Y, rect.Width, rect.Height); }
                    else if (h.Look == HandleLook.Round) { g.FillEllipse(round, rect); g.DrawEllipse(dark, rect); }
                    else g.FillEllipse(small, rect);
                }
            }
        }

        private bool Pending { get { return _dragging || _arcBending || _poly.Count > 0; } }

        private void DrawToolPreview(Graphics g)
        {
            if (!Pending) return;
            var s = PendingShape();
            if (s != null)
            {
                using (var path = ShapePath(s))
                using (var view = ViewMatrix())
                {
                    path.Transform(view);
                    var c = s.Op == MapOp.Hole ? System.Drawing.Color.FromArgb(90, 0, 0, 0)
                        : s.Op == MapOp.Unwall ? System.Drawing.Color.FromArgb(70, 255, 255, 255)
                        : System.Drawing.Color.FromArgb(150, s.Op == MapOp.Wall ? Editor.WallColor(s.Color) : Editor.Palette[s.Color]);
                    using (var b = new SolidBrush(c)) g.FillPath(b, path);
                    using (var pen = new Pen(System.Drawing.Color.White, 1.5f) { DashStyle = DashStyle.Dot }) g.DrawPath(pen, path);
                }
            }
            if (_poly.Count > 0)
            {
                // the corners so far, the next side to the mouse, and the first corner (click it to finish)
                using (var pen = new Pen(System.Drawing.Color.White, 1.5f))
                {
                    for (var i = 0; i < _poly.Count; i++)
                    {
                        var a = ToScreen(_poly[i].X, _poly[i].Y);
                        var b = i + 1 < _poly.Count ? ToScreen(_poly[i + 1].X, _poly[i + 1].Y) : ToScreen(_hover.X, _hover.Y);
                        g.DrawLine(pen, a, b);
                    }
                }
                for (var i = 0; i < _poly.Count; i++)
                {
                    var a = ToScreen(_poly[i].X, _poly[i].Y);
                    var first = i == 0 && _poly.Count >= 3;
                    var r = first ? 6f : 3.5f;
                    g.FillEllipse(first ? Brushes.Yellow : Brushes.White, a.X - r, a.Y - r, 2 * r, 2 * r);
                }
            }
            if (_arcBending)
            {
                // the line between the ends, to bend away from
                var a = ToScreen(_dragFrom.X, _dragFrom.Y); var b = ToScreen(_dragTo.X, _dragTo.Y);
                using (var pen = new Pen(System.Drawing.Color.FromArgb(140, 255, 255, 255), 1f) { DashStyle = DashStyle.Dash }) g.DrawLine(pen, a, b);
            }
            var sz = PendingSizeText(s);
            if (sz != null)
            {
                var at = _dragging && _ed.Mode != ShapeMode.Polygon ? _dragTo : _hover;
                var p = ToScreen(at.X, at.Y);
                using (var f = new Font(Font.FontFamily, 9f)) g.DrawString(sz, f, Brushes.White, p.X + 12, p.Y + 6);
            }
        }

        /// <summary>The outline of one shape on its own (map units), for selection and previews.</summary>
        internal static GraphicsPath ShapePath(MapShape s)
        {
            var path = new GraphicsPath();
            var p = s.P;
            switch (s.Kind)
            {
                case ShapeKind.Rect:
                {
                    float x0 = Math.Min(p[0], p[2]), y0 = Math.Min(p[1], p[3]), x1 = Math.Max(p[0], p[2]), y1 = Math.Max(p[1], p[3]);
                    var r = p.Length > 4 ? Math.Min(p[4], Math.Min(x1 - x0, y1 - y0) / 2f) : 0f;
                    if (r <= 0.001f) path.AddRectangle(new RectangleF(x0, y0, x1 - x0, y1 - y0));
                    else
                    {
                        path.AddArc(x0, y0, 2 * r, 2 * r, 180, 90);
                        path.AddArc(x1 - 2 * r, y0, 2 * r, 2 * r, 270, 90);
                        path.AddArc(x1 - 2 * r, y1 - 2 * r, 2 * r, 2 * r, 0, 90);
                        path.AddArc(x0, y1 - 2 * r, 2 * r, 2 * r, 90, 90);
                        path.CloseFigure();
                    }
                    break;
                }
                case ShapeKind.Circle:
                    path.AddEllipse(p[0] - p[2], p[1] - p[2], p[2] * 2, p[2] * 2);
                    break;
                case ShapeKind.Arc:
                {
                    float cx = p[0], cy = p[1], r = Math.Abs(p[2]), half = Math.Abs(p[5]) / 2f, a0 = Math.Min(p[3], p[4]), sweep = Math.Abs(p[4] - p[3]);
                    if (sweep >= 360f) { path.AddEllipse(cx - r - half, cy - r - half, 2 * (r + half), 2 * (r + half)); if (r > half) path.AddEllipse(cx - r + half, cy - r + half, 2 * (r - half), 2 * (r - half)); break; }
                    var pts = new List<PointF>();
                    const int n = 64, cap = 12;
                    // outer edge, round cap at the end, inner edge back, round cap at the start (like the game)
                    for (var i = 0; i <= n; i++) pts.Add(Polar(cx, cy, r + half, a0 + sweep * i / n));
                    var e1 = Polar(cx, cy, r, a0 + sweep);
                    for (var i = 1; i < cap; i++) pts.Add(Polar(e1.X, e1.Y, half, a0 + sweep + 180f * i / cap));
                    for (var i = n; i >= 0; i--) pts.Add(Polar(cx, cy, r - half, a0 + sweep * i / n));
                    var e0 = Polar(cx, cy, r, a0);
                    for (var i = 1; i < cap; i++) pts.Add(Polar(e0.X, e0.Y, half, a0 + 180f + 180f * i / cap));
                    path.AddPolygon(pts.ToArray());
                    break;
                }
                default:
                {
                    var pts = new PointF[p.Length / 2];
                    for (var i = 0; i < pts.Length; i++) pts[i] = new PointF(p[2 * i], p[2 * i + 1]);
                    path.AddPolygon(pts);
                    break;
                }
            }
            return path;
        }

        // ------------------------------------------------------------------ tools

        private static bool IsShapeTool(Tool t) { return t == Tool.Floor || t == Tool.Wall || t == Tool.Hole || t == Tool.Unwall; }

        private MapShape PendingShape()
        {
            var op = _ed.Tool == Tool.Floor ? MapOp.Floor : _ed.Tool == Tool.Wall ? MapOp.Wall : _ed.Tool == Tool.Hole ? MapOp.Hole : MapOp.Unwall;
            var s = new MapShape { Op = op, Color = _ed.Color };
            switch (_ed.Mode)
            {
                case ShapeMode.Circle:
                {
                    var r = Snap((_dragTo - _dragFrom).Length);
                    if (r <= 0.01f) return null;
                    s.Kind = ShapeKind.Circle;
                    s.P = new[] { _dragFrom.X, _dragFrom.Y, r };
                    break;
                }
                case ShapeMode.Arc:
                {
                    var p = ArcThrough(_dragFrom, _dragTo, _arcBending ? Bend(_dragFrom, _dragTo, _hover) : 0f, _ed.Thickness);
                    if (p == null) return null;
                    s.Kind = ShapeKind.Arc;
                    s.P = p;
                    break;
                }
                case ShapeMode.Polygon:
                {
                    var pts = new List<V2>(_poly);
                    if (pts.Count > 0 && !Same(_hover, pts[pts.Count - 1])) pts.Add(_hover);
                    if (pts.Count < 3) return null;
                    s.Kind = ShapeKind.Poly;
                    s.P = Flatten(pts);
                    break;
                }
                default:
                {
                    float x0 = Math.Min(_dragFrom.X, _dragTo.X), x1 = Math.Max(_dragFrom.X, _dragTo.X);
                    float y0 = Math.Min(_dragFrom.Y, _dragTo.Y), y1 = Math.Max(_dragFrom.Y, _dragTo.Y);
                    if (x1 - x0 < 0.01f || y1 - y0 < 0.01f) return null;
                    s.Kind = ShapeKind.Rect;
                    s.P = _ed.Corner > 0f ? new[] { x0, y0, x1, y1, _ed.Corner } : new[] { x0, y0, x1, y1 };
                    break;
                }
            }
            return s;
        }

        private string PendingSizeText(MapShape s)
        {
            switch (_ed.Mode)
            {
                case ShapeMode.Circle: return _dragging ? "radius " + MapData.Num(Snap((_dragTo - _dragFrom).Length)) : null;
                case ShapeMode.Arc:
                    if (s == null) return null;
                    return (_arcBending ? "click to place: " : "") + "radius " + MapData.Num((float)Math.Round(s.P[2], 2)) + ", " + MapData.Num((float)Math.Round(Math.Abs(s.P[4] - s.P[3]))) + "°";
                case ShapeMode.Polygon:
                    return _poly.Count + (_poly.Count == 1 ? " corner" : " corners") + (_poly.Count >= 3 ? " (click the first one or press Enter to finish)" : string.Empty);
                default:
                    return _dragging ? MapData.Num(Math.Abs(_dragTo.X - _dragFrom.X)) + " x " + MapData.Num(Math.Abs(_dragTo.Y - _dragFrom.Y)) : null;
            }
        }

        private static bool Same(V2 a, V2 b) { return Math.Abs(a.X - b.X) < 1e-4f && Math.Abs(a.Y - b.Y) < 1e-4f; }

        private static float[] Flatten(List<V2> pts)
        {
            var p = new float[pts.Count * 2];
            for (var i = 0; i < pts.Count; i++) { p[2 * i] = pts[i].X; p[2 * i + 1] = pts[i].Y; }
            return p;
        }

        private static PointF Polar(float cx, float cy, float r, double deg)
        {
            var a = deg * Math.PI / 180.0;
            return new PointF(cx + r * (float)Math.Cos(a), cy + r * (float)Math.Sin(a));
        }

        // ------------------------------------------------------------------ arcs by their ends and bend

        /// <summary>The left-hand normal of a to b (unit length).</summary>
        private static V2 Normal(V2 a, V2 b)
        {
            var d = b - a; var l = d.Length;
            return l < 1e-6f ? new V2(0, 1) : new V2(-d.Y / l, d.X / l);
        }

        /// <summary>How far q is from the line a-b, to its left (the bend that puts the arc's middle at q).</summary>
        private static float Bend(V2 a, V2 b, V2 q)
        {
            var mid = (a + b) * 0.5f;
            return V2.Dot(q - mid, Normal(a, b));
        }

        /// <summary>
        /// Arc numbers (x y r start end thickness) for an arc from a to b whose middle is <paramref name="bend"/> to
        /// the left of the line a-b (negative: to the right). Null if the ends are on top of each other.
        /// </summary>
        internal static float[] ArcThrough(V2 a, V2 b, float bend, float thickness)
        {
            var len = (b - a).Length;
            if (len < 0.05f) return null;
            var min = 0.02f * len; // an arc can't be straight: at least a slight bend
            if (Math.Abs(bend) < min) bend = bend < 0f ? -min : min;
            bend = Math.Max(-20f * len, Math.Min(20f * len, bend));
            var n = Normal(a, b);
            var half = len / 2f;
            var r = (half * half + bend * bend) / (2f * Math.Abs(bend));
            var c = (a + b) * 0.5f + n * (bend - Math.Sign(bend) * r);
            var aa = Math.Atan2(a.Y - c.Y, a.X - c.X) * 180.0 / Math.PI;
            var ab = Math.Atan2(b.Y - c.Y, b.X - c.X) * 180.0 / Math.PI;
            // counter-clockwise from start to end, through the middle
            double start, sweep;
            if (bend > 0f) { start = ab; sweep = ((aa - ab) % 360.0 + 360.0) % 360.0; }
            else { start = aa; sweep = ((ab - aa) % 360.0 + 360.0) % 360.0; }
            return new[]
            {
                (float)Math.Round(c.X, 4), (float)Math.Round(c.Y, 4), (float)Math.Round(r, 4),
                (float)Math.Round(start, 3), (float)Math.Round(start + sweep, 3), Math.Max(0.05f, thickness),
            };
        }

        /// <summary>An arc's ends (counter-clockwise from a to b) and bend; false for a full ring.</summary>
        private static bool ArcEnds(float[] p, out V2 a, out V2 b, out float bend)
        {
            float cx = p[0], cy = p[1], r = Math.Abs(p[2]);
            double a0 = Math.Min(p[3], p[4]), sweep = Math.Abs(p[4] - p[3]);
            var e0 = Polar(cx, cy, r, a0); var e1 = Polar(cx, cy, r, a0 + sweep);
            a = new V2(e0.X, e0.Y); b = new V2(e1.X, e1.Y);
            bend = -(float)(r * (1.0 - Math.Cos(sweep / 2.0 * Math.PI / 180.0)));
            return sweep < 359.5;
        }

        // ------------------------------------------------------------------ resize handles

        private List<Grip> Handles(MapShape s)
        {
            var list = new List<Grip>();
            var p = s.P;
            switch (s.Kind)
            {
                case ShapeKind.Rect:
                {
                    float x0 = Math.Min(p[0], p[2]), y0 = Math.Min(p[1], p[3]), x1 = Math.Max(p[0], p[2]), y1 = Math.Max(p[1], p[3]);
                    // corners and sides: the grabbed sides follow the mouse
                    for (var ix = 0; ix < 3; ix++)
                        for (var iy = 0; iy < 3; iy++)
                        {
                            if (ix == 1 && iy == 1) continue;
                            int hx = ix, hy = iy;
                            list.Add(new Grip
                            {
                                At = new V2(ix == 0 ? x0 : ix == 1 ? (x0 + x1) / 2f : x1, iy == 0 ? y0 : iy == 1 ? (y0 + y1) / 2f : y1),
                                Look = HandleLook.Square,
                                Apply = (sh, q) =>
                                {
                                    float a0 = Math.Min(sh.P[0], sh.P[2]), b0 = Math.Min(sh.P[1], sh.P[3]), a1 = Math.Max(sh.P[0], sh.P[2]), b1 = Math.Max(sh.P[1], sh.P[3]);
                                    if (hx == 0) a0 = Math.Min(q.X, a1 - 0.1f); else if (hx == 2) a1 = Math.Max(q.X, a0 + 0.1f);
                                    if (hy == 0) b0 = Math.Min(q.Y, b1 - 0.1f); else if (hy == 2) b1 = Math.Max(q.Y, b0 + 0.1f);
                                    sh.P[0] = a0; sh.P[1] = b0; sh.P[2] = a1; sh.P[3] = b1;
                                },
                            });
                        }
                    // corner rounding: a round handle just inside the top-left corner, on its diagonal
                    var rr = p.Length > 4 ? Math.Min(p[4], Math.Min(x1 - x0, y1 - y0) / 2f) : 0f;
                    var off = 16f / _scale;
                    var k = rr * 0.2929f; // where the rounded corner crosses the diagonal
                    list.Add(new Grip
                    {
                        At = new V2(x0 + k + off, y1 - k - off),
                        Look = HandleLook.Round,
                        Apply = (sh, q) =>
                        {
                            float a0 = Math.Min(sh.P[0], sh.P[2]), b0 = Math.Min(sh.P[1], sh.P[3]), a1 = Math.Max(sh.P[0], sh.P[2]), b1 = Math.Max(sh.P[1], sh.P[3]);
                            var along = ((q.X - a0) + (b1 - q.Y)) / 2f - off;
                            var r = Math.Max(0f, Math.Min(Snap(along / 0.2929f), Math.Min(a1 - a0, b1 - b0) / 2f));
                            sh.P = new[] { sh.P[0], sh.P[1], sh.P[2], sh.P[3], r };
                        },
                    });
                    break;
                }
                case ShapeKind.Circle:
                    for (var i = 0; i < 4; i++)
                        list.Add(new Grip
                        {
                            At = new V2(p[0] + (i == 0 ? p[2] : i == 2 ? -p[2] : 0f), p[1] + (i == 1 ? p[2] : i == 3 ? -p[2] : 0f)),
                            Look = HandleLook.Square,
                            Apply = (sh, q) => sh.P[2] = Math.Max(0.1f, Snap((q - new V2(sh.P[0], sh.P[1])).Length)),
                        });
                    break;
                case ShapeKind.Arc:
                {
                    V2 a, b; float bend;
                    var c = new V2(p[0], p[1]);
                    var r = Math.Abs(p[2]); var half = Math.Abs(p[5]) / 2f;
                    if (!ArcEnds(p, out a, out b, out bend))
                    {
                        // a full ring: its radius (top) and width (right, outer edge)
                        list.Add(new Grip { At = new V2(c.X, c.Y + r), Look = HandleLook.Square, Apply = (sh, q) => sh.P[2] = Math.Max(0.1f, Snap((q - new V2(sh.P[0], sh.P[1])).Length)) });
                        list.Add(new Grip { At = new V2(c.X + r + half, c.Y), Look = HandleLook.Round, Apply = (sh, q) => sh.P[5] = Math.Max(0.1f, Snap(2f * Math.Abs((q - new V2(sh.P[0], sh.P[1])).Length - Math.Abs(sh.P[2])))) });
                        break;
                    }
                    var mid = (a + b) * 0.5f + Normal(a, b) * bend;
                    var outward = mid - c; outward = outward * (1f / Math.Max(1e-4f, outward.Length));
                    // ends: move one end, the other end and the bend stay
                    list.Add(new Grip { At = a, Look = HandleLook.Square, Apply = (sh, q) => ReArc(sh, 0, q) });
                    list.Add(new Grip { At = b, Look = HandleLook.Square, Apply = (sh, q) => ReArc(sh, 1, q) });
                    // middle: the bend
                    list.Add(new Grip { At = mid, Look = HandleLook.Square, Apply = (sh, q) => ReArc(sh, 2, q) });
                    // width: on the outer edge at the middle
                    list.Add(new Grip
                    {
                        At = mid + outward * half,
                        Look = HandleLook.Round,
                        Apply = (sh, q) => sh.P[5] = Math.Max(0.1f, Snap(2f * Math.Abs((q - new V2(sh.P[0], sh.P[1])).Length - Math.Abs(sh.P[2])))),
                    });
                    break;
                }
                default:
                {
                    var n = p.Length / 2;
                    // the middle of each side: drag to add a corner there
                    for (var i = 0; i < n; i++)
                    {
                        var j = (i + 1) % n; var at = i;
                        list.Add(new Grip
                        {
                            At = new V2((p[2 * i] + p[2 * j]) / 2f, (p[2 * i + 1] + p[2 * j + 1]) / 2f),
                            Look = HandleLook.Small,
                            Apply = (sh, q) =>
                            {
                                var pts = new List<float>(sh.P);
                                pts.InsertRange(2 * (at + 1), new[] { q.X, q.Y });
                                sh.P = pts.ToArray();
                            },
                        });
                    }
                    // the corners: drag to move, double-click to remove
                    for (var i = 0; i < n; i++)
                    {
                        var at = i;
                        list.Add(new Grip
                        {
                            At = new V2(p[2 * i], p[2 * i + 1]),
                            Look = HandleLook.Square,
                            Apply = (sh, q) => { sh.P[2 * at] = q.X; sh.P[2 * at + 1] = q.Y; },
                            Remove = sh =>
                            {
                                if (sh.P.Length <= 6) return;
                                var pts = new List<float>(sh.P);
                                pts.RemoveRange(2 * at, 2);
                                sh.P = pts.ToArray();
                            },
                        });
                    }
                    break;
                }
            }
            return list;
        }

        /// <summary>Moves an arc's start (0), end (1) or middle (2) to q, keeping the rest.</summary>
        private static void ReArc(MapShape sh, int which, V2 q)
        {
            V2 a, b; float bend;
            ArcEnds(sh.P, out a, out b, out bend);
            var len = (b - a).Length;
            if (which == 2) bend = Bend(a, b, q);
            else
            {
                // moving an end keeps the arc's shape (how far round it goes), scaled to the new length
                if (which == 0) a = q; else b = q;
                if (len > 1e-4f) bend *= (b - a).Length / len;
            }
            var np = ArcThrough(a, b, bend, sh.P[5]);
            if (np != null) sh.P = np;
        }

        private Grip HandleAt(Point screen, MapShape s)
        {
            Grip best = null; var bestD = 8f * 8f;
            foreach (var h in Handles(s))
            {
                var p = ToScreen(h.At.X, h.At.Y);
                var d = (p.X - screen.X) * (p.X - screen.X) + (p.Y - screen.Y) * (p.Y - screen.Y);
                if (d <= bestD) { bestD = d; best = h; }
            }
            return best;
        }

        /// <summary>Self-test: where the selected shape's handles are.</summary>
        internal List<V2> HandlePoints()
        {
            var s = _ed.Selected as MapShape;
            var list = new List<V2>();
            if (s != null) foreach (var h in Handles(s)) list.Add(h.At);
            return list;
        }

        private ObjKind? ObjectFor(Tool t, out int team)
        {
            team = 1;
            switch (t)
            {
                case Tool.RedGoal: return ObjKind.Goal;
                case Tool.BlueGoal: team = 2; return ObjKind.Goal;
                case Tool.RedSpawn: return ObjKind.Spawn;
                case Tool.BlueSpawn: team = 2; return ObjKind.Spawn;
                case Tool.Boost: return ObjKind.Boost;
                case Tool.Bumper: return ObjKind.Bumper;
                case Tool.Spike: return ObjKind.Spike;
                default: return null;
            }
        }

        /// <summary>What's under a map point: objects first, then the shape painted last.</summary>
        private object HitTest(V2 p)
        {
            var objs = _ed.Map.Objects;
            for (var i = objs.Count - 1; i >= 0; i--)
                if (Math.Abs(objs[i].X - p.X) <= 0.5f && Math.Abs(objs[i].Y - p.Y) <= 0.5f) return objs[i];
            var shapes = _ed.Map.Shapes;
            for (var i = shapes.Count - 1; i >= 0; i--)
                if (MapGeometry.Distance(shapes[i], p.X, p.Y) <= 0f) return shapes[i];
            return null;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            var m = ToMap(e.Location);
            if (e.Button == MouseButtons.Right || e.Button == MouseButtons.Middle)
            {
                _panning = true; _panFrom = e.Location; _panCx = _cx; _panCy = _cy;
                Cursor = Cursors.SizeAll;
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            Capture = true;
            if (_ed.Tool == Tool.Select)
            {
                var sel = _ed.Selected as MapShape;
                var h = sel != null ? HandleAt(e.Location, sel) : null;
                if (h != null)
                {
                    if (e.Clicks >= 2 && h.Remove != null) { h.Remove(sel); _ed.Commit(); return; }
                    _handle = h; _handleOrig = (float[])sel.P.Clone(); _handleDown = e.Location; _handleLive = false;
                    return;
                }
                var hit = HitTest(m);
                _ed.Select(hit);
                if (hit != null) { _moving = true; _moveFrom = SnapPoint(m); _moveDone = new V2(0, 0); }
                return;
            }
            if (IsShapeTool(_ed.Tool))
            {
                var sp = SnapPoint(m);
                _hover = sp;
                if (_ed.Mode == ShapeMode.Polygon) { PolygonClick(sp, e.Location); return; }
                if (_arcBending) { FinishShape(); return; }
                _dragging = true;
                _pendTool = _ed.Tool; _pendMode = _ed.Mode;
                _dragFrom = _dragTo = sp;
                Invalidate();
                return;
            }
            int team;
            var kind = ObjectFor(_ed.Tool, out team);
            if (kind != null)
            {
                var sp = SnapPoint(m);
                var o = new MapObject { Kind = kind.Value, Team = team, X = sp.X, Y = sp.Y };
                if (kind == ObjKind.Goal || kind == ObjKind.Spawn || kind == ObjKind.Boost || kind == ObjKind.Bumper || kind == ObjKind.Spike)
                {
                    _ed.Map.Objects.Add(o);
                    _ed.Select(o);
                    _ed.Commit();
                    // keep the object under the mouse movable right away
                    _moving = true; _moveFrom = sp; _moveDone = new V2(0, 0);
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var m = ToMap(e.Location);
            _mouse = m;
            if (_panning)
            {
                _cx = _panCx - (e.X - _panFrom.X) / _scale;
                _cy = _panCy + (e.Y - _panFrom.Y) / _scale;
                Invalidate();
            }
            else if (_dragging)
            {
                _dragTo = _hover = SnapPoint(m);
                Invalidate();
            }
            else if (_arcBending || _poly.Count > 0)
            {
                _hover = SnapPoint(m);
                Invalidate();
            }
            else if (_handle != null)
            {
                if (!_handleLive && Math.Abs(e.X - _handleDown.X) + Math.Abs(e.Y - _handleDown.Y) >= 3) _handleLive = true;
                var sel = _ed.Selected as MapShape;
                if (_handleLive && sel != null)
                {
                    sel.P = (float[])_handleOrig.Clone();
                    _handle.Apply(sel, SnapPoint(m));
                    _ed.Touch();
                }
            }
            else if (_ed.Tool == Tool.Select && e.Button == MouseButtons.None)
            {
                var sel = _ed.Selected as MapShape;
                var over = sel != null && HandleAt(e.Location, sel) != null;
                var want = over ? Cursors.Hand : Cursors.Default;
                if (Cursor != want) Cursor = want;
            }
            if (_moving)
            {
                var sp = SnapPoint(m);
                var want = new V2(sp.X - _moveFrom.X, sp.Y - _moveFrom.Y);
                var dx = want.X - _moveDone.X; var dy = want.Y - _moveDone.Y;
                if (Math.Abs(dx) > 1e-5f || Math.Abs(dy) > 1e-5f)
                {
                    if (_ed.Selected is MapShape) Editor.Move((MapShape)_ed.Selected, dx, dy);
                    else if (_ed.Selected is MapObject) { var o = (MapObject)_ed.Selected; o.X = Round(o.X + dx); o.Y = Round(o.Y + dy); }
                    _moveDone = want;
                    _ed.Touch();
                }
            }
            if (Status != null)
            {
                var sp = SnapPoint(m);
                Status("x " + MapData.Num(sp.X) + "   y " + MapData.Num(sp.Y));
            }
        }

        private static float Round(float v) { return (float)Math.Round(v, 4); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_panning && (e.Button == MouseButtons.Right || e.Button == MouseButtons.Middle))
            {
                _panning = false;
                Cursor = _ed.Tool == Tool.Select ? Cursors.Default : Cursors.Cross;
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            Capture = false;
            if (_dragging)
            {
                _dragging = false;
                if (_ed.Mode == ShapeMode.Arc)
                {
                    // ends placed: now the mouse bends it, a click places it
                    if ((_dragTo - _dragFrom).Length >= 0.1f) { _arcBending = true; _hover = _dragTo; }
                    Invalidate();
                    return;
                }
                FinishShape();
            }
            if (_moving)
            {
                _moving = false;
                _ed.Commit();
            }
            if (_handle != null)
            {
                _handle = null;
                _ed.Commit();
            }
        }

        /// <summary>Adds the shape being drawn to the map.</summary>
        private void FinishShape()
        {
            var s = PendingShape();
            if (_ed.Mode == ShapeMode.Polygon)
            {
                s = null;
                if (_poly.Count >= 3)
                {
                    var op = _ed.Tool == Tool.Floor ? MapOp.Floor : _ed.Tool == Tool.Wall ? MapOp.Wall : _ed.Tool == Tool.Hole ? MapOp.Hole : MapOp.Unwall;
                    s = new MapShape { Op = op, Color = _ed.Color, Kind = ShapeKind.Poly, P = Flatten(_poly) };
                }
            }
            _dragging = false; _arcBending = false; _poly.Clear();
            if (s != null)
            {
                _ed.Map.Shapes.Add(s);
                if (s.Op == MapOp.Hole)
                    _ed.Map.Objects.RemoveAll(o => MapGeometry.Distance(s, o.X, o.Y) < 0f); // a hole takes objects with it
                _ed.Commit();
            }
            Invalidate();
        }

        private void PolygonClick(V2 sp, Point screen)
        {
            if (_poly.Count == 0) { _pendTool = _ed.Tool; _pendMode = _ed.Mode; }
            if (_poly.Count >= 3)
            {
                // clicking the first corner, or the last one again (a double-click), finishes it
                var f = ToScreen(_poly[0].X, _poly[0].Y);
                if (Math.Abs(f.X - screen.X) <= 8 && Math.Abs(f.Y - screen.Y) <= 8) { FinishShape(); return; }
            }
            if (_poly.Count > 0 && Same(sp, _poly[_poly.Count - 1]))
            {
                if (_poly.Count >= 3) FinishShape();
                return;
            }
            _poly.Add(sp);
            Invalidate();
        }

        /// <summary>Enter: finishes a polygon (or places an arc as it's bent now).</summary>
        public bool FinishPending()
        {
            if (_poly.Count >= 3 || _arcBending) { FinishShape(); return true; }
            return false;
        }

        /// <summary>Backspace: takes back the last polygon corner.</summary>
        public bool RemoveLastPoint()
        {
            if (_poly.Count == 0) return false;
            _poly.RemoveAt(_poly.Count - 1);
            Invalidate();
            return true;
        }

        private void CancelPending()
        {
            _dragging = false; _arcBending = false; _poly.Clear();
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            var before = ToMap(e.Location);
            var f = e.Delta > 0 ? 1.2f : 1f / 1.2f;
            _scale = Math.Max(3f, Math.Min(300f, _scale * f));
            var after = ToMap(e.Location);
            _cx += before.X - after.X; _cy += before.Y - after.Y;
            Invalidate();
        }

        /// <summary>Self-test: a left-button drag (or click) between two map points, as if done with the mouse.</summary>
        internal void Simulate(V2 from, V2 to)
        {
            var a = ToScreen(from.X, from.Y); var b = ToScreen(to.X, to.Y);
            var pa = new Point((int)Math.Round(a.X), (int)Math.Round(a.Y)); var pb = new Point((int)Math.Round(b.X), (int)Math.Round(b.Y));
            OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, pa.X, pa.Y, 0));
            OnMouseMove(new MouseEventArgs(MouseButtons.Left, 0, (pa.X + pb.X) / 2, (pa.Y + pb.Y) / 2, 0));
            OnMouseMove(new MouseEventArgs(MouseButtons.Left, 0, pb.X, pb.Y, 0));
            OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, pb.X, pb.Y, 0));
        }

        /// <summary>Self-test: a double-click at a map point.</summary>
        internal void SimulateDoubleClick(V2 at)
        {
            var a = ToScreen(at.X, at.Y);
            var p = new Point((int)Math.Round(a.X), (int)Math.Round(a.Y));
            OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, p.X, p.Y, 0));
            OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, p.X, p.Y, 0));
            OnMouseDown(new MouseEventArgs(MouseButtons.Left, 2, p.X, p.Y, 0));
            OnMouseUp(new MouseEventArgs(MouseButtons.Left, 2, p.X, p.Y, 0));
        }

        /// <summary>Escape cancels a drag or a shape in progress.</summary>
        public bool CancelDrag()
        {
            if (_dragging || _arcBending || _poly.Count > 0) { CancelPending(); Capture = false; return true; }
            if (_moving) { _moving = false; Capture = false; _ed.Revert(); return true; }
            if (_handle != null) { _handle = null; Capture = false; _ed.Revert(); return true; }
            return false;
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return (keyData & Keys.KeyCode) == Keys.Left || (keyData & Keys.KeyCode) == Keys.Right
                || (keyData & Keys.KeyCode) == Keys.Up || (keyData & Keys.KeyCode) == Keys.Down || base.IsInputKey(keyData);
        }

        /// <summary>Arrow keys nudge the selection by one snap step.</summary>
        public bool Nudge(Keys key)
        {
            if (_ed.Selected == null) return false;
            var step = _ed.Snap > 0f ? _ed.Snap : 0.05f;
            float dx = key == Keys.Left ? -step : key == Keys.Right ? step : 0f;
            float dy = key == Keys.Down ? -step : key == Keys.Up ? step : 0f;
            if (dx == 0f && dy == 0f) return false;
            if (_ed.Selected is MapShape) Editor.Move((MapShape)_ed.Selected, dx, dy);
            else { var o = (MapObject)_ed.Selected; o.X = Round(o.X + dx); o.Y = Round(o.Y + dy); }
            _ed.Commit();
            return true;
        }
    }
}
