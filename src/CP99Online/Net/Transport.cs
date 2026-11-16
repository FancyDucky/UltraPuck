using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Steamworks;

namespace CP99Online.Net
{
    /// <summary>
    /// Unreliable datagram transport. Peers are small integer ids. Reliability and ordering are
    /// layered on top by <see cref="Connection"/>, so UDP and Steam share one code path.
    /// </summary>
    internal interface ITransport : IDisposable
    {
        string Name { get; }
        void Send(int peer, byte[] data, int length);
        /// <summary>Returns false when no more packets are waiting.</summary>
        bool Receive(out int peer, out byte[] data, out int length);
        void Disconnect(int peer);
    }

    internal sealed class UdpTransport : ITransport
    {
        public const int MaxPacket = 1400;
        private readonly Socket _socket;
        private readonly byte[] _recv = new byte[2048];
        private readonly Dictionary<string, int> _idByEndpoint = new Dictionary<string, int>();
        private readonly Dictionary<int, EndPoint> _endpointById = new Dictionary<int, EndPoint>();
        private int _nextId = 1;

        public string Name { get { return "Direct IP"; } }

        private UdpTransport(Socket s) { _socket = s; }

        public static UdpTransport Listen(string bindAddress, int port)
        {
            var s = Make();
            s.Bind(new IPEndPoint(ParseBind(bindAddress), port));
            return new UdpTransport(s);
        }

        /// <summary>Client side: the host is always peer 0.</summary>
        public static UdpTransport Connect(string host, int port)
        {
            var s = Make();
            s.Bind(new IPEndPoint(IPAddress.Any, 0));
            var t = new UdpTransport(s);
            var ep = new IPEndPoint(Resolve(host), port);
            t._idByEndpoint[ep.ToString()] = 0;
            t._endpointById[0] = ep;
            return t;
        }

        private static IPAddress ParseBind(string a)
        {
            IPAddress ip;
            return !string.IsNullOrEmpty(a) && IPAddress.TryParse(a, out ip) ? ip : IPAddress.Any;
        }

        private static IPAddress Resolve(string host)
        {
            IPAddress ip;
            if (IPAddress.TryParse(host, out ip)) return ip;
            foreach (var a in Dns.GetHostEntry(host).AddressList)
                if (a.AddressFamily == AddressFamily.InterNetwork) return a;
            throw new Exception("Could not resolve " + host);
        }

        private static Socket Make()
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Blocking = false;
            try
            {
                // Stop Windows reporting ICMP "port unreachable" as a receive error (SIO_UDP_CONNRESET).
                s.IOControl(-1744830452, new byte[] { 0, 0, 0, 0 }, null);
            }
            catch { }
            return s;
        }

        public void Send(int peer, byte[] data, int length)
        {
            EndPoint ep;
            if (!_endpointById.TryGetValue(peer, out ep)) return;
            try { _socket.SendTo(data, 0, length, SocketFlags.None, ep); }
            catch (SocketException) { }
        }

        public bool Receive(out int peer, out byte[] data, out int length)
        {
            peer = -1; data = null; length = 0;
            while (true)
            {
                int n;
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                try
                {
                    if (_socket.Available <= 0) return false;
                    n = _socket.ReceiveFrom(_recv, ref from);
                }
                catch (SocketException) { continue; }
                catch (ObjectDisposedException) { return false; }

                var key = from.ToString();
                int id;
                if (!_idByEndpoint.TryGetValue(key, out id))
                {
                    id = _nextId++;
                    _idByEndpoint[key] = id;
                    _endpointById[id] = from;
                }
                peer = id;
                data = new byte[n];
                System.Buffer.BlockCopy(_recv, 0, data, 0, n);
                length = n;
                return true;
            }
        }

        public void Disconnect(int peer)
        {
            EndPoint ep;
            if (!_endpointById.TryGetValue(peer, out ep)) return;
            _endpointById.Remove(peer);
            _idByEndpoint.Remove(ep.ToString());
        }

