using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace RemoteDesk
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Native.SetProcessDPIAware();
            if (args.Length > 0 && args[0] == "--selftest") { SelfTest(args.Length > 1 ? args[1] : "127.0.0.1:7788"); return; }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length > 0 && args[0] == "--viewtest") { ViewTest(args.Length > 1 ? args[1] : "127.0.0.1:7788"); return; }
            if (args.Length > 0 && args[0] == "--uishot") { UiShot(args.Length > 1 ? args[1] : "."); return; }
            bool background = args.Length > 0 && args[0] == "--background"; // started with Windows
            bool first;
            using (Mutex single = new Mutex(true, "RemoteDesk.SingleInstance", out first))
            using (EventWaitHandle showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "RemoteDesk.Show"))
            {
                if (!first)
                {
                    // Already running (maybe hidden in the tray): open that window instead. Only
                    // one copy may run because the desk ID belongs to the computer.
                    if (!background) showSignal.Set();
                    return;
                }
                MainForm form = new MainForm(background);
                Thread waiter = new Thread(delegate()
                {
                    while (showSignal.WaitOne())
                        try { form.BeginInvoke(new MethodInvoker(form.ShowFromTray)); } catch { }
                });
                waiter.IsBackground = true;
                waiter.Start();
                Application.Run(form);
            }
        }

        // Opens the real viewer window against an in-process host and saves a screenshot of it.
        static void ViewTest(string server)
        {
            HostService host = new HostService();
            host.Id = "987654322";
            host.Server = server;
            host.RequireConsent = false;
            ManualResetEvent ready = new ManualResetEvent(false);
            host.Status = delegate(string s) { if (s.StartsWith("Ready")) ready.Set(); };
            host.NewPassword();
            host.Start();
            ready.WaitOne(8000);
            Form dummy = new Form();
            dummy.Opacity = 0; dummy.ShowInTaskbar = false; dummy.Show();
            ViewerForm v = null;
            Connector.Start(server, host.Id, host.Password, delegate { }, delegate(Channel c)
            {
                dummy.BeginInvoke(new MethodInvoker(delegate
                {
                    v = new ViewerForm(c, "viewtest");
                    v.Show(); v.Start();
                }));
            }, delegate(string e) { File.WriteAllText("viewtest.txt", "fail: " + e); Application.Exit(); });
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 4000;
            t.Tick += delegate
            {
                t.Stop();
                if (v != null)
                {
                    Rectangle r = v.Bounds;
                    using (Bitmap b = new Bitmap(r.Width, r.Height))
                    {
                        Graphics gb = Graphics.FromImage(b);
                        gb.CopyFromScreen(r.Location, System.Drawing.Point.Empty, r.Size);
                        b.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "viewer.png"));
                    }
                }
                host.Stop();
                Application.Exit();
            };
            t.Start();
            Application.Run();
        }

        // Renders the branded dialogs to PNG files so the design can be checked without clicking through the app.
        static void UiShot(string dir)
        {
            MainForm m = new MainForm();
            m.StartPosition = FormStartPosition.Manual;
            m.Location = new Point(-6000, -6000);
            m.Show();
            for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(50); }
            for (int page = 0; page < 5; page++)
            {
                m.ShowPage(page);
                if (page == 0) m.Hero.Set(Theme.GCheck, Theme.Green, "Connected", "Remote device is ready.", "512 734 096", "Online", "00:02", "Disconnect", null);
                for (int i = 0; i < 4; i++) { Application.DoEvents(); Thread.Sleep(30); }
                using (Bitmap b = new Bitmap(m.Width, m.Height))
                {
                    m.DrawToBitmap(b, new Rectangle(0, 0, m.Width, m.Height));
                    b.Save(Path.Combine(dir, "ui-page" + page + ".png"));
                }
            }
            m.Close();

            try { FileManagerShot(dir); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(dir, "ui-error.txt"), ex.ToString()); }

            Form[] dialogs = { new PasswordDialog("626 941 456"), new ConsentDialog("86.108.23.52"), new SetPasswordDialog() };
            string[] dialogNames = { "ui-password.png", "ui-consent.png", "ui-setpassword.png" };
            for (int i = 0; i < dialogs.Length; i++)
            {
                Form f = dialogs[i];
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-4000, -4000);
                f.Show();
                Application.DoEvents();
                using (Bitmap b = new Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(b, new Rectangle(0, 0, f.Width, f.Height));
                    b.Save(Path.Combine(dir, dialogNames[i]));
                }
                f.Close();
            }
        }

        // The file manager, with Documents on the remote side (from this PC).
        static void FileManagerShot(string dir)
        {
            ViewerForm vf = new ViewerForm(new Channel(new MemoryStream()), Brand.Name + " - 512 734 096");
            FileManagerForm fmf = new FileManagerForm(vf, Brand.Name + " files - 512 734 096");
            fmf.StartPosition = FormStartPosition.Manual;
            fmf.Location = new Point(-6000, -6000);
            fmf.Show();
            for (int i = 0; i < 5; i++) { Application.DoEvents(); Thread.Sleep(40); }
            fmf.OnMessage((byte)'l', Proto.Cat(Proto.Ints(1), Encoding.UTF8.GetBytes(FileListing.Describe(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)))));
            for (int i = 0; i < 5; i++) { Application.DoEvents(); Thread.Sleep(40); }
            using (Bitmap b = new Bitmap(fmf.Width, fmf.Height))
            {
                fmf.DrawToBitmap(b, new Rectangle(0, 0, fmf.Width, fmf.Height));
                b.Save(Path.Combine(dir, "ui-files.png"));
            }
            fmf.Close();
            vf.Dispose();
        }

        // Connects and returns the channel (null + error on failure); retries while the host
        // is still (re-)registering with the relay, e.g. right after the previous session.
        static Channel ConnectRetry(string server, string id, string password, out string error)
        {
            error = "timeout";
            for (int attempt = 0; attempt < 8; attempt++)
            {
                Channel ch = null;
                string err = null;
                ManualResetEvent done = new ManualResetEvent(false);
                Connector.Start(server, id, password, delegate { }, delegate(Channel c) { ch = c; done.Set(); }, delegate(string e) { err = e; done.Set(); });
                if (!done.WaitOne(20000)) return null;
                if (ch != null) { error = null; return ch; }
                error = err;
                if (err == null || !err.Contains("not found")) return null;
                Thread.Sleep(1000);
            }
            return null;
        }

        // Connects, hangs up, and returns "connected" or the error text.
        static string TryConnect(string server, string id, string password, StringBuilder log)
        {
            string err;
            Channel ch = ConnectRetry(server, id, password, out err);
            if (ch == null) return err;
            ch.Send('X', null);
            ch.Close();
            Thread.Sleep(1500);
            return "connected";
        }

        static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // Clipboard both ways, and the file manager: list a folder, upload into a chosen
        // folder, download a whole folder (with a sub-folder) - all over the live session.
        static void ClipboardAndFilesTest(HostService host, Channel channel, StringBuilder log, out bool clipOk, out bool fmOk)
        {
            string gotOnHost = null, gotOnViewer = null;
            host.SetClipboard = delegate(string t) { gotOnHost = t; };

            string dir = Path.Combine(Path.GetTempPath(), "RemoteDeskFM_" + Guid.NewGuid().ToString("N"));
            string src = Path.Combine(dir, "src"), up = Path.Combine(dir, "up");
            Directory.CreateDirectory(Path.Combine(src, "inner"));
            Random rnd = new Random(7);
            byte[] a = new byte[50000], b = new byte[300000], u = new byte[120000];
            rnd.NextBytes(a); rnd.NextBytes(b); rnd.NextBytes(u);
            File.WriteAllBytes(Path.Combine(src, "a.txt"), a);
            File.WriteAllBytes(Path.Combine(src, "inner", "b.bin"), b);

            channel.Send('C', Encoding.UTF8.GetBytes("copied on the viewer"));
            host.SendClipboard("copied on the host");
            channel.Send('L', Proto.Cat(Proto.Ints(1), Encoding.UTF8.GetBytes(src)));
            channel.Send('j', Proto.Cat(Proto.Ints(900), Proto.LongBytes(u.Length), Encoding.UTF8.GetBytes(up + "\nuploaded.dat")));
            for (int off = 0; off < u.Length; off += 65536)
                channel.Send('U', Proto.Cat(Proto.Ints(900), Proto.LongBytes(off), Proto.Sub(u, off, Math.Min(65536, u.Length - off))));
            channel.Send('Z', Proto.Ints(900));
            channel.Send('g', Proto.Cat(Proto.Ints(2), Encoding.UTF8.GetBytes(src)));

            bool listed = false, uploaded = false, downloadDone = false;
            System.Collections.Generic.Dictionary<string, byte[]> got = new System.Collections.Generic.Dictionary<string, byte[]>();
            string curName = null;
            MemoryStream cur = null;
            DateTime end = DateTime.UtcNow.AddSeconds(40);
            while (DateTime.UtcNow < end && !(listed && uploaded && downloadDone && gotOnViewer != null))
            {
                byte[] d;
                byte t = channel.Recv(out d);
                if (t == 'C') gotOnViewer = Proto.Str(d);
                else if (t == 'l' && Proto.Int(d, 0) == 1)
                {
                    string listing = Proto.Str(Proto.Sub(d, 4, d.Length - 4));
                    listed = listing.Contains("\ta.txt\t") && listing.Contains("\tinner\t");
                }
                else if (t == 'a' && Proto.Int(d, 0) == 900) uploaded = Proto.Int(d, 1) == 1;
                else if (t == 'f' && Proto.Int(d, 0) == 2) { curName = Proto.Str(Proto.Sub(d, 12, d.Length - 12)); cur = new MemoryStream(); }
                else if (t == 'd' && Proto.Int(d, 0) == 2 && cur != null) cur.Write(d, 12, d.Length - 12);
                else if (t == 'e' && Proto.Int(d, 0) == 2 && cur != null) { got[curName] = cur.ToArray(); cur = null; }
                else if (t == 'n' && Proto.Int(d, 0) == 2) downloadDone = true;
                else if (t == 'x') log.AppendLine("file manager error: " + Proto.Str(Proto.Sub(d, 4, d.Length - 4)));
            }
            for (int i = 0; i < 30 && gotOnHost == null; i++) Thread.Sleep(100);

            clipOk = gotOnHost == "copied on the viewer" && gotOnViewer == "copied on the host";
            byte[] ga, gb;
            bool downloadOk = downloadDone && got.TryGetValue("src\\a.txt", out ga) && Same(ga, a) && got.TryGetValue("src\\inner\\b.bin", out gb) && Same(gb, b);
            bool uploadOk = uploaded && File.Exists(Path.Combine(up, "uploaded.dat")) && Same(File.ReadAllBytes(Path.Combine(up, "uploaded.dat")), u);
            fmOk = listed && uploadOk && downloadOk;
            log.AppendLine("clipboard viewer->host: " + (gotOnHost == "copied on the viewer" ? "OK" : "FAILED") + ", host->viewer: " + (gotOnViewer == "copied on the host" ? "OK" : "FAILED"));
            log.AppendLine("file manager: listing " + (listed ? "OK" : "FAILED") + ", upload into folder " + (uploadOk ? "OK" : "FAILED") + ", folder download " + (downloadOk ? "OK (" + got.Count + " files, bytes match)" : "FAILED (" + got.Count + " files)"));
            try { Directory.Delete(dir, true); } catch { }
        }

        // Headless end-to-end check: host + viewer in one process through the relay.
        static void SelfTest(string server)
        {
            StringBuilder log = new StringBuilder();
            try
            {
                HostService host = new HostService();
                host.Id = "987654321";
                host.Server = server;
                host.RequireConsent = false;
                ManualResetEvent ready = new ManualResetEvent(false);
                host.Status = delegate(string s) { log.AppendLine("host: " + s); if (s.StartsWith("Ready")) ready.Set(); };
                host.NewPassword();
                host.Start();
                if (!ready.WaitOne(8000)) throw new Exception("host never became ready");

                // wrong password first
                ManualResetEvent done1 = new ManualResetEvent(false);
                Connector.Start(server, host.Id, "WRONGPASS1", delegate { }, delegate { log.AppendLine("ERROR: wrong password accepted"); done1.Set(); },
                    delegate(string e) { log.AppendLine("wrong password -> " + e); done1.Set(); });
                done1.WaitOne(15000);

                ready.Reset();
                if (!ready.WaitOne(8000)) throw new Exception("host did not re-register");

                Channel channel = null;
                ManualResetEvent done2 = new ManualResetEvent(false);
                Connector.Start(server, host.Id, host.Password, delegate { }, delegate(Channel c) { channel = c; done2.Set(); },
                    delegate(string e) { log.AppendLine("connect failed: " + e); done2.Set(); });
                if (!done2.WaitOne(15000) || channel == null) throw new Exception("connect failed");
                log.AppendLine("authenticated + encrypted channel up");

                int rects = 0, shapes = 0, moves = 0, px = -1, py = -1; long bytes = 0; int sw = 0, sh = 0;
                DateTime end = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < end)
                {
                    byte[] d;
                    byte t = channel.Recv(out d);
                    if (t == 'S') { sw = Proto.Int(d, 0); sh = Proto.Int(d, 1); }
                    if (t == 'R') { rects++; bytes += d.Length; }
                    if (t == 'c' && shapes++ == 0)
                        File.WriteAllBytes(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest-pointer.png"), Proto.Sub(d, 8, d.Length - 8));
                    if (t == 'p') { moves++; px = Proto.Int(d, 0); py = Proto.Int(d, 1); }
                    if (rects == 1) channel.Send('M', Proto.Ints(0, 32768, 32768, 0));
                }
                log.AppendLine("screen " + sw + "x" + sh + ", rects received: " + rects + ", bytes: " + bytes);
                log.AppendLine("pointer: " + shapes + " shape(s), " + moves + " position update(s), last at " + px + "," + py + " (screen centre is " + sw / 2 + "," + sh / 2 + ")");

                // file transfer: send bytes over the wire exactly as ViewerForm.SendFile does,
                // then check the host wrote an identical file to its SaveDir.
                string saveDir = Path.Combine(Path.GetTempPath(), "RemoteDeskSelfTest_" + Guid.NewGuid().ToString("N"));
                host.SaveDir = saveDir;
                byte[] fileData = new byte[600 * 1024];
                new Random(12345).NextBytes(fileData);
                string fileName = "selftest.bin";
                int fileId = 777;
                channel.Send('T', Proto.Cat(Proto.Ints(fileId), Proto.LongBytes(fileData.Length), Encoding.UTF8.GetBytes(fileName)));
                int chunkSize = 131072;
                for (int off = 0; off < fileData.Length; off += chunkSize)
                {
                    int n = Math.Min(chunkSize, fileData.Length - off);
                    channel.Send('U', Proto.Cat(Proto.Ints(fileId), Proto.LongBytes(off), Proto.Sub(fileData, off, n)));
                }
                channel.Send('Z', Proto.Ints(fileId));
                string expectedPath = Path.Combine(saveDir, fileName);
                bool fileOk = false;
                DateTime fend = DateTime.UtcNow.AddSeconds(30); // generous: the relay can be slow at times
                while (DateTime.UtcNow < fend)
                {
                    if (File.Exists(expectedPath))
                    {
                        byte[] got = File.ReadAllBytes(expectedPath);
                        fileOk = got.Length == fileData.Length;
                        if (fileOk) for (int i = 0; i < got.Length; i++) if (got[i] != fileData[i]) { fileOk = false; break; }
                        break;
                    }
                    Thread.Sleep(100);
                }
                log.AppendLine("file transfer: " + (fileOk ? "OK, bytes match" : "FAILED, exists=" + File.Exists(expectedPath)));
                try { Directory.Delete(saveDir, true); } catch { }

                bool clipOk, fmOk;
                ClipboardAndFilesTest(host, channel, log, out clipOk, out fmOk);
                fileOk = fileOk && clipOk && fmOk;

                channel.Send('X', null);
                channel.Close();
                Thread.Sleep(1500); // let the host finish that session

                // local network: discovery answer, then a direct connection that skips the relay
                bool lanOk = false, directOk = false;
                Lan.Start(host.Id, host.AcceptDirect);
                using (System.Net.Sockets.UdpClient q = new System.Net.Sockets.UdpClient(0))
                {
                    q.Client.ReceiveTimeout = 3000;
                    byte[] ask = Encoding.ASCII.GetBytes("ALDQ");
                    q.Send(ask, ask.Length, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, Lan.DiscoveryPort));
                    System.Net.IPEndPoint from = null;
                    try
                    {
                        string reply = Encoding.UTF8.GetString(q.Receive(ref from));
                        lanOk = reply.StartsWith("ALDA|" + host.Id + "|");
                        log.AppendLine("lan discovery reply: " + reply);
                    }
                    catch (Exception ex) { log.AppendLine("lan discovery: no reply (" + ex.Message + ")"); }
                }
                Channel direct = null;
                ManualResetEvent done3 = new ManualResetEvent(false);
                Connector.Start("127.0.0.1:" + Lan.DirectPort, host.Id, host.Password, delegate { }, delegate(Channel c) { direct = c; done3.Set(); },
                    delegate(string e) { log.AppendLine("direct connect failed: " + e); done3.Set(); });
                if (done3.WaitOne(15000) && direct != null)
                {
                    DateTime dend = DateTime.UtcNow.AddSeconds(5);
                    while (DateTime.UtcNow < dend && !directOk)
                    {
                        byte[] d;
                        if (direct.Recv(out d) == 'R') directOk = true;
                    }
                    direct.Send('X', null);
                    direct.Close();
                }
                Lan.Stop();
                log.AppendLine("direct connection (no relay): " + (directOk ? "OK, screen received" : "FAILED"));

                // unattended access: the permanent password gets in with nobody there to accept,
                // while the session password still needs someone to click Accept.
                host.RequireConsent = true;
                host.Consent = delegate { return false; }; // nobody at the computer
                host.UnattendedKey = Proto.DeriveKey("TESTPASS99", host.Id);
                bool unattendedOk = TryConnect(server, host.Id, "TESTPASS99", log) == "connected";
                string attended = TryConnect(server, host.Id, host.Password, log);
                bool consentOk = attended.Contains("declined");
                log.AppendLine("unattended password without anyone accepting: " + (unattendedOk ? "OK, connected" : "FAILED"));
                log.AppendLine("session password with nobody accepting: " + (consentOk ? "OK, declined as expected" : "FAILED (" + attended + ")"));

                // remote restart + reconnect: a stand-in for the real restart takes the host
                // offline for a few seconds, like a reboot would.
                bool restartAsked = false, comesBack = false;
                host.ComesBackAfterRestart = delegate { return true; };
                host.Restart = delegate
                {
                    restartAsked = true;
                    ThreadPool.QueueUserWorkItem(delegate { Thread.Sleep(500); host.Stop(); Thread.Sleep(3000); host.Start(); });
                };
                string rerr;
                Channel rc = ConnectRetry(server, host.Id, "TESTPASS99", out rerr);
                if (rc == null) log.AppendLine("restart test connect failed: " + rerr);
                else
                {
                    rc.Send('r', null);
                    try
                    {
                        DateTime rend = DateTime.UtcNow.AddSeconds(10);
                        while (DateTime.UtcNow < rend)
                        {
                            byte[] d;
                            if (rc.Recv(out d) == 'q') { comesBack = Proto.Int(d, 0) != 0; break; }
                        }
                    }
                    catch { }
                    rc.Close();
                }
                Thread.Sleep(1000);
                bool reconnected = restartAsked && comesBack && TryConnect(server, host.Id, "TESTPASS99", log) == "connected";
                log.AppendLine("remote restart: " + (restartAsked ? "requested" : "NOT requested") + ", viewer told it comes back: " + comesBack + ", reconnected afterwards: " + reconnected);

                host.Stop();
                log.AppendLine(rects > 0 && fileOk && lanOk && directOk && unattendedOk && consentOk && reconnected ? "SELFTEST PASS" : "SELFTEST FAIL");
            }
            catch (Exception ex) { log.AppendLine("SELFTEST FAIL: " + ex); }
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest.txt"), log.ToString());
        }
    }
}
