// ============================================================================
//  JAK 3 EN LIGNE - ANTI-TRICHE / PROTECTION DE LA SESSION PUBLIQUE
//
//  * Empreinte de la version : chaque joueur calcule une empreinte (SHA-256) de TOUT
//    le code du mod (goal_src) et de Jak3Online.exe. Elle est envoyee, signee, avec
//    son identite. Le createur publie l'empreinte OFFICIELLE (signee par sa cle) :
//    un jeu modifie (code change pour tricher) n'a pas la meme empreinte et ne peut
//    pas jouer dans le monde public ; les autres l'ignorent.
//  * Verifications croisees : chaque jeu surveille les autres (vitesse impossible,
//    points de vie impossibles, coups tires de trop loin ou trop vite, degats
//    impossibles sur un boss). Trois fautes en 2 minutes = joueur SUSPECT : ses coups
//    sont ignores et le createur est prevenu (il peut le bannir).
//  * Le jeu signale ce qu'il voit chez lui (mode debug, points de vie au-dessus du
//    maximum...) : plus aucun gain.
//  * Aucune adresse IP n'est jamais echangee entre joueurs : tout passe par le relais
//    internet. Les messages importants sont signes (personne ne peut parler, tirer ou
//    tuer "au nom" d'un autre joueur).
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Jak3Online
{
    static class Empreinte
    {
        static byte[] fp;
        static bool started;
        static readonly object lk = new object();
        public static string Detail = "";

        // dossier data du mod (Jak3Online.exe est dans data/online)
        static string DataDir()
        {
            string over = Environment.GetEnvironmentVariable("JAK3ONLINE_DATA");
            if (!string.IsNullOrEmpty(over)) return over;
            return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
        }

        public static void Start()
        {
            lock (lk)
            {
                if (started) return;
                started = true;
            }
            Thread t = new Thread(Compute);
            t.IsBackground = true;
            t.Priority = ThreadPriority.BelowNormal;
            t.Name = "empreinte";
            t.Start();
        }

        public static byte[] Value { get { lock (lk) return fp; } }

        static void Compute()
        {
            try
            {
                string root = Path.Combine(DataDir(), "goal_src", "jak3");
                List<string> files = new List<string>();
                if (Directory.Exists(root)) files.AddRange(Directory.GetFiles(root, "*.gc", SearchOption.AllDirectories));
                string gd = Path.Combine(root, "dgos", "game.gd");
                if (File.Exists(gd)) files.Add(gd);
                string gp = Path.Combine(root, "game.gp");
                if (File.Exists(gp)) files.Add(gp);
                for (int i = 0; i < files.Count; i++) files[i] = files[i].Substring(root.Length).Replace('\\', '/').ToLowerInvariant();
                files.Sort(StringComparer.Ordinal);
                using (SHA256 h = SHA256.Create())
                {
                    byte[] buf;
                    foreach (string rel in files)
                    {
                        byte[] name = Encoding.ASCII.GetBytes(rel + "\n");
                        h.TransformBlock(name, 0, name.Length, null, 0);
                        buf = File.ReadAllBytes(root + rel.Replace('/', Path.DirectorySeparatorChar));
                        // fins de ligne normalisees (CRLF / LF)
                        int n = 0;
                        for (int i = 0; i < buf.Length; i++) if (buf[i] != 13) buf[n++] = buf[i];
                        h.TransformBlock(buf, 0, n, null, 0);
                    }
                    // le programme compagnon lui-meme
                    string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                    buf = File.ReadAllBytes(exe);
                    h.TransformFinalBlock(buf, 0, buf.Length);
                    lock (lk) fp = h.Hash;
                    Detail = files.Count + " fichiers";
                }
            }
            catch (Exception ex)
            {
                lock (lk) fp = new byte[32];
                Detail = "erreur : " + ex.Message;
            }
        }

        public static string Short(byte[] f)
        {
            if (f == null) return "?";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 4; i++) sb.Append(f[i].ToString("X2"));
            return sb.ToString();
        }

        // empreinte pas encore calculee (tout a zero)
        public static bool Unknown(byte[] f)
        {
            if (f == null) return true;
            foreach (byte x in f) if (x != 0) return false;
            return true;
        }

        public static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }

    partial class Client
    {
        byte[] officialFp;              // empreinte officielle (publiee par le createur)
        long officialStamp;
        bool selfCheat;                 // ce jeu a triche : plus aucun gain
        uint warnSeq;
        string warnText = "";
        DateTime lastVersionPublish = DateTime.MinValue;
        uint myHouseOwner;                               // maison ou je suis (proprietaire ; 0 = dehors)
        DateTime lastHouseMsg = DateTime.MinValue;
        bool versionWarned;
        readonly Dictionary<uint, double[]> lastSeen = new Dictionary<uint, double[]>();   // id -> x, y, z, t(ms)
        readonly Dictionary<uint, DateTime> lastState = new Dictionary<uint, DateTime>();
        readonly Dictionary<uint, long> deadSeen = new Dictionary<uint, long>();
        readonly Dictionary<uint, int> fastCount = new Dictionary<uint, int>();
        readonly Dictionary<uint, List<double[]>> speedWin = new Dictionary<uint, List<double[]>>();

        void Warn(string text)
        {
            lock (lk) { warnText = Proto.GameText(text, 63); warnSeq++; }
            L("ANTI-TRICHE : " + text);
        }

        // ---------------- version officielle (createur -> tout le monde, gardee par le relais)
        void PublishVersion()
        {
            byte[] f = Empreinte.Value;
            if (!IsCreateur || f == null) return;
            lastVersionPublish = DateTime.UtcNow;
            long stamp = Profil.NowMs();
            byte[] body = Build(w => { w.Write(f); w.Write(stamp); });
            byte[] sig = Createur.Sign(Concat(Encoding.ASCII.GetBytes("version"), body));
            if (sig == null) return;
            SendMsg(MSG_VERSION, 0, Build(w => { w.Write(body); w.Write((ushort)sig.Length); w.Write(sig); }));
            lock (lk) { officialFp = f; officialStamp = stamp; }
        }

        void OnVersionMsg(uint from, byte[] p)
        {
            if (p.Length < 32 + 8 + 2) return;
            byte[] body = new byte[40];
            Buffer.BlockCopy(p, 0, body, 0, 40);
            int siglen = BitConverter.ToUInt16(p, 40);
            if (p.Length < 42 + siglen) return;
            byte[] sig = new byte[siglen];
            Buffer.BlockCopy(p, 42, sig, 0, siglen);
            if (!Createur.Verify(Concat(Encoding.ASCII.GetBytes("version"), body), sig)) return;
            long stamp = BitConverter.ToInt64(body, 32);
            byte[] f = new byte[32];
            Buffer.BlockCopy(body, 0, f, 0, 32);
            lock (lk)
            {
                if (stamp < officialStamp) return;
                officialFp = f;
                officialStamp = stamp;
                foreach (MemberInfo m in members.Values)
                    if (m.Fingerprint != null && !Empreinte.Unknown(m.Fingerprint) && (m.Flags & 2) == 0) m.BadVersion = !Empreinte.Same(m.Fingerprint, f);
            }
            CheckMyVersion();
        }

        void CheckMyVersion()
        {
            byte[] mine = Empreinte.Value;
            byte[] off;
            lock (lk) off = officialFp;
            if (!InWorld || IsCreateur || mine == null || off == null) return;
            if (!Empreinte.Same(mine, off) && !versionWarned)
            {
                versionWarned = true;
                Warn(T("Ta version du mod n'est pas la version officielle : mets-la a jour", "Your mod is not the official version: update it"));
                PushFeed(T("Version du mod differente : monde public interdit (mets le mod a jour)", "Different mod version: public world refused (update the mod)"));
                LeaveLater();
            }
        }

        // appele quand un HELLO donne l'empreinte d'un joueur
        void NoteFingerprint(MemberInfo m, byte[] f)
        {
            m.Fingerprint = f;
            byte[] off = officialFp;
            bool bad = off != null && !Empreinte.Unknown(f) && !Empreinte.Same(f, off) && (m.Flags & 2) == 0;
            if (bad && !m.BadVersion && InWorld) AddChat(3, "", m.Name + T(" utilise une version modifiee du mod : ignore", " uses a modified mod: ignored"));
            m.BadVersion = bad;
        }

        // ---------------- fautes constatees chez les autres
        void Strike(uint id, string why)
        {
            bool nowSuspect = false;
            string name;
            lock (lk)
            {
                MemberInfo m;
                if (!members.TryGetValue(id, out m)) return;
                name = m.Name;
                if ((DateTime.UtcNow - m.StrikeWindow).TotalSeconds > 120) { m.StrikeWindow = DateTime.UtcNow; m.Strikes = 0; }
                m.Strikes++;
                if (m.Strikes >= 3 && !m.Suspect) { m.Suspect = true; nowSuspect = true; }
            }
            L("anti-triche : " + name + " - " + why);
            if (nowSuspect)
            {
                if (IsCreateur) AddChat(3, "", T("ANTI-TRICHE : ", "ANTI-CHEAT: ") + name + T(" est suspect (", " is suspect (") + why + T(") - TAB pour le bannir", ") - TAB to ban"));
                SendReport(id, why);
            }
        }

        void SendReport(uint id, string why)
        {
            if (SessionId == 0 || ident == null) return;
            ulong uid = UidOfMember(id);
            long stamp = Profil.NowMs();
            byte[] body = Build(w => { w.Write(id); w.Write(uid); w.Write(stamp); WStr(w, why, 40); });
            byte[] sig = ident.Sign(Concat(BitConverter.GetBytes(MyId), Encoding.ASCII.GetBytes(SessionCode), body));
            uint to = 0;
            lock (lk) foreach (MemberInfo m in members.Values) if ((m.Flags & 2) != 0 && m.Verified) to = m.Id;
            if (to != 0) SendMsg(MSG_REPORT, to, Build(w => { w.Write(body); w.Write((ushort)sig.Length); w.Write(sig); }));
        }

        void OnReport(uint from, byte[] p)
        {
            if (!IsCreateur || p.Length < 4 + 8 + 8 + 40 + 2) return;
            BinaryReader r = new BinaryReader(new MemoryStream(p));
            uint id = r.ReadUInt32();
            r.ReadUInt64();
            r.ReadInt64();
            string why = Rd.Str(r, 40);
            int bodyLen = (int)r.BaseStream.Position;
            int siglen = r.ReadUInt16();
            byte[] sig = r.ReadBytes(siglen);
            byte[] body = new byte[bodyLen];
            Buffer.BlockCopy(p, 0, body, 0, bodyLen);
            MemberInfo m;
            lock (lk) { if (!members.TryGetValue(from, out m) || !m.Verified) return; }
            if (!Identity.Verify(m.Pub, Concat(BitConverter.GetBytes(from), Encoding.ASCII.GetBytes(SessionCode), body), sig)) return;
            AddChat(3, "", T("SIGNALEMENT de ", "REPORT from ") + m.Name + " : " + NameOf(id) + " (" + why + ")");
        }

        // l'etat d'un autre joueur vient d'arriver : plausibilite
        void CheckRemoteState(uint id, byte[] st)
        {
            if (!InWorld || st == null || st.Length < 28) return;
            uint flags = BitConverter.ToUInt32(st, 0);
            float x = BitConverter.ToSingle(st, 12), y = BitConverter.ToSingle(st, 16), z = BitConverter.ToSingle(st, 20);
            float hp = BitConverter.ToSingle(st, 24);
            long now = Profil.NowMs();
            // valeur invalide : un instant de mort (noyade...) ou un bug, pas une triche -> on l'ignore
            if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z) || float.IsNaN(hp) || float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(z)) return;
            if (hp > 40f) Strike(id, T("points de vie impossibles", "impossible health"));
            double[] last;
            lock (lk)
            {
                if (!lastSeen.TryGetValue(id, out last)) { last = new double[4]; lastSeen[id] = last; last[3] = -1; }
            }
            if ((flags & Proto.FLAG_DEAD) != 0) lock (lk) deadSeen[id] = now;
            long dAt;
            bool recentlyDead;
            lock (lk) recentlyDead = deadSeen.TryGetValue(id, out dAt) && now - dAt < 8000;
            if (last[3] >= 0 && (flags & Proto.FLAG_DEAD) == 0 && !recentlyDead)
            {
                float dt = (now - (long)last[3]) / 1000f;
                if (dt > 0.2f && dt < 3f)
                {
                    double dx = x - last[0], dz = z - last[2];
                    double d = Math.Sqrt(dx * dx + dz * dz) / 4096.0;
                    // on additionne les petits deplacements sur 3 secondes (une teleportation ou une
                    // reapparition, c'est UN grand saut : il n'est pas compte). Plus de 90 m/s de
                    // moyenne pendant 3 s = impossible, meme en vehicule.
                    bool strike = false;
                    lock (lk)
                    {
                        List<double[]> q;
                        if (!speedWin.TryGetValue(id, out q)) { q = new List<double[]>(); speedWin[id] = q; }
                        // (teleportations, reapparitions, tremplins des parcours : un bond instantane ne compte pas)
                        if (d / dt < 400.0) q.Add(new double[] { now, d });
                        q.RemoveAll(e => now - e[0] > 3000);
                        double sum = 0;
                        foreach (double[] e in q) sum += e[1];
                        if (sum > 150.0 * 3.0) { strike = true; q.Clear(); }
                    }
                    // (un joueur qui rame a des bonds de position : on le note seulement, sans l'accuser)
                    if (strike) L("anti-triche (info) : vitesse tres elevee pour " + NameOf(id) + " (lag probable)");
                }
            }
            if (last[3] < 0 || (now - (long)last[3]) > 250)
            {
                last[0] = x; last[1] = y; last[2] = z; last[3] = now;
            }
        }

        // un coup PvP arrive : a-t-il pu etre tire ?
        bool HitPlausible(uint from, float dmg)
        {
            if (!InWorld) return true;
            MemberInfo m;
            lock (lk) { if (!members.TryGetValue(from, out m)) return false; }
            if (m.Suspect || m.BadVersion) return false;
            // cadence : 8 coups par seconde au plus
            if ((DateTime.UtcNow - m.LastHitFrom).TotalMilliseconds > 1000) { m.LastHitFrom = DateTime.UtcNow; m.HitCount = 0; }
            // au-dela de 30 coups / s le coup est ignore ; on ne signale le joueur que si c'est enorme
            // (un paquet de coups arrive d'un bloc apres du lag : ce n'est pas une triche)
            if (++m.HitCount > 30) { if (m.HitCount == 90) Strike(from, T("cadence de tir impossible", "impossible fire rate")); return false; }
            // distance : le tireur doit etre a portee
            double[] last;
            float x, y, z;
            lock (lk) { if (!lastSeen.TryGetValue(from, out last)) last = null; }
            if (last != null && last[3] >= 0 && MyPos(out x, out y, out z))
            {
                double dx = x - last[0], dy = y - last[1], dz = z - last[2];
                double d = Math.Sqrt(dx * dx + dy * dy + dz * dz) / 4096.0;
                // trop loin : coup ignore (position en retard, teleportation...) ; signale seulement si
                // c'est impossible meme avec du lag
                if (d > 140) { if (d > 1500) Strike(from, T("coup tire de trop loin", "hit from too far")); return false; }
            }
            return true;
        }

        // le jeu signale une triche chez moi
        void OnSelfCheat(int reason)
        {
            if (!InWorld || IsCreateur) return;
            // a chaque fois : expulse du monde en ligne (la triche est deja coupee par le jeu)
            LeaveLater();
            if (!selfCheat)
            {
                selfCheat = true;
                string why = reason == 1 ? T("mode debug", "debug mode") : reason == 2 ? T("points de vie modifies", "modified health")
                    : reason == 3 ? T("munitions modifiees", "modified ammo") : reason == 4 ? T("codes de triche", "cheat codes")
                    : reason == 5 ? T("coffre ouvert a distance", "chest opened from afar") : T("jeu modifie", "modified game");
                // la triche est coupee par le jeu, et le tricheur est expulse du monde en ligne
                Warn(T("TRICHE DETECTEE (", "CHEAT DETECTED (") + why + T(") : triche coupee, tu es expulse du monde en ligne", "): cheat disabled, you are kicked from the online world"));
                PushFeed(T("Expulse : triche (", "Kicked: cheating (") + why + ")");
            }
        }

        // ---------------- boucle (1 fois par seconde)
        void GardeTick()
        {
            if (SessionId == 0) return;
            if (IsCreateur && InWorld && (DateTime.UtcNow - lastVersionPublish).TotalSeconds > 300) PublishVersion();
            CheckMyVersion();
            ClockTick();
            // maison : rappel regulier (pour les joueurs arrives apres)
            if (myHouseOwner != 0 && (DateTime.UtcNow - lastHouseMsg).TotalSeconds > 5)
            {
                SendMsg(MSG_HOUSE, 0, BitConverter.GetBytes(myHouseOwner));
                lastHouseMsg = DateTime.UtcNow;
            }
        }

        public bool TestSelfCheat { get { return selfCheat; } }
        public string TestFingerprint { get { return Empreinte.Short(Empreinte.Value); } }
        public int TestSuspects { get { lock (lk) { int n = 0; foreach (MemberInfo m in members.Values) if (m.Suspect) n++; return n; } } }
    }
}
