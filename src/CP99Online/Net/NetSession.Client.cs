using System;
using System.Collections.Generic;
using System.Linq;
using Steamworks;
using UnityEngine;

namespace CP99Online.Net
{
    internal sealed partial class NetSession
    {
        internal struct LobbyEntry
        {
            public byte Id;
            public string Name;
            public int Team; // 255 = host
        }

        private sealed class Snap
        {
            public float Time;
            public byte Flags;
            public float TimeScale, Fader, TimeLeft;
            public byte Score0, Score1;
            public float[] X, Y, Rot;
            public byte[] BallFlags, Charge, Teleport;
            public float PuckX, PuckY, PuckRot;
            public int Carrier;
            public bool Disrespect;
            public byte PuckTeleport;
        }

        /// <summary>The longest clients ever draw the world in the past (a very wobbly connection).</summary>
        internal const float InterpDelay = 0.2f;

        // ---- playback clock
        // Clients draw the world a little in the past, blending between the two snapshots around that moment. The
        // moment is "host time now, minus a small buffer": host time comes from a clock offset that follows the
        // fastest-arriving snapshots and only drifts gently, so a packet that's a few ms late or early never makes
        // the picture jump. The buffer is sized from how much the connection's timing actually wobbles.
        private const float MinDelay = 0.035f, MaxDelay = InterpDelay, ClockWindow = 2f;
        private readonly List<KeyValuePair<float, float>> _clockSamples = new List<KeyValuePair<float, float>>(); // (local receive time, host time - local time)
        private readonly List<float> _sortScratch = new List<float>();
        private float _clockOffset, _renderDelay = 0.1f, _lastRenderTime;
        private bool _clockReady;
        private float _clockLogAt;

        /// <summary>Client: the host time the world is drawn at right now (shots and players use the same clock).</summary>
        internal float RenderTime { get { return _lastRenderTime; } }

        /// <summary>Client: how far in the past the world is drawn right now, in seconds.</summary>
        internal float RenderDelay { get { return _renderDelay; } }

        private void ClockReset() { _clockSamples.Clear(); _clockReady = false; _renderDelay = 0.1f; _lastRenderTime = 0f; }

        private void ClockSample(float hostTime, float now)
        {
            _clockSamples.Add(new KeyValuePair<float, float>(now, hostTime - now));
            while (_clockSamples.Count > 0 && now - _clockSamples[0].Key > ClockWindow) _clockSamples.RemoveAt(0);
        }

        /// <summary>The playback time for this frame (host clock), moving smoothly forward.</summary>
        private float ClockRenderTime(float now)
        {
            if (_clockSamples.Count == 0) return _lastRenderTime;
            // the fastest packets show the true offset; slower ones were just delayed on the way
            var best = float.MinValue;
            foreach (var s in _clockSamples) if (s.Value > best) best = s.Value;
            // the timing wobble: how far behind the fastest packets the slow ones arrive (95th percentile)
            _sortScratch.Clear();
            foreach (var s in _clockSamples) _sortScratch.Add(best - s.Value);
            _sortScratch.Sort();
            var jitter = _sortScratch[Mathf.Min(_sortScratch.Count - 1, (int)(_sortScratch.Count * 0.95f))];
            var wantDelay = Mathf.Clamp(Proto.SendRate * 1.5f + jitter + 0.01f, MinDelay, MaxDelay);

            var dt = Mathf.Max(0f, now - _clockNow);
            _clockNow = now;
            if (!_clockReady || Mathf.Abs(best - _clockOffset) > 0.25f)
            {
                _clockOffset = best; _renderDelay = wantDelay; _clockReady = true; // first snapshot, or a big jump (the host paused / hitched)
                _lastRenderTime = now + _clockOffset - _renderDelay;
                return _lastRenderTime;
            }
            // drift gently: the offset follows the fastest packets, the buffer grows fast and shrinks slowly
            _clockOffset = Mathf.MoveTowards(_clockOffset, best, dt * 0.05f);
            _renderDelay = Mathf.MoveTowards(_renderDelay, wantDelay, dt * (wantDelay > _renderDelay ? 0.5f : 0.02f));
            var rt = now + _clockOffset - _renderDelay;
            if (rt < _lastRenderTime) rt = _lastRenderTime; // never backwards
            _lastRenderTime = rt;
            if (now > _clockLogAt)
            {
                _clockLogAt = now + 10f;
                NetLog.Info("Net clock: drawing " + Mathf.RoundToInt(_renderDelay * 1000f) + " ms in the past (timing wobble " + Mathf.RoundToInt(jitter * 1000f) + " ms)");
            }
            return rt;
        }

        private float _clockNow;
        private readonly byte[] _teleportSeen = new byte[32];

        // Tests: how smoothly the playback clock moves. Each frame it should move forward exactly as much real time
        // passed; the average mismatch is how much the picture stutters. The old clock (re-anchored on every
        // snapshot, 100 ms behind) is measured alongside for comparison.
        private float _smPrevOld = -1f, _smPrevNew = -1f, _smPrevNow, _smErrOld, _smErrNew, _smLogAt;
        private int _smFrames;

        // Tests: how evenly players move frame to frame (change in the per-frame step; lower = smoother), own vs others.
        private readonly Vector3[] _mPrev = new Vector3[32], _mStep = new Vector3[32];
        private float _mOwn, _mOther; private int _mOwnN, _mOtherN;

        // Tests: how far from the truth other players are drawn, at the moment your own player is shown (the host's
        // present). Each frame remembers where they were drawn; once snapshots for that moment arrive, compare.
        private struct Drawn { public float T; public int I; public Vector2 P; }
        private readonly List<Drawn> _drawn = new List<Drawn>();
        private float _truthErr, _truthMax; private int _truthN;

        private void MeasureTruth(int i, Vector3 pos)
        {
            _drawn.Add(new Drawn { T = RenderTime + _renderDelay, I = i, P = pos });
            var latest = _snaps[_snaps.Count - 1].Time;
            var k = 0;
            for (; k < _drawn.Count && _drawn[k].T <= latest; k++)
            {
                Snap a, b; float t;
                Find(_drawn[k].T, false, out a, out b, out t);
                var d = _drawn[k];
                if (d.I >= a.X.Length || d.I >= b.X.Length || a.Teleport[d.I] != b.Teleport[d.I]) continue;
                var truth = new Vector2(Mathf.Lerp(a.X[d.I], b.X[d.I], t), Mathf.Lerp(a.Y[d.I], b.Y[d.I], t));
                var e = Vector2.Distance(truth, d.P);
                if (e > 3f) continue; // a respawn
                _truthErr += e; _truthN++; if (e > _truthMax) _truthMax = e;
            }
            if (k > 0) _drawn.RemoveRange(0, k);
            if (_drawn.Count > 4000) _drawn.Clear();
        }

        private void MeasureMotion(int i, bool own, Vector3 pos)
        {
            if (!IsLocal(i) && _snaps.Count > 0) MeasureTruth(i, pos);
            if (i >= _mPrev.Length) return;
            var step = pos - _mPrev[i];
            if (_mPrev[i] != Vector3.zero && step.magnitude < 2f)
            {
                var jerk = (step - _mStep[i]).magnitude;
                if (own) { _mOwn += jerk; _mOwnN++; } else { _mOther += jerk; _mOtherN++; }
            }
            _mPrev[i] = pos; _mStep[i] = step;
        }

