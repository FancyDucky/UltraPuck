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
            public byte[] BallFlags, Charge;
            public float PuckX, PuckY, PuckRot;
            public int Carrier;
            public bool Disrespect;
        }

        /// <summary>Clients draw the world this far in the past (smooth movement between snapshots).</summary>
        internal const float InterpDelay = 0.1f;

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

        // Local input, accumulated every frame and sent at Proto.SendRate.
        private uint _inputSeq;
        private float _inputAccum;
        private readonly byte[] _downCount = new byte[3];
        private byte _chargeUpCount;
        private bool _prevCharging;

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

        private void ClientReset()
        {
            Lobby.Clear();
            _snaps.Clear();
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
                case Msg.Own:
                    if (Match.Active) Prediction.Read(r, Now);
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
            for (var i = 0; i < n; i++)
            {
                var team = r.U8();
                var color = r.U8();
                var ai = r.Bool();
                var name = r.Str();
                var symbol = r.U8();
                var owner = r.U8();
                var mine = owner == _myId;
                var p = new PlayerData(ai, 0, mine ? MatchJoystick : 0, team)
                {
                    teamColor = (TeamColor)color,
                    account = new Account(name, ai, symbol)
                };
                if (mine) local = i;
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
            Match.ClientPending = true;
            Match.OnlinePlayers.Clear();
            foreach (var p in players) Match.OnlinePlayers.Add(p);
            PlayerSelectionManager_V2.players = players;

            _snaps.Clear();
            _latestSnapSeq = 0;
            _wasReplaying = false;
            Time.timeScale = 1f;
            Status = "Loading " + arena + "...";
            NetLog.Info("Host started match " + id + " on " + arena + " (" + n + " players, my slot " + local + ")");
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
                var local = s == Match.LocalSlot;
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
            PollLocalInput();
            {
                Vector2 move, aim; byte held;
                ReadControls(out move, out aim, out held);
                Prediction.FrameTick(move, aim, (held & Proto.HSprint) != 0);
                Prediction.LogStats(Now);
            }
            _inputAccum += Time.unscaledDeltaTime;
            if (_inputAccum >= Proto.SendRate)
            {
                _inputAccum = Mathf.Min(_inputAccum - Proto.SendRate, Proto.SendRate);
                SendInput(c);
            }
        }

        private BallInput LocalInput()
        {
            var b = Match.Ball(Match.LocalSlot);
            return b != null ? b.GetComponent<BallInput>() : null;
        }

        private void PollLocalInput()
        {
            var bi = LocalInput();
            if (bi == null) return;
            if (TestHarness.DriveInput)
            {
                // Local test only: shoot/shove every 1.5s.
                if ((int)(Now / 1.5f) != (int)((Now - Time.unscaledDeltaTime) / 1.5f)) { _downCount[0]++; Prediction.OnShootPressed(); }
                return;
            }
            if (Input.GetButtonDown(bi.buttonName_shoot) || Input.GetButtonDown(bi.buttonName_shootAlt)) { _downCount[0]++; Prediction.OnShootPressed(); }
            if (Input.GetButtonDown(bi.buttonName_callPass) || bi.axisName_x2 == "Mouse X" && Input.GetKeyDown(KeyCode.F)) _downCount[1]++;
            var charging = IsCharging(bi);
            if (charging && !_prevCharging) _downCount[2]++;
            if (!charging && _prevCharging) _chargeUpCount++;
            _prevCharging = charging;
        }

        private static bool IsCharging(BallInput bi)
        {
            var axis = 0f;
            try { if (!string.IsNullOrEmpty(bi.axisName_charge)) axis = Input.GetAxis(bi.axisName_charge); } catch { }
            return axis > 0.5f || Input.GetButton(bi.buttonName_charge) || Input.GetButton(bi.buttonName_chargeAlt);
        }

        private void ClientFixedUpdate()
        {
            if (!Match.Active) return;
            Vector2 move, aim; byte held;
            ReadControls(out move, out aim, out held);
            Prediction.FixedTick(Globals.gameManager, Now, move, (held & Proto.HSprint) != 0, (held & Proto.HCharge) != 0);
        }

        private void SendInput(Connection c)
        {
            Vector2 move, aim; byte held;
            ReadControls(out move, out aim, out held);
            Begin(Msg.Input);
            _msg.U32(++_inputSeq);
            _msg.I16(Proto.PackUnit(move.x)); _msg.I16(Proto.PackUnit(move.y));
            _msg.I16(Proto.PackUnit(aim.x)); _msg.I16(Proto.PackUnit(aim.y));
            _msg.U8(held);
            for (var i = 0; i < 3; i++) _msg.U8(_downCount[i]);
            _msg.U8(_chargeUpCount);
            _msg.U8(Possession.ClientSwitchCount);
            c.SendUnreliable(_msg);
            Prediction.OnInputSent(_inputSeq);
        }

        /// <summary>This PC's controls right now: what goes in the input message, and what the prediction uses.</summary>
        private void ReadControls(out Vector2 move, out Vector2 aim, out byte held)
        {
            var bi = LocalInput();
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
                aim = move;
                held = 0;
                if (TestHarness.BrakeTest && Mathf.Repeat(Now, 8f) < 0.4f) held |= Proto.HSprint; // brake now and then
            }
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
            s.BallFlags = new byte[n]; s.Charge = new byte[n];
            for (var i = 0; i < n; i++)
            {
                s.X[i] = r.F32(); s.Y[i] = r.F32();
                s.Rot[i] = Proto.UnpackAngle(r.U16());
                s.BallFlags[i] = r.U8();
                s.Charge[i] = r.U8();
            }
            s.PuckX = r.F32(); s.PuckY = r.F32();
            s.PuckRot = Proto.UnpackAngle(r.U16());
            var carrier = r.U8();
            s.Carrier = carrier == 255 ? -1 : carrier;
            s.Disrespect = r.Bool();

            _latestSnapRecv = Now;
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
            var renderTime = latest.Time + (Now - _latestSnapRecv) - InterpDelay;
            Snap a = latest, b = latest;
            var t = 0f;
            for (var i = _snaps.Count - 2; i >= 0; i--)
            {
                if (_snaps[i].Time <= renderTime)
                {
                    a = _snaps[i];
                    b = _snaps[i + 1];
                    var span = b.Time - a.Time;
                    t = span > 0.0001f ? Mathf.Clamp01((renderTime - a.Time) / span) : 1f;
                    break;
                }
            }
            if (a == latest && _snaps.Count > 1 && _snaps[0].Time > renderTime) { a = b = _snaps[0]; t = 0f; }
            Apply(gm, a, b, t);
        }

        private void Apply(GameManager gm, Snap a, Snap b, float t)
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
            Vector3 mineSnap = Vector3.zero, minePos = Vector3.zero; float mineSnapRot = 0f, mineRot = 0f; // our predicted ball: snapshot vs ours
            for (var i = 0; i < n; i++)
            {
                var ball = Match.Balls[i];
                var tr = ball.transform;
                var snapPos = new Vector3(Mathf.Lerp(a.X[i], b.X[i], t), Mathf.Lerp(a.Y[i], b.Y[i], t), tr.position.z);
                if (Prediction.Owns(i)) // this PC moves it (Prediction)
                {
                    mineSnap = snapPos; minePos = tr.position;
                    mineSnapRot = Mathf.LerpAngle(a.Rot[i], b.Rot[i], t); mineRot = tr.eulerAngles.z;
                }
                else
                {
                    tr.position = snapPos;
                    tr.rotation = Quaternion.Euler(0f, 0f, Mathf.LerpAngle(a.Rot[i], b.Rot[i], t));
                }

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
            pt.position = new Vector3(Mathf.Lerp(a.PuckX, b.PuckX, t), Mathf.Lerp(a.PuckY, b.PuckY, t), pt.position.z);
            if (b.Carrier >= 0 && Prediction.Owns(b.Carrier))
            {
                // carried by our predicted ball: keep it where the host has it relative to the ball, but turned with
                // the way we face now (not the host's older facing)
                var rel = Quaternion.Euler(0f, 0f, Mathf.DeltaAngle(mineSnapRot, mineRot)) * (pt.position - mineSnap);
                pt.position = new Vector3(minePos.x + rel.x, minePos.y + rel.y, pt.position.z);
            }
            pt.rotation = Quaternion.Euler(0f, 0f, Mathf.LerpAngle(a.PuckRot, b.PuckRot, t));
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
