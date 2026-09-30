using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace RemoteDesk
{
    // Shares this computer's screen. Keeps a connection open to the relay under our ID and,
    // when a viewer is paired, authenticates it, asks the user for consent, then streams
    // changed screen regions and applies the viewer's input.
    //
    // Messages (inside the encrypted channel)
    //   host -> viewer:  W waiting for consent | G granted | D denied | S w,h | R x,y,w,h,jpeg
    //   viewer -> host:  M kind,x,y,data | k vk,down | C utf8 text | Q quality,scale | X bye
    public class HostService
    {
        class Session
        {
            public TcpClient Client;
            public volatile bool Active = true;
            public volatile int Quality = 60;
            public volatile int Scale = 100;
            public HashSet<int> Pressed = new HashSet<int>();
            public Dictionary<int, FileRecv> Files = new Dictionary<int, FileRecv>();
        }

        class FileRecv
        {
            public FileStream Stream;
            public string TempPath, FinalPath, Name;
            public long Expected, Written;
        }

        public string Id;
        public string Server = Brand.DefaultRelay;
        public string SaveDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        public bool RequireConsent = true;
        public Func<string, bool> Consent;             // viewer address -> allow?
        public Action<string> SetClipboard;
        public Action<string> Status;
        public Action<bool, string> SessionChanged;    // active, viewer address
        public Action PasswordChanged;

        readonly object pwLock = new object();
        string password;
        byte[] pwKey;
        volatile bool run;
        int gen;
        int failCount;
        TcpClient current;
        volatile Session session;

        public string Password { get { lock (pwLock) return password; } }

        public void NewPassword()
        {
            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // 32 symbols, no lookalikes
            byte[] r = Proto.Rand(10);
            char[] c = new char[10];
            for (int i = 0; i < c.Length; i++) c[i] = alphabet[r[i] % 32];
            string p = new string(c);
            byte[] k = Proto.DeriveKey(p, Id);
            lock (pwLock) { password = p; pwKey = k; }
            if (PasswordChanged != null) PasswordChanged();
        }

        public void Start()
        {
            Stop();
            run = true;
            int my = ++gen;
            Thread t = new Thread(delegate() { Loop(my); });
            t.IsBackground = true;
            t.Start();
        }

        public void Stop()
        {
            run = false;
            gen++;
            AbortSession();
            TcpClient c = current;
            if (c != null) { try { c.Close(); } catch { } }
        }

        public void AbortSession()
        {
            Session s = session;
            if (s != null) { s.Active = false; try { s.Client.Close(); } catch { } }
        }

        // A viewer on the same network connecting straight to us (see Lan.cs). It opens like a
        // relay pairing ('V'+our id -> 'O'), then runs the normal password check and session.
        public void AcceptDirect(TcpClient c)
        {
            Thread t = new Thread(delegate()
            {
                try
                {
                    c.NoDelay = true;
                    NetworkStream s = c.GetStream();
                    s.ReadTimeout = 10000;
                    Channel ch = new Channel(s);
                    ch.MaxLen = 128;
                    byte[] d;
                    if (ch.Recv(out d) != 'V' || Proto.Str(d) != Id) { ch.Send('E', Proto.Text("Remote ID not found on this network")); return; }
                    ch.MaxLen = Proto.MaxFrame;
                    ch.Send('O', null);
                    RunSession(ch, c, ((System.Net.IPEndPoint)c.Client.RemoteEndPoint).Address.ToString() + " (local network)");
                }
                catch { }
                finally { try { c.Close(); } catch { } }
            });
            t.IsBackground = true;
            t.Start();
        }

        void SetStatus(string s) { if (Status != null) Status(s); }

        static void ParseServer(string server, out string host, out int port)
        {
            server = server.Trim();
            int i = server.LastIndexOf(':');
            host = server; port = 7788;
            if (i > 0 && int.TryParse(server.Substring(i + 1), out port)) host = server.Substring(0, i);
            else port = 7788;
        }

        void Loop(int my)
        {
            while (run && gen == my)
            {
                TcpClient c = null;
                int delayMs = 2500;
                try
                {
                    SetStatus("Connecting to relay...");
                    string host; int port;
                    ParseServer(Server, out host, out port);
                    c = new TcpClient();
                    IAsyncResult ar = c.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(5000)) throw new Exception("relay not reachable");
                    c.EndConnect(ar);
                    current = c;
                    c.NoDelay = true;
                    c.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                    NetworkStream s = c.GetStream();
                    s.ReadTimeout = 10000;
                    Channel ch = new Channel(s);
                    ch.Closer = delegate() { c.Close(); };
                    ch.Send('H', Proto.Text(Id));
                    byte[] d;
                    byte t = ch.Recv(out d);
                    if (t != 'O') throw new Exception(t == 'E' ? Proto.Str(d) : "unexpected reply");
                    s.ReadTimeout = Timeout.Infinite;
                    SetStatus("Ready - waiting for connections");
                    while (true)
                    {
                        t = ch.Recv(out d);
                        if (t == 'K') continue;
                        if (t == 'P') break;
                        throw new Exception("unexpected message");
                    }
                    delayMs = 300;
                    RunSession(ch, c, Proto.Str(d));
                }
                catch (Exception ex)
                {
                    if (run && gen == my) SetStatus("Offline: " + ex.Message);
                }
                finally
                {
                    if (c != null) { try { c.Close(); } catch { } }
                }
                for (int i = 0; i < delayMs / 100 && run && gen == my; i++) Thread.Sleep(100);
            }
        }

        void RunSession(Channel ch, TcpClient client, string viewerIp)
        {
            NetworkStream s = client.GetStream();
            s.ReadTimeout = 15000;
            byte[] nH = Proto.Rand(16);
            ch.Send('N', nH);
            byte[] d;
            byte t = ch.Recv(out d);
            if (t != 'A' || d.Length != 48) return;
            byte[] nV = Proto.Sub(d, 0, 16);
            byte[] proof = Proto.Sub(d, 16, 32);
            byte[] key;
            lock (pwLock) key = pwKey;

            if (!Proto.Eq(Proto.Hmac(key, Proto.Text("auth"), nH, nV), proof))
            {
                ch.Send('F', null);
                Thread.Sleep(1500);
                if (++failCount >= 5) { failCount = 0; NewPassword(); }
                return;
            }
            failCount = 0;
            ch.Send('Y', Proto.Hmac(key, Proto.Text("host"), nH, nV));
            ch.EnableSecurity(Proto.Hmac(key, Proto.Text("enc"), nH, nV), Proto.Hmac(key, Proto.Text("mac"), nH, nV), true);
            s.ReadTimeout = Timeout.Infinite;
            ch.Send('W', null);

            if (session != null || (RequireConsent && (Consent == null || !Consent(viewerIp))))
            {
                ch.Send('D', null); // declined, or already in a session with someone else
                return;
            }

            Rectangle scr = Screen.PrimaryScreen.Bounds;
            Session sess = new Session();
            sess.Client = client;
            session = sess;
            ch.Send('G', null);
            if (SessionChanged != null) SessionChanged(true, viewerIp);
            SetStatus("Connected - " + viewerIp + " is viewing this screen");

            Thread cap = new Thread(delegate()
            {
                try { CaptureLoop(ch, sess, scr); }
                catch (Exception ex) { SetStatus("Capture error: " + ex.Message); }
                sess.Active = false;
                try { client.Close(); } catch { }
            });
            cap.IsBackground = true;
            cap.Start();

            try { InputLoop(ch, sess); }
            catch { }
            finally
            {
                sess.Active = false;
                try { client.Close(); } catch { }
                cap.Join(2000);
                foreach (int vk in sess.Pressed) InputInjector.Key(vk, false);
                lock (sess.Files)
                {
                    foreach (FileRecv fr in sess.Files.Values)
                    {
                        try { fr.Stream.Close(); } catch { }
                        try { File.Delete(fr.TempPath); } catch { }
                    }
                    sess.Files.Clear();
                }
                session = null;
                if (SessionChanged != null) SessionChanged(false, viewerIp);
            }
        }

        void InputLoop(Channel ch, Session sess)
        {
            while (sess.Active)
            {
                byte[] d;
                byte t = ch.Recv(out d);
                switch ((char)t)
                {
                    case 'M':
                        if (d.Length >= 16)
                            InputInjector.Mouse(Proto.Int(d, 0), Proto.Int(d, 1), Proto.Int(d, 2), Proto.Int(d, 3));
                        break;
                    case 'k':
                        if (d.Length >= 8)
                        {
                            int vk = Proto.Int(d, 0);
                            bool down = Proto.Int(d, 1) != 0;
                            if (down) sess.Pressed.Add(vk); else sess.Pressed.Remove(vk);
                            InputInjector.Key(vk, down);
                        }
                        break;
                    case 'C':
                        if (SetClipboard != null && d.Length < 1024 * 1024) SetClipboard(Proto.Str(d));
                        break;
                    case 'Q':
                        if (d.Length >= 8)
                        {
                            sess.Quality = Math.Max(20, Math.Min(95, Proto.Int(d, 0)));
                            sess.Scale = Math.Max(25, Math.Min(100, Proto.Int(d, 1)));
                        }
                        break;
                    case 'T': StartIncomingFile(sess, d); break;
                    case 'U': WriteFileChunk(sess, d); break;
                    case 'Z': FinishIncomingFile(sess, d); break;
                    case 'X':
                        return;
                }
            }
        }

        // ---- incoming file transfer (viewer -> this computer) ---------------------------

        void StartIncomingFile(Session sess, byte[] d)
        {
            try
            {
                int fileId = Proto.Int(d, 0);
                long size = Proto.LongAt(d, 4);
                string name = Proto.Str(Proto.Sub(d, 12, d.Length - 12));
                name = SanitizeName(name);
                if (size < 0 || size > 20L * 1024 * 1024 * 1024) throw new Exception("bad size");

                Directory.CreateDirectory(SaveDir);
                string finalPath = UniquePath(Path.Combine(SaveDir, name));
                FileRecv fr = new FileRecv
                {
                    Stream = new FileStream(finalPath + ".part", FileMode.Create, FileAccess.Write),
                    TempPath = finalPath + ".part",
                    FinalPath = finalPath,
                    Name = name,
                    Expected = size
                };
                lock (sess.Files) sess.Files[fileId] = fr;
                SetStatus("Receiving " + name + " (" + FormatSize(size) + ")...");
            }
            catch (Exception ex) { SetStatus("File transfer error: " + ex.Message); }
        }

        void WriteFileChunk(Session sess, byte[] d)
        {
            int fileId = Proto.Int(d, 0);
            FileRecv fr;
            lock (sess.Files) sess.Files.TryGetValue(fileId, out fr);
            if (fr == null) return;
            try
            {
                fr.Stream.Write(d, 12, d.Length - 12);
                fr.Written += d.Length - 12;
            }
            catch (Exception ex)
            {
                SetStatus("File transfer error: " + ex.Message);
                AbortIncomingFile(sess, fileId, fr);
            }
        }

        void FinishIncomingFile(Session sess, byte[] d)
        {
            int fileId = Proto.Int(d, 0);
            FileRecv fr;
            lock (sess.Files) { sess.Files.TryGetValue(fileId, out fr); sess.Files.Remove(fileId); }
            if (fr == null) return;
            try
            {
                fr.Stream.Close();
                if (fr.Written != fr.Expected) throw new Exception("size mismatch");
                File.Move(fr.TempPath, fr.FinalPath);
                SetStatus("Received " + fr.Name + " (" + FormatSize(fr.Written) + ") -> " + SaveDir);
            }
            catch (Exception ex)
            {
                SetStatus("File transfer error: " + ex.Message);
                try { File.Delete(fr.TempPath); } catch { }
            }
        }

        void AbortIncomingFile(Session sess, int fileId, FileRecv fr)
        {
            lock (sess.Files) sess.Files.Remove(fileId);
            try { fr.Stream.Close(); } catch { }
            try { File.Delete(fr.TempPath); } catch { }
        }

        static string SanitizeName(string name)
        {
            name = Path.GetFileName(name);
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            if (name.Length == 0) name = "file";
            if (name.Length > 200) name = name.Substring(0, 200);
            return name;
        }

        static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path);
            string baseName = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int i = 1; ; i++)
            {
                string candidate = Path.Combine(dir, baseName + " (" + i + ")" + ext);
                if (!File.Exists(candidate)) return candidate;
            }
        }

        static string FormatSize(long bytes)
        {
            double kb = bytes / 1024.0;
            if (kb < 1024) return kb.ToString("0.#") + " KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return mb.ToString("0.#") + " MB";
            return (mb / 1024.0).ToString("0.#") + " GB";
        }

        // ---- screen capture -------------------------------------------------------------

        const int Tile = 64;
        const int FrameMs = 33; // up to ~30 screen updates per second
        const PixelFormat Fmt = PixelFormat.Format32bppRgb;

        void CaptureLoop(Channel ch, Session sess, Rectangle scr)
        {
            ImageCodecInfo jpg = null;
            foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders())
                if (c.MimeType == "image/jpeg") jpg = c;

            Bitmap full = new Bitmap(scr.Width, scr.Height, Fmt);
            Bitmap scaled = null, prev = null;
            int curScale = -1, w = 0, h = 0;
            bool force = true;
            CursorState pointer = new CursorState();
            try
            {
                while (sess.Active)
                {
                    DateTime t0 = DateTime.UtcNow;
                    int scale = sess.Scale;
                    if (scale != curScale)
                    {
                        if (scaled != null) { scaled.Dispose(); scaled = null; }
                        if (prev != null) prev.Dispose();
                        if (scale == 100) { w = scr.Width; h = scr.Height; }
                        else
                        {
                            w = Math.Max(16, scr.Width * scale / 100);
                            h = Math.Max(16, scr.Height * scale / 100);
                            scaled = new Bitmap(w, h, Fmt);
                        }
                        prev = new Bitmap(w, h, Fmt);
                        ch.Send('S', Proto.Ints(w, h));
                        force = true;
                        curScale = scale;
                    }

                    SendPointer(ch, pointer, scr, scale);
                    using (Graphics g = Graphics.FromImage(full)) GrabScreen(g, scr);
                    Bitmap src = full;
                    if (scaled != null)
                    {
                        using (Graphics g = Graphics.FromImage(scaled))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                            g.DrawImage(full, 0, 0, w, h);
                        }
                        src = scaled;
                    }

                    List<Rectangle> changed = Diff(src, prev, w, h, force);
                    force = false;
                    if (changed.Count == 0) { Thread.Sleep(15); continue; }

                    foreach (Rectangle box in changed)
                    {
                        byte[] jpeg;
                        using (Bitmap crop = src.Clone(box, src.PixelFormat))
                        using (MemoryStream ms = new MemoryStream())
                        using (EncoderParameters ep = new EncoderParameters(1))
                        {
                            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)sess.Quality);
                            crop.Save(ms, jpg, ep);
                            jpeg = ms.ToArray();
                        }
                        ch.Send('R', Proto.Cat(Proto.Ints(box.X, box.Y, box.Width, box.Height), jpeg));
                    }

                    int spent = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
                    if (spent < FrameMs) Thread.Sleep(FrameMs - spent);
                }
            }
            finally
            {
                full.Dispose();
                if (scaled != null) scaled.Dispose();
                if (prev != null) prev.Dispose();
            }
        }

        // SRCCOPY | CAPTUREBLT so layered/transparent windows are included.
        static void GrabScreen(Graphics g, Rectangle scr)
        {
            IntPtr sdc = Native.GetDC(IntPtr.Zero);
            IntPtr hdc = g.GetHdc();
            try { Native.BitBlt(hdc, 0, 0, scr.Width, scr.Height, sdc, scr.X, scr.Y, 0x00CC0020 | 0x40000000); }
            finally { g.ReleaseHdc(hdc); Native.ReleaseDC(IntPtr.Zero, sdc); }
        }

        // Compares src with the previously sent frame in 64x64 tiles, updates prev with the
        // changed tiles and returns the changed areas. Changes far apart are kept as separate
        // rectangles so a small change in one corner doesn't resend the whole screen.
        static List<Rectangle> Diff(Bitmap src, Bitmap prev, int w, int h, bool force)
        {
            List<Rectangle> done = new List<Rectangle>();
            if (force) { CopyAll(src, prev, w, h); done.Add(new Rectangle(0, 0, w, h)); return done; }

            Rectangle all = new Rectangle(0, 0, w, h);
            BitmapData a = src.LockBits(all, ImageLockMode.ReadOnly, Fmt);
            BitmapData b = prev.LockBits(all, ImageLockMode.ReadWrite, Fmt);
            List<Rectangle> open = new List<Rectangle>();
            try
            {
                for (int ty = 0; ty < h; ty += Tile)
                {
                    int th = Math.Min(Tile, h - ty);
                    List<Rectangle> spans = new List<Rectangle>();
                    int runStart = -1, runEnd = -1;
                    for (int tx = 0; tx < w; tx += Tile)
                    {
                        int tw = Math.Min(Tile, w - tx);
                        UIntPtr bytes = new UIntPtr((uint)(tw * 4));
                        bool dirty = false;
                        for (int r = 0; r < th; r++)
                        {
                            IntPtr pa = new IntPtr(a.Scan0.ToInt64() + (long)(ty + r) * a.Stride + tx * 4);
                            IntPtr pb = new IntPtr(b.Scan0.ToInt64() + (long)(ty + r) * b.Stride + tx * 4);
                            if (Native.memcmp(pa, pb, bytes) != 0) { dirty = true; break; }
                        }
                        if (!dirty) continue;
                        for (int r = 0; r < th; r++)
                        {
                            IntPtr pa = new IntPtr(a.Scan0.ToInt64() + (long)(ty + r) * a.Stride + tx * 4);
                            IntPtr pb = new IntPtr(b.Scan0.ToInt64() + (long)(ty + r) * b.Stride + tx * 4);
                            Native.CopyMemory(pb, pa, bytes);
                        }
                        // a gap of 4+ clean tiles starts a new span
                        if (runStart >= 0 && tx - runEnd >= 4 * Tile) { spans.Add(new Rectangle(runStart, ty, runEnd - runStart, th)); runStart = -1; }
                        if (runStart < 0) runStart = tx;
                        runEnd = tx + tw;
                    }
                    if (runStart >= 0) spans.Add(new Rectangle(runStart, ty, runEnd - runStart, th));

                    // grow rectangles from the row above downwards when that doesn't waste much area
                    List<Rectangle> next = new List<Rectangle>();
                    foreach (Rectangle s in spans)
                    {
                        int hit = -1;
                        for (int i = 0; i < open.Count && hit < 0; i++)
                        {
                            Rectangle o = open[i];
                            if (o.Right <= s.Left || s.Right <= o.Left) continue;
                            Rectangle u = Rectangle.Union(o, s);
                            if ((long)u.Width * u.Height * 10 <= ((long)o.Width * o.Height + (long)s.Width * s.Height) * 13) hit = i;
                        }
                        if (hit >= 0) { next.Add(Rectangle.Union(open[hit], s)); open.RemoveAt(hit); }
                        else next.Add(s);
                    }
                    done.AddRange(open);
                    open = next;
                }
                done.AddRange(open);
            }
            finally
            {
                src.UnlockBits(a);
                prev.UnlockBits(b);
            }
            // very scattered changes: one bounding box is cheaper than many small pictures
            if (done.Count > 24)
            {
                Rectangle box = done[0];
                foreach (Rectangle r in done) box = Rectangle.Union(box, r);
                done.Clear();
                done.Add(box);
            }
            return done;
        }

        static void CopyAll(Bitmap src, Bitmap prev, int w, int h)
        {
            using (Graphics g = Graphics.FromImage(prev))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImageUnscaled(src, 0, 0);
            }
        }

        // The mouse pointer is sent separately from the picture: its shape when it changes and
        // its position when it moves. The viewer shows it as its own local cursor, so moving
        // the mouse feels instant instead of waiting for the next screen update.
        class CursorState
        {
            public IntPtr Handle;
            public int Scale = -1, X = int.MinValue, Y = int.MinValue;
            public bool Visible;
        }

        //   host -> viewer:  c hotX,hotY,png (pointer shape) | p x,y,visible (pointer position)
        static void SendPointer(Channel ch, CursorState st, Rectangle scr, int scale)
        {
            CURSORINFO ci = new CURSORINFO();
            ci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
            if (!Native.GetCursorInfo(ref ci)) return;
            bool visible = (ci.flags & 1) != 0;
            if (visible && (ci.hCursor != st.Handle || scale != st.Scale))
            {
                byte[] shape = PointerShape(ci.hCursor, scale);
                if (shape != null) ch.Send('c', shape);
                st.Handle = ci.hCursor;
                st.Scale = scale;
            }
            int x = (ci.pt.x - scr.X) * scale / 100, y = (ci.pt.y - scr.Y) * scale / 100;
            if (x == st.X && y == st.Y && visible == st.Visible) return;
            ch.Send('p', Proto.Ints(x, y, visible ? 1 : 0));
            st.X = x; st.Y = y; st.Visible = visible;
        }

        // Draws the cursor on black and on white to recover real transparency (works for colour,
        // shadowed and old black-and-white cursors). Pixels that invert the screen, like the text
        // I-beam, become black with a white outline so they show on any background.
        static byte[] PointerShape(IntPtr hCursor, int scale)
        {
            ICONINFO ii;
            if (!Native.GetIconInfo(hCursor, out ii)) return null;
            int w, h, hx = ii.xHotspot, hy = ii.yHotspot;
            try
            {
                using (Bitmap m = Image.FromHbitmap(ii.hbmColor != IntPtr.Zero ? ii.hbmColor : ii.hbmMask))
                {
                    w = m.Width;
                    h = ii.hbmColor != IntPtr.Zero ? m.Height : m.Height / 2;
                }
            }
            catch { return null; }
            finally
            {
                if (ii.hbmMask != IntPtr.Zero) Native.DeleteObject(ii.hbmMask);
                if (ii.hbmColor != IntPtr.Zero) Native.DeleteObject(ii.hbmColor);
            }
            if (w <= 0 || h <= 0 || w > 256 || h > 256) return null;

            Bitmap shape = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            try
            {
                using (Bitmap onBlack = RenderPointer(hCursor, w, h, Color.Black))
                using (Bitmap onWhite = RenderPointer(hCursor, w, h, Color.White))
                {
                    bool[,] invert = new bool[w, h];
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                        {
                            Color b = onBlack.GetPixel(x, y), wh = onWhite.GetPixel(x, y);
                            int diff = ((wh.R - b.R) + (wh.G - b.G) + (wh.B - b.B)) / 3;
                            if (diff < -32) { invert[x, y] = true; continue; }
                            int a = Math.Max(0, Math.Min(255, 255 - diff));
                            if (a == 0) continue;
                            shape.SetPixel(x, y, Color.FromArgb(a, Math.Min(255, b.R * 255 / a), Math.Min(255, b.G * 255 / a), Math.Min(255, b.B * 255 / a)));
                        }
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                        {
                            if (invert[x, y]) { shape.SetPixel(x, y, Color.Black); continue; }
                            if (shape.GetPixel(x, y).A != 0) continue;
                            for (int dy = -1; dy <= 1; dy++)
                                for (int dx = -1; dx <= 1; dx++)
                                {
                                    int nx = x + dx, ny = y + dy;
                                    if (nx >= 0 && ny >= 0 && nx < w && ny < h && invert[nx, ny]) { shape.SetPixel(x, y, Color.White); dy = 2; break; }
                                }
                        }
                    AddOutline(shape, w, h);
                }
                if (scale != 100)
                {
                    int sw = Math.Max(8, w * scale / 100), sh = Math.Max(8, h * scale / 100);
                    Bitmap small = new Bitmap(sw, sh, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(small))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(shape, 0, 0, sw, sh);
                    }
                    shape.Dispose();
                    shape = small;
                    hx = hx * scale / 100;
                    hy = hy * scale / 100;
                }
                using (MemoryStream ms = new MemoryStream())
                {
                    shape.Save(ms, ImageFormat.Png);
                    return Proto.Cat(Proto.Ints(hx, hy), ms.ToArray());
                }
            }
            finally { shape.Dispose(); }
        }

        // A pointer drawn in only light (or only dark) pixels vanishes on a matching background,
        // e.g. a thin white text I-beam on a white page. Give such pointers a contrasting outline.
        static void AddOutline(Bitmap shape, int w, int h)
        {
            bool dark = false, light = false;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    Color c = shape.GetPixel(x, y);
                    if (c.A < 128) continue;
                    int luma = (c.R * 3 + c.G * 6 + c.B) / 10;
                    if (luma < 90) dark = true;
                    if (luma > 170) light = true;
                }
            if (dark == light) return; // has both (normal arrow) or nothing visible
            Color edge = light ? Color.FromArgb(230, 0, 0, 0) : Color.FromArgb(230, 255, 255, 255);
            bool[,] add = new bool[w, h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (shape.GetPixel(x, y).A >= 128) continue;
                    for (int dy = -1; dy <= 1 && !add[x, y]; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx >= 0 && ny >= 0 && nx < w && ny < h && shape.GetPixel(nx, ny).A >= 128) { add[x, y] = true; break; }
                        }
                }
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (add[x, y]) shape.SetPixel(x, y, edge);
        }

        static Bitmap RenderPointer(IntPtr hCursor, int w, int h, Color background)
        {
            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(background);
                IntPtr hdc = g.GetHdc();
                try { Native.DrawIconEx(hdc, 0, 0, hCursor, w, h, 0, IntPtr.Zero, 3); } // DI_NORMAL
                finally { g.ReleaseHdc(hdc); }
            }
            return bmp;
        }
    }
}