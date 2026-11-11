using System.Collections.Generic;
using System.Linq;
using CP99Online.Net;
using UltraPuck;
using UnityEngine;

namespace CP99Online.Rounds
{
    /// <summary>
    /// Rounds Mode on screen: the card select screen (one row per player picking on this PC, so online each
    /// person only sees their own cards), the reveal, and everyone's cards in the lower-left corner
    /// (hover a card for details). Also drives CardGame on the host / offline.
    /// </summary>
    internal sealed class CardUI : MonoBehaviour
    {
        private readonly Dictionary<int, int> _cursor = new Dictionary<int, int>();
        private readonly Dictionary<int, float> _lastX = new Dictionary<int, float>();
        private bool _cursorForced, _cursorBefore;
        private Texture2D _white;
        private GUIStyle _title, _sub, _cardName, _cardText, _small, _chip, _tipName, _tipText;
        private string _tipTitle, _tipBody;

        private static bool InArena { get { return Globals.gameManager != null; } }

        private void Update()
        {
            if (!InArena) { RestoreCursor(); return; }
            if (CardGame.IsAuthority)
            {
                CardGame.Update();
                CardEffects.Tick(Time.deltaTime);
            }

            var picking = CardGame.PhaseActive && !CardGame.Revealing;
            if (CardGame.PhaseActive)
            {
                if (!_cursorForced) { _cursorForced = true; _cursorBefore = UnityEngine.Cursor.visible; }
                UnityEngine.Cursor.visible = true;
            }
            else RestoreCursor();

            if (picking && Time.realtimeSinceStartup - CardGame.PhaseStartedAt > CardGame.InputDelay) HandleInput();
            if (!CardGame.PhaseActive) { _cursor.Clear(); _lastX.Clear(); }
        }

        private void RestoreCursor()
        {
            if (!_cursorForced) return;
            _cursorForced = false;
            UnityEngine.Cursor.visible = _cursorBefore;
        }

        // ------------------------------------------------------------------ input

        private List<int> LocalPickers()
        {
            var list = new List<int>();
            for (var i = 0; i < CardGame.Seats.Count; i++)
            {
                var s = CardGame.Seats[i];
                if (CardGame.IsLocal(s) && s.Offer.Length > 0) list.Add(i);
            }
            return list;
        }

        private void HandleInput()
        {
            var pickers = LocalPickers();
            foreach (var i in pickers)
            {
                var s = CardGame.Seats[i];
                var bi = s.Ball != null ? s.Ball.GetComponent<BallInput>() : null;
                var cur = CursorOf(i, s);
                if (bi != null)
                {
                    var x = Axis(bi.axisName_x1);
                    float last;
                    _lastX.TryGetValue(i, out last);
                    if (x > 0.55f && last <= 0.55f) cur++;
                    if (x < -0.55f && last >= -0.55f) cur--;
                    _lastX[i] = x;
                    cur = Mathf.Clamp(cur, 0, s.Offer.Length - 1);
                    _cursor[i] = cur;
                    if (Down(bi.buttonName_shoot) || Down(bi.buttonName_shootAlt) || Down(bi.buttonName_charge) || Down(bi.buttonName_chargeAlt))
                        Pick(i, cur);
                }
            }
            // Keyboard shortcuts when exactly one person picks on this PC.
            if (pickers.Count == 1)
            {
                var i = pickers[0];
                var s = CardGame.Seats[i];
                var cur = CursorOf(i, s);
                if (Input.GetKeyDown(KeyCode.LeftArrow)) _cursor[i] = Mathf.Max(0, cur - 1);
                if (Input.GetKeyDown(KeyCode.RightArrow)) _cursor[i] = Mathf.Min(s.Offer.Length - 1, cur + 1);
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) Pick(i, CursorOf(i, s));
                for (var k = 0; k < s.Offer.Length && k < 9; k++)
                    if (Input.GetKeyDown(KeyCode.Alpha1 + k) || Input.GetKeyDown(KeyCode.Keypad1 + k)) Pick(i, k);
            }
        }

        private int CursorOf(int seat, Seat s)
        {
            int c;
            if (!_cursor.TryGetValue(seat, out c)) c = s.Choice >= 0 ? s.Choice : s.Offer.Length / 2;
            return Mathf.Clamp(c, 0, Mathf.Max(0, s.Offer.Length - 1));
        }

        private void Pick(int seat, int index)
        {
            _cursor[seat] = index;
            if (CardGame.IsAuthority) CardGame.Pick(seat, index, 0);
            else
            {
                CardGame.PickLocal(seat, index);
                NetSession.Instance.ClientSendCardPick(seat, index);
            }
        }

