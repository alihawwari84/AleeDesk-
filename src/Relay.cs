using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace RemoteDesk
{
    // Rendezvous relay. Hosts register under their ID and wait; a viewer asking for that ID
    // is paired with the host and the relay then just forwards bytes. All session traffic is
    // end-to-end encrypted between host and viewer, so the relay never sees screen content
    // or the password. Can run standalone (Main) or embedded inside the app (Start/Stop).
    public static class RelayServer
    {
        class Entry { public TcpClient Client; public Channel Ch; }

        static readonly Dictionary<string, Entry> waiting = new Dictionary<string, Entry>();
        static readonly Dictionary<string, Queue<DateTime>> misses = new Dictionary<string, Queue<DateTime>>();
        static readonly object gate = new object();
        static TcpListener listener;

        static void Log(string fmt, params object[] a)
        {
            try { Console.WriteLine(fmt, a); } catch { }
        }

        public static int Main(string[] args)
        {
            int port = 7788;
            if (args.Length > 0) int.TryParse(args[0], out port);
            string err = Start(port);
            if (err != null) { Console.WriteLine("Cannot listen on port {0}: {1}", port, err); return 1; }
            Console.WriteLine("RemoteDesk relay listening on port {0}  (Ctrl+C to stop)", port);
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }

        // Starts accepting connections in the background. Returns null on success or an error message.
        public static string Start(int port)
        {
            TcpListener l;
            lock (gate)
            {
                if (listener != null) return null;
                try { l = new TcpListener(IPAddress.Any, port); l.Start(); }
                catch (Exception ex) { return ex.Message; }
                listener = l;
            }
            Thread acceptor = new Thread(delegate()
            {
                try
                {
                    while (true)
                    {
                        TcpClient c = l.AcceptTcpClient();
                        Thread t = new Thread(delegate() { Handle(c); });
                        t.IsBackground = true;
                        t.Start();
                    }
                }
                catch { }
            });
            acceptor.IsBackground = true;
            acceptor.Start();
            return null;
        }

        public static void Stop()
        {
            lock (gate)
            {
                if (listener != null) { try { listener.Stop(); } catch { } listener = null; }
            }
        }

        static bool ValidId(string id)
        {
            if (id.Length < 4 || id.Length > 16) return false;
            foreach (char ch in id) if (ch < '0' || ch > '9') return false;
            return true;
        }

        static bool Dead(Entry e)
        {
            try { return e.Client.Client.Poll(0, SelectMode.SelectRead) && e.Client.Client.Available == 0; }
            catch { return true; }
        }

        static void Handle(TcpClient c)
        {
            bool keep = false;
            try
            {
                c.NoDelay = true;
                c.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                System.Net.Sockets.NetworkStream s = c.GetStream();
                s.ReadTimeout = 10000;
                s.WriteTimeout = 5000;
                Channel ch = new Channel(s);
                ch.MaxLen = 128;
                byte[] data;
                byte role = ch.Recv(out data);
                string id = Encoding.ASCII.GetString(data);
                string ip = ((IPEndPoint)c.Client.RemoteEndPoint).Address.ToString();
                if (!ValidId(id)) { ch.Send('E', Proto.Text("Bad ID")); return; }
                if (role == 'H') keep = RunHost(c, ch, id);
                else if (role == 'V') keep = Pair(c, ch, id, ip);
            }
            catch { }
            finally { if (!keep) { try { c.Close(); } catch { } } }
        }

        // Returns true when the connection has been handed off to a pairing and must stay open.
        static bool RunHost(TcpClient c, Channel ch, string id)
        {
            Entry me = new Entry { Client = c, Ch = ch };
            lock (gate)
            {
                Entry old;
                if (waiting.TryGetValue(id, out old))
                {
                    if (!Dead(old)) { ch.Send('E', Proto.Text("ID already in use")); return false; }
                    try { old.Client.Close(); } catch { }
                }
                waiting[id] = me;
            }
            ch.Send('O', null);
            Log("{0}  host {1} registered", DateTime.Now.ToString("HH:mm:ss"), id);

            while (true)
            {
                for (int i = 0; i < 10; i++)
                {
                    Thread.Sleep(1000);
                    lock (gate)
                    {
                        Entry cur;
                        if (!waiting.TryGetValue(id, out cur) || cur != me) return true; // paired
                        if (Dead(me)) { waiting.Remove(id); return false; }
                    }
                }
                lock (gate)
                {
                    Entry cur;
                    if (!waiting.TryGetValue(id, out cur) || cur != me) return true;
                    try { ch.Send('K', null); }
                    catch { waiting.Remove(id); return false; }
                }
            }
        }

        static bool TooManyMisses(string ip)
        {
            lock (gate)
            {
                Queue<DateTime> q;
                if (!misses.TryGetValue(ip, out q)) return false;
                while (q.Count > 0 && (DateTime.UtcNow - q.Peek()).TotalSeconds > 60) q.Dequeue();
                return q.Count >= 20;
            }
        }

        static void Miss(string ip)
        {
            lock (gate)
            {
                Queue<DateTime> q;
                if (!misses.TryGetValue(ip, out q)) { q = new Queue<DateTime>(); misses[ip] = q; }
                q.Enqueue(DateTime.UtcNow);
            }
        }

        static bool Pair(TcpClient viewer, Channel vch, string id, string ip)
        {
            if (TooManyMisses(ip)) { vch.Send('E', Proto.Text("Too many attempts, try again later")); return false; }
            Entry host = null;
            lock (gate)
            {
                Entry e;
                if (waiting.TryGetValue(id, out e))
                {
                    waiting.Remove(id);
                    if (!Dead(e))
                    {
                        try { e.Ch.Send('P', Proto.Text(ip)); host = e; }
                        catch { try { e.Client.Close(); } catch { } }
                    }
                    else { try { e.Client.Close(); } catch { } }
                }
            }
            if (host == null)
            {
                Miss(ip);
                vch.Send('E', Proto.Text("Remote ID not found or offline. Check the ID and that both computers use the same relay address."));
                return false;
            }
            vch.Send('O', null);
            Log("{0}  {1} -> host {2} paired", DateTime.Now.ToString("HH:mm:ss"), ip, id);

            NetworkStreamPair(viewer, host.Client);
            return true;
        }

        static void NetworkStreamPair(TcpClient a, TcpClient b)
        {
            System.Net.Sockets.NetworkStream sa = a.GetStream(), sb = b.GetStream();
            sa.ReadTimeout = Timeout.Infinite; sa.WriteTimeout = Timeout.Infinite;
            sb.ReadTimeout = Timeout.Infinite; sb.WriteTimeout = Timeout.Infinite;
            Thread t = new Thread(delegate() { Pump(sa, sb, a, b); });
            t.IsBackground = true;
            t.Start();
            Pump(sb, sa, a, b);
        }

        static void Pump(System.Net.Sockets.NetworkStream from, System.Net.Sockets.NetworkStream to, TcpClient a, TcpClient b)
        {
            byte[] buf = new byte[65536];
            try
            {
                int n;
                while ((n = from.Read(buf, 0, buf.Length)) > 0) to.Write(buf, 0, n);
            }
            catch { }
            try { a.Close(); } catch { }
            try { b.Close(); } catch { }
        }
    }
}