        private void MeasureSmoothness(float oldTime, float newTime)
        {
            var now = Now;
            if (_smPrevOld >= 0f && _snaps.Count > 2)
            {
                var dt = now - _smPrevNow;
                _smErrOld += Mathf.Abs((oldTime - _smPrevOld) - dt);
                _smErrNew += Mathf.Abs((newTime - _smPrevNew) - dt);
                _smFrames++;
            }
            _smPrevOld = oldTime; _smPrevNew = newTime; _smPrevNow = now;
            if (_smFrames >= 300 && now > _smLogAt)
            {
                _smLogAt = now + 5f;
                NetLog.Info("TEST smoothness: clock stutter per frame old " + (_smErrOld / _smFrames * 1000f).ToString("F2") + " ms, new " +
                    (_smErrNew / _smFrames * 1000f).ToString("F2") + " ms; drawing " + Mathf.RoundToInt(_renderDelay * 1000f) + " ms behind (old: 100 ms); " +
                    "motion unevenness own " + (_mOwnN > 0 ? (_mOwn / _mOwnN * 1000f).ToString("F1") : "-") + ", others " + (_mOtherN > 0 ? (_mOther / _mOtherN * 1000f).ToString("F1") : "-") + " (mm per frame)");
                if (_truthN > 0) NetLog.Info("TEST opponents vs the host's present: off by " + (_truthErr / _truthN).ToString("F2") + " on average, at most " + _truthMax.ToString("F2") + " (units; a player is ~0.8 across)" + (LiveOpponentsOn ? " [live]" : " [buffered]"));
                _truthErr = _truthMax = 0f; _truthN = 0;
                _mOwn = _mOther = 0f; _mOwnN = _mOtherN = 0;
                _smErrOld = _smErrNew = 0f; _smFrames = 0;
            }
        }

        public readonly List<LobbyEntry> Lobby = new List<LobbyEntry>();
        public string HostName { get; private set; }
        public bool Welcomed { get; private set; }

        private byte _myId;
        private float _connectStarted;
        private readonly List<Snap> _snaps = new List<Snap>();
        private float _latestSnapRecv;
        private uint _latestSnapSeq;
        private bool _clientHold;
        private bool _wasReplaying;
        private float _hostTimeLeft = 999f;

        // Local input, accumulated every frame and sent at Proto.SendRate, for each player on this PC.
        private sealed class Pad
        {
            public uint Seq;
            public readonly byte[] DownCount = new byte[3];
            public byte ChargeUpCount;
            public bool PrevCharging;
        }
        private readonly Dictionary<int, Pad> _pads = new Dictionary<int, Pad>();
        private float _inputAccum;

        private Pad PadOf(int slot)
        {
            Pad p;
            if (!_pads.TryGetValue(slot, out p)) _pads[slot] = p = new Pad();
            return p;
        }

        public static float HostTimeLeft { get { return Instance != null ? Instance._hostTimeLeft : 999f; } }
        public static bool ClientHoldingStart { get { return Instance != null && Instance._clientHold; } }

        // ---------------------------------------------------------------- start / stop

        public void JoinDirect(string host, int port)
        {
            if (Role != Role.None) return;
            try
            {
                _transport = LagTransport.Wrap(UdpTransport.Connect(host, port));
            }
            catch (Exception e)
            {
                Status = "Could not connect: " + e.Message;
                NetLog.Warn(Status);
                return;
            }
            BeginClient(host + ":" + port);
        }

        public void JoinSteam(CSteamID host)
        {
            if (Role != Role.None) return;
            _transport = SteamTransport.Connect(host);
            BeginClient("Steam host");
        }

        private void BeginClient(string where)
        {
            Role = Role.Client;
            Welcomed = false;
            var c = new Connection(_transport, 0, Now);
            _conns[0] = c;
            Begin(Msg.Hello);
            _msg.U16(Proto.Version);
            _msg.Str(LocalName());
            _msg.U8(Plugin.OnlineTeam.Value);
            c.SendReliable(_msg, Now);
            _connectStarted = Now;
            Status = "Connecting to " + where + "...";
            NetLog.Info(Status);
        }

        /// <summary>Client: ask the host to bring everyone to player select (it does when it's on its main menu).</summary>
        internal void ClientGoToPlayerSelect()
        {
            if (!IsClient || _conns.Count == 0) return;
            Begin(Msg.GoToPlayerSelect);
            _conns.Values.First().SendReliable(_msg, Now);
            NetLog.Info("Asked the host to go to player select");
        }

        private void ClientReset()
        {
            Lobby.Clear();
            _snaps.Clear();
            ClockReset();
            Welcomed = false;
            _clientHold = false;
            _hostTimeLeft = 999f;
        }

        public void RequestTeam(int team)
        {
            Plugin.OnlineTeam.Value = team;
            if (Role != Role.Client) return;
            Begin(Msg.SetTeam);
            _msg.U8(team);
            _conns[0].SendReliable(_msg, Now);
        }

        // ---------------------------------------------------------------- messages

        private void ClientHandle(Connection c, Msg type, Reader r)
        {
            switch (type)
            {
                case Msg.Welcome:
                    _myId = r.U8();
                    HostName = r.Str();
                    Welcomed = true;
                    Status = "Connected to " + HostName + " - waiting for the host to start a match";
                    NetLog.Info(Status + " (client id " + _myId + ")");
                    Toast.Show("Joined " + HostName + "'s game");
                    break;
                case Msg.Reject:
                    Stop("Host refused: " + r.Str());
                    Toast.Show(Status);
                    break;
                case Msg.Bye:
                    r.Str();
                    Stop((string.IsNullOrEmpty(HostName) ? "The host" : HostName) + " ended the game");
                    Toast.Show(Status);
                    break;
                case Msg.Lobby:
                {
                    var before = Lobby.ToDictionary(e => e.Id, e => e.Name);
                    Lobby.Clear();
                    int n = r.U8();
                    for (var i = 0; i < n; i++)
                        Lobby.Add(new LobbyEntry { Id = r.U8(), Name = r.Str(), Team = r.U8() });
                    // Tell this player when someone else joins or leaves (not for the first list after joining).
                    if (before.Count > 0)
                    {
                        foreach (var e in Lobby)
                            if (e.Id != 0 && e.Id != _myId && !before.ContainsKey(e.Id)) Toast.Show(e.Name + " joined");
                        foreach (var kv in before)
                            if (kv.Key != 0 && kv.Key != _myId && Lobby.All(e => e.Id != kv.Key)) Toast.Show(kv.Value + " left");
                    }
                    break;
                }
                case Msg.BoxAssign:
                    ClientHandleBoxAssign(r);
                    break;
                case Msg.BoxTable:
                    ClientHandleBoxTable(r);
                    break;
                case Msg.Scene:
                    ClientHandleScene(r);
                    break;
                case Msg.CardState:
                    ClientHandleCards(r);
                    break;
                case Msg.CardShot:
                    ClientHandleShot(r);
                    break;
                case Msg.Pylons:
                    ClientHandlePylons(r);
                    break;
                case Msg.Possess:
                    {
                        var n = r.U8();
                        var state = new List<KeyValuePair<int, int>>();
                        for (var i = 0; i < n; i++) { int home = r.U8(); state.Add(new KeyValuePair<int, int>(home, r.U8())); }
                        if (Match.Active) Possession.ClientApply(state);
                    }
                    break;
                case Msg.MapData:
                    ClientHandleMap(r);
                    break;
                case Msg.Nav:
                    _navMenu = r.Str();
                    _navElement = r.Str();
                    _navGoals = r.I32();
                    _navLength = r.F32();
                    _hasNav = true;
                    break;
                case Msg.StartMatch:
                    ClientStartMatch(r);
                    break;
                case Msg.Snapshot:
                    if (Match.Active) ReadSnapshot(r);
                    break;
                case Msg.Events:
                    if (Match.Active) ApplyEvents(r);
                    break;
                case Msg.Goal:
                    if (Match.Active) ClientGoal(r.U8(), (Goal.GoalType)r.U8());
                    break;
                case Msg.ResetField:
                    if (Match.Active) ClientResetField(r.Bool());
                    break;
                case Msg.SuddenDeath:
                    if (Match.Active && Globals.gameManager != null && Globals.gameManager.guiTimer != null)
                        Globals.gameManager.guiTimer.SuddenDeath();
                    break;
                case Msg.TimeUp:
                    if (Match.Active) ClientTimeUp();
                    break;
                case Msg.MatchEnd:
                    if (Match.Active) ClientMatchEnd(r);
                    break;
            }
        }

