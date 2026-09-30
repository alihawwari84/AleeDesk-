using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace RemoteDesk
{
    public class LanDevice
    {
        public string Id, Name;
        public IPEndPoint Direct;
        public DateTime Seen;
    }

    // Finds other AleeDesk computers on the local network and lets them connect directly,
    // skipping the internet relay (faster, and works without internet).
    //   discovery: UDP 7789 - "ALDA|id|name|tcpPort|version" announcements, "ALDQ" = who's there?
    //   direct:    TCP 7790 - same opening as the relay ('V'+id -> 'O'), then the normal
    //              password handshake and encrypted session.
    public static class Lan
    {
        public const int DiscoveryPort = 7789, DirectPort = 7790;
        const int AnnounceMs = 4000, ExpireMs = 12000;

        public static volatile bool Discoverable = true;
        static readonly Dictionary<string, LanDevice> seen = new Dictionary<string, LanDevice>();
        static readonly object gate = new object();
        static UdpClient udp;
        static TcpListener listener;
        static volatile bool running;
        static string myId;
        static Action<TcpClient> incoming;

        public static void Start(string id, Action<TcpClient> onIncoming)
        {
            if (running) return;
            myId = id;
            incoming = onIncoming;
            running = true;
            try
            {
                udp = new UdpClient();
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
                udp.EnableBroadcast = true;
                Background(ReceiveLoop);
                Background(AnnounceLoop);
            }
            catch { udp = null; }
            try
            {
                listener = new TcpListener(IPAddress.Any, DirectPort);
                listener.Start();
                Background(AcceptLoop);
            }
            catch { listener = null; }
        }

        public static void Stop()
        {
            running = false;
            try { if (udp != null) udp.Close(); } catch { }
            try { if (listener != null) listener.Stop(); } catch { }
            udp = null;
            listener = null;
        }

        // Devices heard from recently, not counting this computer.
        public static List<LanDevice> Devices()
        {
            List<LanDevice> list = new List<LanDevice>();
            lock (gate)
            {
                List<string> old = new List<string>();
                foreach (KeyValuePair<string, LanDevice> kv in seen)
                    if ((DateTime.UtcNow - kv.Value.Seen).TotalMilliseconds > ExpireMs) old.Add(kv.Key);
                    else list.Add(kv.Value);
                foreach (string k in old) seen.Remove(k);
            }
            list.Sort(delegate(LanDevice a, LanDevice b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            return list;
        }

        public static LanDevice Find(string id)
        {
            foreach (LanDevice d in Devices()) if (d.Id == id) return d;
            return null;
        }

        // Ask everyone on the network to answer now instead of at their next announcement.
        public static void Query() { Broadcast(Encoding.ASCII.GetBytes("ALDQ")); }

        // True when a quick TCP connect to the device succeeds (used before trying direct).
        public static bool Reachable(IPEndPoint ep, int timeoutMs)
        {
            using (TcpClient c = new TcpClient())
            {
                try
                {
                    IAsyncResult ar = c.BeginConnect(ep.Address, ep.Port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
                    c.EndConnect(ar);
                    return true;
                }
                catch { return false; }
            }
        }

        static void Background(ThreadStart work)
        {
            Thread t = new Thread(work);
            t.IsBackground = true;
            t.Start();
        }

        static byte[] Announcement()
        {
            string name = Environment.MachineName.Replace("|", "");
            return Encoding.UTF8.GetBytes("ALDA|" + myId + "|" + name + "|" + DirectPort + "|" + Brand.Version);
        }

        static void ReceiveLoop()
        {
            while (running)
            {
                try
                {
                    IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    string msg = Encoding.UTF8.GetString(udp.Receive(ref from));
                    if (msg == "ALDQ")
                    {
                        if (Discoverable) { byte[] a = Announcement(); udp.Send(a, a.Length, from); }
                        continue;
                    }
                    string[] f = msg.Split('|');
                    int port;
                    if (f.Length < 4 || f[0] != "ALDA" || f[1] == myId || !int.TryParse(f[3], out port)) continue;
                    lock (gate)
                        seen[f[1]] = new LanDevice { Id = f[1], Name = f[2], Direct = new IPEndPoint(from.Address, port), Seen = DateTime.UtcNow };
                }
                catch { if (running) Thread.Sleep(200); }
            }
        }

        static void AnnounceLoop()
        {
            while (running)
            {
                if (Discoverable) Broadcast(Announcement());
                for (int i = 0; i < AnnounceMs / 200 && running; i++) Thread.Sleep(200);
            }
        }

        static void Broadcast(byte[] data)
        {
            UdpClient u = udp;
            if (u == null) return;
            foreach (IPAddress target in BroadcastAddresses())
            {
                try { u.Send(data, data.Length, new IPEndPoint(target, DiscoveryPort)); } catch { }
            }
        }

        // 255.255.255.255 plus each network card's own broadcast address (some routers only
        // pass the latter).
        static List<IPAddress> BroadcastAddresses()
        {
            List<IPAddress> list = new List<IPAddress> { IPAddress.Broadcast };
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                        byte[] ip = ua.Address.GetAddressBytes(), mask = ua.IPv4Mask.GetAddressBytes(), b = new byte[4];
                        for (int i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~mask[i]);
                        IPAddress bc = new IPAddress(b);
                        if (!list.Contains(bc)) list.Add(bc);
                    }
                }
            }
            catch { }
            return list;
        }

        static void AcceptLoop()
        {
            while (running)
            {
                try
                {
                    TcpClient c = listener.AcceptTcpClient();
                    if (!Discoverable || incoming == null) { c.Close(); continue; }
                    incoming(c);
                }
                catch { if (running) Thread.Sleep(200); }
            }
        }
    }
}
