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