        private void ClientStartMatch(Reader r)
        {
            var id = r.U8();
            var arena = r.Str();
            var mode = r.U8();
            var goals = r.I32();
            var length = r.F32();
            var c1 = r.U8();
            var c2 = r.U8();
            var noSpikes = r.Bool();
            int n = r.U8();

            var players = new List<PlayerData>();
            var owners = new List<byte>();
            var local = -1;
            var locals = new List<int>();
            for (var i = 0; i < n; i++)
            {
                var team = r.U8();
                var color = r.U8();
                var ai = r.Bool();
                var name = r.Str();
                var symbol = r.U8();
                var owner = r.U8();
                var localIndex = r.U8();
                var mine = owner == _myId;
                var p = new PlayerData(ai, 0, mine ? MatchJoystick(localIndex) : 0, team)
                {
                    teamColor = (TeamColor)color,
                    account = new Account(name, ai, symbol)
                };
                if (mine) { if (local < 0) local = i; locals.Add(i); }
                players.Add(p);
                owners.Add(owner);
            }

            ArenaManager.arenaName = arena;
            if (arena == "0000_Custom")
            {
                if (Maps.MapLibrary.Incoming != null) ArenaCreatorManager.currentArena = Maps.MapLibrary.StandIn(Maps.MapLibrary.Incoming, 9999);
                else NetLog.Warn("The host picked a custom arena but its map didn't arrive");
            }
            ArenaManager.noSpikes = noSpikes;
            GameModeManager.gameMode = (GameModeManager.GameMode)mode;
            NumGoalsToWinManager.numGoalsToWin = goals;
            GameTimerManager.gameLength = length;
            ColorManager.team1Color = (TeamColor)c1;
            ColorManager.team2Color = (TeamColor)c2;
            ChallengeManager.isInChallenge = false;
            ChallengeManager.currentChallenge = new Challenge(Challenge.ChallengeType.SPARKLETEAM, string.Empty, string.Empty,
                string.Empty, string.Empty, string.Empty, Challenge.ScoreType.HIGHSCORE, new int[3], Challenge.ChallengeType.SPARKLETEAM);
            ChallengeMatch_Manager.challengeMatch = false;
            ArenaCreatorManager.comingFromArenaCreator = false;
            if (AIDifficultyManager.skill_shooting == null)
            {
                AIDifficultyManager.skill_shooting = new int[2];
                AIDifficultyManager.skill_aggression = new int[2];
                AIDifficultyManager.skill_agility = new int[2];
                AIDifficultyManager.skill_goaltending = new int[2];
            }

            Match.End();
            Match.Id = id;
            Match.Players = players;
            Match.Owners = owners;
            Match.LocalSlot = local;
            Match.LocalSlots.Clear();
            Match.LocalSlots.AddRange(locals);
            _pads.Clear();
            Possession.ClientSwitchCounts.Clear();
            Match.ClientPending = true;
            Match.OnlinePlayers.Clear();
            foreach (var p in players) Match.OnlinePlayers.Add(p);
            PlayerSelectionManager_V2.players = players;

            _snaps.Clear();
            ClockReset();
            _latestSnapSeq = 0;
            _wasReplaying = false;
            Time.timeScale = 1f;
            Status = "Loading " + arena + "...";
            NetLog.Info("Host started match " + id + " on " + arena + " (" + n + " players, mine: " + (locals.Count > 0 ? string.Join(", ", locals.Select(x => x.ToString()).ToArray()) : "none") + ")");
            Application.LoadLevel("Arena3D");
        }

        private byte[][] _mapParts;
        private uint _mapHash;

        /// <summary>A piece of the host's custom map; once all are in, it becomes the map for the next match.</summary>
        private void ClientHandleMap(Reader r)
        {
            var hash = r.U32();
            int index = r.U16(), count = r.U16(), len = r.U16();
            var data = r.Raw(len);
            if (_mapParts == null || _mapHash != hash || _mapParts.Length != count) { _mapParts = new byte[count][]; _mapHash = hash; }
            if (index < count) _mapParts[index] = data;
            if (_mapParts.Any(p => p == null)) return;
            var all = _mapParts.SelectMany(p => p).ToArray();
            _mapParts = null;
            var text = System.Text.Encoding.UTF8.GetString(all);
            if (Maps.MapData.Hash(text) != hash) { NetLog.Warn("The host's map arrived damaged"); return; }
            try
            {
                var map = Maps.MapData.Parse(text);
                Maps.MapLibrary.Incoming = map;
                string saved = null;
                try { saved = Maps.MapLibrary.Save(map, Maps.MapLibrary.DownloadFolder); } catch (Exception e) { NetLog.Warn("Couldn't save the host's map: " + e.Message); }
                NetLog.Info("Got the host's map \"" + map.Name + "\" (" + all.Length + " bytes)" + (saved != null ? ", saved to " + saved : string.Empty));
            }
            catch (FormatException e) { NetLog.Warn("Can't read the host's map: " + e.Message); }
        }

        /// <summary>GameManager.SpawnPlayers postfix (client): makes every ball a display-only puppet.</summary>
        internal void ClientOnSpawned(GameManager gm, int[] countsBefore)
        {
            if (!Match.ClientPending || !Match.Bind(gm, countsBefore)) return;
            Match.ClientPending = false;

            for (var s = 0; s < Match.Balls.Length; s++)
            {
                var b = Match.Balls[s];
                var local = Match.LocalSlots.Contains(s);
                b.GetComponent<BallInput>().enabled = local; // local one still reads this PC's controls
                b.GetComponent<BallAI_v2>().enabled = false;
                b.GetComponent<BallMovement>().enabled = false;
                b.GetComponent<BallHandling>().enabled = false;
                b.GetComponent<BallShove>().enabled = false;
                b.GetComponent<BallSprint>().enabled = false;
                b.GetComponent<BallFall>().enabled = false;
                b.GetComponent<BallDeathRespawn>().enabled = false;
                MakePuppet(b.gameObject);
            }
            MakePuppet(gm.puck);
            gm.puck.GetComponent<Puck>().enabled = true;

            Match.Active = true;
            _clientHold = true;
            Status = "In match (connected to " + HostName + ")";

            Begin(Msg.Loaded);
            _msg.U8(Match.Id);
            _conns[0].SendReliable(_msg, Now);
            NetLog.Info("Arena loaded, " + Match.Balls.Length + " balls bound");
        }

        internal static void MakePuppet(GameObject go)
        {
            var rb = go.GetComponent<Rigidbody2D>();
            if (rb != null) { rb.isKinematic = true; rb.velocity = Vector2.zero; }
            var col = go.GetComponent<Collider2D>();
            if (col != null) col.enabled = false;
        }

        // ---------------------------------------------------------------- per frame

        private void ClientUpdate()
        {
            Connection c;
            if (!_conns.TryGetValue(0, out c)) return;
            if (!Welcomed && Now - _connectStarted > Proto.Timeout) { Stop("Couldn't reach the host"); return; }
            if (Welcomed && Now - c.LastHeard > Proto.Timeout) { Stop("Lost connection to the host"); Toast.Show(Status); return; }
            LobbyClientUpdate();

            var gm = Globals.gameManager;
            if (Match.Active && gm != null && _clientHold) gm.startCountdown = 3.01f;

            if (!Match.Active) return;
            foreach (var slot in Match.LocalSlots) PollLocalInput(slot);
            _inputAccum += Time.unscaledDeltaTime;
            if (_inputAccum >= Proto.SendRate)
            {
                _inputAccum = Mathf.Min(_inputAccum - Proto.SendRate, Proto.SendRate);
                foreach (var slot in Match.LocalSlots) SendInput(c, slot);
            }
        }

        private static BallInput LocalInput(int slot)
        {
            var b = Match.Ball(slot);
            return b != null ? b.GetComponent<BallInput>() : null;
        }

