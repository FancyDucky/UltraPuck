using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace CP99Online.Net
{
    internal sealed partial class NetSession
    {
        internal sealed class ClientInfo
        {
            public Connection Conn;
            public byte Id;
            public string Name;
            public int Team;
            public bool Loaded;
            public readonly NetInput.Remote Input = new NetInput.Remote();
        }

        private sealed class Shadow
        {
            public int Frame = -1;
            public bool Shoot, Shove, Spikes;
            public Boost Boost;
            public Bumper Bumper;
            public BallData Check;
            public BallFallDummySnapshot Fall;

            public void Reset(int frame)
            {
                Frame = frame;
                Shoot = Shove = Spikes = false;
                Boost = null; Bumper = null; Check = null; Fall = null;
            }
        }

        private const float LoadWaitLimit = 20f;

        private readonly Dictionary<int, ClientInfo> _clients = new Dictionary<int, ClientInfo>();
        private byte _nextClientId = 1;
        private byte _matchCounter;
        private bool _holdingStart;
        private float _holdSince;
        private float _snapAccum;
        private uint _snapSeq;
        private bool _timeUpSent, _matchEndSent;
        private int _suppressResetField;
        private readonly Writer _events = new Writer();
        private int _eventCount;
        private Shadow[] _shadows = new Shadow[0];
        private readonly Shadow _puckShadow = new Shadow();

        public IEnumerable<ClientInfo> Clients { get { return _clients.Values.OrderBy(c => c.Id); } }
        public int ClientCount { get { return _clients.Count; } }
        public bool WaitingForClients { get { return _holdingStart; } }

        // ---------------------------------------------------------------- start / stop

        public void StartHostDirect(int port, string bindAddress)
        {
            if (Role != Role.None) return;
            try
            {
                _transport = UdpTransport.Listen(bindAddress, port);
            }
            catch (Exception e)
            {
                Status = "Could not host on port " + port + ": " + e.Message;
                NetLog.Warn(Status);
                return;
            }
            Role = Role.Host;
            Status = "Hosting (direct IP, UDP port " + port + ")";
            NetLog.Info(Status);
        }

        public void StartHostSteam()
        {
            if (Role != Role.None) return;
            if (!SteamLobby.Available)
            {
                Status = "Steam isn't available (launch the game from Steam)";
                return;
            }
            _transport = SteamTransport.Listen();
            Role = Role.Host;
            Status = "Hosting (Steam)";
            SteamLobby.CreateLobby();
            NetLog.Info(Status);
        }

        private void HostReset()
        {
            _clients.Clear();
            NetInput.BySlot.Clear();
            _holdingStart = false;
            _eventCount = 0;
            _events.Reset();
        }

        public void SetClientTeam(byte clientId, int team)
        {
            foreach (var ci in _clients.Values)
                if (ci.Id == clientId) ci.Team = team;
            BroadcastLobby();
        }

        public void Kick(byte clientId)
        {
            foreach (var kv in _clients.ToList())
            {
                if (kv.Value.Id != clientId) continue;
                Begin(Msg.Bye); _msg.Str("Removed by host");
                kv.Value.Conn.SendUnreliable(_msg);
                DropClient(kv.Key, kv.Value.Name + " was removed");
            }
        }

        // ---------------------------------------------------------------- messages

        private void HostHandle(Connection c, Msg type, Reader r)
        {
            ClientInfo ci;
            _clients.TryGetValue(c.PeerId, out ci);
            switch (type)
            {
                case Msg.Hello:
                {
                    var version = r.U16();
                    var name = Clean(r.Str());
                    var team = r.U8() > 0 ? 1 : 0;
                    if (ci != null) return;
                    if (version != Proto.Version)
                    {
                        Reject(c, "Mod version mismatch (host protocol " + Proto.Version + ", yours " + version + "). Update CP99 Online.");
                        return;
                    }
                    if (_clients.Count >= Proto.MaxClients)
                    {
                        Reject(c, "The lobby is full");
                        return;
                    }
                    ci = new ClientInfo { Conn = c, Id = _nextClientId++, Name = name, Team = team };
                    _clients[c.PeerId] = ci;
                    Begin(Msg.Welcome);
                    _msg.U8(ci.Id);
                    _msg.Str(LocalName());
                    c.SendReliable(_msg, Now);
                    NetLog.Info(name + " joined (client " + ci.Id + ", team " + (team + 1) + ")");
                    BroadcastLobby();
                    break;
                }
                case Msg.SetTeam:
                    if (ci != null) { ci.Team = r.U8() > 0 ? 1 : 0; BroadcastLobby(); }
                    break;
                case Msg.Input:
                    if (ci != null) ReadInput(ci.Input, r);
                    break;
                case Msg.Loaded:
                    if (ci != null && r.U8() == Match.Id)
                    {
                        ci.Loaded = true;
                        NetLog.Info(ci.Name + " loaded the arena");
                    }
                    break;
                case Msg.Bye:
                    DropClient(c.PeerId, (ci != null ? ci.Name : "A player") + " left");
                    break;
            }
        }

        private void Reject(Connection c, string reason)
        {
            NetLog.Info("Rejected a player: " + reason);
            Begin(Msg.Reject);
            _msg.Str(reason);
            for (var i = 0; i < 3; i++) c.SendUnreliable(_msg);
            _conns.Remove(c.PeerId);
            _transport.Disconnect(c.PeerId);
        }

        private static void ReadInput(NetInput.Remote input, Reader r)
        {
            var seq = r.U32();
            if (seq != 0 && (int)(seq - input.Seq) <= 0) return; // stale/out of order
            input.Seq = seq;
            input.Move = new Vector2(Proto.UnpackUnit(r.I16()), Proto.UnpackUnit(r.I16()));
            input.Aim = new Vector2(Proto.UnpackUnit(r.I16()), Proto.UnpackUnit(r.I16()));
            input.Held = r.U8();
            for (var i = 0; i < 3; i++) input.DownCount[i] = r.U8();
            input.ChargeUpCount = r.U8();
        }

        private void DropClient(int peer, string why)
        {
            ClientInfo ci;
            if (_clients.TryGetValue(peer, out ci))
            {
                _clients.Remove(peer);
                if (Match.Owners != null)
                    for (var s = 0; s < Match.Owners.Count; s++)
                        if (Match.Owners[s] == ci.Id) NetInput.BySlot.Remove(s); // their ball just stops
            }
            _conns.Remove(peer);
            if (_transport != null) _transport.Disconnect(peer);
            if (why != null) NetLog.Info(why);
            BroadcastLobby();
        }

        private void BroadcastLobby()
        {
            Begin(Msg.Lobby);
            var list = Clients.ToList();
            _msg.U8(list.Count + 1);
            _msg.U8(0); _msg.Str(LocalName() + " (HOST)"); _msg.U8(255);
            foreach (var ci in list) { _msg.U8(ci.Id); _msg.Str(ci.Name); _msg.U8(ci.Team); }
            BroadcastReliable();
        }

        private void BroadcastReliable()
        {
            foreach (var ci in _clients.Values) ci.Conn.SendReliable(_msg, Now);
        }

        private void BroadcastUnreliable()
        {
            foreach (var ci in _clients.Values) ci.Conn.SendUnreliable(_msg);
        }

        // ---------------------------------------------------------------- per frame

        private void HostUpdate()
        {
            List<int> dead = null;
            foreach (var c in _conns.Values)
                if (Now - c.LastHeard > Proto.Timeout) (dead ?? (dead = new List<int>())).Add(c.PeerId);
            if (dead != null)
                foreach (var p in dead)
                {
                    ClientInfo ci;
                    _clients.TryGetValue(p, out ci);
                    DropClient(p, (ci != null ? ci.Name : "A player") + " timed out");
                }

            if (_holdingStart) HoldStart();
            FlushEvents();
        }

        /// <summary>Keeps the opening countdown frozen until every client has loaded the arena.</summary>
        private void HoldStart()
        {
            var gm = Globals.gameManager;
            if (gm == null || !Match.Active) { _holdingStart = false; return; }
            var allLoaded = _clients.Values.All(c => c.Loaded || !Match.Owners.Contains(c.Id));
            if (allLoaded || Now - _holdSince > LoadWaitLimit)
            {
                _holdingStart = false;
                NetLog.Info(allLoaded ? "All players loaded, starting" : "Starting without players who are still loading");
                gm.ResetField(); // broadcasts ResetField, so every countdown starts together
                return;
            }
            gm.startCountdown = 3.01f;
        }

        private void HostLateUpdate()
        {
            if (!Match.Active) return;
            var gm = Globals.gameManager;
            if (gm == null) return;

            FlushEvents();

            if (GameModeManager.gameMode == GameModeManager.GameMode.TIMED && gm.guiTimer != null &&
                gm.guiTimer.timeLeft < 0f && !gm.suddenDeath && !_timeUpSent)
            {
                _timeUpSent = true;
                Begin(Msg.TimeUp);
                BroadcastReliable();
            }

            _snapAccum += Time.unscaledDeltaTime;
            if (_snapAccum < Proto.SendRate) return;
            _snapAccum = Mathf.Min(_snapAccum - Proto.SendRate, Proto.SendRate);
            SendSnapshot(gm);
        }

        private void SendSnapshot(GameManager gm)
        {
            var rm = gm.replayManager;
            Begin(Msg.Snapshot);
            _msg.U32(++_snapSeq);
            _msg.F32(Now);
            byte flags = 0;
            if (rm.replaying) flags |= Proto.FReplaying;
            if (gm.roundOver) flags |= Proto.FRoundOver;
            if (gm.matchOver) flags |= Proto.FMatchOver;
            if (gm.suddenDeath) flags |= Proto.FSuddenDeath;
            if (gm.pauseMenuManager != null && gm.pauseMenuManager.paused) flags |= Proto.FPaused;
            _msg.U8(flags);
            _msg.F32(Time.timeScale);
            _msg.F32(rm.faderAlpha);
            _msg.F32(gm.guiTimer != null ? gm.guiTimer.timeLeft : 0f);
            _msg.U8(gm.teams[0].score);
            _msg.U8(gm.teams[1].score);

            var puck = gm.puck.GetComponent<Puck>();
            _msg.U8(Match.Balls.Length);
            for (var i = 0; i < Match.Balls.Length; i++)
            {
                var b = Match.Balls[i];
                var p = b.transform.position;
                _msg.F32(p.x); _msg.F32(p.y);
                _msg.U16(Proto.PackAngle(b.transform.eulerAngles.z));
                byte bf = 0;
                if (b.alive) bf |= Proto.BAlive;
                if (b.fallen) bf |= Proto.BFallen;
                if (b.ballMesh.activeSelf) bf |= Proto.BMesh;
                _msg.U8(bf);
                var bh = b.GetComponent<BallHandling>();
                _msg.U8((byte)Mathf.Clamp(bh.ballCharge / 3f * 255f, 0f, 255f));
            }
            var pp = gm.puck.transform.position;
            _msg.F32(pp.x); _msg.F32(pp.y);
            _msg.U16(Proto.PackAngle(gm.puck.transform.eulerAngles.z));
            var carrier = puck.carryer != null ? Match.Slot(puck.carryer.GetComponent<BallData>()) : -1;
            _msg.U8(carrier < 0 ? (byte)255 : (byte)carrier);
            _msg.Bool(puck.disrespecting);
            BroadcastUnreliable();
        }

        // ---------------------------------------------------------------- game hooks

        /// <summary>GameManager.NewGame prefix: adds remote players and tells clients to load the arena.</summary>
        internal void HostOnNewGame()
        {
            Match.End();
            Match.Players = null;
            Match.Owners = null;
            var players = PlayerSelectionManager_V2.players;
            if (players == null) return;
            players.RemoveAll(p => Match.OnlinePlayers.Contains(p));
            Match.OnlinePlayers.Clear();
            if (_clients.Count == 0) return;

            if (ChallengeManager.isInChallenge || ChallengeMatch_Manager.challengeMatch)
            {
                NetLog.Info("Challenges are single-player; online players were not added");
                return;
            }
            if (ArenaManager.arenaName == "0000_Custom")
            {
                Status = "Custom arenas aren't supported online yet - pick a built-in arena";
                NetLog.Warn(Status);
                return;
            }

            var owners = new List<byte>();
            for (var i = 0; i < players.Count; i++) owners.Add(0);
            foreach (var ci in Clients)
            {
                if (players.Count >= 10) { NetLog.Warn("10 player limit reached; " + ci.Name + " will spectate"); continue; }
                var p = new PlayerData(false, 0, 0, ci.Team)
                {
                    teamColor = ci.Team == 0 ? ColorManager.team1Color : ColorManager.team2Color,
                    account = new Account(ci.Name, false, 0)
                };
                players.Add(p);
                Match.OnlinePlayers.Add(p);
                owners.Add(ci.Id);
                ci.Loaded = false;
            }

            Match.Id = ++_matchCounter;
            Match.Players = players;
            Match.Owners = owners;
            Match.LocalSlot = -1;

            NetInput.BySlot.Clear();
            for (var s = 0; s < owners.Count; s++)
            {
                if (owners[s] == 0) continue;
                var ci = _clients.Values.First(c => c.Id == owners[s]);
                ci.Input.Sync();
                NetInput.BySlot[s] = ci.Input;
            }

            Begin(Msg.StartMatch);
            _msg.U8(Match.Id);
            _msg.Str(ArenaManager.arenaName);
            _msg.U8((byte)GameModeManager.gameMode);
            _msg.I32(NumGoalsToWinManager.numGoalsToWin);
            _msg.F32(GameTimerManager.gameLength);
            _msg.U8((byte)ColorManager.team1Color);
            _msg.U8((byte)ColorManager.team2Color);
            _msg.Bool(ArenaManager.noSpikes);
            _msg.U8(players.Count);
            for (var i = 0; i < players.Count; i++)
            {
                var p = players[i];
                _msg.U8(p.team);
                _msg.U8((byte)p.teamColor);
                _msg.Bool(p.AI);
                _msg.Str(p.account != null ? p.account.name : "PLAYER");
                _msg.U8(p.account != null ? p.account.symbol : 0);
                _msg.U8(owners[i]);
            }
            BroadcastReliable();

            _timeUpSent = _matchEndSent = false;
            _snapSeq = 0;
            _snapAccum = 0f;
            NetLog.Info("Online match " + Match.Id + " on " + ArenaManager.arenaName + " with " + players.Count + " players");
        }

        /// <summary>GameManager.SpawnPlayers postfix (host): routes remote players' balls to network input.</summary>
        internal void HostOnSpawned(GameManager gm, int[] countsBefore)
        {
            if (Match.Players == null || !Match.Bind(gm, countsBefore)) return;
            _shadows = new Shadow[Match.Balls.Length];
            for (var i = 0; i < _shadows.Length; i++) _shadows[i] = new Shadow();
            _puckShadow.Reset(-1);

            for (var s = 0; s < Match.Balls.Length; s++)
            {
                if (!Match.IsRemote(s)) continue;
                var input = Match.Balls[s].GetComponent<BallInput>();
                input.axisName_x1 = NetInput.Name(s, "x1");
                input.axisName_y1 = NetInput.Name(s, "y1");
                input.axisName_x2 = NetInput.Name(s, "x2");
                input.axisName_y2 = NetInput.Name(s, "y2");
                input.axisName_charge = NetInput.Name(s, "chargeaxis");
                input.buttonName_shoot = input.buttonName_shootAlt = NetInput.Name(s, "shoot");
                input.buttonName_charge = input.buttonName_chargeAlt = NetInput.Name(s, "charge");
                input.buttonName_sprint = input.buttonName_sprintAlt = NetInput.Name(s, "sprint");
                input.buttonName_callPass = NetInput.Name(s, "callpass");
            }

            Match.Active = true;
            _holdingStart = true;
            _holdSince = Now;
        }

        internal void HostOnScoreGoal(GameManager gm, TeamData team, Goal.GoalType type)
        {
            Begin(Msg.Goal);
            _msg.U8(gm.teams.IndexOf(team));
            _msg.U8((byte)type);
            BroadcastReliable();
        }

        internal void HostOnResetField(bool suddenDeath)
        {
            if (_suppressResetField > 0 || _holdingStart) return;
            Begin(Msg.ResetField);
            _msg.Bool(suddenDeath);
            BroadcastReliable();
        }

        internal void HostOnSuddenDeath(bool before)
        {
            if (before)
            {
                Begin(Msg.SuddenDeath);
                BroadcastReliable();
                _suppressResetField++;
            }
            else
            {
                _suppressResetField--;
            }
        }

        internal void HostOnMatchOver(GameManager gm)
        {
            if (_matchEndSent) return;
            _matchEndSent = true;
            FlushEvents();
            Begin(Msg.MatchEnd);
            _msg.Bool(gm.teams[0].wonMatch);
            _msg.Bool(gm.teams[1].wonMatch);
            _msg.U8(Match.Balls.Length);
            foreach (var b in Match.Balls)
            {
                var p = b.playerData;
                _msg.I32(p.goals); _msg.I32(p.assists); _msg.I32(p.checks); _msg.I32(p.deaths);
                _msg.I32(p.interceptions); _msg.I32(p.selfGoals); _msg.F32(p.holdTime);
                _msg.Bool(p.winner); _msg.I32(p.wins); _msg.I32(p.losses);
            }
            BroadcastReliable();
            NetLog.Info("Match over");
        }

        internal void HostOnKill(BallDeathRespawn bdr, bool blood)
        {
            var s = Match.Slot(bdr.GetComponent<BallData>());
            if (s < 0) return;
            AddEvent(Ev.Kill); _events.U8(s); _events.Bool(blood);
        }

        internal void HostOnPing(BallHandling bh)
        {
            var s = Match.Slot(bh.GetComponent<BallData>());
            if (s < 0) return;
            AddEvent(Ev.Ping); _events.U8(s);
        }

        /// <summary>
        /// Runs before ReplayManager.LateUpdate captures the frame. Gameplay scripts mark effects
        /// (shot, shove, boost pad, bumper, check, spikes, fall) on the current replay slot; any newly
        /// set mark becomes a network event.
        /// </summary>
        internal void HostBeforeReplayCapture(ReplayManager rm)
        {
            if (!Match.Active || rm.replaying || !rm.capturing) return;
            var f = rm.frameCaptureNumber;
            for (var s = 0; s < Match.Storage.Length && s < _shadows.Length; s++)
            {
                var st = Match.Storage[s];
                if (st == null || st.replayStates == null || f >= st.replayStates.Length) continue;
                var rs = st.replayStates[f];
                var sh = _shadows[s];
                if (sh.Frame != f) sh.Reset(f);

                if (rs.shoot && !sh.Shoot) { AddEvent(Ev.Shoot); _events.U8(s); }
                if (rs.shove && !sh.Shove) { AddEvent(Ev.Shove); _events.U8(s); }
                if (rs.spikes && !sh.Spikes) { AddEvent(Ev.Spikes); _events.U8(s); }
                if (rs.boost != null && rs.boost != sh.Boost)
                {
                    int idx;
                    if (Match.BoostIndex.TryGetValue(rs.boost, out idx)) { AddEvent(Ev.Boost); _events.U8(s); _events.U16((ushort)idx); }
                }
                if (rs.bumper != null && rs.bumper != sh.Bumper) AddBumper(s, rs.bumper);
                if (rs.check != null && rs.check != sh.Check)
                {
                    var victim = Match.Slot(rs.check);
                    if (victim >= 0) { AddEvent(Ev.Check); _events.U8(s); _events.U8(victim); }
                }
                if (rs.ballFallDummy != null && rs.ballFallDummy != sh.Fall)
                {
                    var d = rs.ballFallDummy;
                    AddEvent(Ev.Fall); _events.U8(s);
                    _events.F32(d.position.x); _events.F32(d.position.y);
                    _events.F32(d.rotation.eulerAngles.z);
                    _events.F32(d.velocity.x); _events.F32(d.velocity.y);
                }

                sh.Shoot = rs.shoot; sh.Shove = rs.shove; sh.Spikes = rs.spikes;
                sh.Boost = rs.boost; sh.Bumper = rs.bumper; sh.Check = rs.check; sh.Fall = rs.ballFallDummy;
            }

            var ps = Match.PuckStorage;
            if (ps != null && ps.replayStates != null && f < ps.replayStates.Length)
            {
                var rs = ps.replayStates[f];
                if (_puckShadow.Frame != f) _puckShadow.Reset(f);
                if (rs.bumper != null && rs.bumper != _puckShadow.Bumper) AddBumper(Proto.PuckSlot, rs.bumper);
                _puckShadow.Bumper = rs.bumper;
            }
        }

        private void AddBumper(int slot, Bumper bumper)
        {
            int idx;
            if (!Match.BumperIndex.TryGetValue(bumper, out idx)) return;
            AddEvent(Ev.Bumper); _events.U8(slot); _events.U16((ushort)idx);
        }

        private void AddEvent(Ev ev)
        {
            if (_eventCount >= 200) FlushEvents();
            _events.U8((byte)ev);
            _eventCount++;
        }

        private void FlushEvents()
        {
            if (_eventCount == 0) return;
            Begin(Msg.Events);
            _msg.U8(_eventCount);
            _msg.Bytes(_events.Buffer, 0, _events.Length);
            BroadcastReliable();
            _events.Reset();
            _eventCount = 0;
        }
    }
}
