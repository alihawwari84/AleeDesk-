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
            bool first;
            using (Mutex single = new Mutex(true, "RemoteDesk.SingleInstance", out first))
            {
                if (!first)
                {
                    MessageBox.Show(Brand.Name + " is already running on this computer.\n\nTwo copies would share the same ID.", Brand.Name);
                    return;
                }
                Application.Run(new MainForm());
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

            Form[] forms = { new PasswordDialog("626 941 456"), new ConsentDialog("86.108.23.52") };
            string[] names = { "ui-password.png", "ui-consent.png" };
            for (int i = 0; i < forms.Length; i++)
            {
                Form f = forms[i];
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-4000, -4000);
                f.Show();
                Application.DoEvents();
                using (Bitmap b = new Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(b, new Rectangle(0, 0, f.Width, f.Height));
                    b.Save(Path.Combine(dir, names[i]));
                }
                f.Close();
            }
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
                DateTime fend = DateTime.UtcNow.AddSeconds(5);
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

                channel.Send('X', null);
                channel.Close();
                host.Stop();
                log.AppendLine(rects > 0 && fileOk ? "SELFTEST PASS" : "SELFTEST FAIL");
            }
            catch (Exception ex) { log.AppendLine("SELFTEST FAIL: " + ex); }
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest.txt"), log.ToString());
        }
    }
}