        private void PollLocalInput(int slot)
        {
            var bi = LocalInput(slot);
            if (bi == null) return;
            var pad = PadOf(slot);
            if (TestHarness.DriveInput)
            {
                // Local test only: shoot/shove every 1.5s.
                if ((int)(Now / 1.5f) != (int)((Now - Time.unscaledDeltaTime) / 1.5f)) pad.DownCount[0]++;
                return;
            }
            if (Input.GetButtonDown(bi.buttonName_shoot) || Input.GetButtonDown(bi.buttonName_shootAlt)) pad.DownCount[0]++;
            if (Input.GetButtonDown(bi.buttonName_callPass) || bi.axisName_x2 == "Mouse X" && Input.GetKeyDown(KeyCode.F)) pad.DownCount[1]++;
            var charging = IsCharging(bi);
            if (charging && !pad.PrevCharging) pad.DownCount[2]++;
            if (!charging && pad.PrevCharging) pad.ChargeUpCount++;
            pad.PrevCharging = charging;
        }

        private static bool IsCharging(BallInput bi)
        {
            var axis = 0f;
            try { if (!string.IsNullOrEmpty(bi.axisName_charge)) axis = Input.GetAxis(bi.axisName_charge); } catch { }
            return axis > 0.5f || Input.GetButton(bi.buttonName_charge) || Input.GetButton(bi.buttonName_chargeAlt);
        }

        /// <summary>This PC's move and aim sticks for one of its players, as sent to the host (zero while paused).</summary>
        private void ReadSticks(int slot, out Vector2 move, out Vector2 aim, out byte held)
        {
            var bi = LocalInput(slot);
            var gm = Globals.gameManager;
            var paused = gm != null && gm.pauseMenuManager != null && gm.pauseMenuManager.paused;
            move = Vector2.zero;
            aim = Vector2.zero;
            held = 0;
            if (bi != null && !paused)
            {
                move = bi.axis1Input;
                aim = bi.axis2Input;
                if (bi.axisName_x2 == "Mouse X" && bi.axisName_y2 == "Mouse Y" && gm != null && gm.gameCam != null)
                {
                    // Same mouse-aim maths as BallHandling.Update, done here because BallHandling is off.
                    var body = Possession.BodyOf(bi.GetComponent<BallData>()); // after a bot switch, aim from the ball you control
                    var sp = gm.gameCam.GetComponent<Camera>().WorldToScreenPoint(body != null ? body.transform.position : bi.transform.position) - Input.mousePosition;
                    aim = new Vector2(sp.y, -sp.x);
                    if (aim.magnitude > 1f) aim = aim.normalized;
                }
                if (IsCharging(bi)) held |= Proto.HCharge;
                if (Input.GetButton(bi.buttonName_sprint) || Input.GetButton(bi.buttonName_sprintAlt)) held |= Proto.HSprint;
            }
            if (TestHarness.DriveInput)
            {
                move = new Vector2(Mathf.Cos(Now * 0.8f), Mathf.Sin(Now * 0.8f));
                aim = SpinTest ? new Vector2(Mathf.Cos(Now * 9f), Mathf.Sin(Now * 9f)) : move; // test: spin fast
                held = 0;
                if (TestHarness.BrakeTest && Mathf.Repeat(Now, 8f) < 0.4f) held |= Proto.HSprint; // brake now and then
            }
        }

        private static readonly bool SpinTest = System.Environment.GetEnvironmentVariable("CP99_TEST_SPIN") == "1";

        private void SendInput(Connection c, int slot)
        {
            var pad = PadOf(slot);
            Vector2 move, aim;
            byte held;
            ReadSticks(slot, out move, out aim, out held);
            Begin(Msg.Input);
            _msg.U8((byte)slot);
            _msg.U32(++pad.Seq);
            _msg.I16(Proto.PackUnit(move.x)); _msg.I16(Proto.PackUnit(move.y));
            _msg.I16(Proto.PackUnit(aim.x)); _msg.I16(Proto.PackUnit(aim.y));
            _msg.U8(held);
            for (var i = 0; i < 3; i++) _msg.U8(pad.DownCount[i]);
            _msg.U8(pad.ChargeUpCount);
            _msg.U8(Possession.ClientSwitchCountOf(slot));
            c.SendUnreliable(_msg);
        }

        private void ReadSnapshot(Reader r)
        {
            var seq = r.U32();
            if (_latestSnapSeq != 0 && (int)(seq - _latestSnapSeq) <= 0) return;
            _latestSnapSeq = seq;
            var s = new Snap
            {
                Time = r.F32(), Flags = r.U8(), TimeScale = r.F32(), Fader = r.F32(), TimeLeft = r.F32(),
                Score0 = r.U8(), Score1 = r.U8()
            };
            int n = r.U8();
            s.X = new float[n]; s.Y = new float[n]; s.Rot = new float[n];
            s.BallFlags = new byte[n]; s.Charge = new byte[n]; s.Teleport = new byte[n];
            for (var i = 0; i < n; i++)
            {
                s.X[i] = r.F32(); s.Y[i] = r.F32();
                s.Rot[i] = Proto.UnpackAngle(r.U16());
                s.BallFlags[i] = r.U8();
                s.Charge[i] = r.U8();
                s.Teleport[i] = r.U8();
            }
            s.PuckX = r.F32(); s.PuckY = r.F32();
            s.PuckRot = Proto.UnpackAngle(r.U16());
            var carrier = r.U8();
            s.Carrier = carrier == 255 ? -1 : carrier;
            s.Disrespect = r.Bool();
            s.PuckTeleport = r.U8();

            if (_latestSnapRecv > 0f && Now - _latestSnapRecv < 2f && Now - _latestSnapRecv > WorstSnapGap) WorstSnapGap = Now - _latestSnapRecv;
            _latestSnapRecv = Now;
            ClockSample(s.Time, Now);
            _snaps.Add(s);
            if (_snaps.Count > 40) _snaps.RemoveAt(0);
        }

        private void ClientLateUpdate()
        {
            if (!Match.Active || _snaps.Count == 0) return;
            var gm = Globals.gameManager;
            if (gm == null) return;

            // Render slightly in the past and interpolate between the two snapshots around that time.
            var latest = _snaps[_snaps.Count - 1];
            var renderTime = ClockRenderTime(Now);
            if (TestHarness.Active) MeasureSmoothness(latest.Time + (Now - _latestSnapRecv) - 0.1f, renderTime);
            Snap a, b, la, lb;
            float t, lt;
            Find(renderTime, false, out a, out b, out t);
            // This PC's own players skip the playback buffer: drawn at the host's present (the newest snapshot,
            // carried forward by its speed for the few ms since), so your own movement shows ~50 ms sooner. The buffer
            // only exists to blend other players smoothly; your own player doesn't need it.
            Find(renderTime + _renderDelay, true, out la, out lb, out lt);
            Apply(gm, a, b, t, la, lb, lt);
        }

        /// <summary>The two snapshots around host time <paramref name="time"/> and how far between them (past the newest one when extrapolating).</summary>
        private void Find(float time, bool extrapolate, out Snap a, out Snap b, out float t)
        {
            var latest = _snaps[_snaps.Count - 1];
            a = b = latest;
            t = 0f;
            if (_snaps.Count == 1) return;
            if (time >= latest.Time)
            {
                a = _snaps[_snaps.Count - 2];
                var span = latest.Time - a.Time;
                if (!extrapolate || span <= 0.0001f) { t = 1f; return; }
                t = 1f + EaseAhead(time - latest.Time) / span; // carry on at the last speed, easing off through a stall
                return;
            }
            for (var i = _snaps.Count - 2; i >= 0; i--)
            {
                if (_snaps[i].Time <= time)
                {
                    a = _snaps[i];
                    b = _snaps[i + 1];
                    var span = b.Time - a.Time;
                    t = span > 0.0001f ? Mathf.Clamp01((time - a.Time) / span) : 1f;
                    return;
                }
            }
            a = b = _snaps[0]; // older than everything we have
        }

