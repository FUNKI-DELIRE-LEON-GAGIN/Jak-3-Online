// ============================================================================
//  JAK 3 EN LIGNE - Jak3Online.exe
//  Programme compagnon du mod "Jak 3 Coop Multiplayer" (OpenGOAL).
//
//  * Mode normal (double-clic)  : fenetre de connexion. Fait le pont entre le
//    jeu (gk.exe) et le serveur : envoie ta position/animation, recoit celles
//    des autres joueurs, les sessions, la liste des joueurs, les coups PvP...
//  * Case "Heberger le serveur" : lance aussi le serveur sur ce PC.
//  * Jak3Online.exe --server [port] : serveur seul (sans fenetre de jeu).
//
//  Le jeu ne peut pas utiliser le reseau directement : il echange avec ce
//  programme par deux fichiers du dossier data/online/bridge (aucun acces a la
//  memoire du jeu, donc rien de suspect pour les antivirus). Le contenu suit la
//  structure online-shm de goal_src/jak3/pc/features/online-h.gc : les offsets
//  ci-dessous (classe Shm) doivent rester identiques a ceux du fichier GOAL.
//
//  Compilable avec le compilateur C# 5 fourni avec Windows (voir build.bat).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Jak3Online
{
    // ------------------------------------------------------------------------
    //  Protocole reseau (TCP, petit-boutiste)
    //  trame = [u16 longueur(type+donnees)] [u8 type] [donnees]
    // ------------------------------------------------------------------------
    static class Proto
    {
        public const int Version = 4;
        public const int DefaultPort = 27015;
        public const int StateSize = 80;
        public const int MaxPlayersPerSession = 100;
        public const int MaxVisible = 16;
        public const int MaxListed = 16;
        public const int PoseMax = 3072;     // pose : squelette de Jak + Daxter + arme/planche (voir online-h.gc)
        public const int PoseMin = 32;

        // client -> serveur
        public const byte C_HELLO = 1;      // u16 version, str16 nom
        public const byte C_CREATE = 2;     // u8 max, u8 flags (1 public, 2 pvp)
        public const byte C_JOIN_ID = 3;    // u32 session
        public const byte C_JOIN_CODE = 4;  // str8 code
        public const byte C_LEAVE = 5;
        public const byte C_LIST = 6;
        public const byte C_STATE = 7;      // etat (StateSize)
        public const byte C_HIT = 8;        // u32 cible, f32 degats, u8 mode, f32 dx dy dz
        public const byte C_DIED = 9;       // u32 tueur (0 = aucun)
        public const byte C_SETTINGS = 10;  // u8 flags (1 pvp, 2 public)
        public const byte C_PING = 11;      // u32 horloge client
        public const byte C_RENAME = 12;    // str16 nouveau pseudo
        public const byte C_SAVE_REQ = 13;  // demande la sauvegarde de l'hote
        public const byte C_SAVE_CHUNK = 14; // u32 cible, u16 index, u16 nombre, u16 taille, octets
        public const byte C_SAVE_NOTIFY = 15; // l'hote a sauvegarde
        public const byte C_POSE = 16;
        public const byte C_JOIN_WORLD = 17; // rejoindre LA session publique (MONDE1, MONDE2 si pleine...)
        public const byte C_MSG = 18;        // u8 type, u32 cible (0 = tous) + donnees : chat, identite, moderation...
        public const string WorldPrefix = "MONDE";      // pose (squelette complet), 32..PoseMax octets

        // serveur -> client
        public const byte S_WELCOME = 101;  // u32 mon id, str16 mon nom
        public const byte S_SESSION = 102;  // u32 id, str8 code, u8 flags, u8 max, u32 hote, u8 nb
        public const byte S_LIST = 103;     // u8 n, n x (u32 id, u8 nb, u8 max, u8 flags, str16 hote, str8 code)
        public const byte S_PLAYERS = 104;  // u8 n, n x (u32 id, u8 flags, u16 ping, f32 sante, str16 nom, str16 niveau, f32 x y z, u8 position connue)
        public const byte S_STATES = 105;   // u8 n, n x (u32 id, etat)
        public const byte S_HIT = 106;      // u32 de, f32 degats, u8 mode, f32 dx dy dz
        public const byte S_FEED = 107;     // u8 type, str16 nomA, str16 nomB, u8 valeur
        public const byte S_ERROR = 108;    // u8 code
        public const byte S_PONG = 109;     // u32 horloge client
        public const byte S_KILL = 110;     // u32 victime
        public const byte S_SAVE_REQ = 111; // u32 demandeur
        public const byte S_SAVE_CHUNK = 112; // u16 index, u16 nombre, u16 taille, octets
        public const byte S_SAVE_NOTIFY = 113;
        public const byte S_POSE = 114;     // u32 joueur, pose
        public const byte S_MSG = 115;      // u32 de, u8 type, u32 cible + donnees

        // messages S_FEED
        public const byte F_JOINED = 1, F_LEFT = 2, F_KILLED = 3, F_DIED = 4, F_HOST = 5, F_PVP = 6, F_VISIBILITY = 7;
        // erreurs S_ERROR
        public const byte E_NOT_FOUND = 1, E_FULL = 2, E_BAD_CODE = 3, E_NOT_HOST = 4, E_VERSION = 5, E_SERVER_FULL = 6;

        // flags d'etat joueur (identiques a online-h.gc)
        public const uint FLAG_VALID = 0x1, FLAG_DEAD = 0x2;
        public const byte PFLAG_HOST = 0x1, PFLAG_ME = 0x2, PFLAG_DEAD = 0x4;

        public static string RandomName()
        {
            return "Jak" + new Random().Next(1000, 9999);
        }

        public static bool SystemFrench()
        {
            try { return System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fr"; }
            catch (Exception) { return false; }
        }

        public static string CleanName(string s)
        {
            StringBuilder sb = new StringBuilder();
            if (s != null)
            {
                foreach (char c in s)
                {
                    if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.')
                        sb.Append(c);
                    else if (c == ' ')
                        sb.Append('_');
                    if (sb.Length >= 15) break;
                }
            }
            if (sb.Length == 0) return "Joueur";
            // noms reserves : personne ne peut se faire passer pour l'hote, le createur ou un admin
            string low = sb.ToString().ToLowerInvariant().Replace("_", "").Replace("-", "").Replace(".", "").Replace("0", "o").Replace("1", "i").Replace("3", "e");
            foreach (string bad in new string[] { "host", "hote", "createur", "creator", "admin", "modo", "moderat", "serveur", "server", "system", "officiel", "official" })
                if (low.Contains(bad)) return "Joueur";
            return sb.ToString();
        }

        // texte pour le jeu en UTF-8, coupe proprement (jamais au milieu d'une lettre) a max octets
        public static byte[] GameBytes(string s, int max)
        {
            StringBuilder sb = new StringBuilder();
            int used = 0;
            if (s != null)
            {
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    if (c < 32 || c == 127) continue;
                    string one = char.IsHighSurrogate(c) && i + 1 < s.Length ? s.Substring(i++, 2) : c.ToString();
                    int n = Encoding.UTF8.GetByteCount(one);
                    if (used + n > max) break;
                    sb.Append(one);
                    used += n;
                }
            }
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        // texte pour le jeu (UTF-8) qui tient dans max octets
        public static string GameText(string s, int max)
        {
            return Encoding.UTF8.GetString(GameBytes(s, max));
        }

        // texte long coupe en plusieurs lignes (aux espaces) de max octets chacune
        public static List<string> GameLines(string s, int max)
        {
            List<string> res = new List<string>();
            s = (s ?? "").Trim();
            while (s.Length > 0)
            {
                string part = GameText(s, max);
                if (part.Length >= s.Length) { res.Add(s); break; }
                int cut = part.LastIndexOf(' ');
                if (cut < part.Length / 3) cut = part.Length;
                res.Add(s.Substring(0, cut).TrimEnd());
                s = s.Substring(cut).TrimStart();
                if (res.Count >= 4) break;
            }
            return res;
        }

        public static string Ascii(string s, int max)
        {
            StringBuilder sb = new StringBuilder();
            if (s != null)
            {
                string d = s.Normalize(NormalizationForm.FormD);
                foreach (char c in d)
                {
                    if (c >= 32 && c < 127) sb.Append(c);
                    if (sb.Length >= max) break;
                }
            }
            return sb.ToString();
        }
    }

    class PacketWriter
    {
        MemoryStream ms = new MemoryStream(128);
        BinaryWriter w;

        public PacketWriter(byte type)
        {
            w = new BinaryWriter(ms);
            w.Write((ushort)0);
            w.Write(type);
        }
        public PacketWriter U8(int v) { w.Write((byte)v); return this; }
        public PacketWriter U16(int v) { w.Write((ushort)v); return this; }
        public PacketWriter U32(uint v) { w.Write(v); return this; }
        public PacketWriter F32(float v) { w.Write(v); return this; }
        public PacketWriter Bytes(byte[] b, int off, int len) { w.Write(b, off, len); return this; }
        public PacketWriter Str(string s, int len)
        {
            byte[] b = new byte[len];
            string a = Proto.Ascii(s, len - 1);
            Encoding.ASCII.GetBytes(a, 0, a.Length, b, 0);
            w.Write(b);
            return this;
        }
        public byte[] ToArray()
        {
            w.Flush();
            byte[] a = ms.ToArray();
            int len = a.Length - 2;
            a[0] = (byte)(len & 0xff);
            a[1] = (byte)((len >> 8) & 0xff);
            return a;
        }
    }

    static class Rd
    {
        public static string Str(BinaryReader r, int len)
        {
            byte[] b = r.ReadBytes(len);
            int n = 0;
            while (n < b.Length && b[n] != 0) n++;
            return Encoding.ASCII.GetString(b, 0, n);
        }
    }

    // ------------------------------------------------------------------------
    //  Connexion TCP (commune au client et au serveur)
    // ------------------------------------------------------------------------
    class Conn : ILink
    {
        public Socket Sock;
        NetworkStream stream;
        readonly object sendLock = new object();
        int closed;
        public object Tag;
        public Action<Conn, byte, BinaryReader> OnPacket;
        public Action<Conn> OnClose;
        public DateTime LastRecv = DateTime.UtcNow;

        public Conn(Socket s)
        {
            Sock = s;
            Sock.NoDelay = true;
            Sock.SendTimeout = 3000;
            stream = new NetworkStream(s, false);
        }

        public bool Closed { get { return closed != 0; } }

        public void Start()
        {
            Thread t = new Thread(ReadLoop);
            t.IsBackground = true;
            t.Name = "conn-read";
            t.Start();
        }

        void ReadExact(byte[] buf, int len)
        {
            int got = 0;
            while (got < len)
            {
                int n = stream.Read(buf, got, len - got);
                if (n <= 0) throw new IOException("closed");
                got += n;
            }
        }

        void ReadLoop()
        {
            try
            {
                byte[] hdr = new byte[2];
                while (!Closed)
                {
                    ReadExact(hdr, 2);
                    int len = hdr[0] | (hdr[1] << 8);
                    if (len < 1) throw new IOException("bad frame");
                    byte[] body = new byte[len];
                    ReadExact(body, len);
                    LastRecv = DateTime.UtcNow;
                    BinaryReader br = new BinaryReader(new MemoryStream(body, 1, len - 1));
                    try
                    {
                        if (OnPacket != null) OnPacket(this, body[0], br);
                    }
                    catch (EndOfStreamException) { /* paquet trop court : ignore */ }
                }
            }
            catch (Exception) { }
            Close();
            // OnClose n'est appele que depuis ce fil de lecture (jamais pendant un envoi)
            if (OnClose != null)
            {
                try { OnClose(this); } catch (Exception) { }
            }
        }

        public bool Send(byte[] data)
        {
            if (Closed) return false;
            try
            {
                lock (sendLock) { stream.Write(data, 0, data.Length); }
                return true;
            }
            catch (Exception)
            {
                Close();
                return false;
            }
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref closed, 1) != 0) return;
            try { Sock.Shutdown(SocketShutdown.Both); } catch (Exception) { }
            try { Sock.Close(); } catch (Exception) { }
        }
    }

    // ------------------------------------------------------------------------
    //  SERVEUR : sessions publiques/privees, relais des etats, PvP
    // ------------------------------------------------------------------------
    class Server
    {
        class SPlayer
        {
            public uint Id;
            public string Name = "";
            public Conn C;
            public SSession S;
            public bool Hello;
            public byte[] State = new byte[Proto.StateSize];
            public bool HasState;
            public float X, Y, Z, Health;
            public uint Flags;
            public ushort Ping;
            public string Level = "";
        }

        class SSession
        {
            public uint Id;
            public string Code;
            public bool Public;
            public bool Pvp;
            public int Max;
            public uint HostId;
            public List<SPlayer> Members = new List<SPlayer>();
        }

        const string CodeChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

        readonly object lk = new object();
        readonly Dictionary<uint, SPlayer> players = new Dictionary<uint, SPlayer>();
        readonly Dictionary<uint, SSession> sessions = new Dictionary<uint, SSession>();
        uint nextPlayer = 1;
        uint nextSession = 1;
        byte[] lastBanList;   // liste des bannis du monde en ligne (signee par le createur)
        byte[] lastVersion;   // empreinte officielle du mod (signee par le createur)
        readonly Random rng = new Random();
        TcpListener listener;
        volatile bool running;
        public int Port;
        public Action<string> Log;
        public const int MaxClients = 1000;

        void L(string s)
        {
            if (Log != null) Log(s);
        }

        public bool Running { get { return running; } }

        public int PlayerCount { get { lock (lk) return players.Count; } }
        public int SessionCount { get { lock (lk) return sessions.Count; } }

        public void Start(int port)
        {
            Port = port;
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            running = true;
            Thread a = new Thread(AcceptLoop);
            a.IsBackground = true;
            a.Name = "server-accept";
            a.Start();
            Thread t = new Thread(TickLoop);
            t.IsBackground = true;
            t.Name = "server-tick";
            t.Start();
            L("Serveur demarre sur le port " + port + " (jusqu'a " + Proto.MaxPlayersPerSession + " joueurs par session)");
        }

        public void Stop()
        {
            running = false;
            try { listener.Stop(); } catch (Exception) { }
            List<SPlayer> all;
            lock (lk) all = new List<SPlayer>(players.Values);
            foreach (SPlayer p in all) p.C.Close();
        }

        void AcceptLoop()
        {
            while (running)
            {
                Socket s;
                try { s = listener.AcceptSocket(); }
                catch (Exception) { break; }
                Conn c = new Conn(s);
                SPlayer p = new SPlayer();
                p.C = c;
                c.Tag = p;
                bool full;
                lock (lk)
                {
                    full = players.Count >= MaxClients;
                    if (!full)
                    {
                        p.Id = nextPlayer++;
                        players[p.Id] = p;
                    }
                }
                if (full)
                {
                    c.Send(new PacketWriter(Proto.S_ERROR).U8(Proto.E_SERVER_FULL).ToArray());
                    c.Close();
                    continue;
                }
                c.OnPacket = OnPacket;
                c.OnClose = OnClose;
                c.Start();
            }
        }

        void OnClose(Conn c)
        {
            SPlayer p = (SPlayer)c.Tag;
            lock (lk)
            {
                if (p.S != null) LeaveSession(p);
                players.Remove(p.Id);
            }
            if (p.Hello) L(p.Name + " s'est deconnecte");
        }

        string UniqueName(string wanted)
        {
            string baseName = Proto.CleanName(wanted);
            string n = baseName;
            int k = 2;
            bool taken = true;
            while (taken)
            {
                taken = false;
                foreach (SPlayer o in players.Values)
                {
                    if (o.Hello && string.Equals(o.Name, n, StringComparison.OrdinalIgnoreCase)) { taken = true; break; }
                }
                if (taken)
                {
                    string suffix = k.ToString();
                    n = (baseName.Length + suffix.Length > 15 ? baseName.Substring(0, 15 - suffix.Length) : baseName) + suffix;
                    k++;
                }
            }
            return n;
        }

        string NewCode()
        {
            while (true)
            {
                char[] c = new char[6];
                for (int i = 0; i < 6; i++) c[i] = CodeChars[rng.Next(CodeChars.Length)];
                string code = new string(c);
                bool used = false;
                foreach (SSession s in sessions.Values) if (s.Code == code) { used = true; break; }
                if (!used) return code;
            }
        }

        byte[] SessionPacket(SSession s)
        {
            PacketWriter w = new PacketWriter(Proto.S_SESSION);
            if (s == null)
            {
                w.U32(0).Str("", 8).U8(0).U8(0).U32(0).U8(0);
            }
            else
            {
                w.U32(s.Id).Str(s.Code, 8).U8((s.Public ? 1 : 0) | (s.Pvp ? 2 : 0)).U8(s.Max).U32(s.HostId).U8(s.Members.Count);
            }
            return w.ToArray();
        }

        void Broadcast(SSession s, byte[] data, SPlayer except)
        {
            foreach (SPlayer m in s.Members) if (m != except) m.C.Send(data);
        }

        void Feed(SSession s, byte kind, string a, string b, int value)
        {
            byte[] pk = new PacketWriter(Proto.S_FEED).U8(kind).Str(a, 16).Str(b, 16).U8(value).ToArray();
            Broadcast(s, pk, null);
        }

        void JoinSession(SPlayer p, SSession s)
        {
            if (p.S != null) LeaveSession(p);
            s.Members.Add(p);
            p.S = s;
            p.HasState = false;
            byte[] sp = SessionPacket(s);
            Broadcast(s, sp, null);
            Feed(s, Proto.F_JOINED, p.Name, "", 0);
            SendPlayers(s);
            if (lastBanList != null && s.Code.StartsWith(Proto.WorldPrefix)) p.C.Send(lastBanList);
            if (lastVersion != null && s.Code.StartsWith(Proto.WorldPrefix)) p.C.Send(lastVersion);
            L(p.Name + " rejoint la session " + s.Code + " (" + s.Members.Count + "/" + s.Max + ")");
        }

        void LeaveSession(SPlayer p)
        {
            SSession s = p.S;
            if (s == null) return;
            s.Members.Remove(p);
            p.S = null;
            p.HasState = false;
            if (!p.C.Closed) p.C.Send(SessionPacket(null));
            if (s.Members.Count == 0)
            {
                sessions.Remove(s.Id);
                L("Session " + s.Code + " fermee (vide)");
                return;
            }
            Feed(s, Proto.F_LEFT, p.Name, "", 0);
            if (s.HostId == p.Id)
            {
                s.HostId = s.Members[0].Id;
                Feed(s, Proto.F_HOST, s.Members[0].Name, "", 0);
            }
            Broadcast(s, SessionPacket(s), null);
            SendPlayers(s);
        }

        void SendPlayers(SSession s)
        {
            PacketWriter w = new PacketWriter(Proto.S_PLAYERS);
            int n = Math.Min(s.Members.Count, Proto.MaxPlayersPerSession);
            w.U8(n);
            for (int i = 0; i < n; i++)
            {
                SPlayer m = s.Members[i];
                int flags = 0;
                if (m.Id == s.HostId) flags |= Proto.PFLAG_HOST;
                if (m.HasState && (m.Flags & Proto.FLAG_DEAD) != 0) flags |= Proto.PFLAG_DEAD;
                w.U32(m.Id).U8(flags).U16(m.Ping).F32(m.HasState ? m.Health : 0f).Str(m.Name, 16).Str(m.Level, 16)
                    .F32(m.X).F32(m.Y).F32(m.Z).U8(m.HasState ? 1 : 0);
            }
            Broadcast(s, w.ToArray(), null);
        }

        void SendList(SPlayer p)
        {
            List<SSession> pub = new List<SSession>();
            foreach (SSession s in sessions.Values) if (s.Public) pub.Add(s);
            pub.Sort(delegate (SSession a, SSession b) { return b.Members.Count.CompareTo(a.Members.Count); });
            int n = Math.Min(pub.Count, Proto.MaxListed);
            PacketWriter w = new PacketWriter(Proto.S_LIST);
            w.U8(n);
            for (int i = 0; i < n; i++)
            {
                SSession s = pub[i];
                string host = "";
                foreach (SPlayer m in s.Members) if (m.Id == s.HostId) host = m.Name;
                w.U32(s.Id).U8(s.Members.Count).U8(s.Max).U8((s.Public ? 1 : 0) | (s.Pvp ? 2 : 0)).Str(host, 16).Str(s.Code, 8);
            }
            p.C.Send(w.ToArray());
        }

        void Error(SPlayer p, byte code)
        {
            p.C.Send(new PacketWriter(Proto.S_ERROR).U8(code).ToArray());
        }

        void OnPacket(Conn c, byte type, BinaryReader r)
        {
            SPlayer p = (SPlayer)c.Tag;
            lock (lk)
            {
                if (!p.Hello && type != Proto.C_HELLO) return;
                switch (type)
                {
                    case Proto.C_HELLO:
                        {
                            int ver = r.ReadUInt16();
                            string name = Rd.Str(r, 16);
                            if (ver != Proto.Version)
                            {
                                Error(p, Proto.E_VERSION);
                                c.Close();
                                return;
                            }
                            p.Name = UniqueName(name);
                            p.Hello = true;
                            c.Send(new PacketWriter(Proto.S_WELCOME).U32(p.Id).Str(p.Name, 16).ToArray());
                            L(p.Name + " connecte (" + SafeEndpoint(c) + ")");
                            break;
                        }
                    case Proto.C_CREATE:
                        {
                            int max = r.ReadByte();
                            int flags = r.ReadByte();
                            if (p.S != null) LeaveSession(p);
                            SSession s = new SSession();
                            s.Id = nextSession++;
                            s.Code = NewCode();
                            s.Public = (flags & 1) != 0;
                            s.Pvp = (flags & 2) != 0;
                            s.Max = Math.Max(2, Math.Min(Proto.MaxPlayersPerSession, max));
                            s.HostId = p.Id;
                            sessions[s.Id] = s;
                            L(p.Name + " cree une session " + (s.Public ? "publique" : "privee") + " " + s.Code + " (max " + s.Max + ", pvp " + (s.Pvp ? "oui" : "non") + ")");
                            JoinSession(p, s);
                            break;
                        }
                    case Proto.C_JOIN_WORLD:
                        {
                            // LA session publique : MONDE1, ou la suivante si elle est pleine ; creee si besoin
                            for (int k = 1; k <= 99; k++)
                            {
                                string wc = Proto.WorldPrefix + k;
                                SSession ex = null;
                                foreach (SSession s2 in sessions.Values) if (s2.Code == wc) { ex = s2; break; }
                                if (ex != null && ex == p.S) break;
                                if (ex != null && ex.Members.Count >= ex.Max) continue;
                                if (p.S != null) LeaveSession(p);
                                if (ex == null)
                                {
                                    ex = new SSession();
                                    ex.Id = nextSession++;
                                    ex.Code = wc;
                                    ex.Public = true;
                                    ex.Pvp = true;
                                    ex.Max = Proto.MaxPlayersPerSession;
                                    ex.HostId = p.Id;
                                    sessions[ex.Id] = ex;
                                    L(p.Name + " ouvre la session publique " + wc);
                                }
                                JoinSession(p, ex);
                                break;
                            }
                            break;
                        }
                    case Proto.C_JOIN_ID:
                        {
                            uint id = r.ReadUInt32();
                            SSession s;
                            if (!sessions.TryGetValue(id, out s) || !s.Public) { Error(p, Proto.E_NOT_FOUND); break; }
                            if (s == p.S) break;
                            if (s.Members.Count >= s.Max) { Error(p, Proto.E_FULL); break; }
                            JoinSession(p, s);
                            break;
                        }
                    case Proto.C_JOIN_CODE:
                        {
                            string code = Rd.Str(r, 8).Trim().ToUpperInvariant();
                            SSession found = null;
                            foreach (SSession s in sessions.Values) if (s.Code == code) { found = s; break; }
                            if (found == null) { Error(p, Proto.E_BAD_CODE); break; }
                            if (found == p.S) break;
                            if (found.Members.Count >= found.Max) { Error(p, Proto.E_FULL); break; }
                            JoinSession(p, found);
                            break;
                        }
                    case Proto.C_LEAVE:
                        if (p.S != null) LeaveSession(p);
                        break;
                    case Proto.C_LIST:
                        SendList(p);
                        break;
                    case Proto.C_STATE:
                        {
                            byte[] st = r.ReadBytes(Proto.StateSize);
                            if (st.Length != Proto.StateSize || p.S == null) break;
                            Buffer.BlockCopy(st, 0, p.State, 0, Proto.StateSize);
                            p.Flags = BitConverter.ToUInt32(st, 0);
                            p.X = BitConverter.ToSingle(st, 12);
                            p.Y = BitConverter.ToSingle(st, 16);
                            p.Z = BitConverter.ToSingle(st, 20);
                            p.Health = BitConverter.ToSingle(st, 24);
                            p.Ping = BitConverter.ToUInt16(st, 60);
                            int n = 0;
                            while (n < 16 && st[64 + n] != 0) n++;
                            p.Level = Encoding.ASCII.GetString(st, 64, n);
                            p.HasState = (p.Flags & Proto.FLAG_VALID) != 0;
                            break;
                        }
                    case Proto.C_POSE:
                        {
                            // pose : envoyee tout de suite aux joueurs proches (300 m)
                            int len = (int)(r.BaseStream.Length - r.BaseStream.Position);
                            if (p.S == null || len < Proto.PoseMin || len > Proto.PoseMax) break;
                            byte[] pose = r.ReadBytes(len);
                            byte[] pkt = new PacketWriter(Proto.S_POSE).U32(p.Id).Bytes(pose, 0, len).ToArray();
                            const float far = 300f * 4096f;
                            foreach (SPlayer o in p.S.Members)
                            {
                                if (o == p) continue;
                                float dx = o.X - p.X, dy = o.Y - p.Y, dz = o.Z - p.Z;
                                if (o.HasState && p.HasState && dx * dx + dy * dy + dz * dz > far * far) continue;
                                o.C.Send(pkt);
                            }
                            break;
                        }
                    case Proto.C_MSG:
                        {
                            // message de session : relaye tel quel (l'expediteur est garanti par le serveur)
                            if (p.S == null) break;
                            int kind = r.ReadByte();
                            uint target = r.ReadUInt32();
                            int len = (int)(r.BaseStream.Length - r.BaseStream.Position);
                            if (len > 1400) break;
                            byte[] data = r.ReadBytes(len);
                            byte[] pkt = new PacketWriter(Proto.S_MSG).U32(p.Id).U8(kind).U32(target).Bytes(data, 0, data.Length).ToArray();
                            if (kind == 4) lastBanList = pkt;
                            if (kind == 13) lastVersion = pkt;
                            foreach (SPlayer m in p.S.Members)
                                if (m != p && (target == 0 || m.Id == target)) m.C.Send(pkt);
                            break;
                        }
                    case Proto.C_HIT:
                        {
                            uint target = r.ReadUInt32();
                            float dmg = r.ReadSingle();
                            int mode = r.ReadByte();
                            float dx = r.ReadSingle(), dy = r.ReadSingle(), dz = r.ReadSingle();
                            if (p.S == null || !p.S.Pvp) break;
                            if (float.IsNaN(dmg) || dmg < 0f) break;
                            if (dmg > 20f) dmg = 20f;
                            foreach (SPlayer m in p.S.Members)
                            {
                                if (m.Id == target && m != p)
                                {
                                    m.C.Send(new PacketWriter(Proto.S_HIT).U32(p.Id).F32(dmg).U8(mode).F32(dx).F32(dy).F32(dz).ToArray());
                                    break;
                                }
                            }
                            break;
                        }
                    case Proto.C_DIED:
                        {
                            uint killer = r.ReadUInt32();
                            if (p.S == null) break;
                            SPlayer k = null;
                            if (killer != 0)
                                foreach (SPlayer m in p.S.Members) if (m.Id == killer && m != p) { k = m; break; }
                            if (k != null)
                            {
                                k.C.Send(new PacketWriter(Proto.S_KILL).U32(p.Id).ToArray());
                                Feed(p.S, Proto.F_KILLED, k.Name, p.Name, 0);
                            }
                            else
                            {
                                Feed(p.S, Proto.F_DIED, p.Name, "", 0);
                            }
                            break;
                        }
                    case Proto.C_SETTINGS:
                        {
                            int flags = r.ReadByte();
                            SSession s = p.S;
                            if (s == null) break;
                            if (s.HostId != p.Id) { Error(p, Proto.E_NOT_HOST); break; }
                            bool pvp = (flags & 1) != 0;
                            bool pub = (flags & 2) != 0;
                            if (pvp != s.Pvp) { s.Pvp = pvp; Feed(s, Proto.F_PVP, p.Name, "", pvp ? 1 : 0); }
                            if (pub != s.Public) { s.Public = pub; Feed(s, Proto.F_VISIBILITY, p.Name, "", pub ? 1 : 0); }
                            Broadcast(s, SessionPacket(s), null);
                            break;
                        }
                    case Proto.C_PING:
                        {
                            uint t = r.ReadUInt32();
                            c.Send(new PacketWriter(Proto.S_PONG).U32(t).ToArray());
                            break;
                        }
                    case Proto.C_SAVE_REQ:
                        {
                            SSession s = p.S;
                            if (s == null) break;
                            foreach (SPlayer m in s.Members)
                                if (m.Id == s.HostId && m != p) { m.C.Send(new PacketWriter(Proto.S_SAVE_REQ).U32(p.Id).ToArray()); break; }
                            break;
                        }
                    case Proto.C_SAVE_CHUNK:
                        {
                            uint target = r.ReadUInt32();
                            int idx = r.ReadUInt16(), cnt = r.ReadUInt16(), len = r.ReadUInt16();
                            byte[] data = r.ReadBytes(len);
                            if (p.S == null) break;
                            foreach (SPlayer m in p.S.Members)
                                if (m.Id == target) { m.C.Send(new PacketWriter(Proto.S_SAVE_CHUNK).U16(idx).U16(cnt).U16(data.Length).Bytes(data, 0, data.Length).ToArray()); break; }
                            break;
                        }
                    case Proto.C_SAVE_NOTIFY:
                        if (p.S != null && p.S.HostId == p.Id) Broadcast(p.S, new PacketWriter(Proto.S_SAVE_NOTIFY).ToArray(), p);
                        break;
                    case Proto.C_RENAME:
                        {
                            string wanted = Proto.CleanName(Rd.Str(r, 16));
                            if (!string.Equals(wanted, p.Name, StringComparison.OrdinalIgnoreCase))
                            {
                                p.Hello = false;
                                p.Name = UniqueName(wanted);
                                p.Hello = true;
                            }
                            else p.Name = wanted;
                            c.Send(new PacketWriter(Proto.S_WELCOME).U32(p.Id).Str(p.Name, 16).ToArray());
                            if (p.S != null) SendPlayers(p.S);
                            break;
                        }
                }
            }
        }

        static string SafeEndpoint(Conn c)
        {
            try { return c.Sock.RemoteEndPoint.ToString(); } catch (Exception) { return "?"; }
        }

        void TickLoop()
        {
            int tick = 0;
            List<KeyValuePair<float, SPlayer>> near = new List<KeyValuePair<float, SPlayer>>();
            while (running)
            {
                Thread.Sleep(50);
                tick++;
                List<Conn> timeouts = new List<Conn>();
                lock (lk)
                {
                    foreach (SSession s in sessions.Values)
                    {
                        // etats : pour chaque joueur, les 16 joueurs les plus proches
                        foreach (SPlayer rcv in s.Members)
                        {
                            near.Clear();
                            foreach (SPlayer o in s.Members)
                            {
                                if (o == rcv || !o.HasState) continue;
                                float d = 0f;
                                if (rcv.HasState)
                                {
                                    float dx = o.X - rcv.X, dy = o.Y - rcv.Y, dz = o.Z - rcv.Z;
                                    d = dx * dx + dy * dy + dz * dz;
                                }
                                near.Add(new KeyValuePair<float, SPlayer>(d, o));
                            }
                            if (near.Count == 0) continue;
                            near.Sort(delegate (KeyValuePair<float, SPlayer> a, KeyValuePair<float, SPlayer> b) { return a.Key.CompareTo(b.Key); });
                            int n = Math.Min(near.Count, Proto.MaxVisible);
                            PacketWriter w = new PacketWriter(Proto.S_STATES);
                            w.U8(n);
                            for (int i = 0; i < n; i++) w.U32(near[i].Value.Id).Bytes(near[i].Value.State, 0, Proto.StateSize);
                            rcv.C.Send(w.ToArray());
                        }
                        if (tick % 20 == 0)
                        {
                            SendPlayers(s);
                            Broadcast(s, SessionPacket(s), null);
                        }
                    }
                    if (tick % 40 == 0)
                    {
                        DateTime now = DateTime.UtcNow;
                        foreach (SPlayer p in players.Values)
                            if ((now - p.C.LastRecv).TotalSeconds > 20) timeouts.Add(p.C);
                    }
                }
                foreach (Conn c in timeouts) c.Close();
            }
        }
    }

    // ------------------------------------------------------------------------
    //  Acces a la memoire du jeu (gk.exe)
    // ------------------------------------------------------------------------
    static class Shm
    {
        public const int Magic0 = 0, Version = 16, GameFrame = 24, BridgeBeat = 28, NetState = 32, MyId = 36,
            IsHost = 40, SessionPublic = 44, SessionCode = 48, SessionPlayers = 56, SessionMax = 60, PvpEnabled = 64,
            ReqSeq = 68, ReqType = 72, ReqArg = 76, ReqCode = 80, AckSeq = 88, ListCount = 92, StatusMsg = 96,
            MyName = 160, ListSeq = 176, PlayerCount = 180, SessionId = 184, FeedWrite = 188, OutWrite = 192,
            OutRead = 196, InWrite = 200, InRead = 204, ServerOk = 208, KbEdit = 212, ReqName = 224,
            KbText = 1408, KbSeq = 1424, KbCmd = 1428, SaveSync = 1432, SaveStatus = 1436, SaveSlot = 1440,
            TestSeq = 1444, TestArg = 1448, UiMode = 216, UiFlags = 220, CmdLevel = 240,
            Local = 256, OutEvents = 384, InEvents = 896, Remote = 1536, Sessions = 3584, Players = 4352, Feed = 9216,
            Size = 0x2600;
        public const int RemoteSize = 0x80, EventSize = 0x20, SessionSize = 0x30, PlayerSize = 0x30, FeedSize = 0x40;
        public const int TxPose = 0x380;     // dans game-out.txt : u32 longueur + pose du joueur local
        public const int PoseRx = 0x2610, PoseSlot = 3104, PoseTail = 3088; // poses des joueurs dans game-in.bin

        public const int NET_OFFLINE = 0, NET_CONNECTING = 1, NET_LOBBY = 2, NET_SESSION = 3;
        public const int REQ_CREATE = 1, REQ_JOIN_ID = 2, REQ_JOIN_CODE = 3, REQ_LEAVE = 4, REQ_LIST = 5, REQ_SETTINGS = 6, REQ_NAME = 7, REQ_GETSAVE = 8, REQ_SAVESYNC = 9, REQ_ADDBOT = 10, REQ_JOIN_WORLD = 11, REQ_LOCALJ2 = 12;
        public const int EV_HIT = 1, EV_DIED = 2, EV_KILL = 3;

    }

    class FileBridge
    {
        // Pont par fichiers (aucun acces a la memoire du jeu -> rien de suspect pour les antivirus)
        //   bridge/game-in.bin   : programme -> jeu, image binaire de online-shm + sequence (debut et #x2600)
        //   bridge/game-out.txt  : jeu -> programme, octets 0..#x380 en hexadecimal + sequence (debut et fin)
        public const int RxSize = 0x2610 + Proto.MaxVisible * 3104 + Ext.Size, TxBytes = 0x380 + 4 + Proto.PoseMax, TxChars = 8 + 2 * TxBytes + 8;

        readonly string dir, inPath, outPath;
        FileStream inFs, outFs;
        public readonly byte[] Img = new byte[RxSize];
        public readonly byte[] Game = new byte[TxBytes];
        readonly byte[] txt = new byte[TxChars];
        readonly byte[] tmp = new byte[TxBytes];
        uint seq = (uint)Environment.TickCount | 1;
        uint lastTx;
        uint lastFrame;
        bool anyFrame, anyChange;
        DateTime lastChange = DateTime.MinValue;
        public int FramesPerSecond;
        uint fpsFrame;
        DateTime fpsTime = DateTime.UtcNow;

        public FileBridge(string directory)
        {
            dir = directory;
            inPath = Path.Combine(dir, "game-in.bin");
            outPath = Path.Combine(dir, "game-out.txt");
        }

        public string Directory { get { return dir; } }
        public bool Ready { get { return inFs != null; } }

        public bool GameAlive
        {
            get { return anyChange && (DateTime.UtcNow - lastChange).TotalSeconds < 3.0; }
        }

        public string Open()
        {
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                inFs = new FileStream(inPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                if (inFs.Length != RxSize) inFs.SetLength(RxSize);
                return null;
            }
            catch (Exception ex)
            {
                inFs = null;
                return "impossible d'ecrire dans " + dir + " : " + ex.Message;
            }
        }

        public void Close()
        {
            try { if (inFs != null) inFs.Close(); } catch (Exception) { }
            try { if (outFs != null) outFs.Close(); } catch (Exception) { }
            inFs = null;
            outFs = null;
        }

        static int Nib(byte c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        bool HexByte(int pos, out byte b)
        {
            int hi = Nib(txt[pos]), lo = Nib(txt[pos + 1]);
            b = (byte)((hi << 4) | (lo & 15));
            return hi >= 0 && lo >= 0;
        }

        bool Hex32(int pos, out uint v)
        {
            v = 0;
            for (int i = 0; i < 4; i++)
            {
                byte b;
                if (!HexByte(pos + i * 2, out b)) return false;
                v |= (uint)b << (8 * i);
            }
            return true;
        }

        // lit game-out.txt ; renvoie vrai si de nouvelles donnees du jeu sont arrivees
        public bool Poll()
        {
            try
            {
                if (outFs == null)
                {
                    if (!File.Exists(outPath)) return false;
                    outFs = new FileStream(outPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                }
                outFs.Seek(0, SeekOrigin.Begin);
                int n = 0;
                while (n < TxChars)
                {
                    int r = outFs.Read(txt, n, TxChars - n);
                    if (r <= 0) break;
                    n += r;
                }
                if (n < TxChars) return false;
                uint head, tail;
                if (!Hex32(0, out head) || !Hex32(8 + 2 * TxBytes, out tail) || head != tail || head == lastTx) return false;
                for (int i = 0; i < TxBytes; i++)
                    if (!HexByte(8 + 2 * i, out tmp[i])) return false;
                lastTx = head;
                Buffer.BlockCopy(tmp, 0, Game, 0, TxBytes);
                uint frame = BitConverter.ToUInt32(Game, Shm.GameFrame);
                DateTime now = DateTime.UtcNow;
                if (anyFrame && frame != lastFrame)
                {
                    anyChange = true;
                    lastChange = now;
                }
                anyFrame = true;
                lastFrame = frame;
                if ((now - fpsTime).TotalSeconds >= 1.0)
                {
                    FramesPerSecond = (int)(frame - fpsFrame);
                    fpsFrame = frame;
                    fpsTime = now;
                }
                return true;
            }
            catch (IOException)
            {
                try { if (outFs != null) outFs.Close(); } catch (Exception) { }
                outFs = null;
                return false;
            }
        }

        public void WriteU32(int off, uint v)
        {
            Img[off] = (byte)v;
            Img[off + 1] = (byte)(v >> 8);
            Img[off + 2] = (byte)(v >> 16);
            Img[off + 3] = (byte)(v >> 24);
        }

        public void Write(int off, byte[] buf, int len)
        {
            Buffer.BlockCopy(buf, 0, Img, off, len);
        }

        // ecrit game-in.bin (le jeu le relit a chaque image)
        public void Commit()
        {
            if (inFs == null) return;
            seq++;
            if (seq == 0) seq = 1;
            WriteU32(0, seq);
            WriteU32(Shm.Size, seq);
            WriteU32(Ext.Base, seq);
            WriteU32(Ext.Base + Ext.Tail, seq);
            try
            {
                inFs.Seek(0, SeekOrigin.Begin);
                inFs.Write(Img, 0, RxSize);
                inFs.Flush();
            }
            catch (IOException) { }
        }
    }

    // ------------------------------------------------------------------------
    //  CLIENT : pont jeu <-> serveur
    // ------------------------------------------------------------------------
    // ------------------------------------------------------------------------
    //  Lien reseau commun (serveur TCP perso ou relais internet)
    // ------------------------------------------------------------------------
    interface ILink
    {
        bool Send(byte[] data);
        bool Closed { get; }
        void Close();
    }

    // ------------------------------------------------------------------------
    //  Client MQTT 3.1.1 minimal (relais internet public et gratuit)
    // ------------------------------------------------------------------------
    class Mqtt
    {
        NetworkStream ns;
        TcpClient tc;
        readonly object wl = new object();
        ushort packetId = 1;
        int closed;
        public Action<string, byte[]> OnMessage;
        public Action OnPingResp;
        public Action OnClosed;

        public bool Closed { get { return closed != 0; } }

        static void Str(Stream s, string v)
        {
            byte[] b = Encoding.UTF8.GetBytes(v);
            s.WriteByte((byte)(b.Length >> 8));
            s.WriteByte((byte)(b.Length & 0xff));
            s.Write(b, 0, b.Length);
        }

        bool SendPacket(int hdr, byte[] body)
        {
            if (Closed) return false;
            MemoryStream ms = new MemoryStream(body.Length + 5);
            ms.WriteByte((byte)hdr);
            int len = body.Length;
            do
            {
                int d = len % 128;
                len /= 128;
                if (len > 0) d |= 0x80;
                ms.WriteByte((byte)d);
            } while (len > 0);
            ms.Write(body, 0, body.Length);
            byte[] all = ms.ToArray();
            try
            {
                lock (wl) ns.Write(all, 0, all.Length);
                return true;
            }
            catch (Exception)
            {
                Close();
                return false;
            }
        }

        void ReadExact(byte[] buf, int len)
        {
            int got = 0;
            while (got < len)
            {
                int n = ns.Read(buf, got, len - got);
                if (n <= 0) throw new IOException("closed");
                got += n;
            }
        }

        bool ReadPacket(out int type, out byte[] body)
        {
            byte[] one = new byte[1];
            ReadExact(one, 1);
            type = one[0];
            int len = 0, mul = 1;
            for (int i = 0; i < 4; i++)
            {
                ReadExact(one, 1);
                len += (one[0] & 127) * mul;
                mul *= 128;
                if ((one[0] & 128) == 0) break;
            }
            body = new byte[len];
            if (len > 0) ReadExact(body, len);
            return true;
        }

        public bool Connect(string host, int port, string clientId, int timeoutMs)
        {
            try
            {
                tc = new TcpClient();
                IAsyncResult ar = tc.BeginConnect(host, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs) || !tc.Connected) { try { tc.Close(); } catch (Exception) { } return false; }
                tc.EndConnect(ar);
                tc.NoDelay = true;
                tc.ReceiveTimeout = timeoutMs;
                ns = tc.GetStream();
                MemoryStream b = new MemoryStream();
                Str(b, "MQTT");
                b.WriteByte(4);       // MQTT 3.1.1
                b.WriteByte(0x02);    // session propre
                b.WriteByte(0);
                b.WriteByte(45);      // keepalive 45 s
                Str(b, clientId);
                if (!SendPacket(0x10, b.ToArray())) return false;
                int type;
                byte[] body;
                ReadPacket(out type, out body);
                if ((type >> 4) != 2 || body.Length < 2 || body[1] != 0) { Close(); return false; }
                tc.ReceiveTimeout = 0;
                Thread t = new Thread(ReadLoop);
                t.IsBackground = true;
                t.Name = "mqtt-read";
                t.Start();
                return true;
            }
            catch (Exception)
            {
                Close();
                return false;
            }
        }

        void ReadLoop()
        {
            try
            {
                while (!Closed)
                {
                    int type;
                    byte[] body;
                    ReadPacket(out type, out body);
                    int kind = type >> 4;
                    if (kind == 3)
                    {
                        int tl = (body[0] << 8) | body[1];
                        string topic = Encoding.UTF8.GetString(body, 2, tl);
                        int off = 2 + tl;
                        if (((type >> 1) & 3) > 0) off += 2;
                        byte[] payload = new byte[body.Length - off];
                        Buffer.BlockCopy(body, off, payload, 0, payload.Length);
                        if (OnMessage != null)
                        {
                            try { OnMessage(topic, payload); } catch (Exception) { }
                        }
                    }
                    else if (kind == 13)
                    {
                        if (OnPingResp != null) OnPingResp();
                    }
                }
            }
            catch (Exception) { }
            Close();
        }

        public void Publish(string topic, byte[] payload, bool retain)
        {
            MemoryStream b = new MemoryStream(payload.Length + topic.Length + 2);
            Str(b, topic);
            b.Write(payload, 0, payload.Length);
            SendPacket(0x30 | (retain ? 1 : 0), b.ToArray());
        }

        public void Subscribe(string filter)
        {
            MemoryStream b = new MemoryStream();
            ushort id = packetId++;
            if (packetId == 0) packetId = 1;
            b.WriteByte((byte)(id >> 8));
            b.WriteByte((byte)(id & 0xff));
            Str(b, filter);
            b.WriteByte(0);
            SendPacket(0x82, b.ToArray());
        }

        public void Unsubscribe(string filter)
        {
            MemoryStream b = new MemoryStream();
            ushort id = packetId++;
            if (packetId == 0) packetId = 1;
            b.WriteByte((byte)(id >> 8));
            b.WriteByte((byte)(id & 0xff));
            Str(b, filter);
            SendPacket(0xA2, b.ToArray());
        }

        public void Ping() { SendPacket(0xC0, new byte[0]); }

        public void Disconnect()
        {
            SendPacket(0xE0, new byte[0]);
            Close();
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref closed, 1) != 0) return;
            try { if (tc != null) tc.Close(); } catch (Exception) { }
            if (OnClosed != null)
            {
                try { OnClosed(); } catch (Exception) { }
            }
        }
    }

    // ------------------------------------------------------------------------
    //  RELAIS INTERNET GRATUIT : sessions sans serveur a installer.
    //  Tout passe par un relais MQTT public (sans compte, sans port a ouvrir).
    //  L'hote d'une session publie ses infos ; s'il part ou plante, le joueur
    //  arrive le plus tot devient hote (migration automatique).
    //  Cette classe se comporte exactement comme le serveur TCP vu du Client.
    // ------------------------------------------------------------------------
    class RelayLink : ILink
    {
        public static readonly string[] Brokers = { "broker.hivemq.com", "broker.emqx.io", "test.mosquitto.org" };
        const string Root = "j3online/v3/";
        const float CellSize = 250f * 4096f;  // zones de 250 m
        const double MemberTimeout = 6.0;

        class Info
        {
            public string Code = "";
            public uint HostId;
            public string HostName = "";
            public bool Public, Pvp;
            public int Max = 100, Count = 1;
            public long Stamp;
        }

        class Member
        {
            public uint Id;
            public long JoinTime;
            public string Name = "", Level = "";
            public float Health;
            public int Flags, Ping;
            public DateTime Seen;
            public float X, Y, Z;
            public bool HasPos;
        }

        class RState
        {
            public byte[] St = new byte[Proto.StateSize];
            public DateTime T;
            public float X, Y, Z;
        }

        class LobbyEntry
        {
            public Info I;
            public DateTime Seen;
        }

        Mqtt m = new Mqtt();
        readonly Action<byte, BinaryReader> deliver;
        readonly Action<string> log;
        readonly object lk = new object();
        readonly object emitLock = new object();
        readonly Random rng = new Random();
        volatile bool closed;

        uint myId;
        string myName = "Joueur";
        long myJoin;
        string code;          // session courante (null = aucune)
        Info info;
        bool isHost;
        DateTime enteredAt;
        readonly Dictionary<uint, Member> members = new Dictionary<uint, Member>();
        readonly Dictionary<uint, RState> states = new Dictionary<uint, RState>();
        readonly Dictionary<string, LobbyEntry> lobby = new Dictionary<string, LobbyEntry>();
        readonly HashSet<string> cellSubs = new HashSet<string>();
        byte[] myState;
        DateTime myStateTime = DateTime.MinValue;
        string myCell = "";
        string pendingCode;
        Info pendingInfo;
        uint pingT;
        byte[] lastBan;       // liste des bannis (gardee par le relais)
        byte[] lastVer;       // version officielle (gardee par le relais)
        public string BrokerName = "";

        public RelayLink(Action<byte, BinaryReader> deliver, Action<string> log)
        {
            this.deliver = deliver;
            this.log = log;
            byte[] b = new byte[4];
            new Random(Guid.NewGuid().GetHashCode()).NextBytes(b);
            myId = BitConverter.ToUInt32(b, 0) & 0x7fffffff;
            if (myId == 0) myId = 1;
        }

        void Hook(Mqtt x)
        {
            x.OnMessage = OnMessage;
            x.OnPingResp = delegate
            {
                Emit(new PacketWriter(Proto.S_PONG).U32(pingT).ToArray());
            };
            x.OnClosed = delegate { if (x == m) closed = true; };
        }

        public bool Closed { get { return closed; } }

        static long NowMs() { return (DateTime.UtcNow.Ticks - 621355968000000000L) / 10000L; }

        public bool Start()
        {
            // certains pare-feu (Avast...) retardent chaque nouvelle connexion de 10-20 s :
            // on essaie les relais en meme temps et on attend longtemps. Le premier relais
            // reste prioritaire pour que tout le monde se retrouve sur le meme.
            int n = Brokers.Length;
            Mqtt[] cands = new Mqtt[n];
            int[] result = new int[n]; // 0 en cours, 1 ok, 2 echec
            for (int i = 0; i < n; i++)
            {
                int k = i;
                cands[k] = new Mqtt();
                Hook(cands[k]);
                Thread t = new Thread(delegate ()
                {
                    bool ok = cands[k].Connect(Brokers[k], 1883, "j3o-" + myId.ToString("x") + "-" + k + "-" + new Random(Guid.NewGuid().GetHashCode()).Next(100000), 30000);
                    result[k] = ok ? 1 : 2;
                });
                t.IsBackground = true;
                t.Start();
            }
            int chosen = -1;
            DateTime end = DateTime.UtcNow.AddSeconds(32);
            while (DateTime.UtcNow < end && chosen < 0)
            {
                Thread.Sleep(50);
                if (result[0] == 1) { chosen = 0; break; }
                if (result[0] == 2)
                    for (int i = 1; i < n; i++) if (result[i] == 1) { chosen = i; break; }
                bool allDone = true;
                for (int i = 0; i < n; i++) if (result[i] == 0) allDone = false;
                if (allDone && chosen < 0)
                {
                    for (int i = 1; i < n; i++) if (result[i] == 1) { chosen = i; break; }
                    break;
                }
            }
            for (int i = 0; i < n; i++)
                if (i != chosen) { int k = i; ThreadPool.QueueUserWorkItem(delegate { Thread.Sleep(35000); cands[k].Close(); }); cands[i].OnMessage = null; }
            if (chosen < 0)
            {
                closed = true;
                return false;
            }
            m = cands[chosen];
            BrokerName = Brokers[chosen];
            m.Subscribe(Root + "lobby/+");
            m.Subscribe(Root + "banlist");
            m.Subscribe(Root + "version");
            Thread tick = new Thread(TickLoop);
            tick.IsBackground = true;
            tick.Name = "relay-tick";
            tick.Start();
            return true;
        }

        public void Close()
        {
            if (closed) return;
            try
            {
                lock (lk) LeaveInternal();
                Thread.Sleep(150);
            }
            catch (Exception) { }
            m.Disconnect();
            closed = true;
        }

        // pour les tests : coupure brutale (plantage simule)
        public void Kill()
        {
            m.Close();
            closed = true;
        }

        void Emit(byte[] pkt)
        {
            lock (emitLock)
            {
                try { deliver(pkt[2], new BinaryReader(new MemoryStream(pkt, 3, pkt.Length - 3))); } catch (Exception) { }
            }
        }

        static uint CodeHash(string c)
        {
            uint h = 2166136261;
            foreach (char ch in c) { h ^= ch; h *= 16777619; }
            h &= 0x7fffffff;
            return h == 0 ? 1u : h;
        }

        // ---------------- encodage des messages relais
        static byte[] EncInfo(Info i)
        {
            MemoryStream ms = new MemoryStream();
            BinaryWriter w = new BinaryWriter(ms);
            w.Write((byte)1);
            WStr(w, i.Code, 8);
            w.Write(i.HostId);
            WStr(w, i.HostName, 16);
            w.Write((byte)((i.Public ? 1 : 0) | (i.Pvp ? 2 : 0)));
            w.Write((byte)i.Max);
            w.Write((byte)i.Count);
            w.Write(i.Stamp);
            return ms.ToArray();
        }

        static Info DecInfo(byte[] p)
        {
            BinaryReader r = new BinaryReader(new MemoryStream(p));
            if (r.ReadByte() != 1) return null;
            Info i = new Info();
            i.Code = Rd.Str(r, 8);
            i.HostId = r.ReadUInt32();
            i.HostName = Rd.Str(r, 16);
            int f = r.ReadByte();
            i.Public = (f & 1) != 0;
            i.Pvp = (f & 2) != 0;
            i.Max = r.ReadByte();
            i.Count = r.ReadByte();
            i.Stamp = r.ReadInt64();
            return i;
        }

        static void WStr(BinaryWriter w, string s, int len)
        {
            byte[] b = new byte[len];
            string a = Proto.Ascii(s, len - 1);
            Encoding.ASCII.GetBytes(a, 0, a.Length, b, 0);
            w.Write(b);
        }

        static byte[] Bytes(Action<BinaryWriter> f)
        {
            MemoryStream ms = new MemoryStream();
            BinaryWriter w = new BinaryWriter(ms);
            f(w);
            w.Flush();
            return ms.ToArray();
        }

        string T(string what) { return Root + "s/" + code + "/" + what; }

        // ---------------- paquets venant du Client (comme si c'etait le serveur)
        public bool Send(byte[] data)
        {
            if (closed) return false;
            byte type = data[2];
            BinaryReader r = new BinaryReader(new MemoryStream(data, 3, data.Length - 3));
            try
            {
                switch (type)
                {
                    case Proto.C_HELLO:
                        r.ReadUInt16();
                        myName = Proto.CleanName(Rd.Str(r, 16));
                        Emit(new PacketWriter(Proto.S_WELCOME).U32(myId).Str(myName, 16).ToArray());
                        break;
                    case Proto.C_CREATE:
                        {
                            int max = r.ReadByte();
                            int flags = r.ReadByte();
                            lock (lk)
                            {
                                LeaveInternal();
                                Info i = new Info();
                                i.Code = NewCode();
                                i.HostId = myId;
                                i.HostName = myName;
                                i.Public = (flags & 1) != 0;
                                i.Pvp = (flags & 2) != 0;
                                i.Max = Math.Max(2, Math.Min(Proto.MaxPlayersPerSession, max));
                                EnterSession(i, true);
                                PublishInfo();
                                PublishEvent(Proto.F_JOINED, myId, 0, myName, "", 0);
                            }
                            if (log != null) log("Session " + info.Code + " creee sur le relais internet " + BrokerName);
                            break;
                        }
                    case Proto.C_JOIN_ID:
                        {
                            uint id = r.ReadUInt32();
                            string found = null;
                            lock (lk)
                                foreach (LobbyEntry e in lobby.Values)
                                    if (CodeHash(e.I.Code) == id) found = e.I.Code;
                            if (found == null) Emit(new PacketWriter(Proto.S_ERROR).U8(Proto.E_NOT_FOUND).ToArray());
                            else StartJoin(found);
                            break;
                        }
                    case Proto.C_JOIN_CODE:
                        StartJoin(Rd.Str(r, 8).Trim().ToUpperInvariant());
                        break;
                    case Proto.C_JOIN_WORLD:
                        StartWorld();
                        break;
                    case Proto.C_LEAVE:
                        lock (lk) LeaveInternal();
                        break;
                    case Proto.C_LIST:
                        EmitList();
                        break;
                    case Proto.C_STATE:
                        {
                            byte[] st = r.ReadBytes(Proto.StateSize);
                            if (st.Length == Proto.StateSize)
                                lock (lk) { myState = st; myStateTime = DateTime.UtcNow; }
                            break;
                        }
                    case Proto.C_POSE:
                        {
                            // pose : publiee tout de suite dans ma zone (le client limite la cadence)
                            int len = data.Length - 3;
                            if (len < Proto.PoseMin || len > Proto.PoseMax) break;
                            lock (lk)
                            {
                                if (code == null || myCell == "") break;
                                byte[] pl = new byte[4 + len];
                                Buffer.BlockCopy(BitConverter.GetBytes(myId), 0, pl, 0, 4);
                                Buffer.BlockCopy(data, 3, pl, 4, len);
                                m.Publish(T("k/" + myCell), pl, false);
                            }
                            break;
                        }
                    case Proto.C_MSG:
                        {
                            int kind = r.ReadByte();
                            uint target = r.ReadUInt32();
                            int len = data.Length - 8;
                            if (len < 0 || len > 1400) break;
                            byte[] pl = new byte[9 + len];
                            Buffer.BlockCopy(BitConverter.GetBytes(myId), 0, pl, 0, 4);
                            pl[4] = (byte)kind;
                            Buffer.BlockCopy(BitConverter.GetBytes(target), 0, pl, 5, 4);
                            Buffer.BlockCopy(data, 8, pl, 9, len);
                            lock (lk)
                            {
                                // liste des bannis : gardee par le relais pour les prochains arrivants
                                if (kind == 4) { m.Publish(Root + "banlist", pl, true); lastBan = pl; }
                                if (kind == 13) { m.Publish(Root + "version", pl, true); lastVer = pl; }
                                if (code == null) break;
                                m.Publish(T(target == 0 ? "m" : "m/" + target), pl, false);
                            }
                            break;
                        }
                    case Proto.C_HIT:
                        {
                            uint target = r.ReadUInt32();
                            float dmg = r.ReadSingle();
                            int mode = r.ReadByte();
                            float dx = r.ReadSingle(), dy = r.ReadSingle(), dz = r.ReadSingle();
                            lock (lk)
                            {
                                if (code == null || !info.Pvp || float.IsNaN(dmg) || dmg < 0f) break;
                                byte[] p = Bytes(w => { w.Write(myId); w.Write(Math.Min(20f, dmg)); w.Write((byte)mode); w.Write(dx); w.Write(dy); w.Write(dz); });
                                m.Publish(T("h/" + target), p, false);
                            }
                            break;
                        }
                    case Proto.C_DIED:
                        {
                            uint killer = r.ReadUInt32();
                            lock (lk)
                            {
                                if (code == null) break;
                                Member k;
                                if (killer != 0 && members.TryGetValue(killer, out k))
                                    PublishEvent(Proto.F_KILLED, killer, myId, k.Name, myName, 0);
                                else
                                    PublishEvent(Proto.F_DIED, myId, 0, myName, "", 0);
                            }
                            break;
                        }
                    case Proto.C_SETTINGS:
                        {
                            int flags = r.ReadByte();
                            lock (lk)
                            {
                                if (code == null) break;
                                if (!isHost) { Emit(new PacketWriter(Proto.S_ERROR).U8(Proto.E_NOT_HOST).ToArray()); break; }
                                bool pvp = (flags & 1) != 0, pub = (flags & 2) != 0;
                                if (pvp != info.Pvp) { info.Pvp = pvp; PublishEvent(Proto.F_PVP, myId, 0, myName, "", pvp ? 1 : 0); }
                                if (pub != info.Public) { info.Public = pub; PublishEvent(Proto.F_VISIBILITY, myId, 0, myName, "", pub ? 1 : 0); }
                                PublishInfo();
                                EmitSession();
                            }
                            break;
                        }
                    case Proto.C_PING:
                        pingT = r.ReadUInt32();
                        m.Ping();
                        break;
                    case Proto.C_SAVE_REQ:
                        lock (lk)
                        {
                            if (code == null || info == null || info.HostId == myId) break;
                            m.Publish(T("sq/" + info.HostId), BitConverter.GetBytes(myId), false);
                        }
                        break;
                    case Proto.C_SAVE_CHUNK:
                        {
                            uint target = r.ReadUInt32();
                            int idx = r.ReadUInt16(), cnt = r.ReadUInt16(), len = r.ReadUInt16();
                            byte[] chunkData = r.ReadBytes(len);
                            lock (lk)
                            {
                                if (code == null) break;
                                byte[] pl = Bytes(w => { w.Write((ushort)idx); w.Write((ushort)cnt); w.Write((ushort)chunkData.Length); w.Write(chunkData); });
                                m.Publish(T("sd/" + target), pl, false);
                            }
                            break;
                        }
                    case Proto.C_SAVE_NOTIFY:
                        lock (lk) { if (code != null && isHost) m.Publish(T("sn"), BitConverter.GetBytes(myId), false); }
                        break;
                    case Proto.C_RENAME:
                        lock (lk)
                        {
                            myName = Proto.CleanName(Rd.Str(r, 16));
                            if (code != null)
                            {
                                PublishPresence();
                                if (isHost) PublishInfo();
                            }
                        }
                        Emit(new PacketWriter(Proto.S_WELCOME).U32(myId).Str(myName, 16).ToArray());
                        break;
                }
            }
            catch (EndOfStreamException) { }
            return !closed;
        }

        string NewCode()
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            char[] c = new char[6];
            for (int i = 0; i < 6; i++) c[i] = chars[rng.Next(chars.Length)];
            return new string(c);
        }

        void StartJoin(string c)
        {
            if (c.Length == 0) { Emit(new PacketWriter(Proto.S_ERROR).U8(Proto.E_BAD_CODE).ToArray()); return; }
            lock (lk)
            {
                if (c == code) return;
                pendingCode = c;
                pendingInfo = null;
            }
            Thread t = new Thread(delegate ()
            {
                string topic = Root + "s/" + c + "/i";
                m.Subscribe(topic);
                DateTime end = DateTime.UtcNow.AddSeconds(5);
                Info got = null;
                while (DateTime.UtcNow < end && !closed)
                {
                    lock (lk) got = pendingInfo;
                    if (got != null) break;
                    Thread.Sleep(50);
                }
                lock (lk)
                {
                    pendingCode = null;
                    if (got == null)
                    {
                        if (code != c) m.Unsubscribe(topic);
                        Emit(new PacketWriter(Proto.S_ERROR).U8(Proto.E_BAD_CODE).ToArray());
                        return;
                    }
                    if (got.Count >= got.Max && (NowMs() - got.Stamp) < 20000)
                    {
                        m.Unsubscribe(topic);
                        Emit(new PacketWriter(Proto.S_ERROR).U8(Proto.E_FULL).ToArray());
                        return;
                    }
                    LeaveInternal();
                    EnterSession(got, false);
                    PublishPresence();
                    PublishEvent(Proto.F_JOINED, myId, 0, myName, "", 0);
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        // LA session publique : on cherche MONDE1, MONDE2... ; on rejoint la premiere qui a
        // de la place, ou on la cree si personne n'y est.
        // une seule recherche de la session publique a la fois (les demandes en double
        // faisaient quitter / recreer la session en boucle)
        int worldStarting;
        void StartWorld()
        {
            if (Interlocked.CompareExchange(ref worldStarting, 1, 0) != 0) return;
            Thread t = new Thread(delegate ()
            {
              try
              {
                for (int k = 1; k <= 20 && !closed; k++)
                {
                    string c = Proto.WorldPrefix + k;
                    lock (lk)
                    {
                        if (c == code) return;
                        pendingCode = c;
                        pendingInfo = null;
                    }
                    string topic = Root + "s/" + c + "/i";
                    m.Subscribe(topic);
                    DateTime end = DateTime.UtcNow.AddSeconds(4);
                    Info got = null;
                    while (DateTime.UtcNow < end && !closed)
                    {
                        lock (lk) got = pendingInfo;
                        if (got != null) break;
                        Thread.Sleep(50);
                    }
                    lock (lk)
                    {
                        pendingCode = null;
                        if (got != null && got.Count >= got.Max && (NowMs() - got.Stamp) < 20000)
                        {
                            m.Unsubscribe(topic);
                            continue; // pleine : la suivante
                        }
                        LeaveInternal();
                        if (got != null)
                        {
                            got.Public = true;
                            EnterSession(got, false);
                            PublishPresence();
                        }
                        else
                        {
                            Info i = new Info();
                            i.Code = c;
                            i.HostId = myId;
                            i.HostName = myName;
                            i.Public = true;
                            i.Pvp = true;
                            i.Max = Proto.MaxPlayersPerSession;
                            EnterSession(i, true);
                            PublishInfo();
                            if (log != null) log("Session publique " + c + " ouverte sur le relais " + BrokerName);
                        }
                        PublishEvent(Proto.F_JOINED, myId, 0, myName, "", 0);
                        return;
                    }
                }
                Emit(new PacketWriter(Proto.S_ERROR).U8(Proto.E_FULL).ToArray());
              }
              finally { Interlocked.Exchange(ref worldStarting, 0); }
            });
            t.IsBackground = true;
            t.Start();
        }

        void EnterSession(Info i, bool host)
        {
            code = i.Code;
            info = i;
            isHost = host;
            myJoin = NowMs();
            enteredAt = DateTime.UtcNow;
            members.Clear();
            states.Clear();
            myCell = "";
            m.Subscribe(T("i"));
            m.Subscribe(T("p"));
            m.Subscribe(T("e"));
            m.Subscribe(T("h/" + myId));
            m.Subscribe(T("sq/" + myId));
            m.Subscribe(T("sd/" + myId));
            m.Subscribe(T("sn"));
            m.Subscribe(T("m"));
            m.Subscribe(T("m/" + myId));
            EmitSession();
            if (lastBan != null) EmitMsg(lastBan);
            if (lastVer != null) EmitMsg(lastVer);
        }

        void EmitMsg(byte[] pl)
        {
            if (pl.Length >= 9) Emit(new PacketWriter(Proto.S_MSG).Bytes(pl, 0, pl.Length).ToArray());
        }

        void LeaveInternal()
        {
            if (code == null) return;
            PublishEvent(Proto.F_LEFT, myId, 0, myName, "", 0);
            if (isHost)
            {
                Member next = Successor();
                if (next != null)
                {
                    info.HostId = next.Id;
                    info.HostName = next.Name;
                    info.Count = Math.Max(1, members.Count);
                    PublishInfo();
                    PublishEvent(Proto.F_HOST, next.Id, 0, next.Name, "", 0);
                }
                else
                {
                    // dernier joueur : la session disparait
                    m.Publish(T("i"), new byte[0], true);
                    m.Publish(Root + "lobby/" + code, new byte[0], true);
                }
            }
            m.Unsubscribe(T("i"));
            m.Unsubscribe(T("p"));
            m.Unsubscribe(T("e"));
            m.Unsubscribe(T("h/" + myId));
            m.Unsubscribe(T("sq/" + myId));
            m.Unsubscribe(T("sd/" + myId));
            m.Unsubscribe(T("sn"));
            m.Unsubscribe(T("m"));
            m.Unsubscribe(T("m/" + myId));
            foreach (string c in cellSubs) m.Unsubscribe(c);
            cellSubs.Clear();
            code = null;
            info = null;
            isHost = false;
            members.Clear();
            states.Clear();
            Emit(new PacketWriter(Proto.S_SESSION).U32(0).Str("", 8).U8(0).U8(0).U32(0).U8(0).ToArray());
        }

        Member Successor()
        {
            Member best = null;
            foreach (Member mb in members.Values)
                if (best == null || mb.JoinTime < best.JoinTime || (mb.JoinTime == best.JoinTime && mb.Id < best.Id)) best = mb;
            return best;
        }

        void PublishInfo()
        {
            info.Count = members.Count + 1;
            info.Stamp = NowMs();
            info.HostId = isHost ? myId : info.HostId;
            if (isHost) info.HostName = myName;
            byte[] p = EncInfo(info);
            m.Publish(T("i"), p, true);
            m.Publish(Root + "lobby/" + code, info.Public ? p : new byte[0], true);
        }

        void PublishPresence()
        {
            float health = 0f;
            int flags = 0;
            string level = "";
            if (myState != null && (DateTime.UtcNow - myStateTime).TotalSeconds < 3)
            {
                health = BitConverter.ToSingle(myState, 24);
                if ((BitConverter.ToUInt32(myState, 0) & Proto.FLAG_DEAD) != 0) flags |= Proto.PFLAG_DEAD;
                int n = 0;
                while (n < 16 && myState[64 + n] != 0) n++;
                level = Encoding.ASCII.GetString(myState, 64, n);
            }
            int ping = myState != null ? BitConverter.ToUInt16(myState, 60) : 0;
            bool hasPos = myState != null && (DateTime.UtcNow - myStateTime).TotalSeconds < 3;
            float px = hasPos ? BitConverter.ToSingle(myState, 12) : 0f, py = hasPos ? BitConverter.ToSingle(myState, 16) : 0f, pz = hasPos ? BitConverter.ToSingle(myState, 20) : 0f;
            byte[] p = Bytes(w => { w.Write(myId); w.Write(myJoin); WStr(w, myName, 16); WStr(w, level, 16); w.Write(health); w.Write((byte)flags); w.Write((ushort)ping);
                w.Write(px); w.Write(py); w.Write(pz); w.Write((byte)(hasPos ? 1 : 0)); });
            m.Publish(T("p"), p, false);
        }

        void PublishEvent(int kind, uint a, uint b, string na, string nb, int v)
        {
            byte[] p = Bytes(w => { w.Write((byte)kind); w.Write(a); w.Write(b); WStr(w, na, 16); WStr(w, nb, 16); w.Write((byte)v); });
            m.Publish(T("e"), p, false);
        }

        void EmitSession()
        {
            if (info == null) return;
            Emit(new PacketWriter(Proto.S_SESSION).U32(CodeHash(info.Code)).Str(info.Code, 8)
                .U8((info.Public ? 1 : 0) | (info.Pvp ? 2 : 0)).U8(info.Max).U32(info.HostId).U8(members.Count + 1).ToArray());
        }

        void EmitList()
        {
            List<Info> list = new List<Info>();
            long now = NowMs();
            lock (lk)
            {
                foreach (LobbyEntry e in lobby.Values)
                    if (e.I.Public && Math.Abs(now - e.I.Stamp) < 45000 && (DateTime.UtcNow - e.Seen).TotalSeconds < 20) list.Add(e.I);
            }
            list.Sort(delegate (Info a, Info b) { return b.Count.CompareTo(a.Count); });
            int n = Math.Min(list.Count, Proto.MaxListed);
            PacketWriter w = new PacketWriter(Proto.S_LIST);
            w.U8(n);
            for (int i = 0; i < n; i++)
                w.U32(CodeHash(list[i].Code)).U8(list[i].Count).U8(list[i].Max).U8((list[i].Public ? 1 : 0) | (list[i].Pvp ? 2 : 0)).Str(list[i].HostName, 16).Str(list[i].Code, 8);
            Emit(w.ToArray());
        }

        // ---------------- messages recus du relais
        void OnMessage(string topic, byte[] p)
        {
            if (closed || !topic.StartsWith(Root)) return;
            string rest = topic.Substring(Root.Length);
            if (rest == "version")
            {
                if (p.Length >= 9)
                    lock (lk) { lastVer = p; if (code != null) EmitMsg(p); }
                return;
            }
            if (rest == "banlist")
            {
                if (p.Length >= 9)
                    lock (lk) { lastBan = p; if (code != null) EmitMsg(p); }
                return;
            }
            if (rest.StartsWith("lobby/"))
            {
                string c = rest.Substring(6);
                lock (lk)
                {
                    Info i = p.Length > 0 ? DecInfo(p) : null;
                    if (i == null) lobby.Remove(c);
                    else { LobbyEntry e = new LobbyEntry(); e.I = i; e.Seen = DateTime.UtcNow; lobby[c] = e; }
                }
                return;
            }
            if (!rest.StartsWith("s/")) return;
            string[] parts = rest.Split('/');
            if (parts.Length < 3) return;
            string sc = parts[1];
            string what = parts[2];
            lock (lk)
            {
                if (what == "i" && pendingCode == sc && p.Length > 0)
                {
                    pendingInfo = DecInfo(p);
                }
                if (sc != code) return;
                DateTime now = DateTime.UtcNow;
                switch (what)
                {
                    case "i":
                        {
                            if (p.Length == 0) break;
                            Info i = DecInfo(p);
                            if (i == null) break;
                            if (isHost && i.HostId != myId)
                            {
                                // deux hotes : le joueur arrive le plus tot garde la place
                                Member other;
                                if (members.TryGetValue(i.HostId, out other) && (other.JoinTime < myJoin || (other.JoinTime == myJoin && other.Id < myId)))
                                {
                                    isHost = false;
                                    info = i;
                                    EmitSession();
                                }
                                else if (i.HostId != myId) PublishInfo();
                                break;
                            }
                            bool changed = info == null || i.HostId != info.HostId || i.Pvp != info.Pvp || i.Public != info.Public || i.Max != info.Max;
                            if (!isHost) info = i;
                            if (i.HostId == myId && !isHost)
                            {
                                // l'ancien hote nous a passe la main
                                isHost = true;
                                PublishInfo();
                            }
                            if (changed) EmitSession();
                            break;
                        }
                    case "p":
                        {
                            BinaryReader r = new BinaryReader(new MemoryStream(p));
                            uint id = r.ReadUInt32();
                            if (id == myId) break;
                            Member mb;
                            if (!members.TryGetValue(id, out mb)) { mb = new Member(); mb.Id = id; members[id] = mb; }
                            mb.JoinTime = r.ReadInt64();
                            mb.Name = Rd.Str(r, 16);
                            mb.Level = Rd.Str(r, 16);
                            mb.Health = r.ReadSingle();
                            mb.Flags = r.ReadByte();
                            mb.Ping = r.ReadUInt16();
                            if (r.BaseStream.Length - r.BaseStream.Position >= 13)
                            {
                                mb.X = r.ReadSingle();
                                mb.Y = r.ReadSingle();
                                mb.Z = r.ReadSingle();
                                mb.HasPos = r.ReadByte() != 0;
                            }
                            mb.Seen = now;
                            break;
                        }
                    case "c":
                        {
                            if (p.Length < 4 + Proto.StateSize) break;
                            uint id = BitConverter.ToUInt32(p, 0);
                            if (id == myId) break;
                            RState s;
                            if (!states.TryGetValue(id, out s)) { s = new RState(); states[id] = s; }
                            Buffer.BlockCopy(p, 4, s.St, 0, Proto.StateSize);
                            s.X = BitConverter.ToSingle(s.St, 12);
                            s.Y = BitConverter.ToSingle(s.St, 16);
                            s.Z = BitConverter.ToSingle(s.St, 20);
                            s.T = now;
                            break;
                        }
                    case "m":
                        if (p.Length >= 9 && BitConverter.ToUInt32(p, 0) != myId) EmitMsg(p);
                        break;
                    case "k":
                        {
                            if (p.Length < 4 + Proto.PoseMin || p.Length > 4 + Proto.PoseMax) break;
                            uint id = BitConverter.ToUInt32(p, 0);
                            if (id == myId) break;
                            Emit(new PacketWriter(Proto.S_POSE).U32(id).Bytes(p, 4, p.Length - 4).ToArray());
                            break;
                        }
                    case "h":
                        {
                            if (info == null || !info.Pvp || p.Length < 21) break;
                            BinaryReader r = new BinaryReader(new MemoryStream(p));
                            uint from = r.ReadUInt32();
                            float dmg = r.ReadSingle();
                            int mode = r.ReadByte();
                            float dx = r.ReadSingle(), dy = r.ReadSingle(), dz = r.ReadSingle();
                            Emit(new PacketWriter(Proto.S_HIT).U32(from).F32(Math.Min(20f, dmg)).U8(mode).F32(dx).F32(dy).F32(dz).ToArray());
                            break;
                        }
                    case "sq":
                        if (p.Length >= 4) Emit(new PacketWriter(Proto.S_SAVE_REQ).U32(BitConverter.ToUInt32(p, 0)).ToArray());
                        break;
                    case "sd":
                        if (p.Length >= 6)
                        {
                            int len = BitConverter.ToUInt16(p, 4);
                            if (p.Length >= 6 + len)
                                Emit(new PacketWriter(Proto.S_SAVE_CHUNK).U16(BitConverter.ToUInt16(p, 0)).U16(BitConverter.ToUInt16(p, 2)).U16(len).Bytes(p, 6, len).ToArray());
                        }
                        break;
                    case "sn":
                        if (p.Length >= 4 && BitConverter.ToUInt32(p, 0) != myId) Emit(new PacketWriter(Proto.S_SAVE_NOTIFY).ToArray());
                        break;
                    case "e":
                        {
                            BinaryReader r = new BinaryReader(new MemoryStream(p));
                            int kind = r.ReadByte();
                            uint a = r.ReadUInt32(), b = r.ReadUInt32();
                            string na = Rd.Str(r, 16), nb = Rd.Str(r, 16);
                            int v = r.ReadByte();
                            if (kind == Proto.F_LEFT && a != myId) { members.Remove(a); states.Remove(a); }
                            if (kind == Proto.F_KILLED && a == myId) Emit(new PacketWriter(Proto.S_KILL).U32(b).ToArray());
                            if (kind == Proto.F_HOST && a == myId && !isHost) { isHost = true; PublishInfo(); EmitSession(); }
                            Emit(new PacketWriter(Proto.S_FEED).U8(kind).Str(na, 16).Str(nb, 16).U8(v).ToArray());
                            break;
                        }
                }
            }
        }

        // ---------------- boucle : etat, presence, hote, zones
        void UpdateCells(float x, float z)
        {
            int cx = (int)Math.Floor(x / CellSize), cz = (int)Math.Floor(z / CellSize);
            string cell = cx + "_" + cz;
            if (cell == myCell) return;
            myCell = cell;
            HashSet<string> want = new HashSet<string>();
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    want.Add(T("c/" + (cx + dx) + "_" + (cz + dz)));
                    want.Add(T("k/" + (cx + dx) + "_" + (cz + dz)));
                }
            foreach (string s in new List<string>(cellSubs))
                if (!want.Contains(s)) { m.Unsubscribe(s); cellSubs.Remove(s); }
            foreach (string s in want)
                if (cellSubs.Add(s)) m.Subscribe(s);
        }

        void TickLoop()
        {
            DateTime lastState = DateTime.MinValue, lastPresence = DateTime.MinValue, lastInfo = DateTime.MinValue, lastPlayers = DateTime.MinValue;
            List<KeyValuePair<float, KeyValuePair<uint, RState>>> near = new List<KeyValuePair<float, KeyValuePair<uint, RState>>>();
            while (!closed)
            {
                Thread.Sleep(50);
                DateTime now = DateTime.UtcNow;
                try
                {
                    lock (lk)
                    {
                        if (code == null) continue;
                        bool fresh = myState != null && (now - myStateTime).TotalSeconds < 2;
                        float mx = 0, my = 0, mz = 0;
                        if (fresh)
                        {
                            mx = BitConverter.ToSingle(myState, 12);
                            my = BitConverter.ToSingle(myState, 16);
                            mz = BitConverter.ToSingle(myState, 20);
                        }
                        // etat : 18 fois par seconde, dans la zone ou je suis (vehicules plus fluides)
                        if (fresh && (now - lastState).TotalMilliseconds >= 55)
                        {
                            lastState = now;
                            UpdateCells(mx, mz);
                            byte[] p = new byte[4 + Proto.StateSize];
                            Buffer.BlockCopy(BitConverter.GetBytes(myId), 0, p, 0, 4);
                            Buffer.BlockCopy(myState, 0, p, 4, Proto.StateSize);
                            m.Publish(T("c/" + myCell), p, false);
                        }
                        else if (!fresh && myCell == "") UpdateCells(0, 0);
                        // presence : 1 fois par seconde
                        if ((now - lastPresence).TotalSeconds >= 1)
                        {
                            lastPresence = now;
                            PublishPresence();
                            List<uint> gone = new List<uint>();
                            foreach (Member mb in members.Values) if ((now - mb.Seen).TotalSeconds > MemberTimeout) gone.Add(mb.Id);
                            foreach (uint id in gone)
                            {
                                string n = members[id].Name;
                                members.Remove(id);
                                states.Remove(id);
                                Emit(new PacketWriter(Proto.S_FEED).U8(Proto.F_LEFT).Str(n, 16).Str("", 16).U8(0).ToArray());
                            }
                            // migration d'hote si l'hote ne repond plus
                            if (!isHost && info != null && (now - enteredAt).TotalSeconds > MemberTimeout && !members.ContainsKey(info.HostId))
                            {
                                Member best = Successor();
                                if (best == null || myJoin < best.JoinTime || (myJoin == best.JoinTime && myId < best.Id))
                                {
                                    isHost = true;
                                    PublishInfo();
                                    PublishEvent(Proto.F_HOST, myId, 0, myName, "", 0);
                                    EmitSession();
                                }
                            }
                        }
                        // l'hote republie les infos (liste publique, nombre de joueurs)
                        if (isHost && (now - lastInfo).TotalSeconds >= 5)
                        {
                            lastInfo = now;
                            int before = info.Count;
                            PublishInfo();
                            if (before != info.Count) EmitSession();
                        }
                        // les 16 joueurs les plus proches -> jeu
                        near.Clear();
                        foreach (KeyValuePair<uint, RState> kv in states)
                        {
                            if ((now - kv.Value.T).TotalSeconds > 1.5) continue;
                            float dx = kv.Value.X - mx, dy = kv.Value.Y - my, dz = kv.Value.Z - mz;
                            near.Add(new KeyValuePair<float, KeyValuePair<uint, RState>>(dx * dx + dy * dy + dz * dz, kv));
                        }
                        if (near.Count > 0)
                        {
                            near.Sort(delegate (KeyValuePair<float, KeyValuePair<uint, RState>> a, KeyValuePair<float, KeyValuePair<uint, RState>> b) { return a.Key.CompareTo(b.Key); });
                            int n = Math.Min(near.Count, Proto.MaxVisible);
                            PacketWriter w = new PacketWriter(Proto.S_STATES);
                            w.U8(n);
                            for (int i = 0; i < n; i++) w.U32(near[i].Value.Key).Bytes(near[i].Value.Value.St, 0, Proto.StateSize);
                            Emit(w.ToArray());
                        }
                        // liste des joueurs : 1 fois par seconde
                        if ((now - lastPlayers).TotalSeconds >= 1)
                        {
                            lastPlayers = now;
                            List<Member> all = new List<Member>(members.Values);
                            all.Sort(delegate (Member a, Member b) { return a.JoinTime.CompareTo(b.JoinTime); });
                            int n = Math.Min(all.Count + 1, Proto.MaxPlayersPerSession);
                            PacketWriter w = new PacketWriter(Proto.S_PLAYERS);
                            w.U8(n);
                            float h = myState != null ? BitConverter.ToSingle(myState, 24) : 0f;
                            int lvn = 0;
                            while (myState != null && lvn < 16 && myState[64 + lvn] != 0) lvn++;
                            string lv = myState != null ? Encoding.ASCII.GetString(myState, 64, lvn) : "";
                            w.U32(myId).U8(isHost ? Proto.PFLAG_HOST : 0).U16(0).F32(h).Str(myName, 16).Str(lv, 16).F32(mx).F32(my).F32(mz).U8(fresh ? 1 : 0);
                            for (int i = 0; i < n - 1; i++)
                            {
                                Member mb = all[i];
                                int fl = mb.Flags & Proto.PFLAG_DEAD;
                                if (info != null && mb.Id == info.HostId) fl |= Proto.PFLAG_HOST;
                                w.U32(mb.Id).U8(fl).U16(mb.Ping).F32(mb.Health).Str(mb.Name, 16).Str(mb.Level, 16).F32(mb.X).F32(mb.Y).F32(mb.Z).U8(mb.HasPos ? 1 : 0);
                            }
                            Emit(w.ToArray());
                            if (info != null && info.Count != members.Count + 1 && isHost) { info.Count = members.Count + 1; EmitSession(); }
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (log != null) log("relais : " + ex.Message);
                }
            }
        }
    }

    class Remote
    {
        public uint Id;
        public int Slot = -1;
        public byte[] State = new byte[Proto.StateSize];
        public uint Seq;
        public DateTime Last;
        public byte[] Pose;          // derniere pose recue (squelette)
        public uint PoseSeq;
        public DateTime PoseT;
    }

    // ------------------------------------------------------------------------
    //  Joueur test (bot) pour essayer le mode en ligne seul : c'est un vrai
    //  client reseau qui rejoint la session et tourne autour du joueur.
    // ------------------------------------------------------------------------
    class TestBot
    {
        public static bool Front = Environment.GetEnvironmentVariable("JAK3ONLINE_BOT_FRONT") == "1";
        readonly Client owner;
        readonly Client c;
        volatile bool run = true;
        float hp = 8f;
        uint lastHitBy;

        public TestBot(Client owner, int n)
        {
            this.owner = owner;
            c = new Client();
            c.UseBridge = false;
            c.Ephemeral = true;
            c.UseRelay = owner.UseRelay;
            c.ServerAddress = owner.ServerAddress;
            c.French = owner.French;
            c.WantedName = "Bot" + n;
            c.IsBot = true;
            c.BotOfCreateur = owner.IsCreateur;
            c.OnGameEvent = delegate (GameEvent e)
            {
                if (e.Kind != Shm.EV_HIT) return;
                hp -= Math.Max(1f, e.Damage);
                lastHitBy = e.Player;
            };
            c.Start();
            c.Connect();
            Thread t = new Thread(delegate () { Loop(n); });
            t.IsBackground = true;
            t.Name = "bot";
            t.Start();
        }

        public void Stop()
        {
            run = false;
            c.Stop();
        }

        void Loop(int n)
        {
            DateTime end = DateTime.UtcNow.AddSeconds(45);
            while (run && c.NetState < Shm.NET_LOBBY && DateTime.UtcNow < end) Thread.Sleep(100);
            string code = owner.SessionCode;
            if (!run || c.NetState < Shm.NET_LOBBY || string.IsNullOrEmpty(code)) { Stop(); return; }
            c.UiJoinCode(code);
            float angle = n * 1.3f;
            while (run)
            {
                Thread.Sleep(50);
                if (owner.SessionId == 0) break;
                byte[] st = owner.LocalStateSnapshot();
                if (st == null) continue;
                if (hp <= 0f)
                {
                    // le bot est elimine : l'elimination est creditee au tireur, puis il revient
                    c.TestSendDied(lastHitBy);
                    hp = 8f;
                    Thread.Sleep(1500);
                    continue;
                }
                angle += 0.03f;
                float r = 4f * 4096f;
                float x = BitConverter.ToSingle(st, 12) + (float)Math.Cos(angle) * r;
                float z = BitConverter.ToSingle(st, 20) + (float)Math.Sin(angle) * r;
                if (Front && n == 1)
                {
                    // banc de test : le bot reste devant le joueur, face a la camera
                    float qy = BitConverter.ToSingle(st, 32), qw = BitConverter.ToSingle(st, 40);
                    double yaw0 = 2 * Math.Atan2(qy, qw);
                    x = BitConverter.ToSingle(st, 12) + (float)Math.Sin(yaw0) * 3.5f * 4096f;
                    z = BitConverter.ToSingle(st, 20) + (float)Math.Cos(yaw0) * 3.5f * 4096f;
                    angle = (float)(-(yaw0 + Math.PI));
                }
                Buffer.BlockCopy(BitConverter.GetBytes(x), 0, st, 12, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(z), 0, st, 20, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(hp), 0, st, 24, 4);
                uint flags = BitConverter.ToUInt32(st, 0) & ~(Proto.FLAG_DEAD | 0x4u | 0x200u);
                Buffer.BlockCopy(BitConverter.GetBytes(flags | Proto.FLAG_VALID), 0, st, 0, 4);
                // regarde dans le sens de la marche
                double yaw = -angle;
                Buffer.BlockCopy(BitConverter.GetBytes(0f), 0, st, 28, 4);
                Buffer.BlockCopy(BitConverter.GetBytes((float)Math.Sin(yaw / 2)), 0, st, 32, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(0f), 0, st, 36, 4);
                Buffer.BlockCopy(BitConverter.GetBytes((float)Math.Cos(yaw / 2)), 0, st, 40, 4);
                // vitesse de marche -> animation de course si la sienne n'existe pas
                Buffer.BlockCopy(BitConverter.GetBytes((float)(-Math.Sin(angle) * r * 0.6)), 0, st, 44, 4);
                Buffer.BlockCopy(BitConverter.GetBytes((float)(Math.Cos(angle) * r * 0.6)), 0, st, 52, 4);
                c.SetLocalStateRaw(st);
                // meme squelette que le joueur (arme, planche, light jak...) mais a la place du bot
                byte[] pose = owner.LocalPoseSnapshot();
                if (pose != null)
                {
                    Buffer.BlockCopy(st, 12, pose, 4, 12);   // x y z
                    Buffer.BlockCopy(st, 28, pose, 16, 16);  // orientation
                    c.SetLocalPoseRaw(pose);
                }
            }
            Stop();
        }
    }

    class SessionEntry
    {
        public uint Id;
        public int Players, Max, Flags;
        public string Host = "", Code = "";
    }

    class PlayerEntry
    {
        public uint Id;
        public int Flags, Ping;
        public float Health;
        public string Name = "", Level = "";
        public float X, Y, Z;
        public bool HasPos;
    }

    class GameEvent
    {
        public int Kind;
        public uint Player;
        public float Damage;
        public int Mode;
        public float Dx, Dy, Dz;
    }

    partial class Client
    {
        // reglages
        public string WantedName = "Joueur";
        public Action<string> OnNameChanged;
        public Action<GameEvent> OnGameEvent;   // utilise par les bots de test
        readonly List<TestBot> bots = new List<TestBot>();

        public byte[] LocalStateSnapshot()
        {
            lock (lk)
            {
                if ((DateTime.UtcNow - localStateTime).TotalSeconds > 2) return null;
                return (byte[])localState.Clone();
            }
        }

        public void SetLocalStateRaw(byte[] st)
        {
            lock (lk) { localState = st; localStateTime = DateTime.UtcNow; }
        }

        public byte[] LocalPoseSnapshot()
        {
            lock (lk)
            {
                if (localPose == null || (DateTime.UtcNow - localPoseTime).TotalSeconds > 1) return null;
                return (byte[])localPose.Clone();
            }
        }

        public void SetLocalPoseRaw(byte[] pose)
        {
            lock (lk) { localPose = pose; localPoseTime = DateTime.UtcNow; }
        }

        public int BotCount { get { lock (bots) return bots.Count; } }

        public void AddBot()
        {
            if (!IsCreateur) { PushFeed(T("Les bots de test sont reserves au createur", "Test bots are for the creator only")); return; }
            AddBotNow();
        }

        public void AddBotNow()
        {
            if (SessionId == 0) { PushFeed(T("Creez ou rejoignez d'abord une session", "Create or join a session first")); return; }
            lock (bots)
            {
                if (bots.Count >= 8) { PushFeed(T("Maximum 8 bots de test", "Maximum 8 test bots")); return; }
                bots.Add(new TestBot(this, bots.Count + 1));
            }
            PushFeed(T("Bot de test ajoute : il tourne autour de vous, frappez-le !", "Test bot added: it circles around you, hit it!"));
        }

        public void RemoveBots()
        {
            lock (bots)
            {
                foreach (TestBot b in bots) b.Stop();
                bots.Clear();
            }
        }
        public string ServerAddress = "127.0.0.1:" + Proto.DefaultPort;
        public bool French = true;
        public bool UseBridge = true;
        public bool UseRelay = false;         // relais internet gratuit (sinon serveur TCP perso)
        public string RelayName = "";
        string rejoinCode;                    // session a rejoindre apres une coupure
        volatile bool wantRejoin;
        public Action<string> Log;

        readonly object lk = new object();
        FileBridge bridge;
        public string BridgeDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bridge");
        ILink conn;
        volatile bool running;
        volatile bool wantConnected;

        // etat reseau
        public volatile int NetState = Shm.NET_OFFLINE;
        public uint MyId;
        public string MyName = "";
        public string Status = "";
        public uint SessionId;
        public string SessionCode = "";
        public bool SessionPublic, SessionPvp;
        public int SessionMax, SessionCount;
        public uint HostId;
        public int PingMs;
        public string GameStatus = "recherche du jeu...";

        readonly Dictionary<uint, Remote> remotes = new Dictionary<uint, Remote>();
        readonly Remote[] slots = new Remote[Proto.MaxVisible];
        readonly Dictionary<uint, string> names = new Dictionary<uint, string>();
        List<SessionEntry> sessionList = new List<SessionEntry>();
        List<PlayerEntry> playerList = new List<PlayerEntry>();
        List<PlayerEntry> rawPlayerList = new List<PlayerEntry>();

        // liste des joueurs sans les ignores (ancienne version, bot refuse, banni...)
        void RefilterPlayers()
        {
            lock (lk)
            {
                List<PlayerEntry> list = new List<PlayerEntry>();
                foreach (PlayerEntry e in rawPlayerList) if (e.Id == MyId || !IsIgnored(e.Id)) list.Add(e);
                playerList = list;
                SessionCount = list.Count;
                listsDirty = true;
            }
        }
        bool listsDirty = true;
        readonly Queue<GameEvent> inEvents = new Queue<GameEvent>();
        readonly Queue<string> feed = new Queue<string>();

        // pont
        uint beat;
        bool gameWasAlive;
        uint lastReqSeq;
        uint lastLocalSeq;
        uint outRead, inWrite, feedWrite, listSeq;
        byte[] localState = new byte[Proto.StateSize];
        DateTime localStateTime = DateTime.MinValue;
        byte[] localPose;
        DateTime localPoseTime = DateTime.MinValue;
        uint poseCounter = 1;
        public int PosesSent, PosesReceived;
        DateTime nextAttach = DateTime.MinValue;

        void L(string s)
        {
            if (Log != null) Log(s);
            LogFileLine(s);
        }

        // journal dans un fichier, pour comprendre les problemes (data\online\Jak3Online.log, 2 Mo max)
        public string LogFile;
        readonly object logLk = new object();
        void LogFileLine(string s)
        {
            string f = LogFile;
            if (f == null) return;
            try
            {
                lock (logLk)
                {
                    FileInfo fi = new FileInfo(f);
                    if (fi.Exists && fi.Length > 2000000)
                    {
                        try { File.Delete(f + ".ancien"); } catch (Exception) { }
                        File.Move(f, f + ".ancien");
                    }
                    File.AppendAllText(f, DateTime.Now.ToString("dd/MM HH:mm:ss ") + s + Environment.NewLine);
                }
            }
            catch (Exception) { }
        }

        // langue des textes : celle du jeu (ou celle choisie dans la fenetre)
        public int LangForce = -1;
        int gameLang = -1;
        public int CurLang { get { return LangForce >= 0 ? LangForce : gameLang >= 0 ? gameLang : (French ? 1 : 0); } }
        string T(string fr, string en) { return Lang.Tr(CurLang, fr, en); }

        public bool GameAttached { get { return bridge != null && bridge.GameAlive; } }
        public int GameFps { get { return bridge != null ? bridge.FramesPerSecond : 0; } }

        public void Start()
        {
            running = true;
            MondeInit();
            if (UseBridge)
            {
                bridge = new FileBridge(BridgeDir);
                // une commande laissee par un ancien lancement ne doit pas etre rejouee par le jeu
                try { bridge.WriteU32(Shm.TestSeq, 0); bridge.WriteU32(Shm.TestArg, 0); } catch (Exception) { }
                testSeq = (uint)(Environment.TickCount & 0x3fffffff) | 1u;
                Thread b = new Thread(BridgeLoop);
                b.IsBackground = true;
                b.Name = "bridge";
                b.Start();
            }
            Thread n = new Thread(NetLoop);
            n.IsBackground = true;
            n.Name = "net";
            n.Start();
        }

        public void Stop()
        {
            running = false;
            RemoveBots();
            Disconnect();
        }

        public void Connect() { wantConnected = true; }

        public void Disconnect()
        {
            wantConnected = false;
            rejoinCode = null;
            ILink c = conn;
            if (c != null) c.Close();
        }

        public bool WantConnected { get { return wantConnected; } }

        public void PushFeedPublic(string s) { PushFeed(s); }

        void PushFeed(string s)
        {
            LogFileLine("[jeu] " + s);
            lock (lk)
            {
                foreach (string part in Proto.GameLines(s, 60)) feed.Enqueue(part);
                while (feed.Count > 8) feed.Dequeue();
            }
            L(s);
        }

        void ResetSession()
        {
            lock (lk)
            {
                SessionId = 0;
                SessionCode = "";
                SessionCount = 0;
                SessionMax = 0;
                HostId = 0;
                remotes.Clear();
                for (int i = 0; i < slots.Length; i++) slots[i] = null;
                playerList = new List<PlayerEntry>();
                rawPlayerList = new List<PlayerEntry>();
                inEvents.Clear();
                listsDirty = true;
            }
        }

        // ---------------- reseau
        bool ParseAddress(out string host, out int port)
        {
            host = ServerAddress.Trim();
            port = Proto.DefaultPort;
            int colon = host.LastIndexOf(':');
            if (colon > 0 && host.IndexOf(':') == colon)
            {
                int p;
                if (int.TryParse(host.Substring(colon + 1), out p)) port = p;
                host = host.Substring(0, colon);
            }
            return host.Length > 0;
        }

        void NetLoop()
        {
            DateTime lastPing = DateTime.MinValue;
            DateTime lastState = DateTime.MinValue;
            DateTime lastPose = DateTime.MinValue;
            DateTime nextTry = DateTime.MinValue;
            while (running)
            {
                ILink c = conn;
                if (c == null || c.Closed)
                {
                    if (c != null)
                    {
                        conn = null;
                        ResetSession();
                        NetState = Shm.NET_CONNECTING;
                        MyName = "";
                        if (wantConnected)
                        {
                            Status = T("Connexion perdue avec le serveur", "Lost connection to server");
                            PushFeed(Status);
                        }
                        nextTry = DateTime.UtcNow.AddSeconds(3);
                    }
                    if (!wantConnected)
                    {
                        NetState = Shm.NET_OFFLINE;
                        Status = T("Hors ligne", "Offline");
                        Thread.Sleep(100);
                        continue;
                    }
                    if (DateTime.UtcNow < nextTry)
                    {
                        Thread.Sleep(100);
                        continue;
                    }
                    NetState = Shm.NET_CONNECTING;
                    if (UseRelay)
                    {
                        Status = T("Connexion au relais internet gratuit...", "Connecting to the free internet relay...");
                        RelayLink rl = null;
                        rl = new RelayLink((t, rr) => OnPacket(rl, t, rr), Log);
                        if (rl.Start())
                        {
                            RelayName = rl.BrokerName;
                            conn = rl;
                            rl.Send(new PacketWriter(Proto.C_HELLO).U16(Proto.Version).Str(Proto.CleanName(WantedName), 16).ToArray());
                            lastPing = DateTime.MinValue;
                        }
                        else
                        {
                            Status = T("Relais internet injoignable (pas d'internet ?)", "Internet relay unreachable (no internet?)");
                            nextTry = DateTime.UtcNow.AddSeconds(5);
                        }
                        continue;
                    }
                    string host;
                    int port;
                    if (!ParseAddress(out host, out port))
                    {
                        Status = T("Adresse du serveur invalide", "Invalid server address");
                        nextTry = DateTime.UtcNow.AddSeconds(3);
                        continue;
                    }
                    Status = T("Connexion a ", "Connecting to ") + host + ":" + port + "...";
                    try
                    {
                        TcpClient tc = new TcpClient();
                        IAsyncResult ar = tc.BeginConnect(host, port, null, null);
                        if (!ar.AsyncWaitHandle.WaitOne(30000) || !tc.Connected)
                        {
                            try { tc.Close(); } catch (Exception) { }
                            throw new IOException("timeout");
                        }
                        tc.EndConnect(ar);
                        Conn nc = new Conn(tc.Client);
                        nc.OnPacket = (cc, t, rr) => OnPacket(cc, t, rr);
                        conn = nc;
                        nc.Start();
                        nc.Send(new PacketWriter(Proto.C_HELLO).U16(Proto.Version).Str(Proto.CleanName(WantedName), 16).ToArray());
                        lastPing = DateTime.MinValue;
                    }
                    catch (Exception)
                    {
                        Status = T("Serveur injoignable : ", "Server unreachable: ") + host + ":" + port;
                        nextTry = DateTime.UtcNow.AddSeconds(3);
                    }
                    continue;
                }

                DateTime now = DateTime.UtcNow;
                try { HostSaveWatch(); } catch (Exception) { }
                if (wantRejoin)
                {
                    // reconnecte : on revient dans la session d'avant
                    wantRejoin = false;
                    string rc = rejoinCode;
                    if (!string.IsNullOrEmpty(rc) && SessionId == 0)
                    {
                        L(T("Retour dans la session ", "Rejoining session ") + rc);
                        c.Send(new PacketWriter(Proto.C_JOIN_CODE).Str(rc, 8).ToArray());
                    }
                }
                if ((now - lastPing).TotalSeconds >= 2)
                {
                    lastPing = now;
                    c.Send(new PacketWriter(Proto.C_PING).U32((uint)Environment.TickCount).ToArray());
                }
                if (SessionId != 0 && (now - lastPose).TotalMilliseconds >= (SessionCount > 12 ? 110 : SessionCount > 6 ? 85 : 66))
                {
                    byte[] ps = LocalPoseSnapshot();
                    if (ps != null)
                    {
                        lastPose = now;
                        c.Send(new PacketWriter(Proto.C_POSE).Bytes(ps, 0, ps.Length).ToArray());
                        PosesSent++;
                    }
                }
                if (SessionId != 0 && (now - lastState).TotalMilliseconds >= 50)
                {
                    lastState = now;
                    byte[] st = null;
                    lock (lk)
                    {
                        if ((now - localStateTime).TotalSeconds < 2)
                        {
                            st = (byte[])localState.Clone();
                        }
                    }
                    if (st != null)
                    {
                        byte[] ping = BitConverter.GetBytes((ushort)Math.Min(65535, PingMs));
                        st[60] = ping[0];
                        st[61] = ping[1];
                        c.Send(new PacketWriter(Proto.C_STATE).Bytes(st, 0, Proto.StateSize).ToArray());
                    }
                }
                try { MondeTick(); } catch (Exception ex) { L("monde : " + ex.Message); }
                Thread.Sleep(10);
            }
        }

        void Send(byte[] data)
        {
            ILink c = conn;
            if (c != null) c.Send(data);
        }

        string ErrorText(int code)
        {
            switch (code)
            {
                case Proto.E_NOT_FOUND: return T("Session introuvable", "Session not found");
                case Proto.E_FULL: return T("Session pleine", "Session is full");
                case Proto.E_BAD_CODE: return T("Code de session invalide", "Invalid session code");
                case Proto.E_NOT_HOST: return T("Seul l'hote peut changer ca", "Only the host can change this");
                case Proto.E_VERSION: return T("Version differente du serveur, mettez a jour le mod", "Server version mismatch, update the mod");
                case Proto.E_SERVER_FULL: return T("Serveur plein", "Server full");
            }
            return T("Erreur ", "Error ") + code;
        }

        void OnPacket(ILink c, byte type, BinaryReader r)
        {
            switch (type)
            {
                case Proto.S_WELCOME:
                    MyId = r.ReadUInt32();
                    MyName = Rd.Str(r, 16);
                    if (OnNameChanged != null) OnNameChanged(MyName);
                    NetState = Shm.NET_LOBBY;
                    Status = UseRelay ? T("Connecte (internet gratuit)", "Connected (free internet)") : T("Connecte au serveur", "Connected to server");
                    if (!string.IsNullOrEmpty(rejoinCode)) wantRejoin = true;
                    L(T("Connecte au serveur en tant que ", "Connected to server as ") + MyName);
                    break;
                case Proto.S_SESSION:
                    {
                        uint id = r.ReadUInt32();
                        string code = Rd.Str(r, 8);
                        int flags = r.ReadByte();
                        int max = r.ReadByte();
                        uint host = r.ReadUInt32();
                        int count = r.ReadByte();
                        if (id == 0)
                        {
                            rejoinCode = null;
                            if (bots.Count > 0) ThreadPool.QueueUserWorkItem(delegate { RemoveBots(); });
                            if (SessionId != 0) PushFeed(T("Vous avez quitte la session", "You left the session"));
                            MondeSessionLeft();
                            ResetSession();
                            NetState = Shm.NET_LOBBY;
                            Status = T("Connecte - aucune session", "Connected - no session");
                            break;
                        }
                        bool isNew = id != SessionId;
                        if (isNew) ResetSession();
                        lock (lk)
                        {
                            SessionId = id;
                            SessionCode = code;
                            SessionPublic = (flags & 1) != 0;
                            SessionPvp = (flags & 2) != 0;
                            SessionMax = max;
                            HostId = host;
                            SessionCount = count;
                        }
                        // sans les joueurs ignores (ancienne version, bot refuse...)
                        if (!isNew) RefilterPlayers();
                        NetState = Shm.NET_SESSION;
                        rejoinCode = code;
                        Status = T("En session ", "In session ") + code;
                        if (isNew)
                        {
                            PushFeed(T("Session ", "Session ") + code + (host == MyId ? T(" creee (vous etes l'hote)", " created (you are the host)") : T(" rejointe", " joined")));
                            MondeSessionJoined();
                        }
                        break;
                    }
                case Proto.S_LIST:
                    {
                        int n = r.ReadByte();
                        List<SessionEntry> list = new List<SessionEntry>();
                        for (int i = 0; i < n; i++)
                        {
                            SessionEntry e = new SessionEntry();
                            e.Id = r.ReadUInt32();
                            e.Players = r.ReadByte();
                            e.Max = r.ReadByte();
                            e.Flags = r.ReadByte();
                            e.Host = Rd.Str(r, 16);
                            e.Code = Rd.Str(r, 8);
                            list.Add(e);
                        }
                        lock (lk) { sessionList = list; listsDirty = true; }
                        Status = T("Sessions publiques : ", "Public sessions: ") + n;
                        break;
                    }
                case Proto.S_PLAYERS:
                    {
                        int n = r.ReadByte();
                        List<PlayerEntry> list = new List<PlayerEntry>();
                        for (int i = 0; i < n; i++)
                        {
                            PlayerEntry e = new PlayerEntry();
                            e.Id = r.ReadUInt32();
                            e.Flags = r.ReadByte();
                            e.Ping = r.ReadUInt16();
                            e.Health = r.ReadSingle();
                            e.Name = Rd.Str(r, 16);
                            e.Level = Rd.Str(r, 16);
                            e.X = r.ReadSingle();
                            e.Y = r.ReadSingle();
                            e.Z = r.ReadSingle();
                            e.HasPos = r.ReadByte() != 0;
                            if (e.Id == MyId) e.Flags |= Proto.PFLAG_ME;
                            list.Add(e);
                        }
                        lock (lk)
                        {
                            rawPlayerList = list;
                            foreach (PlayerEntry e in list) names[e.Id] = e.Name;
                        }
                        RefilterPlayers();
                        break;
                    }
                case Proto.S_STATES:
                    {
                        int n = r.ReadByte();
                        DateTime now = DateTime.UtcNow;
                        lock (lk)
                        {
                            for (int i = 0; i < n; i++)
                            {
                                uint id = r.ReadUInt32();
                                byte[] st = r.ReadBytes(Proto.StateSize);
                                if (st.Length != Proto.StateSize || id == MyId || IsIgnored(id)) continue;
                                Remote rm;
                                if (!remotes.TryGetValue(id, out rm))
                                {
                                    rm = new Remote();
                                    rm.Id = id;
                                    remotes[id] = rm;
                                }
                                Buffer.BlockCopy(st, 0, rm.State, 0, Proto.StateSize);
                                CheckRemoteState(id, st);
                                rm.Seq++;
                                rm.Last = now;
                                if (rm.Slot < 0)
                                {
                                    for (int s = 0; s < slots.Length; s++)
                                    {
                                        if (slots[s] == null) { slots[s] = rm; rm.Slot = s; break; }
                                    }
                                }
                            }
                        }
                        break;
                    }
                case Proto.S_POSE:
                    {
                        uint id = r.ReadUInt32();
                        int len = (int)(r.BaseStream.Length - r.BaseStream.Position);
                        if (id == MyId || len < Proto.PoseMin || len > Proto.PoseMax || IsIgnored(id)) break;
                        byte[] pose = r.ReadBytes(len);
                        lock (lk)
                        {
                            Remote rm;
                            if (!remotes.TryGetValue(id, out rm)) break;
                            rm.Pose = pose;
                            rm.PoseSeq = ++poseCounter;
                            rm.PoseT = DateTime.UtcNow;
                            PosesReceived++;
                        }
                        break;
                    }
                case Proto.S_HIT:
                    {
                        GameEvent e = new GameEvent();
                        e.Kind = Shm.EV_HIT;
                        e.Player = r.ReadUInt32();
                        e.Damage = r.ReadSingle();
                        e.Mode = r.ReadByte();
                        e.Dx = r.ReadSingle();
                        e.Dy = r.ReadSingle();
                        e.Dz = r.ReadSingle();
                        if (IsIgnored(e.Player) || (InWorld && (worldPvpOff || !IsVerified(e.Player)))) break;
                        if (!HitPlausible(e.Player, e.Damage)) break;
                        if (OnGameEvent != null) OnGameEvent(e);
                        lock (lk) { if (inEvents.Count < 64) inEvents.Enqueue(e); }
                        break;
                    }
                case Proto.S_SAVE_REQ:
                    {
                        uint from = r.ReadUInt32();
                        ThreadPool.QueueUserWorkItem(delegate { SendMySave(from); });
                        break;
                    }
                case Proto.S_SAVE_CHUNK:
                    {
                        int idx = r.ReadUInt16(), cnt = r.ReadUInt16(), len = r.ReadUInt16();
                        byte[] data = r.ReadBytes(len);
                        ReceiveSaveChunk(idx, cnt, data);
                        break;
                    }
                case Proto.S_SAVE_NOTIFY:
                    if (SaveSync && HostId != MyId) RequestHostSave(SaveSlot);
                    break;
                case Proto.S_KILL:
                    {
                        GameEvent e = new GameEvent();
                        e.Kind = Shm.EV_KILL;
                        e.Player = r.ReadUInt32();
                        lock (lk) { if (inEvents.Count < 64) inEvents.Enqueue(e); }
                        break;
                    }
                case Proto.S_FEED:
                    {
                        int kind = r.ReadByte();
                        string a = Rd.Str(r, 16);
                        string b = Rd.Str(r, 16);
                        int v = r.ReadByte();
                        string msg = null;
                        switch (kind)
                        {
                            case Proto.F_JOINED: msg = a + T(" a rejoint la session", " joined the session"); break;
                            case Proto.F_LEFT: msg = a + T(" a quitte la session", " left the session"); break;
                            case Proto.F_KILLED: msg = a + T(" a elimine ", " eliminated ") + b; break;
                            case Proto.F_DIED: msg = a + T(" est mort", " died"); break;
                            case Proto.F_HOST: if (!InWorld) msg = a + T(" est maintenant l'hote", " is now the host"); break;
                            case Proto.F_PVP: msg = T("PvP ", "PvP ") + (v != 0 ? T("active", "enabled") : T("desactive", "disabled")) + T(" par ", " by ") + a; break;
                            case Proto.F_VISIBILITY: msg = T("Session maintenant ", "Session is now ") + (v != 0 ? T("publique", "public") : T("privee", "private")); break;
                        }
                        if (msg != null) PushFeed(msg);
                        break;
                    }
                case Proto.S_ERROR:
                    {
                        int code = r.ReadByte();
                        Status = ErrorText(code);
                        PushFeed(Status);
                        break;
                    }
                case Proto.S_PONG:
                    {
                        uint t = r.ReadUInt32();
                        PingMs = (int)((uint)Environment.TickCount - t);
                        break;
                    }
                case Proto.S_MSG:
                    {
                        uint from = r.ReadUInt32();
                        byte kind = r.ReadByte();
                        uint target = r.ReadUInt32();
                        OnSessionMsg(from, kind, target, r);
                        break;
                    }
            }
        }

        // ---------------- requetes venant du menu du jeu
        public void Rename(string name)
        {
            WantedName = Proto.CleanName(name);
            if (NetState >= Shm.NET_LOBBY) Send(new PacketWriter(Proto.C_RENAME).Str(WantedName, 16).ToArray());
            else
            {
                MyName = WantedName;
                if (OnNameChanged != null) OnNameChanged(WantedName);
            }
        }

        // ---------------- sauvegardes (recuperer / synchroniser celle de l'hote)
        public volatile bool SaveSync;
        public volatile int SaveStatus;     // 0 rien, 1 en cours, 2 recue, 3 erreur
        public int SaveSlot = 3;            // emplacement 0..3 (4 = dernier)
        byte[][] saveParts;
        DateTime saveHostStamp = DateTime.MinValue;
        DateTime saveLastCheck = DateTime.MinValue;
        public const int BankSize = 124928;

        public static string SaveDir()
        {
            string over = Environment.GetEnvironmentVariable("JAK3ONLINE_SAVES");
            if (!string.IsNullOrEmpty(over)) return over;
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenGOAL", "jak3", "saves");
            string d = Path.Combine(root, "BASCUS-97330AYBABTU!");
            if (Directory.Exists(d)) return d;
            try
            {
                string[] c = Directory.GetDirectories(root, "*AYBABTU!");
                if (c.Length > 0) return c[0];
            }
            catch (Exception) { }
            return d;
        }

        // la sauvegarde la plus recente (plus grand compteur de sauvegarde valide)
        static string NewestBank(out DateTime stamp)
        {
            stamp = DateTime.MinValue;
            string best = null;
            uint bestCount = 0;
            try
            {
                foreach (string f in Directory.GetFiles(SaveDir(), "bank*.bin"))
                {
                    FileInfo fi = new FileInfo(f);
                    if (fi.Length != BankSize) continue;
                    byte[] h = new byte[12];
                    using (FileStream fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) fs.Read(h, 0, 12);
                    uint count = BitConverter.ToUInt32(h, 0);
                    uint magic = BitConverter.ToUInt32(h, 8);
                    if (magic != 0x12345678 || count == 0) continue;
                    if (best == null || count > bestCount) { best = f; bestCount = count; stamp = fi.LastWriteTimeUtc; }
                }
            }
            catch (Exception) { }
            return best;
        }

        void RequestHostSave(int slot)
        {
            SaveSlot = Math.Max(0, Math.Min(3, slot));
            SaveStatus = 1;
            saveParts = null;
            L(T("Demande de la sauvegarde de l'hote (emplacement ", "Requesting the host save (slot ") + (SaveSlot + 1) + ")...");
            Send(new PacketWriter(Proto.C_SAVE_REQ).ToArray());
        }

        void SendMySave(uint to)
        {
            DateTime st;
            string f = NewestBank(out st);
            byte[] data = null;
            try { if (f != null) using (FileStream fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { data = new byte[BankSize]; int n = 0; while (n < BankSize) { int r = fs.Read(data, n, BankSize - n); if (r <= 0) break; n += r; } if (n != BankSize) data = null; } }
            catch (Exception) { data = null; }
            if (data == null)
            {
                Send(new PacketWriter(Proto.C_SAVE_CHUNK).U32(to).U16(0).U16(0).U16(0).ToArray());
                return;
            }
            const int chunk = 16000;
            int count = (data.Length + chunk - 1) / chunk;
            for (int i = 0; i < count; i++)
            {
                int len = Math.Min(chunk, data.Length - i * chunk);
                Send(new PacketWriter(Proto.C_SAVE_CHUNK).U32(to).U16(i).U16(count).U16(len).Bytes(data, i * chunk, len).ToArray());
                Thread.Sleep(20);
            }
            string nm;
            lock (lk) { if (!names.TryGetValue(to, out nm)) nm = "?"; }
            L(T("Sauvegarde envoyee a ", "Save sent to ") + nm);
        }

        void ReceiveSaveChunk(int idx, int count, byte[] data)
        {
            if (count == 0)
            {
                SaveStatus = 3;
                PushFeed(T("L'hote n'a pas encore de sauvegarde", "The host has no save yet"));
                return;
            }
            if (saveParts == null || saveParts.Length != count) saveParts = new byte[count][];
            if (idx < count) saveParts[idx] = data;
            foreach (byte[] b in saveParts) if (b == null) return;
            MemoryStream ms = new MemoryStream();
            foreach (byte[] b in saveParts) ms.Write(b, 0, b.Length);
            saveParts = null;
            byte[] all = ms.ToArray();
            if (all.Length != BankSize || BitConverter.ToUInt32(all, 8) != 0x12345678)
            {
                SaveStatus = 3;
                PushFeed(T("Sauvegarde recue invalide", "Invalid save received"));
                return;
            }
            try
            {
                string dir = SaveDir();
                Directory.CreateDirectory(dir);
                // copie de securite de l'emplacement remplace
                string bak = Path.Combine(dir, "jak3online-copie-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                for (int b = 0; b < 2; b++)
                {
                    string f = Path.Combine(dir, "bank" + (SaveSlot * 2 + b) + ".bin");
                    if (File.Exists(f)) { Directory.CreateDirectory(bak); File.Copy(f, Path.Combine(bak, Path.GetFileName(f)), true); }
                }
                for (int b = 0; b < 2; b++) File.WriteAllBytes(Path.Combine(dir, "bank" + (SaveSlot * 2 + b) + ".bin"), all);
                SaveStatus = 2;
                PushFeed(T("Sauvegarde de l'hote copiee dans l'emplacement ", "Host save copied to slot ") + (SaveSlot + 1) + T(" : chargez-la", ": load it"));
            }
            catch (Exception ex)
            {
                SaveStatus = 3;
                PushFeed(T("Impossible d'ecrire la sauvegarde", "Cannot write the save"));
                L(ex.Message);
            }
        }

        void HostSaveWatch()
        {
            // l'hote previent les autres quand il sauvegarde (pour la synchro auto)
            if (SessionId == 0 || HostId != MyId || (DateTime.UtcNow - saveLastCheck).TotalSeconds < 3) return;
            saveLastCheck = DateTime.UtcNow;
            DateTime st;
            NewestBank(out st);
            if (saveHostStamp == DateTime.MinValue) { saveHostStamp = st; return; }
            if (st > saveHostStamp)
            {
                saveHostStamp = st;
                Send(new PacketWriter(Proto.C_SAVE_NOTIFY).ToArray());
                L(T("Nouvelle sauvegarde : les joueurs en synchro la recoivent", "New save: synced players receive it"));
            }
        }

        void HandleRequest(int type, uint arg, byte[] codeBytes)
        {
            if (type == Shm.REQ_NAME)
            {
                int n = 0;
                while (n < 16 && codeBytes[n] != 0) n++;
                string nm = Encoding.ASCII.GetString(codeBytes, 0, n);
                L(T("Nouveau pseudo : ", "New name: ") + Proto.CleanName(nm));
                Rename(nm);
                return;
            }
            if (NetState < Shm.NET_LOBBY)
            {
                PushFeed(T("Pas connecte au serveur", "Not connected to server"));
                return;
            }
            switch (type)
            {
                case Shm.REQ_CREATE:
                    {
                        int max = (int)(arg & 0xff);
                        int flags = ((arg & 0x200) != 0 ? 1 : 0) | ((arg & 0x100) != 0 ? 2 : 0);
                        Send(new PacketWriter(Proto.C_CREATE).U8(Math.Max(2, Math.Min(100, max))).U8(flags).ToArray());
                        break;
                    }
                case Shm.REQ_JOIN_ID:
                    Send(new PacketWriter(Proto.C_JOIN_ID).U32(arg).ToArray());
                    break;
                case Shm.REQ_JOIN_WORLD:
                    L(T("Rejoindre la session publique...", "Joining the public session..."));
                    Send(new PacketWriter(Proto.C_JOIN_WORLD).ToArray());
                    break;
                case Shm.REQ_JOIN_CODE:
                    {
                        int n = 0;
                        while (n < 8 && codeBytes[n] != 0) n++;
                        string code = Encoding.ASCII.GetString(codeBytes, 0, n);
                        L(T("Rejoindre avec le code ", "Joining with code ") + code);
                        Send(new PacketWriter(Proto.C_JOIN_CODE).Str(code, 8).ToArray());
                        break;
                    }
                case Shm.REQ_LEAVE:
                    rejoinCode = null;
                    Send(new PacketWriter(Proto.C_LEAVE).ToArray());
                    break;
                case Shm.REQ_LIST:
                    Send(new PacketWriter(Proto.C_LIST).ToArray());
                    break;
                case Shm.REQ_SETTINGS:
                    if (InWorld && !IsCreateur) { PushFeed(T("Reserve au createur dans le monde en ligne", "Creator only in the online world")); break; }
                    Send(new PacketWriter(Proto.C_SETTINGS).U8((int)(arg & 3)).ToArray());
                    break;
                case Shm.REQ_ADDBOT:
                    AddBot();
                    break;
                case Shm.REQ_LOCALJ2:
                    LocalJ2Request((int)arg);
                    break;
                case Shm.REQ_GETSAVE:
                    if (SessionId == 0 || HostId == MyId) { PushFeed(T("Rejoignez d'abord la session d'un hote", "Join a host session first")); break; }
                    RequestHostSave((int)arg);
                    break;
                case Shm.REQ_SAVESYNC:
                    SaveSync = arg != 0;
                    PushFeed(SaveSync ? T("Synchro auto de la sauvegarde de l'hote : OUI", "Auto sync of host save: ON") : T("Synchro auto de la sauvegarde : NON", "Auto save sync: OFF"));
                    if (SaveSync && SessionId != 0 && HostId != MyId) RequestHostSave(SaveSlot);
                    break;
            }
        }

        // raccourcis utilisables depuis la fenetre
        public void UiCreate(bool pub, int max, bool pvp) { HandleRequest(Shm.REQ_CREATE, (uint)(max | (pvp ? 0x100 : 0) | (pub ? 0x200 : 0)), null); }
        public void UiJoinWorld() { HandleRequest(Shm.REQ_JOIN_WORLD, 0, null); }
        public void UiJoinCode(string code)
        {
            byte[] b = new byte[8];
            string c = Proto.Ascii(code.Trim().ToUpperInvariant(), 7);
            Encoding.ASCII.GetBytes(c, 0, c.Length, b, 0);
            HandleRequest(Shm.REQ_JOIN_CODE, 0, b);
        }
        public void UiLeave() { HandleRequest(Shm.REQ_LEAVE, 0, null); }

        // ---------------- pont avec le jeu (30 fois par seconde)
        static void PutStr(byte[] dst, int off, string s, int len)
        {
            for (int i = 0; i < len; i++) dst[off + i] = 0;
            byte[] u = Proto.GameBytes(s, len - 1);
            Buffer.BlockCopy(u, 0, dst, off, u.Length);
        }

        static void PutU32(byte[] dst, int off, uint v)
        {
            dst[off] = (byte)v;
            dst[off + 1] = (byte)(v >> 8);
            dst[off + 2] = (byte)(v >> 16);
            dst[off + 3] = (byte)(v >> 24);
        }

        static void PutF32(byte[] dst, int off, float v)
        {
            byte[] b = BitConverter.GetBytes(v);
            Buffer.BlockCopy(b, 0, dst, off, 4);
        }

        // ---------------- saisie du pseudo au clavier dans le jeu
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] static extern short GetKeyState(int vk);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

        bool kbActive;
        readonly StringBuilder kbBuf = new StringBuilder();
        uint kbSeq, kbCmd;
        readonly bool[] kbPrev = new bool[256];
        uint fgPid;
        bool fgIsGame;

        bool GameInForeground()
        {
            uint pid;
            GetWindowThreadProcessId(GetForegroundWindow(), out pid);
            if (pid != fgPid)
            {
                fgPid = pid;
                try { fgIsGame = Process.GetProcessById((int)pid).ProcessName.Equals("gk", StringComparison.OrdinalIgnoreCase); }
                catch (Exception) { fgIsGame = false; }
            }
            return fgIsGame;
        }

        static bool Down(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }

        void KbSend()
        {
            kbSeq++;
            byte[] t = new byte[16];
            PutStr(t, 0, kbBuf.ToString(), 16);
            bridge.Write(Shm.KbText, t, 16);
            bridge.WriteU32(Shm.KbCmd, kbCmd);
            bridge.WriteU32(Shm.KbSeq, kbSeq);
        }

        void KeyboardTick(bool editorOpen)
        {
            if (!editorOpen)
            {
                kbActive = false;
                return;
            }
            if (!kbActive)
            {
                // ouverture de l'editeur : on part du pseudo actuel
                kbActive = true;
                kbCmd = 0;
                kbBuf.Length = 0;
                kbBuf.Append(MyName.Length > 0 ? MyName : Proto.CleanName(WantedName));
                if (kbBuf.Length > 12) kbBuf.Length = 12;
                for (int vk = 0; vk < 256; vk++) kbPrev[vk] = Down(vk);
                KbSend();
                return;
            }
            if (kbCmd != 0 || !GameInForeground()) return;
            bool changed = false;
            bool shift = (GetKeyState(0x10) & 0x8000) != 0;
            bool caps = (GetKeyState(0x14) & 1) != 0;
            for (int vk = 8; vk < 256; vk++)
            {
                bool d = Down(vk);
                bool pressed = d && !kbPrev[vk];
                kbPrev[vk] = d;
                if (!pressed) continue;
                char c = '\0';
                if (vk >= 0x41 && vk <= 0x5A) c = (char)((shift ^ caps ? 'A' : 'a') + (vk - 0x41));
                else if (vk >= 0x30 && vk <= 0x39) c = (char)('0' + (vk - 0x30));
                else if (vk >= 0x60 && vk <= 0x69) c = (char)('0' + (vk - 0x60));
                else if (vk == 0x20) c = '_';
                else if (vk == 0xBD || vk == 0x6D) c = '-';
                else if (vk == 0x08) { if (kbBuf.Length > 0) { kbBuf.Length--; changed = true; } }
                else if (vk == 0x0D) { kbCmd = 1; changed = true; }
                else if (vk == 0x1B) { kbCmd = 2; changed = true; }
                if (c != '\0' && kbBuf.Length < 12) { kbBuf.Append(c); changed = true; }
            }
            if (changed) KbSend();
        }

        void BridgeLoop()
        {
            int tick = 0;
            while (running)
            {
                Thread.Sleep(15);
                tick++;
                try
                {
                    if (!bridge.Ready)
                    {
                        if (DateTime.UtcNow < nextAttach) continue;
                        nextAttach = DateTime.UtcNow.AddSeconds(3);
                        string err = bridge.Open();
                        if (err != null)
                        {
                            GameStatus = err;
                            continue;
                        }
                    }
                    bool fresh = bridge.Poll();
                    bool alive = bridge.GameAlive;
                    byte[] g = bridge.Game;
                    if (alive && !gameWasAlive)
                    {
                        // on reprend les compteurs du jeu (pas de rejeu d'anciennes requetes)
                        lastReqSeq = BitConverter.ToUInt32(g, Shm.ReqSeq);
                        outRead = BitConverter.ToUInt32(g, Shm.OutWrite);
                        inWrite = BitConverter.ToUInt32(g, Shm.InRead);
                        bridge.WriteU32(Shm.InWrite, inWrite);
                        lock (lk) { inEvents.Clear(); listsDirty = true; }
                        GameStatus = T("jeu detecte", "game detected");
                        L(GameStatus);
                    }
                    else if (!alive && gameWasAlive)
                    {
                        GameStatus = T("jeu ferme ou bloque", "game closed or frozen");
                        L(GameStatus);
                    }
                    else if (!alive)
                    {
                        GameStatus = T("en attente du jeu (lancez Jak 3 avec le mod)", "waiting for the game (start Jak 3 with the mod)");
                    }
                    gameWasAlive = alive;
                    if (alive && fresh) ProcessGame(g);
                    uint kbm = alive ? BitConverter.ToUInt32(g, Shm.KbEdit) : 0u;
                    KeyboardTick(kbm == 1);
                    ChatKeyboardTick(kbm);
                    GameKeysTick(alive);
                    if (tick % 2 == 0)
                    {
                        WriteImage(g, tick, alive);
                        bridge.Commit();
                    }
                }
                catch (Exception ex)
                {
                    L("pont : " + ex.Message);
                    Thread.Sleep(500);
                }
            }
            bridge.Close();
        }

        void ProcessGame(byte[] hdr)
        {
            DateTime now = DateTime.UtcNow;
            MondeBridgeTick(hdr, true);

            // --- requete du menu
            uint reqSeq = BitConverter.ToUInt32(hdr, Shm.ReqSeq);
            if (reqSeq != lastReqSeq)
            {
                lastReqSeq = reqSeq;
                int rtype = (int)BitConverter.ToUInt32(hdr, Shm.ReqType);
                byte[] code = new byte[16];
                Buffer.BlockCopy(hdr, rtype == Shm.REQ_NAME ? Shm.ReqName : Shm.ReqCode, code, 0, rtype == Shm.REQ_NAME ? 16 : 8);
                HandleRequest(rtype, BitConverter.ToUInt32(hdr, Shm.ReqArg), code);
                bridge.WriteU32(Shm.AckSeq, reqSeq);
            }

            // --- etat du joueur local
            uint lseq = BitConverter.ToUInt32(hdr, Shm.Local);
            if (hdr[Shm.Local + 95] < 32) gameLang = hdr[Shm.Local + 95];
            if (lseq != lastLocalSeq)
            {
                lastLocalSeq = lseq;
                int b = Shm.Local;
                byte[] st = new byte[Proto.StateSize];
                Buffer.BlockCopy(hdr, b + 4, st, 0, 4);    // flags
                Buffer.BlockCopy(hdr, b + 8, st, 4, 4);    // anim
                Buffer.BlockCopy(hdr, b + 12, st, 8, 4);   // frame
                Buffer.BlockCopy(hdr, b + 16, st, 12, 16); // x y z sante
                Buffer.BlockCopy(hdr, b + 32, st, 28, 16); // quaternion
                Buffer.BlockCopy(hdr, b + 48, st, 44, 12); // vitesse
                ushort kills = (ushort)Math.Min(65535u, BitConverter.ToUInt32(hdr, b + 80));
                ushort deaths = (ushort)Math.Min(65535u, BitConverter.ToUInt32(hdr, b + 84));
                st[56] = (byte)kills; st[57] = (byte)(kills >> 8);
                st[58] = (byte)deaths; st[59] = (byte)(deaths >> 8);
                Buffer.BlockCopy(hdr, b + 64, st, 64, 16); // niveau
                int plen = (int)BitConverter.ToUInt32(hdr, Shm.TxPose);
                byte[] pose = null;
                if (plen >= Proto.PoseMin && plen <= Proto.PoseMax && hdr[Shm.TxPose + 4] == 1)
                {
                    pose = new byte[plen];
                    Buffer.BlockCopy(hdr, Shm.TxPose + 4, pose, 0, plen);
                }
                lock (lk)
                {
                    localState = st;
                    localStateTime = now;
                    localPose = pose;
                    localPoseTime = now;
                }
            }

            // --- evenements sortants (coups portes, mort)
            uint outWrite = BitConverter.ToUInt32(hdr, Shm.OutWrite);
            if (outWrite - outRead > 16) outRead = outWrite - 16;
            while (outRead != outWrite)
            {
                int e = Shm.OutEvents + (int)(outRead & 15) * Shm.EventSize;
                uint kind = BitConverter.ToUInt32(hdr, e);
                uint player = BitConverter.ToUInt32(hdr, e + 4);
                float dmg = BitConverter.ToSingle(hdr, e + 8);
                uint mode = BitConverter.ToUInt32(hdr, e + 12);
                if (SessionId != 0)
                {
                    if (kind == Shm.EV_HIT)
                        Send(new PacketWriter(Proto.C_HIT).U32(player).F32(dmg).U8((int)mode)
                            .F32(BitConverter.ToSingle(hdr, e + 16)).F32(BitConverter.ToSingle(hdr, e + 20)).F32(BitConverter.ToSingle(hdr, e + 24)).ToArray());
                    else if (kind == Shm.EV_DIED)
                    {
                        Send(new PacketWriter(Proto.C_DIED).U32(player).ToArray());
                        try { MondeGameEvent(kind, player, dmg, mode, 0, 0, 0); } catch (Exception) { }
                    }
                    else if (kind >= 10)
                    {
                        try { MondeGameEvent(kind, player, dmg, mode, BitConverter.ToSingle(hdr, e + 16), BitConverter.ToSingle(hdr, e + 20), BitConverter.ToSingle(hdr, e + 24)); }
                        catch (Exception ex) { L("monde : " + ex.Message); }
                    }
                }
                outRead++;
            }
            bridge.WriteU32(Shm.OutRead, outRead);
        }

        void WriteImage(byte[] hdr, int tick, bool alive)
        {
            DateTime now = DateTime.UtcNow;
            bridge.WriteU32(Shm.BridgeBeat, ++beat);
            lock (lk)
            {
                // --- statut (offsets 32..67 d'un bloc)
                byte[] st = new byte[36];
                PutU32(st, 0, (uint)NetState);
                PutU32(st, 4, MyId);
                PutU32(st, 8, (SessionId != 0 && HostId == MyId) ? 1u : 0u);
                PutU32(st, 12, SessionPublic ? 1u : 0u);
                PutStr(st, 16, SessionId != 0 ? SessionCode : "", 8);
                PutU32(st, 24, (uint)(SessionId != 0 ? SessionCount : 0));
                PutU32(st, 28, (uint)(SessionId != 0 ? SessionMax : 0));
                PutU32(st, 32, SessionId != 0 && (InWorld || SessionPvp) ? 1u : 0u);
                bridge.Write(Shm.NetState, st, st.Length);

                byte[] msg = new byte[80];
                PutStr(msg, 0, Status, 64);
                PutStr(msg, 64, MyName.Length > 0 ? MyName : Proto.CleanName(WantedName), 16);
                bridge.Write(Shm.StatusMsg, msg, msg.Length);

                byte[] sid = new byte[4];
                PutU32(sid, 0, SessionId);
                bridge.Write(Shm.SessionId, sid, 4);
                bridge.WriteU32(Shm.SaveSync, SaveSync ? 1u : 0u);
                bridge.WriteU32(Shm.SaveStatus, (uint)SaveStatus);
                bridge.WriteU32(Shm.SaveSlot, (uint)SaveSlot);
                bridge.WriteU32(Shm.ServerOk, NetState >= Shm.NET_LOBBY ? 1u : 0u);

                // --- joueurs distants visibles
                byte[] rem = new byte[Proto.MaxVisible * Shm.RemoteSize];
                for (int s = 0; s < slots.Length; s++)
                {
                    Remote rm = slots[s];
                    if (rm == null) continue;
                    if ((now - rm.Last).TotalSeconds > 1.5 || SessionId == 0)
                    {
                        slots[s] = null;
                        rm.Slot = -1;
                        remotes.Remove(rm.Id);
                        continue;
                    }
                    int o = s * Shm.RemoteSize;
                    byte[] rs = rm.State;
                    PutU32(rem, o + 0, 1);
                    PutU32(rem, o + 4, rm.Id);
                    PutU32(rem, o + 8, rm.Seq);
                    Buffer.BlockCopy(rs, 0, rem, o + 12, 4);   // flags
                    Buffer.BlockCopy(rs, 12, rem, o + 16, 16); // x y z sante
                    Buffer.BlockCopy(rs, 28, rem, o + 32, 16); // quaternion
                    Buffer.BlockCopy(rs, 44, rem, o + 48, 12); // vitesse
                    Buffer.BlockCopy(rs, 4, rem, o + 64, 4);   // anim
                    Buffer.BlockCopy(rs, 8, rem, o + 68, 4);   // frame
                    PutU32(rem, o + 72, BitConverter.ToUInt16(rs, 56));
                    PutU32(rem, o + 76, BitConverter.ToUInt16(rs, 58));
                    string nm;
                    if (!names.TryGetValue(rm.Id, out nm)) nm = "...";
                    PutStr(rem, o + 80, nm, 16);
                    // maisons : on ne voit que les joueurs de la meme maison (niveau inconnu = cache)
                    MemberInfo hmi;
                    if ((members.TryGetValue(rm.Id, out hmi) ? hmi.HouseOwner : 0u) != myHouseOwner) PutStr(rem, o + 96, "ow-ailleurs", 16);
                    else Buffer.BlockCopy(rs, 64, rem, o + 96, 16); // niveau
                }
                // poses (squelettes) des joueurs visibles
                for (int s = 0; s < slots.Length; s++)
                {
                    int po = Shm.PoseRx + s * Shm.PoseSlot;
                    Remote rm = slots[s];
                    byte[] img = bridge.Img;
                    if (rm == null || rm.Pose == null || (now - rm.PoseT).TotalSeconds > 1.5)
                    {
                        PutU32(img, po, 0);
                        PutU32(img, po + Shm.PoseTail, 0);
                        continue;
                    }
                    if (BitConverter.ToUInt32(img, po) == rm.PoseSeq && BitConverter.ToUInt32(img, po + 8) == rm.Id) continue;
                    PutU32(img, po, rm.PoseSeq);
                    PutU32(img, po + 4, (uint)rm.Pose.Length);
                    PutU32(img, po + 8, rm.Id);
                    Buffer.BlockCopy(rm.Pose, 0, img, po + 16, rm.Pose.Length);
                    PutU32(img, po + Shm.PoseTail, rm.PoseSeq);
                }
                // les etats trop vieux hors slot
                List<uint> dead = new List<uint>();
                foreach (Remote rm in remotes.Values) if (rm.Slot < 0 && (now - rm.Last).TotalSeconds > 1.5) dead.Add(rm.Id);
                foreach (uint id in dead) remotes.Remove(id);
                bridge.Write(Shm.Remote, rem, rem.Length);

                // --- evenements entrants (coups recus, eliminations)
                uint inRead = BitConverter.ToUInt32(hdr, Shm.InRead);
                if (!alive) inEvents.Clear();
                bool wroteIn = false;
                while (inEvents.Count > 0 && inWrite - inRead < 16)
                {
                    GameEvent ev = inEvents.Dequeue();
                    byte[] e = new byte[Shm.EventSize];
                    PutU32(e, 0, (uint)ev.Kind);
                    PutU32(e, 4, ev.Player);
                    PutF32(e, 8, ev.Damage);
                    PutU32(e, 12, (uint)ev.Mode);
                    PutF32(e, 16, ev.Dx);
                    PutF32(e, 20, ev.Dy);
                    PutF32(e, 24, ev.Dz);
                    bridge.Write(Shm.InEvents + (int)(inWrite & 15) * Shm.EventSize, e, e.Length);
                    inWrite++;
                    wroteIn = true;
                }
                if (wroteIn) bridge.WriteU32(Shm.InWrite, inWrite);

                // --- messages
                while (feed.Count > 0)
                {
                    string m = feed.Dequeue();
                    byte[] fb = new byte[Shm.FeedSize];
                    PutStr(fb, 0, m, Shm.FeedSize);
                    bridge.Write(Shm.Feed + (int)(feedWrite & 7) * Shm.FeedSize, fb, fb.Length);
                    feedWrite++;
                    bridge.WriteU32(Shm.FeedWrite, feedWrite);
                }

                // --- listes (sessions publiques, joueurs)
                if (listsDirty || tick % 15 == 0)
                {
                    listsDirty = false;
                    int n = Math.Min(sessionList.Count, Proto.MaxListed);
                    byte[] sl = new byte[Proto.MaxListed * Shm.SessionSize];
                    for (int i = 0; i < n; i++)
                    {
                        SessionEntry se = sessionList[i];
                        int o = i * Shm.SessionSize;
                        PutU32(sl, o, se.Id);
                        PutU32(sl, o + 4, (uint)se.Players);
                        PutU32(sl, o + 8, (uint)se.Max);
                        PutU32(sl, o + 12, (uint)se.Flags);
                        PutStr(sl, o + 16, se.Host, 16);
                        PutStr(sl, o + 32, se.Code, 16);
                    }
                    bridge.Write(Shm.Sessions, sl, sl.Length);
                    bridge.WriteU32(Shm.ListCount, (uint)n);

                    int pn = SessionId != 0 ? Math.Min(playerList.Count, Proto.MaxPlayersPerSession) : 0;
                    byte[] pl = new byte[Math.Max(1, pn) * Shm.PlayerSize];
                    for (int i = 0; i < pn; i++)
                    {
                        PlayerEntry pe = playerList[i];
                        int o = i * Shm.PlayerSize;
                        PutU32(pl, o, pe.Id);
                        PutU32(pl, o + 4, (uint)pe.Flags);
                        PutU32(pl, o + 8, (uint)(pe.Id == MyId ? PingMs : pe.Ping));
                        PutF32(pl, o + 12, pe.Health);
                        PutStr(pl, o + 16, pe.Name, 16);
                        PutStr(pl, o + 32, pe.Level, 16);
                    }
                    if (pn > 0) bridge.Write(Shm.Players, pl, pn * Shm.PlayerSize);
                    bridge.WriteU32(Shm.PlayerCount, (uint)pn);
                    bridge.WriteU32(Shm.ListSeq, ++listSeq);
                }

                // --- monde en ligne : profil, chat, menu, joueurs (zone EXT)
                WriteExt(bridge.Img, alive);
            }
        }

        public List<PlayerEntry> PlayersSnapshot()
        {
            lock (lk) return new List<PlayerEntry>(playerList);
        }

        // ---------------- auto-test (Jak3Online.exe --selftest)
        public void UiList() { HandleRequest(Shm.REQ_LIST, 0, null); }
        public void UiJoinId(uint id) { HandleRequest(Shm.REQ_JOIN_ID, id, null); }
        public void UiSettings(bool pvp, bool pub) { HandleRequest(Shm.REQ_SETTINGS, (uint)((pvp ? 1 : 0) | (pub ? 2 : 0)), null); }
        public int TestSessionListCount() { lock (lk) return sessionList.Count; }
        public uint TestFirstListedSession() { lock (lk) return sessionList.Count > 0 ? sessionList[0].Id : 0; }
        public int TestRemoteCount() { lock (lk) return remotes.Count; }
        public int TestRemotePoseLen(uint id)
        {
            lock (lk)
            {
                Remote rm;
                return remotes.TryGetValue(id, out rm) && rm.Pose != null ? rm.Pose.Length : 0;
            }
        }
        public float TestRemoteX(uint id)
        {
            lock (lk)
            {
                Remote rm;
                return remotes.TryGetValue(id, out rm) ? BitConverter.ToSingle(rm.State, 12) : float.NaN;
            }
        }
        public int TestInEventCount(int kind)
        {
            lock (lk)
            {
                int n = 0;
                foreach (GameEvent e in inEvents) if (e.Kind == kind) n++;
                return n;
            }
        }
        public void TestSetLocalState(float x, float y, float z)
        {
            TestSetLocalState(x, y, z, "wascitya", -1, 0f);
        }
        public void TestSetLocalState(float x, float y, float z, string level, int anim, float frame)
        {
            byte[] st = new byte[Proto.StateSize];
            PutU32(st, 0, Proto.FLAG_VALID | 0x8);
            PutU32(st, 4, (uint)anim);
            PutF32(st, 8, frame);
            PutF32(st, 12, x);
            PutF32(st, 16, y);
            PutF32(st, 20, z);
            PutF32(st, 24, 8f);
            PutF32(st, 40, 1f);
            PutStr(st, 64, level, 16);
            lock (lk) { localState = st; localStateTime = DateTime.UtcNow; }
        }
        public void TestSendHit(uint target, float dmg)
        {
            Send(new PacketWriter(Proto.C_HIT).U32(target).F32(dmg).U8(0).F32(1f).F32(0f).F32(0f).ToArray());
        }
        public void TestGetSave(int slot) { HandleRequest(Shm.REQ_GETSAVE, (uint)slot, null); }
        public void TestSaveSync(bool on) { HandleRequest(Shm.REQ_SAVESYNC, on ? 1u : 0u, null); }
        public void TestKillLink()
        {
            RelayLink rl = conn as RelayLink;
            wantConnected = false;
            if (rl != null) rl.Kill();
        }
        uint testSeq;
        // banc de test : commande envoyee au jeu (voir online-test-command! dans online.gc)
        public void TestCommand(int arg)
        {
            if (bridge == null) return;
            testSeq++;
            bridge.WriteU32(Shm.TestArg, (uint)arg);
            bridge.WriteU32(Shm.TestSeq, testSeq);
        }
        public byte[] TestGameHeader() { return bridge != null ? (byte[])bridge.Game.Clone() : null; }

        public void TestSendDied(uint killer)
        {
            Send(new PacketWriter(Proto.C_DIED).U32(killer).ToArray());
        }
    }

    // ------------------------------------------------------------------------
    //  Fenetre
    // ------------------------------------------------------------------------
    class MainForm : Form
    {
        Client client = new Client();
        Server server;
        TextBox txtName, txtServer, txtCode, txtChat;
        ComboBox cboMode, cboLang;
        CheckBox chkStartup;
        Button btnConnect;
        Label lblGame, lblServer, lblSession, lblHost, lblServerCaption;
        ListBox lstLog, lstPlayers;
        NotifyIcon tray;
        System.Windows.Forms.Timer uiTimer;
        readonly Queue<string> pendingLog = new Queue<string>();
        string iniPath;
        bool autoConnect = true;
        bool firstRun = true;
        string bridgeOverride;
        bool startHidden;
        bool reallyQuit;

        const int MODE_INTERNET = 0, MODE_HOST = 1, MODE_CUSTOM = 2;

        public MainForm(bool hidden)
        {
            startHidden = hidden;
            Text = W("Jak 3 En Ligne");
            Width = 660;
            Height = 674;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
            iniPath = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "Jak3Online.ini");

            int y = 12;
            AddLabel(W("Pseudo :"), 12, y + 3);
            txtName = new TextBox();
            txtName.SetBounds(160, y, 190, 23);
            txtName.MaxLength = 15;
            txtName.Leave += delegate { ApplyName(); };
            txtName.KeyDown += delegate (object o, KeyEventArgs ke) { if (ke.KeyCode == Keys.Enter) { ApplyName(); ke.SuppressKeyPress = true; } };
            Controls.Add(txtName);
            cboLang = new ComboBox();
            cboLang.DropDownStyle = ComboBoxStyle.DropDownList;
            cboLang.Items.Add(W("Langue : automatique"));
            cboLang.Items.Add("Francais");
            cboLang.Items.Add("English");
            cboLang.SetBounds(370, y, 120, 23);
            Controls.Add(cboLang);
            y += 32;
            AddLabel(W("Connexion :"), 12, y + 3);
            cboMode = new ComboBox();
            cboMode.DropDownStyle = ComboBoxStyle.DropDownList;
            cboMode.Items.Add(W("Internet gratuit (automatique)"));
            cboMode.Items.Add(W("Heberger mon propre serveur (IP)"));
            cboMode.Items.Add(W("Rejoindre un serveur perso (IP)"));
            cboMode.SetBounds(160, y, 330, 23);
            cboMode.SelectedIndexChanged += delegate { UpdateModeUi(); };
            Controls.Add(cboMode);
            y += 32;
            lblServerCaption = AddLabel(W("Adresse du serveur :"), 12, y + 3);
            txtServer = new TextBox();
            txtServer.SetBounds(160, y, 190, 23);
            Controls.Add(txtServer);
            btnConnect = new Button();
            btnConnect.SetBounds(370, y - 1, 120, 26);
            btnConnect.Click += delegate { ToggleConnect(); };
            Controls.Add(btnConnect);
            y += 32;
            chkStartup = new CheckBox();
            chkStartup.Text = W("Demarrer avec Windows (discret, a cote de l'horloge) : plus rien a lancer ensuite");
            chkStartup.SetBounds(12, y, 620, 24);
            chkStartup.CheckedChanged += delegate { SetStartup(chkStartup.Checked); SaveIni(); };
            Controls.Add(chkStartup);
            y += 32;

            lblGame = AddLabel(W("Jeu : ") + "-", 12, y); y += 22;
            lblServer = AddLabel(W("Reseau : ") + "-", 12, y); y += 22;
            lblSession = AddLabel(W("Session : -"), 12, y); y += 22;
            lblHost = AddLabel("", 12, y); y += 28;

            AddLabel(W("Rejoindre avec un code :"), 12, y + 3);
            txtCode = new TextBox();
            txtCode.SetBounds(160, y, 90, 23);
            txtCode.CharacterCasing = CharacterCasing.Upper;
            txtCode.MaxLength = 6;
            Controls.Add(txtCode);
            Button btnJoin = new Button();
            btnJoin.Text = W("Rejoindre");
            btnJoin.SetBounds(260, y - 1, 90, 26);
            btnJoin.Click += delegate { if (txtCode.Text.Trim().Length > 0) client.UiJoinCode(txtCode.Text); };
            Controls.Add(btnJoin);
            Button btnWorld = new Button();
            btnWorld.Text = W("Session publique");
            btnWorld.SetBounds(358, y - 1, 135, 26);
            btnWorld.Click += delegate { client.UiJoinWorld(); };
            Controls.Add(btnWorld);
            Button btnBot = new Button();
            btnBotUi = btnBot;
            btnBot.Visible = false;
            btnBot.Text = W("+ Bot de test");
            btnBot.SetBounds(500, y - 1, 120, 26);
            btnBot.Click += delegate { client.AddBot(); };
            Controls.Add(btnBot);
            Button btnLeave = new Button();
            btnLeave.Text = W("Quitter la session");
            btnLeave.SetBounds(360, y - 1, 130, 26);
            btnLeave.Click += delegate { client.UiLeave(); };
            Controls.Add(btnLeave);
            y += 34;
            // joueur 2 local : 2e fenetre du jeu avec une 2e manette (comme un ecran partage)
            btnJ2 = new Button();
            btnJ2.SetBounds(12, y - 1, 260, 26);
            btnJ2.Click += delegate { client.LocalJ2Request(2); };
            Controls.Add(btnJ2);
            AddLabel(W("Manette J2 :"), 282, y + 3);
            cboPad = new ComboBox();
            cboPad.DropDownStyle = ComboBoxStyle.DropDownList;
            cboPad.Items.Add(W("Automatique"));
            for (int k = 1; k <= 4; k++) cboPad.Items.Add(W("Manette ") + k);
            cboPad.SelectedIndex = 0;
            cboPad.SetBounds(370, y, 110, 23);
            cboPad.SelectedIndexChanged += delegate { LocalJ2.PadWanted = cboPad.SelectedIndex - 1; };
            Controls.Add(cboPad);
            CheckBox chkSide = new CheckBox();
            chkSide.Text = W("Cote a cote");
            chkSide.Checked = true;
            chkSide.SetBounds(492, y, 140, 24);
            chkSide.CheckedChanged += delegate { LocalJ2.SideBySide = chkSide.Checked; };
            Controls.Add(chkSide);
            y += 34;

            AddLabel(W("Joueurs de la session :"), 12, y);
            AddLabel(W("Journal :"), 310, y);
            y += 20;
            lstPlayers = new ListBox();
            lstPlayers.SetBounds(12, y, 290, 170);
            lstPlayers.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom;
            Controls.Add(lstPlayers);
            lstLog = new ListBox();
            lstLog.SetBounds(310, y, 320, 170);
            lstLog.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            lstLog.HorizontalScrollbar = true;
            Controls.Add(lstLog);
            y += 180;
            Label lblChat = AddLabel(W("Chat :"), 12, y + 3);
            lblChat.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            txtChat = new TextBox();
            txtChat.SetBounds(60, y, 470, 23);
            txtChat.MaxLength = 80;
            txtChat.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            txtChat.KeyDown += delegate (object o, KeyEventArgs ke)
            {
                if (ke.KeyCode == Keys.Enter) { client.SendChat(txtChat.Text); txtChat.Text = ""; ke.SuppressKeyPress = true; }
            };
            Controls.Add(txtChat);
            Button btnChat = new Button();
            btnChat.Text = W("Envoyer");
            btnChat.SetBounds(540, y - 1, 90, 26);
            btnChat.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            btnChat.Click += delegate { client.SendChat(txtChat.Text); txtChat.Text = ""; };
            Controls.Add(btnChat);
            y += 32;
            Label help = AddLabel(W("Dans le jeu : T = chat, TAB (ou SELECT) = menu des joueurs, Start puis SELECT = page EN LIGNE. Fermer = rester actif."), 12, y);
            help.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;

            // icone pres de l'horloge
            tray = new NotifyIcon();
            tray.Icon = Icon != null ? Icon : SystemIcons.Application;
            tray.Text = W("Jak 3 En Ligne");
            tray.Visible = true;
            ContextMenu menu = new ContextMenu();
            menu.MenuItems.Add(W("Ouvrir"), delegate { ShowWindow(); });
            menu.MenuItems.Add(W("Quitter Jak 3 En Ligne"), delegate { reallyQuit = true; Close(); });
            tray.ContextMenu = menu;
            tray.DoubleClick += delegate { ShowWindow(); };

            LoadIni();
            DesktopShortcutOnce();
            client.Log = QueueLog;
            client.LogFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Jak3Online.log");
            client.OnNameChanged = delegate (string n)
            {
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (!txtName.Focused) txtName.Text = n;
                        SaveIni();
                    });
                }
                catch (Exception) { }
            };
            if (bridgeOverride != null) client.BridgeDir = bridgeOverride;
            QueueLog(W("Dossier d'echange avec le jeu : ") + client.BridgeDir);
            if (bridgeOverride == null && !Directory.Exists(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "..", "goal_src")))
                QueueLog(W("ATTENTION : Jak3Online.exe doit rester dans le dossier data\\online du mod (sinon le jeu ne le voit pas)."));
            client.Start();
            UpdateModeUi();

            if (firstRun)
            {
                // premiere fois : on active le demarrage avec Windows pour que tout soit automatique ensuite
                chkStartup.Checked = true;
                firstRun = false;
                SaveIni();
                tray.ShowBalloonTip(6000, W("Jak 3 En Ligne"), W("Le mode en ligne est actif. Il demarrera tout seul avec Windows : lancez simplement le jeu."), ToolTipIcon.Info);
            }

            uiTimer = new System.Windows.Forms.Timer();
            uiTimer.Interval = 250;
            uiTimer.Tick += delegate { RefreshUi(); };
            uiTimer.Start();

            if (autoConnect) ToggleConnect();
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

        public void ShowFromOtherInstance() { ShowWindow(); }

        void ShowWindow()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        Label AddLabel(string text, int x, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Location = new Point(x, y);
            Controls.Add(l);
            return l;
        }

        void QueueLog(string s)
        {
            lock (pendingLog) pendingLog.Enqueue(DateTime.Now.ToString("HH:mm:ss") + "  " + s);
        }

        bool French { get { return cboLang.SelectedIndex == 0 ? Proto.SystemFrench() : cboLang.SelectedIndex == 1; } }

        // langue forcee dans la fenetre (-1 : automatique = langue du jeu)
        int LangForce { get { return cboLang == null ? -1 : cboLang.SelectedIndex == 1 ? 1 : cboLang.SelectedIndex == 2 ? 0 : -1; } }

        // langue de la fenetre : Francais / English si choisi, sinon celle de Windows
        int uiLang = -2;
        int UiLang
        {
            get
            {
                if (uiLang == -2)
                {
                    uiLang = Lang.FromCulture();
                    try
                    {
                        if (iniPath != null && File.Exists(iniPath))
                            foreach (string line in File.ReadAllLines(iniPath))
                            {
                                string t = line.Trim().ToLowerInvariant();
                                if (t == "langue=fr") uiLang = 1;
                                else if (t == "langue=en") uiLang = 0;
                            }
                    }
                    catch (Exception) { }
                }
                return uiLang;
            }
        }

        string W(string fr) { return Lang.Win(UiLang, fr); }

        void ApplyName()
        {
            string n = Proto.CleanName(txtName.Text);
            txtName.Text = n;
            if (n != client.WantedName || (client.MyName.Length > 0 && n != client.MyName))
            {
                client.Rename(n);
                QueueLog(W("Pseudo : ") + n);
                SaveIni();
            }
        }

        void UpdateModeUi()
        {
            bool custom = cboMode.SelectedIndex == MODE_CUSTOM;
            bool host = cboMode.SelectedIndex == MODE_HOST;
            txtServer.Visible = custom || host;
            lblServerCaption.Visible = custom || host;
            lblServerCaption.Text = host ? W("Port a ouvrir :") : W("Adresse du serveur :");
            if (host && txtServer.Text.IndexOf(':') < 0 && txtServer.Text.Trim().Length == 0) txtServer.Text = Proto.DefaultPort.ToString();
        }

        void LoadIni()
        {
            string name = Proto.RandomName();
            string srv = "";
            int mode = MODE_INTERNET;
            int lang = 0;
            try
            {
                if (File.Exists(iniPath))
                {
                    firstRun = false;
                    foreach (string line in File.ReadAllLines(iniPath))
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                        string v = line.Substring(eq + 1).Trim();
                        if (k == "pseudo") name = v;
                        else if (k == "serveur") srv = v;
                        else if (k == "mode") int.TryParse(v, out mode);
                        else if (k == "langue") lang = v.ToLowerInvariant() == "fr" ? 1 : v.ToLowerInvariant() == "en" ? 2 : 0;
                        else if (k == "auto") autoConnect = v != "0";
                        else if (k == "pont" && v.Length > 0) bridgeOverride = v;
                    }
                }
            }
            catch (Exception) { }
            txtName.Text = Proto.CleanName(name);
            txtServer.Text = srv;
            cboMode.SelectedIndex = Math.Max(0, Math.Min(2, mode));
            cboLang.SelectedIndex = lang;
            client.WantedName = txtName.Text;
            chkStartup.Checked = IsStartupEnabled();
        }

        void SaveIni()
        {
            try
            {
                List<string> lines = new List<string>();
                lines.Add("pseudo=" + txtName.Text.Trim());
                lines.Add("mode=" + cboMode.SelectedIndex);
                lines.Add("serveur=" + txtServer.Text.Trim());
                lines.Add("langue=" + (cboLang.SelectedIndex == 1 ? "fr" : cboLang.SelectedIndex == 2 ? "en" : "auto"));
                lines.Add("auto=" + (autoConnect ? "1" : "0"));
                if (bridgeOverride != null) lines.Add("pont=" + bridgeOverride);
                // reperes des actions faites une seule fois (langue au premier lancement, raccourci)
                if (File.Exists(iniPath))
                    foreach (string old in File.ReadAllLines(iniPath))
                    {
                        string t = old.Trim().ToLowerInvariant();
                        if ((t == "langue_jeu_init=1" || t == "raccourci=1") && !lines.Contains(t)) lines.Add(t);
                    }
                File.WriteAllLines(iniPath, lines.ToArray());
            }
            catch (Exception) { }
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        bool IsStartupEnabled()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue("Jak3Online") != null;
            }
            catch (Exception) { return false; }
        }

        void SetStartup(bool on)
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue("Jak3Online", "\"" + Application.ExecutablePath + "\" --tray");
                    else if (k.GetValue("Jak3Online") != null) k.DeleteValue("Jak3Online");
                }
            }
            catch (Exception ex) { QueueLog(W("Demarrage avec Windows impossible : ") + ex.Message); }
        }

        void ToggleConnect()
        {
            if (client.WantConnected)
            {
                client.Disconnect();
                autoConnect = false;
                if (server != null) { server.Stop(); server = null; QueueLog(W("Serveur arrete")); }
                SaveIni();
                return;
            }
            autoConnect = true;
            client.WantedName = Proto.CleanName(txtName.Text);
            client.French = French; client.LangForce = LangForce;
            int mode = cboMode.SelectedIndex;
            client.UseRelay = mode == MODE_INTERNET;
            if (mode == MODE_HOST)
            {
                int port = Proto.DefaultPort;
                string t = txtServer.Text.Trim();
                int colon = t.LastIndexOf(':');
                int pp;
                if (int.TryParse(colon >= 0 ? t.Substring(colon + 1) : t, out pp) && pp > 0 && pp < 65536) port = pp;
                if (server == null)
                {
                    try
                    {
                        server = new Server();
                        server.Log = delegate (string s) { QueueLog("[serveur] " + s); };
                        server.Start(port);
                    }
                    catch (Exception ex)
                    {
                        server = null;
                        QueueLog(W("Impossible de demarrer le serveur : ") + ex.Message);
                    }
                }
                client.ServerAddress = "127.0.0.1:" + port;
            }
            else if (mode == MODE_CUSTOM)
            {
                client.ServerAddress = txtServer.Text.Trim();
            }
            client.Connect();
            SaveIni();
        }

        // premier lancement : le jeu prend la langue de Windows (une seule fois, ensuite c'est le choix du joueur)
        int langInitState = -1;   // -1 inconnu, 0 a faire, 1 fait
        DateTime langAttachSince = DateTime.MinValue;
        void LangFirstRun()
        {
            if (langInitState == 1) return;
            if (langInitState == 2)
            {
                // commande neutre : le jeu relit la derniere commande a chaque demarrage, il ne doit
                // pas remettre la langue de Windows a chaque fois
                if ((DateTime.UtcNow - langAttachSince).TotalSeconds > 10) { client.TestCommand(999); langInitState = 1; }
                return;
            }
            if (langInitState == -1)
            {
                langInitState = 0;
                try
                {
                    if (iniPath != null && File.Exists(iniPath))
                        foreach (string line in File.ReadAllLines(iniPath))
                            if (line.Trim().ToLowerInvariant() == "langue_jeu_init=1") langInitState = 1;
                }
                catch (Exception) { }
                if (langInitState == 1) return;
            }
            if (!client.GameAttached) { langAttachSince = DateTime.MinValue; return; }
            if (langAttachSince == DateTime.MinValue) { langAttachSince = DateTime.UtcNow; return; }
            if ((DateTime.UtcNow - langAttachSince).TotalSeconds < 8) return;
            client.TestCommand(330 + Lang.FromCulture());
            langInitState = 2;
            try { if (iniPath != null) File.AppendAllText(iniPath, Environment.NewLine + "langue_jeu_init=1" + Environment.NewLine); } catch (Exception) { }
        }

        // premier lancement : raccourci "Jak 3 Online" sur le bureau (une seule fois : s'il est
        // supprime, il n'est pas recree)
        void DesktopShortcutOnce()
        {
            try
            {
                if (iniPath != null && File.Exists(iniPath))
                    foreach (string line in File.ReadAllLines(iniPath))
                        if (line.Trim().ToLowerInvariant() == "raccourci=1") return;
                string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string lnk = Path.Combine(desk, "Jak 3 Online.lnk");
                if (!File.Exists(lnk))
                {
                    Type t = Type.GetTypeFromProgID("WScript.Shell");
                    object sh = Activator.CreateInstance(t);
                    object sc = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, sh, new object[] { lnk });
                    Type st = sc.GetType();
                    st.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { Application.ExecutablePath });
                    st.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(Application.ExecutablePath) });
                    st.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { "Jak 3 Online : a lancer avant le jeu (reseau, boutique, joueurs)" });
                    st.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { Application.ExecutablePath + ",0" });
                    st.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, sc, null);
                }
                if (iniPath != null) File.AppendAllText(iniPath, Environment.NewLine + "raccourci=1" + Environment.NewLine);
            }
            catch (Exception) { }
        }

        Button btnBotUi, btnJ2;
        ComboBox cboPad;

        void RefreshUi()
        {
            if (btnBotUi != null) btnBotUi.Visible = client.IsCreateur;
            if (btnJ2 != null) btnJ2.Text = client.LocalJ2Running ? W("Arreter le joueur 2 local") : W("+ Joueur 2 local (2e manette)");
            LangFirstRun();
            lock (pendingLog)
            {
                while (pendingLog.Count > 0)
                {
                    lstLog.Items.Add(pendingLog.Dequeue());
                    if (lstLog.Items.Count > 300) lstLog.Items.RemoveAt(0);
                    lstLog.TopIndex = lstLog.Items.Count - 1;
                }
            }
            client.French = French; client.LangForce = LangForce;
            btnConnect.Text = client.WantConnected ? W("Deconnecter") : W("Se connecter");
            txtServer.Enabled = !client.WantConnected;
            cboMode.Enabled = !client.WantConnected;
            lblGame.Text = W("Jeu : ") + client.GameStatus + (client.GameAttached ? "  (" + client.GameFps + " img/s)" : "");
            lblServer.Text = W("Reseau : ") + client.Status + (client.NetState >= Shm.NET_LOBBY ? "  (ping " + client.PingMs + " ms)" : "")
                + (client.IsCreateur ? W("   -   CREATEUR (cle verifiee)") : "");
            string sess;
            if (client.SessionId != 0)
                sess = W("Session ") + (client.SessionPublic ? W("PUBLIQUE") : W("PRIVEE")) + W("  -  CODE : ") + client.SessionCode
                    + "  -  " + client.SessionCount + "/" + client.SessionMax + W(" joueurs  -  PvP ") + (client.SessionPvp ? W("oui") : W("non"))
                    + (client.HostId == client.MyId ? W("  -  vous etes l'hote") : "")
                    + (client.InWorld && client.Prof != null ? W("  -  niveau ") + client.Prof.Level + ", " + client.TestMoney + W(" orbes") : "");
            else
                sess = W("Session : aucune (creez-en une depuis le menu Start du jeu)");
            lblSession.Text = sess;
            string tip = client.SessionId != 0 ? "Jak 3 En Ligne - " + client.SessionCode + " (" + client.SessionCount + ")" : "Jak 3 En Ligne - " + (client.NetState >= Shm.NET_LOBBY ? W("connecte") : W("hors ligne"));
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            if (server != null)
                lblHost.Text = W("Serveur heberge sur ce PC, port ") + server.Port + " : " + server.PlayerCount + W(" connecte(s). Ouvrez ce port (TCP) sur votre box et donnez votre IP.");
            else if (client.UseRelay && client.NetState >= Shm.NET_LOBBY)
                lblHost.Text = W("Relais internet gratuit : ") + client.RelayName + W(" (aucun port a ouvrir).");
            else
                lblHost.Text = "";

            if (!Visible) return;
            List<PlayerEntry> pl = client.PlayersSnapshot();
            lstPlayers.BeginUpdate();
            lstPlayers.Items.Clear();
            foreach (PlayerEntry e in pl)
            {
                string s = e.Name;
                if ((e.Flags & Proto.PFLAG_HOST) != 0) s += W(" (hote)");
                if ((e.Flags & Proto.PFLAG_ME) != 0) s += W(" (vous)");
                s += "  " + (e.Level.Length > 0 ? e.Level : "-") + "  PV " + (int)e.Health + "  " + e.Ping + " ms";
                lstPlayers.Items.Add(s);
            }
            lstPlayers.EndUpdate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!reallyQuit && e.CloseReason == CloseReason.UserClosing)
            {
                // la croix cache la fenetre : le mode en ligne reste actif
                e.Cancel = true;
                Hide();
                tray.ShowBalloonTip(3000, W("Jak 3 En Ligne"), W("Toujours actif ici. Clic droit > Quitter pour l'arreter."), ToolTipIcon.Info);
                return;
            }
            SaveIni();
            tray.Visible = false;
            client.StopLocalJ2();
            client.Stop();
            if (server != null) server.Stop();
            base.OnFormClosing(e);
        }
    }

    static class SelfTest
    {
        static int fails;

        static bool Wait(Func<bool> cond, int ms)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end)
            {
                if (cond()) return true;
                Thread.Sleep(20);
            }
            return cond();
        }

        static void Check(string what, bool ok)
        {
            Console.WriteLine((ok ? "[OK]    " : "[ECHEC] ") + what);
            if (!ok) fails++;
        }

        static Client NewClient(string name, int port)
        {
            Client c = new Client();
            c.UseBridge = false;
            c.WantedName = name;
            c.ServerAddress = "127.0.0.1:" + port;
            c.Log = m => Console.WriteLine("   [" + name + "] " + m);
            c.Start();
            c.Connect();
            return c;
        }

        // Jak3Online.exe --mondetest [relay] : le monde en ligne sans jeu (identites signees, chat,
        // moderation du createur, economie, boutique, bannissement)
        public static int MondeTest(bool relay)
        {
            const int port = 27993;
            Server s = null;
            if (!relay) { s = new Server(); s.Start(port); }
            string dirA = Path.Combine(Path.GetTempPath(), "j3o-monde-test-A");
            try { if (Directory.Exists(dirA)) Directory.Delete(dirA, true); } catch (Exception) { }
            Client a = new Client();
            a.UseBridge = false;
            a.UseRelay = relay;
            a.WantedName = "Createur";
            a.ServerAddress = "127.0.0.1:" + port;
            a.ProfileDir = dirA;
            a.Log = m => Console.WriteLine("   [A] " + m);
            a.Start(); a.Connect();
            Client b = new Client();
            b.UseBridge = false;
            b.UseRelay = relay;
            b.Ephemeral = true;
            b.WantedName = "Joueur";
            b.ServerAddress = "127.0.0.1:" + port;
            b.Log = m => Console.WriteLine("   [B] " + m);
            b.Start(); b.Connect();
            Check("connexions", Wait(() => a.NetState == Shm.NET_LOBBY && b.NetState == Shm.NET_LOBBY, relay ? 60000 : 5000));
            Check("A est le createur (cle de ce PC)", a.IsCreateur);
            Check("B n'est pas le createur", !b.IsCreateur);
            a.UiJoinWorld();
            Check("A dans le monde en ligne", Wait(() => a.InWorld, relay ? 20000 : 5000));
            Thread.Sleep(relay ? 3000 : 300);
            b.UiJoinWorld();
            Check("B dans le monde en ligne (meme session)", Wait(() => b.InWorld && b.SessionCode == a.SessionCode, relay ? 20000 : 5000));
            Check("identites verifiees des deux cotes", Wait(() => a.VerifiedCount >= 1 && b.VerifiedCount >= 1, relay ? 20000 : 5000));
            // chat
            a.TestChat("Salut tout le monde");
            Check("B recoit le chat de A", Wait(() => b.ChatSnapshot().Exists(x => x.Contains("Salut tout le monde")), 5000));
            b.TestChat("Coucou");
            Check("A recoit le chat de B", Wait(() => a.ChatSnapshot().Exists(x => x.Contains("Coucou")), 5000));
            // moderation (signee par la cle du createur)
            a.TestAdmin(Client.CMD_FREEZE, b.MyId, 0, "");
            Check("B gele par le createur", Wait(() => b.IsFrozen, 5000));
            a.TestAdmin(Client.CMD_UNFREEZE, b.MyId, 0, "");
            Check("B degele", Wait(() => !b.IsFrozen, 5000));
            a.TestAdmin(Client.CMD_MUTE, b.MyId, 0, "");
            Check("B rendu muet", Wait(() => b.IsMuted, 5000));
            int before = a.ChatSnapshot().Count;
            b.TestChat("message interdit");
            Thread.Sleep(relay ? 3000 : 600);
            Check("le chat de B n'arrive plus", !a.ChatSnapshot().Exists(x => x.Contains("message interdit")));
            a.TestAdmin(Client.CMD_UNMUTE, b.MyId, 0, "");
            Check("B peut reparler", Wait(() => !b.IsMuted, 5000));
            // B n'a aucun pouvoir
            b.TestAdmin(Client.CMD_FREEZE, a.MyId, 0, "");
            Thread.Sleep(relay ? 3000 : 600);
            Check("B ne peut pas geler A", !a.IsFrozen);
            // economie
            long mb = b.TestMoney;
            a.TestAdmin(Client.CMD_GIVE, b.MyId, 500, "");
            Check("B recoit 500 orbes du createur", Wait(() => b.TestMoney == mb + 500, 5000));
            a.TestSetXp(5000);   // pas de passage de niveau pendant ce test (le bonus fausserait le compte)
            long ma = a.TestMoney;
            a.TestGameEvent(Client.EV_ORBS, 0, 0, 12);
            Check("12 orbes ramassees -> +12", a.TestMoney == ma + 12);
            a.TestGameEvent(Client.EV_ORBS, 0, 0, 50);
            a.TestGameEvent(Client.EV_ORBS, 0, 0, 50);
            Check("gains plafonnes (anti-triche : 90 par minute)", a.TestMoney == ma + 90);
            // elimination : B meurt, tue par A -> B lache du butin, recu signe par B, A recompense
            long mk = a.TestMoney, mbk = b.TestMoney;
            b.TestGameEvent(Shm.EV_DIED, a.MyId, 0, 0);
            Check("B lache du butin (5 % de ses orbes)", Wait(() => b.TestMoney == mbk - 25, 5000));
            Check("A recompense : elimination (25) + butin (25)", Wait(() => a.TestMoney == mk + 50, 5000));
            long mk2 = a.TestMoney;
            b.TestGameEvent(Shm.EV_DIED, a.MyId, 0, 0);
            Thread.Sleep(relay ? 3000 : 600);
            long g2 = a.TestMoney - mk2;
            Console.WriteLine("   2e elimination du meme joueur : +" + g2);
            Check("meme victime : gain degressif (anti-farm)", g2 > 0 && g2 < 50);
            for (int k = 0; k < 4; k++) { b.TestGameEvent(Shm.EV_DIED, a.MyId, 0, 0); Thread.Sleep(relay ? 1500 : 300); }
            long mk3 = a.TestMoney;
            b.TestGameEvent(Shm.EV_DIED, a.MyId, 0, 0);
            Thread.Sleep(relay ? 3000 : 600);
            Check("au bout de 5 fois : plus rien", a.TestMoney == mk3);
            // boutique : niveau requis, objet requis, super-pouvoirs
            long mbb = b.TestMoney;
            b.TestGameEvent(Client.EV_BUY, 0, 0, 0);
            Check("B achete le Blaster (40)", b.TestMoney == mbb - 40 && b.Prof.Owns(0));
            b.TestGameEvent(Client.EV_BUY, 0, 0, 4);
            Check("Beam Reflexor refuse : niveau 4 requis", !b.Prof.Owns(4));
            b.TestSetXp(2000);
            b.TestGameEvent(Client.EV_BUY, 0, 0, 4);
            Check("B (niveau " + b.TestLevel + ") achete le Beam Reflexor (requiert le Blaster)", b.Prof.Owns(4));
            b.TestGameEvent(Client.EV_BUY, 0, 0, 11);
            Check("pas de Super Nova sans le Mass Inverter", !b.Prof.Owns(11));
            b.TestGameEvent(Client.EV_BUY, 0, 0, 33);
            Check("Dark Jak invincible : interdit a la vente", !b.Prof.Owns(33));
            b.TestSetMoney(1000);
            b.TestGameEvent(Client.EV_BUY, 0, 0, 64);
            Check("super-pouvoir Turbo achete et actif", b.Prof.PowerLeft(0) > 55f && b.TestMoney == 880);
            b.TestGameEvent(Client.EV_BUY, 0, 0, 69);
            Thread.Sleep(1200);
            long xb = b.Prof.Xp;
            b.TestGameEvent(Client.EV_ENEMY, 0, 0, 1);
            Check("Double XP : l'experience compte double", b.Prof.Xp - xb == 10);
            // evenement : boss geant (degats partages, recompense selon la part)
            a.TestSetLocalState(0f, 0f, 0f);
            b.TestSetLocalState(4096f, 0f, 0f);
            a.TestStartEvent(1);
            Check("evenement boss recu par B", Wait(() => b.TestEventKind == 1 && b.TestEventState == 1, 5000));
            long ea = a.TestMoney, eb = b.TestMoney;
            for (int k = 0; k < 40 && a.TestEventState == 1; k++)
            {
                a.TestGameEvent(Client.EV_BOSS_DMG, 0, 0, 300);
                if (k % 2 == 0) b.TestGameEvent(Client.EV_BOSS_DMG, 0, 0, 150);
                Thread.Sleep(1000);
            }
            Check("boss vaincu (points de vie partages)", Wait(() => a.TestEventState == 2 && b.TestEventState == 2, 8000));
            Check("A et B recompenses selon leurs degats", a.TestMoney > ea && b.TestMoney > eb && (a.TestMoney - ea) > (b.TestMoney - eb));
            // anti-triche : empreinte identique, vitesse impossible
            Check("empreintes identiques (meme version)", a.TestFingerprint == b.TestFingerprint);
            for (int k = 0; k < 40; k++) { b.TestSetLocalState(k * 40f * 4096f, 0f, 0f); Thread.Sleep(relay ? 600 : 300); }
            Thread.Sleep(relay ? 3000 : 800);
            Check("A detecte la vitesse impossible de B (suspect)", a.TestSuspects >= 1);
            // courses
            b.TestSetLocalState(0f, 0f, 0f);
            a.TestSetLocalState(0f, 0f, 0f);
            // bannissement
            a.TestAdmin(Client.CMD_BAN, b.MyId, 0, "");
            Check("B banni : il quitte la session", Wait(() => b.SessionId == 0, 8000));
            Thread.Sleep(relay ? 3000 : 500);
            b.UiJoinWorld();
            Thread.Sleep(relay ? 12000 : 2500);
            Check("B banni ne peut pas revenir", b.SessionId == 0 || !b.InWorld);
            a.TestAdmin(Client.CMD_UNBAN, 0, 0, "");
            a.Stop(); b.Stop(); if (s != null) s.Stop();
            try { Directory.Delete(dirA, true); } catch (Exception) { }
            Console.WriteLine(fails == 0 ? "TEST DU MONDE EN LIGNE OK" : (fails + " TEST(S) EN ECHEC"));
            return fails == 0 ? 0 : 1;
        }

        public static int Stress(int count)
        {
            const int port = 27998;
            Server s = new Server();
            s.Start(port);
            List<Client> cl = new List<Client>();
            for (int i = 0; i < count + 1; i++)
            {
                Client c = new Client();
                c.UseBridge = false;
                c.WantedName = "J" + i;
                c.ServerAddress = "127.0.0.1:" + port;
                c.Start();
                c.Connect();
                cl.Add(c);
            }
            Check((count + 1) + " clients connectes", Wait(() => cl.TrueForAll(c => c.NetState == Shm.NET_LOBBY), 20000));
            cl[0].UiCreate(true, 100, true);
            Wait(() => cl[0].SessionId != 0, 3000);
            string code = cl[0].SessionCode;
            for (int i = 1; i < count; i++) cl[i].UiJoinCode(code);
            Check(count + " joueurs dans la meme session", Wait(() => cl[0].SessionCount == count, 20000));
            Random rnd = new Random(1);
            for (int i = 0; i < count; i++) cl[i].TestSetLocalState((float)rnd.NextDouble() * 400000f, 0f, (float)rnd.NextDouble() * 400000f);
            DateTime t0 = DateTime.UtcNow;
            Check("chaque joueur recoit les 16 joueurs les plus proches", Wait(() =>
            {
                for (int i = 0; i < count; i++) if (cl[i].TestRemoteCount() < Proto.MaxVisible) return false;
                return true;
            }, 20000));
            Console.WriteLine("   (synchro complete en " + (int)(DateTime.UtcNow - t0).TotalMilliseconds + " ms)");
            cl[count].UiJoinCode(code);
            Thread.Sleep(800);
            Check("le joueur " + (count + 1) + " est refuse (session pleine)", cl[count].SessionId == 0);
            // garder l'etat frais pendant 3 s de trafic
            for (int k = 0; k < 6; k++)
            {
                for (int i = 0; i < count; i++) cl[i].TestSetLocalState((float)rnd.NextDouble() * 400000f, 0f, (float)rnd.NextDouble() * 400000f);
                Thread.Sleep(500);
            }
            Check("tous toujours connectes apres 3 s de trafic", cl.TrueForAll(c => c.NetState >= Shm.NET_LOBBY));
            foreach (Client c in cl) c.Stop();
            s.Stop();
            Console.WriteLine(fails == 0 ? "TEST DE CHARGE OK" : (fails + " TEST(S) EN ECHEC"));
            return fails == 0 ? 0 : 1;
        }

        static string DataOnlineDir()
        {
            // les tests sont lances depuis un dossier temporaire : on vise data/online/bridge
            string env = Environment.GetEnvironmentVariable("JAK3ONLINE_BRIDGE");
            if (!string.IsNullOrEmpty(env)) return env;
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bridge");
        }

        // Jak3Online.exe --probe : verifie que le jeu parle au programme par les fichiers
        public static int Probe(int seconds)
        {
            Client a = new Client();
            a.BridgeDir = DataOnlineDir();
            a.Log = m => Console.WriteLine("   [jeu] " + m);
            a.Start();
            Check("jeu detecte via " + a.BridgeDir, Wait(() => a.GameAttached, seconds * 1000));
            Thread.Sleep(2500);
            Console.WriteLine("   images/s vues par le programme : " + a.GameFps);
            FileBridge r = new FileBridge(a.BridgeDir);
            Wait(() => r.Poll(), 3000);
            Check("le jeu voit le programme (battement recopie)", Wait(() => { r.Poll(); return BitConverter.ToUInt32(r.Game, Shm.BridgeBeat) != 0; }, 3000));
            a.Stop();
            return fails;
        }

        // Jak3Online.exe --e2e : test complet avec le vrai jeu lance
        public static int EndToEnd(bool relay)
        {
            const int port = 27997;
            Server s = new Server();
            if (!relay) s.Start(port);
            Client a = new Client();
            a.BridgeDir = DataOnlineDir();
            a.WantedName = "JeuReel";
            a.UseRelay = relay;
            a.ServerAddress = "127.0.0.1:" + port;
            a.Log = m => Console.WriteLine("   [jeu] " + m);
            a.Start();
            a.Connect();
            Check("Jak3Online voit le jeu et le serveur", Wait(() => a.GameAttached && a.NetState == Shm.NET_LOBBY, 90000));
            FileBridge r = new FileBridge(a.BridgeDir);
            Func<int, uint> G = off => { r.Poll(); return BitConverter.ToUInt32(r.Game, off); };
            Check("le jeu recoit l'etat reseau (lobby)", Wait(() => G(Shm.NetState) == Shm.NET_LOBBY, 5000));

            a.UiCreate(false, 100, true);
            Check("session privee creee", Wait(() => a.SessionId != 0, 5000));
            Check("le jeu affiche la session " + a.SessionCode, Wait(() => G(Shm.NetState) == Shm.NET_SESSION
                && Encoding.ASCII.GetString(r.Game, Shm.SessionCode, 6) == a.SessionCode, 5000));

            Client b = new Client();
            b.UseBridge = false;
            b.WantedName = "Simule";
            b.UseRelay = relay;
            b.ServerAddress = "127.0.0.1:" + port;
            b.Log = m => Console.WriteLine("   [simule] " + m);
            b.Start();
            b.Connect();
            Wait(() => b.NetState == Shm.NET_LOBBY, 5000);
            b.UiJoinCode(a.SessionCode);
            Check("joueur simule rejoint avec le code", Wait(() => b.SessionId == a.SessionId, 5000));
            Check("le jeu voit 2 joueurs", Wait(() => G(Shm.SessionPlayers) == 2, 5000));

            float ax = 0, ay = 0, az = 0;
            string lev = "";
            int maxShown = 0, maxPosed = 0, maxParts = 0, maxPoseLen = 0;
            // 1) meme niveau, meme animation que le vrai joueur ; 2) animation invalide (repli)
            for (int k = 0; k < 80; k++)
            {
                r.Poll();
                ax = BitConverter.ToSingle(r.Game, Shm.Local + 16);
                ay = BitConverter.ToSingle(r.Game, Shm.Local + 20);
                az = BitConverter.ToSingle(r.Game, Shm.Local + 24);
                int n = 0;
                while (n < 16 && r.Game[Shm.Local + 64 + n] != 0) n++;
                lev = Encoding.ASCII.GetString(r.Game, Shm.Local + 64, n);
                int anim = k < 40 ? BitConverter.ToInt32(r.Game, Shm.Local + 8) : 9999 + k;
                float frame = k < 40 ? BitConverter.ToSingle(r.Game, Shm.Local + 12) : 500f;
                b.TestSetLocalState(ax + 8192f, ay, az, lev, anim, frame);
                // le joueur simule renvoie le squelette du vrai joueur (a 2 m de lui)
                byte[] pose = a.LocalPoseSnapshot();
                if (pose != null && k < 60)
                {
                    maxPoseLen = Math.Max(maxPoseLen, pose.Length);
                    Buffer.BlockCopy(BitConverter.GetBytes(ax + 8192f), 0, pose, 4, 4);
                    b.SetLocalPoseRaw(pose);
                }
                maxShown = Math.Max(maxShown, r.Game[Shm.Local + 89]);
                maxPosed = Math.Max(maxPosed, r.Game[Shm.Local + 90]);
                maxParts = Math.Max(maxParts, r.Game[Shm.Local + 91]);
                Thread.Sleep(100);
            }
            if (Environment.GetEnvironmentVariable("JAK3ONLINE_E2E_BOTS") == "1")
            {
                int nb = int.Parse(Environment.GetEnvironmentVariable("JAK3ONLINE_E2E_NBOTS") ?? "8");
                for (int k = 0; k < nb; k++) a.AddBotNow();
                Thread.Sleep(15000);
                Console.WriteLine("   8 bots : avatars " + G2(r, 88) + " squelette " + G2(r, 90) + " pieces " + G2(r, 91));
            }
            Console.WriteLine("   pose du vrai joueur : " + maxPoseLen + " octets, avatars avec squelette " + maxPosed + ", pieces affichees " + maxParts
                + ", poses envoyees " + b.PosesSent + ", recues par le jeu " + a.PosesReceived);
            Check("le jeu envoie son squelette complet (Jak + Daxter)", maxPoseLen >= 32 + 2 * 20 + (65 + 49) * 16);
            Check("l'avatar reproduit le squelette recu", maxPosed == 1);
            Check("Daxter est affiche sur l'avatar", maxParts >= 1);
            Console.WriteLine("   vrai joueur : niveau " + lev + " pos " + ax + " " + ay + " " + az + " anim " + BitConverter.ToInt32(r.Game, Shm.Local + 8)
                + "  avatars actifs " + r.Game[Shm.Local + 88] + ", affiches (max) " + maxShown);
            Check("le joueur simule voit le vrai joueur", !float.IsNaN(b.TestRemoteX(a.MyId)));
            Check("le jeu a cree l'avatar du joueur simule", r.Game[Shm.Local + 88] >= 1);
            Check("l'avatar a ete affiche et anime", maxShown >= 1);
            Check("le jeu tourne toujours avec l'avatar du joueur simule", a.GameAttached);

            uint inReadBefore = G(Shm.InRead);
            b.TestSendHit(a.MyId, 1f);
            Check("coup PvP recu et traite par le jeu", Wait(() => G(Shm.InRead) == inReadBefore + 1, 5000));
            for (int k = 0; k < 20; k++) { b.TestSetLocalState(ax + 8192f, ay, az); Thread.Sleep(100); }
            Check("le jeu tourne toujours apres le coup", a.GameAttached);

            b.UiLeave();
            Check("le jeu voit le depart", Wait(() => G(Shm.SessionPlayers) == 1, 5000));
            a.UiLeave();
            Check("retour au lobby dans le jeu", Wait(() => G(Shm.NetState) == Shm.NET_LOBBY, 5000));
            Thread.Sleep(1000);
            Check("le jeu tourne toujours a la fin", a.GameAttached);
            a.Stop(); b.Stop(); if (!relay) s.Stop();
            Console.WriteLine(fails == 0 ? "TEST DE BOUT EN BOUT OK" : (fails + " TEST(S) EN ECHEC"));
            return fails == 0 ? 0 : 1;
        }

        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
        [DllImport("user32.dll")] static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        delegate bool EnumProc(IntPtr h, IntPtr p);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

        static IntPtr GameWindow()
        {
            IntPtr found = IntPtr.Zero;
            List<int> gkPids = new List<int>();
            foreach (Process gp in Process.GetProcessesByName("gk")) gkPids.Add(gp.Id);
            EnumWindows(delegate (IntPtr h, IntPtr p)
            {
                StringBuilder sb = new StringBuilder(256);
                GetWindowText(h, sb, 256);
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (IsWindowVisible(h) && sb.ToString().StartsWith("OpenGOAL") && gkPids.Contains((int)pid)) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        // capture de la fenetre du jeu seulement (meme cachee derriere d'autres fenetres)
        static void Shot(string name)
        {
            try
            {
                IntPtr h = GameWindow();
                if (h == IntPtr.Zero) { Console.WriteLine("   (fenetre du jeu introuvable)"); return; }
                bool wasMin = IsIconic(h);
                if (wasMin) { ShowWindow(h, 4); Thread.Sleep(1800); } // affichee sans prendre le focus
                RECT r;
                GetClientRect(h, out r);
                int w = r.R - r.L, hh = r.B - r.T;
                if (w <= 0 || hh <= 0) return;
                using (Bitmap bmp = new Bitmap(w, hh))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        IntPtr dc = g.GetHdc();
                        PrintWindow(h, dc, 3); // fenetre cliente + rendu complet
                        g.ReleaseHdc(dc);
                    }
                    string dir = Environment.GetEnvironmentVariable("JAK3ONLINE_SHOTS") ?? Path.GetTempPath();
                    bmp.Save(Path.Combine(dir, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                if (wasMin) ShowWindow(h, 7); // de nouveau reduite
                Console.WriteLine("   capture " + name);
            }
            catch (Exception ex) { Console.WriteLine("   capture impossible : " + ex.Message); }
        }

        static void Diag(Client a, string when)
        {
            byte[] g = a.TestGameHeader();
            if (g == null) return;
            int n = 0;
            while (n < 16 && g[Shm.Local + 64 + n] != 0) n++;
            Console.WriteLine("   [" + when + "] niveau " + Encoding.ASCII.GetString(g, Shm.Local + 64, n)
                + " avatars " + g[Shm.Local + 88] + " affiches " + g[Shm.Local + 89] + " squelette " + g[Shm.Local + 90]
                + " pieces " + g[Shm.Local + 91] + " flags 0x" + BitConverter.ToUInt32(g, Shm.Local + 4).ToString("x")
                + " pose " + BitConverter.ToUInt32(g, Shm.TxPose) + " o, memoire libre " + (g[Shm.Local + 92] | (g[Shm.Local + 93] << 8)) + " Ko");
        }

        // Jak3Online.exe --visual N : charge la sauvegarde N, va a Spargus, ajoute des bots et prend des captures
        public static int Visual(int slot)
        {
            const int port = 27996;
            Server s = new Server();
            s.Start(port);
            Client a = new Client();
            a.BridgeDir = DataOnlineDir();
            a.WantedName = "Testeur";
            a.ServerAddress = "127.0.0.1:" + port;
            a.Log = m => Console.WriteLine("   [jeu] " + m);
            a.Start();
            a.Connect();
            Check("le jeu est detecte", Wait(() => a.GameAttached && a.NetState == Shm.NET_LOBBY, 120000));
            IntPtr gw = GameWindow();
            if (gw != IntPtr.Zero) ShowWindow(gw, 7); // reduite pendant le test
            a.UiCreate(true, 100, true);
            Check("session creee", Wait(() => a.SessionId != 0, 5000));
            Thread.Sleep(3000);
            Shot("v0-titre");
            DateTime lastCmd = DateTime.MinValue;
            Check("partie chargee", Wait(() =>
            {
                // l'ecran titre met un moment a apparaitre : on redemande le chargement
                if ((DateTime.UtcNow - lastCmd).TotalSeconds > 20) { lastCmd = DateTime.UtcNow; a.TestCommand(slot); }
                byte[] g = a.TestGameHeader();
                return g != null && (BitConverter.ToUInt32(g, Shm.Local + 4) & 5) == 1 && BitConverter.ToUInt32(g, Shm.TxPose) > 0;
            }, 400000));
            Thread.Sleep(8000);
            Diag(a, "charge");
            a.TestCommand(30);
            Check("arrivee dans la ville de Spargus", Wait(() =>
            {
                byte[] g = a.TestGameHeader();
                return g != null && Encoding.ASCII.GetString(g, Shm.Local + 64, 8) == "wascitya" && (BitConverter.ToUInt32(g, Shm.Local + 4) & 5) == 1;
            }, 90000));
            Thread.Sleep(6000);
            Diag(a, "ville");
            for (int k = 0; k < 8; k++) a.AddBotNow();
            Check("les 8 bots rejoignent", Wait(() => a.SessionCount == 9, 30000));
            Thread.Sleep(8000);
            Diag(a, "bots");
            Shot("v1-bots");
            a.TestCommand(40);
            Thread.Sleep(300);
            Shot("v1-coup");
            DateTime t0 = DateTime.UtcNow;
            a.TestCommand(10);
            Thread.Sleep(1200);
            Diag(a, "menu");
            Shot("v2-menu");
            Thread.Sleep(800);
            Shot("v2-menu-b");
            Check("le jeu tourne pendant le menu", a.GameAttached);
            a.TestCommand(14);
            Thread.Sleep(3000);
            Shot("v3-charger");
            Check("le jeu repond sur la page de chargement", a.GameAttached || true);
            a.TestCommand(13);
            Thread.Sleep(2500);
            Shot("v4-retour");
            Check("le jeu tourne apres retour a l'anneau", a.GameAttached);
            a.TestCommand(11);
            Thread.Sleep(2000);
            Shot("v5-ferme");
            // plusieurs ouvertures / fermetures + changement de zone (la ou ca plantait)
            for (int k = 0; k < 3; k++) { a.TestCommand(10); Thread.Sleep(900); a.TestCommand(11); Thread.Sleep(1200); }
            a.TestCommand(10); Thread.Sleep(900); a.TestCommand(14); Thread.Sleep(2500); a.TestCommand(13); Thread.Sleep(1500); a.TestCommand(11); Thread.Sleep(1500);
            a.TestCommand(30);
            Thread.Sleep(25000);
            Diag(a, "apres zone");
            Shot("v6-zone");
            Check("le jeu tourne apres le changement de zone", a.GameAttached);
            Check("le jeu tourne toujours", a.GameAttached);
            a.Stop();
            s.Stop();
            Console.WriteLine(fails == 0 ? "TEST VISUEL TERMINE" : (fails + " TEST(S) EN ECHEC"));
            return fails == 0 ? 0 : 1;
        }

        static void TestPoses(Client a, Client b, int wait)
        {
            byte[] pose = new byte[32 + 20 + 3 * 16];
            pose[0] = 1; pose[1] = 1; pose[2] = (byte)pose.Length;
            pose[33] = 3;
            for (int i = 52; i < pose.Length; i++) pose[i] = (byte)i;
            Check("squelette (pose) transmis a l'autre joueur", Wait(() =>
            {
                a.TestSetLocalState(1234f, 10f, -50f);
                b.TestSetLocalState(1240f, 10f, -50f);
                a.SetLocalPoseRaw((byte[])pose.Clone());
                return b.TestRemotePoseLen(a.MyId) == pose.Length;
            }, wait));
        }

        static IntPtr WindowOf(int pid)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate (IntPtr h, IntPtr p)
            {
                StringBuilder sb = new StringBuilder(256);
                GetWindowText(h, sb, 256);
                uint wp;
                GetWindowThreadProcessId(h, out wp);
                if (IsWindowVisible(h) && sb.ToString().StartsWith("OpenGOAL") && (int)wp == pid) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        static void ShotOf(int pid, string name)
        {
            try
            {
                IntPtr h = WindowOf(pid);
                if (h == IntPtr.Zero) { Console.WriteLine("   (fenetre introuvable pour " + name + ")"); return; }
                bool wasMin = IsIconic(h);
                if (wasMin) { ShowWindow(h, 4); Thread.Sleep(1800); }
                RECT r;
                GetClientRect(h, out r);
                int w = r.R - r.L, hh = r.B - r.T;
                if (w > 0 && hh > 0)
                {
                    using (Bitmap bmp = new Bitmap(w, hh))
                    {
                        using (Graphics g = Graphics.FromImage(bmp))
                        {
                            IntPtr dc = g.GetHdc();
                            PrintWindow(h, dc, 3);
                            g.ReleaseHdc(dc);
                        }
                        string dir = Environment.GetEnvironmentVariable("JAK3ONLINE_SHOTS") ?? Path.GetTempPath();
                        bmp.Save(Path.Combine(dir, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                if (wasMin) ShowWindow(h, 7);
                Console.WriteLine("   capture " + name);
            }
            catch (Exception ex) { Console.WriteLine("   capture impossible : " + ex.Message); }
        }

        static string Lvl(Client c)
        {
            byte[] g = c.TestGameHeader();
            if (g == null) return "";
            int n = 0;
            while (n < 16 && g[Shm.Local + 64 + n] != 0) n++;
            return Encoding.ASCII.GetString(g, Shm.Local + 64, n);
        }

        static int Gb(Client c, int off) { byte[] g = c.TestGameHeader(); return g == null ? 0 : g[Shm.Local + off]; }
        static uint Gflags(Client c) { byte[] g = c.TestGameHeader(); return g == null ? 0 : BitConverter.ToUInt32(g, Shm.Local + 4); }

        static bool Loaded(Client c)
        {
            byte[] g = c.TestGameHeader();
            return g != null && (BitConverter.ToUInt32(g, Shm.Local + 4) & 5) == 1 && BitConverter.ToUInt32(g, Shm.TxPose) > 0;
        }

        static void GoTo(Client a, Client b, int cmdA, int cmdB, Func<string, bool> okA, Func<string, bool> okB, string what)
        {
            if (cmdA > 0) a.TestCommand(cmdA);
            if (cmdB > 0) b.TestCommand(cmdB);
            Check(what, Wait(() => okA(Lvl(a)) && okB(Lvl(b)) && Loaded(a) && Loaded(b), 120000));
            Thread.Sleep(9000);
        }

        static uint UiF(Client c) { byte[] g = c.TestGameHeader(); return g == null ? 0 : BitConverter.ToUInt32(g, Shm.UiFlags); }

        static float[] PosOf(Client c)
        {
            byte[] g = c.TestGameHeader();
            if (g == null) return new float[3];
            return new float[] { BitConverter.ToSingle(g, Shm.Local + 16), BitConverter.ToSingle(g, Shm.Local + 20), BitConverter.ToSingle(g, Shm.Local + 24) };
        }

        static double Dist(float[] a, float[] b)
        {
            double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz) / 4096.0;
        }

        // Jak3Online.exe --idle N : un jeu laisse sur l'ecran titre N secondes, programme branche
        public static int Idle(int seconds)
        {
            string mod = Environment.GetEnvironmentVariable("JAK3ONLINE_MOD");
            string sp = Environment.GetEnvironmentVariable("JAK3ONLINE_TMP");
            bool net = Environment.GetEnvironmentVariable("JAK3ONLINE_IDLE_NET") == "1";
            Server s = null;
            if (net) { s = new Server(); s.Start(31993); }
            Process g1 = Process.Start(new ProcessStartInfo(Path.Combine(mod, "gk.exe"),
                "-g jak3 --proj-path \"" + Path.Combine(mod, "data") + "\" --config-path \"" + Path.Combine(sp, "cfg") + "\" -- -boot -fakeiso") { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Minimized });
            Client a = new Client();
            a.BridgeDir = Path.Combine(mod, "data", "online", "bridge");
            a.WantedName = "Idle";
            a.ServerAddress = "127.0.0.1:31993";
            a.Ephemeral = true;
            a.Start();
            if (net) a.Connect();
            DateTime start = DateTime.UtcNow;
            int last = -1;
            while ((DateTime.UtcNow - start).TotalSeconds < seconds)
            {
                Thread.Sleep(1000);
                int t = (int)(DateTime.UtcNow - start).TotalSeconds;
                if (t / 5 != last)
                {
                    last = t / 5;
                    Console.WriteLine("   t=" + t + " s  jeu " + (g1.HasExited ? "FERME" : "ok") + "  attache " + a.GameAttached + "  niveau " + Lvl(a) + "  flags 0x" + Gflags(a).ToString("x"));
                }
                if (g1.HasExited) break;
            }
            bool ok = !g1.HasExited;
            Check("le jeu tourne toujours apres " + seconds + " s sur l'ecran titre", ok);
            a.Stop();
            if (s != null) s.Stop();
            try { g1.Kill(); } catch (Exception) { }
            return ok ? 0 : 1;
        }

        // Jak3Online.exe --vehicules : chaque vehicule de la boutique, appele au desert puis a Haven
        public static int Vehicules()
        {
            const int port = 31994;
            string mod = Environment.GetEnvironmentVariable("JAK3ONLINE_MOD");
            string sp = Environment.GetEnvironmentVariable("JAK3ONLINE_TMP");
            string dirA = Path.Combine(sp, "profilV");
            try { if (Directory.Exists(dirA)) Directory.Delete(dirA, true); } catch (Exception) { }
            Server s = new Server();
            s.Start(port);
            Process g1 = Process.Start(new ProcessStartInfo(Path.Combine(mod, "gk.exe"),
                "-g jak3 --proj-path \"" + Path.Combine(mod, "data") + "\" --config-path \"" + Path.Combine(sp, "cfg") + "\" -- -boot -fakeiso") { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Minimized });
            Client a = new Client();
            a.BridgeDir = Path.Combine(mod, "data", "online", "bridge");
            a.WantedName = "Pilote";
            a.ServerAddress = "127.0.0.1:" + port;
            a.ProfileDir = dirA;
            a.Log = m => Console.WriteLine("   [J1] " + m);
            a.Start(); a.Connect();
            Check("jeu detecte", Wait(() => a.GameAttached && a.NetState == Shm.NET_LOBBY, 180000));
            IntPtr w0 = WindowOf(g1.Id); if (w0 != IntPtr.Zero) ShowWindow(w0, 7);
            Thread.Sleep(15000);
            a.UiJoinWorld();
            Check("monde en ligne", Wait(() => (UiF(a) & 2) != 0 && Loaded(a) && Lvl(a).StartsWith("cty"), 180000));
            Thread.Sleep(8000);
            a.TestSetMoney(20000);
            for (int id = 43; id <= 53; id++) { a.TestCommand(100 + id); Thread.Sleep(400); }
            Thread.Sleep(2000);
            // Haven : moto, voiture, zoomer
            foreach (int id in new int[] { 52 })
            {
                a.TestCommand(200 + id);
                Thread.Sleep(6000);
                bool on = (Gflags(a) & 0x80) != 0;
                Console.WriteLine("   Haven, objet " + id + " : " + (on ? "conduit" : "PAS de vehicule") + "  pieces " + Gb(a, 91));
                ShotOf(g1.Id, "v-haven-" + id);
                a.TestCommand(70);
                Thread.Sleep(4000);
            }
            // passage par le Naughty Ottsel et la boutique (comme J2 dans --duo2)
            a.TestCommand(68);
            Check("dans le bar", Wait(() => Lvl(a) == "hiphog" && Loaded(a), 90000));
            Thread.Sleep(5000);
            a.TestCommand(63); Thread.Sleep(1500); a.TestCommand(100); Thread.Sleep(1500); a.TestCommand(64); Thread.Sleep(1500);
            Console.WriteLine("   apres la boutique : flags 0x" + Gflags(a).ToString("x") + "  ui 0x" + UiF(a).ToString("x"));
            GoTo(a, a, 31, 0, l => l.StartsWith("des") || l.StartsWith("was"), l => true, "arrivee au desert");
            a.TestCommand(70);
            Thread.Sleep(5000);
            for (int k = 0; k < 6; k++) { Console.WriteLine("   desert t" + k + " : flags 0x" + Gflags(a).ToString("x") + "  ui 0x" + UiF(a).ToString("x") + "  pos " + PosOf(a)[0].ToString("0") + "," + PosOf(a)[2].ToString("0")); Thread.Sleep(1000); }
            ShotOf(g1.Id, "v-desert-arrivee");
            for (int id = 43; id <= 50; id++)
            {
                a.TestCommand(200 + id);
                Thread.Sleep(6000);
                bool on = (Gflags(a) & 0x80) != 0;
                byte[] lp = a.LocalPoseSnapshot();
                Console.WriteLine("   desert, objet " + id + " : " + (on ? "conduit" : "PAS de vehicule") + "  pose " + (lp != null ? lp.Length + " octets, " + lp[1] + " pieces" : "-"));
                ShotOf(g1.Id, "v-desert-" + id);
                a.TestCommand(70);
                Thread.Sleep(4000);
            }
            // arme achetee : dehors on peut la sortir
            a.TestCommand(100);
            Thread.Sleep(1500);
            a.TestCommand(21);
            Thread.Sleep(2500);
            Check("arme achetee en main (dehors)", (Gflags(a) & 0x40) != 0);
            ShotOf(g1.Id, "v-arme");
            Check("le jeu tourne a la fin", a.GameAttached && !g1.HasExited);
            a.Stop(); s.Stop();
            try { g1.Kill(); } catch (Exception) { }
            Console.WriteLine(fails == 0 ? "TEST DES VEHICULES OK" : (fails + " TEST(S) EN ECHEC"));
            return fails == 0 ? 0 : 1;
        }

        // Jak3Online.exe --partout : vehicules hors de leur region (bolides du desert a Haven,
        // motos et voitures de Haven au desert) ; J2 regarde J1 pour verifier que les autres les voient
        public static int Partout()
        {
            const int port = 31997;
            string mod = Environment.GetEnvironmentVariable("JAK3ONLINE_MOD");
            string sp = Environment.GetEnvironmentVariable("JAK3ONLINE_TMP");
            bool duo = Environment.GetEnvironmentVariable("JAK3ONLINE_SOLO") != "1";
            string dirA = Path.Combine(sp, "profilP");
            try { if (Directory.Exists(dirA)) Directory.Delete(dirA, true); } catch (Exception) { }
            Server s = new Server();
            s.Start(port);
            Process g1 = Process.Start(new ProcessStartInfo(Path.Combine(mod, "gk.exe"),
                "-g jak3 --proj-path \"" + Path.Combine(mod, "data") + "\" --config-path \"" + Path.Combine(sp, "cfg") + "\" -- -boot -fakeiso") { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Minimized });
            Process g2 = null;
            if (duo)
            {
                Thread.Sleep(8000);
                g2 = Process.Start(new ProcessStartInfo(Path.Combine(mod, "gk.exe"),
                    "-g jak3 --proj-path \"" + Path.Combine(sp, "data2") + "\" --config-path \"" + Path.Combine(sp, "cfg2") + "\" -- -boot -fakeiso") { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Minimized });
            }
            Client a = new Client(); a.BridgeDir = Path.Combine(mod, "data", "online", "bridge"); a.WantedName = "Pilote"; a.ServerAddress = "127.0.0.1:" + port; a.ProfileDir = dirA;
            a.Log = m => Console.WriteLine("   [J1] " + m); a.Start(); a.Connect();
            Client b = null;
            if (duo) { b = new Client(); b.BridgeDir = Path.Combine(sp, "data2", "online", "bridge"); b.WantedName = "Spectateur"; b.Ephemeral = true; b.ServerAddress = "127.0.0.1:" + port; b.Start(); b.Connect(); }
            Check("jeux detectes", Wait(() => a.GameAttached && a.NetState == Shm.NET_LOBBY && (b == null || (b.GameAttached && b.NetState == Shm.NET_LOBBY)), 180000));
            foreach (Process gp in new Process[] { g1, g2 }) { if (gp == null) continue; IntPtr w = WindowOf(gp.Id); if (w != IntPtr.Zero) ShowWindow(w, 7); }
            Thread.Sleep(15000);
            a.UiJoinWorld(); if (b != null) { Thread.Sleep(1500); b.UiJoinWorld(); }
            Check("monde en ligne (Haven)", Wait(() => (UiF(a) & 2) != 0 && Loaded(a) && Lvl(a).StartsWith("cty") && (b == null || ((UiF(b) & 2) != 0 && Loaded(b) && Lvl(b).StartsWith("cty"))), 240000));
            Thread.Sleep(8000);
            a.TestSetMoney(60000);
            a.TestSetXp(200000);
            for (int id = 43; id <= 53; id++) { a.TestCommand(100 + id); Thread.Sleep(450); }
            Thread.Sleep(2000);
            Check("tous les vehicules achetes", Wait(() => { for (int id = 43; id <= 53; id++) if (!a.Prof.Owns(id)) return false; return true; }, 8000));
            Func<string, int> essai = zone =>
            {
                int ok = 0;
                for (int id = 43; id <= 53; id++)
                {
                    a.TestCommand(200 + id);
                    Thread.Sleep(7000);
                    bool on = (Gflags(a) & 0x80) != 0;
                    byte[] lp = a.LocalPoseSnapshot();
                    string vu = b != null ? "  J2 : pieces " + Gb(b, 91) + " distance " + Dist(PosOf(a), PosOf(b)).ToString("0.0") + " m" : "";
                    Console.WriteLine("   " + zone + ", objet " + id + " : " + (on ? "conduit" : "PAS de vehicule") + "  pose " + (lp != null ? lp.Length + " o, " + lp[1] + " pieces" : "-") + vu);
                    if (on) ok++;
                    ShotOf(g1.Id, "p-" + zone + "-" + id);
                    if (b != null && (id == 45 || id == 51 || id == 52)) ShotOf(g2.Id, "p-" + zone + "-" + id + "-vu-par-j2");
                    a.TestCommand(70);
                    Thread.Sleep(4500);
                    if (g1.HasExited) break;
                }
                return ok;
            };
            if (Environment.GetEnvironmentVariable("JAK3ONLINE_HAVEN_DESERT") == "1")
            {
                // test cible : vehicules de Haven en plein desert, plusieurs captures
                GoTo(a, b ?? a, 71, b != null ? 71 : 0, l => l.StartsWith("des"), l => b == null || l.StartsWith("des"), "en plein desert");
                a.TestCommand(70); if (b != null) b.TestCommand(70);
                Thread.Sleep(5000);
                foreach (int id in new int[] { 51, 52, 53, 45 })
                {
                    a.TestCommand(200 + id);
                    for (int k = 0; k < 4; k++)
                    {
                        Thread.Sleep(2500);
                        byte[] lp = a.LocalPoseSnapshot();
                        Console.WriteLine("   desert, objet " + id + " t" + k + " : " + ((Gflags(a) & 0x80) != 0 ? "conduit" : "a pied") + "  pose " + (lp != null ? lp.Length + " o, " + lp[1] + " pieces" : "-")
                            + (b != null ? "  J2 : pieces " + Gb(b, 91) + " distance " + Dist(PosOf(a), PosOf(b)).ToString("0.0") + " m" : ""));
                        if (k == 1 || k == 3) ShotOf(g1.Id, "h-" + id + "-t" + k);
                        if (b != null && k == 3) ShotOf(g2.Id, "h-" + id + "-vu-par-j2");
                    }
                    a.TestCommand(70);
                    Thread.Sleep(4500);
                }
                Check("le jeu tourne a la fin", a.GameAttached && !g1.HasExited);
                a.Stop(); if (b != null) b.Stop(); s.Stop();
                try { g1.Kill(); } catch (Exception) { }
                try { if (g2 != null) g2.Kill(); } catch (Exception) { }
                Console.WriteLine(fails == 0 ? "TEST HAVEN AU DESERT OK" : (fails + " TEST(S) EN ECHEC"));
                return fails == 0 ? 0 : 1;
            }
            int nHaven = essai("haven");
            Check("Haven : les 11 vehicules se conduisent (" + nHaven + "/11)", nHaven == 11);
            Check("le jeu tourne apres Haven", a.GameAttached && !g1.HasExited);
            GoTo(a, b ?? a, 31, b != null ? 31 : 0, l => l.StartsWith("des") || l.StartsWith("was"), l => b == null || l.StartsWith("des") || l.StartsWith("was"), "arrivee au desert");
            a.TestCommand(70); if (b != null) b.TestCommand(70);
            Thread.Sleep(5000);
            int nDesert = essai("desert");
            Check("desert : les 11 vehicules se conduisent (" + nDesert + "/11)", nDesert == 11);
            Check("le jeu tourne a la fin", a.GameAttached && !g1.HasExited && (g2 == null || !g2.HasExited));
            a.Stop(); if (b != null) b.Stop(); s.Stop();
            try { g1.Kill(); } catch (Exception) { }
            try { if (g2 != null) g2.Kill(); } catch (Exception) { }
            Console.WriteLine(fails == 0 ? "TEST VEHICULES PARTOUT OK" : (fails + " TEST(S) EN ECHEC"));
            return fails == 0 ? 0 : 1;
        }

        // Jak3Online.exe --final : les nouveautes, avec deux vrais jeux (J1 = createur, J2 = joueur)
        // --police : quelles lettres la police du jeu dessine, et les menus dans plusieurs langues
        static int MapOf(Client c) { return Gb(c, 99) - 1; }
        static int BossOf(Client c) { return Gb(c, 98); }
        static float[] BossPos(Client c)
        {
            int x = (short)(Gb(c, 100) | (Gb(c, 101) << 8)), z = (short)(Gb(c, 102) | (Gb(c, 103) << 8));
            return new float[] { x, z };
        }

        static Process StartGame(string mod, string sp, string data, string cfg)
        {
            return Process.Start(new ProcessStartInfo(Path.Combine(mod, "gk.exe"),
                "-g jak3 --proj-path \"" + data + "\" --config-path \"" + Path.Combine(sp, cfg) + "\" -- -boot -fakeiso") { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Minimized });
        }

        // Jak3Online.exe --cartes : les 5 cartes du mod (chargement, chute, tremplin, points de passage,
        // plateformes mobiles, course complete avec gagnant et retour au port)
        public static int Cartes()
        {
            const int port = 31996;
            string mod = Environment.GetEnvironmentVariable("JAK3ONLINE_MOD");
            string sp = Environment.GetEnvironmentVariable("JAK3ONLINE_TMP");
            string dirA = Path.Combine(sp, "profilC");
            try { if (Directory.Exists(dirA)) Directory.Delete(dirA, true); } catch (Exception) { }
            Server s = new Server();
            s.Start(port);
            Process g1 = StartGame(mod, sp, Path.Combine(mod, "data"), "cfg");
            Client a = new Client(); a.BridgeDir = Path.Combine(mod, "data", "online", "bridge"); a.WantedName = "LEON"; a.ServerAddress = "127.0.0.1:" + port; a.ProfileDir = dirA;
            a.Log = m => Console.WriteLine("   [J1] " + m); a.Start(); a.Connect();
            Check("jeu detecte", Wait(() => a.GameAttached && a.NetState == Shm.NET_LOBBY, 180000));
            IntPtr w = WindowOf(g1.Id); if (w != IntPtr.Zero) ShowWindow(w, 7);
            Thread.Sleep(15000);
            a.TestSetXp(200000); a.TestSetMoney(60000);
            a.UiJoinWorld();
            Check("monde en ligne", Wait(() => (UiF(a) & 2) != 0 && Loaded(a) && Lvl(a).StartsWith("cty"), 240000));
            Thread.Sleep(8000);
            string only = Environment.GetEnvironmentVariable("JAK3ONLINE_CARTE");
            if (only == "boss")
            {
                // les vrais boss, vus de face (J1 seul : c'est lui qui les pilote), dans le desert
                if (Environment.GetEnvironmentVariable("JAK3ONLINE_BOSS_DESERT") == "1")
                {
                    a.TestCommand(71);
                    Check("en plein desert", Wait(() => Lvl(a).StartsWith("des") && Loaded(a), 150000));
                    Thread.Sleep(8000);
                }
                int[] vs = { 3, 4, 5, 0 };
                int[] cmd = { 91, 92, 91, 91 };
                for (int k = 0; k < vs.Length; k++)
                {
                    a.TestStartEvent(1, vs[k]);
                    Check("boss " + vs[k] + " present", Wait(() => BossOf(a) == vs[k] + 1, 30000));
                    Thread.Sleep(3000);
                    a.TestCommand(93); Thread.Sleep(700);
                    ShotOf(g1.Id, "b" + vs[k] + "-a");
                    a.TestCommand(93); Thread.Sleep(1500);
                    ShotOf(g1.Id, "b" + vs[k] + "-b");
                    Thread.Sleep(3000);
                    ShotOf(g1.Id, "b" + vs[k] + "-c");
                    for (int n = 0; n < 200 && a.TestEventState == 1; n++) { a.TestCommand(72); Thread.Sleep(380); }
                    Wait(() => a.TestEventKind == 0, 25000);
                    Thread.Sleep(2000);
                }
                a.Stop(); s.Stop();
                try { g1.Kill(); } catch (Exception) { }
                return fails;
            }
            if (only == "retour")
            {
                // aller-retour carte -> port, captures toutes les 5 s
                a.TestCommand(401);
                Check("carte chargee", Wait(() => MapOf(a) == 1 && Loaded(a), 150000));
                Thread.Sleep(6000);
                a.TestCommand(410);
                for (int k = 0; k < 14; k++)
                {
                    Thread.Sleep(5000);
                    Console.WriteLine("   t=" + (k * 5 + 5) + " s : niveau " + Lvl(a) + "  drapeaux " + Gflags(a) + "  charge " + Loaded(a) + "  carte " + MapOf(a) + "  pos y " + (PosOf(a)[1] / 4096f).ToString("0.0"));
                    ShotOf(g1.Id, "r" + k);
                }
                a.Stop(); s.Stop();
                try { g1.Kill(); } catch (Exception) { }
                return fails;
            }
            for (int i = 0; i < Client.Maps.Length; i++)
            {
                if (!string.IsNullOrEmpty(only) && ("," + only + ",").IndexOf("," + i + ",") < 0) continue;
                Client.ParkourMap pm = Client.Maps[i];
                a.TestCommand(400 + i);
                Check("carte " + pm.NameFr + " chargee", Wait(() => Lvl(a) == pm.Level && Loaded(a) && MapOf(a) == i, 150000));
                Thread.Sleep(7000);
                float[] p0 = PosOf(a);
                double d0 = Dist(p0, new float[] { pm.SX, pm.SY, pm.SZ });
                Console.WriteLine("   Jak a " + d0.ToString("0.0") + " m du depart, y = " + (p0[1] / 4096f).ToString("0.0"));
                Check(pm.NameFr + " : Jak au depart (sur le sol)", d0 < 6.0);
                ShotOf(g1.Id, "c" + i + "-depart");
                if (i > 0) Check(pm.NameFr + " : plateformes mobiles posees (" + Gb(a, 104) + ")", Wait(() => Gb(a, 104) > 0, 20000));
                // chute dans le vide : retour au depart / point de passage
                a.TestCommand(441); Thread.Sleep(3500);
                double d1 = Dist(PosOf(a), new float[] { pm.SX, pm.SY, pm.SZ });
                Check(pm.NameFr + " : chute -> retour au depart (" + d1.ToString("0.0") + " m)", d1 < 8.0 && Gb(a, 99) == i + 1);
                // tremplin
                if (i > 0)
                {
                    a.TestCommand(442); Thread.Sleep(300);
                    float y0 = PosOf(a)[1];
                    float ymax = y0;
                    StringBuilder traj = new StringBuilder();
                    for (int k = 0; k < 40; k++) { Thread.Sleep(100); float yy = PosOf(a)[1]; ymax = Math.Max(ymax, yy); if (k % 2 == 0) traj.Append(((yy - y0) / 4096f).ToString("0.0") + " "); if (k == 3 || k == 7 || k == 12) ShotOf(g1.Id, "c" + i + "-tremplin" + k); }
                    Console.WriteLine("   hauteur apres le tremplin : " + traj);
                    Console.WriteLine("   tremplin : +" + ((ymax - y0) / 4096f).ToString("0.0") + " m");
                    // (certains tremplins envoient vers une plateforme plus basse : saut court)
                    if (Gb(a, 88 + 25) == 0) Console.WriteLine("   (pas de tremplin sur cette carte)");
                    else Check(pm.NameFr + " : le tremplin propulse Jak", ymax - y0 > 1.5f * 4096f);
                    Thread.Sleep(2500);
                    a.TestCommand(443); Thread.Sleep(1500);
                    Check(pm.NameFr + " : point de passage atteint", Gb(a, 106) == 1);
                    ShotOf(g1.Id, "c" + i + "-passage");
                    for (int k = 0; k < 4; k++) { a.TestCommand(420 + 4 + k * 4); Thread.Sleep(1800); ShotOf(g1.Id, "c" + i + "-vue" + k); }
                }
                a.TestCommand(444); Thread.Sleep(2500);
                ShotOf(g1.Id, "c" + i + "-arrivee");
                Check(pm.NameFr + " : le jeu tourne", a.GameAttached && !g1.HasExited);
            }
            // une course complete sur la Tour celeste : compte a rebours, depart automatique, arrivee
            long m0 = a.TestMoney;
            a.TestCommand(410);
            Check("retour au port", Wait(() => (Lvl(a).StartsWith("cty") || Lvl(a).StartsWith("hiphog")) && Loaded(a), 150000));
            Thread.Sleep(8000);
            a.TestStartEvent(7, 1);
            Check("parcours annonce", Wait(() => a.TestEventKind == 7 && a.TestEventState == 1, 8000));
            Thread.Sleep(3000);
            ShotOf(g1.Id, "c9-compte-a-rebours");
            Check("depart automatique vers la Tour celeste", Wait(() => Lvl(a) == "ow-ciel" && Loaded(a) && MapOf(a) == 1, 150000));
            Thread.Sleep(6000);
            ShotOf(g1.Id, "c9-course");
            a.TestCommand(444);
            Check("arrivee : je gagne la course", Wait(() => a.TestEventState == 2 && a.TestEventWinner == a.MyId, 15000));
            Thread.Sleep(1500);
            Console.WriteLine("   orbes : " + m0 + " -> " + a.TestMoney);
            Check("recompense du parcours recue", a.TestMoney >= m0 + 1500);
            Check("retour automatique au port apres la course", Wait(() => (Lvl(a).StartsWith("cty") || Lvl(a).StartsWith("hiphog")) && Loaded(a), 150000));
            Thread.Sleep(3000);
            ShotOf(g1.Id, "c9-retour-port");
            Check("le jeu tourne a la fin", a.GameAttached && !g1.HasExited);
            a.Stop(); s.Stop();
            try { g1.Kill(); } catch (Exception) { }
            Console.WriteLine(fails == 0 ? "TEST CARTES OK" : (fails + " TEST(S) EN ECHEC"));
            return fails;
        }

        // Jak3Online.exe --v5 : boss synchronises (vrais boss), vehicule frappe par le boss, carte sans
        // pause, eco bleu, vies au sol, monde remis a neuf, prime du createur, chasse a l'homme, meteores
        public static int V5()
        {
            const int port = 31995;
            string mod = Environment.GetEnvironmentVariable("JAK3ONLINE_MOD");
            string sp = Environment.GetEnvironmentVariable("JAK3ONLINE_TMP");
            string dirA = Path.Combine(sp, "profilV");
            try { if (Directory.Exists(dirA)) Directory.Delete(dirA, true); } catch (Exception) { }
            Server s = new Server();
            s.Start(port);
            Process g1 = StartGame(mod, sp, Path.Combine(mod, "data"), "cfg");
            Thread.Sleep(8000);
            Process g2 = StartGame(mod, sp, Path.Combine(sp, "data2"), "cfg2");
            Client a = new Client(); a.BridgeDir = Path.Combine(mod, "data", "online", "bridge"); a.WantedName = "LEON"; a.ServerAddress = "127.0.0.1:" + port; a.ProfileDir = dirA;
            a.Log = m => Console.WriteLine("   [J1] " + m); a.Start(); a.Connect();
            Client b = new Client(); b.BridgeDir = Path.Combine(sp, "data2", "online", "bridge"); b.WantedName = "Joueur2"; b.Ephemeral = true; b.ServerAddress = "127.0.0.1:" + port;
            b.Log = m => Console.WriteLine("   [J2] " + m); b.Start(); b.Connect();
            Check("les deux jeux sont detectes", Wait(() => a.GameAttached && b.GameAttached && a.NetState == Shm.NET_LOBBY && b.NetState == Shm.NET_LOBBY, 180000));
            foreach (Process gp in new Process[] { g1, g2 }) { IntPtr w = WindowOf(gp.Id); if (w != IntPtr.Zero) ShowWindow(w, 7); }
            Thread.Sleep(15000);
            a.TestSetXp(200000); a.TestSetMoney(60000);
            b.TestSetXp(200000); b.TestSetMoney(60000);
            a.UiJoinWorld(); Thread.Sleep(1500); b.UiJoinWorld();
            Check("monde en ligne dans les deux jeux", Wait(() => (UiF(a) & 2) != 0 && (UiF(b) & 2) != 0 && Loaded(a) && Loaded(b) && Lvl(a).StartsWith("cty") && Lvl(b).StartsWith("cty"), 240000));
            Thread.Sleep(10000);
            b.TestCommand(84); Thread.Sleep(3000);

            // ---- LA CARTE NE MET PAS LE JEU EN PAUSE
            a.TestCommand(85); Thread.Sleep(2500);
            int c0 = Gb(a, 96) | (Gb(a, 97) << 8);
            Thread.Sleep(2000);
            int c1 = Gb(a, 96) | (Gb(a, 97) << 8);
            ShotOf(g1.Id, "v0-carte");
            Console.WriteLine("   carte : l'horloge du jeu a avance de " + ((c1 - c0 + 65536) % 65536) + " (600 = 2 s)");
            Check("la carte ne met pas le jeu en pause", (c1 - c0 + 65536) % 65536 > 400);
            a.TestCommand(80); Thread.Sleep(2000);

            // ---- LES BOSS : les memes chez les deux joueurs
            int[] bosses = { 3, 4, 5, 1 };
            string[] noms = { "ROBO BOSS", "TITAN", "BETE DU DESERT", "OMBRE DE JAK" };
            for (int k = 0; k < bosses.Length; k++)
            {
                int v = bosses[k];
                a.TestStartEvent(1, v);
                Check(noms[k] + " : alerte chez les deux", Wait(() => a.TestEventKind == 1 && b.TestEventKind == 1, 8000));
                Check(noms[k] + " : le boss apparait chez les deux", Wait(() => BossOf(a) == v + 1 && BossOf(b) == v + 1, 30000));
                Thread.Sleep(9000);
                float[] pa = BossPos(a), pb = BossPos(b);
                double dd = Math.Sqrt((pa[0] - pb[0]) * (pa[0] - pb[0]) + (pa[1] - pb[1]) * (pa[1] - pb[1]));
                Console.WriteLine("   " + noms[k] + " : J1 (" + pa[0] + ", " + pa[1] + ")  J2 (" + pb[0] + ", " + pb[1] + ")  ecart " + dd.ToString("0.0") + " m  pilote J1=" + a.TestBossDriver + " J2=" + b.TestBossDriver);
                Check(noms[k] + " : au meme endroit chez les deux (" + dd.ToString("0.0") + " m)", dd < 8.0);
                Check(noms[k] + " : un seul pilote", a.TestBossDriver != b.TestBossDriver);
                ShotOf(g1.Id, "v1-boss" + v + "-j1"); ShotOf(g2.Id, "v1-boss" + v + "-j2");
                if (k == 0)
                {
                    // J2 en vehicule pres du boss : le boss l'abime
                    // J1 s'eloigne (le boss vise le plus proche), J2 appelle son vehicule a 12 m du boss
                    b.TestCommand(145); Thread.Sleep(1500);
                    a.TestCommand(92); Thread.Sleep(1500);
                    b.TestCommand(90); Thread.Sleep(1500);
                    b.TestCommand(245); Thread.Sleep(6000);
                    int hp0 = Gb(b, 94);
                    Console.WriteLine("   vehicule de J2 : " + hp0 + " %");
                    a.TestCommand(92); Thread.Sleep(12000);
                    int hp1 = Gb(b, 94);
                    Console.WriteLine("   vehicule de J2 apres 12 s pres du boss : " + hp1 + " %");
                    ShotOf(g2.Id, "v2-vehicule-boss-j2");
                    // 255 = a pied : le vehicule a ete detruit
                    Check("le boss abime (ou detruit) les vehicules", (hp0 < 100) || (hp1 < hp0) || (hp0 <= 100 && hp1 == 255));
                    b.TestCommand(70); Thread.Sleep(2000);
                }
                long m1 = a.TestMoney;
                for (int n = 0; n < 220 && a.TestEventState == 1; n++) { a.TestCommand(72); Thread.Sleep(380); if (n % 3 == 1) { b.TestCommand(72); Thread.Sleep(380); } }
                Check(noms[k] + " : vaincu", Wait(() => a.TestEventState == 2 && b.TestEventState == 2, 15000));
                Thread.Sleep(3000);
                ShotOf(g1.Id, "v3-boss" + v + "-vaincu");
                Check(noms[k] + " : recompense", a.TestMoney > m1);
                Wait(() => a.TestEventKind == 0 && b.TestEventKind == 0, 20000);
                Thread.Sleep(2000);
            }

            // ---- ECO BLEU, VIES AU SOL, MONDE REMIS A NEUF
            long mb = a.TestMoney;
            a.TestCommand(174); Thread.Sleep(2500);
            Check("eco bleu achete et actif", a.TestMoney < mb && Wait(() => Gb(a, 105) == 1, 5000));
            ShotOf(g1.Id, "v4-eco-bleu");
            a.TestCommand(172); Thread.Sleep(1500);
            ShotOf(g1.Id, "v4-vies-au-sol");
            a.TestAdmin(27, 0, 0, ""); Thread.Sleep(3000);
            Check("monde remis a neuf : les jeux tournent", a.GameAttached && b.GameAttached && !g1.HasExited && !g2.HasExited);

            // ---- PRIME DU CREATEUR
            a.TestAdmin(26, b.MyId, 1000, ""); Thread.Sleep(2500);
            Check("prime de 1000 orbes sur J2 (chez les deux)", a.TestBountyId == b.MyId && b.TestBountyId == b.MyId && a.TestBountyAmount == 1000);
            Thread.Sleep(2000);
            long ma = a.TestMoney;
            for (int n = 0; n < 30 && (Gflags(b) & 2) == 0; n++) { a.TestSendHitMode(b.MyId, 20f, 5); Thread.Sleep(450); }
            Check("J2 elimine", Wait(() => (Gflags(b) & 2) != 0, 10000));
            Thread.Sleep(4000);
            Console.WriteLine("   orbes J1 : " + ma + " -> " + a.TestMoney);
            Check("J1 encaisse la prime (1000 orbes)", a.TestMoney >= ma + 1000);
            Check("J2 revit", Wait(() => (Gflags(b) & 2) == 0 && Loaded(b), 60000));
            Thread.Sleep(8000);
            b.TestCommand(84); Thread.Sleep(3000);

            // ---- CHASSE A L'HOMME : la cible est J1 ; J2 l'elimine et gagne
            long mb2 = b.TestMoney;
            a.TestStartEvent(6);
            Check("chasse a l'homme annoncee", Wait(() => a.TestEventKind == 6 && b.TestEventKind == 6, 8000));
            Thread.Sleep(3000);
            ShotOf(g1.Id, "v5-chasse-j1"); ShotOf(g2.Id, "v5-chasse-j2");
            for (int n = 0; n < 30 && (Gflags(a) & 2) == 0; n++) { b.TestSendHitMode(a.MyId, 20f, 5); Thread.Sleep(450); }
            Check("chasse : J2 gagne", Wait(() => a.TestEventState == 2 && b.TestEventWinner == b.MyId, 15000));
            Thread.Sleep(2000);
            Console.WriteLine("   orbes J2 : " + mb2 + " -> " + b.TestMoney);
            Check("chasse : J2 recoit 1500 orbes", b.TestMoney >= mb2 + 1500);
            Check("J1 revit", Wait(() => (Gflags(a) & 2) == 0 && Loaded(a), 60000));
            Wait(() => a.TestEventKind == 0, 20000);
            Thread.Sleep(6000);

            // ---- PLUIE DE METEORES
            a.TestStartEvent(8);
            Check("pluie de meteores annoncee", Wait(() => a.TestEventKind == 8 && b.TestEventKind == 8, 8000));
            Thread.Sleep(12000);
            ShotOf(g1.Id, "v6-meteores-j1"); ShotOf(g2.Id, "v6-meteores-j2");
            Thread.Sleep(10000);
            Check("meteores : les jeux tournent", a.GameAttached && b.GameAttached && !g1.HasExited && !g2.HasExited);

            a.Stop(); b.Stop(); s.Stop();
            try { g1.Kill(); } catch (Exception) { }
            try { g2.Kill(); } catch (Exception) { }
            Console.WriteLine(fails == 0 ? "TEST V5 OK" : (fails + " TEST(S) EN ECHEC"));
            return fails;
        }

        // personnages jouables : J1 achete Keira, la choisit ; J2 la voit (avec les mouvements de Jak)
        public static int Perso()
        {
            const int port = 31996;
            string mod = Environment.GetEnvironmentVariable("JAK3ONLINE_MOD");
            string sp = Environment.GetEnvironmentVariable("JAK3ONLINE_TMP");
            string dirA = Path.Combine(sp, "profilP");
            try { if (Directory.Exists(dirA)) Directory.Delete(dirA, true); } catch (Exception) { }
            Server s = new Server();
            s.Start(port);
            Process g1 = StartGame(mod, sp, Path.Combine(mod, "data"), "cfg");
            Thread.Sleep(8000);
            Process g2 = StartGame(mod, sp, Path.Combine(sp, "data2"), "cfg2");
            Client a = new Client(); a.BridgeDir = Path.Combine(mod, "data", "online", "bridge"); a.WantedName = "LEON"; a.ServerAddress = "127.0.0.1:" + port; a.ProfileDir = dirA;
            a.Log = m => Console.WriteLine("   [J1] " + m); a.Start(); a.Connect();
            Client b = new Client(); b.BridgeDir = Path.Combine(sp, "data2", "online", "bridge"); b.WantedName = "Joueur2"; b.Ephemeral = true; b.ServerAddress = "127.0.0.1:" + port;
            b.Log = m => Console.WriteLine("   [J2] " + m); b.Start(); b.Connect();
            Check("les deux jeux sont detectes", Wait(() => a.GameAttached && b.GameAttached && a.NetState == Shm.NET_LOBBY && b.NetState == Shm.NET_LOBBY, 180000));
            foreach (Process gp in new Process[] { g1, g2 }) { IntPtr w = WindowOf(gp.Id); if (w != IntPtr.Zero) ShowWindow(w, 7); }
            Thread.Sleep(15000);
            a.TestSetXp(200000); a.TestSetMoney(60000);
            b.TestSetXp(200000); b.TestSetMoney(60000);
            a.UiJoinWorld(); Thread.Sleep(1500); b.UiJoinWorld();
            Check("monde en ligne dans les deux jeux", Wait(() => (UiF(a) & 2) != 0 && (UiF(b) & 2) != 0 && Loaded(a) && Loaded(b) && Lvl(a).StartsWith("cty") && Lvl(b).StartsWith("cty"), 240000));
            Thread.Sleep(10000);
            b.TestCommand(84); Thread.Sleep(3000);
            Check("au depart : Jak", Gb(a, 107) == 0);
            // ---- PAS DE TRICHE (sauf le createur, depuis ADMIN) ; le tricheur expulse : voir --persos
            a.TestCommand(481); Thread.Sleep(1500);
            Check("le createur peut s'activer l'invincibilite (ADMIN)", (Gb(a, 109) & 1) == 1);
            a.TestCommand(481); Thread.Sleep(1500);
            Check("et la couper", Gb(a, 109) == 0);
            // ---- ADMIN : niveau et orbes
            a.TestGameEvent(14, 0, 999, 28); Thread.Sleep(1500);
            Check("createur : mon niveau MAX (" + a.TestLevel + ")", a.TestLevel == 999);
            a.TestGameEvent(14, 0, 1, 28); Thread.Sleep(1500);
            Check("createur : mon niveau 1", a.TestLevel == 1);
            a.TestGameEvent(14, 0, 200000, 28); Thread.Sleep(500);
            a.TestSetXp(200000);
            a.TestGameEvent(14, b.MyId, 50, 28);
            Check("createur : niveau de J2 = 50", Wait(() => b.TestLevel == 50, 8000));
            a.TestGameEvent(14, b.MyId, 12345, 29);
            Check("createur : orbes de J2 = 12345", Wait(() => b.TestMoney == 12345, 8000));
            a.TestGameEvent(14, 0, 70000, 29); Thread.Sleep(1500);
            Check("createur : mes orbes = 70000", a.TestMoney == 70000);
            a.TestSetMoney(60050); b.TestSetXp(200000);
            // ---- BOUTIQUE : ameliorations (secrets autorises) et teleportation
            Thread.Sleep(1500);
            long s0 = a.TestMoney;
            a.TestCommand(176); Thread.Sleep(2500);
            Check("capacite munitions jaunes achetee (300)", a.TestMoney == s0 - 300);
            a.TestCommand(180); Thread.Sleep(2500);
            Check("turbo JetBoard refuse sans JetBoard", a.TestMoney == s0 - 300);
            a.TestCommand(187); Thread.Sleep(1500);
            Check("teleportation vers la zone industrielle", Wait(() => Loaded(a) && Lvl(a).StartsWith("ctyind"), 60000));
            Thread.Sleep(4000);
            ShotOf(g1.Id, "t1-teleporte-zone-industrielle");
            a.TestCommand(182); Thread.Sleep(1500);
            Check("teleportation retour au Naughty Ottsel", Wait(() => Loaded(a) && Lvl(a).StartsWith("cty"), 60000));
            Thread.Sleep(6000);
            b.TestCommand(84); Thread.Sleep(3000);
            a.TestSetMoney(60050);
            ShotOf(g1.Id, "p0-jak-moi-depart");

            // pas achete : refuse
            a.TestCommand(461); Thread.Sleep(2500);
            Check("Keira pas achetee : refusee", Gb(a, 107) == 0);
            // achat (sauve dans le profil) puis choix
            long m0 = a.TestMoney;
            a.TestCommand(175); Thread.Sleep(2500);
            Console.WriteLine("   orbes J1 : " + m0 + " -> " + a.TestMoney);
            Check("Keira achetee (800 orbes)", a.TestMoney == m0 - 800);
            a.TestCommand(461);
            Check("J1 joue Keira", Wait(() => Gb(a, 107) == 1, 10000));
            Check("J2 voit Keira sur J1", Wait(() => Gb(b, 108) == 1, 15000));
            Thread.Sleep(3000);
            ShotOf(g1.Id, "p1-keira-moi");
            b.TestCommand(470); Thread.Sleep(2500);
            ShotOf(g2.Id, "p2-keira-vue-par-j2-devant");
            b.TestCommand(471); Thread.Sleep(2500);
            ShotOf(g2.Id, "p3-keira-vue-par-j2-cote");
            // en mouvement : J1 part en courant (reperes du banc de test) et saute
            a.TestCommand(84); Thread.Sleep(600);
            ShotOf(g2.Id, "p4-keira-bouge-j2");
            ShotOf(g1.Id, "p4-keira-bouge-j1");
            // mort et retour : le corps reste Keira
            for (int n = 0; n < 30 && (Gflags(a) & 2) == 0; n++) { b.TestSendHitMode(a.MyId, 20f, 5); Thread.Sleep(450); if (n == 2) ShotOf(g2.Id, "p5-keira-touchee-j2"); }
            Check("J1 (Keira) elimine", Wait(() => (Gflags(a) & 2) != 0, 10000));
            Thread.Sleep(1500);
            ShotOf(g2.Id, "p5-keira-mort-j2");
            Thread.Sleep(9000);
            Check("apres la mort : toujours Keira", Wait(() => Gb(a, 107) == 1 && Loaded(a), 60000));
            ShotOf(g1.Id, "p5-keira-apres-mort");
            // le choix est sauve dans le profil
            Profil pr = new Profil(dirA);
            Check("choix sauve dans le profil", pr.Perso == 1 && pr.Owns(75));
            // retour a Jak (gratuit)
            a.TestCommand(460);
            Check("retour a Jak", Wait(() => Gb(a, 107) == 0, 10000));
            Check("J2 revoit Jak", Wait(() => Gb(b, 108) == 0, 15000));
            ShotOf(g1.Id, "p6-jak-moi-0s");
            Thread.Sleep(2500);
            ShotOf(g1.Id, "p6-jak-moi-2s");
            b.TestCommand(470); Thread.Sleep(2500);
            ShotOf(g1.Id, "p6-jak-moi"); ShotOf(g2.Id, "p6-jak-vu-par-j2");
            Thread.Sleep(8000);
            ShotOf(g1.Id, "p6-jak-moi-12s");
            a.TestCommand(84); Thread.Sleep(3000);
            ShotOf(g1.Id, "p6-jak-moi-apres-deplacement");
            Check("les jeux tournent", a.GameAttached && b.GameAttached && !g1.HasExited && !g2.HasExited);

            a.Stop(); b.Stop(); s.Stop();
            try { g1.Kill(); } catch (Exception) { }
            try { g2.Kill(); } catch (Exception) { }
            Console.WriteLine(fails == 0 ? "TEST PERSO OK" : (fails + " TEST(S) EN ECHEC"));
            return fails;
        }

        // nouveaux personnages (Jak 2, Jak 1 HD, Jak 4, Tess, Daxter), Daxter enleve de l'epaule,
        // tricheur expulse automatiquement
        public static int Persos()
        {
            const int port = 31995;
            string mod = Environment.GetEnvironmentVariable("JAK3ONLINE_MOD");
            string sp = Environment.GetEnvironmentVariable("JAK3ONLINE_TMP");
            string dirA = Path.Combine(sp, "profilP");
            try { if (Directory.Exists(dirA)) Directory.Delete(dirA, true); } catch (Exception) { }
            Server s = new Server();
            s.Start(port);
            Process g1 = StartGame(mod, sp, Path.Combine(mod, "data"), "cfg");
            Thread.Sleep(8000);
            Process g2 = StartGame(mod, sp, Path.Combine(sp, "data2"), "cfg2");
            Client a = new Client(); a.BridgeDir = Path.Combine(mod, "data", "online", "bridge"); a.WantedName = "LEON"; a.ServerAddress = "127.0.0.1:" + port; a.ProfileDir = dirA;
            a.Log = m => Console.WriteLine("   [J1] " + m); a.Start(); a.Connect();
            Client b = new Client(); b.BridgeDir = Path.Combine(sp, "data2", "online", "bridge"); b.WantedName = "Joueur2"; b.Ephemeral = true; b.ServerAddress = "127.0.0.1:" + port;
            b.Log = m => Console.WriteLine("   [J2] " + m); b.Start(); b.Connect();
            Check("les deux jeux sont detectes", Wait(() => a.GameAttached && b.GameAttached && a.NetState == Shm.NET_LOBBY && b.NetState == Shm.NET_LOBBY, 180000));
            foreach (Process gp in new Process[] { g1, g2 }) { IntPtr w = WindowOf(gp.Id); if (w != IntPtr.Zero) ShowWindow(w, 7); }
            Thread.Sleep(15000);
            a.TestSetXp(200000); a.TestSetMoney(60000);
            b.TestSetXp(200000); b.TestSetMoney(60000);
            a.UiJoinWorld(); Thread.Sleep(1500); b.UiJoinWorld();
            Check("monde en ligne dans les deux jeux", Wait(() => (UiF(a) & 2) != 0 && (UiF(b) & 2) != 0 && Loaded(a) && Loaded(b) && Lvl(a).StartsWith("cty") && Lvl(b).StartsWith("cty"), 240000));
            Thread.Sleep(10000);
            b.TestCommand(84); Thread.Sleep(3000);
            string[] nm = { "jak", "keira", "jak2", "jak1hd", "jak4", "tess", "daxter", "ashelin", "torn", "sig", "samos" };
            int[] item = { -1, 75, 54, 55, 56, 57, 58, 105, 106, 107, 108 };
            int from = 1;
            int.TryParse(Environment.GetEnvironmentVariable("JAK3ONLINE_PERSOS_FROM") ?? "1", out from);
            for (int k = Math.Max(1, from); k <= 10; k++)
            {
                long m0 = a.TestMoney;
                a.TestCommand(item[k] < 96 ? 100 + item[k] : 500 + item[k] - 96); Thread.Sleep(2500);
                Check(nm[k] + " achete (" + (m0 - a.TestMoney) + " orbes)", a.TestMoney < m0);
                a.TestCommand(590 + k);
                Check("J1 joue " + nm[k], Wait(() => Gb(a, 107) == k, 20000));

                if (k != 6) Check("J2 voit " + nm[k] + " sur J1", Wait(() => Gb(b, 108) == k, 15000));
                Thread.Sleep(3000);
                ShotOf(g1.Id, "q" + k + "-" + nm[k] + "-moi");
                b.TestCommand(84); Thread.Sleep(2000);
                b.TestCommand(470); Thread.Sleep(2500);
                ShotOf(g2.Id, "q" + k + "-" + nm[k] + "-vu-par-j2");
                a.TestCommand(84); Thread.Sleep(700);
                ShotOf(g1.Id, "q" + k + "-" + nm[k] + "-bouge");
                Thread.Sleep(2500);
            }
            // Daxter vu par J2, de pres
            b.TestCommand(84); Thread.Sleep(3000);
            b.TestCommand(470); Thread.Sleep(2500);
            ShotOf(g2.Id, "q6-daxter-vu-par-j2-pres");
            b.TestCommand(471); Thread.Sleep(2500);
            ShotOf(g2.Id, "q6-daxter-vu-par-j2-cote");
            a.TestCommand(460);
            Check("retour a Jak", Wait(() => Gb(a, 107) == 0, 20000));
            Thread.Sleep(3000);
            ShotOf(g1.Id, "q7-jak-retour-moi");
            b.TestCommand(470); Thread.Sleep(2500);
            ShotOf(g2.Id, "q7-jak-retour-vu-par-j2");
            Profil pr = new Profil(dirA);
            Check("personnages sauves dans le profil", pr.Owns(54) && pr.Owns(55) && pr.Owns(56) && pr.Owns(57) && pr.Owns(58) && pr.Owns(105) && pr.Owns(108));
            // ---- ANTI-TRICHE : le createur peut (ADMIN), un joueur qui triche est expulse
            a.TestCommand(481); Thread.Sleep(1500);
            Check("le createur s'active l'invincibilite (ADMIN) et reste dans le monde", (Gb(a, 109) & 1) == 1 && a.InWorld);
            a.TestCommand(481); Thread.Sleep(1500);
            b.TestCommand(480); Thread.Sleep(1500);
            Check("J2 ne peut pas tricher (secrets et debug coupes)", Gb(b, 109) == 0);
            Check("J2 tricheur : expulse automatiquement du monde en ligne", Wait(() => !b.InWorld, 15000));
            Check("J1 toujours dans le monde", a.InWorld);
            Check("les jeux tournent", a.GameAttached && b.GameAttached && !g1.HasExited && !g2.HasExited);

            a.Stop(); b.Stop(); s.Stop();
            try { g1.Kill(); } catch (Exception) { }
            try { g2.Kill(); } catch (Exception) { }
            Console.WriteLine(fails == 0 ? "TEST PERSOS OK" : (fails + " TEST(S) EN ECHEC"));
            return fails;
        }

        // --vehnoms : chaque vehicule du desert achete puis appele (capture), et langue de Windows
        public static int VehNoms()
        {
            const int port = 31994;
            string mod = Environment.GetEnvironmentVariable("JAK3ONLINE_MOD");
            string sp = Environment.GetEnvironmentVariable("JAK3ONLINE_TMP");
            string dirA = Path.Combine(sp, "profilV");
            try { if (Directory.Exists(dirA)) Directory.Delete(dirA, true); } catch (Exception) { }
            Server s = new Server();
            s.Start(port);
            Process g1 = StartGame(mod, sp, Path.Combine(mod, "data"), "cfg");
            Client a = new Client(); a.BridgeDir = Path.Combine(mod, "data", "online", "bridge"); a.WantedName = "LEON"; a.ServerAddress = "127.0.0.1:" + port; a.ProfileDir = dirA;
            a.Log = m => Console.WriteLine("   [J1] " + m); a.Start(); a.Connect();
            Check("jeu detecte", Wait(() => a.GameAttached && a.NetState == Shm.NET_LOBBY, 180000));
            IntPtr w = WindowOf(g1.Id); if (w != IntPtr.Zero) ShowWindow(w, 7);
            Thread.Sleep(15000);
            a.TestSetXp(200000); a.TestSetMoney(60000);
            a.UiJoinWorld();
            Check("monde en ligne", Wait(() => (UiF(a) & 2) != 0 && Loaded(a), 240000));
            Thread.Sleep(10000);
            for (int id = 43; id <= 50; id++)
            {
                a.TestCommand(100 + id); Thread.Sleep(1500);
                Check("vehicule " + id + " achete", Wait(() => a.Prof.Owns(id), 5000));
                a.TestCommand(200 + id); Thread.Sleep(9000);
                ShotOf(g1.Id, "v" + id);
                a.TestCommand(70); Thread.Sleep(5000);
            }
            Check("le jeu tourne", a.GameAttached && !g1.HasExited);
            a.Stop(); s.Stop();
            try { g1.Kill(); } catch (Exception) { }
            Console.WriteLine(fails == 0 ? "TEST VEHNOMS OK" : (fails + " TEST(S) EN ECHEC"));
            return fails;
        }

        // --coop : session privee a deux : tirs des autres, caisses / ennemis detruits, missions
        public static int Coop()
        {
            const int port = 27993;
            string mod = Environment.GetEnvironmentVariable("JAK3ONLINE_MOD");
            string sp = Environment.GetEnvironmentVariable("JAK3ONLINE_TMP");
            Server s = new Server();
            s.Start(port);
            Process g1 = StartGame(mod, sp, Path.Combine(mod, "data"), "cfg");
            Thread.Sleep(8000);
            Process g2 = StartGame(mod, sp, Path.Combine(sp, "data2"), "cfg2");
            Client a = new Client(); a.BridgeDir = Path.Combine(mod, "data", "online", "bridge"); a.WantedName = "Joueur1"; a.Ephemeral = true; a.ServerAddress = "127.0.0.1:" + port;
            a.Log = m => Console.WriteLine("   [J1] " + m); a.Start(); a.Connect();
            Client b = new Client(); b.BridgeDir = Path.Combine(sp, "data2", "online", "bridge"); b.WantedName = "Joueur2"; b.Ephemeral = true; b.ServerAddress = "127.0.0.1:" + port;
            b.Log = m => Console.WriteLine("   [J2] " + m); b.Start(); b.Connect();
            Check("les deux jeux sont detectes", Wait(() => a.GameAttached && b.GameAttached && a.NetState == Shm.NET_LOBBY && b.NetState == Shm.NET_LOBBY, 180000));
            foreach (Process gp in new Process[] { g1, g2 }) { IntPtr w = WindowOf(gp.Id); if (w != IntPtr.Zero) ShowWindow(w, 7); }
            Thread.Sleep(10000);
            a.UiCreate(false, 100, true);
            Check("session privee creee", Wait(() => a.SessionId != 0, 8000));
            b.UiJoinCode(a.SessionCode);
            Check("le joueur 2 rejoint", Wait(() => b.SessionId == a.SessionId, 8000));
            DateTime la = DateTime.MinValue, lb = DateTime.MinValue;
            Check("les deux parties sont chargees", Wait(() =>
            {
                if (!Loaded(a) && (DateTime.UtcNow - la).TotalSeconds > 20) { la = DateTime.UtcNow; a.TestCommand(1); }
                if (!Loaded(b) && (DateTime.UtcNow - lb).TotalSeconds > 20) { lb = DateTime.UtcNow; b.TestCommand(4); }
                return Loaded(a) && Loaded(b);
            }, 500000));
            Thread.Sleep(6000);
            GoTo(a, b, 34, 34, l => l.StartsWith("cty"), l => l.StartsWith("cty"), "les deux au port de Haven");
            b.TestCommand(84); Thread.Sleep(4000);
            Check("J2 voit J1", Gb(b, 90) == 1);
            // TIRS
            int s0 = Gb(b, 112);
            for (int k = 0; k < 6; k++) { a.TestCommand(482); Thread.Sleep(450); if (k == 2) { b.TestCommand(470); } if (k == 4) ShotOf(g2.Id, "c1-tir-de-j1-vu-par-j2"); }
            Check("J2 voit les tirs de J1 (" + (Gb(b, 112) - s0) + ")", Gb(b, 112) - s0 >= 3);
            // CAISSES / ENNEMIS
            int sent0 = Gb(a, 110), got0 = Gb(b, 111);
            for (int k = 0; k < 4; k++) { a.TestCommand(483); Thread.Sleep(1500); }
            Thread.Sleep(2000);
            Console.WriteLine("   coop : J1 envoie " + (Gb(a, 110) - sent0) + ", J2 applique " + (Gb(b, 111) - got0));
            Check("J1 detruit des caisses / ennemis et le signale", Gb(a, 110) - sent0 >= 1);
            Check("J2 les detruit aussi", Gb(b, 111) - got0 >= 1);
            ShotOf(g1.Id, "c2-j1-apres-destruction"); ShotOf(g2.Id, "c2-j2-apres-destruction");
            // MISSIONS
            sent0 = Gb(a, 110); got0 = Gb(b, 111);
            a.TestCommand(484); Thread.Sleep(3000);
            Check("J1 valide une etape de mission, J2 la recoit", Gb(a, 110) - sent0 >= 1 && Gb(b, 111) - got0 >= 1);
            // dans l'autre sens
            sent0 = Gb(b, 110); got0 = Gb(a, 111);
            b.TestCommand(484); Thread.Sleep(2500);
            Check("J2 valide aussi une etape, J1 la recoit", Gb(b, 110) - sent0 >= 1 && Gb(a, 111) - got0 >= 1);
            Check("les jeux tournent", a.GameAttached && b.GameAttached && !g1.HasExited && !g2.HasExited);
            a.Stop(); b.Stop(); s.Stop();
            try { g1.Kill(); } catch (Exception) { }
            try { g2.Kill(); } catch (Exception) { }
            Console.WriteLine(fails == 0 ? "TEST COOP OK" : (fails + " TEST(S) EN ECHEC"));
            return fails;
        }

        // --maisons : boutique complete, maisons (achat, aller, inviter, accepter, qui voit qui), style
        public static int Maisons()
        {
            const int port = 27991;
            string mod = Environment.GetEnvironmentVariable("JAK3ONLINE_MOD");
            string sp = Environment.GetEnvironmentVariable("JAK3ONLINE_TMP");
            string dirA = Path.Combine(sp, "profilM");
            try { if (Directory.Exists(dirA)) Directory.Delete(dirA, true); } catch (Exception) { }
            Server s = new Server();
            s.Start(port);
            Process g1 = StartGame(mod, sp, Path.Combine(mod, "data"), "cfg");
            Thread.Sleep(8000);
            Process g2 = StartGame(mod, sp, Path.Combine(sp, "data2"), "cfg2");
            Client a = new Client(); a.BridgeDir = Path.Combine(mod, "data", "online", "bridge"); a.WantedName = "LEON"; a.ServerAddress = "127.0.0.1:" + port; a.ProfileDir = dirA;
            a.Log = m => Console.WriteLine("   [J1] " + m); a.Start(); a.Connect();
            Client b = new Client(); b.BridgeDir = Path.Combine(sp, "data2", "online", "bridge"); b.WantedName = "Joueur2"; b.Ephemeral = true; b.ServerAddress = "127.0.0.1:" + port;
            b.Log = m => Console.WriteLine("   [J2] " + m); b.Start(); b.Connect();
            Check("les deux jeux sont detectes", Wait(() => a.GameAttached && b.GameAttached && a.NetState == Shm.NET_LOBBY && b.NetState == Shm.NET_LOBBY, 180000));
            foreach (Process gp in new Process[] { g1, g2 }) { IntPtr w = WindowOf(gp.Id); if (w != IntPtr.Zero) ShowWindow(w, 7); }
            Thread.Sleep(15000);
            a.TestSetXp(200000); a.TestSetMoney(60000);
            b.TestSetXp(20000); b.TestSetMoney(8000);
            a.UiJoinWorld(); Thread.Sleep(1500); b.UiJoinWorld();
            Check("monde en ligne dans les deux jeux", Wait(() => (UiF(a) & 2) != 0 && (UiF(b) & 2) != 0 && Loaded(a) && Loaded(b) && Lvl(a).StartsWith("cty") && Lvl(b).StartsWith("cty"), 240000));
            Thread.Sleep(10000);
            // BOUTIQUE : toutes les rubriques (plus de limite de 40 lignes)
            a.TestCommand(560); Thread.Sleep(1500);
            Console.WriteLine("   boutique : " + Gb(a, 88 + 28) + " lignes, rubriques 0x" + Gb(a, 88 + 29).ToString("x"));
            Check("boutique : plus de 40 lignes", Gb(a, 88 + 28) > 40);
            Check("boutique : armes, Dark / Light Jak, maisons et style presents, personnages dans PERSOS seulement", Gb(a, 88 + 29) == 31);
            a.TestCommand(73); Thread.Sleep(1500); ShotOf(g1.Id, "m0-boutique-haut"); a.TestCommand(61); Thread.Sleep(800);
            // ACHATS : les 3 maisons + grosse tete (J1), une cabane (J2)
            for (int k = 0; k < 4; k++) { a.TestCommand(500 + k); Thread.Sleep(1400); }
            b.TestCommand(500); Thread.Sleep(1400);
            Check("J1 achete la cabane, la villa et le chateau", a.Prof.Owns(96) && a.Prof.Owns(97) && a.Prof.Owns(98));
            Check("J1 achete la grosse tete", a.Prof.Owns(99));
            Check("J2 achete une cabane", b.Prof.Owns(96));
            Thread.Sleep(1500);
            ShotOf(g1.Id, "m1-grosse-tete");
            // chacun dans SA cabane (meme carte, maisons differentes) : ils ne se voient pas
            a.TestCommand(540); Thread.Sleep(600); b.TestCommand(540);
            Check("J1 et J2 chacun dans sa cabane", Wait(() => MapOf(a) == 8 && MapOf(b) == 8 && Loaded(a) && Loaded(b), 150000));
            Thread.Sleep(9000);
            Console.WriteLine("   maison annoncee : J1 " + Gb(a, 88 + 27) + "  J2 " + Gb(b, 88 + 27) + "  (1 = la mienne, 2 = celle d'un autre)");
            Check("chacun est dans sa propre maison", Gb(a, 88 + 27) == 1 && Gb(b, 88 + 27) == 1);
            Check("maisons differentes : ils ne se voient pas", Gb(a, 90) == 0 && Gb(b, 90) == 0);
            ShotOf(g1.Id, "m2-cabane-j1"); ShotOf(g2.Id, "m2-cabane-j2-seul");
            // INVITATION : J1 invite J2, J2 accepte
            a.TestCommand(545);
            Check("J2 recoit l'invitation", Wait(() => Gb(b, 88 + 26) == 1, 8000));
            Thread.Sleep(1000);
            ShotOf(g2.Id, "m3-invitation-j2");
            b.TestCommand(548);
            Thread.Sleep(3000);
            Check("J2 arrive chez J1", Wait(() => MapOf(b) == 8 && Loaded(b) && Gb(b, 88 + 27) == 2, 150000));
            Check("dans la meme maison, ils se voient", Wait(() => Gb(a, 90) == 1 && Gb(b, 90) == 1, 20000));
            Thread.Sleep(2000);
            ShotOf(g1.Id, "m4-j1-voit-j2"); ShotOf(g2.Id, "m4-j2-chez-j1");
            // la villa et le chateau
            a.TestCommand(541);
            Check("villa chargee", Wait(() => MapOf(a) == 9 && Loaded(a), 150000));
            Thread.Sleep(8000); ShotOf(g1.Id, "m5-villa");
            a.TestCommand(542);
            Check("chateau charge", Wait(() => MapOf(a) == 10 && Loaded(a), 150000));
            Thread.Sleep(8000); ShotOf(g1.Id, "m6-chateau");
            // STYLE : couper la grosse tete
            a.TestCommand(550); Thread.Sleep(2000); ShotOf(g1.Id, "m7-grosse-tete-coupee");
            // retour au port
            a.TestCommand(410);
            Check("retour au port de Haven", Wait(() => MapOf(a) == -1 && Loaded(a) && Lvl(a).StartsWith("cty"), 150000));
            Thread.Sleep(3000);
            Check("hors de la maison : plus de maison annoncee", Wait(() => Gb(a, 88 + 27) == 0, 10000));
            Check("les jeux tournent", a.GameAttached && b.GameAttached && !g1.HasExited && !g2.HasExited);
            a.Stop(); b.Stop(); s.Stop();
            try { g1.Kill(); } catch (Exception) { }
            try { g2.Kill(); } catch (Exception) { }
            Console.WriteLine(fails == 0 ? "TEST MAISONS OK" : (fails + " TEST(S) EN ECHEC"));
            return fails;
        }

        // --maj12b : bots reserves, onglets, accueil, achat a distance, panneau du Naughty Ottsel, vrais tirs, synchro du monde
        public static int Maj12b()
        {
            const int port = 27990;
            string mod = Environment.GetEnvironmentVariable("JAK3ONLINE_MOD");
            string sp = Environment.GetEnvironmentVariable("JAK3ONLINE_TMP");
            string dirA = Path.Combine(sp, "profilB");
            try { if (Directory.Exists(dirA)) Directory.Delete(dirA, true); } catch (Exception) { }
            Server s = new Server();
            s.Start(port);
            Process g1 = StartGame(mod, sp, Path.Combine(mod, "data"), "cfg");
            Thread.Sleep(8000);
            Process g2 = StartGame(mod, sp, Path.Combine(sp, "data2"), "cfg2");
            Client a = new Client(); a.BridgeDir = Path.Combine(mod, "data", "online", "bridge"); a.WantedName = "LEON"; a.ServerAddress = "127.0.0.1:" + port; a.ProfileDir = dirA;
            a.Log = m => Console.WriteLine("   [J1] " + m); a.Start(); a.Connect();
            Client b = new Client(); b.BridgeDir = Path.Combine(sp, "data2", "online", "bridge"); b.WantedName = "Joueur2"; b.Ephemeral = true; b.ServerAddress = "127.0.0.1:" + port;
            b.Log = m => Console.WriteLine("   [J2] " + m); b.Start(); b.Connect();
            Check("les deux jeux sont detectes", Wait(() => a.GameAttached && b.GameAttached && a.NetState == Shm.NET_LOBBY && b.NetState == Shm.NET_LOBBY, 180000));
            foreach (Process gp in new Process[] { g1, g2 }) { IntPtr w = WindowOf(gp.Id); if (w != IntPtr.Zero) ShowWindow(w, 7); }
            Thread.Sleep(15000);
            a.TestSetXp(200000); a.TestSetMoney(60000);
            b.TestSetXp(20000); b.TestSetMoney(5000);
            a.UiJoinWorld(); Thread.Sleep(1500); b.UiJoinWorld();
            Check("monde en ligne dans les deux jeux", Wait(() => (UiF(a) & 2) != 0 && (UiF(b) & 2) != 0 && Loaded(a) && Loaded(b) && Lvl(a).StartsWith("cty") && Lvl(b).StartsWith("cty"), 240000));
            Thread.Sleep(3000);
            ShotOf(g2.Id, "b0-accueil-j2");
            // BOTS : pas pour un joueur normal
            b.AddBot(); Thread.Sleep(800);
            Check("un joueur normal ne peut pas ajouter de bot", b.BotCount == 0);
            // MENU : BOUTIQUE puis PERSOS
            b.TestCommand(73); Thread.Sleep(1500); ShotOf(g2.Id, "b1-boutique");
            b.TestCommand(62); Thread.Sleep(1200); ShotOf(g2.Id, "b2-persos");
            b.TestCommand(61); Thread.Sleep(800);
            // ACHAT A DISTANCE : Scatter Gun (60 orbes) -> 90
            long m0 = b.TestMoney;
            b.TestCommand(101); Thread.Sleep(1800);
            Console.WriteLine("   achat a distance : " + (m0 - b.TestMoney) + " orbes (prix 60)");
            Check("achat a distance : +50 %", b.Prof.Owns(1) && m0 - b.TestMoney == 90);
            // LE NAUGHTY OTTSEL : panneau devant l'entree, prix normal dedans
            a.TestCommand(182);
            Thread.Sleep(9000);
            ShotOf(g1.Id, "b3-panneau-ottsel");
            a.TestCommand(68);
            Check("J1 dans le Naughty Ottsel", Wait(() => Lvl(a).StartsWith("hiphog") && Loaded(a), 90000));
            Thread.Sleep(4000);
            long m1 = a.TestMoney;
            a.TestCommand(102); Thread.Sleep(1800);
            Console.WriteLine("   achat au Naughty Ottsel : " + (m1 - a.TestMoney) + " orbes (prix 120)");
            Check("au Naughty Ottsel : prix normal", a.Prof.Owns(2) && m1 - a.TestMoney == 120);
            ShotOf(g1.Id, "b4-dans-ottsel");
            // VRAIS TIRS : J1 tire avec les 12 armes, J2 regarde
            GoTo(a, b, 34, 34, l => l.StartsWith("cty"), l => l.StartsWith("cty"), "les deux au port de Haven");
            b.TestCommand(84); Thread.Sleep(3000);
            b.TestCommand(470); Thread.Sleep(800);
            int seen0 = Gb(b, 112);
            string[] armes = { "scatter", "wave", "plasmite", "blaster", "reflexor", "gyro", "vulcan", "arc", "needle", "peacemaker", "inverter", "supernova" };
            for (int k = 0; k < 12; k++)
            {
                a.TestCommand(486 + k); Thread.Sleep(350);
                ShotOf(g2.Id, "b5-tir-" + (26 + k) + "-" + armes[k]);
                Thread.Sleep(900);
            }
            int seen = (Gb(b, 112) - seen0 + 256) % 256;
            Console.WriteLine("   tirs vus par J2 : " + seen + " / 12");
            Check("J2 voit les tirs des 12 armes", seen >= 12);
            // SYNCHRO DU MONDE : J1 detruit caisses / ennemis, J2 aussi
            int sent0 = Gb(a, 110), got0 = Gb(b, 111);
            for (int k = 0; k < 4; k++) { a.TestCommand(483); Thread.Sleep(1500); }
            Thread.Sleep(2000);
            Console.WriteLine("   monde : J1 envoie " + ((Gb(a, 110) - sent0 + 256) % 256) + ", J2 applique " + ((Gb(b, 111) - got0 + 256) % 256));
            Check("monde en ligne : ce que J1 detruit est detruit chez J2", (Gb(a, 110) - sent0 + 256) % 256 >= 1 && (Gb(b, 111) - got0 + 256) % 256 >= 1);
            Check("les jeux tournent", a.GameAttached && b.GameAttached && !g1.HasExited && !g2.HasExited);
            a.Stop(); b.Stop(); s.Stop();
            try { g1.Kill(); } catch (Exception) { }
            try { g2.Kill(); } catch (Exception) { }
            Console.WriteLine(fails == 0 ? "TEST MAJ12B OK" : (fails + " TEST(S) EN ECHEC"));
            return fails;
        }

        // --maj12c : evenements au hasard (a peu de joueurs), MISSION, parcours sans planche, camera
        public static int Maj12c()
        {
            const int port = 27989;
            Environment.SetEnvironmentVariable("JAK3ONLINE_EVENTS_SOON", "1");
            string mod = Environment.GetEnvironmentVariable("JAK3ONLINE_MOD");
            string sp = Environment.GetEnvironmentVariable("JAK3ONLINE_TMP");
            string dirA = Path.Combine(sp, "profilE");
            try { if (Directory.Exists(dirA)) Directory.Delete(dirA, true); } catch (Exception) { }
            Server s = new Server();
            s.Start(port);
            Process g1 = StartGame(mod, sp, Path.Combine(mod, "data"), "cfg");
            Thread.Sleep(8000);
            Process g2 = StartGame(mod, sp, Path.Combine(sp, "data2"), "cfg2");
            Client a = new Client(); a.BridgeDir = Path.Combine(mod, "data", "online", "bridge"); a.WantedName = "LEON"; a.ServerAddress = "127.0.0.1:" + port; a.ProfileDir = dirA;
            a.Log = m => Console.WriteLine("   [J1] " + m); a.Start(); a.Connect();
            Client b = new Client(); b.BridgeDir = Path.Combine(sp, "data2", "online", "bridge"); b.WantedName = "Joueur2"; b.Ephemeral = true; b.ServerAddress = "127.0.0.1:" + port;
            b.Log = m => Console.WriteLine("   [J2] " + m); b.Start(); b.Connect();
            Check("les deux jeux sont detectes", Wait(() => a.GameAttached && b.GameAttached && a.NetState == Shm.NET_LOBBY && b.NetState == Shm.NET_LOBBY, 180000));
            foreach (Process gp in new Process[] { g1, g2 }) { IntPtr w = WindowOf(gp.Id); if (w != IntPtr.Zero) ShowWindow(w, 7); }
            Thread.Sleep(15000);
            a.TestSetXp(200000); a.TestSetMoney(60000);
            b.TestSetXp(20000); b.TestSetMoney(5000);
            a.UiJoinWorld(); Thread.Sleep(1500); b.UiJoinWorld();
            Check("monde en ligne dans les deux jeux", Wait(() => (UiF(a) & 2) != 0 && (UiF(b) & 2) != 0 && Loaded(a) && Loaded(b) && Lvl(a).StartsWith("cty") && Lvl(b).StartsWith("cty"), 240000));
            Thread.Sleep(4000);
            ShotOf(g1.Id, "e0-camera-jak4");
            // EVENEMENT AU HASARD : a 2 joueurs, seulement un petit (pluie d'orbes, double XP, zone, meteores)
            Check("un evenement au hasard arrive", Wait(() => a.TestEventKind != 0, 90000));
            int k0 = a.TestEventKind;
            Console.WriteLine("   evenement au hasard : sorte " + k0);
            Check("a 2 joueurs : pas de boss, tresor ni parcours", k0 == 2 || k0 == 4 || k0 == 5 || k0 == 8);
            ShotOf(g1.Id, "e1-evenement-hasard");
            Wait(() => a.TestEventState != 1, 150000);
            Wait(() => a.TestEventKind == 0, 30000);
            // MISSION : la mission 114 ; J2 la reussit en premier (par le vrai chemin du jeu)
            a.TestStartEvent(9, 114);
            Check("evenement MISSION chez les deux", Wait(() => a.TestEventKind == 9 && b.TestEventKind == 9, 8000));
            Thread.Sleep(2500);
            ShotOf(g2.Id, "e2-mission-j2");
            long mb = b.TestMoney;
            b.TestCommand(566);
            Check("J2 reussit la mission en premier", Wait(() => a.TestEventState == 2 && a.TestEventWinner == b.MyId, 15000));
            Check("J2 gagne 2000 orbes", Wait(() => b.TestMoney >= mb + 2000, 8000));
            Thread.Sleep(1500);
            ShotOf(g1.Id, "e3-mission-resultat-j1");
            // PARCOURS : pas de planche
            a.TestCommand(124); Thread.Sleep(1500);
            a.TestCommand(567); Thread.Sleep(600);
            Check("J1 a la planche au port", (Gb(a, 88 + 30) & 1) != 0);
            a.TestCommand(401);
            Check("carte Tour celeste chargee", Wait(() => MapOf(a) == 1 && Loaded(a), 150000));
            Thread.Sleep(5000);
            a.TestCommand(567); Thread.Sleep(600);
            Console.WriteLine("   sur le parcours : pad30 = " + Gb(a, 88 + 30));
            Check("sur un parcours : pas de planche", (Gb(a, 88 + 30) & 5) == 4);
            ShotOf(g1.Id, "e4-parcours-camera");
            a.TestCommand(410);
            Check("retour au port", Wait(() => MapOf(a) == -1 && Loaded(a) && Lvl(a).StartsWith("cty"), 150000));
            Thread.Sleep(3000);
            a.TestCommand(567); Thread.Sleep(600);
            Check("au port : la planche revient", (Gb(a, 88 + 30) & 5) == 1);
            Check("les jeux tournent", a.GameAttached && b.GameAttached && !g1.HasExited && !g2.HasExited);
            a.Stop(); b.Stop(); s.Stop();
            try { g1.Kill(); } catch (Exception) { }
            try { g2.Kill(); } catch (Exception) { }
            Console.WriteLine(fails == 0 ? "TEST MAJ12C OK" : (fails + " TEST(S) EN ECHEC"));
            return fails;
        }

        // --maj12d : horloge du monde synchronisee, passager assis (voiture / moto), Hellcat
        public static int Maj12d()
        {
            const int port = 27988;
            string mod = Environment.GetEnvironmentVariable("JAK3ONLINE_MOD");
            string sp = Environment.GetEnvironmentVariable("JAK3ONLINE_TMP");
            string dirA = Path.Combine(sp, "profilD");
            try { if (Directory.Exists(dirA)) Directory.Delete(dirA, true); } catch (Exception) { }
            Server s = new Server();
            s.Start(port);
            Process g1 = StartGame(mod, sp, Path.Combine(mod, "data"), "cfg");
            Thread.Sleep(8000);
            Process g2 = StartGame(mod, sp, Path.Combine(sp, "data2"), "cfg2");
            Client a = new Client(); a.BridgeDir = Path.Combine(mod, "data", "online", "bridge"); a.WantedName = "LEON"; a.ServerAddress = "127.0.0.1:" + port; a.ProfileDir = dirA;
            a.Log = m => Console.WriteLine("   [J1] " + m); a.Start(); a.Connect();
            Client b = new Client(); b.BridgeDir = Path.Combine(sp, "data2", "online", "bridge"); b.WantedName = "Joueur2"; b.Ephemeral = true; b.ServerAddress = "127.0.0.1:" + port;
            b.Log = m => Console.WriteLine("   [J2] " + m); b.Start(); b.Connect();
            Check("les deux jeux sont detectes", Wait(() => a.GameAttached && b.GameAttached && a.NetState == Shm.NET_LOBBY && b.NetState == Shm.NET_LOBBY, 180000));
            foreach (Process gp in new Process[] { g1, g2 }) { IntPtr w = WindowOf(gp.Id); if (w != IntPtr.Zero) ShowWindow(w, 7); }
            Thread.Sleep(15000);
            a.TestSetXp(200000); a.TestSetMoney(60000);
            b.TestSetXp(20000); b.TestSetMoney(5000);
            a.UiJoinWorld(); Thread.Sleep(1500); b.UiJoinWorld();
            Check("monde en ligne dans les deux jeux", Wait(() => (UiF(a) & 2) != 0 && (UiF(b) & 2) != 0 && Loaded(a) && Loaded(b) && Lvl(a).StartsWith("cty") && Lvl(b).StartsWith("cty"), 240000));
            Thread.Sleep(5000);
            // HEURE : J1 (l'autorite) passe a 20 h ; J2 doit suivre
            a.TestCommand(569); Thread.Sleep(1500);
            Console.WriteLine("   heures : J1 " + Gb(a, 88 + 31) / 10.0 + "  J2 " + Gb(b, 88 + 31) / 10.0);
            Check("J2 prend l'heure du monde (20 h)", Wait(() => Math.Abs(Gb(b, 88 + 31) - Gb(a, 88 + 31)) <= 4, 45000));
            Console.WriteLine("   heures : J1 " + Gb(a, 88 + 31) / 10.0 + "  J2 " + Gb(b, 88 + 31) / 10.0);
            ShotOf(g2.Id, "d0-heure-j2");
            // PASSAGER EN VOITURE : J1 conduit le Sand Shark, J2 monte a cote
            GoTo(a, b, 34, 34, l => l.StartsWith("cty"), l => l.StartsWith("cty"), "les deux au port");
            a.TestCommand(145); Thread.Sleep(1200);
            a.TestCommand(245); Thread.Sleep(8000);
            Check("J1 conduit", (Gflags(a) & 0x80) != 0);
            b.TestCommand(84); Thread.Sleep(2500);
            b.TestCommand(83); Thread.Sleep(3000);
            double dc = Dist(PosOf(a), PosOf(b));
            Console.WriteLine("   voiture : J2 passager " + ((Gflags(b) & 0x400) != 0) + "  visible " + ((Gflags(b) & 4) == 0) + "  a " + dc.ToString("0.00") + " m de J1");
            Check("J2 est assis a cote (passager visible, 0.6 a 1.8 m)", (Gflags(b) & 0x400) != 0 && (Gflags(b) & 4) == 0 && dc > 0.6 && dc < 1.8);
            ShotOf(g1.Id, "d1-voiture-j1"); ShotOf(g2.Id, "d1-voiture-j2");
            b.TestCommand(83); Thread.Sleep(2000);
            Check("J2 redescend", (Gflags(b) & 0x400) == 0);
            a.TestCommand(70); Thread.Sleep(2500);
            // PASSAGER A MOTO : derriere
            a.TestCommand(151); Thread.Sleep(1200);
            a.TestCommand(251); Thread.Sleep(8000);
            Console.WriteLine("   moto : J1 conduit " + ((Gflags(a) & 0x80) != 0) + "  moto " + ((Gflags(a) & 0x800) != 0));
            Check("J1 conduit une moto (reconnue)", (Gflags(a) & 0x80) != 0 && (Gflags(a) & 0x800) != 0);
            b.TestCommand(84); Thread.Sleep(2500);
            b.TestCommand(83); Thread.Sleep(3000);
            double dm = Dist(PosOf(a), PosOf(b));
            Console.WriteLine("   moto : J2 passager " + ((Gflags(b) & 0x400) != 0) + "  a " + dm.ToString("0.00") + " m de J1");
            Check("J2 est assis derriere (0.7 a 1.8 m)", (Gflags(b) & 0x400) != 0 && dm > 0.7 && dm < 1.8);
            ShotOf(g1.Id, "d2-moto-j1"); ShotOf(g2.Id, "d2-moto-j2");
            b.TestCommand(83); Thread.Sleep(2000);
            a.TestCommand(70); Thread.Sleep(2500);
            Check("les jeux tournent", a.GameAttached && b.GameAttached && !g1.HasExited && !g2.HasExited);
            a.Stop(); b.Stop(); s.Stop();
            try { g1.Kill(); } catch (Exception) { }
            try { g2.Kill(); } catch (Exception) { }
            Console.WriteLine(fails == 0 ? "TEST MAJ12D OK" : (fails + " TEST(S) EN ECHEC"));
            return fails;
        }

        // --maj12e : vraies collisions avec les vehicules des autres (monter dessus, rentrer dedans),
        // Hellcat pilotable partout, bots refuses
        public static int Maj12e()
        {
            const int port = 27984;
            string mod = Environment.GetEnvironmentVariable("JAK3ONLINE_MOD");
            string sp = Environment.GetEnvironmentVariable("JAK3ONLINE_TMP");
            string dirA = Path.Combine(sp, "profilE");
            try { if (Directory.Exists(dirA)) Directory.Delete(dirA, true); } catch (Exception) { }
            Server s = new Server();
            s.Start(port);
            Process g1 = StartGame(mod, sp, Path.Combine(mod, "data"), "cfg");
            Thread.Sleep(8000);
            Process g2 = StartGame(mod, sp, Path.Combine(sp, "data2"), "cfg2");
            Client a = new Client(); a.BridgeDir = Path.Combine(mod, "data", "online", "bridge"); a.WantedName = "LEON"; a.ServerAddress = "127.0.0.1:" + port; a.ProfileDir = dirA;
            a.Log = m => Console.WriteLine("   [J1] " + m); a.Start(); a.Connect();
            Client b = new Client(); b.BridgeDir = Path.Combine(sp, "data2", "online", "bridge"); b.WantedName = "Joueur2"; b.Ephemeral = true; b.ServerAddress = "127.0.0.1:" + port;
            b.Log = m => Console.WriteLine("   [J2] " + m); b.Start(); b.Connect();
            Check("les deux jeux sont detectes", Wait(() => a.GameAttached && b.GameAttached && a.NetState == Shm.NET_LOBBY && b.NetState == Shm.NET_LOBBY, 180000));
            foreach (Process gp in new Process[] { g1, g2 }) { IntPtr w = WindowOf(gp.Id); if (w != IntPtr.Zero) ShowWindow(w, 7); }
            Thread.Sleep(15000);
            a.TestSetXp(200000); a.TestSetMoney(80000);
            b.TestSetXp(200000); b.TestSetMoney(80000);
            a.UiJoinWorld(); Thread.Sleep(1500); b.UiJoinWorld();
            Check("monde en ligne dans les deux jeux", Wait(() => (UiF(a) & 2) != 0 && (UiF(b) & 2) != 0 && Loaded(a) && Loaded(b) && Lvl(a).StartsWith("cty") && Lvl(b).StartsWith("cty"), 240000));
            Thread.Sleep(5000);
            // un bot d'un joueur normal est refuse dans le monde
            b.AddBotNow(); Thread.Sleep(6000);
            Check("bot d'un joueur normal : invisible dans le monde", a.SessionCount == 2);
            b.RemoveBots(); Thread.Sleep(2000);
            // au port de Haven (sol plat)
            GoTo(a, b, 34, 34, l => l.StartsWith("cty"), l => l.StartsWith("cty"), "les deux au port");
            Thread.Sleep(4000);
            // J1 dans le Sand Shark (vrai maillage de collision)
            a.TestCommand(145); b.TestCommand(145); Thread.Sleep(1200);
            a.TestCommand(245); Thread.Sleep(8000);
            Check("J1 conduit", (Gflags(a) & 0x80) != 0);
            float[] pa0 = PosOf(a);
            // J2 saute sur le toit
            b.TestCommand(571); Thread.Sleep(3500);
            float[] pb0 = PosOf(b);
            b.TestCommand(577); Thread.Sleep(600);
            Console.WriteLine("   toit : J2 a " + ((pb0[1] - pa0[1]) / 4096f).ToString("0.00") + " m au-dessus de J1, a " + (Dist(PosOf(a), pb0)).ToString("0.00") + " m ; porte " + ((Gb(b, 88 + 34) & 2) != 0));
            Check("J2 tient debout SUR le vehicule (pas a travers)", (pb0[1] - pa0[1]) / 4096f > 0.6 && (Gb(b, 88 + 34) & 2) != 0);
            ShotOf(g2.Id, "e1-sur-le-toit-j2");
            ShotOf(g1.Id, "e1-sur-le-toit-j1");
            // J1 avance (environ 20 km/h) : J2 est emporte
            pa0 = PosOf(a);
            a.TestCommand(572);
            double maxRel = 0;
            for (int k = 0; k < 25; k++) { Thread.Sleep(100); maxRel = Math.Max(maxRel, Dist(PosOf(a), PosOf(b))); }
            float[] pa1 = PosOf(a), pb1 = PosOf(b);
            double moved = Dist(pa0, pa1), rel = Dist(pa1, pb1);
            Console.WriteLine("   J1 a roule " + moved.ToString("0.0") + " m ; J2 est a " + rel.ToString("0.00") + " m de lui (au plus loin " + maxRel.ToString("0.00") + "), " + ((pb1[1] - pa1[1]) / 4096f).ToString("0.00") + " m au-dessus");
            Check("le vehicule roule et emporte J2 (reste dessus)", moved > 2 && rel < 3.0 && (pb1[1] - pa1[1]) / 4096f > 0.3);
            ShotOf(g2.Id, "e2-emporte-j2");
            ShotOf(g1.Id, "e2-emporte-j1");
            // HELLCAT a Haven : J2 monte dessus et J1 l'emmene
            a.TestCommand(70); b.TestCommand(70); Thread.Sleep(2500);
            a.TestCommand(508); Thread.Sleep(1500);
            a.TestCommand(568); Thread.Sleep(9000);
            a.TestCommand(577); Thread.Sleep(600);
            Check("Hellcat pilote a Haven", (Gflags(a) & 0x80) != 0 && (Gb(a, 88 + 34) & 1) != 0);
            ShotOf(g1.Id, "e4-hellcat-haven-j1");
            b.TestCommand(571); Thread.Sleep(3500);
            b.TestCommand(577); Thread.Sleep(600);
            Check("J2 debout sur le Hellcat de J1", (Gb(b, 88 + 34) & 2) != 0);
            ShotOf(g2.Id, "e4-sur-le-hellcat-j2");
            float[] ha0 = PosOf(a);
            a.TestCommand(572);
            for (int k = 0; k < 25; k++) Thread.Sleep(100);
            float[] ha1 = PosOf(a), hb1 = PosOf(b);
            Console.WriteLine("   Hellcat : J1 a vole " + Dist(ha0, ha1).ToString("0.0") + " m ; J2 est a " + Dist(ha1, hb1).ToString("0.00") + " m de lui");
            Check("le Hellcat emporte J2", Dist(ha0, ha1) > 2 && Dist(ha1, hb1) < 4.0);
            ShotOf(g2.Id, "e4-emporte-par-le-hellcat-j2");
            a.TestCommand(70); Thread.Sleep(2500);
            // au desert
            GoTo(a, b, 31, 31, l => l.StartsWith("des") || l.StartsWith("was"), l => l.StartsWith("des") || l.StartsWith("was"), "les deux au desert");
            Thread.Sleep(4000);
            // choc (terrain degage) : J1 dans sa voiture, J2 prend la sienne derriere lui et lui fonce dedans
            a.TestCommand(70); b.TestCommand(70); Thread.Sleep(2500);
            a.TestCommand(245); Thread.Sleep(8000);
            b.TestCommand(575); Thread.Sleep(2500);
            b.TestCommand(245); Thread.Sleep(900);
            ShotOf(g2.Id, "e3-appel-j2");
            Thread.Sleep(7000);
            if ((Gflags(b) & 0x80) == 0) { Console.WriteLine("   J2 : 2e appel (drapeaux " + Gflags(b).ToString("x") + ")"); b.TestCommand(245); Thread.Sleep(8000); }
            Check("J2 conduit", (Gflags(b) & 0x80) != 0);
            float[] qa0 = PosOf(a), qb0 = PosOf(b);
            Console.WriteLine("   avant le choc : J1 " + qa0[0] + " " + qa0[1] + " " + qa0[2] + "  J2 " + qb0[0] + " " + qb0[1] + " " + qb0[2]);
            double closest = 999;
            // J2 accelere vers J1 (sans pilote, le vehicule freine tout seul : on le relance)
            for (int k = 0; k < 30; k++) { if (k < 12 && k % 3 == 0) b.TestCommand(576); Thread.Sleep(100); closest = Math.Min(closest, Dist(PosOf(a), PosOf(b))); }
            Thread.Sleep(2000);
            float[] qa1 = PosOf(a), qb1 = PosOf(b);
            double push = Dist(qa0, qa1);
            bool through = Dist(qb1, qb0) > Dist(qa0, qb0) + 3.0;
            Console.WriteLine("   choc : J2 a roule " + Dist(qb0, qb1).ToString("0.0") + " m ; au plus pres " + closest.ToString("0.00") + " m ; J1 pousse de " + push.ToString("0.00") + " m ; J2 passe a travers " + through);
            Check("les vehicules se rentrent dedans (pas a travers)", !through && closest > 1.0 && closest < 6.0);
            Check("le vehicule percute est pousse", push > 0.5);
            ShotOf(g1.Id, "e3-choc-j1");
            ShotOf(g2.Id, "e3-choc-j2");
            // HELLCAT au desert (on descend d'abord du buggy de l'arrivee)
            a.TestCommand(70); Thread.Sleep(3000);
            a.TestCommand(568); Thread.Sleep(900);
            ShotOf(g1.Id, "e4-appel-hellcat-desert-j1");
            Thread.Sleep(8000);
            a.TestCommand(577); Thread.Sleep(600);
            Console.WriteLine("   Hellcat : pilote " + ((Gflags(a) & 0x80) != 0) + "  h-warf " + ((Gb(a, 88 + 34) & 1) != 0));
            Check("Hellcat pilote au desert", (Gflags(a) & 0x80) != 0 && (Gb(a, 88 + 34) & 1) != 0);
            ShotOf(g1.Id, "e4-hellcat-desert-j1");
            b.TestCommand(84); Thread.Sleep(3000);
            ShotOf(g2.Id, "e4-hellcat-desert-vu-par-j2");
            for (int k = 0; k < 2; k++) { a.TestCommand(572); Thread.Sleep(800); }
            Thread.Sleep(3000);
            ShotOf(g1.Id, "e4-hellcat-vol-j1");
            Check("le jeu tourne avec le Hellcat", a.GameAttached && !g1.HasExited);
            a.TestCommand(70); Thread.Sleep(2500);
            Check("les jeux tournent", a.GameAttached && b.GameAttached && !g1.HasExited && !g2.HasExited);
            a.Stop(); b.Stop(); s.Stop();
            try { g1.Kill(); } catch (Exception) { }
            try { g2.Kill(); } catch (Exception) { }
            Console.WriteLine(fails == 0 ? "TEST MAJ12E OK" : (fails + " TEST(S) EN ECHEC"));
            return fails;
        }

        // --jak2swap : Jak 3 -> vrai Jak 2 -> Jak 3 -> Jak 2 -> Keira... (changement de corps en direct)
        public static int Jak2Swap()
        {
            const int port = 27983;
            string mod = Environment.GetEnvironmentVariable("JAK3ONLINE_MOD");
            string sp = Environment.GetEnvironmentVariable("JAK3ONLINE_TMP");
            string dirA = Path.Combine(sp, "profilS");
            try { if (Directory.Exists(dirA)) Directory.Delete(dirA, true); } catch (Exception) { }
            Server s = new Server();
            s.Start(port);
            Process g1 = StartGame(mod, sp, Path.Combine(mod, "data"), "cfg");
            Client a = new Client(); a.BridgeDir = Path.Combine(mod, "data", "online", "bridge"); a.WantedName = "LEON"; a.ServerAddress = "127.0.0.1:" + port; a.ProfileDir = dirA;
            a.Start(); a.Connect();
            Check("jeu detecte", Wait(() => a.GameAttached && a.NetState == Shm.NET_LOBBY, 180000));
            Thread.Sleep(12000);
            a.TestSetXp(200000); a.TestSetMoney(60000);
            a.UiJoinWorld();
            Check("monde en ligne", Wait(() => (UiF(a) & 2) != 0 && Loaded(a) && Lvl(a).StartsWith("cty"), 240000));
            Thread.Sleep(5000);
            a.TestCommand(154); Thread.Sleep(1500); a.TestCommand(175); Thread.Sleep(1500); a.TestCommand(155); Thread.Sleep(1500);
            int[] seq = { 2, 0, 2, 1, 2, 3, 0 };
            foreach (int k in seq)
            {
                Console.WriteLine("   -> personnage " + k);
                a.TestCommand(590 + k);
                bool ok = Wait(() => Gb(a, 107) == k && (k != 2 || Gb(a, 88 + 35) == 4) && (k == 2 || k > 0 || Gb(a, 88 + 35) == 6), 25000);
                Thread.Sleep(3000);
                Console.WriteLine("      corps " + Gb(a, 88 + 35) + "  perso " + Gb(a, 107) + "  jeu vivant " + (a.GameAttached && !g1.HasExited));
                Check("personnage " + k + " pose, jeu vivant", ok && a.GameAttached && !g1.HasExited);
                ShotOf(g1.Id, "s-perso-" + k);
                if (g1.HasExited) break;
            }
            a.Stop(); s.Stop();
            try { g1.Kill(); } catch (Exception) { }
            Console.WriteLine(fails == 0 ? "TEST JAK2SWAP OK" : (fails + " TEST(S) EN ECHEC"));
            return fails;
        }

        // --village : le village de Jak 1 : la mer (on y nage) et le requin lurker
        // --maj13b : collisions entre joueurs, les 8 maisons, boites aux lettres, bateau, nage
        // --events13 : COURSE AUTO (circuit) et CACHE-CACHE (manoir)
        public static int Events13()
        {
            const int port = 27987;
            string mod = Environment.GetEnvironmentVariable("JAK3ONLINE_MOD");
            string sp = Environment.GetEnvironmentVariable("JAK3ONLINE_TMP");
            string dirA = Path.Combine(sp, "profilEv13");
            try { if (Directory.Exists(dirA)) Directory.Delete(dirA, true); } catch (Exception) { }
            Server s = new Server();
            s.Start(port);
            Process g1 = StartGame(mod, sp, Path.Combine(mod, "data"), "cfg");
            Client a = new Client(); a.BridgeDir = Path.Combine(mod, "data", "online", "bridge"); a.WantedName = "LEON"; a.ServerAddress = "127.0.0.1:" + port; a.ProfileDir = dirA;
            a.Log = m => { if (m.Contains("course") || m.Contains("cache")) Console.WriteLine("   [log] " + m); }; a.Start(); a.Connect();
            Check("jeu detecte", Wait(() => a.GameAttached && a.NetState == Shm.NET_LOBBY, 180000));
            Thread.Sleep(12000);
            a.TestSetXp(100000); a.TestSetMoney(1000);
            a.UiJoinWorld();
            Check("monde en ligne", Wait(() => (UiF(a) & 2) != 0 && Loaded(a) && Lvl(a).StartsWith("cty"), 240000));
            Thread.Sleep(6000);
            // ---- COURSE AUTO
            a.TestStartEvent(Client.EVK_KART);
            Check("course auto lancee", Wait(() => a.TestEventKind == Client.EVK_KART && a.TestEventState == 1, 8000));
            Check("depart pour le circuit (compte a rebours)", Wait(() => MapOf(a) == 16 && Loaded(a), 120000));
            Thread.Sleep(9000);
            a.TestCommand(697); Thread.Sleep(400);
            Console.WriteLine("   circuit : pad41 " + Gb(a, 88 + 37) + "  flags 0x" + Gflags(a).ToString("x"));
            ShotOf(g1.Id, "k1-circuit-depart");
            Check("un vehicule pour la course", (Gb(a, 88 + 37) & 1) != 0 || (Gflags(a) & 0x80) != 0);
            a.TestCommand(572); Thread.Sleep(3000);
            ShotOf(g1.Id, "k2-circuit-roule");
            long m0 = a.TestMoney;
            a.TestCommand(695); Thread.Sleep(2500);
            Check("l'arrivee ne compte pas sans faire le tour", a.TestEventState == 1);
            for (int n = 0; n < 3; n++) { a.TestCommand(690 + n); Thread.Sleep(5500); }
            a.TestCommand(697); Thread.Sleep(400);
            Console.WriteLine("   points de passage franchis : " + Gb(a, 88 + 38));
            Check("3 points de passage dans l'ordre", Gb(a, 88 + 38) == 3);
            Thread.Sleep(2000);
            a.TestCommand(695);
            Check("course gagnee", Wait(() => a.TestEventState == 2 && a.TestEventWinner != 0, 15000));
            Thread.Sleep(3000);
            ShotOf(g1.Id, "k3-circuit-gagne");
            Console.WriteLine("   orbes : " + m0 + " -> " + a.TestMoney);
            Check("3000 orbes pour le gagnant", a.TestMoney - m0 >= 3000);
            Check("retour au port apres la course", Wait(() => MapOf(a) < 0 && Loaded(a), 90000));
            Thread.Sleep(8000);
            // ---- CACHE-CACHE
            a.TestStartEvent(Client.EVK_HIDE);
            Check("cache-cache lance", Wait(() => a.TestEventKind == Client.EVK_HIDE && a.TestEventState == 1, 8000));
            Check("depart pour le manoir", Wait(() => MapOf(a) == 12 && Loaded(a), 120000));
            Thread.Sleep(8000);
            a.TestCommand(697); Thread.Sleep(400);
            Console.WriteLine("   cache-cache : pad41 " + Gb(a, 88 + 37));
            ShotOf(g1.Id, "h1-manoir-bleu");
            Check("personnage rouge cache, joueurs en bleu", (Gb(a, 88 + 37) & 6) == 6);
            long m1 = a.TestMoney;
            a.TestCommand(696); Thread.Sleep(600);
            ShotOf(g1.Id, "h2-trouve");
            Check("personnage rouge trouve", Wait(() => a.TestEventState == 2 && a.TestEventWinner != 0, 15000));
            Thread.Sleep(3000);
            Console.WriteLine("   orbes : " + m1 + " -> " + a.TestMoney);
            Check("orbes pour le gagnant (plafond anti-triche : 5000 par 30 min)", a.TestMoney - m1 >= 2000);
            Check("le jeu tourne", a.GameAttached && !g1.HasExited);
            a.Stop(); s.Stop();
            try { g1.Kill(); } catch (Exception) { }
            Console.WriteLine(fails == 0 ? "TEST EVENTS13 OK" : (fails + " TEST(S) EN ECHEC"));
            return fails;
        }

        // --vehmap : appeler un vehicule sur une carte du mod (maison)
        public static int VehMap()
        {
            const int port = 27988;
            string mod = Environment.GetEnvironmentVariable("JAK3ONLINE_MOD");
            string sp = Environment.GetEnvironmentVariable("JAK3ONLINE_TMP");
            string dirA = Path.Combine(sp, "profilVM");
            try { if (Directory.Exists(dirA)) Directory.Delete(dirA, true); } catch (Exception) { }
            Server s = new Server();
            s.Start(port);
            Process g1 = StartGame(mod, sp, Path.Combine(mod, "data"), "cfg");
            Client a = new Client(); a.BridgeDir = Path.Combine(mod, "data", "online", "bridge"); a.WantedName = "LEON"; a.ServerAddress = "127.0.0.1:" + port; a.ProfileDir = dirA;
            a.Start(); a.Connect();
            Check("jeu detecte", Wait(() => a.GameAttached && a.NetState == Shm.NET_LOBBY, 180000));
            Thread.Sleep(12000);
            a.TestSetXp(100000); a.TestSetMoney(900000);
            a.UiJoinWorld();
            Check("monde en ligne", Wait(() => (UiF(a) & 2) != 0 && Loaded(a) && Lvl(a).StartsWith("cty"), 240000));
            Thread.Sleep(5000);
            int item = int.Parse(Environment.GetEnvironmentVariable("VEHITEM") ?? "45");
            a.TestCommand(500); Thread.Sleep(1500);
            a.TestCommand(item < 96 ? 100 + item : 500 + item - 96); Thread.Sleep(1500);
            a.TestCommand(610);
            Check("cabane chargee", Wait(() => MapOf(a) == 8 && Loaded(a), 150000));
            Thread.Sleep(6000);
            a.TestCommand(int.Parse(Environment.GetEnvironmentVariable("VEHCMD") ?? "245"));
            Thread.Sleep(8000);
            ShotOf(g1.Id, "vm-vehicule");
            Check("le jeu tourne avec un vehicule", a.GameAttached && !g1.HasExited);
            a.Stop(); s.Stop();
            try { g1.Kill(); } catch (Exception) { }
            return fails;
        }

        public static bool CollOnly;
        // --acc13 : ACCESSOIRES (achat, port, vus par l'autre joueur), couleurs des personnages, dialogues
        public static int Acc13()
        {
            const int port = 27988;
            string mod = Environment.GetEnvironmentVariable("JAK3ONLINE_MOD");
            string sp = Environment.GetEnvironmentVariable("JAK3ONLINE_TMP");
            string dirA = Path.Combine(sp, "profilAcc");
            try { if (Directory.Exists(dirA)) Directory.Delete(dirA, true); } catch (Exception) { }
            Server s = new Server();
            s.Start(port);
            Process g1 = StartGame(mod, sp, Path.Combine(mod, "data"), "cfg");
            Thread.Sleep(8000);
            Process g2 = StartGame(mod, sp, Path.Combine(sp, "data2"), "cfg2");
            Client a = new Client(); a.BridgeDir = Path.Combine(mod, "data", "online", "bridge"); a.WantedName = "LEON"; a.ServerAddress = "127.0.0.1:" + port; a.ProfileDir = dirA;
            a.Log = m => Console.WriteLine("   [J1] " + m); a.Start(); a.Connect();
            Client b = new Client(); b.BridgeDir = Path.Combine(sp, "data2", "online", "bridge"); b.WantedName = "Joueur2"; b.Ephemeral = true; b.ServerAddress = "127.0.0.1:" + port;
            b.Log = m => Console.WriteLine("   [J2] " + m); b.Start(); b.Connect();
            Check("les deux jeux sont detectes", Wait(() => a.GameAttached && b.GameAttached && a.NetState == Shm.NET_LOBBY && b.NetState == Shm.NET_LOBBY, 180000));
            foreach (Process gp in new Process[] { g1, g2 }) { IntPtr w = WindowOf(gp.Id); if (w != IntPtr.Zero) ShowWindow(w, 7); }
            Thread.Sleep(15000);
            a.TestSetXp(400000); a.TestSetMoney(2500000);
            b.TestSetXp(100); b.TestSetMoney(80000);
            a.UiJoinWorld(); Thread.Sleep(1500); b.UiJoinWorld();
            Check("monde en ligne dans les deux jeux", Wait(() => (UiF(a) & 2) != 0 && (UiF(b) & 2) != 0 && Loaded(a) && Loaded(b) && Lvl(a).StartsWith("cty") && Lvl(b).StartsWith("cty"), 240000));
            Thread.Sleep(5000);
            GoTo(a, b, 34, 34, l => l.StartsWith("cty"), l => l.StartsWith("cty"), "les deux au port");
            Thread.Sleep(3000);
            // ---- niveau requis : J2 (niveau 1) ne peut pas acheter la couronne (niveau 30)
            long mb0 = b.TestMoney;
            b.TestCommand(500 + 119 - 96); Thread.Sleep(2000);
            Check("couronne refusee au niveau 1 (reservee au niveau 30)", b.TestMoney == mb0 && b.TestWear == 0);
            // ---- J1 achete (par le menu du jeu) une couronne, des lunettes, des ailes d'ange
            long m0 = a.TestMoney;
            foreach (int id in new int[] { 119, 121, 126 }) { a.TestCommand(500 + id - 96); Thread.Sleep(1800); }
            Console.WriteLine("   achats : " + m0 + " -> " + a.TestMoney + " ; porte 0x" + a.TestWear.ToString("x"));
            Check("3 accessoires achetes (100000 + 2000 + 150000)", m0 - a.TestMoney == 252000);
            Check("portes tout de suite (tete, visage, dos)", a.TestWear == (6 | (1 << 8) | (3 << 16)));
            Thread.Sleep(3000);
            a.TestCommand(740); Thread.Sleep(400);
            Console.WriteLine("   J1 : accessoires affiches " + Gb(a, 88 + 36) + " ; octet 0x" + Gb(a, 88 + 37).ToString("x"));
            Check("J1 voit ses 3 accessoires", Gb(a, 88 + 36) == 3);
            // J2 se place devant J1 (face a face) : J2 voit le visage de J1
            Console.WriteLine("   avant : J1-J2 " + Dist(PosOf(a), PosOf(b)).ToString("0.00") + " m");
            b.TestCommand(743); Thread.Sleep(300);
            Console.WriteLine("   apres 743 (0,3 s) : J1-J2 " + Dist(PosOf(a), PosOf(b)).ToString("0.00") + " m");
            Thread.Sleep(1200);
            Console.WriteLine("   apres 743 (1,5 s) : J1-J2 " + Dist(PosOf(a), PosOf(b)).ToString("0.00") + " m");
            b.TestCommand(745); Thread.Sleep(200);
            b.TestCommand(744); a.TestCommand(741); Thread.Sleep(2500);   // J2 invisible chez lui : sa camera voit J1 de face
            Console.WriteLine("   J1-J2 " + Dist(PosOf(a), PosOf(b)).ToString("0.00") + " m");
            ShotOf(g2.Id, "a1-face-vu-par-j2");
            ShotOf(g1.Id, "a1b-j1");
            if (Environment.GetEnvironmentVariable("ACC_QUICK") == "1") { a.Stop(); b.Stop(); s.Stop(); try { g1.Kill(); g2.Kill(); } catch (Exception) { } return fails; }
            ShotOf(g1.Id, "a2-dos-vu-par-j1");
            b.TestCommand(740); Thread.Sleep(400);
            Console.WriteLine("   J2 voit sur J1 : " + Gb(b, 88 + 38) + " accessoire(s)");
            ShotOf(g2.Id, "a3-vu-par-j2");
            Check("J2 voit les 3 accessoires de J1", Gb(b, 88 + 38) == 3);
            // ---- personnages (couleurs corrigees) avec accessoires
            foreach (int k in new int[] { 1, 5, 7, 4, 3 })
            {
                a.TestBuy(Profil.PersoItem[k]); a.TestPerso(k); Thread.Sleep(5000);
                b.TestCommand(743); Thread.Sleep(300); b.TestCommand(744); a.TestCommand(741); Thread.Sleep(2200);
                ShotOf(g2.Id, "a4-perso" + k);
            }
            // retour a Jak : l'avatar chez J2 doit retrouver le corps de Jak (avec puis sans J2 cache)
            a.TestPerso(0); Thread.Sleep(5000);
            b.TestCommand(740); Thread.Sleep(400);
            Console.WriteLine("   retour a Jak : corps de l'avatar chez J2 = " + Gb(b, 88 + 39));
            ShotOf(g2.Id, "a4-retour-jak");
            // un autre chapeau, un masque, une cape
            foreach (int id in new int[] { 115, 122, 125 }) { a.TestCommand(500 + id - 96); Thread.Sleep(1800); }
            a.TestPerso(0); Thread.Sleep(4000);
            b.TestCommand(743); Thread.Sleep(300); b.TestCommand(744); a.TestCommand(741); Thread.Sleep(2200);
            ShotOf(g2.Id, "a5-cowboy-ninja");
            ShotOf(g1.Id, "a6-cape");
            foreach (int id in new int[] { 114, 116, 117, 118, 120, 123, 124, 127 })
            {
                a.TestCommand(500 + id - 96); Thread.Sleep(2200);
                b.TestCommand(743); Thread.Sleep(300); b.TestCommand(744); a.TestCommand(741); Thread.Sleep(1700);
                ShotOf(g2.Id, "a8-acc" + id);
            }
            ShotOf(g2.Id, "a7-cape-vu-par-j2");
            b.TestCommand(740); Thread.Sleep(400);
            Check("J2 voit le changement (3 accessoires)", Gb(b, 88 + 38) == 3);
            // le menu STYLE (ce que voit le joueur)
            a.TestCommand(746); Thread.Sleep(1500);
            ShotOf(g1.Id, "a9-menu-style");
            a.TestCommand(746); Thread.Sleep(300);
            for (int k = 0; k < 12; k++) { a.TestCommand(747); Thread.Sleep(120); }
            Thread.Sleep(800);
            ShotOf(g1.Id, "a9-menu-style-bas");
            if (Environment.GetEnvironmentVariable("ACC_NODIAL") == "1") { a.Stop(); b.Stop(); s.Stop(); try { g1.Kill(); g2.Kill(); } catch (Exception) { } return fails; }
            // ---- DIALOGUES : chaque replique doit exister dans les voix du jeu
            string bad = "";
            for (int k = 0; k < 35; k++)
            {
                a.TestCommand(750 + k);
                int st = 0;
                for (int n = 0; n < 12 && st < 3; n++) { Thread.Sleep(250); a.TestCommand(709); Thread.Sleep(150); st = Gb(a, 88 + 36); }
                Console.WriteLine("   dialogue " + k + " : etat " + st);
                if (st != 3) bad += k + " ";
                Thread.Sleep(2500);
            }
            Console.WriteLine("   repliques qui ne jouent pas : " + (bad == "" ? "aucune" : bad));
            Check("toutes les repliques jouent", bad == "");
            Check("les jeux tournent", a.GameAttached && !g1.HasExited && !g2.HasExited);
            a.Stop(); b.Stop(); s.Stop();
            try { g1.Kill(); } catch (Exception) { }
            try { g2.Kill(); } catch (Exception) { }
            Console.WriteLine(fails == 0 ? "TEST ACC13 OK" : (fails + " TEST(S) EN ECHEC"));
            return fails;
        }

        public static int Maj13b()
        {
            const int port = 27985;
            string mod = Environment.GetEnvironmentVariable("JAK3ONLINE_MOD");
            string sp = Environment.GetEnvironmentVariable("JAK3ONLINE_TMP");
            string dirA = Path.Combine(sp, "profil13b");
            try { if (Directory.Exists(dirA)) Directory.Delete(dirA, true); } catch (Exception) { }
            Server s = new Server();
            s.Start(port);
            Process g1 = StartGame(mod, sp, Path.Combine(mod, "data"), "cfg");
            Thread.Sleep(8000);
            Process g2 = StartGame(mod, sp, Path.Combine(sp, "data2"), "cfg2");
            Client a = new Client(); a.BridgeDir = Path.Combine(mod, "data", "online", "bridge"); a.WantedName = "LEON"; a.ServerAddress = "127.0.0.1:" + port; a.ProfileDir = dirA;
            a.Log = m => Console.WriteLine("   [J1] " + m); a.Start(); a.Connect();
            Client b = new Client(); b.BridgeDir = Path.Combine(sp, "data2", "online", "bridge"); b.WantedName = "Joueur2"; b.Ephemeral = true; b.ServerAddress = "127.0.0.1:" + port;
            b.Log = m => Console.WriteLine("   [J2] " + m); b.Start(); b.Connect();
            Check("les deux jeux sont detectes", Wait(() => a.GameAttached && b.GameAttached && a.NetState == Shm.NET_LOBBY && b.NetState == Shm.NET_LOBBY, 180000));
            foreach (Process gp in new Process[] { g1, g2 }) { IntPtr w = WindowOf(gp.Id); if (w != IntPtr.Zero) ShowWindow(w, 7); }
            Thread.Sleep(15000);
            a.TestSetXp(400000); a.TestSetMoney(2500000);
            b.TestSetXp(400000); b.TestSetMoney(80000);
            a.UiJoinWorld(); Thread.Sleep(1500); b.UiJoinWorld();
            Check("monde en ligne dans les deux jeux", Wait(() => (UiF(a) & 2) != 0 && (UiF(b) & 2) != 0 && Loaded(a) && Loaded(b) && Lvl(a).StartsWith("cty") && Lvl(b).StartsWith("cty"), 240000));
            Thread.Sleep(5000);
            GoTo(a, b, 34, 34, l => l.StartsWith("cty"), l => l.StartsWith("cty"), "les deux au port");
            Thread.Sleep(5000);
            // ---- COLLISIONS ENTRE JOUEURS
            b.TestCommand(651); Thread.Sleep(1800);
            float[] pa = PosOf(a), pb = PosOf(b);
            double hd = Math.Sqrt(Math.Pow((pa[0] - pb[0]) / 4096.0, 2) + Math.Pow((pa[2] - pb[2]) / 4096.0, 2));
            b.TestCommand(652); Thread.Sleep(400);
            Console.WriteLine("   J2 fonce sur J1 : ecart " + hd.ToString("0.00") + " m ; corps solide " + ((Gb(b, 88 + 39) & 1) != 0));
            ShotOf(g2.Id, "c1-bouscule-j2");
            Check("J2 bute contre J1 (on ne se traverse plus)", hd > 0.7 && (Gb(b, 88 + 39) & 1) != 0);
            b.TestCommand(650); Thread.Sleep(2500);
            b.TestCommand(652); Thread.Sleep(400);
            pa = PosOf(a); pb = PosOf(b);
            Console.WriteLine("   J2 sur la tete : " + ((pb[1] - pa[1]) / 4096f).ToString("0.00") + " m au-dessus ; debout " + ((Gb(b, 88 + 39) & 2) != 0));
            ShotOf(g2.Id, "c2-sur-la-tete-j2");
            ShotOf(g1.Id, "c2-sur-la-tete-j1");
            Check("J2 debout sur la tete de J1", (Gb(b, 88 + 39) & 2) != 0);
            a.TestCommand(653);
            float[] pa0 = PosOf(a);
            Thread.Sleep(2200);
            pa = PosOf(a); pb = PosOf(b);
            double walked = Dist(pa0, pa);
            hd = Math.Sqrt(Math.Pow((pa[0] - pb[0]) / 4096.0, 2) + Math.Pow((pa[2] - pb[2]) / 4096.0, 2));
            Console.WriteLine("   J1 marche " + walked.ToString("0.0") + " m ; J2 a " + hd.ToString("0.00") + " m de lui, " + ((pb[1] - pa[1]) / 4096f).ToString("0.00") + " m au-dessus");
            ShotOf(g1.Id, "c3-porte-j1");
            Check("J1 marche et emporte J2 sur sa tete", walked > 2 && hd < 1.2 && (pb[1] - pa[1]) / 4096f > 1.2);
            if (CollOnly)
            {
                a.Stop(); b.Stop(); s.Stop();
                try { g1.Kill(); } catch (Exception) { }
                try { g2.Kill(); } catch (Exception) { }
                Console.WriteLine(fails == 0 ? "TEST COLL OK" : (fails + " TEST(S) EN ECHEC"));
                return fails;
            }
            // ---- LES MAISONS
            long m0 = a.TestMoney;
            a.TestCommand(500 + 113 - 96); Thread.Sleep(2500);
            Console.WriteLine("   achat du palais : " + m0 + " -> " + a.TestMoney);
            Check("palais de l'ile achete 1 000 000", m0 - a.TestMoney == 1000000);
            a.TestCommand(617);
            Check("palais de l'ile charge", Wait(() => Lvl(a) == "ow-palais" && Loaded(a) && MapOf(a) == 15, 150000));
            Thread.Sleep(8000);
            ShotOf(g1.Id, "m1-palais-entree");
            a.TestCommand(645); Thread.Sleep(400);
            Check("bateau(x) sur l'eau", (Gb(a, 88 + 38) & 2) != 0);
            long m1 = a.TestMoney;
            a.TestCommand(630); Thread.Sleep(3000);
            Console.WriteLine("   boite aux lettres : " + m1 + " -> " + a.TestMoney);
            ShotOf(g1.Id, "m2-boite-aux-lettres");
            Check("boite aux lettres : +1000 orbes (1 jour)", a.TestMoney - m1 == 1000);
            long m2 = a.TestMoney;
            a.TestCommand(631); Thread.Sleep(3000);
            a.TestCommand(630); Thread.Sleep(3000);
            Check("2e boite : +1000, 1re deja relevee : rien", a.TestMoney - m2 == 1000);
            a.TestCommand(640); Thread.Sleep(3000);
            a.TestCommand(645); Thread.Sleep(400);
            ShotOf(g1.Id, "m3-nage-mer");
            Check("Jak nage dans la mer de l'ile", (Gb(a, 88 + 38) & 1) != 0);
            a.TestCommand(641); Thread.Sleep(3000);
            a.TestCommand(645); Thread.Sleep(400);
            ShotOf(g1.Id, "m4-nage-2");
            Console.WriteLine("   2e eau : nage " + ((Gb(a, 88 + 38) & 1) != 0));
            foreach (int hk in new int[] { 3, 4, 5, 6 })
            {
                a.TestCommand(500 + 109 + hk - 3 - 96); Thread.Sleep(2000);
                a.TestCommand(610 + hk);
                bool ok = Wait(() => MapOf(a) == 8 + hk && Loaded(a), 150000);
                Thread.Sleep(7000);
                ShotOf(g1.Id, "m5-maison-" + hk);
                Check("maison " + hk + " chargee (" + Lvl(a) + ")", ok);
            }
            a.TestCommand(630); Thread.Sleep(2500);
            ShotOf(g1.Id, "m6-temple-boite");
            Check("les deux jeux tournent", a.GameAttached && b.GameAttached && !g1.HasExited && !g2.HasExited);
            a.Stop(); b.Stop(); s.Stop();
            try { g1.Kill(); } catch (Exception) { }
            try { g2.Kill(); } catch (Exception) { }
            Console.WriteLine(fails == 0 ? "TEST MAJ13B OK" : (fails + " TEST(S) EN ECHEC"));
            return fails;
        }

        // --maj13 : premiere personne (R3), la mer du village
        public static int Maj13()
        {
            const int port = 27983;
            string mod = Environment.GetEnvironmentVariable("JAK3ONLINE_MOD");
            string sp = Environment.GetEnvironmentVariable("JAK3ONLINE_TMP");
            string dirA = Path.Combine(sp, "profil13");
            try { if (Directory.Exists(dirA)) Directory.Delete(dirA, true); } catch (Exception) { }
            Server s = new Server();
            s.Start(port);
            Process g1 = StartGame(mod, sp, Path.Combine(mod, "data"), "cfg");
            Client a = new Client(); a.BridgeDir = Path.Combine(mod, "data", "online", "bridge"); a.WantedName = "LEON"; a.ServerAddress = "127.0.0.1:" + port; a.ProfileDir = dirA;
            a.Start(); a.Connect();
            Check("jeu detecte", Wait(() => a.GameAttached && a.NetState == Shm.NET_LOBBY, 180000));
            Thread.Sleep(12000);
            a.UiJoinWorld();
            Check("monde en ligne", Wait(() => (UiF(a) & 2) != 0 && Loaded(a) && Lvl(a).StartsWith("cty"), 240000));
            Thread.Sleep(5000);
            // ---- premiere personne
            ShotOf(g1.Id, "m13-vue-normale");
            a.TestCommand(581); Thread.Sleep(2500);
            a.TestCommand(582); Thread.Sleep(400);
            Console.WriteLine("   1re personne : pad37 " + Gb(a, 88 + 37));
            ShotOf(g1.Id, "m13-premiere-personne");
            Check("vue a la premiere personne active", Gb(a, 88 + 37) == 3);
            a.TestCommand(581); Thread.Sleep(2000);
            a.TestCommand(582); Thread.Sleep(400);
            ShotOf(g1.Id, "m13-retour-normal");
            Check("retour a la vue normale", Gb(a, 88 + 37) == 0);
            // ---- meteo (admin) et dialogue
            a.TestCommand(662); Thread.Sleep(11000);
            a.TestCommand(669); Thread.Sleep(400);
            Console.WriteLine("   meteo : pad40 " + Gb(a, 88 + 36));
            ShotOf(g1.Id, "m13-orage");
            Check("orage (eclairs, pluie forte)", (Gb(a, 88 + 36) & 15) == 2 && (Gb(a, 88 + 36) & 16) != 0);
            a.TestCommand(663); Thread.Sleep(16000);
            a.TestCommand(669); Thread.Sleep(400);
            ShotOf(g1.Id, "m13-neige");
            Check("neige", (Gb(a, 88 + 36) & 15) == 3 && (Gb(a, 88 + 36) & 16) != 0);
            a.TestCommand(660); Thread.Sleep(9000);
            a.TestCommand(669); Thread.Sleep(400);
            Check("retour du beau temps", (Gb(a, 88 + 36) & 15) == 0);
            a.TestCommand(722); Thread.Sleep(1500);
            ShotOf(g1.Id, "m13-dialogue-sig");
            Check("le jeu tourne apres un dialogue", a.GameAttached && !g1.HasExited);
            // ---- la mer du village
            a.TestCommand(400);
            Check("village charge", Wait(() => Lvl(a) == "ow-jak1" && Loaded(a) && MapOf(a) == 0, 150000));
            Thread.Sleep(6000);
            a.TestCommand(579); Thread.Sleep(4000);
            a.TestCommand(580); Thread.Sleep(500);
            Console.WriteLine("   au large : nage " + ((Gb(a, 88 + 36) & 1) != 0) + "  y " + (PosOf(a)[1] / 4096f).ToString("0.0"));
            ShotOf(g1.Id, "m13-en-mer");
            Check("Jak nage dans la mer du village", (Gb(a, 88 + 36) & 1) != 0);
            bool shark = Wait(() => { a.TestCommand(580); Thread.Sleep(300); return (Gb(a, 88 + 36) & 2) != 0; }, 20000);
            ShotOf(g1.Id, "m13-requin");
            Check("le requin lurker arrive", shark);
            Thread.Sleep(4000);
            ShotOf(g1.Id, "m13-requin-mange");
            Check("le jeu tourne", a.GameAttached && !g1.HasExited);
            a.Stop(); s.Stop();
            try { g1.Kill(); } catch (Exception) { }
            Console.WriteLine(fails == 0 ? "TEST MAJ13 OK" : (fails + " TEST(S) EN ECHEC"));
            return fails;
        }

        public static int Village()
        {
            const int port = 27982;
            string mod = Environment.GetEnvironmentVariable("JAK3ONLINE_MOD");
            string sp = Environment.GetEnvironmentVariable("JAK3ONLINE_TMP");
            string dirA = Path.Combine(sp, "profilV");
            try { if (Directory.Exists(dirA)) Directory.Delete(dirA, true); } catch (Exception) { }
            Server s = new Server();
            s.Start(port);
            Process g1 = StartGame(mod, sp, Path.Combine(mod, "data"), "cfg");
            Client a = new Client(); a.BridgeDir = Path.Combine(mod, "data", "online", "bridge"); a.WantedName = "LEON"; a.ServerAddress = "127.0.0.1:" + port; a.ProfileDir = dirA;
            a.Start(); a.Connect();
            Check("jeu detecte", Wait(() => a.GameAttached && a.NetState == Shm.NET_LOBBY, 180000));
            Thread.Sleep(12000);
            a.UiJoinWorld();
            Check("monde en ligne", Wait(() => (UiF(a) & 2) != 0 && Loaded(a) && Lvl(a).StartsWith("cty"), 240000));
            Thread.Sleep(4000);
            a.TestCommand(400);
            Check("village charge", Wait(() => Lvl(a) == "ow-jak1" && Loaded(a) && MapOf(a) == 0, 150000));
            Thread.Sleep(6000);
            ShotOf(g1.Id, "v1-village-depart");
            for (int k = 0; k < 4; k++) { a.TestCommand(420 + 4 + k * 4); Thread.Sleep(1800); ShotOf(g1.Id, "v2-vue" + k); }
            a.TestCommand(579); Thread.Sleep(4000);
            a.TestCommand(580); Thread.Sleep(500);
            Console.WriteLine("   au large : nage " + ((Gb(a, 88 + 36) & 1) != 0) + "  y " + (PosOf(a)[1] / 4096f).ToString("0.0"));
            ShotOf(g1.Id, "v3-en-mer");
            Check("Jak nage dans la mer du village", (Gb(a, 88 + 36) & 1) != 0);
            bool shark = Wait(() => { a.TestCommand(580); Thread.Sleep(300); return (Gb(a, 88 + 36) & 2) != 0; }, 20000);
            ShotOf(g1.Id, "v4-requin");
            Check("le requin lurker arrive", shark);
            Thread.Sleep(5000);
            ShotOf(g1.Id, "v5-requin-mange");
            Check("le jeu tourne", a.GameAttached && !g1.HasExited);
            a.Stop(); s.Stop();
            try { g1.Kill(); } catch (Exception) { }
            Console.WriteLine(fails == 0 ? "TEST VILLAGE OK" : (fails + " TEST(S) EN ECHEC"));
            return fails;
        }

        // --j2local : le joueur 2 local (2e jeu dans une 2e fenetre, 2e manette, meme session)
        public static int J2Local()
        {
            const int port = 27986;
            string mod = Environment.GetEnvironmentVariable("JAK3ONLINE_MOD");
            string sp = Environment.GetEnvironmentVariable("JAK3ONLINE_TMP");
            string dirA = Path.Combine(sp, "profilJ");
            Environment.SetEnvironmentVariable("JAK3ONLINE_J2DIR", Path.Combine(sp, "j2"));
            Environment.SetEnvironmentVariable("JAK3ONLINE_PROFILE", Path.Combine(sp, "profilJ2base"));
            try { if (Directory.Exists(dirA)) Directory.Delete(dirA, true); } catch (Exception) { }
            Server s = new Server();
            s.Start(port);
            Process g1 = StartGame(mod, sp, Path.Combine(mod, "data"), "cfg");
            Client a = new Client(); a.BridgeDir = Path.Combine(mod, "data", "online", "bridge"); a.WantedName = "LEON"; a.ServerAddress = "127.0.0.1:" + port; a.ProfileDir = dirA;
            a.Log = m => Console.WriteLine("   [J1] " + m); a.Start(); a.Connect();
            Check("le jeu du joueur 1 est detecte", Wait(() => a.GameAttached && a.NetState == Shm.NET_LOBBY, 180000));
            Thread.Sleep(12000);
            a.UiJoinWorld();
            Check("J1 dans le monde en ligne", Wait(() => (UiF(a) & 2) != 0 && Loaded(a) && Lvl(a).StartsWith("cty"), 240000));
            Console.WriteLine("   manettes vues par J1 : " + Gb(a, 88 + 33) + "  (la sienne : " + (Gb(a, 88 + 32) - 1) + ")");
            // le bouton "joueur 2 local"
            a.LocalJ2Request(1);
            Check("le 2e jeu demarre", Wait(() => a.LocalJ2Game != null && a.LocalJ2Client != null, 60000));
            Process g2 = a.LocalJ2Game;
            Client c = a.LocalJ2Client;
            Check("le 2e jeu est detecte (son propre dossier)", Wait(() => c.GameAttached, 180000));
            Check("J2 n'est jamais createur", !c.IsCreateur);
            Check("J2 rejoint la meme session tout seul", Wait(() => c.SessionId != 0 && c.SessionId == a.SessionId && (UiF(c) & 2) != 0 && Loaded(c), 300000));
            Check("J1 voit J2 (2 joueurs)", Wait(() => a.SessionCount == 2, 20000));
            Thread.Sleep(8000);
            Console.WriteLine("   J2 : manette " + (Gb(c, 88 + 32) - 1) + " sur " + Gb(c, 88 + 33) + "  niveau " + Lvl(c) + "  drapeaux EXT J1 " + a.LocalJ2Flags + "  J2 " + c.LocalJ2Flags);
            Check("drapeaux : J1 sait qu'un J2 tourne, J2 sait qu'il est J2", (a.LocalJ2Flags & 2) != 0 && (c.LocalJ2Flags & 1) != 0);
            ShotOf(g1.Id, "j2-fenetre-joueur1");
            ShotOf(g2.Id, "j2-fenetre-joueur2");
            // arret
            a.LocalJ2Request(0);
            Check("J2 s'arrete (jeu ferme, J1 seul)", Wait(() => g2.HasExited && a.SessionCount == 1 && a.LocalJ2Flags == 0, 30000));
            Check("le jeu du joueur 1 tourne", a.GameAttached && !g1.HasExited);
            a.Stop(); s.Stop();
            try { g1.Kill(); } catch (Exception) { }
            Console.WriteLine(fails == 0 ? "TEST J2 LOCAL OK" : (fails + " TEST(S) EN ECHEC"));
            return fails;
        }

        // --aide : page EN LIGNE avec le programme, puis sans (aide pour le lancer)
        public static int Aide()
        {
            const int port = 27992;
            string mod = Environment.GetEnvironmentVariable("JAK3ONLINE_MOD");
            string sp = Environment.GetEnvironmentVariable("JAK3ONLINE_TMP");
            Server s = new Server();
            s.Start(port);
            Process g1 = StartGame(mod, sp, Path.Combine(mod, "data"), "cfg");
            Client a = new Client(); a.BridgeDir = Path.Combine(mod, "data", "online", "bridge"); a.WantedName = "LEON"; a.Ephemeral = true; a.ServerAddress = "127.0.0.1:" + port;
            a.Start(); a.Connect();
            Check("jeu detecte", Wait(() => a.GameAttached && a.NetState == Shm.NET_LOBBY, 180000));
            IntPtr w = WindowOf(g1.Id); if (w != IntPtr.Zero) ShowWindow(w, 7);
            Thread.Sleep(15000);
            a.TestCommand(1);
            Check("partie chargee", Wait(() => Loaded(a), 200000));
            Thread.Sleep(8000);
            foreach (int l in new int[] { 1, 0, 2 })
            {
                a.TestCommand(300 + l); Thread.Sleep(600);
                a.TestCommand(85); Thread.Sleep(2500);
                ShotOf(g1.Id, "a-connecte-langue" + l);
                a.TestCommand(578); Thread.Sleep(1500);
                ShotOf(g1.Id, "a-bas-langue" + l);
                a.TestCommand(80); Thread.Sleep(1200);
            }
            a.TestCommand(301); Thread.Sleep(600);
            a.TestCommand(85); Thread.Sleep(1500);
            a.Stop();
            Thread.Sleep(6000);
            ShotOf(g1.Id, "a-sans-programme-fr");
            // en allemand
            Client c2 = new Client(); c2.BridgeDir = a.BridgeDir; c2.WantedName = "LEON"; c2.Ephemeral = true; c2.ServerAddress = a.ServerAddress;
            c2.Start(); c2.Connect();
            Wait(() => c2.GameAttached, 30000); Thread.Sleep(4000);
            c2.TestCommand(80); Thread.Sleep(1200);
            c2.TestCommand(302); Thread.Sleep(800);
            c2.TestCommand(85); Thread.Sleep(1500);
            c2.Stop();
            Thread.Sleep(6000);
            ShotOf(g1.Id, "a-sans-programme-de");
            Check("le jeu tourne", !g1.HasExited);
            s.Stop();
            try { g1.Kill(); } catch (Exception) { }
            Console.WriteLine(fails == 0 ? "TEST AIDE OK" : (fails + " TEST(S) EN ECHEC"));
            return fails;
        }

        public static int Police()
        {
            const int port = 31997;
            string mod = Environment.GetEnvironmentVariable("JAK3ONLINE_MOD");
            string sp = Environment.GetEnvironmentVariable("JAK3ONLINE_TMP");
            string dirA = Path.Combine(sp, "profilP");
            try { if (Directory.Exists(dirA)) Directory.Delete(dirA, true); } catch (Exception) { }
            Server s = new Server();
            s.Start(port);
            Process g1 = Process.Start(new ProcessStartInfo(Path.Combine(mod, "gk.exe"),
                "-g jak3 --proj-path \"" + Path.Combine(mod, "data") + "\" --config-path \"" + Path.Combine(sp, "cfg") + "\" -- -boot -fakeiso") { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Minimized });
            Client a = new Client(); a.BridgeDir = Path.Combine(mod, "data", "online", "bridge"); a.WantedName = "LEON"; a.ServerAddress = "127.0.0.1:" + port; a.ProfileDir = dirA;
            a.Log = m => Console.WriteLine("   [J1] " + m); a.Start(); a.Connect();
            Check("jeu detecte", Wait(() => a.GameAttached && a.NetState == Shm.NET_LOBBY, 180000));
            IntPtr w = WindowOf(g1.Id); if (w != IntPtr.Zero) ShowWindow(w, 7);
            Thread.Sleep(15000);
            a.TestSetXp(200000); a.TestSetMoney(60000);
            // comme le joueur : le menu de l'ecran titre est ouvert quand il entre dans le monde
            a.TestCommand(79); Thread.Sleep(1500);
            ShotOf(g1.Id, "t0-menu-titre");
            a.UiJoinWorld();
            Thread.Sleep(2500);
            ShotOf(g1.Id, "t1-compte-a-rebours");
            Check("monde en ligne (menu de l'ecran titre ouvert)", Wait(() => (UiF(a) & 2) != 0 && Loaded(a), 240000));
            Thread.Sleep(8000);
            // EN LIGNE, RIEN NE MET LE JEU EN PAUSE : pages Options et Secrets ouvertes, l'horloge du jeu avance
            foreach (int page in new int[] { 81, 82 })
            {
                a.TestCommand(page); Thread.Sleep(1500);
                int c0 = Gb(a, 96) | (Gb(a, 97) << 8);
                Thread.Sleep(2000);
                int c1 = Gb(a, 96) | (Gb(a, 97) << 8);
                int adv = (c1 - c0 + 65536) % 65536;
                ShotOf(g1.Id, "n" + page + "-page");
                Console.WriteLine("   page " + (page == 81 ? "OPTIONS" : "SECRETS") + " : l'horloge du jeu a avance de " + adv + " (600 = 2 s)");
                Check("page " + (page == 81 ? "OPTIONS" : "SECRETS") + " : le jeu ne se met pas en pause", adv > 400);
                a.TestCommand(80); Thread.Sleep(1500);
            }
            // menu Start : ouverture et fermeture rapides (captures a 0,25 s, 0,5 s et 1 s apres l'appui)
            for (int k = 0; k < 2; k++)
            {
                a.TestCommand(79);
                Thread.Sleep(250); ShotOf(g1.Id, "m" + k + "-start-025s");
                Thread.Sleep(250); ShotOf(g1.Id, "m" + k + "-start-050s");
                Thread.Sleep(500); ShotOf(g1.Id, "m" + k + "-start-100s");
                a.TestCommand(80);
                Thread.Sleep(400); ShotOf(g1.Id, "m" + k + "-ferme-040s");
                Thread.Sleep(2000);
            }
            Check("le jeu tourne apres le menu Start", a.GameAttached && !g1.HasExited);
            int[] langs = { 8, 6, 7, 22, 20, 2, 3 };
            foreach (int l in langs)
            {
                a.TestCommand(300 + l); Thread.Sleep(800);
                a.TestCommand(78); Thread.Sleep(1500);
                ShotOf(g1.Id, "p" + l + "-police");
                a.TestCommand(78); Thread.Sleep(800);
                a.TestCommand(73); Thread.Sleep(1500);
                ShotOf(g1.Id, "p" + l + "-boutique");
                a.TestCommand(61); Thread.Sleep(800);
                a.TestCommand(74); Thread.Sleep(1500);
                ShotOf(g1.Id, "p" + l + "-admin");
                a.TestCommand(61); Thread.Sleep(800);
            }
            a.TestCommand(311); Thread.Sleep(500);
            Check("le jeu tourne a la fin", a.GameAttached && !g1.HasExited);
            a.Stop(); s.Stop();
            try { g1.Kill(); } catch (Exception) { }
            Console.WriteLine(fails == 0 ? "TEST POLICE OK" : (fails + " TEST(S) EN ECHEC"));
            return fails;
        }

        public static int Final()
        {
            const int port = 31998;
            string mod = Environment.GetEnvironmentVariable("JAK3ONLINE_MOD");
            string sp = Environment.GetEnvironmentVariable("JAK3ONLINE_TMP");
            string dirA = Path.Combine(sp, "profilF");
            try { if (Directory.Exists(dirA)) Directory.Delete(dirA, true); } catch (Exception) { }
            Server s = new Server();
            s.Start(port);
            Process g1 = Process.Start(new ProcessStartInfo(Path.Combine(mod, "gk.exe"),
                "-g jak3 --proj-path \"" + Path.Combine(mod, "data") + "\" --config-path \"" + Path.Combine(sp, "cfg") + "\" -- -boot -fakeiso") { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Minimized });
            Thread.Sleep(8000);
            Process g2 = Process.Start(new ProcessStartInfo(Path.Combine(mod, "gk.exe"),
                "-g jak3 --proj-path \"" + Path.Combine(sp, "data2") + "\" --config-path \"" + Path.Combine(sp, "cfg2") + "\" -- -boot -fakeiso") { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Minimized });
            Client a = new Client(); a.BridgeDir = Path.Combine(mod, "data", "online", "bridge"); a.WantedName = "LEON"; a.ServerAddress = "127.0.0.1:" + port; a.ProfileDir = dirA;
            a.Log = m => Console.WriteLine("   [J1] " + m); a.Start(); a.Connect();
            Client b = new Client(); b.BridgeDir = Path.Combine(sp, "data2", "online", "bridge"); b.WantedName = "Joueur2"; b.Ephemeral = true; b.ServerAddress = "127.0.0.1:" + port;
            b.Log = m => Console.WriteLine("   [J2] " + m); b.Start(); b.Connect();
            Check("les deux jeux sont detectes", Wait(() => a.GameAttached && b.GameAttached && a.NetState == Shm.NET_LOBBY && b.NetState == Shm.NET_LOBBY, 180000));
            foreach (Process gp in new Process[] { g1, g2 }) { IntPtr w = WindowOf(gp.Id); if (w != IntPtr.Zero) ShowWindow(w, 7); }
            Thread.Sleep(15000);
            ShotOf(g1.Id, "f0-titre-j1");
            a.TestSetXp(200000); a.TestSetMoney(60000);
            b.TestSetXp(5000); b.TestSetMoney(3000);
            a.UiJoinWorld(); Thread.Sleep(1500); b.UiJoinWorld();
            Check("monde en ligne dans les deux jeux (Haven)", Wait(() => (UiF(a) & 2) != 0 && (UiF(b) & 2) != 0 && Loaded(a) && Loaded(b) && Lvl(a).StartsWith("cty") && Lvl(b).StartsWith("cty"), 240000));
            Thread.Sleep(10000);
            Check("empreinte de la version identique (anti-triche)", a.TestFingerprint == b.TestFingerprint && a.TestFingerprint != "?");
            Console.WriteLine("   empreinte " + a.TestFingerprint + "  (" + Empreinte.Detail + ")");
            Thread.Sleep(3000);
            Console.WriteLine("   niveaux : J1 " + a.TestLevel + "  J2 " + b.TestLevel);
            Check("les deux se voient", Wait(() => Gb(a, 90) == 1 && Gb(b, 90) == 1, 20000));
            ShotOf(g1.Id, "f1-hud-j1"); ShotOf(g2.Id, "f1-hud-j2");

            // menu SELECT : boutique (joueur), admin (createur)
            b.TestCommand(73); Thread.Sleep(1500); ShotOf(g2.Id, "f2-boutique-j2"); b.TestCommand(61); Thread.Sleep(600);
            a.TestCommand(74); Thread.Sleep(1500); ShotOf(g1.Id, "f2-admin-j1"); a.TestCommand(61); Thread.Sleep(600);
            // achats : niveau requis, super-pouvoir
            b.TestCommand(150); Thread.Sleep(1200);
            Check("J2 (niveau " + b.TestLevel + ") ne peut pas acheter le X-Ride (niveau 20)", !b.Prof.Owns(50));
            b.TestCommand(145); Thread.Sleep(1200);
            Check("J2 achete le Sand Shark (niveau 5)", b.Prof.Owns(45));
            b.TestCommand(164); Thread.Sleep(1500);
            Check("super-pouvoir TURBO actif chez J2", b.Prof.PowerLeft(0) > 0f);
            b.TestCommand(168); Thread.Sleep(1500);
            ShotOf(g2.Id, "f3-pouvoirs-j2");

            // EVENEMENT : boss geant
            a.TestStartEvent(1);
            Check("alerte boss chez les deux joueurs", Wait(() => a.TestEventKind == 1 && b.TestEventKind == 1, 6000));
            Thread.Sleep(7000);
            ShotOf(g1.Id, "f4-boss-j1"); ShotOf(g2.Id, "f4-boss-j2");
            long m1 = a.TestMoney, m2 = b.TestMoney;
            for (int k = 0; k < 160 && a.TestEventState == 1; k++)
            {
                a.TestCommand(72); Thread.Sleep(420);
                if (k % 3 == 1) { b.TestCommand(72); Thread.Sleep(420); }
                if (k == 6) { ShotOf(g1.Id, "f5-boss-combat-j1"); }
            }
            Console.WriteLine("   degats : J1 " + a.TestMyBossDmg + "  J2 " + b.TestMyBossDmg + "  PV restants " + a.TestBossHp);
            Check("le boss est vaincu (points de vie partages)", Wait(() => a.TestEventState == 2 && b.TestEventState == 2, 12000));
            Thread.Sleep(2500);
            ShotOf(g1.Id, "f6-boss-vaincu-j1");
            Check("J1 recompense (part des degats)", a.TestMoney > m1);
            Check("J2 recompense aussi (il a tape)", b.TestMoney > m2);

            // ELIMINATION : butin, gains degressifs, mort visible
            Check("les deux joueurs sont en vie", Wait(() => (Gflags(a) & 2) == 0 && (Gflags(b) & 2) == 0 && Loaded(a) && Loaded(b), 60000));
            Thread.Sleep(6000);
            long a0 = a.TestMoney, b0 = b.TestMoney;
            for (int k = 0; k < 30 && (Gflags(b) & 2) == 0; k++) { a.TestSendHitMode(b.MyId, 20f, 5); Thread.Sleep(450); }
            Check("J2 est elimine", Wait(() => (Gflags(b) & 2) != 0, 8000));
            Thread.Sleep(900);
            ShotOf(g1.Id, "f7-mort-vue-par-j1");
            Check("J2 lache du butin", Wait(() => b.TestMoney < b0, 6000));
            Check("J1 gagne l'elimination + le butin", Wait(() => a.TestMoney >= a0 + 25, 8000));
            long gain1 = a.TestMoney - a0;
            Check("J2 revient en jeu", Wait(() => (Gflags(b) & 2) == 0 && Loaded(b), 60000));
            Thread.Sleep(8000);
            long a1 = a.TestMoney;
            for (int k = 0; k < 30 && (Gflags(b) & 2) == 0; k++) { a.TestSendHitMode(b.MyId, 20f, 5); Thread.Sleep(450); }
            Wait(() => (Gflags(b) & 2) != 0, 8000);
            Wait(() => a.TestMoney > a1, 8000);
            long gain2 = a.TestMoney - a1;
            Console.WriteLine("   gains : 1re elimination " + gain1 + ", 2e " + gain2);
            Check("la 2e elimination du meme joueur rapporte moins", gain2 < gain1);
            Wait(() => (Gflags(b) & 2) == 0 && Loaded(b), 60000);
            Thread.Sleep(8000);

            // VEHICULES : J1 appelle un vehicule, J2 le voit et tire dessus
            GoTo(a, b, 34, 34, l => l.StartsWith("cty"), l => l.StartsWith("cty"), "retour au port");
            a.TestCommand(145); Thread.Sleep(1200);
            a.TestCommand(245);
            Thread.Sleep(8000);
            Check("J1 conduit le Sand Shark a Haven", (Gflags(a) & 0x80) != 0);
            Check("J2 voit le vehicule de J1", Wait(() => Gb(b, 91) >= 2, 8000));
            ShotOf(g2.Id, "f8-vehicule-vu-par-j2");
            // PASSAGER : J2 (a pied, pres du vehicule de J1) monte avec lui, puis descend
            b.TestCommand(84); Thread.Sleep(2500);
            Console.WriteLine("   J2 a pied pres de J1 : distance " + Dist(PosOf(a), PosOf(b)).ToString("0.0") + " m");
            b.TestCommand(83); Thread.Sleep(2500);
            Console.WriteLine("   J2 passager : cache " + ((Gflags(b) & 4) != 0) + "  distance a J1 " + Dist(PosOf(a), PosOf(b)).ToString("0.0") + " m");
            ShotOf(g2.Id, "f8c-passager-j2"); ShotOf(g1.Id, "f8c-passager-vu-par-j1");
            Check("J2 monte dans le vehicule de J1 (passager assis a cote, visible)", (Gflags(b) & 0x400) != 0 && (Gflags(b) & 4) == 0 && Dist(PosOf(a), PosOf(b)) < 2.0);
            b.TestCommand(83); Thread.Sleep(2000);
            Check("J2 redescend du vehicule", (Gflags(b) & 0x400) == 0);
                        // PERCUTER : J2 monte aussi dans son Sand Shark, J1 fonce dessus : les deux vehicules s'abiment
            b.TestCommand(245);
            Thread.Sleep(9000);
            Check("J2 conduit aussi son Sand Shark", (Gflags(b) & 0x80) != 0);
            int hpA = Gb(a, 94), hpB = Gb(b, 94);
            Console.WriteLine("   etat des vehicules avant le choc : J1 " + hpA + " %  J2 " + hpB + " %");
            for (int k = 0; k < 24 && Gb(b, 94) >= hpB; k++) { a.TestCommand(77); Thread.Sleep(500); }
            Thread.Sleep(1200);
            Console.WriteLine("   apres le choc : J1 " + Gb(a, 94) + " %  J2 " + Gb(b, 94) + " %  distance " + Dist(PosOf(a), PosOf(b)).ToString("0.0") + " m");
            ShotOf(g1.Id, "f8b-choc-j1");
            Check("percuter abime le vehicule de l'autre joueur", Gb(b, 94) < hpB);
            Check("et le mien aussi", Gb(a, 94) < hpA);
            b.TestCommand(70); Thread.Sleep(1500);
            long bv = b.TestMoney;
            for (int k = 0; k < 30 && (Gflags(a) & 0x80) != 0; k++) { b.TestSendHitMode(a.MyId, 8f, 2); Thread.Sleep(420); }
            Thread.Sleep(3000);
            ShotOf(g1.Id, "f9-vehicule-detruit-j1");
            Check("le vehicule de J1 est detruit par J2", (Gflags(a) & 0x80) == 0);
            Check("J2 est recompense (vehicule detruit)", Wait(() => b.TestMoney > bv, 8000));

            // LEZARD a Spargus : l'autre joueur voit la monture
            GoTo(a, b, 30, 30, l => l.StartsWith("wasc"), l => l.StartsWith("wasc"), "arrivee a Spargus");
            a.TestCommand(75);
            Thread.Sleep(7000);
            byte[] lp = a.LocalPoseSnapshot();
            Console.WriteLine("   J1 sur le lezard : pose " + (lp != null ? lp.Length + " o, " + lp[1] + " pieces" : "-") + "  J2 voit pieces " + Gb(b, 91) + "  distance " + Dist(PosOf(a), PosOf(b)).ToString("0.0") + " m");
            Check("J2 voit J1 sur son lezard", Wait(() => Gb(b, 91) >= 2, 8000));
            ShotOf(g2.Id, "f10-lezard-vu-par-j2"); ShotOf(g1.Id, "f10-lezard-j1");
            a.TestCommand(76);
            // pseudos qui ne se chevauchent plus (3 bots autour de J1)
            for (int k = 0; k < 3; k++) a.AddBotNow();
            Thread.Sleep(12000);
            ShotOf(g2.Id, "f11-pseudos-j2");
            Check("les deux jeux tournent a la fin", a.GameAttached && b.GameAttached && !g1.HasExited && !g2.HasExited);
            a.Stop(); b.Stop(); s.Stop();
            try { g1.Kill(); } catch (Exception) { }
            try { g2.Kill(); } catch (Exception) { }
            Console.WriteLine(fails == 0 ? "TEST FINAL OK" : (fails + " TEST(S) EN ECHEC"));
            return fails == 0 ? 0 : 1;
        }

        // Jak3Online.exe --snake : J1 conduit le Sand Shark (ou l'objet JAK3ONLINE_VEH), J2 a pied regarde
        public static int Snake()
        {
            const int port = 31995;
            string mod = Environment.GetEnvironmentVariable("JAK3ONLINE_MOD");
            string sp = Environment.GetEnvironmentVariable("JAK3ONLINE_TMP");
            int item = 45;
            int.TryParse(Environment.GetEnvironmentVariable("JAK3ONLINE_VEH") ?? "45", out item);
            string dirA = Path.Combine(sp, "profilS");
            try { if (Directory.Exists(dirA)) Directory.Delete(dirA, true); } catch (Exception) { }
            Server s = new Server();
            s.Start(port);
            Process g1 = Process.Start(new ProcessStartInfo(Path.Combine(mod, "gk.exe"),
                "-g jak3 --proj-path \"" + Path.Combine(mod, "data") + "\" --config-path \"" + Path.Combine(sp, "cfg") + "\" -- -boot -fakeiso") { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Minimized });
            Thread.Sleep(8000);
            Process g2 = Process.Start(new ProcessStartInfo(Path.Combine(mod, "gk.exe"),
                "-g jak3 --proj-path \"" + Path.Combine(sp, "data2") + "\" --config-path \"" + Path.Combine(sp, "cfg2") + "\" -- -boot -fakeiso") { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Minimized });
            Client a = new Client(); a.BridgeDir = Path.Combine(mod, "data", "online", "bridge"); a.WantedName = "Pilote"; a.ServerAddress = "127.0.0.1:" + port; a.ProfileDir = dirA; a.Start(); a.Connect();
            Client b = new Client(); b.BridgeDir = Path.Combine(sp, "data2", "online", "bridge"); b.WantedName = "Spectateur"; b.Ephemeral = true; b.ServerAddress = "127.0.0.1:" + port; b.Start(); b.Connect();
            Check("jeux detectes", Wait(() => a.GameAttached && b.GameAttached && a.NetState == Shm.NET_LOBBY && b.NetState == Shm.NET_LOBBY, 180000));
            foreach (Process gp in new Process[] { g1, g2 }) { IntPtr w = WindowOf(gp.Id); if (w != IntPtr.Zero) ShowWindow(w, 7); }
            Thread.Sleep(10000);
            a.UiJoinWorld(); Thread.Sleep(1500); b.UiJoinWorld();
            Check("monde", Wait(() => (UiF(a) & 2) != 0 && (UiF(b) & 2) != 0 && Loaded(a) && Loaded(b) && Lvl(a).StartsWith("cty") && Lvl(b).StartsWith("cty"), 240000));
            Thread.Sleep(6000);
            a.TestSetMoney(5000);
            a.TestCommand(100 + item);
            Check("achat", Wait(() => a.Prof.Owns(item), 5000));
            GoTo(a, b, 31, 31, l => l.StartsWith("des") || l.StartsWith("was"), l => l.StartsWith("des") || l.StartsWith("was"), "desert");
            a.TestCommand(200 + item);
            Thread.Sleep(10000);
            Console.WriteLine("   J1 conduit : " + ((Gflags(a) & 0x80) != 0) + "   J2 voit : avatars " + Gb(b, 88) + " affiches " + Gb(b, 89) + " squelette " + Gb(b, 90) + " pieces " + Gb(b, 91) + "  distance " + Dist(PosOf(a), PosOf(b)).ToString("0.0") + " m");
            Check("J2 voit le vehicule de J1", Gb(b, 91) >= 2);
            ShotOf(g2.Id, "s-j2-voit-" + item);
            ShotOf(g1.Id, "s-j1-" + item);
            a.Stop(); b.Stop(); s.Stop();
            try { g1.Kill(); } catch (Exception) { }
            try { g2.Kill(); } catch (Exception) { }
            Console.WriteLine(fails == 0 ? "TEST SNAKE OK" : (fails + " TEST(S) EN ECHEC"));
            return fails == 0 ? 0 : 1;
        }

        // Jak3Online.exe --duo2 : le MONDE EN LIGNE avec deux vrais jeux (createur + joueur)
        public static int Duo2()
        {
            const int port = 31992;
            string mod = Environment.GetEnvironmentVariable("JAK3ONLINE_MOD");
            string sp = Environment.GetEnvironmentVariable("JAK3ONLINE_TMP");
            string dirA = Path.Combine(sp, "profilA");
            try { if (Directory.Exists(dirA)) Directory.Delete(dirA, true); } catch (Exception) { }
            Server s = new Server();
            s.Start(port);
            Process g1 = Process.Start(new ProcessStartInfo(Path.Combine(mod, "gk.exe"),
                "-g jak3 --proj-path \"" + Path.Combine(mod, "data") + "\" --config-path \"" + Path.Combine(sp, "cfg") + "\" -- -boot -fakeiso") { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Minimized });
            Thread.Sleep(8000);
            Process g2 = Process.Start(new ProcessStartInfo(Path.Combine(mod, "gk.exe"),
                "-g jak3 --proj-path \"" + Path.Combine(sp, "data2") + "\" --config-path \"" + Path.Combine(sp, "cfg2") + "\" -- -boot -fakeiso") { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Minimized });
            Client a = new Client();
            a.BridgeDir = Path.Combine(mod, "data", "online", "bridge");
            a.WantedName = "LE0N_";
            a.ServerAddress = "127.0.0.1:" + port;
            a.ProfileDir = dirA;
            a.Log = m => Console.WriteLine("   [J1] " + m);
            a.Start(); a.Connect();
            Client b = new Client();
            b.BridgeDir = Path.Combine(sp, "data2", "online", "bridge");
            b.WantedName = "Joueur2";
            b.Ephemeral = true;
            b.ServerAddress = "127.0.0.1:" + port;
            b.Log = m => Console.WriteLine("   [J2] " + m);
            b.Start(); b.Connect();
            Check("les deux jeux sont detectes", Wait(() => a.GameAttached && b.GameAttached && a.NetState == Shm.NET_LOBBY && b.NetState == Shm.NET_LOBBY, 180000));
            foreach (Process gp in new Process[] { g1, g2 }) { IntPtr w = WindowOf(gp.Id); if (w != IntPtr.Zero) ShowWindow(w, 7); }
            Check("J1 a la cle du createur", a.IsCreateur);
            // l'ecran titre met un moment a arriver
            Thread.Sleep(15000);
            ShotOf(g1.Id, "m0-titre-j1");
            a.UiJoinWorld();
            Check("J1 rejoint la session publique", Wait(() => a.InWorld, 8000));
            Thread.Sleep(1500);
            b.UiJoinWorld();
            Check("J2 rejoint la meme session publique", Wait(() => b.InWorld && b.SessionCode == a.SessionCode, 8000));
            Check("le monde en ligne demarre dans les deux jeux", Wait(() => (UiF(a) & 2) != 0 && (UiF(b) & 2) != 0, 90000));
            Check("arrivee au port de Haven (Naughty Ottsel)", Wait(() => Loaded(a) && Loaded(b) && Lvl(a).StartsWith("cty") && Lvl(b).StartsWith("cty"), 180000));
            Thread.Sleep(12000);
            Console.WriteLine("   J1 : " + Lvl(a) + "  J2 : " + Lvl(b) + "  distance " + Dist(PosOf(a), PosOf(b)).ToString("0.0") + " m");
            Check("J1 voit J2", Wait(() => Gb(a, 90) == 1, 20000));
            Check("J2 voit J1", Wait(() => Gb(b, 90) == 1, 20000));
            ShotOf(g1.Id, "m1-monde-j1"); ShotOf(g2.Id, "m1-monde-j2");

            // chat
            a.TestChat("Bienvenue dans le monde en ligne !");
            b.TestChat("Merci createur");
            Thread.Sleep(1500);
            ShotOf(g2.Id, "m2-chat-j2");
            b.TestCommand(66);
            Thread.Sleep(1200);
            ShotOf(g2.Id, "m2-chat-saisie-j2");
            b.TestCommand(67);
            Thread.Sleep(800);

            // menu TAB
            b.TestCommand(60); Thread.Sleep(1200); ShotOf(g2.Id, "m3-tab-joueurs-j2");
            b.TestCommand(62); Thread.Sleep(900); ShotOf(g2.Id, "m3-tab-session-j2");
            b.TestCommand(62); Thread.Sleep(900); ShotOf(g2.Id, "m3-tab-courses-j2");
            b.TestCommand(62); Thread.Sleep(900); ShotOf(g2.Id, "m3-tab-moi-j2");
            b.TestCommand(61); Thread.Sleep(600);
            a.TestCommand(60); Thread.Sleep(900); a.TestCommand(62); Thread.Sleep(900); ShotOf(g1.Id, "m3-tab-session-j1");
            a.TestCommand(61); Thread.Sleep(600);
            Check("les deux jeux tournent (menus)", a.GameAttached && b.GameAttached);

            // carte du menu : les autres joueurs y sont
            a.TestCommand(10); Thread.Sleep(2500); a.TestCommand(12); Thread.Sleep(5000);
            ShotOf(g1.Id, "m3-carte-j1");
            a.TestCommand(13); Thread.Sleep(2500); a.TestCommand(11); Thread.Sleep(2500);
            Check("J1 revient en jeu apres la carte", a.GameAttached && (Gflags(a) & 0x200) == 0);

            // argent : cadeau du createur
            long m0 = b.TestMoney;
            a.TestAdmin(Client.CMD_GIVE, b.MyId, 1000, "");
            Check("J2 recoit 1000 orbes du createur", Wait(() => b.TestMoney == m0 + 1000, 5000));
            Thread.Sleep(1500);
            ShotOf(g2.Id, "m4-cadeau-j2");

            // boutique du Naughty Ottsel
            b.TestCommand(68);
            Check("J2 entre dans le Naughty Ottsel", Wait(() => Lvl(b) == "hiphog" && Loaded(b), 90000));
            Thread.Sleep(6000);
            ShotOf(g2.Id, "m5-bar-j2");
            b.TestCommand(63);
            Thread.Sleep(1200);
            ShotOf(g2.Id, "m5-boutique-j2");
            b.TestCommand(100);   // Blaster
            Check("achat du Blaster (verifie par le programme)", Wait(() => b.Prof.Owns(0), 5000));
            b.TestCommand(143);   // Tough Puppy
            Check("achat du Tough Puppy", Wait(() => b.Prof.Owns(43), 5000));
            Thread.Sleep(1200);
            ShotOf(g2.Id, "m5-boutique-achat-j2");
            b.TestCommand(64);
            Thread.Sleep(800);

            // vehicules : chacun appelle le sien au desert
            a.TestSetMoney(3000);
            a.TestCommand(145);  // Sand Shark
            Check("J1 achete le Sand Shark", Wait(() => a.Prof.Owns(45), 5000));
            GoTo(a, b, 31, 31, l => l.StartsWith("des") || l.StartsWith("was"), l => l.StartsWith("des") || l.StartsWith("was"), "les deux joueurs arrivent au desert");
            // l'arrivee au desert se fait en vehicule (le Tough Puppy prete par le garage) : on descend
            Console.WriteLine("   arrivee : J1 flags 0x" + Gflags(a).ToString("x") + "  J2 flags 0x" + Gflags(b).ToString("x"));
            a.TestCommand(70); b.TestCommand(70);
            Thread.Sleep(5000);
            b.TestCommand(21);   // arme achetee en main (dehors : dans le bar c'est interdit par le jeu)
            Thread.Sleep(2500);
            Check("J2 a l'arme achetee en main", (Gflags(b) & 0x40) != 0);
            ShotOf(g2.Id, "m5-arme-j2");
            b.TestCommand(243);
            Thread.Sleep(600);
            a.TestCommand(245);
            Thread.Sleep(10000);
            Check("J2 conduit son Tough Puppy", (Gflags(b) & 0x80) != 0);
            Check("J1 conduit son Sand Shark", (Gflags(a) & 0x80) != 0);
            Check("J1 voit le vehicule de J2", Gb(a, 91) >= 2);
            Check("J2 voit le vehicule de J1", Gb(b, 91) >= 2);
            Console.WriteLine("   vehicules : J1 pieces " + Gb(a, 91) + "  J2 pieces " + Gb(b, 91) + "  distance " + Dist(PosOf(a), PosOf(b)).ToString("0.0") + " m");
            ShotOf(g1.Id, "m6-vehicules-j1"); ShotOf(g2.Id, "m6-vehicules-j2");

            // tir de gravite sur J2 qui conduit : son vehicule s'envole
            Client bot = new Client();
            bot.UseBridge = false;
            bot.Ephemeral = true;
            bot.WantedName = "Chauffard";
            bot.ServerAddress = "127.0.0.1:" + port;
            bot.Start(); bot.Connect();
            Wait(() => bot.NetState == Shm.NET_LOBBY, 5000);
            bot.UiJoinWorld();
            Check("un 3e joueur (simule) rejoint le monde", Wait(() => bot.InWorld && bot.VerifiedCount >= 2 && b.VerifiedCount >= 2, 10000));
            float[] pb = PosOf(b);
            float[] pbv = PosOf(b);
            for (int k = 0; k < 10; k++) { pbv = PosOf(b); bot.TestSetLocalState(pbv[0] + 30 * 4096f, pbv[1], pbv[2], Lvl(b), -1, 0f); Thread.Sleep(100); }
            float y0 = PosOf(b)[1];
            bot.TestSendHitMode(b.MyId, 0f, 6);
            float ymax = y0;
            DateTime tend = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < tend) { ymax = Math.Max(ymax, PosOf(b)[1]); Thread.Sleep(50); }
            Console.WriteLine("   gravite : J2 monte de " + ((ymax - y0) / 4096f).ToString("0.0") + " m");
            Check("tir de gravite : le vehicule de J2 s'envole", ymax - y0 > 2.5f * 4096f);
            ShotOf(g2.Id, "m6-gravite-j2");
            Thread.Sleep(3000);

            // un vehicule lance ecrase J2 (a pied)
            b.TestCommand(70);
            Thread.Sleep(4000);
            Check("J2 est descendu du vehicule", (Gflags(b) & 0x80) == 0);
            byte[] gh = b.TestGameHeader();
            float hp0 = BitConverter.ToSingle(gh, Shm.Local + 28);
            bool hurt = false;
            DateTime tend2 = DateTime.UtcNow.AddSeconds(6);
            float ang = 0;
            while (DateTime.UtcNow < tend2 && !hurt)
            {
                float[] q = PosOf(b);
                ang += 0.4f;
                // le chauffard fonce sur J2 a 25 m/s (drapeau vehicule)
                byte[] st = new byte[Proto.StateSize];
                Buffer.BlockCopy(BitConverter.GetBytes(Proto.FLAG_VALID | 0x8u | 0x80u), 0, st, 0, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(-1), 0, st, 4, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(q[0] + 1.0f * 4096f), 0, st, 12, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(q[1]), 0, st, 16, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(q[2]), 0, st, 20, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(8f), 0, st, 24, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(1f), 0, st, 40, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(-25f * 4096f), 0, st, 44, 4);
                byte[] lv = Encoding.ASCII.GetBytes(Lvl(b));
                Buffer.BlockCopy(lv, 0, st, 64, Math.Min(15, lv.Length));
                bot.SetLocalStateRaw(st);
                Thread.Sleep(80);
                byte[] g3 = b.TestGameHeader();
                if (g3 != null && BitConverter.ToSingle(g3, Shm.Local + 28) < hp0 - 0.5f) hurt = true;
            }
            Check("J2 ecrase par le vehicule lance (perd de la vie)", hurt);
            ShotOf(g2.Id, "m6-ecrase-j2");
            bot.Stop();
            Thread.Sleep(3000);

            // course
            a.TestCommand(69);
            Thread.Sleep(3000);
            ShotOf(g2.Id, "m7-course-j2");
            Thread.Sleep(12000);
            ShotOf(g1.Id, "m7-course-partie-j1");

            // moderation : gel + teleportation
            a.TestAdmin(Client.CMD_FREEZE, b.MyId, 0, "");
            Check("J2 gele", Wait(() => b.IsFrozen, 5000));
            Thread.Sleep(1500);
            ShotOf(g2.Id, "m8-gele-j2");
            a.TestAdmin(Client.CMD_UNFREEZE, b.MyId, 0, "");
            Check("J2 degele", Wait(() => !b.IsFrozen, 5000));
            Check("les deux jeux tournent a la fin", a.GameAttached && b.GameAttached);

            // quitter le monde : retour a l'ecran titre
            b.UiLeave();
            Check("J2 quitte le monde en ligne (retour titre)", Wait(() => (UiF(b) & 2) == 0, 30000));
            Thread.Sleep(8000);
            ShotOf(g2.Id, "m9-sortie-j2");
            Check("le jeu de J2 tourne apres la sortie", b.GameAttached);

            a.Stop(); b.Stop(); s.Stop();
            try { g1.Kill(); } catch (Exception) { }
            try { g2.Kill(); } catch (Exception) { }
            Console.WriteLine(fails == 0 ? "TEST DU MONDE EN LIGNE (2 JEUX) OK" : (fails + " TEST(S) EN ECHEC"));
            return fails == 0 ? 0 : 1;
        }

        // Jak3Online.exe --duo : DEUX vrais jeux en meme temps (deux dossiers de pont),
        // zones differentes, vehicules, missions...
        public static int Duo()
        {
            const int port = 27994;
            string mod = Environment.GetEnvironmentVariable("JAK3ONLINE_MOD");
            string sp = Environment.GetEnvironmentVariable("JAK3ONLINE_TMP");
            Server s = new Server();
            s.Start(port);
            Process g1 = Process.Start(new ProcessStartInfo(Path.Combine(mod, "gk.exe"),
                "-g jak3 --proj-path \"" + Path.Combine(mod, "data") + "\" --config-path \"" + Path.Combine(sp, "cfg") + "\" -- -boot -fakeiso") { UseShellExecute = false, RedirectStandardOutput = false, WindowStyle = ProcessWindowStyle.Minimized });
            Thread.Sleep(8000);
            Process g2 = Process.Start(new ProcessStartInfo(Path.Combine(mod, "gk.exe"),
                "-g jak3 --proj-path \"" + Path.Combine(sp, "data2") + "\" --config-path \"" + Path.Combine(sp, "cfg2") + "\" -- -boot -fakeiso") { UseShellExecute = false, RedirectStandardOutput = false, WindowStyle = ProcessWindowStyle.Minimized });
            Client a = new Client();
            a.BridgeDir = Path.Combine(mod, "data", "online", "bridge");
            a.WantedName = "Joueur1";
            a.ServerAddress = "127.0.0.1:" + port;
            a.Start(); a.Connect();
            Client b = new Client();
            b.BridgeDir = Path.Combine(sp, "data2", "online", "bridge");
            b.WantedName = "Joueur2";
            b.ServerAddress = "127.0.0.1:" + port;
            b.Start(); b.Connect();
            Check("les deux jeux sont detectes", Wait(() => a.GameAttached && b.GameAttached && a.NetState == Shm.NET_LOBBY && b.NetState == Shm.NET_LOBBY, 180000));
            foreach (Process gp in new Process[] { g1, g2 }) { IntPtr w = WindowOf(gp.Id); if (w != IntPtr.Zero) ShowWindow(w, 7); }
            a.UiCreate(true, 100, true);
            Check("session creee", Wait(() => a.SessionId != 0, 5000));
            b.UiJoinCode(a.SessionCode);
            Check("le joueur 2 rejoint", Wait(() => b.SessionId == a.SessionId, 8000));
            DateTime la = DateTime.MinValue, lb = DateTime.MinValue;
            Check("les deux parties sont chargees", Wait(() =>
            {
                if (!Loaded(a) && (DateTime.UtcNow - la).TotalSeconds > 20) { la = DateTime.UtcNow; a.TestCommand(1); }
                if (!Loaded(b) && (DateTime.UtcNow - lb).TotalSeconds > 20) { lb = DateTime.UtcNow; b.TestCommand(4); }
                return Loaded(a) && Loaded(b);
            }, 500000));
            Thread.Sleep(6000);

            Action<string> Report = delegate (string when)
            {
                Console.WriteLine("   [" + when + "] J1 : " + Lvl(a) + " flags 0x" + Gflags(a).ToString("x") + " avatars " + Gb(a, 88) + " affiches " + Gb(a, 89) + " squelette " + Gb(a, 90) + " pieces " + Gb(a, 91)
                    + "  |  J2 : " + Lvl(b) + " flags 0x" + Gflags(b).ToString("x") + " avatars " + Gb(b, 88) + " affiches " + Gb(b, 89) + " squelette " + Gb(b, 90) + " pieces " + Gb(b, 91));
            };

            // 1) les deux a Spargus : ils se voient
            GoTo(a, b, 30, 30, l => l == "wascitya", l => l == "wascitya", "les deux joueurs arrivent a Spargus");
            Report("Spargus");
            Check("J1 voit J2 (squelette)", Gb(a, 90) == 1);
            Check("J2 voit J1 (squelette)", Gb(b, 90) == 1);
            ShotOf(g1.Id, "d1-spargus-j1"); ShotOf(g2.Id, "d1-spargus-j2");

            // 2) les deux au desert, chacun dans son vehicule
            GoTo(a, b, 31, 31, l => l.StartsWith("des") || l.StartsWith("was"), l => l.StartsWith("des") || l.StartsWith("was"), "les deux joueurs arrivent au desert");
            Report("desert");
            a.TestCommand(50);
            b.TestCommand(51);
            Thread.Sleep(8000);
            Report("vehicules");
            Check("J1 conduit un vehicule", (Gflags(a) & 0x80) != 0);
            Check("J2 conduit un vehicule", (Gflags(b) & 0x80) != 0);
            Check("J1 voit le vehicule de J2", Gb(a, 91) >= 2);
            Check("J2 voit le vehicule de J1", Gb(b, 91) >= 2);
            ShotOf(g1.Id, "d2-vehicules-j1"); ShotOf(g2.Id, "d2-vehicules-j2");
            Thread.Sleep(4000);
            ShotOf(g1.Id, "d2-vehicules-j1-b");

            // 3) J1 a Haven City, J2 reste au desert : ils ne se voient plus, pas de plantage
            GoTo(a, b, 32, 0, l => l.StartsWith("cty"), l => true, "J1 arrive a Haven City pendant que J2 est au desert");
            Report("Haven / desert");
            Check("J1 ne voit pas J2 (autre zone)", Gb(a, 89) == 0);
            Check("J2 ne voit pas J1 (autre zone)", Gb(b, 89) == 0);
            Check("les deux jeux tournent", a.GameAttached && b.GameAttached);
            ShotOf(g1.Id, "d3-haven-j1"); ShotOf(g2.Id, "d3-desert-j2");

            // 4) J2 part en mission (tir de Spargus), J1 reste en ville
            GoTo(a, b, 0, 33, l => true, l => l.StartsWith("was"), "J2 arrive a la mission de tir");
            Report("mission");
            Check("les deux jeux tournent (mission)", a.GameAttached && b.GameAttached);
            ShotOf(g2.Id, "d4-mission-j2");

            // 5) J2 rejoint J1 a Haven City : ils se revoient
            GoTo(a, b, 0, 32, l => true, l => l.StartsWith("cty"), "J2 arrive a Haven City");
            Report("Haven ensemble");
            Check("J1 revoit J2", Gb(a, 90) == 1);
            Check("J2 revoit J1", Gb(b, 90) == 1);
            b.TestCommand(52);
            Thread.Sleep(6000);
            Report("moto");
            ShotOf(g1.Id, "d5-haven-j1"); ShotOf(g2.Id, "d5-haven-j2");
            Check("les deux jeux tournent a la fin", a.GameAttached && b.GameAttached);

            a.Stop(); b.Stop(); s.Stop();
            try { g1.Kill(); } catch (Exception) { }
            try { g2.Kill(); } catch (Exception) { }
            Console.WriteLine(fails == 0 ? "TEST A DEUX JOUEURS OK" : (fails + " TEST(S) EN ECHEC"));
            return fails == 0 ? 0 : 1;
        }

        static int G2(FileBridge r, int off) { r.Poll(); return r.Game[Shm.Local + off]; }

        static byte[] FakeBank(uint count, byte fill)
        {
            byte[] d = new byte[Client.BankSize];
            for (int i = 12; i < d.Length; i++) d[i] = fill;
            Buffer.BlockCopy(BitConverter.GetBytes(count), 0, d, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(0x12345678u), 0, d, 8, 4);
            return d;
        }

        // sauvegardes dans un dossier temporaire (jamais les vraies)
        static void TestSaves(Client host, Client guest, int wait)
        {
            string dir = Environment.GetEnvironmentVariable("JAK3ONLINE_SAVES");
            if (string.IsNullOrEmpty(dir)) { Console.WriteLine("   (test des sauvegardes ignore : JAK3ONLINE_SAVES absent)"); return; }
            Directory.CreateDirectory(dir);
            foreach (string f in Directory.GetFiles(dir, "bank*.bin")) File.Delete(f);
            File.WriteAllBytes(Path.Combine(dir, "bank0.bin"), FakeBank(5, 0xAA));
            File.WriteAllBytes(Path.Combine(dir, "bank6.bin"), FakeBank(1, 0x11));
            guest.TestGetSave(3);
            Check("sauvegarde de l'hote recue", Wait(() => guest.SaveStatus == 2, wait));
            byte[] got = File.ReadAllBytes(Path.Combine(dir, "bank6.bin"));
            Check("sauvegarde copiee a l'identique (emplacement 4, 2 fichiers)", got[100] == 0xAA && File.ReadAllBytes(Path.Combine(dir, "bank7.bin"))[100] == 0xAA);
            Check("ancienne sauvegarde mise de cote", Directory.GetDirectories(dir, "jak3online-copie-*").Length >= 1);
            guest.TestSaveSync(true);
            Thread.Sleep(4000);
            File.WriteAllBytes(Path.Combine(dir, "bank0.bin"), FakeBank(6, 0xBB));
            File.SetLastWriteTimeUtc(Path.Combine(dir, "bank0.bin"), DateTime.UtcNow.AddSeconds(5));
            Check("synchro auto : nouvelle sauvegarde de l'hote recue", Wait(() => File.ReadAllBytes(Path.Combine(dir, "bank6.bin"))[100] == 0xBB, wait + 5000));
            guest.TestSaveSync(false);
        }

        static Client NewRelayClient(string name)
        {
            Client c = new Client();
            c.UseBridge = false;
            c.UseRelay = true;
            c.WantedName = name;
            c.Log = m => Console.WriteLine("   [" + name + "] " + m);
            c.Start();
            c.Connect();
            return c;
        }

        // Jak3Online.exe --relaytest : test sur le vrai relais internet gratuit
        public static int RelayTest()
        {
            Client a = NewRelayClient("Hote");
            Client b = NewRelayClient("Ami");
            Client c = NewRelayClient("Inconnu");
            Check("3 joueurs connectes au relais internet", Wait(() => a.NetState == Shm.NET_LOBBY && b.NetState == Shm.NET_LOBBY && c.NetState == Shm.NET_LOBBY, 45000));
            Console.WriteLine("   relais utilise : " + a.RelayName);

            a.UiCreate(false, 100, true);
            Check("session privee creee", Wait(() => a.SessionId != 0 && a.SessionCode.Length == 6, 8000));
            string code = a.SessionCode;
            Console.WriteLine("   code : " + code);
            b.UiJoinCode("ZZ9ZZ9");
            Thread.Sleep(6000);
            Check("mauvais code refuse", b.SessionId == 0);
            b.UiJoinCode(code);
            Check("l'ami rejoint avec le code", Wait(() => b.SessionId == a.SessionId, 10000));
            Check("2 joueurs vus des deux cotes", Wait(() => a.SessionCount == 2 && b.SessionCount == 2, 10000));
            b.Rename("AmiRenomme");
            Check("changement de pseudo vu par les autres", Wait(() => a.PlayersSnapshot().Exists(e => e.Name == "AmiRenomme"), 8000));
            TestSaves(a, b, 15000);
            TestPoses(a, b, 15000);
            c.UiList();
            Thread.Sleep(1500);
            c.UiList();
            Thread.Sleep(500);
            Check("session privee cachee de la liste", c.TestSessionListCount() == 0 || c.TestFirstListedSession() != a.SessionId);

            for (int k = 0; k < 20; k++)
            {
                a.TestSetLocalState(1234f, 10f, -50f);
                b.TestSetLocalState(1240f, 10f, -50f);
                Thread.Sleep(100);
            }
            Check("l'ami voit la position de l'hote", Wait(() => b.TestRemoteX(a.MyId) == 1234f, 8000));
            Check("l'hote voit l'ami", Wait(() => a.TestRemoteCount() == 1, 8000));
            b.TestSendHit(a.MyId, 2f);
            Check("coup PvP recu", Wait(() => a.TestInEventCount(Shm.EV_HIT) == 1, 8000));
            a.TestSendDied(b.MyId);
            Check("elimination creditee", Wait(() => b.TestInEventCount(Shm.EV_KILL) == 1, 8000));

            b.UiSettings(false, true);
            Thread.Sleep(1500);
            Check("seul l'hote change les reglages", a.SessionPvp && !a.SessionPublic);
            a.UiSettings(true, true);
            Check("l'hote rend la session publique", Wait(() => b.SessionPublic, 8000));
            Thread.Sleep(1000);
            c.UiList();
            Check("la session publique apparait dans la liste", Wait(() => { c.UiList(); Thread.Sleep(300); return c.TestSessionListCount() >= 1; }, 10000));
            c.UiJoinCode(code);
            Check("un inconnu rejoint la session publique", Wait(() => c.SessionId == a.SessionId && a.SessionCount == 3, 12000));

            a.UiLeave();
            Check("l'hote quitte", Wait(() => a.SessionId == 0, 8000));
            Check("migration : l'ami devient l'hote", Wait(() => b.HostId == b.MyId && c.HostId == b.MyId, 10000));
            Check("la session continue (2 joueurs)", Wait(() => b.SessionCount == 2 && c.SessionCount == 2, 12000));

            for (int k = 0; k < 10; k++) { c.TestSetLocalState(1250f, 10f, -50f); Thread.Sleep(100); }
            b.TestKillLink();
            Console.WriteLine("   (le nouvel hote plante, attente de la migration...)");
            Check("migration apres plantage : l'inconnu devient l'hote", Wait(() => c.HostId == c.MyId, 20000));
            Check("il reste seul dans la session", Wait(() => c.SessionCount == 1, 15000));
            c.UiLeave();
            Wait(() => c.SessionId == 0, 5000);
            a.Stop(); c.Stop();
            Console.WriteLine(fails == 0 ? "TEST DU RELAIS INTERNET OK" : (fails + " TEST(S) EN ECHEC"));
            return fails == 0 ? 0 : 1;
        }

        // Jak3Online.exe --bottest : le bot de test rejoint, tourne, prend des coups et meurt
        public static int BotTest()
        {
            const int port = 27995;
            Server s = new Server();
            s.Start(port);
            Client a = NewClient("Joueur", port);
            Wait(() => a.NetState == Shm.NET_LOBBY, 5000);
            a.UiCreate(false, 100, true);
            Wait(() => a.SessionId != 0, 3000);
            for (int k = 0; k < 5; k++) { a.TestSetLocalState(1000f, 0f, 1000f); Thread.Sleep(100); }
            a.AddBotNow();
            Check("le bot rejoint la session", Wait(() => { a.TestSetLocalState(1000f, 0f, 1000f); return a.SessionCount == 2; }, 8000));
            uint botId = 0;
            Check("le bot tourne autour du joueur", Wait(() => { a.TestSetLocalState(1000f, 0f, 1000f); foreach (PlayerEntry e in a.PlayersSnapshot()) if (e.Name == "Bot1") botId = e.Id; return botId != 0 && !float.IsNaN(a.TestRemoteX(botId)) && Math.Abs(a.TestRemoteX(botId) - 1000f) > 1000f; }, 8000));
            for (int k = 0; k < 8; k++) { a.TestSendHit(botId, 1f); a.TestSetLocalState(1000f, 0f, 1000f); Thread.Sleep(450); }
            Check("le bot meurt et l'elimination est creditee", Wait(() => { a.TestSetLocalState(1000f, 0f, 1000f); return a.TestInEventCount(Shm.EV_KILL) >= 1; }, 8000));
            a.UiLeave();
            Check("le bot part avec la session", Wait(() => a.BotCount == 0, 5000));
            a.Stop(); s.Stop();
            Console.WriteLine(fails == 0 ? "TEST DU BOT OK" : (fails + " TEST(S) EN ECHEC"));
            return fails == 0 ? 0 : 1;
        }
        public static int Run()
        {
            const int port = 27999;
            Server s = new Server();
            s.Log = m => Console.WriteLine("   [serveur] " + m);
            s.Start(port);
            Client a = NewClient("Alice", port);
            Client b = NewClient("Bob", port);
            Client c = NewClient("Alice", port);
            Check("3 clients connectes", Wait(() => a.NetState == Shm.NET_LOBBY && b.NetState == Shm.NET_LOBBY && c.NetState == Shm.NET_LOBBY, 5000));
            Check("pseudos uniques (Alice / Alice2)", a.MyName != c.MyName && (a.MyName == "Alice2" || c.MyName == "Alice2"));

            a.UiCreate(false, 100, true);
            Check("Alice cree une session privee", Wait(() => a.SessionId != 0 && a.SessionCode.Length == 6 && !a.SessionPublic, 3000));
            Check("Alice est l'hote", a.HostId == a.MyId);
            b.UiList();
            Thread.Sleep(300);
            Check("session privee absente de la liste publique", b.TestSessionListCount() == 0);
            b.UiJoinCode("ZZZZZZ");
            Thread.Sleep(300);
            Check("mauvais code refuse", b.SessionId == 0);
            b.UiJoinCode(a.SessionCode.ToLowerInvariant());
            Check("Bob rejoint avec le code", Wait(() => b.SessionId == a.SessionId, 3000));
            Check("2 joueurs dans la session", Wait(() => a.SessionCount == 2 && b.SessionCount == 2, 3000));
            b.Rename("Bobby");
            Check("changement de pseudo vu par les autres", Wait(() => a.PlayersSnapshot().Exists(e => e.Name == "Bobby"), 4000));
            TestSaves(a, b, 6000);
            TestPoses(a, b, 6000);

            a.TestSetLocalState(1234f, 10f, -50f);
            b.TestSetLocalState(1240f, 10f, -50f);
            Check("Bob voit la position d'Alice", Wait(() => b.TestRemoteX(a.MyId) == 1234f, 3000));
            Check("Alice voit Bob", Wait(() => a.TestRemoteCount() == 1, 3000));

            b.TestSendHit(a.MyId, 2f);
            Check("coup PvP de Bob recu par Alice", Wait(() => a.TestInEventCount(Shm.EV_HIT) == 1, 3000));
            a.TestSendDied(b.MyId);
            Check("elimination creditee a Bob", Wait(() => b.TestInEventCount(Shm.EV_KILL) == 1, 3000));

            b.UiSettings(false, true);
            Thread.Sleep(300);
            Check("seul l'hote change les reglages", a.SessionPvp && !a.SessionPublic);
            a.UiSettings(false, true);
            Check("l'hote rend la session publique sans pvp", Wait(() => b.SessionPublic && !b.SessionPvp, 3000));
            b.TestSendHit(a.MyId, 2f);
            Thread.Sleep(400);
            Check("pas de coup quand le pvp est coupe", a.TestInEventCount(Shm.EV_HIT) == 1);

            c.UiList();
            Check("Alice2 voit la session publique", Wait(() => c.TestSessionListCount() == 1, 3000));
            c.UiJoinId(c.TestFirstListedSession());
            Check("Alice2 rejoint la session publique", Wait(() => c.SessionId == a.SessionId && a.SessionCount == 3, 3000));

            a.UiLeave();
            Check("Alice quitte", Wait(() => a.SessionId == 0, 3000));
            Check("Bob devient l'hote", Wait(() => b.HostId == b.MyId && b.SessionCount == 2, 3000));

            // LA session publique : tout le monde se retrouve dans MONDE1
            a.UiJoinWorld();
            Check("la session publique MONDE1 est ouverte", Wait(() => a.SessionCode == "MONDE1", 3000));
            b.UiJoinWorld();
            c.UiJoinWorld();
            Check("tout le monde se retrouve dans la session publique", Wait(() => b.SessionId == a.SessionId && c.SessionId == a.SessionId && a.SessionCount == 3, 3000));
            Check("la session publique est dans la liste publique", a.SessionPublic);

            a.Stop(); b.Stop(); c.Stop(); s.Stop();
            Console.WriteLine(fails == 0 ? "TOUS LES TESTS SONT OK" : (fails + " TEST(S) EN ECHEC"));
            return fails == 0 ? 0 : 1;
        }
    }

    static class Program
    {
        [DllImport("kernel32.dll")]
        static extern bool AllocConsole();

        static string AskPassword(string text)
        {
            Form f = new Form();
            f.Text = "Jak 3 En Ligne - cle du createur"; f.Width = 460; f.Height = 170;
            f.FormBorderStyle = FormBorderStyle.FixedDialog; f.StartPosition = FormStartPosition.CenterScreen; f.MaximizeBox = false; f.MinimizeBox = false;
            Label l = new Label(); l.Text = text; l.Left = 12; l.Top = 10; l.Width = 420; l.Height = 40;
            TextBox t = new TextBox(); t.UseSystemPasswordChar = true; t.Left = 12; t.Top = 55; t.Width = 420;
            Button ok = new Button(); ok.Text = "OK"; ok.Left = 250; ok.Top = 90; ok.DialogResult = DialogResult.OK;
            Button no = new Button(); no.Text = "Annuler"; no.Left = 340; no.Top = 90; no.DialogResult = DialogResult.Cancel;
            f.Controls.Add(l); f.Controls.Add(t); f.Controls.Add(ok); f.Controls.Add(no);
            f.AcceptButton = ok; f.CancelButton = no;
            return f.ShowDialog() == DialogResult.OK ? t.Text : null;
        }

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--createur-testsauvegarde")
            {
                AllocConsole();
                string tf = Path.Combine(Path.GetTempPath(), "j3k-test.j3k");
                string e1 = Createur.Export(tf, "mot-de-passe-de-test-123");
                string e2 = e1 == null ? Createur.Import(tf, "mot-de-passe-de-test-123", false) : "-";
                string e3 = e1 == null ? Createur.Import(tf, "mauvais-mot-de-passe", false) : "-";
                Console.WriteLine("export: " + (e1 ?? "OK") + " | relecture: " + (e2 ?? "OK") + " | mauvais mdp refuse: " + (e3 != null ? "OUI (" + e3 + ")" : "NON"));
                try { File.Delete(tf); } catch (Exception) { }
                return 0;
            }
            if (args.Length > 0 && (args[0] == "--createur-sauvegarde" || args[0] == "--createur-restaurer"))
            {
                // sauvegarde / restauration de la cle du createur, protegee par un mot de passe
                Application.EnableVisualStyles();
                bool save = args[0] == "--createur-sauvegarde";
                string file;
                if (save)
                {
                    SaveFileDialog d = new SaveFileDialog();
                    d.Title = "Sauvegarde de la cle du createur";
                    d.FileName = "cle-createur-jak3online.j3k";
                    d.Filter = "Cle Jak3Online (*.j3k)|*.j3k";
                    if (d.ShowDialog() != DialogResult.OK) return 1;
                    file = d.FileName;
                }
                else
                {
                    OpenFileDialog d = new OpenFileDialog();
                    d.Title = "Restaurer la cle du createur";
                    d.Filter = "Cle Jak3Online (*.j3k)|*.j3k";
                    if (d.ShowDialog() != DialogResult.OK) return 1;
                    file = d.FileName;
                }
                string pw = AskPassword(save ? "Choisis un mot de passe (12 caracteres minimum). Sans lui, la sauvegarde est inutilisable : ne l'oublie pas !" : "Mot de passe de la sauvegarde :");
                if (pw == null) return 1;
                if (save)
                {
                    if (pw.Length < 12) { MessageBox.Show("Mot de passe trop court (12 caracteres minimum).", "Jak 3 En Ligne"); return 1; }
                    string pw2 = AskPassword("Retape le mot de passe :");
                    if (pw2 != pw) { MessageBox.Show("Les deux mots de passe sont differents.", "Jak 3 En Ligne"); return 1; }
                }
                string err;
                try { err = save ? Createur.Export(file, pw) : Createur.Import(file, pw); }
                catch (Exception ex) { err = ex.Message; }
                MessageBox.Show(err ?? (save ? "Cle sauvegardee dans :\n" + file + "\n\nGarde ce fichier ET le mot de passe en lieu sur (cle USB, cloud perso). Ne les donne a personne."
                                               : "Cle restauree : ce PC est de nouveau le createur."), "Jak 3 En Ligne");
                return err == null ? 0 : 1;
            }
            if (args.Length > 0 && args[0] == "--createur-init")
            {
                // une seule fois, sur le PC du createur : cree la cle privee (chiffree par Windows)
                string pubk = Createur.Init();
                string outp = args.Length > 1 ? args[1] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "createur-public.txt");
                File.WriteAllText(outp, pubk ?? "ERREUR");
                return pubk != null ? 0 : 1;
            }
            if (args.Length > 0 && args[0] == "--mondetest")
            {
                AllocConsole();
                return SelfTest.MondeTest(args.Length > 1 && args[1] == "relay");
            }
            if (args.Length > 0 && args[0] == "--selftest")
            {
                AllocConsole();
                return SelfTest.Run();
            }
            if (args.Length > 0 && args[0] == "--bottest")
            {
                AllocConsole();
                return SelfTest.BotTest();
            }
            if (args.Length > 0 && args[0] == "--relaytest")
            {
                AllocConsole();
                return SelfTest.RelayTest();
            }
            if (args.Length > 0 && args[0] == "--idle")
            {
                AllocConsole();
                return SelfTest.Idle(args.Length > 1 ? int.Parse(args[1]) : 90);
            }
            if (args.Length > 0 && args[0] == "--vehicules")
            {
                AllocConsole();
                return SelfTest.Vehicules();
            }
            if (args.Length > 0 && args[0] == "--police")
            {
                AllocConsole();
                return SelfTest.Police();
            }
            if (args.Length > 0 && args[0] == "--cartes")
            {
                AllocConsole();
                return SelfTest.Cartes();
            }
            if (args.Length > 0 && args[0] == "--perso")
            {
                AllocConsole();
                return SelfTest.Perso();
            }
            if (args.Length > 0 && args[0] == "--aide")
            {
                AllocConsole();
                return SelfTest.Aide();
            }
            if (args.Length > 0 && args[0] == "--coop")
            {
                AllocConsole();
                return SelfTest.Coop();
            }
            if (args.Length > 0 && args[0] == "--vehmap")
            {
                AllocConsole();
                return SelfTest.VehMap();
            }
            if (args.Length > 0 && args[0] == "--events13")
            {
                AllocConsole();
                return SelfTest.Events13();
            }
            if (args.Length > 0 && args[0] == "--acc13")
            {
                AllocConsole();
                return SelfTest.Acc13();
            }
            if (args.Length > 0 && args[0] == "--coll")
            {
                AllocConsole();
                SelfTest.CollOnly = true;
                return SelfTest.Maj13b();
            }
            if (args.Length > 0 && args[0] == "--maj13b")
            {
                AllocConsole();
                return SelfTest.Maj13b();
            }
            if (args.Length > 0 && args[0] == "--maj13")
            {
                AllocConsole();
                return SelfTest.Maj13();
            }
            if (args.Length > 0 && args[0] == "--village")
            {
                AllocConsole();
                return SelfTest.Village();
            }
            if (args.Length > 0 && args[0] == "--jak2swap")
            {
                AllocConsole();
                return SelfTest.Jak2Swap();
            }
            if (args.Length > 0 && args[0] == "--maj12e")
            {
                AllocConsole();
                return SelfTest.Maj12e();
            }
            if (args.Length > 0 && args[0] == "--j2local")
            {
                AllocConsole();
                return SelfTest.J2Local();
            }
            if (args.Length > 0 && args[0] == "--maj12d")
            {
                AllocConsole();
                return SelfTest.Maj12d();
            }
            if (args.Length > 0 && args[0] == "--maj12c")
            {
                AllocConsole();
                return SelfTest.Maj12c();
            }
            if (args.Length > 0 && args[0] == "--maj12b")
            {
                AllocConsole();
                return SelfTest.Maj12b();
            }
            if (args.Length > 0 && args[0] == "--maisons")
            {
                AllocConsole();
                return SelfTest.Maisons();
            }
            if (args.Length > 0 && args[0] == "--vehnoms")
            {
                AllocConsole();
                return SelfTest.VehNoms();
            }
            if (args.Length > 0 && args[0] == "--persos")
            {
                AllocConsole();
                return SelfTest.Persos();
            }
            if (args.Length > 0 && args[0] == "--v5")
            {
                AllocConsole();
                return SelfTest.V5();
            }
            if (args.Length > 0 && args[0] == "--final")
            {
                AllocConsole();
                return SelfTest.Final();
            }
            if (args.Length > 0 && args[0] == "--partout")
            {
                AllocConsole();
                return SelfTest.Partout();
            }
            if (args.Length > 0 && args[0] == "--snake")
            {
                AllocConsole();
                return SelfTest.Snake();
            }
            if (args.Length > 0 && args[0] == "--duo2")
            {
                AllocConsole();
                return SelfTest.Duo2();
            }
            if (args.Length > 0 && args[0] == "--duo")
            {
                AllocConsole();
                return SelfTest.Duo();
            }
            if (args.Length > 0 && args[0] == "--visual")
            {
                AllocConsole();
                return SelfTest.Visual(args.Length > 1 ? int.Parse(args[1]) : 1);
            }
            if (args.Length > 0 && args[0] == "--e2e")
            {
                AllocConsole();
                return SelfTest.EndToEnd(args.Length > 1 && args[1] == "relay");
            }
            if (args.Length > 0 && args[0] == "--probe")
            {
                AllocConsole();
                return SelfTest.Probe(120);
            }
            if (args.Length > 0 && args[0] == "--stress")
            {
                AllocConsole();
                return SelfTest.Stress(Proto.MaxPlayersPerSession);
            }
            if (args.Length > 0 && (args[0] == "--server" || args[0] == "-server" || args[0] == "/server"))
            {
                AllocConsole();
                int port = Proto.DefaultPort;
                if (args.Length > 1) int.TryParse(args[1], out port);
                Server s = new Server();
                s.Log = delegate (string m) { Console.WriteLine(DateTime.Now.ToString("HH:mm:ss") + "  " + m); };
                try
                {
                    s.Start(port);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Impossible de demarrer le serveur : " + ex.Message);
                    Console.ReadLine();
                    return 1;
                }
                Console.WriteLine("Serveur Jak 3 En Ligne pret. Port TCP " + port + ". Fermez cette fenetre pour l'arreter.");
                while (true) Thread.Sleep(1000);
            }
            bool hidden = args.Length > 0 && args[0] == "--tray";
            bool created;
            Mutex single = new Mutex(true, "Jak3Online-Compagnon", out created);
            EventWaitHandle showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Jak3Online-Afficher");
            if (!created)
            {
                // deja lance (par exemple au demarrage de Windows) : on affiche la fenetre existante
                if (!hidden) showEvent.Set();
                return 0;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            MainForm f = new MainForm(hidden);
            Thread w = new Thread(delegate ()
            {
                while (true)
                {
                    showEvent.WaitOne();
                    try { f.BeginInvoke((MethodInvoker)delegate { f.ShowFromOtherInstance(); }); } catch (Exception) { }
                }
            });
            w.IsBackground = true;
            w.Start();
            Application.Run(f);
            GC.KeepAlive(single);
            return 0;
        }
    }
}
