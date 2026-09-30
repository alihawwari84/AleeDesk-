using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace RemoteDesk
{
    // Shared helpers: framing, key derivation, and the encrypted channel.
    public static class Proto
    {
        public const int MaxFrame = 32 * 1024 * 1024;

        public static byte[] ReadExact(Stream s, int n)
        {
            byte[] buf = new byte[n];
            int off = 0;
            while (off < n)
            {
                int r = s.Read(buf, off, n - off);
                if (r <= 0) throw new EndOfStreamException();
                off += r;
            }
            return buf;
        }

        public static byte[] ReadFrame(Stream s, int max)
        {
            int len = BitConverter.ToInt32(ReadExact(s, 4), 0);
            if (len <= 0 || len > max) throw new InvalidDataException("Bad frame length");
            return ReadExact(s, len);
        }

        public static byte[] Rand(int n)
        {
            byte[] b = new byte[n];
            using (RNGCryptoServiceProvider r = new RNGCryptoServiceProvider()) r.GetBytes(b);
            return b;
        }

        public static byte[] Cat(params byte[][] parts)
        {
            int n = 0;
            foreach (byte[] p in parts) n += p.Length;
            byte[] o = new byte[n];
            int off = 0;
            foreach (byte[] p in parts) { Buffer.BlockCopy(p, 0, o, off, p.Length); off += p.Length; }
            return o;
        }

        public static byte[] Sub(byte[] a, int off, int len)
        {
            byte[] o = new byte[len];
            Buffer.BlockCopy(a, off, o, 0, len);
            return o;
        }

        public static byte[] Hmac(byte[] key, params byte[][] parts)
        {
            using (HMACSHA256 h = new HMACSHA256(key)) return h.ComputeHash(Cat(parts));
        }

        public static byte[] Text(string s) { return Encoding.UTF8.GetBytes(s); }
        public static string Str(byte[] b) { return Encoding.UTF8.GetString(b); }

        public static bool Eq(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int d = 0;
            for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i];
            return d == 0;
        }

        public static byte[] DeriveKey(string password, string id)
        {
            using (Rfc2898DeriveBytes k = new Rfc2898DeriveBytes(password, Text("RemoteDesk|" + id), 60000))
                return k.GetBytes(32);
        }

        public static byte[] Ints(params int[] v)
        {
            byte[] b = new byte[v.Length * 4];
            for (int i = 0; i < v.Length; i++) Buffer.BlockCopy(BitConverter.GetBytes(v[i]), 0, b, i * 4, 4);
            return b;
        }

        public static int Int(byte[] d, int index) { return BitConverter.ToInt32(d, index * 4); }

        public static byte[] LongBytes(long v) { return BitConverter.GetBytes(v); }
        public static long LongAt(byte[] d, int byteOffset) { return BitConverter.ToInt64(d, byteOffset); }
    }

    // Length-prefixed message channel. Plain until EnableSecurity is called, after which
    // every message is AES-256-CBC encrypted and HMAC-SHA256 authenticated with a counter
    // (so tampering, replay and reordering are rejected).
    public class Channel
    {
        readonly Stream s;
        readonly object wlock = new object();
        byte[] encKey, macKey;
        bool secure;
        ulong sendCtr, recvCtr;
        byte sendDir, recvDir;

        public int MaxLen = Proto.MaxFrame;
        public Action Closer;

        public Channel(Stream s) { this.s = s; }

        public void EnableSecurity(byte[] enc, byte[] mac, bool isHost)
        {
            encKey = enc; macKey = mac;
            sendDir = (byte)(isHost ? 1 : 2);
            recvDir = (byte)(isHost ? 2 : 1);
            secure = true;
        }

        public void Close()
        {
            try { if (Closer != null) Closer(); } catch { }
            try { s.Close(); } catch { }
        }

        public void Send(byte type, byte[] data)
        {
            if (data == null) data = new byte[0];
            byte[] plain = new byte[data.Length + 1];
            plain[0] = type;
            Buffer.BlockCopy(data, 0, plain, 1, data.Length);
            lock (wlock)
            {
                byte[] blob = secure ? Seal(plain) : plain;
                byte[] frame = new byte[4 + blob.Length];
                Buffer.BlockCopy(BitConverter.GetBytes(blob.Length), 0, frame, 0, 4);
                Buffer.BlockCopy(blob, 0, frame, 4, blob.Length);
                s.Write(frame, 0, frame.Length);
                s.Flush();
            }
        }

        public void Send(char type, byte[] data) { Send((byte)type, data); }

        public byte Recv(out byte[] data)
        {
            byte[] blob = Proto.ReadFrame(s, MaxLen);
            byte[] plain = secure ? Open(blob) : blob;
            data = Proto.Sub(plain, 1, plain.Length - 1);
            return plain[0];
        }

        byte[] Seal(byte[] plain)
        {
            byte[] iv = Proto.Rand(16);
            byte[] ct;
            using (AesCryptoServiceProvider aes = new AesCryptoServiceProvider())
            {
                aes.Key = encKey; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                using (ICryptoTransform t = aes.CreateEncryptor()) ct = t.TransformFinalBlock(plain, 0, plain.Length);
            }
            byte[] tag = Proto.Hmac(macKey, new byte[] { sendDir }, BitConverter.GetBytes(sendCtr), iv, ct);
            sendCtr++;
            return Proto.Cat(iv, ct, tag);
        }

        byte[] Open(byte[] blob)
        {
            if (blob.Length < 16 + 16 + 32) throw new InvalidDataException("Short frame");
            byte[] iv = Proto.Sub(blob, 0, 16);
            byte[] ct = Proto.Sub(blob, 16, blob.Length - 48);
            byte[] tag = Proto.Sub(blob, blob.Length - 32, 32);
            byte[] expect = Proto.Hmac(macKey, new byte[] { recvDir }, BitConverter.GetBytes(recvCtr), iv, ct);
            if (!Proto.Eq(expect, tag)) throw new InvalidDataException("Authentication failed");
            recvCtr++;
            using (AesCryptoServiceProvider aes = new AesCryptoServiceProvider())
            {
                aes.Key = encKey; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                using (ICryptoTransform t = aes.CreateDecryptor()) return t.TransformFinalBlock(ct, 0, ct.Length);
            }
        }
    }
}