        /// <summary>
        /// How far to carry something on past the newest snapshot: fully for the first 40 ms (normal timing), then
        /// less and less, levelling off at 140 ms. A late snapshot (the connection wobbling by 100+ ms) then looks
        /// like a slight slowdown instead of a freeze followed by a jump.
        /// </summary>
        private static float EaseAhead(float x)
        {
            const float full = 0.04f, tail = 0.1f;
            if (x <= full) return Mathf.Max(0f, x);
            return full + tail * (1f - Mathf.Exp(-(x - full) / tail));
        }

        private static bool IsLocal(int slot) { return Match.LocalSlots.Contains(slot); }

        // Own players are carried forward from the newest snapshot by their speed; each new snapshot corrects that
        // guess a little. Instead of showing the correction as a jump, it goes into an offset that fades out over
        // ~50 ms, so steady movement stays immediate and corrections blend in.
        private readonly Vector2[] _ownLast = new Vector2[32], _ownOffset = new Vector2[32], _ownVel = new Vector2[32];
        private readonly bool[] _ownInit = new bool[32];

        // Own players' facing is worked out here from this PC's own sticks, with the same turning the host does
        // (BallHandling: ease toward the aim stick, else the move stick, at 10x per second). It reacts the moment you
        // move the stick, instead of when the next snapshot happens to arrive, which on a bursty connection made
        // your player freeze and then jump. With the sticks let go it settles onto the host's facing.
        private readonly float[] _ownRot = new float[32], _ownRotAt = new float[32];

        // tests: while this PC carries the puck, how far the old straight-line guess would have put the puck from
        // where it's drawn now, and the biggest one-frame turn of the carrier
        private float _carryLogAt, _carryOldErr, _carryTurn, _carryLastRot = float.NaN;
        private int _carryFrames;
        private void MeasureCarry(Snap pb, Transform carrier)
        {
            Snap la, lb; float lt;
            Find(RenderTime + _renderDelay, true, out la, out lb, out lt);
            var old = new Vector2(Mathf.LerpUnclamped(la.PuckX, lb.PuckX, lt), Mathf.LerpUnclamped(la.PuckY, lb.PuckY, lt));
            _carryOldErr = Mathf.Max(_carryOldErr, Vector2.Distance(old, (Vector2)(carrier.position + carrier.up)));
            var z = carrier.eulerAngles.z;
            if (!float.IsNaN(_carryLastRot)) _carryTurn = Mathf.Max(_carryTurn, Mathf.Abs(Mathf.DeltaAngle(_carryLastRot, z)));
            _carryLastRot = z;
            _carryFrames++;
            if (Now < _carryLogAt) return;
            _carryLogAt = Now + 1f;
            NetLog.Info("TEST carry: " + _carryFrames + " frames, old guess was up to " + _carryOldErr.ToString("F2") + " units off, biggest turn in one frame " + _carryTurn.ToString("F1") + " deg");
            _carryOldErr = 0f; _carryTurn = 0f; _carryFrames = 0;
        }

        private float OwnFacing(int i, float hostRot, bool reset)
        {
            if (i >= _ownRot.Length) return hostRot;
            var dt = Mathf.Clamp(Time.deltaTime, 0f, 0.1f);
            if (reset || Now - _ownRotAt[i] > 0.5f) { _ownRotAt[i] = Now; return _ownRot[i] = hostRot; } // swap, respawn, new match
            _ownRotAt[i] = Now;
            Vector2 move, aim;
            byte held;
            ReadSticks(i, out move, out aim, out held);
            var idle = aim.magnitude <= 0.2f && move.magnitude <= 0.2f;
            if (aim.magnitude > 0.2f) _ownRot[i] = Mathf.LerpAngle(_ownRot[i], Mathf.Atan2(aim.y, aim.x) * Mathf.Rad2Deg + 180f, dt * 10f);
            else if (move.magnitude > 0.2f) _ownRot[i] = Mathf.LerpAngle(_ownRot[i], Mathf.Atan2(move.y, move.x) * Mathf.Rad2Deg - 90f, dt * 10f);
            else _ownRot[i] = Mathf.LerpAngle(_ownRot[i], hostRot, dt * 4f);
            if (idle && Mathf.Abs(Mathf.DeltaAngle(_ownRot[i], hostRot)) > 150f) _ownRot[i] = hostRot;
            return _ownRot[i];
        }

        // ---- own-movement prediction
        // This PC moves its own players itself, from its own sticks, with the game's movement rules (BallMovement:
        // push = stick x speed x 0.02 against the ball's drag; BallSprint: brake; BallShove: dash), so starting,
        // stopping, turning and dashing show the moment you do them instead of a round trip later. The host still
        // decides: each snapshot is compared with where this PC had the player at the moment the host had seen
        // those controls (about one ping ago), and the difference (a bump, a hit, a wall bounce, a card) is added
        // in and blended out on screen over ~80 ms.
        private sealed class Pred
        {
            public bool On;
            public Vector2 P, V, Offset;
            public float LastSnap = -1f, At, DashAt = -99f, Gain = 1f, FullStickFor;
            public byte DashSeen;
            public readonly List<Vector3> Hist = new List<Vector3>();  // x, y, present time
            public readonly List<Vector2> HistV = new List<Vector2>();
        }
        private readonly Pred[] _pred = new Pred[32];
        private static readonly bool NoPredictTest = System.Environment.GetEnvironmentVariable("CP99_TEST_PREDICT") == "0";
        private static bool PredictOn { get { return Plugin.InstantOwnMovement.Value && !NoPredictTest; } }
        private float _predErr, _predMax; private int _predN, _predSnaps; private float _predLogAt;

        private const float DashSpeedAdd = -700f, DashSpeedDecay = 400f; // BallShove.Shove's SpeedAddition

        /// <summary>How long ago (in this PC's present-time stamps) the host had seen the controls in a snapshot.</summary>
        private float InputLag()
        {
            Connection c;
            var ping = _conns.TryGetValue(0, out c) && c.Ping > 0f ? c.Ping : 0.1f;
            return Mathf.Clamp(ping + Proto.SendRate, 0.02f, 0.4f);
        }

        private static bool HistAt(Pred p, float t, out Vector2 pos, out Vector2 vel)
        {
            pos = vel = Vector2.zero;
            var h = p.Hist;
            if (h.Count < 2 || t < h[0].z || t > h[h.Count - 1].z) return false;
            for (var k = h.Count - 1; k > 0; k--)
            {
                if (h[k - 1].z > t) continue;
                var span = h[k].z - h[k - 1].z;
                var f = span > 0.00001f ? (t - h[k - 1].z) / span : 1f;
                pos = Vector2.Lerp(h[k - 1], h[k], f);
                vel = Vector2.Lerp(p.HistV[k - 1], p.HistV[k], f);
                return true;
            }
            return false;
        }

        private void PredReset(Pred p, Vector2 pos, Vector2 vel)
        {
            p.P = pos; p.V = vel; p.Offset = Vector2.zero;
            p.Hist.Clear(); p.HistV.Clear();
        }

