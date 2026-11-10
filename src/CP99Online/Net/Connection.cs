using System.Collections.Generic;

namespace CP99Online.Net
{
    /// <summary>
    /// Per-peer channel over an unreliable transport: unreliable messages, plus reliable ordered
    /// messages using sequence numbers, cumulative acks and timed resends.
    /// Packet layout: [kind][...]. kind 0 = unreliable payload, 1 = reliable (u16 seq + payload),
    /// 2 = ack (u16 highest in-order seq received).
    /// </summary>
    internal sealed class Connection
    {
        private const byte KindUnreliable = 0;
        private const byte KindReliable = 1;
        private const byte KindAck = 2;
        private const float ResendInterval = 0.2f;

        private sealed class Pending
        {
            public ushort Seq;
            public byte[] Packet;
            public float LastSent;
        }

        public readonly int PeerId;
        public float LastHeard;

        private readonly ITransport _transport;
        private readonly List<Pending> _unacked = new List<Pending>();
        private readonly Dictionary<ushort, byte[]> _early = new Dictionary<ushort, byte[]>();
        private readonly Writer _w = new Writer();
        private ushort _nextSend = 1;
        private ushort _nextExpected = 1;

        public Connection(ITransport transport, int peerId, float now)
        {
            _transport = transport;
            PeerId = peerId;
            LastHeard = now;
        }

        private static bool Newer(ushort a, ushort b) { return (short)(a - b) > 0; }

        public void SendUnreliable(Writer msg)
        {
            _w.Reset();
            _w.U8(KindUnreliable);
            _w.Bytes(msg.Buffer, 0, msg.Length);
            _transport.Send(PeerId, _w.Buffer, _w.Length);
        }

        public void SendReliable(Writer msg, float now)
        {
            _w.Reset();
            _w.U8(KindReliable);
            _w.U16(_nextSend);
            _w.Bytes(msg.Buffer, 0, msg.Length);
            var p = new Pending { Seq = _nextSend, Packet = _w.ToArray(), LastSent = now };
            _nextSend++;
            _unacked.Add(p);
            _transport.Send(PeerId, p.Packet, p.Packet.Length);
        }

        public void Update(float now)
        {
            for (var i = 0; i < _unacked.Count; i++)
            {
                var p = _unacked[i];
                if (now - p.LastSent < ResendInterval) continue;
                p.LastSent = now;
                _transport.Send(PeerId, p.Packet, p.Packet.Length);
            }
        }

        public int PendingReliable { get { return _unacked.Count; } }

        /// <summary>Feeds a raw packet; appends any deliverable message payloads to <paramref name="output"/>.</summary>
        public void Receive(byte[] data, int length, float now, List<Reader> output)
        {
            LastHeard = now;
            if (length < 1) return;
            var kind = data[0];
            if (kind == KindUnreliable)
            {
                output.Add(new Reader(data, 1, length - 1));
            }
            else if (kind == KindReliable && length >= 3)
            {
                var seq = (ushort)(data[1] | (data[2] << 8));
                if (seq == _nextExpected)
                {
                    output.Add(new Reader(data, 3, length - 3));
                    _nextExpected++;
                    byte[] next;
                    while (_early.TryGetValue(_nextExpected, out next))
                    {
                        _early.Remove(_nextExpected);
                        output.Add(new Reader(next, 3, next.Length - 3));
                        _nextExpected++;
                    }
                }
                else if (Newer(seq, _nextExpected) && !_early.ContainsKey(seq) && _early.Count < 1024)
                {
                    var copy = new byte[length];
                    System.Buffer.BlockCopy(data, 0, copy, 0, length);
                    _early[seq] = copy;
                }
                SendAck();
            }
            else if (kind == KindAck && length >= 3)
            {
                var ack = (ushort)(data[1] | (data[2] << 8));
                _unacked.RemoveAll(p => !Newer(p.Seq, ack));
            }
        }

        private void SendAck()
        {
            var ack = (ushort)(_nextExpected - 1);
            var pkt = new byte[] { KindAck, (byte)ack, (byte)(ack >> 8) };
            _transport.Send(PeerId, pkt, pkt.Length);
        }
    }
}
