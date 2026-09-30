// ------------------------------------------------------------------------
//  Joueur 2 local ("ecran partage") : un 2e jeu dans une 2e fenetre, avec sa
//  propre manette, son propre profil, qui rejoint automatiquement la meme
//  session que le joueur 1. Sert aussi a tester le mode en ligne tout seul.
// ------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Jak3Online
{
    partial class Client
    {
        public bool IsLocalJ2;                 // ce client est le joueur 2 local (jamais createur)
        public volatile int LocalJ2Flags;      // EXT local-j2 : 1 je suis J2, 2 un J2 local tourne, 4 fenetres cote a cote, bits 8-15 manette+1
        LocalJ2 j2;

        public bool LocalJ2Running { get { LocalJ2 x = j2; return x != null && x.Running; } }
        public Client LocalJ2Client { get { LocalJ2 x = j2; return x != null ? x.C : null; } }
        public Process LocalJ2Game { get { LocalJ2 x = j2; return x != null ? x.Game : null; } }

        // demande du jeu ou de la fenetre : 1 = lancer, 0 = arreter, 2 = basculer
        public void LocalJ2Request(int what)
        {
            if (IsLocalJ2) return;
            bool on = LocalJ2Running;
            if (what == 2) what = on ? 0 : 1;
            if (what == 1 && !on)
            {
                LocalJ2 x = new LocalJ2(this);
                j2 = x;
                ThreadPool.QueueUserWorkItem(delegate { x.Run(); });
            }
            else if (what == 0 && j2 != null)
            {
                LocalJ2 x = j2;
                j2 = null;
                ThreadPool.QueueUserWorkItem(delegate { x.Stop(true); });
            }
        }

        public void StopLocalJ2() { LocalJ2 x = j2; j2 = null; if (x != null) x.Stop(true); }
    }

    class LocalJ2
    {
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
        [DllImport("user32.dll")] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder sb, int n);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        delegate bool EnumProc(IntPtr h, IntPtr p);

        public static int PadWanted = -1;      // manette choisie pour J2 (-1 = automatique)
        public static bool SideBySide = true;  // fenetres cote a cote

        readonly Client main;
        public Client C;
        Process game;
        volatile bool stop;

        public LocalJ2(Client main) { this.main = main; }

        public bool Running { get { return !stop; } }
        public Process Game { get { return game; } }

        public static string Root
        {
            get
            {
                string over = Environment.GetEnvironmentVariable("JAK3ONLINE_J2DIR");
                if (!string.IsNullOrEmpty(over)) return over;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jak3Online", "j2");
            }
        }

        // dossier "data" du mod (le programme est dans data\online)
        public static string ModData
        {
            get
            {
                string over = Environment.GetEnvironmentVariable("JAK3ONLINE_MOD");
                if (!string.IsNullOrEmpty(over)) return Path.Combine(over, "data");
                return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
            }
        }
        public static string ModRoot { get { return Path.GetFullPath(Path.Combine(ModData, "..")); } }

        void Say(string fr, string en) { main.PushFeedPublic(main.French ? fr : en); }

        static void Cmd(string args)
        {
            ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c " + args);
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            using (Process p = Process.Start(psi)) p.WaitForExit(15000);
        }

        // dossier du 2e jeu : des liens (jonctions) vers les fichiers du mod, sauf "online" et "log"
        // qui lui sont propres (sinon les deux jeux parleraient au meme programme)
        static string PrepareData()
        {
            string data = Path.Combine(Root, "data");
            string mod = ModData;
            string mark = Path.Combine(data, "mod.txt");
            Directory.CreateDirectory(data);
            string old = File.Exists(mark) ? File.ReadAllText(mark).Trim() : "";
            foreach (string d in Directory.GetDirectories(mod))
            {
                string n = Path.GetFileName(d);
                string ln = n.ToLowerInvariant();
                if (ln == "online" || ln == "log") continue;
                string link = Path.Combine(data, n);
                // le mod a change de place : on retire seulement le lien (rmdir sans /s ne touche pas au mod)
                if (Directory.Exists(link) && !string.Equals(old, mod, StringComparison.OrdinalIgnoreCase)) Cmd("rmdir \"" + link + "\"");
                if (!Directory.Exists(link)) Cmd("mklink /J \"" + link + "\" \"" + d + "\"");
            }
            File.WriteAllText(mark, mod);
            Directory.CreateDirectory(Path.Combine(data, "online", "bridge"));
            Directory.CreateDirectory(Path.Combine(data, "log"));
            return data;
        }

        // premiere fois : le J2 recoit une copie des sauvegardes et des reglages du joueur 1
        static string PrepareConfig()
        {
            string cfg = Path.Combine(Root, "cfg");
            string mine = Path.Combine(cfg, "OpenGOAL", "jak3");
            string j1 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenGOAL", "jak3");
            Directory.CreateDirectory(Path.Combine(mine, "settings"));
            try
            {
                string saves = Path.Combine(j1, "saves");
                if (Directory.Exists(saves))
                    foreach (string f in Directory.GetFiles(saves, "*", SearchOption.AllDirectories))
                    {
                        string rel = f.Substring(saves.Length).TrimStart('\\', '/');
                        string to = Path.Combine(Path.Combine(mine, "saves"), rel);
                        if (!File.Exists(to)) { Directory.CreateDirectory(Path.GetDirectoryName(to)); File.Copy(f, to); }
                    }
                string pcs = Path.Combine(j1, "settings", "pc-settings.gc");
                string myPcs = Path.Combine(mine, "settings", "pc-settings.gc");
                if (File.Exists(pcs) && !File.Exists(myPcs)) File.Copy(pcs, myPcs);
            }
            catch (Exception) { }
            return cfg;
        }

        // la sauvegarde la plus recente du J2 (emplacements 1 a 4)
        static int LatestSlot(string cfg)
        {
            int best = 0;
            DateTime bt = DateTime.MinValue;
            try
            {
                string saves = Path.Combine(cfg, "OpenGOAL", "jak3", "saves");
                if (Directory.Exists(saves))
                    foreach (string f in Directory.GetFiles(saves, "bank*.bin", SearchOption.AllDirectories))
                    {
                        string n = Path.GetFileNameWithoutExtension(f);
                        int k;
                        if (!int.TryParse(n.Substring(4), out k) || k < 0 || k > 3) continue;
                        DateTime t = File.GetLastWriteTimeUtc(f);
                        if (t > bt) { bt = t; best = k + 1; }
                    }
            }
            catch (Exception) { }
            return best;
        }

        static IntPtr WindowOf(int pid)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate (IntPtr h, IntPtr p)
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder(256);
                GetWindowText(h, sb, 256);
                uint wp;
                GetWindowThreadProcessId(h, out wp);
                if (IsWindowVisible(h) && sb.ToString().StartsWith("OpenGOAL") && (int)wp == pid) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        IntPtr MainGameWindow()
        {
            int mine = game != null ? game.Id : -1;
            foreach (Process gp in Process.GetProcessesByName("gk"))
                if (gp.Id != mine) { IntPtr h = WindowOf(gp.Id); if (h != IntPtr.Zero) return h; }
            return IntPtr.Zero;
        }

        // J1 a gauche, J2 a droite (chaque jeu passe d'abord en mode fenetre)
        void Arrange()
        {
            if (!SideBySide) return;
            try
            {
                System.Drawing.Rectangle wa = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
                int w = wa.Width / 2, h = Math.Min(wa.Height, w * 9 / 16 + 40);
                int y = wa.Top + (wa.Height - h) / 2;
                IntPtr h1 = MainGameWindow();
                IntPtr h2 = game != null ? WindowOf(game.Id) : IntPtr.Zero;
                const uint NOZ = 0x0004 | 0x0010; // SWP_NOZORDER | SWP_NOACTIVATE
                if (h1 != IntPtr.Zero) SetWindowPos(h1, IntPtr.Zero, wa.Left, y, w, h, NOZ);
                if (h2 != IntPtr.Zero) SetWindowPos(h2, IntPtr.Zero, wa.Left + w, y, w, h, NOZ);
            }
            catch (Exception) { }
        }

        void UpdateFlags()
        {
            int pad = PadWanted;
            if (pad < 0)
            {
                // automatique : une autre manette que celle du joueur 1
                byte[] g = main.TestGameHeader();
                int j1pad = g != null ? g[Shm.Local + 88 + 32] - 1 : -1;
                int count = g != null ? g[Shm.Local + 88 + 33] : 0;
                pad = count >= 2 ? (j1pad == 0 ? 1 : 0) : 0;
            }
            int side = SideBySide ? 4 : 0;
            main.LocalJ2Flags = 2 | side;
            Client c = C;
            if (c != null) c.LocalJ2Flags = 1 | side | ((pad + 1) << 8);
        }

        public void Run()
        {
            try
            {
                string gk = Path.Combine(ModRoot, "gk.exe");
                if (!File.Exists(gk)) { Say("Joueur 2 : gk.exe introuvable", "Player 2: gk.exe not found"); stop = true; return; }
                Say("Joueur 2 local : preparation...", "Local player 2: preparing...");
                string data = PrepareData();
                string cfg = PrepareConfig();
                Client c = new Client();
                c.IsLocalJ2 = true;
                c.ProfileDir = Path.Combine(SafeStore.Dir(), "j2");
                Directory.CreateDirectory(c.ProfileDir);
                c.BridgeDir = Path.Combine(data, "online", "bridge");
                string nm = main.MyName ?? "Joueur";
                if (nm.Length > 13) nm = nm.Substring(0, 13);
                c.WantedName = nm + " 2";
                c.UseRelay = main.UseRelay;
                c.ServerAddress = main.ServerAddress;
                c.RelayName = main.RelayName;
                c.French = main.French;
                C = c;
                UpdateFlags();
                c.Start();
                c.Connect();
                if (stop) { Stop(false); return; }
                // pas de fenetre de console pour le 2e jeu (seulement sa fenetre de jeu)
                game = Process.Start(new ProcessStartInfo(gk,
                    "-g jak3 --proj-path \"" + data + "\" --config-path \"" + cfg + "\" -- -boot -fakeiso") { UseShellExecute = false, CreateNoWindow = true });
                Say("Joueur 2 local : le 2e jeu demarre (2e manette)", "Local player 2: second game starting (2nd controller)");
                DateTime start = DateTime.UtcNow, lastLoad = DateTime.MinValue, lastArrange = DateTime.MinValue;
                bool loaded = false;
                int arranged = 0;
                while (!stop)
                {
                    Thread.Sleep(500);
                    if (game.HasExited) break;
                    UpdateFlags();
                    // fenetres cote a cote (plusieurs fois : le jeu change de taille en demarrant)
                    if (arranged < 4 && (DateTime.UtcNow - lastArrange).TotalSeconds > 4 && (DateTime.UtcNow - start).TotalSeconds > 6)
                    {
                        lastArrange = DateTime.UtcNow;
                        arranged++;
                        Arrange();
                    }
                    if (!c.GameAttached) continue;
                    // session privee : on charge la partie la plus recente (le monde en ligne, lui,
                    // s'ouvre tout seul depuis l'ecran titre)
                    if (!loaded)
                    {
                        byte[] g = c.TestGameHeader();
                        loaded = g != null && (BitConverter.ToUInt32(g, Shm.Local + 4) & 5) == 1 && BitConverter.ToUInt32(g, Shm.TxPose) > 0;
                        int slot = LatestSlot(cfg);
                        bool world = main.InWorld;
                        if (!loaded && !world && slot > 0 && (DateTime.UtcNow - lastLoad).TotalSeconds > 15)
                        {
                            lastLoad = DateTime.UtcNow;
                            c.TestCommand(slot);
                        }
                        if (!loaded && !world && slot > 0) continue;
                    }
                    // meme session que le joueur 1
                    Follow(c);
                }
            }
            catch (Exception ex)
            {
                Say("Joueur 2 local : erreur " + ex.Message, "Local player 2: error " + ex.Message);
            }
            Stop(false);
        }

        DateTime lastFollow = DateTime.MinValue;
        void Follow(Client c)
        {
            if ((DateTime.UtcNow - lastFollow).TotalSeconds < 3) return;
            if (c.NetState < Shm.NET_LOBBY) return;
            string want = main.SessionId != 0 ? main.SessionCode : "";
            string have = c.SessionId != 0 ? c.SessionCode : "";
            if (want == have) return;
            lastFollow = DateTime.UtcNow;
            if (have.Length > 0) { c.UiLeave(); return; }
            if (main.InWorld) c.UiJoinWorld();
            else if (want.Length > 0) c.UiJoinCode(want);
        }

        int stopped;
        public void Stop(bool killGame)
        {
            stop = true;
            if (Interlocked.Exchange(ref stopped, 1) != 0) { if (killGame) KillGame(); return; }
            main.LocalJ2Flags = 0;
            try { Client c = C; C = null; if (c != null) c.Stop(); } catch (Exception) { }
            if (killGame) KillGame();
            Say("Joueur 2 local : termine", "Local player 2: stopped");
        }

        void KillGame()
        {
            try { if (game != null && !game.HasExited) game.Kill(); } catch (Exception) { }
        }
    }
}