        private Vector2 PredictOwn(int i, BallData ball, Snap sa, Snap sb, Vector2 hostNow, bool jumped, float rot)
        {
            var p = _pred[i] ?? (_pred[i] = new Pred());
            var gm = Globals.gameManager;
            var dt = Mathf.Clamp(Time.deltaTime, 0f, 0.1f);
            var present = RenderTime + _renderDelay;
            var span = sb.Time - sa.Time;
            var hostVel = span > 0.0001f ? new Vector2(sb.X[i] - sa.X[i], sb.Y[i] - sa.Y[i]) / span : Vector2.zero;
            var f = sb.BallFlags[i];
            var pad = PadOf(i);
            var stale = Now - p.At > 0.5f;
            p.At = Now;
            var hold = (f & Proto.BAlive) == 0 || (f & Proto.BFallen) != 0 || jumped || stale || gm == null ||
                       gm.startCountdown > 0f || gm.pauseMenuManager != null && gm.pauseMenuManager.paused || dt <= 0f;
            if (hold || !p.On)
            {
                p.On = !hold;
                PredReset(p, hostNow, hostVel);
                p.LastSnap = sb.Time;
                p.DashSeen = pad.DownCount[0];
                return hostNow;
            }

            // a new snapshot: compare with where this PC had the player when the host had seen the same controls
            if (sb.Time != p.LastSnap)
            {
                p.LastSnap = sb.Time;
                Vector2 hp, hv;
                if (HistAt(p, sb.Time - InputLag(), out hp, out hv))
                {
                    var ep = new Vector2(sb.X[i], sb.Y[i]) - hp;
                    var ev = hostVel - hv;
                    if (TestHarness.Active) { _predErr += ep.magnitude; _predMax = Mathf.Max(_predMax, ep.magnitude); _predN++; }
                    if (ep.magnitude > 1.5f) { PredReset(p, hostNow, hostVel); return hostNow; } // a big hit or a respawn: go there
                    p.P += ep; p.Offset -= ep; p.V += ev;
                    var from = sb.Time - InputLag();
                    for (var k = 0; k < p.Hist.Count; k++)
                    {
                        if (p.Hist[k].z < from) continue;
                        p.Hist[k] += new Vector3(ep.x, ep.y, 0f);
                        p.HistV[k] += ev;
                    }
                }
                _predSnaps++;
            }

            Vector2 move, aim;
            byte held;
            ReadSticks(i, out move, out aim, out held);
            var carrying = sb.Carrier == i;
            var stunned = Rounds.Stun.Remaining(ball) > 0f;
            var rb = ball.GetComponent<Rigidbody2D>();
            var mass = Mathf.Max(0.01f, ball.mass);
            var drag = rb != null ? rb.drag : 0f;

            // dash: one push along the facing, if a charge is ready (the host has the final say)
            if (pad.DownCount[0] != p.DashSeen)
            {
                p.DashSeen = pad.DownCount[0];
                int max; float charges;
                if (!carrying && !stunned && ball.canShove && Time.time - p.DashAt > 0.4f &&
                    UltraPuck.DashTracker.Get(ball, out max, out charges) && charges >= 1f)
                {
                    var up = (Vector2)(Quaternion.Euler(0f, 0f, rot) * Vector3.up);
                    p.V += up * (Rounds.CardEffects.GameDashForce * Time.fixedDeltaTime / mass);
                    p.DashAt = Time.time;
                }
            }

            var braking = (held & Proto.HSprint) != 0 && !carrying;
            var speed = ball.totalSpeed;
            var since = Time.time - p.DashAt;
            if (since < 3f) speed += Mathf.Min(0f, DashSpeedAdd + DashSpeedDecay * since);
            if (braking) speed += DashSpeedAdd;
            if (stunned) speed = 0f;
            speed = Mathf.Max(0f, speed) * p.Gain;

            // learn how fast this player really goes (cards, weight, Slow Poke): compare top speeds at full stick
            // (only while the host has the player gliding freely the way the stick points: not into a wall or a crowd)
            var free = hostVel.magnitude > 0.5f && Vector2.Dot(hostVel.normalized, move.normalized) > 0.95f;
            if (move.magnitude > 0.95f && free && !braking && !stunned && since > 2f && drag > 0.01f && ball.totalSpeed > 1f)
            {
                p.FullStickFor += dt;
                var modelTop = ball.totalSpeed * 0.02f / mass / drag;
                var ratio = modelTop > 0.01f ? hostVel.magnitude / modelTop : 1f;
                if (p.FullStickFor > 0.6f && ratio > 0.4f && ratio < 2.5f)
                    p.Gain = Mathf.Clamp(Mathf.Lerp(p.Gain, ratio, dt * 2f), 0.4f, 2.5f);
            }
            else p.FullStickFor = 0f;

            var decay = drag + (braking ? 80f * Time.fixedDeltaTime / mass : 0f);
            p.V = (p.V + move * (speed * 0.02f / mass) * dt) / (1f + decay * dt);
            p.P = SlideAlong(ball, p.P, ref p.V, p.V * dt);

            p.Hist.Add(new Vector3(p.P.x, p.P.y, present));
            p.HistV.Add(p.V);
            while (p.Hist.Count > 0 && present - p.Hist[0].z > 1f) { p.Hist.RemoveAt(0); p.HistV.RemoveAt(0); }

            p.Offset *= Mathf.Exp(-dt / 0.08f);
            if (TestHarness.Active && Now > _predLogAt)
            {
                _predLogAt = Now + 5f;
                if (_predN > 0) NetLog.Info("TEST own prediction vs host: off by " + (_predErr / _predN).ToString("F3") + " on average, at most " + _predMax.ToString("F2") + " (" + _predN + " checks, lag " + Mathf.RoundToInt(InputLag() * 1000f) + " ms, gain " + p.Gain.ToString("F2") + ")");
                _predErr = _predMax = 0f; _predN = 0;
            }
            return p.P + p.Offset;
        }

