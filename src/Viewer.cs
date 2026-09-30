using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace RemoteDesk
{
    // Client side of the handshake. Runs on a worker thread and reports through callbacks.
    public static class Connector
    {
        // Returns an action that cancels the attempt.
        public static Action Start(string server, string id, string password,
            Action<string> status, Action<Channel> ready, Action<string> fail)
        {
            TcpClient c = new TcpClient();
            bool cancelled = false;
            Thread t = new Thread(delegate()
            {
                bool ok = false;
                try
                {
                    string host = server.Trim(); int port = 7788;
                    int i = host.LastIndexOf(':');
                    if (i > 0 && int.TryParse(host.Substring(i + 1), out port)) host = host.Substring(0, i); else port = 7788;

                    status("Contacting relay...");
                    IAsyncResult ar = c.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(6000)) throw new Exception("Relay server not reachable at " + host + ":" + port + " - check the address and firewall");
                    c.EndConnect(ar);
                    c.NoDelay = true;
                    NetworkStream s = c.GetStream();
                    s.ReadTimeout = 15000;
                    Channel ch = new Channel(s);
                    ch.Closer = delegate() { c.Close(); };

                    ch.Send('V', Proto.Text(id));
                    byte[] d;
                    byte r = ch.Recv(out d);
                    if (r == 'E') { fail(Proto.Str(d)); return; }
                    if (r != 'O') throw new Exception("Unexpected reply from relay");

                    status("Verifying password...");
                    s.ReadTimeout = 30000;
                    r = ch.Recv(out d);
                    if (r != 'N' || d.Length != 16) throw new Exception("Unexpected reply from remote computer");
                    byte[] nH = d;
                    byte[] key = Proto.DeriveKey(password, id);
                    byte[] nV = Proto.Rand(16);
                    ch.Send('A', Proto.Cat(nV, Proto.Hmac(key, Proto.Text("auth"), nH, nV)));
                    r = ch.Recv(out d);
                    if (r == 'F') { fail("Wrong password"); return; }
                    if (r != 'Y' || !Proto.Eq(d, Proto.Hmac(key, Proto.Text("host"), nH, nV)))
                    { fail("Remote computer failed authentication"); return; }
                    ch.EnableSecurity(Proto.Hmac(key, Proto.Text("enc"), nH, nV), Proto.Hmac(key, Proto.Text("mac"), nH, nV), false);

                    s.ReadTimeout = Timeout.Infinite;
                    while (true)
                    {
                        r = ch.Recv(out d);
                        if (r == 'W') { status("Waiting for the remote user to accept..."); continue; }
                        if (r == 'D') { fail("The remote user declined the connection"); return; }
                        if (r == 'G') break;
                        throw new Exception("Unexpected message");
                    }
                    ok = true;
                    ready(ch);
                }
                catch (Exception ex)
                {
                    fail(cancelled ? "Cancelled" : ex.Message);
                }
                finally
                {
                    if (!ok) { try { c.Close(); } catch { } }
                }
            });
            t.IsBackground = true;
            t.Start();
            return delegate() { cancelled = true; try { c.Close(); } catch { } };
        }
    }

    class Surface : Panel
    {
        public Surface()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.Selectable | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque, true);
            TabStop = true;
            BackColor = Color.Black;
        }
    }

    // The window showing the remote screen. Sends mouse and keyboard input while it has focus.
    public class ViewerForm : Form, IMessageFilter
    {
        readonly Channel ch;
        readonly Surface surface = new Surface();
        readonly object canvasLock = new object();
        readonly HashSet<int> down = new HashSet<int>();
        readonly ToolStripComboBox cmbQuality = new ToolStripComboBox();
        readonly ToolStripComboBox cmbSize = new ToolStripComboBox();
        Bitmap canvas;
        bool paintPending, sizedOnce, userClosing;
        readonly ToolStripLabel lblXfer = new ToolStripLabel("");
        int nextFileId;
        int lastMove;

        // Remote mouse pointer: its shape becomes our local cursor (so movement is instant); its
        // position is only drawn when the pointer moves on the other side or we're not hovering.
        Bitmap pointerImg;
        Point pointerHot, pointerPos, lastSentPos;
        bool pointerVisible, pointerKnown, mouseInside;
        float pointerScale = -1;
        IntPtr pointerHandle;
        Rectangle pointerDrawn = Rectangle.Empty;

        // Called when the other computer confirms it's restarting; true = it will be reachable
        // again, so the main window can reconnect automatically.
        public Action<bool> RemoteRestarting;

        public ViewerForm(Channel ch, string title)
        {
            this.ch = ch;
            Text = title;
            Icon = Brand.Icon;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(960, 560);
            MinimumSize = new Size(480, 320);

            ToolStrip bar = new ToolStrip();
            bar.GripStyle = ToolStripGripStyle.Hidden;
            bar.Renderer = new LightToolRenderer();
            bar.BackColor = Theme.Card;
            bar.ForeColor = Theme.Text;
            bar.Padding = new Padding(8, 3, 8, 3);
            bar.Items.Add(new ToolStripLabel("Quality"));
            cmbQuality.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbQuality.Items.AddRange(new object[] { "Fast", "Balanced", "Best" });
            cmbQuality.SelectedIndex = 1;
            cmbQuality.Width = 80;
            StyleCombo(cmbQuality);
            bar.Items.Add(cmbQuality);
            bar.Items.Add(new ToolStripLabel("  Size"));
            cmbSize.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbSize.Items.AddRange(new object[] { "100%", "75%", "50%" });
            cmbSize.SelectedIndex = 0;
            cmbSize.Width = 70;
            StyleCombo(cmbSize);
            bar.Items.Add(cmbSize);
            bar.Items.Add(new ToolStripSeparator());
            ToolStripButton bClip = new ToolStripButton("Send clipboard");
            bClip.Click += delegate { SendClipboard(); };
            bar.Items.Add(bClip);
            ToolStripButton bFull = new ToolStripButton("Fullscreen");
            bFull.Click += delegate { ToggleFullscreen(); };
            bar.Items.Add(bFull);
            ToolStripButton bRestart = new ToolStripButton("Restart");
            bRestart.ToolTipText = "Restart the remote computer and reconnect";
            bRestart.Click += delegate { AskRestart(); };
            bar.Items.Add(bRestart);
            ToolStripButton bEnd = new ToolStripButton("Disconnect");
            bEnd.Alignment = ToolStripItemAlignment.Right;
            bEnd.ForeColor = Theme.Red;
            bEnd.Click += delegate { Close(); };
            bar.Items.Add(bEnd);
            lblXfer.Alignment = ToolStripItemAlignment.Right;
            lblXfer.ForeColor = Theme.Primary;
            bar.Items.Add(lblXfer);
            cmbQuality.SelectedIndexChanged += delegate { SendQuality(); surface.Focus(); };
            cmbSize.SelectedIndexChanged += delegate { SendQuality(); surface.Focus(); };

            surface.Dock = DockStyle.Fill;
            surface.Paint += OnSurfacePaint;
            surface.MouseMove += delegate(object s, MouseEventArgs e) { MouseEvt(0, e); };
            surface.MouseDown += delegate(object s, MouseEventArgs e) { surface.Focus(); MouseEvt(e.Button == MouseButtons.Left ? 1 : e.Button == MouseButtons.Right ? 3 : 5, e); };
            surface.MouseUp += delegate(object s, MouseEventArgs e) { MouseEvt(e.Button == MouseButtons.Left ? 2 : e.Button == MouseButtons.Right ? 4 : 6, e); };
            surface.MouseWheel += delegate(object s, MouseEventArgs e) { MouseEvt(7, e); };
            surface.Resize += delegate { UpdateLocalCursor(); surface.Invalidate(); };
            surface.MouseEnter += delegate { mouseInside = true; RefreshPointer(); };
            surface.MouseLeave += delegate { mouseInside = false; RefreshPointer(); };
            surface.Cursor = Cursors.Default;
            surface.AllowDrop = true;
            surface.DragEnter += delegate(object s, DragEventArgs e)
            {
                e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            };
            surface.DragDrop += delegate(object s, DragEventArgs e)
            {
                string[] paths = (string[])e.Data.GetData(DataFormats.FileDrop);
                foreach (string p in paths) SendFile(p);
            };

            Controls.Add(surface);
            Controls.Add(bar);

            Application.AddMessageFilter(this);
            Deactivate += delegate { ReleaseKeys(); };
            FormClosing += delegate { userClosing = true; ReleaseKeys(); TrySend('X', null); };
            FormClosed += delegate
            {
                Application.RemoveMessageFilter(this);
                ch.Close();
                if (pointerHandle != IntPtr.Zero) Native.DestroyIcon(pointerHandle);
            };
            Shown += delegate { surface.Focus(); };
        }

        static void StyleCombo(ToolStripComboBox c)
        {
            c.FlatStyle = FlatStyle.Flat;
            c.BackColor = Theme.Surface;
            c.ForeColor = Theme.Ink;
        }

        public void Start()
        {
            Thread t = new Thread(ReceiveLoop);
            t.IsBackground = true;
            t.Start();
            SendQuality(); // tell the other side the selected quality/size right away
        }

        // ---- receiving ------------------------------------------------------------------

        void ReceiveLoop()
        {
            string reason = "Connection closed";
            try
            {
                while (true)
                {
                    byte[] d;
                    byte t = ch.Recv(out d);
                    if (t == 'S')
                    {
                        int w = Proto.Int(d, 0), h = Proto.Int(d, 1);
                        lock (canvasLock)
                        {
                            if (canvas != null) canvas.Dispose();
                            canvas = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                        }
                        BeginInvoke(new MethodInvoker(delegate { FitWindow(w, h); UpdateLocalCursor(); RequestPaint(); }));
                    }
                    else if (t == 'c' && d.Length > 8)
                    {
                        Point hot = new Point(Proto.Int(d, 0), Proto.Int(d, 1));
                        Bitmap img;
                        using (MemoryStream ms = new MemoryStream(d, 8, d.Length - 8))
                        using (Image png = Image.FromStream(ms))
                            img = new Bitmap(png);
                        BeginInvoke(new MethodInvoker(delegate
                        {
                            if (pointerImg != null) pointerImg.Dispose();
                            pointerImg = img;
                            pointerHot = hot;
                            pointerScale = -1;
                            UpdateLocalCursor();
                            RefreshPointer();
                        }));
                    }
                    else if (t == 'p' && d.Length >= 12)
                    {
                        Point pos = new Point(Proto.Int(d, 0), Proto.Int(d, 1));
                        bool vis = Proto.Int(d, 2) != 0;
                        BeginInvoke(new MethodInvoker(delegate
                        {
                            pointerPos = pos;
                            pointerVisible = vis;
                            pointerKnown = true;
                            RefreshPointer();
                        }));
                    }
                    else if (t == 'R')
                    {
                        int x = Proto.Int(d, 0), y = Proto.Int(d, 1), w = Proto.Int(d, 2), h = Proto.Int(d, 3);
                        using (MemoryStream ms = new MemoryStream(d, 16, d.Length - 16))
                        using (Image img = Image.FromStream(ms))
                        {
                            lock (canvasLock)
                            {
                                if (canvas == null) continue;
                                using (Graphics g = Graphics.FromImage(canvas))
                                    g.DrawImage(img, new Rectangle(x, y, w, h));
                            }
                        }
                        RequestPaint();
                    }
                    else if (t == 'q' && d.Length >= 4)
                    {
                        bool back = Proto.Int(d, 0) != 0;
                        BeginInvoke(new MethodInvoker(delegate
                        {
                            userClosing = true; // not a lost connection - no error message
                            if (RemoteRestarting != null) RemoteRestarting(back);
                            Close();
                        }));
                        return;
                    }
                    else if (t == 'B') break;
                }
            }
            catch (Exception ex) { if (!userClosing) reason = "Connection lost: " + ex.Message; }
            try
            {
                BeginInvoke(new MethodInvoker(delegate
                {
                    if (!userClosing)
                    {
                        userClosing = true;
                        MessageBox.Show(this, reason, Brand.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        Close();
                    }
                }));
            }
            catch { }
        }

        void RequestPaint()
        {
            if (paintPending || !IsHandleCreated) return;
            paintPending = true;
            try { BeginInvoke(new MethodInvoker(delegate { paintPending = false; surface.Invalidate(); })); }
            catch { paintPending = false; }
        }

        void FitWindow(int w, int h)
        {
            if (sizedOnce) return;
            sizedOnce = true;
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            double sc = Math.Min(1.0, Math.Min((wa.Width * 0.9) / w, (wa.Height * 0.85) / h));
            ClientSize = new Size((int)(w * sc), (int)(h * sc) + 25);
            CenterToScreen();
        }

        // ---- drawing --------------------------------------------------------------------

        Rectangle DestRect()
        {
            int cw, chh;
            lock (canvasLock)
            {
                if (canvas == null) return Rectangle.Empty;
                cw = canvas.Width; chh = canvas.Height;
            }
            Size box = surface.ClientSize;
            if (box.Width < 1 || box.Height < 1) return Rectangle.Empty;
            double sc = Math.Min((double)box.Width / cw, (double)box.Height / chh);
            int w = (int)(cw * sc), h = (int)(chh * sc);
            return new Rectangle((box.Width - w) / 2, (box.Height - h) / 2, w, h);
        }

        void OnSurfacePaint(object sender, PaintEventArgs e)
        {
            e.Graphics.Clear(Color.Black);
            Rectangle dst = DestRect();
            if (dst.IsEmpty)
            {
                using (Font f = new Font("Segoe UI", 11))
                    e.Graphics.DrawString("Waiting for the remote screen...", f, Brushes.Gray, 20, 20);
                return;
            }
            lock (canvasLock)
            {
                if (canvas == null) return;
                e.Graphics.InterpolationMode = dst.Width < canvas.Width ? InterpolationMode.HighQualityBilinear : InterpolationMode.HighQualityBicubic;
                e.Graphics.DrawImage(canvas, dst);
            }
            Rectangle pr = PointerRect();
            if (!pr.IsEmpty)
            {
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                e.Graphics.DrawImage(pointerImg, pr);
            }
        }

        // ---- remote pointer -------------------------------------------------------------

        float CanvasScale()
        {
            Rectangle dst = DestRect();
            lock (canvasLock) return canvas == null || dst.IsEmpty ? 1f : dst.Width / (float)canvas.Width;
        }

        // Pointer shown slightly larger than the picture scale when the window is small, so it
        // stays easy to see.
        float PointerScale() { return Math.Max(CanvasScale(), 0.85f); }

        // Uses the remote pointer's shape as our own cursor over the remote screen, so it moves
        // with the local mouse instantly.
        void UpdateLocalCursor()
        {
            if (pointerImg == null) return;
            float f = PointerScale();
            if (Math.Abs(f - pointerScale) < 0.02f) return;
            pointerScale = f;
            int w = Math.Min(128, Math.Max(8, (int)Math.Round(pointerImg.Width * f)));
            int h = Math.Min(128, Math.Max(8, (int)Math.Round(pointerImg.Height * f)));
            using (Bitmap b = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(pointerImg, 0, 0, w, h);
                }
                IntPtr icon = b.GetHicon();
                ICONINFO ii;
                bool ok = Native.GetIconInfo(icon, out ii);
                Native.DestroyIcon(icon);
                if (!ok) return;
                ii.fIcon = false;
                ii.xHotspot = Math.Min(w - 1, (int)(pointerHot.X * f));
                ii.yHotspot = Math.Min(h - 1, (int)(pointerHot.Y * f));
                IntPtr cursor = Native.CreateIconIndirect(ref ii);
                Native.DeleteObject(ii.hbmMask);
                Native.DeleteObject(ii.hbmColor);
                if (cursor == IntPtr.Zero) return;
                surface.Cursor = new Cursor(cursor);
                if (pointerHandle != IntPtr.Zero) Native.DestroyIcon(pointerHandle);
                pointerHandle = cursor;
            }
        }

        // Where to draw the remote pointer, or empty when our own cursor already shows it.
        Rectangle PointerRect()
        {
            if (pointerImg == null || !pointerKnown || !pointerVisible) return Rectangle.Empty;
            if (mouseInside)
            {
                // we're controlling it: only draw when it was moved from the other side
                bool idle = Environment.TickCount - lastMove > 700;
                int dx = pointerPos.X - lastSentPos.X, dy = pointerPos.Y - lastSentPos.Y;
                if (!idle || dx * dx + dy * dy <= 9) return Rectangle.Empty;
            }
            Rectangle dst = DestRect();
            if (dst.IsEmpty) return Rectangle.Empty;
            float c = CanvasScale(), f = PointerScale();
            int x = dst.X + (int)(pointerPos.X * c - pointerHot.X * f);
            int y = dst.Y + (int)(pointerPos.Y * c - pointerHot.Y * f);
            return new Rectangle(x, y, (int)(pointerImg.Width * f), (int)(pointerImg.Height * f));
        }

        void RefreshPointer()
        {
            Rectangle now = PointerRect();
            if (now == pointerDrawn) return;
            if (!pointerDrawn.IsEmpty) surface.Invalidate(Rectangle.Inflate(pointerDrawn, 2, 2));
            if (!now.IsEmpty) surface.Invalidate(Rectangle.Inflate(now, 2, 2));
            pointerDrawn = now;
        }

        // ---- sending input --------------------------------------------------------------

        void TrySend(char type, byte[] data)
        {
            try { ch.Send(type, data); } catch { }
        }

        void MouseEvt(int kind, MouseEventArgs e)
        {
            Rectangle r = DestRect();
            if (r.IsEmpty) return;
            if (kind == 0)
            {
                int now = Environment.TickCount;
                if (now - lastMove < 12) return;
                lastMove = now;
            }
            double fx = Math.Max(0, Math.Min(1, (e.X - r.X) / (double)r.Width));
            double fy = Math.Max(0, Math.Min(1, (e.Y - r.Y) / (double)r.Height));
            int data = kind == 7 ? e.Delta : 0;
            TrySend('M', Proto.Ints(kind, (int)(fx * 65535), (int)(fy * 65535), data));
            lastMove = Environment.TickCount;
            lock (canvasLock)
                if (canvas != null) lastSentPos = new Point((int)(fx * canvas.Width), (int)(fy * canvas.Height));
            RefreshPointer();
        }

        // Grab keystrokes while the remote screen has focus, so shortcuts like Alt+Tab,
        // Tab and Ctrl+C go to the remote computer instead of this window.
        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != 0x100 && m.Msg != 0x101 && m.Msg != 0x104 && m.Msg != 0x105) return false;
            if (!IsHandleCreated || Form.ActiveForm != this || !surface.Focused) return false;
            int vk = (int)(m.WParam.ToInt64() & 0xFF);
            bool isDown = m.Msg == 0x100 || m.Msg == 0x104;
            if (isDown) down.Add(vk); else down.Remove(vk);
            TrySend('k', Proto.Ints(vk, isDown ? 1 : 0));
            return true;
        }

        void ReleaseKeys()
        {
            foreach (int vk in down) TrySend('k', Proto.Ints(vk, 0));
            down.Clear();
        }

        void SendQuality()
        {
            int[] q = { 55, 78, 95 };
            int[] s = { 100, 75, 50 };
            int qi = Math.Max(0, cmbQuality.SelectedIndex), si = Math.Max(0, cmbSize.SelectedIndex);
            TrySend('Q', Proto.Ints(q[qi], s[si]));
        }

        void SendClipboard()
        {
            try
            {
                if (Clipboard.ContainsText()) TrySend('C', Encoding.UTF8.GetBytes(Clipboard.GetText()));
            }
            catch { }
            surface.Focus();
        }

        // ---- outgoing file transfer (drag a file onto the remote screen) ----------------

        void SetXfer(string text)
        {
            try { BeginInvoke(new MethodInvoker(delegate { lblXfer.Text = text; })); } catch { }
        }

        void SendFile(string path)
        {
            int fileId = Interlocked.Increment(ref nextFileId);
            Thread t = new Thread(delegate()
            {
                string name = Path.GetFileName(path);
                try
                {
                    FileInfo fi = new FileInfo(path);
                    long size = fi.Length;
                    byte[] nameBytes = Encoding.UTF8.GetBytes(name);
                    ch.Send('T', Proto.Cat(Proto.Ints(fileId), Proto.LongBytes(size), nameBytes));

                    byte[] buf = new byte[256 * 1024];
                    long sent = 0;
                    DateTime lastUi = DateTime.MinValue;
                    using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        int n;
                        while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                        {
                            byte[] chunk = n == buf.Length ? buf : Proto.Sub(buf, 0, n);
                            ch.Send('U', Proto.Cat(Proto.Ints(fileId), Proto.LongBytes(sent), chunk));
                            sent += n;
                            if ((DateTime.UtcNow - lastUi).TotalMilliseconds > 150)
                            {
                                lastUi = DateTime.UtcNow;
                                int pct = size > 0 ? (int)(sent * 100 / size) : 100;
                                SetXfer("Sending " + name + "... " + pct + "%");
                            }
                        }
                    }
                    ch.Send('Z', Proto.Ints(fileId));
                    SetXfer("Sent " + name);
                }
                catch (Exception ex)
                {
                    SetXfer("Failed to send " + name + ": " + ex.Message);
                }
                Thread.Sleep(4000);
                SetXfer("");
            });
            t.IsBackground = true;
            t.Start();
        }

        void AskRestart()
        {
            DialogResult r = MessageBox.Show(this,
                "Restart the remote computer now?\n\nUnsaved work on it may be lost. " + Brand.Name + " reconnects automatically when it's back, if that computer has unattended access and Start with Windows turned on.",
                Brand.Name + " - restart", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (r == DialogResult.Yes) TrySend('r', null);
            surface.Focus();
        }

        void ToggleFullscreen()
        {
            if (FormBorderStyle == FormBorderStyle.None)
            {
                FormBorderStyle = FormBorderStyle.Sizable;
                WindowState = FormWindowState.Normal;
            }
            else
            {
                FormBorderStyle = FormBorderStyle.None;
                WindowState = FormWindowState.Maximized;
            }
            surface.Focus();
        }
    }
}