        private static float Axis(string n)
        {
            if (string.IsNullOrEmpty(n)) return 0f;
            try { return Input.GetAxisRaw(n); } catch { return 0f; }
        }

        private static bool Down(string n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            try { return Input.GetButtonDown(n); } catch { return false; }
        }

        // ------------------------------------------------------------------ drawing

        private void OnGUI()
        {
            if (!InArena || CardGame.Seats.Count == 0) return;
            Styles();
            _tipTitle = null;
            var u = Screen.height / 720f;

            if (CardGame.PhaseActive)
            {
                Fill(new Rect(0, 0, Screen.width, Screen.height), new Color(0f, 0f, 0f, 0.6f));
                if (CardGame.Revealing) DrawReveal(u); else DrawPicking(u);
            }

            var showPanel = CardGame.PhaseActive ||
                (RoundsMode.Instance != null && RoundsMode.Instance.ShowDuringPlay.Value && (RoundsMode.Active || !CardGame.IsAuthority));
            if (showPanel && CardGame.Seats.Any(s => s.Cards.Count > 0)) DrawPanel(u);

            if (_tipTitle != null) DrawTooltip(u);
        }

        private void DrawPicking(float u)
        {
            var team = CardGame.TeamMode;
            Label(new Rect(0, 26 * u, Screen.width, 50 * u), team ? "VOTE FOR YOUR TEAM'S CARD" : "PICK A CARD", _title);
            var left = CardGame.SecondsLeft;
            var sub = (team ? "Your team gets the card with the most votes (ties are random)." : "Your card lasts for the rest of the match.") +
                "   " + CardGame.PickedCount + "/" + CardGame.PickerCount + " picked" +
                (left >= 0f ? "   " + Mathf.CeilToInt(left) + "s" : string.Empty);
            Label(new Rect(0, 72 * u, Screen.width, 26 * u), sub, _sub);

            var rows = LocalPickers();
            if (rows.Count == 0)
            {
                Label(new Rect(0, Screen.height * 0.42f, Screen.width, 40 * u), "Waiting for the other players to pick...", _title);
                return;
            }
            var top = 112 * u;
            var avail = Screen.height * 0.70f;
            var rowH = avail / rows.Count;
            var cardH = Mathf.Min(320 * u, rowH - 36 * u);
            var cardW = cardH * 0.7f;
            var gap = 18 * u;
            for (var r = 0; r < rows.Count; r++)
            {
                var i = rows[r];
                var s = CardGame.Seats[i];
                var y = top + r * rowH;
                var status = s.Choice >= 0
                    ? (team ? "Voted: " : "Picked: ") + CardName(s.Offer[s.Choice]) + "  (you can still change it)"
                    : "Stick / arrows to choose, A / Space / Enter / 1-" + s.Offer.Length + " to pick, or click a card";
                var nameStyle = new GUIStyle(_sub) { alignment = TextAnchor.MiddleCenter };
                nameStyle.normal.textColor = TeamColor(s.Team);
                Label(new Rect(0, y, Screen.width, 26 * u), s.Name + "  -  " + status, nameStyle);

                var total = s.Offer.Length * cardW + (s.Offer.Length - 1) * gap;
                var x0 = (Screen.width - total) / 2f;
                var cur = CursorOf(i, s);
                for (var k = 0; k < s.Offer.Length; k++)
                {
                    var rect = new Rect(x0 + k * (cardW + gap), y + 30 * u, cardW, cardH);
                    var votes = team ? CardGame.Seats.Count(o => o.Team == s.Team && o.Offer == s.Offer && o.Choice == k) : 0;
                    var footer = team ? (votes == 1 ? "1 vote" : votes + " votes") : (k + 1).ToString();
                    var selected = k == cur;
                    var chosen = k == s.Choice;
                    if (DrawCard(rect, s.Offer[k], selected, chosen, s.Choice >= 0 && !chosen, footer, u)) Pick(i, k);
                }
            }
        }