        /// <summary>Moves a predicted player, stopping at walls (bouncing as the game would) instead of going through.</summary>
        private static Vector2 SlideAlong(BallData ball, Vector2 from, ref Vector2 vel, Vector2 step)
        {
            var dist = step.magnitude;
            var col = ball.GetComponent<CircleCollider2D>();
            if (col == null || dist < 0.00001f) return from + step;
            var s = ball.transform.lossyScale;
            var r = col.radius * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y));
            var dir = step / dist;
            foreach (var h in Physics2D.CircleCastAll(from, r, dir, dist))
            {
                if (h.collider == null || h.collider.isTrigger || h.collider == col) continue;
                if (h.fraction <= 0f) continue; // already touching (after a correction): let it move out
                var vn = Vector2.Dot(vel, h.normal);
                if (vn >= 0f) continue;
                var bounce = Mathf.Max(col.sharedMaterial != null ? col.sharedMaterial.bounciness : 0f,
                                       h.collider.sharedMaterial != null ? h.collider.sharedMaterial.bounciness : 0f);
                vel -= h.normal * vn * (1f + bounce);
                return h.centroid + h.normal * 0.01f;
            }
            return from + step;
        }

        private Vector2 SmoothOwn(int i, Vector2 target, Snap sa, Snap sb, bool teleported)
        {
            if (i >= _ownLast.Length) return target;
            var span = sb.Time - sa.Time;
            var vel = span > 0.0001f ? new Vector2(sb.X[i] - sa.X[i], sb.Y[i] - sa.Y[i]) / span : Vector2.zero;
            return Smooth(i, target, vel, teleported);
        }

        private static readonly bool NoLiveTest = System.Environment.GetEnvironmentVariable("CP99_TEST_LIVE") == "0"; // tests: compare with it off
        private static bool LiveOpponentsOn { get { return Plugin.LiveOpponents.Value && !NoLiveTest; } }

        private const int PuckSmoothSlot = 31; // the loose puck's slot in the smoothing arrays

        /// <summary>Something drawn at the present (guessed from its last speed): blend each new correction in.</summary>
        private Vector2 Smooth(int i, Vector2 target, Vector2 vel, bool teleported)
        {
            var dt = Mathf.Clamp(Time.unscaledDeltaTime, 0f, 0.1f);
            if (_ownInit[i] && !teleported)
            {
                var expected = _ownLast[i] + _ownVel[i] * dt;      // where it would be if nothing changed
                var correction = target - expected;
                if (correction.magnitude < 1.5f) _ownOffset[i] -= correction; // small: blend it in
                else _ownOffset[i] = Vector2.zero;                // big (a hit, a respawn): just go there
                _ownOffset[i] *= Mathf.Exp(-dt / 0.05f);
            }
            else _ownOffset[i] = Vector2.zero;
            _ownInit[i] = true;
            _ownLast[i] = target;
            _ownVel[i] = vel;
            return target + _ownOffset[i];
        }

        private static Vector2 NotThroughWalls(Vector2 from, Vector2 to)
        {
            var hits = Physics2D.LinecastAll(from, to);
            foreach (var h in hits) // nearest first; players and the puck have no colliders on clients
            {
                if (h.collider == null || h.collider.isTrigger) continue;
                var back = (to - from).normalized * 0.05f;
                return h.point - back;
            }
            return to;
        }

        private void Apply(GameManager gm, Snap a, Snap b, float t, Snap la, Snap lb, float lt)
        {
            var rm = gm.replayManager;
            var replaying = (b.Flags & Proto.FReplaying) != 0;

            if (replaying != _wasReplaying) NetLog.Debug(replaying ? "Mirroring host instant replay" : "Replay finished");
            if (replaying && !_wasReplaying)
            {
                foreach (Transform child in gm.bloodSplatterManager.transform) UnityEngine.Object.Destroy(child.gameObject);
                gm.ballFallDummyManager.ClearAllDummies();
            }
            _wasReplaying = replaying;
            rm.replaying = replaying;
            rm.instantReplayText.SetActive(replaying);
            rm.faderObject.SetActive(replaying);
            if (replaying)
            {
                rm.faderObject.GetComponent<Renderer>().material.color = new Color(0f, 0f, 0f, b.Fader);
                Time.timeScale = b.TimeScale;
            }

            gm.roundOver = (b.Flags & Proto.FRoundOver) != 0;
            gm.matchOver = (b.Flags & Proto.FMatchOver) != 0;
            gm.suddenDeath = (b.Flags & Proto.FSuddenDeath) != 0;
            gm.teams[0].score = b.Score0;
            gm.teams[1].score = b.Score1;
            _hostTimeLeft = b.TimeLeft;

            var puckGo = gm.puck;
            var puck = puckGo.GetComponent<Puck>();
            var n = Mathf.Min(Match.Balls.Length, Mathf.Min(a.X.Length, b.X.Length));
            for (var i = 0; i < n; i++)
            {
                var ball = Match.Balls[i];
                var tr = ball.transform;
                // This PC's own players are drawn at the present (the host's newest positions, carried on by their
                // speed). With Live Opponents everyone else is too, so the whole rink is shown at one moment and a dash
                // that looks like it hits, hits. Otherwise others are drawn a little in the past (smoother, but behind).
                var mine = IsLocal(i);
                var present = Plugin.InstantOwnMovement.Value && (mine || LiveOpponentsOn) && !replaying && i < la.X.Length && i < lb.X.Length;
                var own = present && mine;
                var sa = present ? la : a;
                var sb = present ? lb : b;
                var st = present ? lt : t;
                var bt = sa.Teleport[i] != sb.Teleport[i] ? 1f : st; // teleported (bot swap): jump, don't slide
                if (b.Teleport[i] != _teleportSeen[i]) { _teleportSeen[i] = b.Teleport[i]; NetLog.Debug("Client: player " + i + " teleported (snapped)"); }
                var target = new Vector2(Mathf.LerpUnclamped(sa.X[i], sb.X[i], bt), Mathf.LerpUnclamped(sa.Y[i], sb.Y[i], bt));
                var jumped = sa.Teleport[i] != sb.Teleport[i];
                var rot = Mathf.LerpAngle(sa.Rot[i], sb.Rot[i], bt);
                if (own) rot = OwnFacing(i, rot, jumped || (sb.BallFlags[i] & Proto.BAlive) == 0);
                if (own && PredictOn) target = PredictOwn(i, ball, sa, sb, target, jumped, rot);
                else if (present) target = SmoothOwn(i, target, sa, sb, jumped);
                tr.position = new Vector3(target.x, target.y, tr.position.z);
                tr.rotation = Quaternion.Euler(0f, 0f, rot);
                if (TestHarness.Active) MeasureMotion(i, own, tr.position);

                var f = b.BallFlags[i];
                ball.alive = (f & Proto.BAlive) != 0;
                ball.fallen = (f & Proto.BFallen) != 0;
                var mesh = (f & Proto.BMesh) != 0;
                if (ball.ballMesh.activeSelf != mesh)
                {
                    ball.ballMesh.SetActive(mesh);
                    if (mesh)
                    {
                        ball.ballMesh.transform.localPosition = new Vector3(0f, 0f, 0.35f);
                        ball.GetComponent<BallSpecialColors>().ActivateSpecialColors();
                        if (!replaying && gm.startCountdown <= 0f)
                        {
                            var fx = UnityEngine.Object.Instantiate(gm.particleManager.respawnParticlesPrefabs[(int)ball.team.teamColor],
                                tr.position + Vector3.back / 2f, Quaternion.identity) as GameObject;
                            UnityEngine.Object.Destroy(fx, 2f);
                        }
                    }
                    else
                    {
                        ball.GetComponent<BallSpecialColors>().DeactivateSpecialColors();
                    }
                    ball.GetComponent<BallFire>().SetParticlesActive(mesh);
                }
                ball.carryObject = b.Carrier == i ? puckGo : null;
                ball.GetComponent<BallHandling>().ballCharge = b.Charge[i] / 255f * Proto.ChargeScale;
            }

            var pt = puckGo.transform;
            // the puck is shown at the same moment as the players around it: the present when it's carried by one of
            // this PC's players, or with Live Opponents always
            var ownPuck = Plugin.InstantOwnMovement.Value && !replaying && (LiveOpponentsOn || lb.Carrier >= 0 && IsLocal(lb.Carrier));
            var pa = ownPuck ? la : a;
            var pb = ownPuck ? lb : b;
            var puckJumped = pa.PuckTeleport != pb.PuckTeleport;
            var ptt = puckJumped ? 1f : (ownPuck ? lt : t);
            var puckAt = new Vector2(Mathf.LerpUnclamped(pa.PuckX, pb.PuckX, ptt), Mathf.LerpUnclamped(pa.PuckY, pb.PuckY, ptt));
            if (ownPuck && pb.Carrier < 0)
            {
                // a loose puck guessed ahead: never through a wall (it would have bounced; the next snapshot shows how)
                var from = new Vector2(pb.PuckX, pb.PuckY);
                if (ptt > 1f) puckAt = NotThroughWalls(from, puckAt);
                var span = pb.Time - pa.Time;
                var vel = span > 0.0001f ? new Vector2(pb.PuckX - pa.PuckX, pb.PuckY - pa.PuckY) / span : Vector2.zero;
                puckAt = Smooth(PuckSmoothSlot, puckAt, vel, puckJumped || pa.Carrier != pb.Carrier);
            }
            else _ownInit[PuckSmoothSlot] = false;
            pt.position = new Vector3(puckAt.x, puckAt.y, pt.position.z);
            pt.rotation = Quaternion.Euler(0f, 0f, Mathf.LerpAngle(pa.PuckRot, pb.PuckRot, ptt));
            // A carried puck sits 1 unit in front of its carrier (BallHandling.FixedUpdate). Put it there from the
            // carrier as drawn, rather than guessing its path: while spinning it circles the player, and carrying a
            // circling point on in a straight line threw it a player's width away.
            var heldBy = Match.Ball(pb.Carrier);
            if (!replaying && heldBy != null && pa.Carrier == pb.Carrier)
            {
                var ct = heldBy.transform;
                var front = ct.position + ct.up;
                pt.position = new Vector3(front.x, front.y, pt.position.z);
                if (TestHarness.Active && IsLocal(pb.Carrier)) MeasureCarry(pb, heldBy.transform);
            }
            var carrierBall = Match.Ball(b.Carrier);
            puck.carryer = carrierBall != null ? carrierBall.gameObject : null;
            puck.disrespecting = b.Disrespect;
        }

        // ---------------------------------------------------------------- host-driven moments

        private void ClientGoal(int teamIndex, Goal.GoalType type)
        {
            var gm = Globals.gameManager;
            if (gm == null || teamIndex >= gm.teams.Count) return;
            gm.ScoreGoal(gm.teams[teamIndex], type); // EndRound is blocked on clients; host drives the replay
            var sm = gm.soundManager;
            sm.CreateSound(UnityEngine.Random.value < 0.5f ? sm.cheeringGoalSound : sm.cheeringGoalSound2, 8f, 0.4f, 1f);
        }

        private void ClientResetField(bool suddenDeath)
        {
            var gm = Globals.gameManager;
            if (gm == null) return;
            _clientHold = false;
            gm.puck.GetComponent<Puck>().carryer = null;
            foreach (var b in Match.Balls) b.carryObject = null;
            gm.ResetField(suddenDeath);
        }

        private void ClientTimeUp()
        {
            var gm = Globals.gameManager;
            if (gm == null) return;
            var sm = gm.soundManager;
            sm.CreateSound(sm.buzzerSound, 2f, 0.5f, 0.75f);
            sm.CreateSound(sm.cheeringGoalSound, 8f, 0.4f, 1f);
            UnityEngine.Object.Instantiate(gm.slideInMessageManager.matchOverMessage);
            if (gm.teams[0].score != gm.teams[1].score)
            {
                var winner = gm.teams[0].score > gm.teams[1].score ? 0 : 1;
                UnityEngine.Object.Instantiate(gm.particleManager.fireworksParticlesPrefabs[(int)gm.teams[winner].teamColor],
                    gm.currentArena.goalNodes[winner].transform.position, Quaternion.identity);
                gm.musicManager.Invoke("FadeOutMusic", 4f);
            }
            gm.TurnOffCountdownText();
            gm.roundOver = true;
        }

        private void ClientMatchEnd(Reader r)
        {
            var gm = Globals.gameManager;
            var won0 = r.Bool();
            var won1 = r.Bool();
            int n = r.U8();
            for (var i = 0; i < n; i++)
            {
                var goals = r.I32(); var assists = r.I32(); var checks = r.I32(); var deaths = r.I32();
                var interceptions = r.I32(); var selfGoals = r.I32(); var hold = r.F32();
                var winner = r.Bool(); var wins = r.I32(); var losses = r.I32();
                var b = Match.Ball(i);
                if (b == null) continue;
                var p = b.playerData;
                p.goals = goals; p.assists = assists; p.checks = checks; p.deaths = deaths;
                p.interceptions = interceptions; p.selfGoals = selfGoals; p.holdTime = hold;
                p.winner = winner; p.wins = wins; p.losses = losses;
            }
            if (gm != null)
            {
                gm.teams[0].wonMatch = won0;
                gm.teams[1].wonMatch = won1;
                try { gm.medalManager.AssignMedals(); } catch (Exception e) { NetLog.Warn("Medals: " + e.Message); }
            }
            NetLog.Info("Match over");
            // Match stays active until the scene actually changes (OnLevelWasLoaded ends it), so the
            // client's own end-of-match code (stat saving, achievements, scene change) stays blocked.
            if (gm != null) gm.replayManager.replaying = false;
            Time.timeScale = 1f;
            Status = "Connected to " + HostName + " - waiting for the next match";
            Application.LoadLevel("MatchOver");
        }

        // ---------------------------------------------------------------- effects

        private void ApplyEvents(Reader r)
        {
            var gm = Globals.gameManager;
            if (gm == null) return;
            var sm = gm.soundManager;
            var pm = gm.particleManager;
            int count = r.U8();
            for (var e = 0; e < count; e++)
            {
                var ev = (Ev)r.U8();
                switch (ev)
                {
                    case Ev.Shoot:
                    {
                        var b = Match.Ball(r.U8());
                        if (b == null) break;
                        var fx = UnityEngine.Object.Instantiate(pm.shootParticlesPrefab,
                            gm.puck.transform.position - b.transform.up / 3f + Vector3.forward / 3f, b.transform.rotation) as GameObject;
                        UnityEngine.Object.Destroy(fx, 5f);
                        sm.CreateSound(sm.shootSound, 2f, 0.75f, UnityEngine.Random.Range(0.95f, 1.05f));
                        break;
                    }
                    case Ev.Shove:
                    {
                        var shoveSlot = r.U8();
                        UltraPuck.DashTracker.OnRemoteShove(shoveSlot);
                        var b = Match.Ball(shoveSlot);
                        if (b == null) break;
                        var fx = UnityEngine.Object.Instantiate(pm.thrustParticlePrefab, b.transform.position, b.transform.rotation) as GameObject;
                        fx.GetComponent<ThrustParticles>().player = b.gameObject;
                        UnityEngine.Object.Destroy(fx, 3f);
                        sm.CreateSound(sm.thrustSound, 2f, 0.75f, UnityEngine.Random.Range(0.925f, 1f));
                        break;
                    }
                    case Ev.Boost:
                    {
                        r.U8();
                        int idx = r.U16();
                        if (idx >= Match.Boosts.Length) break;
                        var fx = UnityEngine.Object.Instantiate(pm.activateBoostParticlePrefab,
                            Match.Boosts[idx].transform.position + new Vector3(0f, 0f, -0.1f), Quaternion.identity) as GameObject;
                        UnityEngine.Object.Destroy(fx, 5f);
                        sm.CreateSound(sm.boostSound, 1.5f, 0.333f, UnityEngine.Random.Range(0.95f, 1f));
                        break;
                    }
                    case Ev.Bumper:
                    {
                        var who = r.U8();
                        int idx = r.U16();
                        if (idx >= Match.Bumpers.Length) break;
                        Match.Bumpers[idx].PlayBounceAnimation();
                        sm.CreateSound(sm.bumperSound, 1.5f, who == Proto.PuckSlot ? 0.5f : 0.75f, UnityEngine.Random.Range(0.9f, 1.05f));
                        break;
                    }
                    case Ev.Check:
                    {
                        var checker = Match.Ball(r.U8());
                        var victim = Match.Ball(r.U8());
                        if (checker == null || victim == null) break;
                        var ps = victim.GetComponent<BallParticles>().checkParticles;
                        ps.transform.rotation = checker.transform.rotation;
                        ps.transform.position = victim.transform.position + checker.transform.up * 0.5f;
                        ps.Emit(16);
                        sm.CreateSound(sm.thrustHitSound, 2f, 0.45f, UnityEngine.Random.Range(0.9f, 1.05f));
                        break;
                    }
                    case Ev.Spikes:
                    {
                        r.U8();
                        sm.CreateSound(sm.splatSound, 2f, 0.75f, UnityEngine.Random.Range(0.95f, 1.05f));
                        if (UnityEngine.Random.value < 0.45f)
                            sm.CreateSound(sm.cheeringOoohSounds[UnityEngine.Random.Range(0, sm.cheeringOoohSounds.Length)], 3f, 0.333f, 1f);
                        break;
                    }
                    case Ev.Fall:
                    {
                        var b = Match.Ball(r.U8());
                        var x = r.F32(); var y = r.F32(); var rot = r.F32(); var vx = r.F32(); var vy = r.F32();
                        if (b == null) break;
                        gm.ballFallDummyManager.CreateDummy(new BallFallDummySnapshot(b,
                            new Vector3(x, y, b.transform.position.z), Quaternion.Euler(0f, 0f, rot), new Vector3(vx, vy, 0f)));
                        break;
                    }
                    case Ev.Kill:
                    {
                        var b = Match.Ball(r.U8());
                        var blood = r.Bool();
                        if (b == null || !blood) break;
                        var bdr = b.GetComponent<BallDeathRespawn>();
                        var go = UnityEngine.Object.Instantiate(bdr.bloodSplatterPrefab, b.transform.position, b.transform.rotation) as GameObject;
                        go.GetComponent<BloodSplatter>().bloodColor = ColorManager.GetColor(b.team.teamColor);
                        go.GetComponent<BloodSplatter>().Initialize();
                        go.transform.parent = gm.bloodSplatterManager.transform;
                        break;
                    }
                    case Ev.Ping:
                    {
                        var b = Match.Ball(r.U8());
                        if (b != null) b.GetComponent<BallHandling>().ping.Emit(5);
                        break;
                    }
                    default:
                        NetLog.Warn("Unknown event " + ev);
                        return;
                }
            }
        }
    }
}
