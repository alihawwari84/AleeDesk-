using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace RemoteDesk
{
    public class FileEntry
    {
        public char Kind;       // D folder, F file, V drive, P common folder (Desktop etc.)
        public string Name, FullPath;
        public long Size;
        public DateTime Modified;

        public bool IsFolder { get { return Kind != 'F'; } }
    }

    // Folder listings as text, so the same code serves the local pane and the remote computer.
    //   first line: the folder's full path ("" = top level: drives and common folders)
    //   then one line per entry: kind \t name \t full path \t size \t modified ticks
    //   "!message" instead when the folder can't be read
    public static class FileListing
    {
        public static string Describe(string path)
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                if (string.IsNullOrEmpty(path))
                {
                    sb.Append('\n');
                    string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    Line(sb, 'P', "Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), 0, DateTime.MinValue);
                    Line(sb, 'P', "Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), 0, DateTime.MinValue);
                    if (Directory.Exists(Path.Combine(home, "Downloads"))) Line(sb, 'P', "Downloads", Path.Combine(home, "Downloads"), 0, DateTime.MinValue);
                    foreach (DriveInfo d in DriveInfo.GetDrives())
                    {
                        if (!d.IsReady) continue;
                        string label = d.VolumeLabel.Length > 0 ? d.VolumeLabel : d.DriveType == DriveType.Fixed ? "Local Disk" : d.DriveType.ToString();
                        Line(sb, 'V', label + " (" + d.Name.TrimEnd('\\') + ")", d.Name, d.AvailableFreeSpace, DateTime.MinValue);
                    }
                    return sb.ToString();
                }
                DirectoryInfo di = new DirectoryInfo(path);
                sb.Append(di.FullName).Append('\n');
                foreach (DirectoryInfo sub in di.GetDirectories())
                    if (!Hidden(sub.Attributes)) Line(sb, 'D', sub.Name, sub.FullName, 0, sub.LastWriteTimeUtc);
                foreach (FileInfo f in di.GetFiles())
                    if (!Hidden(f.Attributes)) Line(sb, 'F', f.Name, f.FullName, f.Length, f.LastWriteTimeUtc);
                return sb.ToString();
            }
            catch (Exception ex) { return "!" + ex.Message; }
        }

        static bool Hidden(FileAttributes a) { return (a & (FileAttributes.Hidden | FileAttributes.System)) != 0; }

        static void Line(StringBuilder sb, char kind, string name, string full, long size, DateTime modified)
        {
            sb.Append(kind).Append('\t').Append(name).Append('\t').Append(full).Append('\t').Append(size).Append('\t').Append(modified.Ticks).Append('\n');
        }

        // Returns null and sets error when the listing is an error message.
        public static List<FileEntry> Parse(string text, out string path, out string error)
        {
            path = "";
            error = null;
            if (text.StartsWith("!")) { error = text.Substring(1); return null; }
            string[] lines = text.Split('\n');
            path = lines[0];
            List<FileEntry> list = new List<FileEntry>();
            for (int i = 1; i < lines.Length; i++)
            {
                string[] f = lines[i].Split('\t');
                long size, ticks;
                if (f.Length < 5 || f[0].Length != 1 || !long.TryParse(f[3], out size) || !long.TryParse(f[4], out ticks)) continue;
                list.Add(new FileEntry { Kind = f[0][0], Name = f[1], FullPath = f[2], Size = size, Modified = new DateTime(ticks, DateTimeKind.Utc) });
            }
            return list;
        }

        public static string Size(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return kb.ToString("0") + " KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return mb.ToString("0.#") + " MB";
            return (mb / 1024.0).ToString("0.#") + " GB";
        }
    }

    // One side of the file manager: title, current folder, Up/Refresh, and the file list.
    class FilePane : RoundedPanel
    {
        public string CurrentPath = "";
        public readonly ListView List = new ListView();
        public Action<string> Navigate; // asks for a folder to be listed ("" = top level)
        readonly Label lblPath;
        readonly FlatButton up, refresh;
        static ImageList icons;

        public FilePane(string title, string glyph)
        {
            BackColor = Theme.Card;
            Stroke = Theme.Border;
            Radius = 14;
            Controls.Add(new GlyphBadge { Glyph = glyph, Location = new Point(16, 14), Size = new Size(30, 30) });
            Controls.Add(new Label { Text = title, Font = new Font("Segoe UI Semibold", 11f), ForeColor = Theme.Ink, AutoSize = true, Location = new Point(54, 17) });
            up = FlatButton.IconOnly(Theme.GBack);
            up.Click += delegate { if (CurrentPath.Length > 0 && Navigate != null) Navigate(ParentFolder(CurrentPath)); };
            refresh = FlatButton.IconOnly(Theme.GRefresh);
            refresh.Click += delegate { if (Navigate != null) Navigate(CurrentPath); };
            Controls.Add(up);
            Controls.Add(refresh);
            lblPath = new Label { ForeColor = Theme.Muted, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
            Controls.Add(lblPath);

            List.View = View.Details;
            List.FullRowSelect = true;
            List.HideSelection = false;
            List.BorderStyle = BorderStyle.None;
            List.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            List.Font = new Font("Segoe UI", 9.5f);
            List.Columns.Add("Name", 230);
            List.Columns.Add("Size", 80, HorizontalAlignment.Right);
            List.Columns.Add("Modified", 120);
            List.SmallImageList = Icons();
            List.DoubleClick += delegate
            {
                if (List.SelectedItems.Count != 1) return;
                FileEntry e = (FileEntry)List.SelectedItems[0].Tag;
                if (e.IsFolder && Navigate != null) Navigate(e.FullPath);
            };
            List.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Back && CurrentPath.Length > 0 && Navigate != null) Navigate(ParentFolder(CurrentPath));
            };
            Controls.Add(List);
        }

        static string ParentFolder(string path)
        {
            string t = path.TrimEnd('\\');
            if (t.EndsWith(":")) return ""; // drive root -> top level
            DirectoryInfo p = Directory.GetParent(t);
            return p == null ? "" : p.FullName;
        }

        static ImageList Icons()
        {
            if (icons != null) return icons;
            icons = new ImageList { ImageSize = new Size(20, 20), ColorDepth = ColorDepth.Depth32Bit };
            string[] glyphs = { "", "", "", "" }; // folder, file, drive, common folder
            Color[] colors = { Color.FromArgb(234, 179, 8), Theme.Muted, Theme.Text, Theme.Primary };
            using (Font f = new Font(Theme.IconFont, 11f))
                for (int i = 0; i < glyphs.Length; i++)
                {
                    Bitmap b = new Bitmap(20, 20);
                    using (Graphics g = Graphics.FromImage(b))
                    {
                        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                        using (SolidBrush br = new SolidBrush(colors[i]))
                        using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                            g.DrawString(glyphs[i], f, br, new RectangleF(0, 0, 20, 20), sf);
                    }
                    icons.Images.Add(b);
                }
            return icons;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (lblPath == null || up == null || refresh == null) return; // still being built
            float s = Width / 470f;
            refresh.SetBounds(Width - (int)(46 * s), (int)(12 * s), (int)(32 * s), (int)(32 * s));
            up.SetBounds(refresh.Left - (int)(38 * s), (int)(12 * s), (int)(32 * s), (int)(32 * s));
            lblPath.SetBounds((int)(16 * s), (int)(52 * s), Width - (int)(32 * s), (int)(22 * s));
            List.SetBounds((int)(10 * s), (int)(80 * s), Width - (int)(20 * s), Height - (int)(90 * s));
            if (List.Columns.Count == 3)
            {
                int w = List.ClientSize.Width;
                List.Columns[1].Width = (int)(112 * s);
                List.Columns[2].Width = (int)(112 * s);
                List.Columns[0].Width = Math.Max(60, w - List.Columns[1].Width - List.Columns[2].Width - 4);
            }
        }

        public void ShowListing(string path, List<FileEntry> entries)
        {
            CurrentPath = path;
            lblPath.Text = path.Length == 0 ? "This PC" : path;
            up.Enabled = path.Length > 0;
            List.BeginUpdate();
            List.Items.Clear();
            foreach (FileEntry e in entries)
            {
                int icon = e.Kind == 'D' ? 0 : e.Kind == 'F' ? 1 : e.Kind == 'V' ? 2 : 3;
                string size = e.Kind == 'F' ? FileListing.Size(e.Size) : e.Kind == 'V' ? FileListing.Size(e.Size) + " free" : "";
                string date = e.Modified == DateTime.MinValue || e.Modified.Year < 1990 ? "" : e.Modified.ToLocalTime().ToString("dd/MM/yyyy");
                ListViewItem it = new ListViewItem(new string[] { e.Name, size, date }, icon);
                it.Tag = e;
                List.Items.Add(it);
            }
            List.EndUpdate();
        }

        public void ShowError(string message)
        {
            lblPath.Text = "Can't open this folder: " + message;
        }

        public List<FileEntry> Selected()
        {
            List<FileEntry> list = new List<FileEntry>();
            foreach (ListViewItem it in List.SelectedItems) list.Add((FileEntry)it.Tag);
            return list;
        }
    }

    // Thin progress line with a percentage.
    class ProgressLine : Painted
    {
        float value;
        public float Value { get { return value; } set { this.value = Math.Max(0, Math.Min(1, value)); Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Outside);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (System.Drawing.Drawing2D.GraphicsPath p = Theme.Round(new RectangleF(0, 0, Width - 1, Height - 1), Height / 2f))
            using (SolidBrush b = new SolidBrush(Theme.Border))
                g.FillPath(b, p);
            if (value > 0)
                using (System.Drawing.Drawing2D.GraphicsPath p = Theme.Round(new RectangleF(0, 0, Math.Max(Height, (Width - 1) * value), Height - 1), Height / 2f))
                using (SolidBrush b = new SolidBrush(Theme.Primary))
                    g.FillPath(b, p);
        }
    }

    // Two-pane file manager: this computer on the left, the remote computer on the right.
    // Everything goes over the viewer's encrypted session.
    public class FileManagerForm : Form
    {
        readonly ViewerForm viewer;
        readonly FilePane local, remote;
        readonly ProgressLine bar;
        readonly Label status;
        readonly FlatButton btnUpload, btnDownload;
        int nextReq = 1;
        int remoteListReq;

        // uploads in flight: count of files still waiting for the remote side to confirm
        int uploadsPending, uploadsFailed;

        // downloads: request id -> local folder it goes into; the file being written
        readonly Dictionary<int, string> downloadTo = new Dictionary<int, string>();
        FileStream incoming;
        string incomingTemp, incomingFinal;
        long incomingSize, incomingDone;

        public FileManagerForm(ViewerForm viewer, string title)
        {
            this.viewer = viewer;
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = title;
            Icon = Brand.Icon;
            ClientSize = new Size(1040, 640);
            MinimumSize = new Size(800, 480);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Surface;
            Font = new Font("Segoe UI", 9.5f);

            local = new FilePane("This computer", Theme.GLaptop) { Location = new Point(20, 20), Size = new Size(470, 520) };
            remote = new FilePane("Remote computer", Theme.GMonitor) { Location = new Point(550, 20), Size = new Size(470, 520) };
            local.Navigate = ListLocal;
            remote.Navigate = ListRemote;
            Controls.Add(local);
            Controls.Add(remote);

            btnUpload = new FlatButton { Glyph = Theme.GArrow, Location = new Point(500, 230), Size = new Size(40, 40) };
            btnDownload = new FlatButton { Glyph = Theme.GBack, Location = new Point(500, 280), Size = new Size(40, 40) };
            btnUpload.Click += delegate { Upload(); };
            btnDownload.Click += delegate { Download(); };
            ToolTip tip = new ToolTip();
            tip.SetToolTip(btnUpload, "Send the selected files to the remote computer");
            tip.SetToolTip(btnDownload, "Copy the selected files to this computer");
            Controls.Add(btnUpload);
            Controls.Add(btnDownload);

            bar = new ProgressLine { Location = new Point(20, 560), Size = new Size(1000, 8) };
            status = new Label { Location = new Point(20, 578), Size = new Size(1000, 44), ForeColor = Theme.Muted, Text = "Select files, then use → to send them to the remote computer or ← to copy them here. Folders are included with everything in them." };
            Controls.Add(bar);
            Controls.Add(status);

            Resize += delegate { LayoutPanes(); };
            Shown += delegate { LayoutPanes(); ListLocal(""); ListRemote(""); };
            FormClosed += delegate { CloseIncoming(true); };
        }

        void LayoutPanes()
        {
            float s = DeviceScale();
            int gap = (int)(20 * s), mid = (int)(60 * s);
            int w = (ClientSize.Width - 2 * gap - mid) / 2, h = ClientSize.Height - (int)(120 * s);
            local.SetBounds(gap, gap, w, h);
            remote.SetBounds(gap + w + mid, gap, w, h);
            int cx = gap + w + (mid - btnUpload.Width) / 2;
            btnUpload.Location = new Point(cx, gap + h / 2 - btnUpload.Height - (int)(5 * s));
            btnDownload.Location = new Point(cx, gap + h / 2 + (int)(5 * s));
            bar.SetBounds(gap, gap + h + (int)(20 * s), ClientSize.Width - 2 * gap, (int)(8 * s));
            status.SetBounds(gap, bar.Bottom + (int)(10 * s), ClientSize.Width - 2 * gap, (int)(44 * s));
        }

        float DeviceScale()
        {
            using (Graphics g = CreateGraphics()) return g.DpiX / 96f;
        }

        void UI(MethodInvoker a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(a); } catch { }
        }

        void SetStatus(string text, float progress)
        {
            status.Text = text;
            status.ForeColor = Theme.Muted;
            bar.Value = progress;
        }

        void SetError(string text)
        {
            status.Text = text;
            status.ForeColor = Theme.Red;
            bar.Value = 0;
        }

        // ---- listing --------------------------------------------------------------------

        void ListLocal(string path)
        {
            string p, err;
            List<FileEntry> entries = FileListing.Parse(FileListing.Describe(path), out p, out err);
            if (entries == null) local.ShowError(err); else local.ShowListing(p, entries);
        }

        void ListRemote(string path)
        {
            remoteListReq = nextReq++;
            viewer.Send('L', Proto.Cat(Proto.Ints(remoteListReq), Proto.Text(path)));
        }

        // ---- messages from the remote computer (called on the viewer's receive thread) ----

        public void OnMessage(byte type, byte[] d)
        {
            int req = Proto.Int(d, 0);
            switch ((char)type)
            {
                case 'l':
                {
                    string p, err;
                    List<FileEntry> entries = FileListing.Parse(Proto.Str(Proto.Sub(d, 4, d.Length - 4)), out p, out err);
                    if (req != remoteListReq) return;
                    UI(delegate { if (entries == null) remote.ShowError(err); else remote.ShowListing(p, entries); });
                    break;
                }
                case 'a': // an upload was saved (or failed) on the remote side
                {
                    bool ok = Proto.Int(d, 1) != 0;
                    string msg = Proto.Str(Proto.Sub(d, 8, d.Length - 8));
                    UI(delegate
                    {
                        uploadsPending--;
                        if (!ok) uploadsFailed++;
                        if (uploadsPending > 0) return;
                        if (uploadsFailed > 0) SetError(uploadsFailed + " file(s) couldn't be saved on the remote computer: " + msg);
                        else SetStatus("Sent to the remote computer.", 1);
                        ListRemote(remote.CurrentPath);
                    });
                    break;
                }
                case 'f': StartIncoming(req, d); break;
                case 'd':
                    if (incoming != null)
                    {
                        incoming.Write(d, 12, d.Length - 12);
                        incomingDone += d.Length - 12;
                        long done = incomingDone, size = incomingSize;
                        string name = Path.GetFileName(incomingFinal);
                        UI(delegate { SetStatus("Copying " + name + "...  " + FileListing.Size(done) + " of " + FileListing.Size(size), size > 0 ? done / (float)size : 1); });
                    }
                    break;
                case 'e': CloseIncoming(false); break;
                case 'n':
                {
                    string folder;
                    lock (downloadTo) { downloadTo.TryGetValue(req, out folder); downloadTo.Remove(req); }
                    UI(delegate
                    {
                        SetStatus("Copied to " + folder + ".", 1);
                        if (local.CurrentPath == folder) ListLocal(folder);
                    });
                    break;
                }
                case 'x':
                {
                    CloseIncoming(true);
                    string msg = Proto.Str(Proto.Sub(d, 4, d.Length - 4));
                    lock (downloadTo) downloadTo.Remove(req);
                    UI(delegate { SetError("Couldn't copy from the remote computer: " + msg); ListLocal(local.CurrentPath); });
                    break;
                }
            }
        }

        void StartIncoming(int req, byte[] d)
        {
            CloseIncoming(true);
            string folder;
            lock (downloadTo) if (!downloadTo.TryGetValue(req, out folder)) return;
            long size = Proto.LongAt(d, 4);
            // the name may contain sub-folders (a downloaded folder); keep it inside the target
            string rel = Proto.Str(Proto.Sub(d, 12, d.Length - 12)).Replace('/', '\\');
            List<string> parts = new List<string>();
            foreach (string part in rel.Split('\\'))
            {
                string clean = part;
                foreach (char c in Path.GetInvalidFileNameChars()) clean = clean.Replace(c, '_');
                if (clean.Length > 0 && clean != "." && clean != "..") parts.Add(clean);
            }
            if (parts.Count == 0) return;
            string target = Path.Combine(folder, string.Join("\\", parts.ToArray()));
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            incomingFinal = parts.Count == 1 ? Unique(target) : target;
            incomingTemp = incomingFinal + ".part";
            incoming = new FileStream(incomingTemp, FileMode.Create, FileAccess.Write);
            incomingSize = size;
            incomingDone = 0;
        }

        void CloseIncoming(bool abort)
        {
            FileStream s = incoming;
            incoming = null;
            if (s == null) return;
            try { s.Close(); } catch { }
            try
            {
                if (abort) File.Delete(incomingTemp);
                else
                {
                    if (File.Exists(incomingFinal)) File.Delete(incomingFinal);
                    File.Move(incomingTemp, incomingFinal);
                }
            }
            catch { }
        }

        static string Unique(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path), name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            for (int i = 1; ; i++)
            {
                string p = Path.Combine(dir, name + " (" + i + ")" + ext);
                if (!File.Exists(p)) return p;
            }
        }

        // ---- transfers ------------------------------------------------------------------

        void Download()
        {
            List<FileEntry> sel = remote.Selected();
            if (sel.Count == 0) { SetError("Select files or folders on the remote computer first."); return; }
            if (local.CurrentPath.Length == 0) { SetError("Open a folder on this computer first (for example Desktop) - that's where the files go."); return; }
            foreach (FileEntry e in sel)
            {
                if (e.Kind == 'V' || e.Kind == 'P') { SetError("Open " + e.Name + " and pick the files inside it."); return; }
                int req = nextReq++;
                lock (downloadTo) downloadTo[req] = local.CurrentPath;
                viewer.Send('g', Proto.Cat(Proto.Ints(req), Proto.Text(e.FullPath)));
            }
            SetStatus("Copying from the remote computer...", 0);
        }

        void Upload()
        {
            List<FileEntry> sel = local.Selected();
            if (sel.Count == 0) { SetError("Select files or folders on this computer first."); return; }
            if (remote.CurrentPath.Length == 0) { SetError("Open a folder on the remote computer first - that's where the files go."); return; }
            string target = remote.CurrentPath;

            // every file with the remote folder it goes into (folders keep their structure)
            List<KeyValuePair<string, string>> files = new List<KeyValuePair<string, string>>();
            foreach (FileEntry e in sel)
            {
                if (e.Kind == 'V' || e.Kind == 'P') { SetError("Open " + e.Name + " and pick the files inside it."); return; }
                if (e.Kind == 'F') { files.Add(new KeyValuePair<string, string>(e.FullPath, target)); continue; }
                string root = Path.GetDirectoryName(e.FullPath.TrimEnd('\\'));
                try
                {
                    foreach (string f in Directory.GetFiles(e.FullPath, "*", SearchOption.AllDirectories))
                    {
                        string rel = Path.GetDirectoryName(f).Substring(root.Length).TrimStart('\\');
                        files.Add(new KeyValuePair<string, string>(f, Path.Combine(target, rel)));
                    }
                }
                catch (Exception ex) { SetError("Can't read " + e.Name + ": " + ex.Message); return; }
            }
            if (files.Count == 0) { SetError("There are no files in what you selected."); return; }
            long total = 0;
            foreach (KeyValuePair<string, string> f in files) total += new FileInfo(f.Key).Length;

            uploadsPending += files.Count;
            uploadsFailed = 0;
            btnUpload.Enabled = false;
            long totalBytes = total;
            Thread t = new Thread(delegate()
            {
                long sent = 0;
                DateTime lastUi = DateTime.MinValue;
                try
                {
                    foreach (KeyValuePair<string, string> f in files)
                    {
                        string name = Path.GetFileName(f.Key);
                        long before = sent;
                        viewer.UploadFile(f.Key, f.Value, delegate(long done)
                        {
                            sent = before + done;
                            if ((DateTime.UtcNow - lastUi).TotalMilliseconds < 120) return;
                            lastUi = DateTime.UtcNow;
                            long s = sent;
                            UI(delegate { SetStatus("Sending " + name + "...  " + FileListing.Size(s) + " of " + FileListing.Size(totalBytes), totalBytes > 0 ? s / (float)totalBytes : 1); });
                        });
                    }
                    UI(delegate { SetStatus("Finishing on the remote computer...", 1); });
                }
                catch (Exception ex) { UI(delegate { SetError("Sending stopped: " + ex.Message); }); }
                UI(delegate { btnUpload.Enabled = true; });
            });
            t.IsBackground = true;
            t.Start();
        }
    }
}