        private void DrawReveal(float u)
        {
            Label(new Rect(0, 26 * u, Screen.width, 50 * u), "NEW CARDS", _title);
            var entries = CardGame.TeamMode
                ? CardGame.Seats.Where(s => s.Last != null).GroupBy(s => s.Team).Select(g => new { Who = "Team " + (g.Key + 1), Team = g.Key, Card = g.First().Last }).ToList()
                : CardGame.Seats.Where(s => s.Last != null).Select(s => new { Who = s.Name, Team = s.Team, Card = s.Last }).ToList();
            if (entries.Count == 0) return;
            var perRow = Mathf.Min(entries.Count, 4);
            var rowsN = (entries.Count + perRow - 1) / perRow;
            var cardH = Mathf.Min(240 * u, (Screen.height * 0.72f) / rowsN - 34 * u);
            var cardW = cardH * 0.7f;
            var gap = 22 * u;
            for (var e = 0; e < entries.Count; e++)
            {
                var row = e / perRow;
                var inRow = Mathf.Min(perRow, entries.Count - row * perRow);
                var x0 = (Screen.width - (inRow * cardW + (inRow - 1) * gap)) / 2f;
                var x = x0 + (e % perRow) * (cardW + gap);
                var y = 100 * u + row * (cardH + 34 * u);
                var who = new GUIStyle(_sub) { alignment = TextAnchor.MiddleCenter };
                who.normal.textColor = TeamColor(entries[e].Team);
                Label(new Rect(x - gap / 2, y, cardW + gap, 26 * u), entries[e].Who, who);
                DrawCard(new Rect(x, y + 28 * u, cardW, cardH), entries[e].Card, false, true, false, null, u);
            }
        }

        /// <summary>Everyone's cards, lower-left. Hover a card for its description.</summary>
        private void DrawPanel(float u)
        {
            var seats = CardGame.Seats;
            var lineH = 22 * u;
            var h = 30 * u + seats.Count * (lineH + 4 * u);
            // As wide as the longest row of cards (capped).
            var widest = 0f;
            foreach (var s in seats)
            {
                var rowW = 0f;
                foreach (var g in s.Cards.GroupBy(id => id))
                    rowW += _chip.CalcSize(new GUIContent(CardName(g.Key) + (g.Count() > 1 ? " x" + g.Count() : string.Empty))).x + 15 * u;
                widest = Mathf.Max(widest, rowW);
            }
            var w = Mathf.Min(Mathf.Max(Screen.width * 0.5f, 200 * u), 140 * u + widest);
            var box = new Rect(10 * u, Screen.height - h - 10 * u, w, h);
            Fill(box, new Color(0f, 0f, 0f, CardGame.PhaseActive ? 0.75f : 0.45f));
            Label(new Rect(box.x + 8 * u, box.y + 4 * u, w, 22 * u), "CARDS", _small);
            var mouse = Event.current.mousePosition;
            var y = box.y + 28 * u;
            foreach (var s in seats)
            {
                var name = new GUIStyle(_small);
                name.normal.textColor = TeamColor(s.Team);
                var label = s.Name + (CardGame.PhaseActive && !CardGame.Revealing && s.Human && s.Offer.Length > 0 ? (s.Choice >= 0 ? " *" : " ...") : string.Empty);
                Label(new Rect(box.x + 8 * u, y, 120 * u, lineH), label, name);
                var x = box.x + 132 * u;
                foreach (var g in s.Cards.GroupBy(id => id))
                {
                    var text = CardName(g.Key) + (g.Count() > 1 ? " x" + g.Count() : string.Empty);
                    var size = _chip.CalcSize(new GUIContent(text));
                    var chip = new Rect(x, y + 1 * u, size.x + 10 * u, lineH - 2 * u);
                    if (chip.xMax > box.xMax - 6 * u) { Label(new Rect(x, y, 30 * u, lineH), "...", _small); break; }
                    var c = CardColor(g.Key);
                    var hover = chip.Contains(mouse);
                    Fill(chip, new Color(c.r * 0.55f, c.g * 0.55f, c.b * 0.55f, hover ? 1f : 0.9f));
                    if (hover) Outline(chip, Color.white, Mathf.Max(1f, u));
                    Label(chip, text, _chip);
                    if (hover)
                    {
                        _tipTitle = CardName(g.Key) + (g.Count() > 1 ? "  x" + g.Count() : string.Empty);
                        _tipBody = CardText(g.Key) + "\n" + s.Name;
                    }
                    x = chip.xMax + 5 * u;
                }
                y += lineH + 4 * u;
            }
        }

        private void DrawTooltip(float u)
        {
            var w = 260 * u;
            var body = _tipText.CalcHeight(new GUIContent(_tipBody), w - 16 * u);
            var h = 34 * u + body + 8 * u;
            var m = Event.current.mousePosition;
            var r = new Rect(Mathf.Min(m.x + 14 * u, Screen.width - w - 4), Mathf.Max(4, m.y - h - 8 * u), w, h);
            Fill(r, new Color(0.08f, 0.08f, 0.1f, 0.96f));
            Outline(r, new Color(1f, 1f, 1f, 0.5f), Mathf.Max(1f, u));
            Label(new Rect(r.x + 8 * u, r.y + 6 * u, w - 16 * u, 24 * u), _tipTitle, _tipName);
            Label(new Rect(r.x + 8 * u, r.y + 32 * u, w - 16 * u, body), _tipBody, _tipText);
        }

