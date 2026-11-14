using System;
using System.Collections.Generic;
using UnityEngine;

namespace CP99Online.Net
{
    internal enum Role { None, Host, Client }

    /// <summary>
    /// Owns the transport and connections and dispatches messages. Host-side logic lives in
    /// NetSession.Host.cs and client-side logic in NetSession.Client.cs.
    /// </summary>
    internal sealed partial class NetSession : MonoBehaviour
    {
        public static NetSession Instance { get; private set; }

        public Role Role { get; private set; }
        public string Status { get; private set; }
        public string TransportName { get { return _transport == null ? string.Empty : _transport.Name; } }

        private ITransport _transport;
        private readonly Dictionary<int, Connection> _conns = new Dictionary<int, Connection>();
        private readonly List<Reader> _inbox = new List<Reader>();
        private readonly Writer _msg = new Writer();
        private float _lastKeepAlive;

        internal static float Now { get { return Time.realtimeSinceStartup; } }

        public static bool IsHost { get { return Instance != null && Instance.Role == Role.Host; } }
        public static bool IsClient { get { return Instance != null && Instance.Role == Role.Client; } }
        public static bool HostInMatch { get { return IsHost && Match.Active; } }
        public static bool ClientInMatch { get { return IsClient && Match.Active; } }

        private void Awake()
        {
            Instance = this;
            Status = "Offline";
        }

        private void OnApplicationQuit() { Stop("Game closed"); }

        private void Update()
        {
            if (_transport == null) return;
            Pump();
            if (_transport == null) return;

            if (Role == Role.Host) HostUpdate();
            else if (Role == Role.Client) ClientUpdate();
            if (_transport == null) return;

            foreach (var c in _conns.Values) c.Update(Now);
            if (Now - _lastKeepAlive > 1f)
            {
                _lastKeepAlive = Now;
                Begin(Msg.KeepAlive);
                foreach (var c in _conns.Values) c.SendUnreliable(_msg);
            }
        }

        private void FixedUpdate()
        {
            if (_transport != null && Role == Role.Client) ClientFixedUpdate();
        }

        private void LateUpdate()
        {
            if (_transport == null) return;
            if (Role == Role.Host) HostLateUpdate();
            else if (Role == Role.Client) ClientLateUpdate();
        }

        private void OnLevelWasLoaded(int level)
        {
            var scene = Application.loadedLevelName;
            NetLog.Debug("Scene loaded: " + scene);
            if (Role == Role.Host) HostOnSceneLoaded(scene);
            else if (Role == Role.Client) ClientOnSceneLoaded(scene);
            if (scene != "Arena3D" && (Match.Active || Match.ClientPending))
            {
                NetLog.Info("Left the match scene");
                Match.End();
            }
        }

        private void Pump()
        {
            int peer; byte[] data; int len;
            var guard = 0;
            while (_transport != null && guard++ < 4000 && _transport.Receive(out peer, out data, out len))
            {
                Connection c;
                if (!_conns.TryGetValue(peer, out c))
                {
                    if (Role != Role.Host || _conns.Count >= Proto.MaxClients * 2) continue;
                    c = new Connection(_transport, peer, Now);
                    _conns[peer] = c;
                }
                _inbox.Clear();
                c.Receive(data, len, Now, _inbox);
                for (var i = 0; i < _inbox.Count; i++)
                {
                    try
                    {
                        var r = _inbox[i];
                        var type = (Msg)r.U8();
                        if (Role == Role.Host) HostHandle(c, type, r);
                        else ClientHandle(c, type, r);
                    }
                    catch (Exception e)
                    {
                        NetLog.Warn("Bad message from peer " + peer + ": " + e);
                    }
                    if (_transport == null) return;
                }
            }
        }

        private Writer Begin(Msg type)
        {
            _msg.Reset();
            _msg.U8((byte)type);
            return _msg;
        }

        /// <summary>Ends hosting or leaves the host. Safe to call at any time.</summary>
        public void Stop(string reason)
        {
            if (Role == Role.None && _transport == null) return;
            var wasClientInMatch = ClientInMatch || Match.ClientPending && IsClient;
            try
            {
                Begin(Msg.Bye);
                _msg.Str(reason);
                foreach (var c in _conns.Values) c.SendUnreliable(_msg);
            }
            catch { }
            if (_transport != null)
            {
                try { _transport.Dispose(); } catch { }
                _transport = null;
            }
            _conns.Clear();
            var was = Role;
            Role = Role.None;
            HostReset();
            ClientReset();
            SteamLobby.Leave();
            Status = reason;
            NetLog.Info("Session ended (" + was + "): " + reason);

            if (wasClientInMatch)
            {
                Match.End();
                Time.timeScale = 1f;
                PlayerSelectionManager_V2.players = null;
                Application.LoadLevel("LoadMainMenu");
            }
        }

        internal static string LocalName()
        {
            var n = Plugin.PlayerName.Value;
            if (string.IsNullOrEmpty(n.Trim()) && SteamLobby.Available)
            {
                try { n = Steamworks.SteamFriends.GetPersonaName(); } catch { }
            }
            return Clean(n);
        }

        internal static string Clean(string s)
        {
            s = (s ?? string.Empty).Trim();
            if (s.Length == 0) s = "Player";
            if (s.Length > 16) s = s.Substring(0, 16);
            return s.ToUpperInvariant();
        }
    }
}
