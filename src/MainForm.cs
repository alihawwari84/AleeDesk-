using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace RemoteDesk
{
    // Product name, icon and embedded images in one place.
    public static class Brand
    {
        public const string Name = "AleeDesk";
        public const string Version = "1.1";

        // Built-in relay server, so a fresh install connects without any setup.
        public const string DefaultRelay = "5.189.191.129:7788";
        static Icon icon;

        public static Icon Icon
        {
            get
            {
                if (icon == null) { try { icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { } }
                return icon;
            }
        }

        public static Image Avatar()
        {
            try
            {
                using (Stream s = typeof(Brand).Assembly.GetManifestResourceStream("ALeeDesk.avatar.png"))
                using (Image img = Image.FromStream(s))
                    return new Bitmap(img);
            }
            catch { return null; }
        }
    }

    public class Contact
    {
        public string Name, Id;
    }

    public class HistoryItem
    {
        public bool Incoming;
        public string Who;
        public DateTime Start;
        public int Seconds;
    }

    // Secrets in settings.ini are encrypted with Windows DPAPI, so only this Windows user on
    // this computer can read them back.
    static class Secret
    {
        static readonly byte[] Entropy = System.Text.Encoding.UTF8.GetBytes("AleeDesk");

        public static string Protect(byte[] data)
        {
            return Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(data, Entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser));
        }

        public static byte[] Unprotect(string text)
        {
            try { return System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(text), Entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser); }
            catch { return null; }
        }
    }

    // "Start with Windows": a per-user Run entry that launches AleeDesk in the tray.
    static class Autostart
    {
        const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";

        static string Command { get { return "\"" + Application.ExecutablePath + "\" --background"; } }

        public static bool Enabled
        {
            get
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key))
                    return k != null && k.GetValue(Brand.Name) != null;
            }
            set
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Key))
                {
                    if (value) k.SetValue(Brand.Name, Command);
                    else if (k.GetValue(Brand.Name) != null) k.DeleteValue(Brand.Name);
                }
            }
        }

        // Keep the entry pointing at this exe if it was moved.
        public static void Refresh()
        {
            try { if (Enabled) Enabled = true; } catch { }
        }
    }

    public class Settings
    {
        public string Id;
        public string Server = Brand.DefaultRelay;
        public bool Consent = true;
        public bool RunRelay;
        public bool Discoverable = true;
        public bool Unattended;
        public byte[] UnattendedKey; // derived from the permanent password, never the password itself
        public Dictionary<string, string> Saved = new Dictionary<string, string>(); // desk id -> remembered password
        public List<KeyValuePair<string, DateTime>> Recent = new List<KeyValuePair<string, DateTime>>();
        public List<Contact> Contacts = new List<Contact>();
        public List<HistoryItem> History = new List<HistoryItem>();

        static string FilePath
        {
            get
            {
                // Folder name kept from the first version so the computer keeps its ID.
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteDesk");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "settings.ini");
            }
        }

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int i = line.IndexOf('=');
                    if (i < 0) continue;
                    string k = line.Substring(0, i), v = line.Substring(i + 1);
                    if (k == "id") s.Id = v;
                    else if (k == "server") s.Server = v;
                    else if (k == "consent") s.Consent = v != "0";
                    else if (k == "relay") s.RunRelay = v == "1";
                    else if (k == "lan") s.Discoverable = v != "0";
                    else if (k == "unattended") s.Unattended = v == "1";
                    else if (k == "ukey") s.UnattendedKey = Secret.Unprotect(v);
                    else if (k == "saved") s.ParseSaved(v);
                    else if (k == "recent") s.ParseRecent(v);
                    else if (k == "contacts") s.ParseContacts(v);
                    else if (k == "history") s.ParseHistory(v);
                }
            }
            catch { }
            bool changed = false;
            // Earlier versions defaulted to "this computer"; move them to the built-in relay
            // unless this PC is deliberately running its own.
            if (!s.RunRelay && (s.Server.Trim().Length == 0 || s.Server.Trim() == "127.0.0.1:7788"))
            {
                s.Server = Brand.DefaultRelay;
                changed = true;
            }
            if (s.Id == null || s.Id.Length != 9)
            {
                byte[] r = Proto.Rand(4);
                s.Id = (100000000 + (BitConverter.ToUInt32(r, 0) % 900000000u)).ToString();
                changed = true;
            }
            if (changed) s.Save();
            return s;
        }

        void ParseRecent(string v)
        {
            foreach (string part in v.Split(';'))
            {
                string[] kv = part.Split(':');
                long ticks;
                if (kv.Length == 2 && kv[0].Length >= 4 && long.TryParse(kv[1], out ticks))
                    Recent.Add(new KeyValuePair<string, DateTime>(kv[0], new DateTime(ticks, DateTimeKind.Utc)));
            }
        }

        void ParseContacts(string v)
        {
            foreach (string part in v.Split(';'))
            {
                string[] kv = part.Split('|');
                if (kv.Length == 2 && kv[1].Length >= 4)
                    Contacts.Add(new Contact { Name = Uri.UnescapeDataString(kv[0]), Id = kv[1] });
            }
        }

        void ParseHistory(string v)
        {
            foreach (string part in v.Split(';'))
            {
                string[] f = part.Split('|');
                long ticks;
                int secs;
                if (f.Length == 4 && long.TryParse(f[2], out ticks) && int.TryParse(f[3], out secs))
                    History.Add(new HistoryItem { Incoming = f[0] == "in", Who = Uri.UnescapeDataString(f[1]), Start = new DateTime(ticks, DateTimeKind.Utc), Seconds = secs });
            }
        }

        public void AddRecent(string id)
        {
            Recent.RemoveAll(delegate(KeyValuePair<string, DateTime> r) { return r.Key == id; });
            Recent.Insert(0, new KeyValuePair<string, DateTime>(id, DateTime.UtcNow));
            if (Recent.Count > 9) Recent.RemoveRange(9, Recent.Count - 9);
        }

        void ParseSaved(string v)
        {
            foreach (string part in v.Split(';'))
            {
                string[] kv = part.Split(':');
                if (kv.Length != 2) continue;
                byte[] pwd = Secret.Unprotect(kv[1]);
                if (pwd != null) Saved[kv[0]] = System.Text.Encoding.UTF8.GetString(pwd);
            }
        }

        public void AddHistory(bool incoming, string who, DateTime start)
        {
            History.Insert(0, new HistoryItem { Incoming = incoming, Who = who, Start = start, Seconds = (int)(DateTime.UtcNow - start).TotalSeconds });
            if (History.Count > 30) History.RemoveRange(30, History.Count - 30);
        }

        public void Save()
        {
            List<string> rec = new List<string>(), con = new List<string>(), his = new List<string>();
            foreach (KeyValuePair<string, DateTime> r in Recent) rec.Add(r.Key + ":" + r.Value.Ticks);
            foreach (Contact c in Contacts) con.Add(Uri.EscapeDataString(c.Name) + "|" + c.Id);
            foreach (HistoryItem h in History) his.Add((h.Incoming ? "in" : "out") + "|" + Uri.EscapeDataString(h.Who) + "|" + h.Start.Ticks + "|" + h.Seconds);
            List<string> sav = new List<string>();
            foreach (KeyValuePair<string, string> p in Saved) sav.Add(p.Key + ":" + Secret.Protect(System.Text.Encoding.UTF8.GetBytes(p.Value)));
            try
            {
                File.WriteAllLines(FilePath, new string[] {
                    "id=" + Id, "server=" + Server, "consent=" + (Consent ? "1" : "0"), "relay=" + (RunRelay ? "1" : "0"), "lan=" + (Discoverable ? "1" : "0"),
                    "unattended=" + (Unattended ? "1" : "0"), "ukey=" + (UnattendedKey != null ? Secret.Protect(UnattendedKey) : ""), "saved=" + string.Join(";", sav),
                    "recent=" + string.Join(";", rec), "contacts=" + string.Join(";", con), "history=" + string.Join(";", his)
                });
            }
            catch { }
        }
    }

    // Main window: navigation on the left (Remote Access, Devices, Contacts, Sessions,
    // Settings), the selected page on the right.
    public class MainForm : Form
    {
        const int RelayPort = 7788;
        const float PageW = 738f;
        static readonly string[] PageNames = { "Remote Access", "Devices", "Contacts", "Sessions", "Settings" };

        readonly Settings cfg = Settings.Load();
        readonly HostService host = new HostService();
        readonly ToolTip tip = new ToolTip();
        readonly Timer clock = new Timer { Interval = 1000 };
        readonly List<NavItem> nav = new List<NavItem>();
        readonly List<Panel> pages = new List<Panel>();
        Label pageTitle, lblPwd, lblStatus, lblRelay;
        FlatButton btnDefaultRelay;
        StatusPill pill;
        InputBox idInput, txtServer, contactName, contactId;
        Toggle tglConsent, tglRelay;
        FlatButton btnConnect;
        HeroPanel hero;
        FlowLayoutPanel devicesList, contactsList, sessionsList;
        Action cancelConnect;
        HostBanner banner;
        string lastStatus = "Starting...";

        // What the connection card on the Remote Access page shows.
        string connectingTo, connectingText;
        ViewerForm outView;
        string outId;
        DateTime outStart;
        bool outDirect;
        string inWho;
        DateTime inStart;
        string resultTitle, resultText;
        bool resultError;

        NotifyIcon tray;
        bool quitting, startHidden, trayHintShown;
        Toggle tglUnattended, tglAutostart;
        FlatButton btnUnattendedPwd;
        Label lblUnattended;

        public MainForm() : this(false) { }

        // startHidden: launched with Windows - stay in the tray until opened.
        public MainForm(bool startHidden)
        {
            this.startHidden = startHidden;
            Text = Brand.Name;
            Icon = Brand.Icon;
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1040, 660);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9.5f);
            BackColor = Theme.Surface;

            Panel content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface };
            pageTitle = new Label { Font = new Font("Segoe UI Semibold", 20f), ForeColor = Theme.Ink, AutoSize = true, Location = new Point(34, 22) };
            content.Controls.Add(pageTitle);
            SmoothPicture avatar = new SmoothPicture { Image = Brand.Avatar(), Location = new Point(734, 22), Size = new Size(40, 40) };
            tip.SetToolTip(avatar, "ALee");
            content.Controls.Add(avatar);
            pages.Add(BuildRemotePage());
            pages.Add(BuildDevicesPage());
            pages.Add(BuildContactsPage());
            pages.Add(BuildSessionsPage());
            pages.Add(BuildSettingsPage());
            foreach (Panel p in pages)
            {
                p.Location = new Point(36, 84);
                p.Size = new Size(738, 556);
                p.Visible = false;
                content.Controls.Add(p);
            }

            // Docked in reverse order of adding: the sidebar first, then the page area.
            Controls.Add(content);
            Controls.Add(BuildSidebar());

            clock.Tick += delegate { Tick(); };
            clock.Start();
            WireHost();
            ShowPage(0);
            RefreshHero();
            BuildTray();
            FormClosing += OnClosing;
            Shown += delegate { RebuildDevices(); RebuildContacts(); RebuildSessions(); SetStatus(lastStatus); idInput.Box.Focus(); };
        }

        // ---- tray -----------------------------------------------------------------------

        void BuildTray()
        {
            tray = new NotifyIcon { Icon = Brand.Icon, Text = Brand.Name + " - desk " + Theme.FormatId(cfg.Id), Visible = true };
            ContextMenuStrip m = new ContextMenuStrip();
            m.Items.Add("Open " + Brand.Name, null, delegate { ShowFromTray(); });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Quit", null, delegate { quitting = true; Close(); });
            tray.ContextMenuStrip = m;
            tray.DoubleClick += delegate { ShowFromTray(); };
        }

        internal void ShowFromTray()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        // While this desk must stay reachable (unattended access or start with Windows), the
        // close button only hides the window; Quit in the tray menu really exits.
        void OnClosing(object sender, FormClosingEventArgs e)
        {
            bool stayReachable = cfg.Unattended || tglAutostart.Checked;
            if (!quitting && e.CloseReason == CloseReason.UserClosing && stayReachable)
            {
                e.Cancel = true;
                Hide();
                if (!trayHintShown)
                {
                    trayHintShown = true;
                    tray.ShowBalloonTip(5000, Brand.Name + " is still running", "This desk can still be reached. Right-click the icon here to quit.", ToolTipIcon.Info);
                }
                return;
            }
            host.Stop();
            RelayServer.Stop();
            Lan.Stop();
            tray.Visible = false;
            tray.Dispose();
        }

        protected override void SetVisibleCore(bool value)
        {
            if (startHidden)
            {
                startHidden = false;
                if (!IsHandleCreated) CreateHandle();
                value = false;
            }
            base.SetVisibleCore(value);
        }

        // ---- sidebar and pages ----------------------------------------------------------

        Panel BuildSidebar()
        {
            Panel side = new Panel { Dock = DockStyle.Left, Width = 230, BackColor = Theme.Card };
            side.Paint += (s, e) => { using (Pen p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, side.Width - 1, 0, side.Width - 1, side.Height); };
            side.Controls.Add(new MarkLogo { Location = new Point(24, 24), Size = new Size(30, 30) });
            side.Controls.Add(new Label { Text = Brand.Name, Font = new Font("Segoe UI", 15f, FontStyle.Bold), ForeColor = Theme.Ink, AutoSize = true, Location = new Point(60, 22) });
            string[] glyphs = { Theme.GMonitor, Theme.GLaptop, Theme.GContact, Theme.GClock, Theme.GSettings };
            for (int i = 0; i < PageNames.Length; i++)
            {
                int page = i;
                NavItem n = new NavItem { Text = PageNames[i], Glyph = glyphs[i], Location = new Point(14, 92 + i * 48), Size = new Size(202, 42) };
                n.Click += delegate { ShowPage(page); };
                nav.Add(n);
                side.Controls.Add(n);
            }
            side.Controls.Add(new TwoTone { A = "Less waiting.", B = "More fixing.", Location = new Point(24, 584), Size = new Size(200, 24) });
            side.Controls.Add(new Label { Text = "Version " + Brand.Version, ForeColor = Theme.Faint, Font = new Font("Segoe UI", 8.5f), AutoSize = true, Location = new Point(24, 612) });
            return side;
        }

        internal HeroPanel Hero { get { return hero; } } // for --uishot previews

        internal void ShowPage(int index)
        {
            for (int i = 0; i < pages.Count; i++)
            {
                pages[i].Visible = i == index;
                nav[i].Selected = i == index;
            }
            pageTitle.Text = PageNames[index];
            if (index == 1) { Lan.Query(); RebuildDevices(); }
        }

        static Panel NewPage() { return new Panel { BackColor = Theme.Surface }; }

        static RoundedPanel Card(int x, int y, int w, int h)
        {
            return new RoundedPanel { BackColor = Theme.Card, Stroke = Theme.Border, Radius = 14, Location = new Point(x, y), Size = new Size(w, h) };
        }

        static Label Caption(string text, int x, int y)
        {
            return new Label { Text = text, Font = new Font("Segoe UI Semibold", 8.5f), ForeColor = Theme.Muted, AutoSize = true, Location = new Point(x, y) };
        }

        static FlowLayoutPanel List(int y, int h, bool wrap)
        {
            return new FlowLayoutPanel { Location = new Point(0, y), Size = new Size(738, h), BackColor = Theme.Surface, AutoScroll = true, WrapContents = wrap, FlowDirection = wrap ? FlowDirection.LeftToRight : FlowDirection.TopDown };
        }

        Panel BuildRemotePage()
        {
            Panel p = NewPage();

            RoundedPanel me = Card(0, 0, 360, 196);
            me.Controls.Add(Caption("THIS DESK", 22, 18));
            me.Controls.Add(new Label { Text = Theme.FormatId(cfg.Id), Font = new Font("Segoe UI Semibold", 22f), ForeColor = Theme.Ink, AutoSize = true, Location = new Point(18, 36) });
            FlatButton copyId = FlatButton.IconOnly(Theme.GCopy);
            copyId.SetBounds(304, 44, 34, 34);
            copyId.Click += delegate { Clip(cfg.Id); Flash(copyId); };
            tip.SetToolTip(copyId, "Copy ID");
            me.Controls.Add(copyId);
            me.Controls.Add(Caption("PASSWORD", 22, 96));
            lblPwd = new Label { Font = new Font("Consolas", 15f, FontStyle.Bold), ForeColor = Theme.Ink, AutoSize = true, Location = new Point(20, 114) };
            me.Controls.Add(lblPwd);
            FlatButton copyPwd = FlatButton.IconOnly(Theme.GCopy);
            copyPwd.SetBounds(264, 112, 34, 34);
            copyPwd.Click += delegate { Clip(host.Password); Flash(copyPwd); };
            tip.SetToolTip(copyPwd, "Copy password");
            me.Controls.Add(copyPwd);
            FlatButton newPwd = FlatButton.IconOnly(Theme.GRefresh);
            newPwd.SetBounds(304, 112, 34, 34);
            newPwd.Click += delegate { host.NewPassword(); };
            tip.SetToolTip(newPwd, "New password");
            me.Controls.Add(newPwd);
            pill = new StatusPill { Location = new Point(22, 156), Size = new Size(96, 26) };
            me.Controls.Add(pill);
            lblStatus = new Label { Location = new Point(128, 160), Size = new Size(212, 20), ForeColor = Theme.Muted, Font = new Font("Segoe UI", 8.5f), AutoEllipsis = true };
            me.Controls.Add(lblStatus);
            p.Controls.Add(me);

            RoundedPanel other = Card(378, 0, 360, 196);
            other.Controls.Add(Caption("CONNECT TO ANOTHER DESK", 22, 18));
            idInput = new InputBox("Enter remote desk ID", 12f) { Location = new Point(22, 42), Size = new Size(316, 42) };
            idInput.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; StartConnect(idInput.Value); } };
            other.Controls.Add(idInput);
            btnConnect = new FlatButton { Text = "Connect", Glyph = Theme.GArrow, Location = new Point(22, 96), Size = new Size(316, 42) };
            btnConnect.Click += delegate { StartConnect(idInput.Value); };
            other.Controls.Add(btnConnect);
            other.Controls.Add(new Label { Text = "You'll then be asked for its password.", ForeColor = Theme.Muted, Font = new Font("Segoe UI", 8.5f), Location = new Point(22, 150), Size = new Size(316, 40) });
            p.Controls.Add(other);

            hero = new HeroPanel { Location = new Point(0, 216), Size = new Size(738, 150) };
            p.Controls.Add(hero);
            p.Controls.Add(new FeatureStrip { Location = new Point(0, 392), Size = new Size(738, 96) });
            return p;
        }

        Panel BuildDevicesPage()
        {
            Panel p = NewPage();
            devicesList = List(0, 556, true);
            p.Controls.Add(devicesList);
            return p;
        }

        Panel BuildContactsPage()
        {
            Panel p = NewPage();
            RoundedPanel add = Card(0, 0, 738, 78);
            contactName = new InputBox("Name", 10.5f) { Location = new Point(20, 21), Size = new Size(250, 36) };
            contactId = new InputBox("Desk ID", 10.5f) { Location = new Point(282, 21), Size = new Size(220, 36) };
            FlatButton addBtn = new FlatButton { Text = "Add contact", Glyph = Theme.GAdd, Location = new Point(514, 21), Size = new Size(202, 36) };
            addBtn.Click += delegate { AddContact(); };
            contactId.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; AddContact(); } };
            add.Controls.Add(contactName);
            add.Controls.Add(contactId);
            add.Controls.Add(addBtn);
            p.Controls.Add(add);
            contactsList = List(96, 460, false);
            p.Controls.Add(contactsList);
            return p;
        }

        Panel BuildSessionsPage()
        {
            Panel p = NewPage();
            p.Controls.Add(Caption("RECENT SESSIONS ON THIS DESK", 2, 12));
            FlatButton clear = FlatButton.Ghost("Clear history", Theme.GDelete);
            clear.SetBounds(588, 0, 150, 36);
            clear.Click += delegate { cfg.History.Clear(); cfg.Save(); RebuildSessions(); };
            p.Controls.Add(clear);
            sessionsList = List(50, 506, false);
            p.Controls.Add(sessionsList);
            return p;
        }

        Panel BuildSettingsPage()
        {
            Panel p = NewPage();

            RoundedPanel net = Card(0, 0, 738, 150);
            net.Controls.Add(new GlyphBadge { Glyph = Theme.GGlobe, Location = new Point(22, 16), Size = new Size(32, 32) });
            net.Controls.Add(new Label { Text = "Connection", Font = new Font("Segoe UI Semibold", 11.5f), ForeColor = Theme.Ink, AutoSize = true, Location = new Point(64, 19) });
            net.Controls.Add(new Label { Text = "Relay server", ForeColor = Theme.Muted, AutoSize = true, Location = new Point(24, 70) });
            txtServer = new InputBox("host:port", 10.5f) { Location = new Point(124, 62), Size = new Size(260, 36) };
            txtServer.Value = cfg.Server;
            txtServer.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ApplyServer(null); } };
            net.Controls.Add(txtServer);
            FlatButton apply = new FlatButton { Text = "Apply", Location = new Point(394, 62), Size = new Size(96, 36) };
            apply.Click += delegate { ApplyServer(apply); };
            net.Controls.Add(apply);
            tglRelay = new Toggle { Text = "Run the relay server on this PC", Checked = cfg.RunRelay, Location = new Point(24, 110), Size = new Size(360, 24) };
            net.Controls.Add(tglRelay);
            lblRelay = new Label { Location = new Point(514, 56), Size = new Size(204, 46), ForeColor = Theme.Muted, Font = new Font("Segoe UI", 8.5f) };
            net.Controls.Add(lblRelay);
            btnDefaultRelay = FlatButton.Ghost("Use built-in relay", Theme.GRefresh);
            btnDefaultRelay.Font = new Font("Segoe UI Semibold", 9f);
            btnDefaultRelay.SetBounds(514, 104, 190, 32);
            btnDefaultRelay.Click += delegate { UseServer(Brand.DefaultRelay); };
            net.Controls.Add(btnDefaultRelay);
            p.Controls.Add(net);

            RoundedPanel sec = Card(0, 166, 738, 146);
            sec.Controls.Add(new GlyphBadge { Glyph = Theme.GShield, Location = new Point(22, 16), Size = new Size(32, 32) });
            sec.Controls.Add(new Label { Text = "Security", Font = new Font("Segoe UI Semibold", 11.5f), ForeColor = Theme.Ink, AutoSize = true, Location = new Point(64, 19) });
            tglConsent = new Toggle { Text = "Ask me before accepting a connection", Checked = cfg.Consent, Location = new Point(24, 66), Size = new Size(400, 24) };
            tglConsent.CheckedChanged += delegate { cfg.Consent = tglConsent.Checked; host.RequireConsent = cfg.Consent; cfg.Save(); };
            sec.Controls.Add(tglConsent);
            FlatButton newPwd = FlatButton.Ghost("New password", Theme.GRefresh);
            newPwd.SetBounds(560, 58, 156, 38);
            newPwd.Click += delegate { host.NewPassword(); Flash(newPwd); };
            sec.Controls.Add(newPwd);
            Toggle tglLan = new Toggle { Text = "Let computers on my network find this desk", Checked = cfg.Discoverable, Location = new Point(24, 104), Size = new Size(460, 24) };
            tglLan.CheckedChanged += delegate { cfg.Discoverable = tglLan.Checked; Lan.Discoverable = cfg.Discoverable; cfg.Save(); };
            sec.Controls.Add(tglLan);
            p.Controls.Add(sec);

            RoundedPanel una = Card(0, 328, 738, 140);
            una.Controls.Add(new GlyphBadge { Glyph = Theme.GLock, Location = new Point(22, 16), Size = new Size(32, 32) });
            una.Controls.Add(new Label { Text = "Unattended access", Font = new Font("Segoe UI Semibold", 11.5f), ForeColor = Theme.Ink, AutoSize = true, Location = new Point(64, 19) });
            tglUnattended = new Toggle { Text = "Allow access with a permanent password - no one needs to accept", Checked = cfg.Unattended && cfg.UnattendedKey != null, Location = new Point(24, 62), Size = new Size(520, 24) };
            tglUnattended.CheckedChanged += delegate
            {
                if (tglUnattended.Checked && cfg.UnattendedKey == null && !AskUnattendedPassword()) { tglUnattended.Checked = false; return; }
                cfg.Unattended = tglUnattended.Checked;
                cfg.Save();
                ApplyUnattended();
                if (cfg.Unattended && !tglAutostart.Checked) tglAutostart.Checked = true; // needed to be reachable after a restart
            };
            una.Controls.Add(tglUnattended);
            bool autostartOn = false;
            try { autostartOn = Autostart.Enabled; } catch { }
            tglAutostart = new Toggle { Text = "Start " + Brand.Name + " with Windows", Checked = autostartOn, Location = new Point(24, 98), Size = new Size(520, 24) };
            tglAutostart.CheckedChanged += delegate
            {
                try { Autostart.Enabled = tglAutostart.Checked; }
                catch (Exception ex) { MessageBox.Show(this, "Couldn't change this setting: " + ex.Message, Brand.Name); }
            };
            una.Controls.Add(tglAutostart);
            btnUnattendedPwd = FlatButton.Ghost("Set password", Theme.GLock);
            btnUnattendedPwd.SetBounds(560, 56, 156, 38);
            btnUnattendedPwd.Click += delegate { AskUnattendedPassword(); };
            una.Controls.Add(btnUnattendedPwd);
            lblUnattended = new Label { Location = new Point(560, 100), Size = new Size(156, 22), ForeColor = Theme.Muted, Font = new Font("Segoe UI", 8.5f), TextAlign = ContentAlignment.MiddleCenter };
            una.Controls.Add(lblUnattended);
            p.Controls.Add(una);

            RoundedPanel about = Card(0, 484, 738, 72);
            about.Controls.Add(new MarkLogo { Location = new Point(24, 18), Size = new Size(36, 36) });
            about.Controls.Add(new Label { Text = Brand.Name, Font = new Font("Segoe UI", 12f, FontStyle.Bold), ForeColor = Theme.Ink, AutoSize = true, Location = new Point(72, 22) });
            about.Controls.Add(new TwoTone { A = "Less waiting.", B = "More fixing.", Location = new Point(180, 24), Size = new Size(260, 24) });
            about.Controls.Add(new Label { Text = "Version " + Brand.Version, ForeColor = Theme.Muted, AutoSize = true, Location = new Point(620, 27) });
            p.Controls.Add(about);
            return p;
        }

        bool AskUnattendedPassword()
        {
            using (SetPasswordDialog d = new SetPasswordDialog())
            {
                if (d.ShowDialog(this) != DialogResult.OK) return false;
                cfg.UnattendedKey = Proto.DeriveKey(d.Password, cfg.Id);
            }
            cfg.Save();
            ApplyUnattended();
            Flash(btnUnattendedPwd);
            return true;
        }

        void ApplyUnattended()
        {
            host.UnattendedKey = cfg.Unattended ? cfg.UnattendedKey : null;
            btnUnattendedPwd.Text = cfg.UnattendedKey != null ? "Change password" : "Set password";
            lblUnattended.ForeColor = cfg.Unattended && cfg.UnattendedKey != null ? Theme.Green : Theme.Muted;
            lblUnattended.Text = cfg.UnattendedKey == null ? "No password set yet" : cfg.Unattended ? "On - password set" : "Password set (off)";
        }

        // Lists are rebuilt after the window has been DPI-scaled, so size rows from the
        // (already scaled) page width instead of fixed pixel values.
        float K { get { return pages[0].Width / PageW; } }

        static void Clear(Control c)
        {
            while (c.Controls.Count > 0) c.Controls[0].Dispose();
        }

        Control EmptyCard(string glyph, string title, string text)
        {
            float k = K;
            RoundedPanel empty = new RoundedPanel { BackColor = Theme.Card, Stroke = Theme.Border, Radius = 14, Size = new Size((int)(714 * k), (int)(116 * k)) };
            empty.Controls.Add(new GlyphBadge { Glyph = glyph, Location = new Point((int)(26 * k), (int)(32 * k)), Size = new Size((int)(52 * k), (int)(52 * k)) });
            empty.Controls.Add(new Label { Text = title, Font = new Font("Segoe UI Semibold", 11f), ForeColor = Theme.Ink, AutoSize = true, Location = new Point((int)(96 * k), (int)(34 * k)) });
            empty.Controls.Add(new Label { Text = text, ForeColor = Theme.Muted, AutoSize = true, Location = new Point((int)(97 * k), (int)(60 * k)) });
            return empty;
        }

        string lanShown = "";

        // Section heading that starts a new row in the devices list.
        Label ListHeading(string text, bool first)
        {
            float k = K;
            Label l = Caption(text, 0, 0);
            l.AutoSize = false;
            l.Size = new Size((int)(714 * k), (int)(22 * k));
            l.Margin = new Padding((int)(2 * k), first ? 0 : (int)(8 * k), 0, (int)(6 * k));
            return l;
        }

        void RebuildDevices()
        {
            float k = K;
            devicesList.SuspendLayout();
            Clear(devicesList);

            List<LanDevice> lan = Lan.Devices();
            lanShown = LanSignature(lan);
            devicesList.Controls.Add(ListHeading("ON THIS NETWORK", true));
            if (lan.Count == 0)
            {
                Label none = new Label { Text = cfg.Discoverable ? "No other " + Brand.Name + " computers found on this network yet." : "Network discovery is off (Settings > Security).", ForeColor = Theme.Muted, AutoSize = false, Size = new Size((int)(714 * k), (int)(24 * k)), Margin = new Padding((int)(2 * k), 0, 0, (int)(10 * k)) };
                devicesList.Controls.Add(none);
            }
            foreach (LanDevice d in lan)
            {
                string id = d.Id;
                DeviceTile t = new DeviceTile(d.Name, Theme.FormatId(d.Id), true) { Size = new Size((int)(226 * k), (int)(130 * k)), Margin = new Padding(0, 0, (int)(18 * k), (int)(18 * k)) };
                t.Click += delegate { StartConnect(id); };
                tip.SetToolTip(t, "Connect directly to " + d.Name + " (" + d.Direct.Address + ")");
                devicesList.Controls.Add(t);
            }
            if (devicesList.Controls.Count > 0) devicesList.SetFlowBreak(devicesList.Controls[devicesList.Controls.Count - 1], true);

            devicesList.Controls.Add(ListHeading("COMPUTERS YOU CONNECTED TO", false));
            if (cfg.Recent.Count == 0)
                devicesList.Controls.Add(EmptyCard(Theme.GMonitor, "No devices yet", "Computers you connect to show up here - one click to reconnect."));
            foreach (KeyValuePair<string, DateTime> r in cfg.Recent)
            {
                string id = r.Key;
                DeviceTile t = new DeviceTile(Theme.FormatId(id), "Connected " + Theme.Ago(r.Value), false) { Size = new Size((int)(226 * k), (int)(130 * k)), Margin = new Padding(0, 0, (int)(18 * k), (int)(18 * k)) };
                t.Click += delegate { StartConnect(id); };
                ContextMenuStrip menu = new ContextMenuStrip();
                menu.Items.Add("Connect", null, delegate { StartConnect(id); });
                menu.Items.Add("Add to contacts", null, delegate { ShowPage(2); contactId.Value = Theme.FormatId(id); contactName.Box.Focus(); });
                if (cfg.Saved.ContainsKey(id))
                    menu.Items.Add("Forget saved password", null, delegate { cfg.Saved.Remove(id); cfg.Save(); });
                menu.Items.Add("Remove", null, delegate
                {
                    cfg.Recent.RemoveAll(delegate(KeyValuePair<string, DateTime> x) { return x.Key == id; });
                    cfg.Save();
                    RebuildDevices();
                });
                t.ContextMenuStrip = menu;
                tip.SetToolTip(t, "Click to connect, right-click for more");
                devicesList.Controls.Add(t);
            }
            devicesList.ResumeLayout();
        }

        void RebuildContacts()
        {
            float k = K;
            contactsList.SuspendLayout();
            Clear(contactsList);
            if (cfg.Contacts.Count == 0)
                contactsList.Controls.Add(EmptyCard(Theme.GContact, "No contacts yet", "Save the computers you support with a name, then connect in one click."));
            foreach (Contact c in cfg.Contacts)
            {
                Contact contact = c;
                ContactRow row = new ContactRow(c.Name, c.Id, delegate { StartConnect(contact.Id); }, delegate
                {
                    cfg.Contacts.Remove(contact);
                    cfg.Save();
                    RebuildContacts();
                }) { Size = new Size((int)(714 * k), (int)(62 * k)), Margin = new Padding(0, 0, 0, (int)(10 * k)) };
                tip.SetToolTip(row.RemoveButton, "Remove contact");
                contactsList.Controls.Add(row);
            }
            contactsList.ResumeLayout();
        }

        void RebuildSessions()
        {
            float k = K;
            sessionsList.SuspendLayout();
            Clear(sessionsList);
            if (cfg.History.Count == 0)
                sessionsList.Controls.Add(EmptyCard(Theme.GClock, "No sessions yet", "Every connection to or from this desk is listed here with its duration."));
            foreach (HistoryItem h in cfg.History)
                sessionsList.Controls.Add(new SessionRow(h.Incoming, h.Who, h.Start, h.Seconds) { Size = new Size((int)(714 * k), (int)(58 * k)), Margin = new Padding(0, 0, 0, (int)(8 * k)) });
            sessionsList.ResumeLayout();
        }

        void AddContact()
        {
            string name = contactName.Value.Trim(), id = "";
            foreach (char c in contactId.Value) if (char.IsDigit(c)) id += c;
            if (name.Length == 0) { contactName.Flag(); return; }
            if (id.Length < 4) { contactId.Flag(); return; }
            cfg.Contacts.RemoveAll(delegate(Contact c) { return c.Id == id; });
            cfg.Contacts.Insert(0, new Contact { Name = name, Id = id });
            cfg.Save();
            contactName.Value = "";
            contactId.Value = "";
            RebuildContacts();
        }

        // ---- helpers --------------------------------------------------------------------

        void UI(MethodInvoker a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(a); } catch { }
        }

        static void Clip(string s) { try { Clipboard.SetText(s); } catch { } }

        // Briefly swap a button's icon for a check mark to confirm the click.
        static void Flash(FlatButton b)
        {
            if (b.Tag == null) b.Tag = b.Glyph ?? "";
            b.Glyph = Theme.GCheck;
            b.Invalidate();
            Timer t = new Timer { Interval = 1200 };
            t.Tick += delegate { t.Stop(); t.Dispose(); b.Glyph = (string)b.Tag; b.Invalidate(); };
            t.Start();
        }

        // ---- connection card ------------------------------------------------------------

        void RefreshHero()
        {
            if (connectingTo != null)
                hero.Set(Theme.GClock, Theme.Amber, "Connecting…", connectingText, null, null, null, "Cancel", delegate { if (cancelConnect != null) cancelConnect(); });
            else if (outView != null)
                hero.Set(Theme.GCheck, Theme.Green, "Connected", "Remote device is ready.", Theme.FormatId(outId), outDirect ? "Online · direct" : "Online · via relay", Theme.Clock(DateTime.UtcNow - outStart), "Disconnect", delegate { if (outView != null) outView.Close(); });
            else if (inWho != null)
                hero.Set(Theme.GShield, Theme.Primary, "Someone is connected", "They can see and control this computer.", inWho, "Viewing this desk", Theme.Clock(DateTime.UtcNow - inStart), "Disconnect", delegate { host.AbortSession(); });
            else if (resultTitle != null)
                hero.Set(resultError ? Theme.GWarn : Theme.GInfo, resultError ? Theme.Red : Theme.Primary, resultTitle, resultText, null, null, null, "Dismiss", delegate { resultTitle = null; RefreshHero(); });
            else
                hero.Set(Theme.GMonitor, Theme.Primary, "Ready to connect", "Enter a desk ID above, or pick one from Devices or Contacts.", null, null, null, null, null);
        }

        int ticks;

        void Tick()
        {
            if (outView != null && connectingTo == null) hero.SetTime(Theme.Clock(DateTime.UtcNow - outStart));
            else if (inWho != null && connectingTo == null) hero.SetTime(Theme.Clock(DateTime.UtcNow - inStart));
            // keep the "On this network" list current while the Devices page is open
            if (++ticks % 2 == 0 && pages[1].Visible && LanSignature(Lan.Devices()) != lanShown) RebuildDevices();
        }

        static string LanSignature(List<LanDevice> list)
        {
            string s = "";
            foreach (LanDevice d in list) s += d.Id + "@" + d.Direct + ";";
            return s;
        }

        void SetResult(bool error, string title, string text)
        {
            resultError = error;
            resultTitle = title;
            resultText = text;
            RefreshHero();
        }

        // ---- hosting --------------------------------------------------------------------

        void WireHost()
        {
            host.Id = cfg.Id;
            host.Server = cfg.Server;
            host.RequireConsent = cfg.Consent;
            host.PasswordChanged = delegate { UI(delegate { lblPwd.Text = host.Password; }); };
            host.Status = delegate(string s) { lastStatus = s; UI(delegate { SetStatus(s); }); };
            host.SetClipboard = delegate(string t) { UI(delegate { Clip(t); }); };
            host.SessionChanged = OnSessionChanged;
            host.Consent = AskConsent;
            host.NewPassword();
            lblPwd.Text = host.Password;
            SetStatus("Starting...");
            if (cfg.RunRelay) UseLocalRelay(false); else RelayNote();
            host.Start();
            Lan.Discoverable = cfg.Discoverable;
            Lan.Start(cfg.Id, host.AcceptDirect);
            ApplyUnattended();
            Autostart.Refresh();
            tglRelay.CheckedChanged += delegate
            {
                cfg.RunRelay = tglRelay.Checked;
                cfg.Save();
                if (cfg.RunRelay) UseLocalRelay(true);
                else { RelayServer.Stop(); UseServer(Brand.DefaultRelay); }
            };
        }

        void SetStatus(string s)
        {
            Color c;
            string label, detail = s;
            if (s.StartsWith("Ready")) { c = Theme.Green; label = "Online"; detail = "Ready for connections"; }
            else if (s.StartsWith("Connected")) { c = Theme.Primary; label = "In session"; }
            else if (s.StartsWith("Receiving") || s.StartsWith("Received")) { c = Theme.Primary; label = "File transfer"; }
            else if (s.StartsWith("Offline")) { c = Theme.Red; label = "Offline"; detail = s.Substring(Math.Min(s.Length, 9)).Trim(); }
            else if (s.Contains("error")) { c = Theme.Red; label = "Error"; }
            else { c = Theme.Amber; label = "Connecting"; }
            pill.Dot = c;
            pill.Text = label;
            pill.FitWidth();
            pill.Invalidate();
            lblStatus.Left = pill.Right + pill.Height / 3;
            lblStatus.Width = lblStatus.Parent.Width - lblStatus.Left - pill.Left;
            lblStatus.Text = detail;
            tip.SetToolTip(pill, s);
            tip.SetToolTip(lblStatus, s);
        }

        void UseLocalRelay(bool restartHost)
        {
            string err = RelayServer.Start(RelayPort);
            if (err != null)
            {
                lblRelay.ForeColor = Theme.Red;
                lblRelay.Text = "Could not start the relay on port " + RelayPort + ": " + err;
                return;
            }
            lblRelay.ForeColor = Theme.Muted;
            lblRelay.Text = "Relay on. Other PC: set the relay to " + LanIp() + ":" + RelayPort + " and allow " + Brand.Name + " through the firewall.";
            btnDefaultRelay.Visible = false;
            txtServer.Value = "127.0.0.1:" + RelayPort;
            cfg.Server = txtServer.Value;
            cfg.Save();
            host.Server = cfg.Server;
            if (restartHost) host.Start();
        }

        static string LanIp()
        {
            try
            {
                foreach (System.Net.NetworkInformation.NetworkInterface ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                    System.Net.NetworkInformation.IPInterfaceProperties p = ni.GetIPProperties();
                    if (p.GatewayAddresses.Count == 0) continue;
                    foreach (System.Net.NetworkInformation.UnicastIPAddressInformation ua in p.UnicastAddresses)
                        if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) return ua.Address.ToString();
                }
            }
            catch { }
            return "<this-PC-IP>";
        }

        void ApplyServer(FlatButton feedback)
        {
            string server = txtServer.Value.Trim();
            UseServer(server.Length == 0 ? Brand.DefaultRelay : server);
            if (feedback != null) Flash(feedback);
        }

        void UseServer(string server)
        {
            txtServer.Value = server;
            cfg.Server = server;
            cfg.Save();
            host.Server = server;
            host.Start();
            RelayNote();
        }

        // Explains which relay is in use, with a one-click way back to the built-in one.
        void RelayNote()
        {
            if (cfg.RunRelay) return;
            bool builtIn = cfg.Server == Brand.DefaultRelay;
            lblRelay.ForeColor = Theme.Muted;
            lblRelay.Text = builtIn ? "Using the built-in " + Brand.Name + " relay. Nothing to set up."
                                    : "Custom relay. Both computers must use the same one.";
            btnDefaultRelay.Visible = !builtIn;
        }

        bool AskConsent(string ip)
        {
            bool allow = false;
            Invoke(new MethodInvoker(delegate
            {
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                using (ConsentDialog d = new ConsentDialog(ip)) allow = d.ShowDialog() == DialogResult.OK;
            }));
            return allow;
        }

        void OnSessionChanged(bool active, string who)
        {
            UI(delegate
            {
                if (banner != null) { banner.Close(); banner.Dispose(); banner = null; }
                if (active)
                {
                    banner = new HostBanner(who, delegate { host.AbortSession(); });
                    banner.Show();
                    inWho = who;
                    inStart = DateTime.UtcNow;
                }
                else if (inWho != null)
                {
                    cfg.AddHistory(true, inWho, inStart);
                    cfg.Save();
                    inWho = null;
                    RebuildSessions();
                }
                RefreshHero();
            });
        }

        // ---- connecting -----------------------------------------------------------------

        void StartConnect(string text)
        {
            ShowPage(0);
            if (cancelConnect != null) return;
            string id = "";
            foreach (char c in text) if (char.IsDigit(c)) id += c;
            if (id.Length < 4)
            {
                SetResult(true, "Enter a desk ID", "Type the 9-digit ID shown on the other computer, then press Connect.");
                idInput.Flag();
                return;
            }
            if (id == cfg.Id)
            {
                SetResult(true, "That's this desk", "You entered this computer's own ID. Use the ID shown on the other computer.");
                return;
            }
            string fid = Theme.FormatId(id), pwd;
            idInput.Value = fid;
            bool remember = false, usedSaved = cfg.Saved.TryGetValue(id, out pwd);
            if (!usedSaved)
                using (PasswordDialog d = new PasswordDialog(fid))
                {
                    if (d.ShowDialog(this) != DialogResult.OK) return;
                    pwd = d.Password;
                    remember = d.Remember;
                }

            btnConnect.Enabled = false;
            resultTitle = null;
            connectingTo = fid;
            LanDevice lan = Lan.Find(id);
            connectingText = lan != null ? "Looking for " + lan.Name + " on your network..." : "Contacting the relay server...";
            RefreshHero();

            // Same network: try a direct connection first (fast, no internet needed), otherwise
            // or if that fails, go through the relay server.
            bool cancelled = false;
            cancelConnect = delegate { cancelled = true; };
            System.Threading.Thread route = new System.Threading.Thread(delegate()
            {
                bool direct = lan != null && Lan.Reachable(lan.Direct, 1500);
                string server = direct ? lan.Direct.ToString() : cfg.Server;
                UI(delegate
                {
                    if (cancelled) { EndConnect(); SetResult(false, "Connection cancelled", "No connection was made."); return; }
                    connectingText = direct ? "Connecting directly on your network..." : "Contacting the relay server...";
                    RefreshHero();
                    cancelConnect = Connector.Start(server, id, pwd,
                        delegate(string s) { UI(delegate { connectingText = s; RefreshHero(); }); },
                        delegate(Channel ch)
                        {
                            UI(delegate
                            {
                                EndConnect();
                                cfg.AddRecent(id);
                                if (remember) cfg.Saved[id] = pwd;
                                cfg.Save();
                                RebuildDevices();
                                OpenViewer(ch, id, direct, pwd);
                            });
                        },
                        delegate(string err)
                        {
                            UI(delegate
                            {
                                EndConnect();
                                if (err == "Cancelled") SetResult(false, "Connection cancelled", "No connection was made.");
                                else if (usedSaved && err == "Wrong password")
                                {
                                    cfg.Saved.Remove(id);
                                    cfg.Save();
                                    SetResult(true, "Saved password no longer works", "The password for " + fid + " has changed. Connect again and enter the new one.");
                                }
                                else SetResult(true, "Couldn't connect", err);
                            });
                        });
                });
            });
            route.IsBackground = true;
            route.Start();
        }

        void OpenViewer(Channel ch, string id, bool direct, string pwd)
        {
            string fid = Theme.FormatId(id);
            DateTime start = DateTime.UtcNow;
            ViewerForm v = new ViewerForm(ch, Brand.Name + " - " + fid + (direct ? "  (local network)" : ""));
            outView = v;
            outId = id;
            outDirect = direct;
            outStart = start;
            v.FormClosed += delegate
            {
                cfg.AddHistory(false, fid, start);
                cfg.Save();
                RebuildSessions();
                if (outView == v)
                {
                    outView = null;
                    SetResult(false, "Session ended", "You were connected to " + fid + " for " + Theme.Clock(DateTime.UtcNow - start) + ".");
                }
            };
            RefreshHero();
            v.Show();
            v.Start();
        }

        void EndConnect()
        {
            cancelConnect = null;
            connectingTo = null;
            btnConnect.Enabled = true;
        }
    }
}