        /// <summary>Draws one card; returns true if it was clicked.</summary>
        private bool DrawCard(Rect r, string id, bool selected, bool chosen, bool dim, string footer, float u)
        {
            if (selected) r = new Rect(r.x - r.width * 0.03f, r.y - r.height * 0.03f, r.width * 1.06f, r.height * 1.06f);
            var c = CardColor(id);
            var a = dim ? 0.45f : 1f;
            Fill(r, new Color(0.11f, 0.12f, 0.15f, 0.97f * (dim ? 0.8f : 1f)));
            var head = new Rect(r.x, r.y, r.width, r.height * 0.26f);
            Fill(head, new Color(c.r, c.g, c.b, a));
            var nameStyle = new GUIStyle(_cardName) { fontSize = Mathf.RoundToInt(r.height * 0.075f) };
            Label(new Rect(head.x + 6 * u, head.y, head.width - 12 * u, head.height), CardName(id), nameStyle);
            var textStyle = new GUIStyle(_cardText) { fontSize = Mathf.Max(10, Mathf.RoundToInt(r.height * 0.052f)) };
            textStyle.normal.textColor = new Color(0.9f, 0.92f, 0.95f, a);
            Label(new Rect(r.x + 10 * u, head.yMax + 10 * u, r.width - 20 * u, r.height * 0.55f), CardText(id), textStyle);
            if (!string.IsNullOrEmpty(footer))
            {
                var f = new GUIStyle(_small) { alignment = TextAnchor.MiddleCenter, fontSize = Mathf.Max(10, Mathf.RoundToInt(r.height * 0.05f)) };
                Label(new Rect(r.x, r.yMax - r.height * 0.12f, r.width, r.height * 0.1f), footer, f);
            }
            if (chosen) Outline(r, new Color(1f, 0.85f, 0.25f), 4 * u);
            else if (selected) Outline(r, Color.white, 3 * u);

            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition))
            {
                e.Use();
                return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ helpers

        private static string CardName(string id) { var c = UltraPuck.Cards.Get(id); return c != null ? c.Name : id; }
        private static string CardText(string id) { var c = UltraPuck.Cards.Get(id); return c != null ? c.Description : "A card from a mod you don't have."; }
        private static Color CardColor(string id) { var c = UltraPuck.Cards.Get(id); return c != null ? c.Color : Color.gray; }

        private static Color TeamColor(int team)
        {
            var gm = Globals.gameManager;
            if (gm != null && team >= 0 && team < gm.teams.Count)
            {
                try { var c = ColorManager.GetColor(gm.teams[team].teamColor); return Color.Lerp(c, Color.white, 0.25f); } catch { }
            }
            return Color.white;
        }

        private void Fill(Rect r, Color c)
        {
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, _white);
            GUI.color = old;
        }

        private void Outline(Rect r, Color c, float t)
        {
            Fill(new Rect(r.x, r.y, r.width, t), c);
            Fill(new Rect(r.x, r.yMax - t, r.width, t), c);
            Fill(new Rect(r.x, r.y, t, r.height), c);
            Fill(new Rect(r.xMax - t, r.y, t, r.height), c);
        }

        private static void Label(Rect r, string text, GUIStyle style) { GUI.Label(r, text, style); }

        private void Styles()
        {
            var u = Screen.height / 720f;
            if (_white == null)
            {
                _white = new Texture2D(1, 1);
                _white.SetPixel(0, 0, Color.white);
                _white.Apply();
            }
            if (_title == null)
            {
                _title = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
                _sub = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
                _cardName = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, wordWrap = true };
                _cardText = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperCenter, wordWrap = true };
                _small = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold };
                _chip = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, padding = new RectOffset(0, 0, 0, 0) };
                _tipName = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
                _tipText = new GUIStyle(GUI.skin.label) { wordWrap = true };
                foreach (var s in new[] { _title, _sub, _cardName, _cardText, _small, _chip, _tipName, _tipText }) s.normal.textColor = Color.white;
                _tipText.normal.textColor = new Color(0.85f, 0.87f, 0.9f);
            }
            _title.fontSize = Mathf.RoundToInt(30 * u);
            _sub.fontSize = Mathf.RoundToInt(16 * u);
            _small.fontSize = Mathf.RoundToInt(13 * u);
            _chip.fontSize = Mathf.RoundToInt(12 * u);
            _tipName.fontSize = Mathf.RoundToInt(16 * u);
            _tipText.fontSize = Mathf.RoundToInt(13 * u);
        }
    }
}
