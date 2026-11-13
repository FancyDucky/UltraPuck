using System;
using System.Text;

namespace CP99Online.Net
{
    /// <summary>Little-endian binary writer with a reusable buffer.</summary>
    internal sealed class Writer
    {
        private byte[] _buf = new byte[512];
        private int _len;

        public int Length { get { return _len; } }
        public byte[] Buffer { get { return _buf; } }

        public Writer Reset() { _len = 0; return this; }

        private void Ensure(int n)
        {
            if (_len + n <= _buf.Length) return;
            var nb = new byte[Math.Max(_buf.Length * 2, _len + n)];
            System.Buffer.BlockCopy(_buf, 0, nb, 0, _len);
            _buf = nb;
        }

        public void U8(byte v) { Ensure(1); _buf[_len++] = v; }
        public void U8(int v) { U8((byte)v); }
        public void Bool(bool v) { U8(v ? (byte)1 : (byte)0); }
        public void U16(ushort v) { Ensure(2); _buf[_len++] = (byte)v; _buf[_len++] = (byte)(v >> 8); }
        public void I16(short v) { U16((ushort)v); }
        public void U32(uint v) { Ensure(4); for (var i = 0; i < 4; i++) _buf[_len++] = (byte)(v >> (8 * i)); }
        public void I32(int v) { U32((uint)v); }
        public void F32(float v) { var b = BitConverter.GetBytes(v); Ensure(4); System.Buffer.BlockCopy(b, 0, _buf, _len, 4); _len += 4; }

        public void Str(string s)
        {
            var b = Encoding.UTF8.GetBytes(s ?? string.Empty);
            var n = Math.Min(b.Length, 255);
            U8((byte)n);
            Ensure(n);
            System.Buffer.BlockCopy(b, 0, _buf, _len, n);
            _len += n;
        }

        public void Bytes(byte[] src, int offset, int count)
        {
            Ensure(count);
            System.Buffer.BlockCopy(src, offset, _buf, _len, count);
            _len += count;
        }

        public byte[] ToArray()
        {
            var r = new byte[_len];
            System.Buffer.BlockCopy(_buf, 0, r, 0, _len);
            return r;
        }
    }

    internal sealed class WireException : Exception
    {
        public WireException(string m) : base(m) { }
    }

    internal sealed class Reader
    {
        private readonly byte[] _buf;
        private readonly int _end;
        private int _pos;

        public Reader(byte[] buf, int offset, int count)
        {
            _buf = buf; _pos = offset; _end = offset + count;
        }

        public int Remaining { get { return _end - _pos; } }

        private void Need(int n) { if (_pos + n > _end) throw new WireException("truncated packet"); }

        public byte U8() { Need(1); return _buf[_pos++]; }
        public bool Bool() { return U8() != 0; }
        public ushort U16() { Need(2); var v = (ushort)(_buf[_pos] | (_buf[_pos + 1] << 8)); _pos += 2; return v; }
        public short I16() { return (short)U16(); }

        public byte[] Raw(int n)
        {
            Need(n);
            var b = new byte[n];
            System.Buffer.BlockCopy(_buf, _pos, b, 0, n);
            _pos += n;
            return b;
        }

        public uint U32() { Need(4); uint v = 0; for (var i = 0; i < 4; i++) v |= (uint)_buf[_pos + i] << (8 * i); _pos += 4; return v; }
        public int I32() { return (int)U32(); }
        public float F32() { Need(4); var v = BitConverter.ToSingle(_buf, _pos); _pos += 4; return v; }

        public string Str()
        {
            var n = U8();
            Need(n);
            var s = Encoding.UTF8.GetString(_buf, _pos, n);
            _pos += n;
            return s;
        }
    }
}