        public void Dispose()
        {
            try { _socket.Close(); } catch { }
        }
    }

    /// <summary>Steam P2P (relayed / NAT-punched by Steam). Host is peer 0 on the client side.</summary>
    internal sealed class SteamTransport : ITransport
    {
        private readonly Dictionary<ulong, int> _idBySteam = new Dictionary<ulong, int>();
        private readonly Dictionary<int, CSteamID> _steamById = new Dictionary<int, CSteamID>();
        private readonly bool _isHost;
        private int _nextId = 1;
        private Callback<P2PSessionRequest_t> _sessionRequest;
        private readonly byte[] _recv = new byte[4096];

        public string Name { get { return "Steam"; } }

        private SteamTransport(bool isHost)
        {
            _isHost = isHost;
            _sessionRequest = Callback<P2PSessionRequest_t>.Create(OnSessionRequest);
        }

        public static SteamTransport Listen() { return new SteamTransport(true); }

        public static SteamTransport Connect(CSteamID host)
        {
            var t = new SteamTransport(false);
            t._idBySteam[host.m_SteamID] = 0;
            t._steamById[0] = host;
            return t;
        }

        private void OnSessionRequest(P2PSessionRequest_t req)
        {
            // Hosts accept anyone (the Hello handshake decides); clients only accept their host.
            if (_isHost || _idBySteam.ContainsKey(req.m_steamIDRemote.m_SteamID))
                SteamNetworking.AcceptP2PSessionWithUser(req.m_steamIDRemote);
        }

        /// <summary>For the log: whether Steam sends this peer's packets straight across or through its relay servers.</summary>
        public string Route(int peer)
        {
            CSteamID id;
            P2PSessionState_t st;
            if (!_steamById.TryGetValue(peer, out id) || !SteamNetworking.GetP2PSessionState(id, out st)) return "route unknown";
            return st.m_bUsingRelay != 0 ? "through Steam's relay" : "direct";
        }

        public CSteamID SteamIdOf(int peer)
        {
            CSteamID id;
            return _steamById.TryGetValue(peer, out id) ? id : CSteamID.Nil;
        }

        public void Send(int peer, byte[] data, int length)
        {
            CSteamID id;
            if (!_steamById.TryGetValue(peer, out id)) return;
            var mode = length > 1100 ? EP2PSend.k_EP2PSendReliable : EP2PSend.k_EP2PSendUnreliableNoDelay;
            SteamNetworking.SendP2PPacket(id, data, (uint)length, mode, 0);
        }

        public bool Receive(out int peer, out byte[] data, out int length)
        {
            peer = -1; data = null; length = 0;
            uint size;
            while (SteamNetworking.IsP2PPacketAvailable(out size, 0))
            {
                var buf = size <= _recv.Length ? _recv : new byte[size];
                uint read;
                CSteamID from;
                if (!SteamNetworking.ReadP2PPacket(buf, size, out read, out from, 0)) return false;

                int id;
                if (!_idBySteam.TryGetValue(from.m_SteamID, out id))
                {
                    if (!_isHost) continue; // stray packet from someone other than our host
                    id = _nextId++;
                    _idBySteam[from.m_SteamID] = id;
                    _steamById[id] = from;
                }
                peer = id;
                data = new byte[read];
                System.Buffer.BlockCopy(buf, 0, data, 0, (int)read);
                length = (int)read;
                return true;
            }
            return false;
        }

        public void Disconnect(int peer)
        {
            CSteamID id;
            if (!_steamById.TryGetValue(peer, out id)) return;
            SteamNetworking.CloseP2PSessionWithUser(id);
            _steamById.Remove(peer);
            _idBySteam.Remove(id.m_SteamID);
        }

        public void Dispose()
        {
            foreach (var id in _steamById.Values) SteamNetworking.CloseP2PSessionWithUser(id);
            _steamById.Clear();
            _idBySteam.Clear();
            if (_sessionRequest != null) { _sessionRequest.Unregister(); _sessionRequest = null; }
        }
    }
}

namespace CP99Online.Net
{
    /// <summary>
    /// Tests only (CP99_TEST_LAG="ms,wobbleMs[,lossPercent]"): holds every incoming packet back by a delay (plus a
    /// random wobble, which can reorder packets like the real internet) and drops some, so online play can be tried
    /// at realistic ping on one PC. Set it on both test instances for a round trip of about 2 x ms.
    /// </summary>
    internal sealed class LagTransport : ITransport
    {
        private struct Held { public float At; public int Peer; public byte[] Data; public int Length; }

        private readonly ITransport _inner;
        private readonly float _delay, _wobble, _loss;
        private readonly List<Held> _held = new List<Held>();
        private readonly System.Random _rng = new System.Random();

        private LagTransport(ITransport inner, float delay, float wobble, float loss)
        {
            _inner = inner; _delay = delay; _wobble = wobble; _loss = loss;
        }

        /// <summary>The transport itself, or wrapped in a LagTransport when CP99_TEST_LAG is set.</summary>
        internal static ITransport Wrap(ITransport inner)
        {
            var env = Environment.GetEnvironmentVariable("CP99_TEST_LAG");
            if (string.IsNullOrEmpty(env) || inner == null) return inner;
            var parts = env.Split(',');
            float ms, wob = 0f, loss = 0f;
            if (!float.TryParse(parts[0], out ms)) return inner;
            if (parts.Length > 1) float.TryParse(parts[1], out wob);
            if (parts.Length > 2) float.TryParse(parts[2], out loss);
            NetLog.Info("TEST lag: incoming packets held " + ms + " ms (+0-" + wob + " ms), " + loss + "% dropped");
            return new LagTransport(inner, ms / 1000f, wob / 1000f, loss / 100f);
        }

        public string Name { get { return _inner.Name; } }
        public void Send(int peer, byte[] data, int length) { _inner.Send(peer, data, length); }
        public void Disconnect(int peer) { _inner.Disconnect(peer); }
        public void Dispose() { _inner.Dispose(); }

        public bool Receive(out int peer, out byte[] data, out int length)
        {
            var now = UnityEngine.Time.realtimeSinceStartup;
            int p; byte[] d; int n;
            while (_inner.Receive(out p, out d, out n))
            {
                if (_rng.NextDouble() < _loss) continue;
                var copy = new byte[n];
                Buffer.BlockCopy(d, 0, copy, 0, n);
                _held.Add(new Held { At = now + _delay + (float)_rng.NextDouble() * _wobble, Peer = p, Data = copy, Length = n });
            }
            var best = -1;
            for (var i = 0; i < _held.Count; i++)
                if (_held[i].At <= now && (best < 0 || _held[i].At < _held[best].At)) best = i;
            if (best < 0) { peer = -1; data = null; length = 0; return false; }
            var h = _held[best];
            _held.RemoveAt(best);
            peer = h.Peer; data = h.Data; length = h.Length;
            return true;
        }
    }
}
