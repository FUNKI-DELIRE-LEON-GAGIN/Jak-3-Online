// ============================================================================
//  JAK 3 EN LIGNE - EVENEMENTS DU MONDE EN LIGNE
//
//  Toutes les 6 a 9 minutes, un evenement arrive quelque part pres d'un joueur :
//    * ALERTE BOSS : un boss geant apparait (3 Jak geants, le ROBO BOSS, le TITAN, la BETE
//      DU DESERT). Tout le monde tape dessus ; ses points de vie sont partages. Un seul jeu
//      "pilote" le boss (le joueur proche au plus petit numero) : sa position est envoyee aux
//      autres, le boss est au meme endroit chez tout le monde.
//    * PLUIE D'ORBES : 90 secondes d'orbes en pagaille (gains doubles sur place).
//    * CHASSE AU TRESOR : un coffre est cache dans la zone ; le premier qui l'ouvre gagne.
//    * DOUBLE XP : 5 minutes d'experience doublee pour tout le monde.
//    * ZONE A CAPTURER : rester le plus longtemps dans le cercle pendant 2 minutes.
//    * PLUIE DE METEORES : rester dans la zone malgre les meteores (score comme la zone).
//    * CHASSE A L'HOMME (createur) : le premier qui elimine la cible gagne gros.
//    * PARCOURS (cartes du mod) : le premier arrive en haut gagne ; les autres rentrent au port.
//
//  C'est "l'autorite" qui lance les evenements et annonce les resultats : le createur
//  s'il est connecte (cle verifiee), sinon l'hote technique de la session. Chaque
//  joueur calcule lui-meme ses gains a partir des messages signes.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Jak3Online
{
    partial class Client
    {
        public const int EVK_NONE = 0, EVK_BOSS = 1, EVK_ORBS = 2, EVK_TREASURE = 3, EVK_DOUBLEXP = 4, EVK_ZONE = 5,
            EVK_HUNT = 6, EVK_PARKOUR = 7, EVK_METEOR = 8, EVK_MAX = 8;
        const int EV_START = 1, EV_END = 2, EV_DMG = 3, EV_FOUND = 4, EV_SCORE = 5, EV_SYNC = 6,
            EV_POS = 7, EV_HUNTED = 8, EV_FINISH = 9;
        public const int EV_BOSS_POS = 22;
        public const int EV_PERSO = 23;      // le jeu choisit un personnage (mode = numero)

        class WorldEvent
        {
            public uint Id, Owner, Winner, Target;
            public int Kind, Boss, State;          // State : 1 en cours, 2 reussi, 3 rate
            public float X, Y, Z, HpMax, Radius;
            public int Dur;
            public string Level = "", Name = "", Top = "";
            public long StartMs, EndMs, ResultMs;
            public readonly Dictionary<uint, float> Dmg = new Dictionary<uint, float>();
            public readonly Dictionary<uint, float> Score = new Dictionary<uint, float>();
            public bool Rewarded, FoundSent, ScoreSent, HuntSent;
        }

        WorldEvent ev;
        uint evSeq;
        float myBossDmg, myZone;
        long lastZoneTick;
        DateTime lastDmgSend = DateTime.MinValue;
        float lastDmgSent = -1f;
        DateTime nextEvent = DateTime.MinValue;
        readonly Random evRng = new Random(Guid.NewGuid().GetHashCode());

        // boss synchronise
        uint bossSeq, bossAnim, bossRemoteDriver;
        float bossX, bossY, bossZ, bossYaw;
        long bossRemoteMs, bossMySendMs, bossGameMs;

        static readonly string[] BossNamesFr = { "DARK JAK GEANT", "OMBRE DE JAK", "JAK DECHU", "ROBO BOSS", "TITAN", "BETE DU DESERT" };
        static readonly string[] BossNamesEn = { "GIANT DARK JAK", "JAK'S SHADOW", "FALLEN JAK", "ROBO BOSS", "TITAN", "DESERT BEAST" };
        // vie des boss (x points de base) : les vrais boss sont plus solides
        static readonly float[] BossHpMul = { 1.0f, 0.8f, 1.3f, 1.5f, 2.0f, 1.2f };

        // ---------------- cartes des parcours (memes numeros que *ow-maps* dans online-maps.gc)
        public class ParkourMap
        {
            public string NameFr, NameEn, Level;
            public int Reward, Xp, Stars;
            public float SX, SY, SZ, FX, FY, FZ;    // depart / arrivee (unites du jeu)
        }
        public static readonly ParkourMap[] Maps = {
            // BEGIN MAPS (build_maps.py)
            MapDef("Village de Jak 1", "Jak 1 village", "ow-jak1", 1, 900, 700, -1626.50f, 107.00f, 1812.50f, -1390.10f, 63.30f, 1489.60f),
            MapDef("Tour celeste", "Sky tower", "ow-ciel", 3, 2000, 1500, 3000.00f, 300.60f, -3034.00f, 3000.00f, 367.30f, -3000.00f),
            MapDef("Volcan", "Volcano", "ow-volcan", 4, 3000, 2000, -3000.00f, 212.60f, -3106.00f, -3000.00f, 301.40f, -2960.00f),
            MapDef("Glacier", "Glacier", "ow-glace", 4, 3000, 2000, -3000.00f, 210.60f, 2964.00f, -3000.00f, 275.90f, 3276.00f),
            MapDef("Paris", "Paris", "ow-paris", 5, 4000, 2500, 3004.00f, 100.70f, 2856.00f, 3000.00f, 241.00f, 3130.00f),
            // END MAPS (build_maps.py)
        };
        static ParkourMap MapDef(string fr, string en, string lvl, int stars, int reward, int xp, float sx, float sy, float sz, float fx, float fy, float fz)
        {
            ParkourMap m = new ParkourMap();
            m.NameFr = fr; m.NameEn = en; m.Level = lvl; m.Stars = stars; m.Reward = reward; m.Xp = xp;
            m.SX = sx * 4096f; m.SY = sy * 4096f; m.SZ = sz * 4096f; m.FX = fx * 4096f; m.FY = fy * 4096f; m.FZ = fz * 4096f;
            return m;
        }
        string MapName(int i) { return i >= 0 && i < Maps.Length ? T(Maps[i].NameFr, Maps[i].NameEn) : "?"; }

        // nom de l'evenement dans MA langue (le nom recu est celui de la langue de l'autorite)
        string EvName(WorldEvent e)
        {
            if (e.Kind == EVK_BOSS && e.Boss >= 0 && e.Boss < BossNamesFr.Length) return T(BossNamesFr[e.Boss], BossNamesEn[e.Boss]);
            if (e.Kind == EVK_PARKOUR) return MapName(e.Boss);
            if (e.Kind == EVK_HUNT) return NameOf(e.Target);
            return e.Name;
        }

        // l'autorite des evenements : le createur (cle verifiee) s'il est la, sinon l'hote
        uint EventAuthority()
        {
            if (IsCreateur) return MyId;
            lock (lk)
            {
                uint best = 0;
                foreach (MemberInfo m in members.Values)
                    if (m.Verified && (m.Flags & 2) != 0 && !m.Banned && (best == 0 || m.Id < best)) best = m.Id;
                if (best != 0) return best;
            }
            return HostId;
        }

        bool EventActive(int kind)
        {
            WorldEvent e = ev;
            return e != null && e.State == 1 && (kind == 0 || e.Kind == kind) && Profil.NowMs() < e.EndMs;
        }

        bool EventDoubleXp() { return InWorld && EventActive(EVK_DOUBLEXP); }

        static bool ScoreKind(int k) { return k == EVK_ZONE || k == EVK_METEOR; }

        bool MyPos(out float x, out float y, out float z)
        {
            x = y = z = 0;
            byte[] st = LocalStateSnapshot();
            if (st == null) return false;
            x = BitConverter.ToSingle(st, 12); y = BitConverter.ToSingle(st, 16); z = BitConverter.ToSingle(st, 20);
            return true;
        }

        string MyLevelName()
        {
            byte[] st = LocalStateSnapshot();
            if (st == null) return "";
            int n = 0;
            while (n < 16 && st[64 + n] != 0) n++;
            return Encoding.ASCII.GetString(st, 64, n);
        }

        bool PlayerPos(uint id, out float x, out float y, out float z)
        {
            x = y = z = 0;
            if (id == MyId) return MyPos(out x, out y, out z);
            lock (lk)
                foreach (PlayerEntry pe in playerList)
                    if (pe.Id == id && pe.HasPos) { x = pe.X; y = pe.Y; z = pe.Z; return true; }
            return false;
        }

        bool NearEvent(float meters)
        {
            WorldEvent e = ev;
            float x, y, z;
            if (e == null || !MyPos(out x, out y, out z)) return false;
            float dx = x - e.X, dy = y - e.Y, dz = z - e.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz) < meters * 4096.0;
        }

        bool OrbRainNearMe() { return InWorld && EventActive(EVK_ORBS) && NearEvent(70f); }

        // ---------------- lancement (autorite)
        // variant : boss (0..5, -1 = au hasard) ou carte du parcours ; target : joueur chasse
        public void StartEventAt(int kind, float x, float y, float z, string level, int variant = -1, uint target = 0)
        {
            if (SessionId == 0 || kind < 1 || kind > EVK_MAX) return;
            if (kind == EVK_HUNT && target == 0) target = MyId;
            if (kind == EVK_PARKOUR)
            {
                if (variant < 0 || variant >= Maps.Length) variant = evRng.Next(Maps.Length);
                ParkourMap pm = Maps[variant];
                x = pm.SX; y = pm.SY; z = pm.SZ; level = pm.Level;
            }
            long now = Profil.NowMs();
            byte[] b4 = new byte[4];
            evRng.NextBytes(b4);
            uint id = BitConverter.ToUInt32(b4, 0) | 1;
            int players = Math.Max(1, SessionCount);
            // position : un peu a l'ecart du joueur choisi (le jeu cherche le sol)
            double ang = evRng.NextDouble() * Math.PI * 2;
            double dist = kind == EVK_BOSS ? 22 : kind == EVK_TREASURE ? 35 + evRng.NextDouble() * 25 : kind == EVK_ZONE ? 18 : kind == EVK_ORBS ? 8 : kind == EVK_METEOR ? 12 : 0;
            float ex = x + (float)(Math.Sin(ang) * dist * 4096.0), ez = z + (float)(Math.Cos(ang) * dist * 4096.0);
            int dur = kind == EVK_BOSS ? 300 : kind == EVK_ORBS ? 90 : kind == EVK_TREASURE ? 300 : kind == EVK_DOUBLEXP ? 300
                : kind == EVK_HUNT ? 300 : kind == EVK_PARKOUR ? 480 : kind == EVK_METEOR ? 100 : 120;
            int boss = kind == EVK_BOSS ? (variant >= 0 && variant < BossNamesFr.Length ? variant : evRng.Next(BossNamesFr.Length))
                : kind == EVK_PARKOUR ? variant : 0;
            float hp = kind == EVK_BOSS ? (300f + 150f * players) * BossHpMul[boss] : 0f;
            string name = kind == EVK_BOSS ? (French ? BossNamesFr[boss] : BossNamesEn[boss]) : kind == EVK_PARKOUR ? Maps[boss].NameFr : "";
            uint tg = target;
            byte[] payload = Build(w =>
            {
                w.Write((byte)EV_START); w.Write(id); w.Write((byte)kind); w.Write(ex); w.Write(y); w.Write(ez);
                WStr(w, level, 16); WStr(w, name, 24); w.Write(now); w.Write((ushort)dur); w.Write(hp); w.Write((byte)boss);
                w.Write(tg);
            });
            SignedEventSend(0, payload);
        }

        void StartRandomEvent()
        {
            // pres d'un joueur au hasard (position connue)
            List<PlayerEntry> cands = new List<PlayerEntry>();
            lock (lk) foreach (PlayerEntry pe in playerList) if (pe.HasPos || pe.Id == MyId) cands.Add(pe);
            if (cands.Count == 0) return;
            PlayerEntry t = cands[evRng.Next(cands.Count)];
            float x = t.X, y = t.Y, z = t.Z;
            string lvl = t.Level ?? "";
            if (t.Id == MyId) { if (!MyPos(out x, out y, out z)) return; lvl = MyLevelName(); }
            // pas d'evenement dans les petits interieurs (bar, tente...) ni sur les cartes des parcours
            if (lvl.StartsWith("hiphog") || lvl.StartsWith("onintent") || lvl.StartsWith("freehq") || lvl.StartsWith("title") || lvl.StartsWith("ow-") || lvl.Length == 0) return;
            int[] bag = { EVK_BOSS, EVK_BOSS, EVK_BOSS, EVK_ORBS, EVK_TREASURE, EVK_DOUBLEXP, EVK_ZONE, EVK_METEOR, EVK_METEOR, EVK_PARKOUR };
            int kind = bag[evRng.Next(bag.Length)];
            // un parcours tout seul emmene tout le monde sur une carte : seulement a plusieurs
            if (kind == EVK_PARKOUR && SessionCount < 3) kind = EVK_BOSS;
            StartEventAt(kind, x, y, z, lvl);
        }

        // message d'evenement signe par son auteur (identite du joueur)
        void SignedEventSend(uint target, byte[] payload)
        {
            if (ident == null) return;
            byte[] sig = ident.Sign(Concat(BitConverter.GetBytes(MyId), Encoding.ASCII.GetBytes(SessionCode), payload));
            byte[] all = Build(w => { w.Write((ushort)payload.Length); w.Write(payload); w.Write((ushort)sig.Length); w.Write(sig); });
            SendMsg(MSG_EVENT, target, all);
            OnEventMsg(MyId, all);
        }

        string EventStartText(WorldEvent e)
        {
            string zone = ZoneText(e.Level);
            switch (e.Kind)
            {
                case EVK_BOSS: return T("ALERTE : ", "ALERT: ") + EvName(e) + T(" DETECTE - ", " DETECTED - ") + zone;
                case EVK_ORBS: return T("PLUIE D'ORBES - ", "ORB RAIN - ") + zone + T(" (90 s)", " (90 s)");
                case EVK_TREASURE: return T("CHASSE AU TRESOR - un coffre est cache : ", "TREASURE HUNT - a chest is hidden: ") + zone;
                case EVK_DOUBLEXP: return T("DOUBLE XP POUR TOUT LE MONDE (5 min) !", "DOUBLE XP FOR EVERYONE (5 min)!");
                case EVK_HUNT:
                    return e.Target == MyId
                        ? T("CHASSE A L'HOMME : TU ES LA CIBLE ! Survis 5 minutes", "MANHUNT: YOU ARE THE TARGET! Survive 5 minutes")
                        : T("CHASSE A L'HOMME : le premier qui elimine ", "MANHUNT: the first to eliminate ") + NameOf(e.Target) + T(" gagne 1500 orbes !", " wins 1500 orbs!");
                case EVK_PARKOUR:
                    return T("PARCOURS : ", "PARKOUR: ") + MapName(e.Boss) + T(" - le premier en haut gagne ", " - first to the top wins ")
                        + (e.Boss >= 0 && e.Boss < Maps.Length ? Maps[e.Boss].Reward : 0) + T(" orbes !", " orbs!");
                case EVK_METEOR: return T("PLUIE DE METEORES - ", "METEOR SHOWER - ") + zone + T(" : tiens bon dans la zone !", ": hold on in the zone!");
                default: return T("ZONE A CAPTURER - ", "CAPTURE THE ZONE - ") + zone + T(" (2 min)", " (2 min)");
            }
        }

        // ---------------- reception
        void OnEventMsg(uint from, byte[] p)
        {
            BinaryReader r0 = new BinaryReader(new MemoryStream(p));
            int plen = r0.ReadUInt16();
            if (plen < 5 || plen > 400) return;
            byte[] payload = r0.ReadBytes(plen);
            int siglen = r0.ReadUInt16();
            byte[] sig = r0.ReadBytes(siglen);
            MemberInfo m = null;
            if (from != MyId)
            {
                lock (lk) { if (!members.TryGetValue(from, out m) || !m.Verified || m.Banned || m.BadVersion) return; }
                if (!Identity.Verify(m.Pub, Concat(BitConverter.GetBytes(from), Encoding.ASCII.GetBytes(SessionCode), payload), sig)) return;
            }
            BinaryReader r = new BinaryReader(new MemoryStream(payload));
            int op = r.ReadByte();
            uint id = r.ReadUInt32();
            bool fromAuthority = from == EventAuthority() || (m != null && (m.Flags & 2) != 0) || (from == MyId && EventAuthority() == MyId) || from == HostId;
            long now = Profil.NowMs();
            switch (op)
            {
                case EV_START:
                    {
                        if (!fromAuthority) return;
                        WorldEvent e = new WorldEvent();
                        e.Id = id; e.Owner = from; e.State = 1;
                        e.Kind = r.ReadByte();
                        e.X = r.ReadSingle(); e.Y = r.ReadSingle(); e.Z = r.ReadSingle();
                        e.Level = Rd.Str(r, 16); e.Name = Rd.Str(r, 24);
                        e.StartMs = Math.Min(now, r.ReadInt64());
                        e.Dur = Math.Min(600, (int)r.ReadUInt16());
                        e.EndMs = now + e.Dur * 1000L;
                        e.HpMax = Math.Max(0f, Math.Min(100000f, r.ReadSingle()));
                        e.Boss = r.ReadByte();
                        if (r.BaseStream.Position + 4 <= r.BaseStream.Length) e.Target = r.ReadUInt32();
                        if (e.Kind < 1 || e.Kind > EVK_MAX) return;
                        e.Radius = e.Kind == EVK_ZONE ? 15f : e.Kind == EVK_ORBS ? 30f : e.Kind == EVK_METEOR ? 40f : 0f;
                        lock (lk)
                        {
                            if (ev != null && ev.Id == id) return;   // deja connu
                            ev = e; evSeq++; myBossDmg = 0f; myZone = 0f; lastDmgSent = -1f;
                            bossSeq = 0; bossRemoteDriver = 0; bossRemoteMs = 0;
                        }
                        string txt = EventStartText(e);
                        lock (lk) { announce = txt; announceKind = 3; announceSeq++; }
                        AddChat(4, T("EVENEMENT", "EVENT"), txt);
                        break;
                    }
                case EV_END:
                    {
                        if (!fromAuthority) return;
                        int state = r.ReadByte();
                        uint winner = r.ReadUInt32();
                        string top = Rd.Str(r, 64);
                        WorldEvent e = ev;
                        if (e == null || e.Id != id || e.State != 1) return;
                        lock (lk) { e.State = state == 2 ? 2 : 3; e.Winner = winner; e.Top = top; e.ResultMs = now; evSeq++; }
                        EventResult(e);
                        break;
                    }
                case EV_DMG:
                    {
                        float total = r.ReadSingle();
                        WorldEvent e = ev;
                        if (e == null || e.Id != id || e.Kind != EVK_BOSS || float.IsNaN(total) || total < 0) return;
                        // plausibilite : pas plus de 45 degats par seconde depuis le debut
                        float cap = 45f * Math.Max(1f, (now - e.StartMs) / 1000f);
                        lock (lk) { e.Dmg[from] = Math.Min(total, cap); evSeq++; }
                        if (m != null && total > cap * 1.5f) Strike(from, T("degats impossibles sur le boss", "impossible boss damage"));
                        break;
                    }
                case EV_FOUND:
                    {
                        WorldEvent e = ev;
                        if (e == null || e.Id != id || e.Kind != EVK_TREASURE || e.State != 1) return;
                        // l'autorite donne le coffre au premier
                        if (EventAuthority() == MyId) EndEvent(e, 2, from, NameOf(from));
                        break;
                    }
                case EV_SCORE:
                    {
                        float sc = r.ReadSingle();
                        WorldEvent e = ev;
                        if (e == null || e.Id != id || !ScoreKind(e.Kind) || float.IsNaN(sc)) return;
                        float cap = Math.Max(1f, (now - e.StartMs) / 1000f) + 2f;
                        lock (lk) e.Score[from] = Math.Max(0f, Math.Min(sc, cap));
                        break;
                    }
                case EV_SYNC:
                    {
                        // un nouveau joueur demande l'evenement en cours
                        WorldEvent e = ev;
                        if (EventAuthority() != MyId || e == null || e.State != 1) return;
                        int left = (int)Math.Max(1, (e.EndMs - now) / 1000);
                        byte[] payload2 = Build(w =>
                        {
                            w.Write((byte)EV_START); w.Write(e.Id); w.Write((byte)e.Kind); w.Write(e.X); w.Write(e.Y); w.Write(e.Z);
                            WStr(w, e.Level, 16); WStr(w, e.Name, 24); w.Write(e.StartMs); w.Write((ushort)left); w.Write(e.HpMax); w.Write((byte)e.Boss);
                            w.Write(e.Target);
                        });
                        byte[] sg = ident.Sign(Concat(BitConverter.GetBytes(MyId), Encoding.ASCII.GetBytes(SessionCode), payload2));
                        SendMsg(MSG_EVENT, from, Build(w => { w.Write((ushort)payload2.Length); w.Write(payload2); w.Write((ushort)sg.Length); w.Write(sg); }));
                        break;
                    }
                case EV_POS:
                    {
                        // position du boss chez son pilote
                        float x = r.ReadSingle(), y = r.ReadSingle(), z = r.ReadSingle(), yaw = r.ReadSingle();
                        int act = r.ReadByte();
                        WorldEvent e = ev;
                        if (from == MyId || e == null || e.Id != id || e.Kind != EVK_BOSS || e.State != 1) return;
                        if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z) || float.IsNaN(yaw)) return;
                        bool stale = now - bossRemoteMs > 1500;
                        if (stale || from <= bossRemoteDriver || bossRemoteDriver == 0)
                        {
                            lock (lk)
                            {
                                bossRemoteDriver = from; bossRemoteMs = now;
                                if (!IDriveBoss())
                                {
                                    bossX = x; bossY = y; bossZ = z; bossYaw = yaw; bossAnim = (uint)Math.Max(0, Math.Min(5, act));
                                    bossSeq++;
                                }
                            }
                        }
                        break;
                    }
                case EV_HUNTED:
                    {
                        // la cible de la chasse a l'homme est tombee : l'autorite donne la victoire
                        uint killer = r.ReadUInt32();
                        WorldEvent e = ev;
                        if (e == null || e.Id != id || e.Kind != EVK_HUNT || e.State != 1 || from != e.Target) return;
                        if (EventAuthority() == MyId && killer != 0 && killer != e.Target) EndEvent(e, 2, killer, NameOf(killer));
                        break;
                    }
                case EV_FINISH:
                    {
                        // un joueur est arrive en haut du parcours : le premier gagne
                        WorldEvent e = ev;
                        if (e == null || e.Id != id || e.Kind != EVK_PARKOUR || e.State != 1) return;
                        if (EventAuthority() == MyId) EndEvent(e, 2, from, NameOf(from));
                        break;
                    }
            }
        }

        string ZoneText(string level)
        {
            if (level.StartsWith("hiphog")) return "Naughty Ottsel";
            if (level.StartsWith("ctyport")) return T("Haven : port", "Haven: port");
            if (level.StartsWith("ctyslum")) return T("Haven : bidonville", "Haven: slums");
            if (level.StartsWith("ctyfarm")) return T("Haven : fermes", "Haven: farms");
            if (level.StartsWith("ctyind")) return T("Haven : industrie", "Haven: industrial");
            if (level.StartsWith("ctygen")) return T("Haven : centre", "Haven: center");
            if (level.StartsWith("cty")) return "Haven City";
            if (level.StartsWith("wasstad")) return T("Arene de Spargus", "Spargus arena");
            if (level.StartsWith("wasdoor")) return T("Portes de Spargus", "Spargus gates");
            if (level.StartsWith("was")) return "Spargus";
            if (level.StartsWith("des")) return T("Desert", "Wasteland");
            for (int i = 0; i < Maps.Length; i++) if (level == Maps[i].Level) return MapName(i);
            return level;
        }

        float BossHpLeft(WorldEvent e)
        {
            float sum = 0f;
            lock (lk) foreach (float d in e.Dmg.Values) sum += d;
            return Math.Max(0f, e.HpMax - sum);
        }

        void EndEvent(WorldEvent e, int state, uint winner, string winnerName)
        {
            string top = "";
            if (e.Kind == EVK_BOSS)
            {
                List<KeyValuePair<uint, float>> l;
                lock (lk) l = new List<KeyValuePair<uint, float>>(e.Dmg);
                l.Sort((a, b) => b.Value.CompareTo(a.Value));
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < Math.Min(3, l.Count); i++) sb.Append((i + 1) + ". " + NameOf(l[i].Key) + " " + (int)l[i].Value + "  ");
                top = sb.ToString();
                if (l.Count > 0) winner = l[0].Key;
            }
            else if (ScoreKind(e.Kind))
            {
                List<KeyValuePair<uint, float>> l;
                lock (lk) l = new List<KeyValuePair<uint, float>>(e.Score);
                l.Sort((a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value) : a.Key.CompareTo(b.Key));
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < Math.Min(3, l.Count); i++) sb.Append((i + 1) + ". " + NameOf(l[i].Key) + " " + (int)l[i].Value + "s  ");
                top = sb.ToString();
                winner = l.Count > 0 && l[0].Value >= 5f ? l[0].Key : 0;
                state = winner != 0 ? 2 : 3;
            }
            else if ((e.Kind == EVK_TREASURE || e.Kind == EVK_HUNT || e.Kind == EVK_PARKOUR) && winner != 0)
                top = T("Gagne par ", "Won by ") + winnerName;
            string t = top;
            uint wn = winner;
            byte[] payload = Build(w => { w.Write((byte)EV_END); w.Write(e.Id); w.Write((byte)state); w.Write(wn); WStr(w, t, 64); });
            SignedEventSend(0, payload);
        }

        // chacun calcule ses gains a partir du resultat signe
        void EventResult(WorldEvent e)
        {
            if (e.Rewarded) return;
            e.Rewarded = true;
            string txt = "";
            long money = 0, xp = 0;
            switch (e.Kind)
            {
                case EVK_BOSS:
                    {
                        float total = 0f;
                        lock (lk) foreach (float d in e.Dmg.Values) total += d;
                        float mine;
                        lock (lk) { if (!e.Dmg.TryGetValue(MyId, out mine)) mine = 0f; }
                        mine = Math.Max(mine, myBossDmg);
                        if (e.State == 2)
                        {
                            txt = EvName(e) + T(" VAINCU !", " DEFEATED!");
                            AddChat(4, T("EVENEMENT", "EVENT"), T("Meilleurs degats : ", "Top damage: ") + e.Top);
                            if (mine >= Math.Max(3f, e.HpMax * 0.02f) && total > 0f)
                            {
                                double share = Math.Min(1.0, mine / total);
                                double big = e.Boss >= 3 ? 1.5 : 1.0;   // les vrais boss rapportent plus
                                money = (long)Math.Round((80 + 520 * share) * big);
                                xp = (long)Math.Round((150 + 850 * share) * big);
                                if (e.Winner == MyId) { money += 100; xp += 100; }
                                lock (lk) { profil.BossKills++; if (e.Winner == MyId) profil.EventsWon++; }
                            }
                        }
                        else txt = EvName(e) + T(" s'est enfui...", " got away...");
                        break;
                    }
                case EVK_TREASURE:
                    if (e.State == 2)
                    {
                        txt = T("TRESOR TROUVE par ", "TREASURE FOUND by ") + NameOf(e.Winner) + " !";
                        if (e.Winner == MyId) { money = 600; xp = 500; lock (lk) profil.EventsWon++; }
                    }
                    else txt = T("Personne n'a trouve le tresor", "Nobody found the treasure");
                    break;
                case EVK_ZONE:
                case EVK_METEOR:
                    if (e.State == 2)
                    {
                        txt = (e.Kind == EVK_ZONE ? T("ZONE CAPTUREE par ", "ZONE CAPTURED by ") : T("METEORES : le plus tenace est ", "METEORS: toughest player is "))
                            + NameOf(e.Winner) + "  " + e.Top;
                        if (e.Winner == MyId) { money = e.Kind == EVK_ZONE ? 400 : 500; xp = 400; lock (lk) profil.EventsWon++; }
                        else if (myZone >= 20f) { money = e.Kind == EVK_ZONE ? 50 : 80; xp = 100; }
                    }
                    else txt = e.Kind == EVK_ZONE ? T("Personne n'a tenu la zone", "Nobody held the zone") : T("Fin de la pluie de meteores", "Meteor shower is over");
                    break;
                case EVK_HUNT:
                    if (e.State == 2)
                    {
                        txt = T("CHASSE A L'HOMME : ", "MANHUNT: ") + NameOf(e.Winner) + T(" a eu ", " got ") + NameOf(e.Target) + " !";
                        if (e.Winner == MyId) { money = 1500; xp = 1000; lock (lk) profil.EventsWon++; }
                    }
                    else
                    {
                        txt = NameOf(e.Target) + T(" a survecu a la chasse !", " survived the manhunt!");
                        if (e.Target == MyId && !IsCreateur) { money = 400; xp = 400; }
                    }
                    break;
                case EVK_PARKOUR:
                    if (e.State == 2)
                    {
                        int mi = e.Boss >= 0 && e.Boss < Maps.Length ? e.Boss : 0;
                        txt = T("PARCOURS ", "PARKOUR ") + MapName(mi) + T(" : ", ": ") + NameOf(e.Winner) + T(" est arrive en premier !", " made it first!");
                        if (e.Winner == MyId) { money = Maps[mi].Reward; xp = Maps[mi].Xp; lock (lk) profil.EventsWon++; }
                    }
                    else txt = T("Personne n'a fini le parcours", "Nobody finished the parkour");
                    break;
                case EVK_ORBS: txt = T("Fin de la pluie d'orbes", "Orb rain is over"); break;
                case EVK_DOUBLEXP: txt = T("Fin du double XP", "Double XP is over"); break;
            }
            lock (lk) { announce = txt; announceKind = e.State == 2 ? 1 : 3; announceSeq++; }
            AddChat(4, T("EVENEMENT", "EVENT"), txt);
            if (InWorld && (money > 0 || xp > 0) && !selfCheat)
            {
                long got = profil.Gain("event", money, 5000, 1800000, xp);
                Reward((int)got, (int)xp, T("Evenement : ", "Event: ") + (e.Kind == EVK_BOSS ? EvName(e) : txt));
            }
        }

        // ---------------- le jeu m'envoie mes degats sur le boss / mes actions
        void OnMyBossDamage(float dmg)
        {
            WorldEvent e = ev;
            if (e == null || e.State != 1 || e.Kind != EVK_BOSS || dmg <= 0f || float.IsNaN(dmg)) return;
            lock (lk) { myBossDmg += Math.Min(dmg, 40f); e.Dmg[MyId] = myBossDmg; evSeq++; }
        }

        // est-ce MON jeu qui pilote le boss ? (il l'a chez lui, et personne de plus petit numero ne le pilote)
        bool IDriveBoss()
        {
            long now = Profil.NowMs();
            if (now - bossGameMs > 1000) return false;
            return bossRemoteDriver == 0 || now - bossRemoteMs > 1500 || MyId < bossRemoteDriver;
        }

        // le jeu pilote le boss : sa position part chez les autres (4 fois par seconde au plus)
        void OnGameBossPos(uint evId, float yaw, int act, float x, float y, float z)
        {
            WorldEvent e = ev;
            if (e == null || e.State != 1 || e.Kind != EVK_BOSS || e.Id != evId) return;
            if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z) || float.IsNaN(yaw)) return;
            long now = Profil.NowMs();
            bossGameMs = now;
            if (!IDriveBoss() || now - bossMySendMs < 220) return;
            bossMySendMs = now;
            byte[] payload = Build(w => { w.Write((byte)EV_POS); w.Write(evId); w.Write(x); w.Write(y); w.Write(z); w.Write(yaw); w.Write((byte)act); });
            byte[] sig = ident.Sign(Concat(BitConverter.GetBytes(MyId), Encoding.ASCII.GetBytes(SessionCode), payload));
            SendMsg(MSG_EVENT, 0, Build(w => { w.Write((ushort)payload.Length); w.Write(payload); w.Write((ushort)sig.Length); w.Write(sig); }));
        }

        // je suis tombe : si j'etais la cible de la chasse, mon tueur gagne
        void OnHuntDeath(uint killer)
        {
            WorldEvent e = ev;
            if (e == null || e.State != 1 || e.Kind != EVK_HUNT || e.Target != MyId || killer == 0 || killer == MyId || e.HuntSent) return;
            e.HuntSent = true;
            uint id = e.Id;
            byte[] payload = Build(w => { w.Write((byte)EV_HUNTED); w.Write(id); w.Write(killer); });
            SignedEventSend(0, payload);
        }

        void OnGameEventAction(int action, float x, float y, float z)
        {
            WorldEvent e = ev;
            if (e == null || e.State != 1) return;
            long now = Profil.NowMs();
            if (action == 1 && e.Kind == EVK_TREASURE && !e.FoundSent)
            {
                // le coffre est ouvert : je dois vraiment etre a cote
                if (!NearEvent(20f)) { OnSelfCheat(5); return; }
                e.FoundSent = true;
                byte[] payload = Build(w => { w.Write((byte)EV_FOUND); w.Write(e.Id); w.Write(now); });
                SignedEventSend(0, payload);
            }
            else if (action == 3 && ScoreKind(e.Kind))
            {
                // une seconde dans la zone (au plus une par seconde reelle)
                if (now - lastZoneTick >= 900 && NearEvent(e.Radius + 3f)) { lastZoneTick = now; lock (lk) myZone += 1f; evSeq++; }
            }
            else if (action == 5 && e.Kind == EVK_PARKOUR && !e.FoundSent && e.Boss >= 0 && e.Boss < Maps.Length)
            {
                // arrive en haut du parcours : je dois vraiment y etre
                ParkourMap pm = Maps[e.Boss];
                float mx, my, mz;
                if (!MyPos(out mx, out my, out mz)) return;
                double d = Math.Sqrt((mx - pm.FX) * (mx - pm.FX) + (my - pm.FY) * (my - pm.FY) + (mz - pm.FZ) * (mz - pm.FZ)) / 4096.0;
                if (d > 25.0) { L("parcours : arrivee refusee (" + (int)d + " m)"); return; }
                e.FoundSent = true;
                byte[] payload = Build(w => { w.Write((byte)EV_FINISH); w.Write(e.Id); w.Write(now); });
                SignedEventSend(0, payload);
            }
        }

        // ---------------- boucle (1 fois par seconde)
        void EventTick()
        {
            if (SessionId == 0) { ev = null; return; }
            long now = Profil.NowMs();
            WorldEvent e = ev;
            bool authority = EventAuthority() == MyId;
            // l'autorite lance un evenement de temps en temps (monde en ligne)
            if (InWorld && authority)
            {
                DateTime dn = DateTime.UtcNow;
                if (nextEvent == DateTime.MinValue)
                {
                    bool soon = Environment.GetEnvironmentVariable("JAK3ONLINE_EVENTS_SOON") == "1";
                    nextEvent = dn.AddSeconds(soon ? 25 : 180);
                }
                if ((e == null || e.State != 1) && dn >= nextEvent && GameAttached && (uiFlags & 2) != 0)
                {
                    nextEvent = dn.AddSeconds(360 + evRng.Next(180));
                    StartRandomEvent();
                }
            }
            if (e == null) return;
            if (e.State == 1)
            {
                // chasse a l'homme : le point suit la cible
                if (e.Kind == EVK_HUNT)
                {
                    float hx, hy, hz;
                    if (PlayerPos(e.Target, out hx, out hy, out hz)) { e.X = hx; e.Y = hy; e.Z = hz; }
                    // la cible a quitte la session : fin de la chasse
                    if (authority && e.Target != MyId)
                    {
                        bool here;
                        lock (lk) here = members.ContainsKey(e.Target);
                        if (!here) EndEvent(e, 3, 0, "");
                    }
                }
                // mes degats sur le boss : annonces a tout le monde
                if (e.Kind == EVK_BOSS && myBossDmg != lastDmgSent && (DateTime.UtcNow - lastDmgSend).TotalMilliseconds >= 900)
                {
                    lastDmgSend = DateTime.UtcNow;
                    lastDmgSent = myBossDmg;
                    float d = myBossDmg;
                    uint id = e.Id;
                    byte[] payload = Build(w => { w.Write((byte)EV_DMG); w.Write(id); w.Write(d); });
                    SignedEventSend(0, payload);
                }
                // zone / meteores : scores envoyes 3 s avant la fin
                if (ScoreKind(e.Kind) && !e.ScoreSent && now > e.EndMs - 3000)
                {
                    e.ScoreSent = true;
                    float sc = myZone;
                    uint id = e.Id;
                    byte[] payload = Build(w => { w.Write((byte)EV_SCORE); w.Write(id); w.Write(sc); });
                    SignedEventSend(0, payload);
                }
                if (authority)
                {
                    if (e.Kind == EVK_BOSS && BossHpLeft(e) <= 0f) EndEvent(e, 2, 0, "");
                    else if (now > e.EndMs + (ScoreKind(e.Kind) ? 1500 : 0)) EndEvent(e, e.Kind == EVK_ORBS || e.Kind == EVK_DOUBLEXP ? 2 : 3, 0, "");
                }
                else if (now > e.EndMs + 20000)
                {
                    // l'autorite a disparu : l'evenement s'arrete tout seul
                    lock (lk) { e.State = 3; e.ResultMs = now; evSeq++; }
                    EventResult(e);
                }
            }
            else if (now - e.ResultMs > 12000) { lock (lk) { ev = null; evSeq++; } }
        }

        // un joueur arrive : il demande l'evenement en cours
        void EventSyncRequest()
        {
            byte[] payload = Build(w => { w.Write((byte)EV_SYNC); w.Write(0u); });
            byte[] sig = ident.Sign(Concat(BitConverter.GetBytes(MyId), Encoding.ASCII.GetBytes(SessionCode), payload));
            SendMsg(MSG_EVENT, 0, Build(w => { w.Write((ushort)payload.Length); w.Write(payload); w.Write((ushort)sig.Length); w.Write(sig); }));
        }

        // ---------------- zone EXT (pour le jeu)
        void WriteEventExt(byte[] img, int b)
        {
            WorldEvent e = ev;
            PutU32(img, b + Ext.EvSeq, evSeq);
            if (e == null) return;
            long now = Profil.NowMs();
            PutU32(img, b + Ext.EvKind, (uint)e.Kind);
            PutU32(img, b + Ext.EvState, (uint)e.State);
            PutF32(img, b + Ext.EvTimer, e.State == 1 ? Math.Max(0f, (e.EndMs - now) / 1000f) : (now - e.ResultMs) / 1000f);
            PutF32(img, b + Ext.EvPos, e.X);
            PutF32(img, b + Ext.EvPos + 4, e.Y);
            PutF32(img, b + Ext.EvPos + 8, e.Z);
            PutF32(img, b + Ext.EvHp, e.Kind == EVK_BOSS && e.HpMax > 0f ? BossHpLeft(e) / e.HpMax : 0f);
            PutF32(img, b + Ext.EvMyDmg, myBossDmg);
            int count;
            lock (lk) count = ScoreKind(e.Kind) ? e.Score.Count : e.Dmg.Count;
            PutU32(img, b + Ext.EvCount, (uint)count);
            PutU32(img, b + Ext.EvId, e.Id);
            PutStr(img, b + Ext.EvName, EvName(e), 32);
            PutStr(img, b + Ext.EvLevel, e.Level, 16);
            PutF32(img, b + Ext.EvRadius, e.Radius * 4096f);
            PutU32(img, b + Ext.EvBoss, (uint)e.Boss);
            PutF32(img, b + Ext.EvScore, myZone);
            PutStr(img, b + Ext.EvTop, e.Top, 64);
            PutU32(img, b + Ext.EvTarget, e.Target);
            PutF32(img, b + Ext.EvDur, e.Dur);
            // boss synchronise
            lock (lk)
            {
                PutU32(img, b + Ext.BossSeq, bossSeq);
                PutU32(img, b + Ext.BossDriver, (uint)(e.Kind == EVK_BOSS && IDriveBoss() ? 1 : 0));
                PutU32(img, b + Ext.BossAnim, bossAnim);
                PutF32(img, b + Ext.BossPos, bossX);
                PutF32(img, b + Ext.BossPos + 4, bossY);
                PutF32(img, b + Ext.BossPos + 8, bossZ);
                PutF32(img, b + Ext.BossPos + 12, bossYaw);
            }
        }

        // ---------------- raccourcis de test
        public void TestStartEvent(int kind, int variant = -1)
        {
            float x, y, z;
            if (!MyPos(out x, out y, out z)) return;
            StartEventAt(kind, x, y, z, MyLevelName(), variant);
        }
        public int TestEventKind { get { WorldEvent e = ev; return e == null ? 0 : e.Kind; } }
        public int TestEventState { get { WorldEvent e = ev; return e == null ? 0 : e.State; } }
        public uint TestEventWinner { get { WorldEvent e = ev; return e == null ? 0 : e.Winner; } }
        public float TestBossHp { get { WorldEvent e = ev; return e == null || e.HpMax <= 0 ? 0f : BossHpLeft(e) / e.HpMax; } }
        public float TestMyBossDmg { get { return myBossDmg; } }
        public bool TestBossDriver { get { return IDriveBoss(); } }
        public uint TestBossSeq { get { return bossSeq; } }
    }
}
