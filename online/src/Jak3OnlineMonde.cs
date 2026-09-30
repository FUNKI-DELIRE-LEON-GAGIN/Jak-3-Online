// ============================================================================
//  JAK 3 EN LIGNE - le MONDE EN LIGNE (session publique facon "GTA en ligne")
//
//  * identite de chaque joueur : cle RSA gardee sur son PC (protegee par Windows).
//    Chaque message important (chat, elimination, profil) est signe : personne ne
//    peut parler ou tricher "au nom" d'un autre joueur.
//  * CREATEUR : une seule cle au monde (sur le PC du createur du mod) donne les
//    pouvoirs d'administration dans la session publique. Les autres programmes ne
//    contiennent que la partie publique de la cle : impossible de la copier.
//  * profil du monde en ligne : orbes (argent), experience, niveau, objets achetes.
//    Fichier chiffre par Windows + sceau anti-modification ; gains plafonnes.
//  * chat (touche T), menu TAB (joueurs, moderation), primes, courses, boutique.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Jak3Online
{
    // ------------------------------------------------------------------------
    //  Stockage protege par Windows (DPAPI) : lisible seulement par ce compte Windows
    // ------------------------------------------------------------------------
    static class SafeStore
    {
        static readonly byte[] Entropy = Encoding.ASCII.GetBytes("Jak3Online-Monde-v1");

        public static string Dir()
        {
            string over = Environment.GetEnvironmentVariable("JAK3ONLINE_PROFILE");
            if (!string.IsNullOrEmpty(over)) return over;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jak3Online");
        }

        public static byte[] Load(string dir, string name)
        {
            try
            {
                string f = Path.Combine(dir, name);
                if (!File.Exists(f)) return null;
                return ProtectedData.Unprotect(File.ReadAllBytes(f), Entropy, DataProtectionScope.CurrentUser);
            }
            catch (Exception) { return null; }
        }

        public static bool Save(string dir, string name, byte[] data)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string f = Path.Combine(dir, name);
                string tmp = f + ".tmp";
                File.WriteAllBytes(tmp, ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser));
                if (File.Exists(f)) File.Replace(tmp, f, null);
                else File.Move(tmp, f);
                return true;
            }
            catch (Exception) { return false; }
        }
    }

    // ------------------------------------------------------------------------
    //  Identite d'un joueur (RSA 1024, SHA-256)
    // ------------------------------------------------------------------------
    class Identity
    {
        public readonly RSACryptoServiceProvider Rsa;
        public readonly byte[] Pub;
        public readonly ulong Uid;

        static RSACryptoServiceProvider NewRsa(int bits)
        {
            // fournisseur "AES" (24) : obligatoire pour les signatures SHA-256
            CspParameters cp = new CspParameters(24);
            cp.Flags = CspProviderFlags.CreateEphemeralKey;
            RSACryptoServiceProvider r = bits > 0 ? new RSACryptoServiceProvider(bits, cp) : new RSACryptoServiceProvider(cp);
            r.PersistKeyInCsp = false;
            return r;
        }

        public Identity(string dir)
        {
            byte[] blob = dir != null ? SafeStore.Load(dir, "identite.dat") : null;
            Rsa = NewRsa(blob == null ? 1024 : 0);
            bool ok = false;
            if (blob != null)
            {
                try { Rsa.ImportCspBlob(blob); ok = true; } catch (Exception) { ok = false; }
            }
            if (!ok)
            {
                if (blob != null) Rsa = NewRsa(1024);
                if (dir != null) SafeStore.Save(dir, "identite.dat", Rsa.ExportCspBlob(true));
            }
            Pub = Rsa.ExportCspBlob(false);
            Uid = UidOf(Pub);
        }

        public static ulong UidOf(byte[] pub)
        {
            using (SHA256 h = SHA256.Create())
            {
                byte[] d = h.ComputeHash(pub);
                ulong u = BitConverter.ToUInt64(d, 0);
                return u == 0 ? 1UL : u;
            }
        }

        public byte[] Sign(byte[] data)
        {
            lock (Rsa) return Rsa.SignData(data, "SHA256");
        }

        static readonly Dictionary<ulong, RSACryptoServiceProvider> cache = new Dictionary<ulong, RSACryptoServiceProvider>();

        public static bool Verify(byte[] pub, byte[] data, byte[] sig)
        {
            if (pub == null || sig == null || data == null) return false;
            try
            {
                ulong k = UidOf(pub);
                RSACryptoServiceProvider r;
                lock (cache)
                {
                    if (!cache.TryGetValue(k, out r))
                    {
                        if (cache.Count > 400) cache.Clear();
                        r = NewRsa(0);
                        r.ImportCspBlob(pub);
                        cache[k] = r;
                    }
                }
                lock (r) return r.VerifyData(data, "SHA256", sig);
            }
            catch (Exception) { return false; }
        }

        public static string UidText(ulong u) { return u.ToString("X16"); }
    }

    // ------------------------------------------------------------------------
    //  CREATEUR : la cle d'administration du monde en ligne.
    //  La partie publique est ecrite ici ; la partie privee n'existe que sur le PC
    //  du createur (%APPDATA%\Jak3Online\createur.dat, chiffree par Windows).
    // ------------------------------------------------------------------------
    static class Createur
    {
        // cle publique RSA 2048 (blob CSP en base 64) - remplie par "Jak3Online.exe --createur-init"
        public const string PublicKey = "BgIAAACkAABSU0ExAAgAAAEAAQA5OuPQHsjUX4qZpnmCGlEabdMRk2j6XZI3HweNIeRYm0JIG3PA7zS+ndkOJAWTnWyYbTPJJi6GLApUh0SEqAP1XTQBDu3/OyPVSW/6E4baDpJFG92DbAC6fWU6DbRhyuTZ52nQRMn5Xr1rfM5dJSgPXMGEWTz6FyM3ZUmX/92ouCO8W8/OYj5wrQMMF/Pda2AL8YdNrpf699YdFMkiEkLGrwuIOyum+tA4P9HMy+oTEnCo4U7S6ZkZQQH4HktzuPIRle8sG4LfdHIanYXZ32OUh5JDLOS2ffdiMtPxGlc0Th91qHrpFpQ3YRmlxIG2BIfQMHGNuFHnhW9BodTExzjA";

        static RSACryptoServiceProvider priv;
        static bool loaded;
        static byte[] pub;

        public static byte[] Pub
        {
            get
            {
                if (pub == null && PublicKey.Length > 0)
                {
                    try { pub = Convert.FromBase64String(PublicKey); } catch (Exception) { pub = null; }
                }
                return pub;
            }
        }

        // ce PC a-t-il la cle privee du createur ?
        public static bool IsMe
        {
            get
            {
                if (!loaded)
                {
                    loaded = true;
                    byte[] blob = SafeStore.Load(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jak3Online"), "createur.dat");
                    if (blob != null && Pub != null)
                    {
                        try
                        {
                            CspParameters cp = new CspParameters(24);
                            cp.Flags = CspProviderFlags.CreateEphemeralKey;
                            RSACryptoServiceProvider r = new RSACryptoServiceProvider(cp);
                            r.PersistKeyInCsp = false;
                            r.ImportCspBlob(blob);
                            // la cle privee doit correspondre a la cle publique du programme
                            byte[] test = Encoding.ASCII.GetBytes("createur-test");
                            if (Identity.Verify(Pub, test, r.SignData(test, "SHA256"))) priv = r;
                        }
                        catch (Exception) { priv = null; }
                    }
                }
                return priv != null;
            }
        }

        public static byte[] Sign(byte[] data)
        {
            if (!IsMe) return null;
            lock (priv) return priv.SignData(data, "SHA256");
        }

        public static bool Verify(byte[] data, byte[] sig)
        {
            return Pub != null && Identity.Verify(Pub, data, sig);
        }

        // ---------------- sauvegarde de la cle (pour changer de PC / reinstaller Windows)
        // Fichier chiffre AES-256 avec un mot de passe (PBKDF2 200 000 tours) + controle HMAC.
        // Sans le mot de passe, le fichier est inutilisable.
        static byte[] Kdf(string pw, byte[] salt, int n)
        {
            using (Rfc2898DeriveBytes d = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(pw), salt, 200000)) return d.GetBytes(n);
        }

        public static string Export(string path, string pw)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jak3Online");
            byte[] blob = SafeStore.Load(dir, "createur.dat");
            if (blob == null) return "Aucune cle createur sur ce PC.";
            if (!IsMe) return "La cle de ce PC ne correspond pas a la cle publique du mod.";
            byte[] salt = new byte[16], iv = new byte[16];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) { rng.GetBytes(salt); rng.GetBytes(iv); }
            byte[] k = Kdf(pw, salt, 64);
            byte[] ek = new byte[32], mk = new byte[32];
            Buffer.BlockCopy(k, 0, ek, 0, 32); Buffer.BlockCopy(k, 32, mk, 0, 32);
            byte[] ct;
            using (Aes aes = Aes.Create())
            {
                aes.Key = ek; aes.IV = iv;
                using (ICryptoTransform t = aes.CreateEncryptor()) ct = t.TransformFinalBlock(blob, 0, blob.Length);
            }
            MemoryStream ms = new MemoryStream();
            ms.Write(Encoding.ASCII.GetBytes("J3OK1"), 0, 5); ms.Write(salt, 0, 16); ms.Write(iv, 0, 16); ms.Write(ct, 0, ct.Length);
            byte[] body = ms.ToArray();
            byte[] mac; using (HMACSHA256 h = new HMACSHA256(mk)) mac = h.ComputeHash(body);
            ms.Write(mac, 0, 32);
            File.WriteAllBytes(path, ms.ToArray());
            return null;
        }

        public static string Import(string path, string pw, bool write = true)
        {
            byte[] all = File.ReadAllBytes(path);
            if (all.Length < 5 + 32 + 32 + 16 || Encoding.ASCII.GetString(all, 0, 5) != "J3OK1") return "Ce fichier n'est pas une sauvegarde de cle.";
            byte[] salt = new byte[16], iv = new byte[16];
            Buffer.BlockCopy(all, 5, salt, 0, 16); Buffer.BlockCopy(all, 21, iv, 0, 16);
            byte[] k = Kdf(pw, salt, 64);
            byte[] ek = new byte[32], mk = new byte[32];
            Buffer.BlockCopy(k, 0, ek, 0, 32); Buffer.BlockCopy(k, 32, mk, 0, 32);
            byte[] mac; using (HMACSHA256 h = new HMACSHA256(mk)) mac = h.ComputeHash(all, 0, all.Length - 32);
            for (int i = 0; i < 32; i++) if (mac[i] != all[all.Length - 32 + i]) return "Mot de passe incorrect (ou fichier abime).";
            byte[] blob;
            using (Aes aes = Aes.Create())
            {
                aes.Key = ek; aes.IV = iv;
                using (ICryptoTransform t = aes.CreateDecryptor()) blob = t.TransformFinalBlock(all, 37, all.Length - 37 - 32);
            }
            CspParameters cp = new CspParameters(24); cp.Flags = CspProviderFlags.CreateEphemeralKey;
            RSACryptoServiceProvider r = new RSACryptoServiceProvider(cp); r.PersistKeyInCsp = false;
            r.ImportCspBlob(blob);
            byte[] test = Encoding.ASCII.GetBytes("createur-test");
            if (Pub == null || !Identity.Verify(Pub, test, r.SignData(test, "SHA256"))) return "Cette cle n'est pas celle de ce mod.";
            if (!write) return null;
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jak3Online");
            if (!SafeStore.Save(dir, "createur.dat", blob)) return "Impossible d'ecrire la cle.";
            loaded = false; priv = null;
            return null;
        }

        // Jak3Online.exe --createur-init : cree la cle (une seule fois) et ecrit la partie publique
        public static string Init()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jak3Online");
            byte[] blob = SafeStore.Load(dir, "createur.dat");
            CspParameters cp = new CspParameters(24);
            cp.Flags = CspProviderFlags.CreateEphemeralKey;
            RSACryptoServiceProvider r;
            if (blob == null)
            {
                r = new RSACryptoServiceProvider(2048, cp);
                r.PersistKeyInCsp = false;
                if (!SafeStore.Save(dir, "createur.dat", r.ExportCspBlob(true))) return null;
            }
            else
            {
                r = new RSACryptoServiceProvider(cp);
                r.PersistKeyInCsp = false;
                r.ImportCspBlob(blob);
            }
            return Convert.ToBase64String(r.ExportCspBlob(false));
        }
    }

    // ------------------------------------------------------------------------
    //  Catalogue : boutique du Naughty Ottsel (armes, pouvoirs de Jak, armures) et
    //  boutique du menu SELECT (vehicules, super-pouvoirs, soins). Prix en orbes et
    //  NIVEAU requis. Les memes numeros sont utilises par le jeu (online-world.gc).
    // ------------------------------------------------------------------------
    static class Catalog
    {
        public const int KIND_FEATURE = 1, KIND_SECRET = 2, KIND_VEHICLE = 3, KIND_ITEM = 4, KIND_CONSUMABLE = 5, KIND_POWER = 6, KIND_COSM = 7;
        public const int Count = 128;
        // MAISONS (numero 0..7 = cartes 8..15 du jeu) : objet, boites aux lettres, orbes par boite et par jour
        public static readonly int[] HouseItem = { 96, 97, 98, 109, 110, 111, 112, 113 };
        public static readonly int[] HouseBoxes = { 1, 2, 3, 2, 4, 5, 7, 10 };
        public static readonly int[] HouseMailPerDay = { 150, 250, 300, 250, 450, 600, 800, 1000 };
        public const int MailDaysMax = 3;   // le courrier s'accumule au plus 3 jours
        public const int FirstPower = 64, PowerCount = 8;
        // 1.3 : ACCESSOIRES (objets 114..127, bits 0..13 de Cosm) : emplacement (0 tete, 1 visage, 2 dos) et numero
        public const int FirstCosm = 114, CosmCount = 14;
        public static readonly int[] CosmSlot = { 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 2, 2, 2, 2 };
        public static readonly int[] CosmIndex = { 1, 2, 3, 4, 5, 6, 7, 1, 2, 3, 1, 2, 3, 4 };

        public class Item
        {
            public int Id, Kind, Bit, Price, Req = -1, Level = 1;
            public string Name;
            public bool Hidden;        // plus en vente (ex. invincibilite de Dark Jak : interdite en PvP)
            public int Seconds;        // super-pouvoirs : duree
        }

        public static readonly Item[] Items = new Item[Count];

        static Item Add(int id, string name, int kind, int bit, int price, int req, int level)
        {
            Item it = new Item();
            it.Id = id; it.Name = name; it.Kind = kind; it.Bit = bit; it.Price = price; it.Req = req; it.Level = Math.Max(1, level);
            Items[id] = it;
            return it;
        }

        static Catalog()
        {
            // armes (game-feature) - boutique du Naughty Ottsel
            Add(0, "Blaster", KIND_FEATURE, 9, 40, -1, 1);
            Add(1, "Scatter Gun", KIND_FEATURE, 6, 60, -1, 1);
            Add(2, "Vulcan Fury", KIND_FEATURE, 12, 120, -1, 3);
            Add(3, "Peacemaker", KIND_FEATURE, 15, 220, -1, 6);
            Add(4, "Beam Reflexor", KIND_FEATURE, 10, 160, 0, 4);
            Add(5, "Wave Concussor", KIND_FEATURE, 7, 180, 1, 5);
            Add(6, "Arc Wielder", KIND_FEATURE, 13, 240, 2, 7);
            Add(7, "Mass Inverter", KIND_FEATURE, 16, 300, 3, 9);
            Add(8, "Gyro Burster", KIND_FEATURE, 11, 360, 4, 11);
            Add(9, "Plasmite RPG", KIND_FEATURE, 8, 380, 5, 12);
            Add(10, "Needle Lazer", KIND_FEATURE, 14, 420, 6, 14);
            Add(11, "Super Nova", KIND_FEATURE, 17, 800, 7, 20);
            // chargeurs (game-feature)
            Add(12, "Chargeur jaune +", KIND_FEATURE, 23, 50, 0, 2);
            Add(13, "Chargeur jaune ++", KIND_FEATURE, 24, 120, 12, 6);
            Add(14, "Chargeur rouge +", KIND_FEATURE, 25, 50, 1, 2);
            Add(15, "Chargeur rouge ++", KIND_FEATURE, 26, 120, 14, 6);
            Add(16, "Chargeur bleu +", KIND_FEATURE, 27, 60, 2, 4);
            Add(17, "Chargeur bleu ++", KIND_FEATURE, 28, 140, 16, 8);
            Add(18, "Chargeur noir +", KIND_FEATURE, 29, 100, 3, 7);
            Add(19, "Chargeur noir ++", KIND_FEATURE, 30, 200, 18, 12);
            // degats des armes (ameliorations, anciennement des "secrets" du jeu)
            Add(20, "Degats armes jaunes", KIND_SECRET, 45, 180, 0, 5);
            Add(21, "Degats armes rouges", KIND_SECRET, 42, 180, 1, 5);
            Add(22, "Degats armes bleues", KIND_SECRET, 48, 200, 2, 7);
            Add(23, "Degats armes noires", KIND_SECRET, 51, 260, 3, 10);
            // planche (game-feature)
            Add(24, "JetBoard", KIND_FEATURE, 18, 80, -1, 2);
            Add(25, "JetBoard : saut", KIND_FEATURE, 37, 70, 24, 3);
            Add(26, "JetBoard : trainee", KIND_FEATURE, 38, 90, 24, 4);
            Add(27, "JetBoard : eclair", KIND_FEATURE, 39, 140, 24, 6);
            // dark jak
            Add(28, "Dark Jak", KIND_FEATURE, 40, 200, -1, 5);
            Add(29, "Dark Jak : claque", KIND_FEATURE, 41, 100, 28, 6);
            Add(30, "Dark Jak : bombe", KIND_FEATURE, 42, 160, 28, 8);
            Add(31, "Dark Jak : explosion", KIND_FEATURE, 43, 240, 30, 10);
            Add(32, "Dark Jak : attaque guidee", KIND_FEATURE, 44, 200, 28, 9);
            Add(33, "Dark Jak : invincible", KIND_FEATURE, 45, 0, 28, 99).Hidden = true;
            // light jak
            Add(34, "Light Jak", KIND_FEATURE, 46, 260, -1, 8);
            Add(35, "Light Jak : soin", KIND_FEATURE, 47, 140, 34, 9);
            Add(36, "Light Jak : vol", KIND_FEATURE, 48, 220, 34, 11);
            Add(37, "Light Jak : gel du temps", KIND_FEATURE, 49, 0, 34, 99).Hidden = true;   // ralentit le monde : interdit en ligne
            Add(38, "Light Jak : bouclier", KIND_FEATURE, 50, 260, 34, 12);
            // armures
            Add(39, "Armure 1", KIND_FEATURE, 52, 120, -1, 3);
            Add(40, "Armure 2", KIND_FEATURE, 53, 240, 39, 6);
            Add(41, "Armure 3", KIND_FEATURE, 54, 360, 40, 9);
            Add(42, "Armure 4", KIND_FEATURE, 55, 500, 41, 12);
            // vehicules du desert (game-vehicles) : a appeler PARTOUT (menu SELECT)
            Add(43, "Sacré joujou", KIND_VEHICLE, 0, 150, -1, 1);
            Add(44, "Puce des dunes", KIND_VEHICLE, 3, 350, -1, 3);
            Add(45, "Requin des sables", KIND_VEHICLE, 1, 600, -1, 5);
            Add(46, "Écrabouille-tout", KIND_VEHICLE, 2, 900, -1, 8);
            Add(47, "Sprinteur", KIND_VEHICLE, 5, 1200, -1, 10);
            Add(48, "Thermofouineur", KIND_VEHICLE, 4, 1600, -1, 13);
            Add(49, "Démon de la poussière", KIND_VEHICLE, 6, 2200, -1, 16);
            Add(50, "Tapageur du désert", KIND_VEHICLE, 7, 3000, -1, 20);
            // vehicules de Haven City (bits a nous)
            Add(51, "Moto de Haven", KIND_ITEM, 0, 250, -1, 2);
            Add(52, "Voiture de Haven", KIND_ITEM, 1, 500, -1, 4);
            Add(53, "Zoomer de course", KIND_ITEM, 2, 1400, -1, 12);
            // consommables
            Add(60, "Munitions pleines", KIND_CONSUMABLE, 0, 25, -1, 1);
            Add(61, "Soin complet", KIND_CONSUMABLE, 1, 20, -1, 1);
            Add(62, "Eco noir + eco clair", KIND_CONSUMABLE, 2, 40, -1, 5);
            Add(63, "Reparer mon vehicule", KIND_CONSUMABLE, 3, 60, -1, 2);
            // SUPER-POUVOIRS (temporaires)
            Add(64, "Turbo (60 s)", KIND_POWER, 0, 120, -1, 2).Seconds = 60;
            Add(65, "Super saut (60 s)", KIND_POWER, 1, 120, -1, 3).Seconds = 60;
            Add(66, "Peau d'acier (60 s)", KIND_POWER, 2, 250, -1, 7).Seconds = 60;
            Add(67, "Fantome (2 min)", KIND_POWER, 3, 200, -1, 6).Seconds = 120;
            Add(68, "Radar (3 min)", KIND_POWER, 4, 150, -1, 4).Seconds = 180;
            Add(69, "Double XP (10 min)", KIND_POWER, 5, 400, -1, 5).Seconds = 600;
            Add(70, "Aimant a orbes (10 min)", KIND_POWER, 6, 300, -1, 5).Seconds = 600;
            Add(71, "Regeneration (60 s)", KIND_POWER, 7, 180, -1, 6).Seconds = 60;
            // consommables poses AU SOL devant soi (d'autres joueurs peuvent les ramasser !)
            Add(72, "5 vies au sol", KIND_CONSUMABLE, 4, 60, -1, 1);
            Add(73, "Caisse de munitions au sol", KIND_CONSUMABLE, 5, 40, -1, 1);
            Add(74, "ECO BLEU (3 min)", KIND_CONSUMABLE, 6, 150, -1, 3);
            // PERSONNAGES jouables (corps sur le squelette de Jak, voir online.gc) : Jak est gratuit
            Add(75, "Keira", KIND_ITEM, 8, 1500, -1, 3);
            // PERSONNAGES (objets a nous, bits 9-13)
            Add(54, "Jak 2", KIND_ITEM, 9, 1200, -1, 3);
            Add(55, "Jak 1 HD", KIND_ITEM, 10, 1000, -1, 3);
            Add(56, "Jak 4", KIND_ITEM, 11, 3000, -1, 8);
            Add(57, "Tess", KIND_ITEM, 12, 1500, -1, 3);
            Add(58, "Daxter", KIND_ITEM, 13, 500, -1, 1);
            // 1.3 : personnages de Jak 3 (bits 23..26)
            Add(105, "Ashelin", KIND_ITEM, 23, 2500, -1, 8);
            Add(106, "Torn", KIND_ITEM, 24, 2500, -1, 8);
            Add(107, "Sig", KIND_ITEM, 25, 4000, -1, 12);
            Add(108, "Samos", KIND_ITEM, 26, 5000, -1, 15);
            // MAISONS (bits 14-16) et STYLE (bits 17-21)
            // (1.3 : prix revus d'apres ce qu'on gagne en jouant : ~5 000 a 10 000 orbes par heure)
            Add(96, "Maison : cabane dans les bois", KIND_ITEM, 14, 3000, -1, 3);
            Add(97, "Maison : villa au bord de l'eau", KIND_ITEM, 15, 12000, -1, 6);
            Add(98, "Maison : chateau", KIND_ITEM, 16, 30000, -1, 10);
            // 1.3 : nouvelles maisons (bits 27..31), la plus belle a 1 000 000
            Add(109, "Maison : maison en cubes", KIND_ITEM, 27, 20000, -1, 8);
            Add(110, "Maison : manoir", KIND_ITEM, 28, 75000, -1, 14);
            Add(111, "Maison : penthouse du gratte-ciel", KIND_ITEM, 29, 150000, -1, 18);
            Add(112, "Maison : temple precurseur", KIND_ITEM, 30, 350000, -1, 22);
            Add(113, "Maison : PALAIS DE L'ILE", KIND_ITEM, 31, 1000000, -1, 25);
            Add(99, "Grosse tete", KIND_ITEM, 17, 400, -1, 2);
            Add(100, "Petite tete", KIND_ITEM, 18, 400, -1, 2);
            Add(101, "Pantalon pour Daxter", KIND_ITEM, 19, 300, -1, 1).Hidden = true;   // le jeu ne l'affiche que dans ses cinematiques
            Add(102, "Monde miroir", KIND_ITEM, 20, 250, -1, 1);
            Add(103, "Sans barbe (Jak)", KIND_ITEM, 21, 200, -1, 1);
            // VEHICULE VOLANT : le Hellcat (h-warf, repris du mod Jak 4), a appeler partout
            Add(104, "HELLCAT (vole partout)", KIND_ITEM, 22, 12000, -1, 12);
            // ACCESSOIRES (1.3) : portes sur ton personnage, TOUS les joueurs les voient ; certains
            // sont reserves aux hauts niveaux
            Add(114, "Casquette", KIND_COSM, 0, 1500, -1, 2);
            Add(115, "Chapeau de cowboy", KIND_COSM, 1, 4000, -1, 5);
            Add(116, "Haut-de-forme", KIND_COSM, 2, 8000, -1, 8);
            Add(117, "Casque de guerrier", KIND_COSM, 3, 20000, -1, 15);
            Add(118, "Cornes de demon", KIND_COSM, 4, 30000, -1, 20);
            Add(119, "Couronne d'or", KIND_COSM, 5, 100000, -1, 30);
            Add(120, "Aureole", KIND_COSM, 6, 250000, -1, 50);
            Add(121, "Lunettes de soleil", KIND_COSM, 7, 2000, -1, 3);
            Add(122, "Masque de ninja", KIND_COSM, 8, 6000, -1, 6);
            Add(123, "Masque de fer", KIND_COSM, 9, 25000, -1, 18);
            Add(124, "Sac a dos", KIND_COSM, 10, 3000, -1, 3);
            Add(125, "Cape rouge", KIND_COSM, 11, 12000, -1, 10);
            Add(126, "Ailes d'ange", KIND_COSM, 12, 150000, -1, 40);
            Add(127, "Ailes de demon", KIND_COSM, 13, 300000, -1, 60);
            // AMELIORATIONS (secrets du jeu, seulement ceux-ci : pas de triche)
            Add(76, "Capacite munitions jaunes", KIND_SECRET, 55, 300, -1, 3);
            Add(77, "Capacite munitions rouges", KIND_SECRET, 54, 300, -1, 3);
            Add(78, "Capacite munitions bleues", KIND_SECRET, 56, 350, -1, 5);
            Add(79, "Capacite munitions noires", KIND_SECRET, 57, 400, -1, 7);
            Add(80, "Turbo JetBoard dans le desert", KIND_SECRET, 29, 400, 24, 3);
            Add(81, "Vehicules renforces", KIND_SECRET, 28, 600, -1, 5);
            // TELEPORTATION (consommables : mode 10 + lieu, memes lieux que les courses)
            string[] tp = { "le Naughty Ottsel", "le port de Haven", "le bidonville", "le QG de la resistance", "le centre de Haven", "la zone industrielle",
                "les portes de Spargus", "le corral aux lezards", "l'entree du nid", "le desert (A)", "le desert (D)", "le desert (G)" };
            // le Naughty Ottsel (le point d'arrivee du monde) : gratuit
            for (int i = 0; i < tp.Length; i++) Add(82 + i, tp[i], KIND_CONSUMABLE, 10 + i, i == 0 ? 0 : i < 6 ? 30 : 60, -1, 1);
        }

        // achat a distance (hors du Naughty Ottsel) des objets de sa boutique : +50 % (comme le jeu)
        public static int PriceFor(int id, bool far) { int p = Price(id); return far && id >= 0 && id <= 42 ? p * 3 / 2 : p; }
        public static int Price(int id) { return id >= 0 && id < Catalog.Count && Items[id] != null && !Items[id].Hidden ? Items[id].Price : 0; }
        public static int ReqLevel(int id) { return id >= 0 && id < Catalog.Count && Items[id] != null ? (Items[id].Hidden ? 255 : Items[id].Level) : 255; }
    }

    // ------------------------------------------------------------------------
    //  Rangs : le niveau donne un rang (a cote du pseudo, dans le jeu)
    // ------------------------------------------------------------------------
    static class Rank
    {
        public static readonly int[] From = { 1, 5, 10, 15, 20, 30, 40, 50, 75 };
        public static readonly string[] Names = { "Recrue", "Pillard", "Chasseur", "Guerrier", "Commando", "Champion", "Heros", "Legende", "Precurseur" };
        public static readonly string[] NamesEn = { "Rookie", "Raider", "Hunter", "Warrior", "Commando", "Champion", "Hero", "Legend", "Precursor" };

        public static int Of(int level)
        {
            int r = 0;
            for (int i = 0; i < From.Length; i++) if (level >= From[i]) r = i;
            return r;
        }
    }

    // ------------------------------------------------------------------------
    //  Profil du monde en ligne (fichier chiffre + sceau anti-modification)
    // ------------------------------------------------------------------------
    class Profil
    {
        // sceau : un fichier modifie a la main (ou copie d'un autre PC) est refuse
        static readonly byte[] SealKey = Encoding.ASCII.GetBytes("J3O:monde:sceau:7f3a9c21e84b5d60:ne-pas-modifier");

        public long Money, Xp;
        public ulong Features, Secrets;
        public uint Vehicles, Items;
        public int Kills, Deaths, Missions, Races, Bounties;
        public long LastDaily;
        // v4 : super-pouvoirs (fin, en ms), statistiques
        public readonly long[] PowerUntil = new long[Catalog.PowerCount];
        public int BestStreak, VehKills, BossKills, EventsWon;
        public int Perso;              // v5 : personnage joue (0 = Jak)
        public ulong Cosm;             // v7 : accessoires achetes (bits 0..13 = objets 114..127)
        public int Wear;               // v7 : accessoires portes (octet 0 tete, 1 visage, 2 dos : numero, 0 = rien)
        public readonly long[] MailLast = new long[8 * 16];   // v6 : derniere releve de chaque boite aux lettres (ms)
        public long TotalEarned;
        public int XpMul = 1;          // Double XP (pouvoir ou evenement) : fixe par Client
        public readonly Dictionary<int, long> TaskReward = new Dictionary<int, long>();
        public readonly HashSet<uint> UsedNonces = new HashSet<uint>();
        public readonly HashSet<ulong> BanList = new HashSet<ulong>();   // (createur) joueurs bannis
        public readonly Dictionary<ulong, string> BanNames = new Dictionary<ulong, string>();
        public long BannedUntil;   // ce joueur a ete banni de la session publique

        readonly string dir;
        readonly object lk = new object();
        bool dirty;
        DateTime lastSave = DateTime.MinValue;

        // fenetres de gains (anti-triche)
        readonly Dictionary<string, Queue<KeyValuePair<long, long>>> windows = new Dictionary<string, Queue<KeyValuePair<long, long>>>();

        public Profil(string directory)
        {
            dir = directory;
            if (dir != null) Load();
        }

        public static long NowMs() { return (DateTime.UtcNow.Ticks - 621355968000000000L) / 10000L; }

        public static int LevelOf(long xp)
        {
            // niveau n (1..999) : il faut 50*k*(k+1) points d'experience pour passer k niveaux
            int k = 0;
            while (k < 998 && 50L * (k + 1) * (k + 2) <= xp) k++;
            return k + 1;
        }

        public static long XpForLevel(int level)
        {
            long k = Math.Max(0, level - 1);
            return 50L * k * (k + 1);
        }

        public int Level { get { lock (lk) return LevelOf(Xp); } }

        byte[] Serialize()
        {
            MemoryStream ms = new MemoryStream();
            BinaryWriter w = new BinaryWriter(ms);
            w.Write((int)7);
            w.Write(Money); w.Write(Xp); w.Write(Features); w.Write(Secrets); w.Write(Vehicles); w.Write(Items);
            w.Write(Kills); w.Write(Deaths); w.Write(Missions); w.Write(Races); w.Write(Bounties);
            w.Write(LastDaily); w.Write(BannedUntil);
            w.Write(TaskReward.Count);
            foreach (KeyValuePair<int, long> kv in TaskReward) { w.Write(kv.Key); w.Write(kv.Value); }
            w.Write(UsedNonces.Count);
            foreach (uint n in UsedNonces) w.Write(n);
            w.Write(BanList.Count);
            foreach (ulong u in BanList) { w.Write(u); string nm; BanNames.TryGetValue(u, out nm); w.Write(nm ?? ""); }
            // v4
            w.Write(PowerUntil.Length);
            foreach (long t in PowerUntil) w.Write(t);
            w.Write(BestStreak); w.Write(VehKills); w.Write(BossKills); w.Write(EventsWon); w.Write(TotalEarned);
            w.Write(Perso);
            // v6
            w.Write(MailLast.Length);
            foreach (long t in MailLast) w.Write(t);
            // v7
            w.Write(Cosm); w.Write(Wear);
            w.Flush();
            byte[] body = ms.ToArray();
            byte[] mac;
            using (HMACSHA256 h = new HMACSHA256(SealKey)) mac = h.ComputeHash(body);
            byte[] all = new byte[body.Length + 32];
            Buffer.BlockCopy(body, 0, all, 0, body.Length);
            Buffer.BlockCopy(mac, 0, all, body.Length, 32);
            return all;
        }

        void Load()
        {
            byte[] all = SafeStore.Load(dir, "profil.dat");
            if (all == null || all.Length < 40) return;
            byte[] body = new byte[all.Length - 32];
            Buffer.BlockCopy(all, 0, body, 0, body.Length);
            byte[] mac;
            using (HMACSHA256 h = new HMACSHA256(SealKey)) mac = h.ComputeHash(body);
            for (int i = 0; i < 32; i++) if (mac[i] != all[body.Length + i]) return;   // modifie : profil ignore
            try
            {
                BinaryReader r = new BinaryReader(new MemoryStream(body));
                int ver = r.ReadInt32();
                if (ver < 3 || ver > 7) return;
                Money = r.ReadInt64(); Xp = r.ReadInt64(); Features = r.ReadUInt64(); Secrets = r.ReadUInt64();
                Vehicles = r.ReadUInt32(); Items = r.ReadUInt32();
                Kills = r.ReadInt32(); Deaths = r.ReadInt32(); Missions = r.ReadInt32(); Races = r.ReadInt32(); Bounties = r.ReadInt32();
                LastDaily = r.ReadInt64(); BannedUntil = r.ReadInt64();
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++) { int k = r.ReadInt32(); TaskReward[k] = r.ReadInt64(); }
                n = r.ReadInt32();
                for (int i = 0; i < n; i++) UsedNonces.Add(r.ReadUInt32());
                n = r.ReadInt32();
                for (int i = 0; i < n; i++) { ulong u = r.ReadUInt64(); BanList.Add(u); BanNames[u] = r.ReadString(); }
                if (ver >= 4)
                {
                    n = r.ReadInt32();
                    for (int i = 0; i < n; i++) { long t = r.ReadInt64(); if (i < PowerUntil.Length) PowerUntil[i] = t; }
                    BestStreak = r.ReadInt32(); VehKills = r.ReadInt32(); BossKills = r.ReadInt32(); EventsWon = r.ReadInt32();
                    TotalEarned = r.ReadInt64();
                }
                if (ver >= 5) Perso = r.ReadInt32();
                if (ver >= 6)
                {
                    n = r.ReadInt32();
                    for (int i = 0; i < n; i++) { long t = r.ReadInt64(); if (i < MailLast.Length) MailLast[i] = t; }
                }
                if (ver >= 7) { Cosm = r.ReadUInt64(); Wear = r.ReadInt32(); }
            }
            catch (Exception) { }
            // valeurs impossibles : on borne
            if (Money < 0) Money = 0;
            if (Money > 9999999) Money = 9999999;
            if (Xp < 0) Xp = 0;
        }

        public void Save(bool force)
        {
            lock (lk)
            {
                if (dir == null || (!dirty && !force)) return;
                if (!force && (DateTime.UtcNow - lastSave).TotalSeconds < 3) return;
                SafeStore.Save(dir, "profil.dat", Serialize());
                dirty = false;
                lastSave = DateTime.UtcNow;
            }
        }

        public void Touch() { lock (lk) dirty = true; }

        // gain plafonne : "max" par "perMs" pour cette source ; renvoie ce qui a ete accorde
        public long Gain(string source, long amount, long max, long perMs, long xp)
        {
            if (amount <= 0 && xp <= 0) return 0;
            lock (lk)
            {
                long now = NowMs();
                Queue<KeyValuePair<long, long>> q;
                if (!windows.TryGetValue(source, out q)) { q = new Queue<KeyValuePair<long, long>>(); windows[source] = q; }
                while (q.Count > 0 && now - q.Peek().Key > perMs) q.Dequeue();
                long used = 0;
                foreach (KeyValuePair<long, long> kv in q) used += kv.Value;
                long give = Math.Max(0, Math.Min(amount, max - used));
                if (amount > 0 && give <= 0) return 0;
                if (give > 0) q.Enqueue(new KeyValuePair<long, long>(now, give));
                Money = Math.Min(9999999, Money + give);
                // l'experience suit la meme proportion
                long gx = amount > 0 ? (long)Math.Round(xp * (double)give / amount) : xp;
                gx *= Math.Max(1, XpMul);
                Xp = Math.Min(999999999, Xp + gx);
                TotalEarned += give;
                dirty = true;
                return amount > 0 ? give : gx;
            }
        }

        public bool Owns(int id)
        {
            Catalog.Item it = id >= 0 && id < Catalog.Count ? Catalog.Items[id] : null;
            if (it == null) return false;
            lock (lk)
            {
                switch (it.Kind)
                {
                    case Catalog.KIND_FEATURE: return (Features & (1UL << it.Bit)) != 0;
                    case Catalog.KIND_SECRET: return (Secrets & (1UL << it.Bit)) != 0;
                    case Catalog.KIND_VEHICLE: return (Vehicles & (1u << it.Bit)) != 0;
                    case Catalog.KIND_ITEM: return (Items & (1u << it.Bit)) != 0;
                    case Catalog.KIND_COSM: return (Cosm & (1UL << it.Bit)) != 0;
                }
            }
            return false;
        }

        // 0 ok, 1 inconnu, 2 deja achete, 3 il faut d'abord l'objet requis, 4 pas assez d'orbes, 5 niveau trop bas
        public int Buy(int id, bool far = false)
        {
            Catalog.Item it = id >= 0 && id < Catalog.Count ? Catalog.Items[id] : null;
            if (it == null || it.Hidden) return 1;
            if (it.Kind != Catalog.KIND_CONSUMABLE && it.Kind != Catalog.KIND_POWER && Owns(id)) return 2;
            if (it.Req >= 0 && !Owns(it.Req)) return 3;
            if (Level < it.Level) return 5;
            lock (lk)
            {
                int price = Catalog.PriceFor(id, far);
                if (Money < price) return 4;
                Money -= price;
                switch (it.Kind)
                {
                    case Catalog.KIND_FEATURE: Features |= 1UL << it.Bit; break;
                    case Catalog.KIND_SECRET: Secrets |= 1UL << it.Bit; break;
                    case Catalog.KIND_VEHICLE: Vehicles |= 1u << it.Bit; break;
                    case Catalog.KIND_ITEM: Items |= 1u << it.Bit; break;
                    case Catalog.KIND_COSM:
                        Cosm |= 1UL << it.Bit;
                        // porte tout de suite (a la place de l'accessoire du meme emplacement)
                        {
                            int c = id - Catalog.FirstCosm, sh = Catalog.CosmSlot[c] * 8;
                            Wear = (Wear & ~(0xff << sh)) | (Catalog.CosmIndex[c] << sh);
                        }
                        break;
                    case Catalog.KIND_POWER:
                        {
                            long now = NowMs();
                            PowerUntil[it.Bit] = Math.Max(now, PowerUntil[it.Bit]) + it.Seconds * 1000L;
                            // on ne cumule pas plus de 3 fois la duree
                            PowerUntil[it.Bit] = Math.Min(PowerUntil[it.Bit], now + it.Seconds * 3000L);
                            break;
                        }
                }
                dirty = true;
            }
            Save(true);
            return 0;
        }

        // BOITE AUX LETTRES : orbes accumules depuis la derniere releve (au plus 3 jours) ; la 1re
        // releve d'une boite rapporte une journee. Renvoie ce qui a ete donne (0 : boite vide).
        public long CollectMail(int house, int box)
        {
            if (house < 0 || house >= Catalog.HouseItem.Length || box < 0 || box >= Catalog.HouseBoxes[house]) return 0;
            if (!Owns(Catalog.HouseItem[house])) return 0;
            lock (lk)
            {
                long now = NowMs();
                int slot = house * 16 + box;
                long last = MailLast[slot];
                const long day = 24L * 3600L * 1000L;
                if (last <= 0 || last > now) last = now - day;
                double days = Math.Min(Catalog.MailDaysMax, (now - last) / (double)day);
                long give = (long)Math.Floor(days * Catalog.HouseMailPerDay[house]);
                if (give < 1) return 0;
                // on garde la fraction non versee (pas de perte en relevant souvent)
                long used = (long)Math.Ceiling(give / (double)Catalog.HouseMailPerDay[house] * day);
                MailLast[slot] = days >= Catalog.MailDaysMax ? now : Math.Min(now, last + used);
                Money = Math.Min(9999999, Money + give);
                Xp = Math.Min(999999999, Xp + give / 10);
                TotalEarned += give;
                dirty = true;
                return give;
            }
        }

        // courrier en attente dans une boite (orbes)
        public long MailWaiting(int house, int box)
        {
            if (house < 0 || house >= Catalog.HouseItem.Length || box < 0 || box >= Catalog.HouseBoxes[house]) return 0;
            lock (lk)
            {
                long now = NowMs();
                long last = MailLast[house * 16 + box];
                const long day = 24L * 3600L * 1000L;
                if (last <= 0 || last > now) last = now - day;
                return (long)Math.Floor(Math.Min(Catalog.MailDaysMax, (now - last) / (double)day) * Catalog.HouseMailPerDay[house]);
            }
        }

        // personnage joue : Jak (0) toujours, les autres s'ils sont achetes
        public static readonly int[] PersoItem = { -1, 75, 54, 55, 56, 57, 58, 105, 106, 107, 108 };
        public bool SetPerso(int k)
        {
            if (k < 0 || k >= PersoItem.Length) return false;
            if (k > 0 && !Owns(PersoItem[k])) return false;
            lock (lk) { Perso = k; dirty = true; }
            Save(true);
            return true;
        }

        // ACCESSOIRES portes : seulement des accessoires achetes (sinon : rien a cet emplacement)
        public static int CosmItem(int slot, int index)
        {
            for (int c = 0; c < Catalog.CosmCount; c++)
                if (Catalog.CosmSlot[c] == slot && Catalog.CosmIndex[c] == index) return Catalog.FirstCosm + c;
            return -1;
        }
        public int WearChecked()
        {
            int w = 0;
            for (int slot = 0; slot < 3; slot++)
            {
                int idx = (Wear >> (slot * 8)) & 0xff;
                int it = CosmItem(slot, idx);
                if (it >= 0 && Owns(it)) w |= idx << (slot * 8);
            }
            return w;
        }
        public void SetWear(int w)
        {
            lock (lk) { Wear = w & 0xffffff; dirty = true; }
            Wear = WearChecked();
            Save(true);
        }

        // secondes restantes d'un super-pouvoir
        public float PowerLeft(int bit)
        {
            lock (lk)
            {
                if (bit < 0 || bit >= PowerUntil.Length) return 0f;
                long left = PowerUntil[bit] - NowMs();
                return left > 0 ? left / 1000f : 0f;
            }
        }

        public bool TaskRewardAllowed(int task, long cooldownMs)
        {
            lock (lk)
            {
                long now = NowMs(), t;
                if (TaskReward.TryGetValue(task, out t) && now - t < cooldownMs) return false;
                TaskReward[task] = now;
                dirty = true;
                return true;
            }
        }
    }

    // informations sur un autre joueur de la session (apprises par son message HELLO signe)
    class MemberInfo
    {
        public uint Id;
        public byte[] Pub;
        public ulong Uid;
        public string Name = "";
        public int Level = 1;
        public long Xp;
        public int Flags;             // 1 profil du monde, 2 createur
        public bool Verified;
        public DateTime Seen, FirstXpTime;
        public long FirstXp = -1;
        public bool Suspect, Muted, Banned, Frozen;
        public float X, Y, Z;
        public bool HasPos;
        public int ChatCount;
        public DateTime ChatWindow;
        public long LastKillReward;
        public long GhostUntil;        // super-pouvoir Fantome : invisible sur les cartes
        public byte[] Fingerprint;     // empreinte de la version du mod (anti-triche)
        public bool BadVersion;        // version differente de la version officielle
        public bool OldProto;          // ancienne version du mod (avant 1.2) : ignore
        public bool Bot, BotOk;        // bot de test ; BotOk = ajoute par le createur
        public bool OldNoted;
        public uint HouseOwner;        // maison ou il est (proprietaire ; 0 = dehors)
        public int Strikes;            // anti-triche : comportements impossibles constates
        public DateTime StrikeWindow;
        public DateTime LastHitFrom;   // cadence des coups recus de ce joueur
        public int HitCount;
    }

    class ChatLine
    {
        public int Color;     // 0 joueur, 1 createur, 2 hote, 3 systeme, 4 annonce, 5 moi
        public string Name = "", Text = "";
    }

    partial class Client
    {
        // ---------------- messages de session (chat, identite, moderation...)
        public const byte MSG_HELLO = 1, MSG_CHAT = 2, MSG_ADMIN = 3, MSG_BANLIST = 4, MSG_KILL = 5,
            MSG_BOUNTY = 6, MSG_RACE = 7, MSG_HELLO_REQ = 8, MSG_EVENT = 9, MSG_REPORT = 11, MSG_NOTE = 12, MSG_VERSION = 13, MSG_COOP = 14, MSG_SHOT = 15, MSG_HOUSE = 16, MSG_INVITE = 17, MSG_CLOCK = 18;

        // commandes de moderation (le jeu utilise les memes numeros, voir online-world.gc)
        public const int CMD_KICK = 1, CMD_BAN = 2, CMD_UNBAN = 3, CMD_MUTE = 4, CMD_UNMUTE = 5, CMD_FREEZE = 6,
            CMD_UNFREEZE = 7, CMD_BRING = 8, CMD_GOTO = 9, CMD_KILL = 10, CMD_HEAL = 11, CMD_GIVE = 12, CMD_TIME = 13,
            CMD_ANNOUNCE = 14, CMD_PVP = 15, CMD_HEAL_ALL = 16, CMD_BRING_ALL = 17, CMD_SLAP = 18, CMD_HOST = 19,
            CMD_GIVE_ITEM = 20, CMD_FREEZE_ALL = 21, CMD_UNFREEZE_ALL = 22, CMD_EVENT = 23, CMD_GIVE_VEHICLES = 24,
            CMD_PUBLISH_VERSION = 25, CMD_BOUNTY = 26, CMD_RESET_ACTORS = 27, CMD_SET_LEVEL = 28, CMD_SET_MONEY = 29, CMD_IGNORE = 30, CMD_DAYLOCK = 31, CMD_WEATHER = 32, CMD_DIALOG = 33;

        // evenements du jeu (online-world.gc)
        public const int EV_ORBS = 10, EV_ENEMY = 11, EV_TASK = 12, EV_BUY = 13, EV_ADMIN = 14, EV_RACE = 15, EV_CHEAT = 16,
            EV_BOSS_DMG = 17, EV_EVENT = 18, EV_VEH_DEAD = 19, EV_MAIL = 28;
        // commandes envoyees au jeu (evenements entrants)
        public const int EVIN_CMD = 20, EVIN_GRANT = 21, EVIN_DIALOG = 29;

        Identity ident;
        Profil profil;
        public string ProfileDir = SafeStore.Dir();
        public bool Ephemeral;                  // bots : ni fichier, ni cle sur le disque
        public bool ForceCreateur;              // tests : ce client utilise la cle createur du PC
        public bool IsBot;                      // bot de test (invisible dans le monde s'il n'est pas au createur)
        public bool BotOfCreateur;              // bot ajoute par le createur (signe avec sa cle)
        readonly Dictionary<uint, MemberInfo> members = new Dictionary<uint, MemberInfo>();
        readonly List<ChatLine> chat = new List<ChatLine>();
        uint chatWrite;
        string announce = "";
        uint announceSeq;
        int announceKind;
        readonly HashSet<uint> seenNonces = new HashSet<uint>();
        readonly HashSet<ulong> worldBans = new HashSet<ulong>();
        DateTime lastHello = DateTime.MinValue, lastBanList = DateTime.MinValue, lastWorldTick = DateTime.MinValue;
        bool worldJoinChecked;
        float timeHour = -1f;
        uint timeSeq;
        bool frozen, muted;
        uint bountyId, bountyAmount;
        long bountyStart;
        DateTime nextBounty = DateTime.MinValue;
        // course
        uint raceId, raceOwner, raceSeq;
        int raceState;            // 0 aucune, 1 compte a rebours, 2 en cours, 3 terminee
        long raceStartMs, raceEndMs;
        float raceX, raceY, raceZ, raceSX, raceSY, raceSZ;
        string raceName = "";
        int raceRank, raceCount;
        uint racePot;
        readonly Dictionary<uint, long> raceFinish = new Dictionary<uint, long>();
        bool raceResultSent;
        // jeu -> programme
        uint uiFlags, uiMode, kbEditMode;
        uint keyWrite;
        long lastOrbTime;
        uint gainSeq;
        int levelBefore = -1;

        public Identity Ident { get { return ident; } }
        public bool IsCreateur { get { return (ForceCreateur || (!Ephemeral && !IsLocalJ2)) && Createur.IsMe; } }
        public bool InWorld { get { return SessionId != 0 && SessionCode.StartsWith(Proto.WorldPrefix); } }
        public bool CanModerate { get { return SessionId != 0 && (IsCreateur || (!InWorld && HostId == MyId)); } }
        public Profil Prof { get { return profil; } }

        void MondeInit()
        {
            if (ident != null) return;
            ident = new Identity(Ephemeral ? null : ProfileDir);
            Empreinte.Start();
            profil = new Profil(Ephemeral ? null : ProfileDir);
            if (IsCreateur) foreach (ulong u in profil.BanList) worldBans.Add(u);
        }

        void SendMsg(byte kind, uint target, byte[] payload)
        {
            PacketWriter w = new PacketWriter(Proto.C_MSG).U8(kind).U32(target);
            w.Bytes(payload, 0, payload.Length);
            Send(w.ToArray());
        }

        static byte[] Build(Action<BinaryWriter> f)
        {
            MemoryStream ms = new MemoryStream();
            BinaryWriter w = new BinaryWriter(ms);
            f(w);
            w.Flush();
            return ms.ToArray();
        }

        static void WStr(BinaryWriter w, string s, int len)
        {
            byte[] b = new byte[len];
            string a = Proto.Ascii(s ?? "", len - 1);
            Encoding.ASCII.GetBytes(a, 0, a.Length, b, 0);
            w.Write(b);
        }

        // ---------------- chat
        void AddChat(int color, string name, string text)
        {
            lock (lk)
            {
                // texte long : plusieurs lignes (rien n'est coupe)
                bool first = true;
                foreach (string part in Proto.GameLines(text, 62))
                {
                    ChatLine c = new ChatLine();
                    c.Color = color;
                    c.Name = first ? Proto.Ascii(name, 15) : "";
                    c.Text = part;
                    chat.Add(c);
                    first = false;
                }
                while (chat.Count > 10) chat.RemoveAt(0);
                chatWrite++;
            }
            L((name.Length > 0 ? name + " : " : "") + text);
        }

        public void SendChat(string text)
        {
            text = Proto.Ascii((text ?? "").Trim(), 87);
            if (text.Length == 0 || SessionId == 0) return;
            if (muted) { AddChat(3, "", T("Vous etes rendu muet par la moderation", "You have been muted by a moderator")); return; }
            long stamp = Profil.NowMs();
            byte[] body = Build(w => { w.Write(stamp); WStr(w, text, 88); });
            byte[] sig = ident.Sign(Concat(BitConverter.GetBytes(MyId), Encoding.ASCII.GetBytes(SessionCode), body));
            SendMsg(MSG_CHAT, 0, Build(w => { w.Write(body); w.Write((ushort)sig.Length); w.Write(sig); }));
            AddChat(5, MyName, text);
        }

        static byte[] Concat(params byte[][] parts)
        {
            int n = 0;
            foreach (byte[] p in parts) n += p.Length;
            byte[] r = new byte[n];
            int o = 0;
            foreach (byte[] p in parts) { Buffer.BlockCopy(p, 0, r, o, p.Length); o += p.Length; }
            return r;
        }

        // ---------------- identite + profil public
        void SendHello(uint target)
        {
            if (SessionId == 0 || ident == null) return;
            lastHello = DateTime.UtcNow;
            long stamp = Profil.NowMs();
            int level = profil.Level;
            long xp;
            lock (lk) xp = profil.Xp;
            // 4 = version 1.2 ou plus ; 8 = bot de test ; 16 = bot du createur
            int flags = (InWorld ? 1 : 0) | (IsCreateur ? 2 : 0) | 4 | (IsBot ? 8 : 0) | (IsBot && BotOfCreateur ? 16 : 0);
            byte[] body = Build(w =>
            {
                w.Write((byte)1);
                w.Write((ushort)ident.Pub.Length);
                w.Write(ident.Pub);
                w.Write(stamp);
                w.Write((ushort)level);
                w.Write(xp);
                w.Write((byte)flags);
                WStr(w, MyName, 16);
                byte[] fpv = Empreinte.Value ?? new byte[32];
                w.Write(fpv, 0, 32);
            });
            byte[] sig = ident.Sign(Concat(BitConverter.GetBytes(MyId), Encoding.ASCII.GetBytes(SessionCode), body));
            // le createur prouve qui il est avec sa cle (sinon n'importe qui mettrait le drapeau)
            byte[] csig = IsCreateur ? Createur.Sign(Concat(Encoding.ASCII.GetBytes("createur"), BitConverter.GetBytes(MyId), Encoding.ASCII.GetBytes(SessionCode), BitConverter.GetBytes(stamp)))
                : (IsBot && BotOfCreateur && Createur.IsMe ? Createur.Sign(Concat(Encoding.ASCII.GetBytes("createurbot"), BitConverter.GetBytes(MyId), Encoding.ASCII.GetBytes(SessionCode), BitConverter.GetBytes(stamp))) : null);
            SendMsg(MSG_HELLO, target, Build(w =>
            {
                w.Write(body);
                w.Write((ushort)sig.Length);
                w.Write(sig);
                w.Write((ushort)(csig != null ? csig.Length : 0));
                if (csig != null) w.Write(csig);
            }));
        }

        MemberInfo Member(uint id)
        {
            MemberInfo m;
            if (!members.TryGetValue(id, out m)) { m = new MemberInfo(); m.Id = id; m.Seen = DateTime.UtcNow; members[id] = m; }
            return m;
        }

        // ---------------- horloge du monde : meme heure chez tout le monde (et les nouveaux venus)
        bool dayLock;
        int weather;   // meteo du monde : 0 beau temps, 1 pluie, 2 orage, 3 neige (admin)
        DateTime lastClockSend = DateTime.MinValue;

        float MyGameHour()
        {
            // (lu dans l'image du jeu : etat local complet, pads compris)
            byte[] g;
            lock (lk) g = bridge != null ? bridge.Game : null;
            if (g == null || g.Length <= Shm.Local + 88 + 31 || !GameAttached) return -1f;
            return g[Shm.Local + 88 + 31] / 10f;
        }

        void ClockTick()
        {
            if (SessionId == 0 || !InWorld || EventAuthority() != MyId) return;
            if ((DateTime.UtcNow - lastClockSend).TotalSeconds < 20) return;
            float h = MyGameHour();
            if (h < 0f) return;
            lastClockSend = DateTime.UtcNow;
            bool dl = dayLock;
            int wt = weather;
            SendMsg(MSG_CLOCK, 0, Build(w => { w.Write(h); w.Write((byte)(dl ? 1 : 0)); w.Write((byte)wt); }));
        }

        void OnWorldClock(float h, bool dl)
        {
            if (!InWorld || float.IsNaN(h) || h < 0f || h >= 24.5f) return;
            dayLock = dl;
            float mine = MyGameHour();
            float diff = Math.Abs(mine - h);
            diff = Math.Min(diff, 24f - diff);
            if (mine < 0f || diff > 0.3f) { timeHour = Math.Min(23.99f, h); timeSeq++; }
        }

        // maisons : meme maison que moi (ou tous les deux dehors)
        bool SameHouse(uint id)
        {
            lock (lk) { MemberInfo m; return (members.TryGetValue(id, out m) ? m.HouseOwner : 0u) == myHouseOwner; }
        }

        bool IsIgnored(uint id)
        {
            lock (lk)
            {
                MemberInfo m;
                // ancienne version (avant 1.2) : ignoree partout ; bot d'un autre que le createur :
                // ignore dans le monde en ligne (les bots de test sont reserves au createur)
                return members.TryGetValue(id, out m) && (m.Banned || m.OldProto || (m.Bot && !m.BotOk) || (InWorld && m.BadVersion));
            }
        }

        bool IsVerified(uint id)
        {
            lock (lk)
            {
                MemberInfo m;
                return members.TryGetValue(id, out m) && m.Verified;
            }
        }

        // quitter la session (hors du fil de reception)
        void LeaveLater()
        {
            rejoinCode = null;
            ThreadPool.QueueUserWorkItem(delegate { Thread.Sleep(50); Send(new PacketWriter(Proto.C_LEAVE).ToArray()); });
        }

        // ---------------- reception
        void OnSessionMsg(uint from, byte kind, uint target, BinaryReader r)
        {
            if (from == MyId || SessionId == 0) return;
            if (target != 0 && target != MyId) return;
            byte[] rest = r.ReadBytes((int)(r.BaseStream.Length - r.BaseStream.Position));
            try
            {
                switch (kind)
                {
                    case MSG_HELLO: OnHello(from, rest); break;
                    case MSG_HELLO_REQ: if ((DateTime.UtcNow - lastHello).TotalSeconds > 2) SendHello(from); break;
                    case MSG_CHAT: OnChat(from, rest); break;
                    case MSG_ADMIN: OnAdmin(from, rest); break;
                    case MSG_BANLIST: OnBanList(rest); break;
                    case MSG_KILL: OnKillReceipt(from, rest); break;
                    case MSG_BOUNTY: OnBounty(from, rest); break;
                    case MSG_RACE: OnRace(from, rest); break;
                    case MSG_EVENT: OnEventMsg(from, rest); break;
                    case MSG_NOTE: OnNote(from, rest); break;
                    case MSG_REPORT: OnReport(from, rest); break;
                    case MSG_VERSION: OnVersionMsg(from, rest); break;
                    case MSG_CLOCK:
                        if (rest.Length >= 5 && from == EventAuthority())
                        {
                            OnWorldClock(BitConverter.ToSingle(rest, 0), rest[4] != 0);
                            if (rest.Length >= 6 && InWorld && rest[5] <= 3) weather = rest[5];
                        }
                        break;
                    case MSG_HOUSE:
                        if (rest.Length >= 4)
                            lock (lk) { MemberInfo hm; if (members.TryGetValue(from, out hm)) hm.HouseOwner = BitConverter.ToUInt32(rest, 0); }
                        break;
                    case MSG_INVITE:
                        if (InWorld && rest.Length >= 1 && rest[0] <= 7)
                        {
                            bool ok;
                            string inm = "?";
                            lock (lk) { MemberInfo im; ok = members.TryGetValue(from, out im) && im.Verified && !im.Banned && !im.BadVersion; if (ok) inm = im.Name; }
                            if (!ok || IsIgnored(from)) break;
                            GameEvent e = new GameEvent();
                            e.Kind = EV_INVITE; e.Player = from; e.Mode = rest[0];
                            lock (lk) if (inEvents.Count < 64) inEvents.Enqueue(e);
                            PushFeed(inm + T(" t'invite dans sa maison : SELECT / MOI pour accepter", " invites you to their house: SELECT / ME to accept"));
                        }
                        break;
                    case MSG_SHOT:
                        // tir d'un joueur d'une autre maison : on ne le voit pas
                        if (rest.Length >= 13 && SameHouse(from))
                        {
                            GameEvent e = new GameEvent();
                            e.Kind = EV_SHOT; e.Player = from; e.Mode = rest[0];
                            e.Dx = BitConverter.ToSingle(rest, 1); e.Dy = BitConverter.ToSingle(rest, 5); e.Dz = BitConverter.ToSingle(rest, 9);
                            lock (lk) if (inEvents.Count < 64) inEvents.Enqueue(e);
                        }
                        break;
                    case MSG_COOP:
                        bool coopOk = rest.Length >= 5 && (!InWorld || rest[0] != 3);
                        if (coopOk && InWorld)
                            lock (lk) { MemberInfo cm; coopOk = members.TryGetValue(from, out cm) && cm.Verified && !cm.Banned && !cm.BadVersion && !cm.Suspect; }
                        if (coopOk)
                        {
                            GameEvent e = new GameEvent();
                            e.Kind = EV_COOP; e.Mode = rest[0]; e.Player = BitConverter.ToUInt32(rest, 1);
                            lock (lk) if (inEvents.Count < 64) inEvents.Enqueue(e);
                        }
                        break;
                }
            }
            catch (Exception) { }
        }

        void OnHello(uint from, byte[] p)
        {
            BinaryReader r = new BinaryReader(new MemoryStream(p));
            int bodyStart = 0;
            if (r.ReadByte() != 1) return;
            int publen = r.ReadUInt16();
            if (publen < 64 || publen > 600) return;
            byte[] pub = r.ReadBytes(publen);
            long stamp = r.ReadInt64();
            int level = r.ReadUInt16();
            long xp = r.ReadInt64();
            int flags = r.ReadByte();
            string name = Rd.Str(r, 16);
            byte[] fpr = r.ReadBytes(32);
            int bodyLen = (int)r.BaseStream.Position - bodyStart;
            byte[] body = new byte[bodyLen];
            Buffer.BlockCopy(p, bodyStart, body, 0, bodyLen);
            int siglen = r.ReadUInt16();
            byte[] sig = r.ReadBytes(siglen);
            int csiglen = r.ReadUInt16();
            byte[] csig = csiglen > 0 ? r.ReadBytes(csiglen) : null;
            if (!Identity.Verify(pub, Concat(BitConverter.GetBytes(from), Encoding.ASCII.GetBytes(SessionCode), body), sig)) return;
            bool isCreateur = (flags & 2) != 0 && csig != null
                && Createur.Verify(Concat(Encoding.ASCII.GetBytes("createur"), BitConverter.GetBytes(from), Encoding.ASCII.GetBytes(SessionCode), BitConverter.GetBytes(stamp)), csig);
            // bot de test : seul un bot ajoute par le createur (signe avec sa cle) est accepte
            bool isBot = (flags & 8) != 0;
            bool botOk = isBot && (flags & 16) != 0 && csig != null
                && Createur.Verify(Concat(Encoding.ASCII.GetBytes("createurbot"), BitConverter.GetBytes(from), Encoding.ASCII.GetBytes(SessionCode), BitConverter.GetBytes(stamp)), csig);
            bool noteOld = false, noteBot = false;
            ulong uid = Identity.UidOf(pub);
            bool isNew;
            bool xpJump = false;
            lock (lk)
            {
                MemberInfo m;
                isNew = !members.TryGetValue(from, out m) || !m.Verified;
                m = Member(from);
                if (m.Verified && m.Uid != uid) return;   // un autre joueur essaie de prendre cet identifiant
                m.Pub = pub;
                m.Uid = uid;
                m.Name = Proto.CleanName(name);
                m.Level = Math.Max(1, Math.Min(999, level));
                m.Xp = xp;
                m.Flags = (flags & 1) | (isCreateur ? 2 : 0);
                m.OldProto = (flags & 4) == 0;
                m.Bot = isBot;
                m.BotOk = botOk;
                if (!m.OldNoted && (m.OldProto || (isBot && !botOk)))
                {
                    m.OldNoted = true;
                    if (m.OldProto) noteOld = true; else noteBot = true;
                }
                m.Verified = true;
                m.Seen = DateTime.UtcNow;
                // anti-triche : progression impossible (plus de 15 000 points par minute) : une faute
                if (m.FirstXp < 0 || xp < m.FirstXp) { m.FirstXp = xp; m.FirstXpTime = DateTime.UtcNow; }
                else
                {
                    double min = Math.Max(1.0, (DateTime.UtcNow - m.FirstXpTime).TotalMinutes);
                    if ((xp - m.FirstXp) / min > 15000 && xp - m.FirstXp > 30000) { m.FirstXp = xp; m.FirstXpTime = DateTime.UtcNow; xpJump = true; }
                }
                if (InWorld && worldBans.Contains(uid)) m.Banned = true;
                if (fpr.Length == 32) NoteFingerprint(m, fpr);
            }
            if (xpJump) Strike(from, T("experience impossible", "impossible experience"));
            if (noteOld) AddChat(3, "", name + T(" a une ancienne version du mod : ignore (mise a jour 1.2 requise)", " has an old mod version: ignored (update 1.2 required)"));
            if (noteBot) AddChat(3, "", name + T(" : bot refuse (reserve au createur)", ": bot refused (creator only)"));
            RefilterPlayers();
            if (isNew)
            {
                SendHello(from);
                if (IsCreateur && InWorld)
                {
                    bool banned;
                    lock (lk) banned = worldBans.Contains(uid);
                    // un joueur banni revient : on le renvoie aussitot
                    if (banned) SendAdmin(CMD_BAN, from, uid, 0, 0, 0, 0, "");
                }
            }
        }

        void OnChat(uint from, byte[] p)
        {
            BinaryReader r = new BinaryReader(new MemoryStream(p));
            byte[] body = r.ReadBytes(8 + 88);
            int siglen = r.ReadUInt16();
            byte[] sig = r.ReadBytes(siglen);
            MemberInfo m;
            lock (lk)
            {
                if (!members.TryGetValue(from, out m) || !m.Verified)
                {
                    // inconnu : on lui demande qui il est (le message est perdu)
                    SendMsg(MSG_HELLO_REQ, from, new byte[0]);
                    return;
                }
                if (m.Banned || m.Muted) return;
                // anti-spam : 5 messages par 8 secondes
                if ((DateTime.UtcNow - m.ChatWindow).TotalSeconds > 8) { m.ChatWindow = DateTime.UtcNow; m.ChatCount = 0; }
                if (++m.ChatCount > 5) return;
            }
            if (!Identity.Verify(m.Pub, Concat(BitConverter.GetBytes(from), Encoding.ASCII.GetBytes(SessionCode), body), sig)) return;
            BinaryReader br = new BinaryReader(new MemoryStream(body));
            br.ReadInt64();
            string text = Rd.Str(br, 88);
            int color = (m.Flags & 2) != 0 ? 1 : (from == HostId && !InWorld ? 2 : 0);
            AddChat(color, m.Name, text);
        }

        // ---------------- moderation
        public void SendAdmin(int cmd, uint target, ulong targetUid, int arg, float x, float y, float z, string text)
        {
            if (SessionId == 0 || !CanModerate) return;
            bool asCreateur = IsCreateur;
            long stamp = Profil.NowMs();
            byte[] nb = new byte[4];
            new Random(Guid.NewGuid().GetHashCode()).NextBytes(nb);
            uint nonce = BitConverter.ToUInt32(nb, 0);
            byte[] body = Build(w =>
            {
                w.Write((byte)cmd); w.Write(target); w.Write(targetUid); w.Write(arg);
                w.Write(x); w.Write(y); w.Write(z);
                WStr(w, text, 64);
                WStr(w, SessionCode, 8);
                w.Write(stamp); w.Write(nonce);
                w.Write((byte)(asCreateur ? 1 : 2));
            });
            byte[] signed = Concat(BitConverter.GetBytes(MyId), body);
            byte[] sig = asCreateur ? Createur.Sign(signed) : ident.Sign(signed);
            if (sig == null) return;
            SendMsg(MSG_ADMIN, 0, Build(w => { w.Write(body); w.Write((ushort)sig.Length); w.Write(sig); }));
            // effet chez moi aussi (le serveur ne me renvoie pas mes messages)
            ApplyAdmin(MyId, cmd, target, targetUid, arg, x, y, z, text, nonce, true);
            if (cmd == CMD_BAN && asCreateur && InWorld) PublishBanList();
        }

        void OnAdmin(uint from, byte[] p)
        {
            BinaryReader r = new BinaryReader(new MemoryStream(p));
            int cmd = r.ReadByte();
            uint target = r.ReadUInt32();
            ulong tuid = r.ReadUInt64();
            int arg = r.ReadInt32();
            float x = r.ReadSingle(), y = r.ReadSingle(), z = r.ReadSingle();
            string text = Rd.Str(r, 64);
            string code = Rd.Str(r, 8);
            long stamp = r.ReadInt64();
            uint nonce = r.ReadUInt32();
            int auth = r.ReadByte();
            int bodyLen = (int)r.BaseStream.Position;
            int siglen = r.ReadUInt16();
            byte[] sig = r.ReadBytes(siglen);
            byte[] body = new byte[bodyLen];
            Buffer.BlockCopy(p, 0, body, 0, bodyLen);
            if (code != SessionCode) return;
            if (Math.Abs(Profil.NowMs() - stamp) > 20 * 60 * 1000L) return;   // vieux message rejoue
            lock (lk) { if (!seenNonces.Add(nonce)) return; if (seenNonces.Count > 5000) seenNonces.Clear(); }
            byte[] signed = Concat(BitConverter.GetBytes(from), body);
            if (auth == 1)
            {
                if (!Createur.Verify(signed, sig)) return;
            }
            else if (auth == 2)
            {
                // hote d'une session privee seulement (dans le monde en ligne, seul le createur modere)
                if (InWorld || from != HostId) return;
                MemberInfo m;
                lock (lk) { if (!members.TryGetValue(from, out m) || !m.Verified) return; }
                if (!Identity.Verify(m.Pub, signed, sig)) return;
            }
            else return;
            ApplyAdmin(from, cmd, target, tuid, arg, x, y, z, text, nonce, auth == 1);
        }

        string NameOf(uint id)
        {
            if (id == MyId) return MyName;
            string n;
            lock (lk)
            {
                MemberInfo m;
                if (members.TryGetValue(id, out m) && m.Name.Length > 0) return m.Name;
                if (names.TryGetValue(id, out n)) return n;
            }
            return "?";
        }

        // niveau (1..999) ou orbes (0..9 999 999) fixes par le createur
        void SetMyLevelOrMoney(int cmd, int arg, string who)
        {
            if (cmd == CMD_SET_LEVEL)
            {
                int lv = Math.Max(1, Math.Min(999, arg));
                lock (lk) { profil.Xp = Profil.XpForLevel(lv); profil.Touch(); }
                levelBefore = profil.Level;
                profil.Save(true);
                PushFeed(T("Niveau ", "Level ") + lv + T(" (par ", " (by ") + who + ")");
            }
            else
            {
                long m = Math.Max(0, Math.Min(9999999, (long)arg));
                lock (lk) { profil.Money = m; profil.Touch(); }
                profil.Save(true);
                PushFeed(T("Orbes : ", "Orbs: ") + m + T(" (par ", " (by ") + who + ")");
            }
        }

        void ApplyAdmin(uint from, int cmd, uint target, ulong tuid, int arg, float x, float y, float z, string text, uint nonce, bool byCreateur)
        {
            bool me = target == MyId || (tuid != 0 && ident != null && tuid == ident.Uid);
            bool all = target == 0 && tuid == 0;
            string who = byCreateur ? T("le createur", "the creator") : T("l'hote", "the host");
            string tn = NameOf(target);
            switch (cmd)
            {
                case CMD_KICK:
                    AddChat(3, "", tn + T(" a ete expulse par ", " was kicked by ") + who);
                    if (me) { PushFeed(T("Vous avez ete expulse de la session", "You were kicked from the session")); LeaveLater(); }
                    break;
                case CMD_BAN:
                    AddChat(3, "", tn + T(" a ete banni par ", " was banned by ") + who);
                    lock (lk)
                    {
                        MemberInfo m;
                        if (members.TryGetValue(target, out m)) { m.Banned = true; if (tuid == 0) tuid = m.Uid; }
                        if (InWorld && tuid != 0) worldBans.Add(tuid);
                        if (IsCreateur && InWorld && tuid != 0) { profil.BanList.Add(tuid); profil.BanNames[tuid] = tn; profil.Touch(); }
                    }
                    if (me)
                    {
                        if (InWorld) { profil.BannedUntil = Profil.NowMs() + 30L * 24 * 3600 * 1000; profil.Save(true); }
                        PushFeed(T("Vous avez ete banni de cette session", "You were banned from this session"));
                        LeaveLater();
                    }
                    break;
                case CMD_UNBAN:
                    lock (lk)
                    {
                        worldBans.Remove(tuid);
                        foreach (MemberInfo m in members.Values) if (m.Uid == tuid) m.Banned = false;
                        if (IsCreateur) { profil.BanList.Remove(tuid); profil.BanNames.Remove(tuid); profil.Touch(); }
                    }
                    if (tuid != 0 && ident != null && tuid == ident.Uid) { profil.BannedUntil = 0; profil.Save(true); }
                    AddChat(3, "", T("Un joueur a ete debanni", "A player was unbanned"));
                    if (IsCreateur && InWorld) PublishBanList();
                    break;
                case CMD_MUTE:
                case CMD_UNMUTE:
                    lock (lk) { MemberInfo m; if (members.TryGetValue(target, out m)) m.Muted = cmd == CMD_MUTE; }
                    if (me) muted = cmd == CMD_MUTE;
                    AddChat(3, "", tn + (cmd == CMD_MUTE ? T(" est rendu muet", " was muted") : T(" peut de nouveau parler", " can talk again")));
                    break;
                case CMD_FREEZE:
                case CMD_UNFREEZE:
                case CMD_FREEZE_ALL:
                case CMD_UNFREEZE_ALL:
                    {
                        bool on = cmd == CMD_FREEZE || cmd == CMD_FREEZE_ALL;
                        bool mine = me || cmd == CMD_FREEZE_ALL || cmd == CMD_UNFREEZE_ALL;
                        if (cmd == CMD_FREEZE_ALL || cmd == CMD_UNFREEZE_ALL)
                        {
                            AddChat(3, "", on ? T("Tout le monde est gele par ", "Everyone was frozen by ") + who : T("Tout le monde peut rebouger", "Everyone can move again"));
                            if (from == MyId) mine = false;
                        }
                        else
                        {
                            lock (lk) { MemberInfo m; if (members.TryGetValue(target, out m)) m.Frozen = on; }
                            AddChat(3, "", tn + (on ? T(" est gele", " is frozen") : T(" est degele", " is unfrozen")));
                        }
                        if (mine) { frozen = on; GameCmd(on ? CMD_FREEZE : CMD_UNFREEZE, from, 0, 0, 0, 0, ""); }
                        break;
                    }
                case CMD_BRING:
                case CMD_BRING_ALL:
                    if (me || (cmd == CMD_BRING_ALL && from != MyId))
                    {
                        GameCmd(CMD_BRING, from, arg, x, y, z, text);
                        PushFeed(T("Teleporte vers ", "Teleported to ") + NameOf(from));
                    }
                    break;
                case CMD_KILL:
                    if (me) GameCmd(CMD_KILL, from, 0, 0, 0, 0, "");
                    AddChat(3, "", tn + T(" a ete foudroye par ", " was smitten by ") + who);
                    break;
                case CMD_SLAP:
                    if (me) GameCmd(CMD_SLAP, from, 0, 0, 0, 0, "");
                    break;
                case CMD_HEAL:
                case CMD_HEAL_ALL:
                    if (me || cmd == CMD_HEAL_ALL) GameCmd(CMD_HEAL, from, 0, 0, 0, 0, "");
                    if (cmd == CMD_HEAL_ALL) AddChat(3, "", T("Tout le monde est soigne !", "Everyone is healed!"));
                    break;
                case CMD_GIVE:
                    if (me && byCreateur && InWorld && arg > 0 && arg <= 100000)
                    {
                        bool fresh;
                        lock (lk) { fresh = profil.UsedNonces.Add(nonce); if (profil.UsedNonces.Count > 2000) profil.UsedNonces.Clear(); }
                        if (fresh)
                        {
                            lock (lk) { profil.Money = Math.Min(9999999, profil.Money + arg); profil.Touch(); }
                            profil.Save(true);
                            Reward(arg, 0, T("Cadeau du createur", "Gift from the creator"));
                        }
                    }
                    if (!me) AddChat(3, "", tn + T(" recoit ", " receives ") + arg + T(" orbes du createur", " orbs from the creator"));
                    break;
                case CMD_SET_LEVEL:
                case CMD_SET_MONEY:
                    if (me && byCreateur && InWorld)
                    {
                        bool fresh;
                        lock (lk) { fresh = profil.UsedNonces.Add(nonce); if (profil.UsedNonces.Count > 2000) profil.UsedNonces.Clear(); }
                        if (fresh) SetMyLevelOrMoney(cmd, arg, T("le createur", "the creator"));
                    }
                    if (!me) AddChat(3, "", tn + (cmd == CMD_SET_LEVEL ? T(" : niveau ", ": level ") : T(" : orbes ", ": orbs ")) + arg + T(" (createur)", " (creator)"));
                    if (!me && byCreateur)
                        lock (lk) { MemberInfo sm; if (members.TryGetValue(target, out sm)) { sm.FirstXp = -1; sm.Strikes = 0; sm.Suspect = false; } }
                    break;
                case CMD_GIVE_VEHICLES:
                    if (me && byCreateur && InWorld)
                    {
                        bool fresh;
                        lock (lk) fresh = profil.UsedNonces.Add(nonce);
                        if (fresh)
                        {
                            lock (lk) { profil.Vehicles |= 0xffu; profil.Items |= 0x7u; profil.Touch(); }
                            profil.Save(true);
                            Reward(0, 0, T("Cadeau du createur : tous les vehicules !", "Gift from the creator: all vehicles!"));
                        }
                    }
                    if (!me) AddChat(3, "", tn + T(" recoit tous les vehicules du createur", " receives all vehicles from the creator"));
                    break;
                case CMD_GIVE_ITEM:
                    if (me && byCreateur && InWorld && arg >= 0 && arg < Catalog.Count && Catalog.Items[arg] != null && !profil.Owns(arg))
                    {
                        bool fresh;
                        lock (lk) fresh = profil.UsedNonces.Add(nonce);
                        if (fresh)
                        {
                            // l'objet est offert : on avance son prix puis on l'achete
                            lock (lk) profil.Money += Catalog.Price(arg);
                            if (profil.Buy(arg) != 0) lock (lk) profil.Money = Math.Max(0, profil.Money - Catalog.Price(arg));
                            Reward(0, 0, T("Cadeau : ", "Gift: ") + Lang.TrFr(CurLang, Catalog.Items[arg].Name));
                        }
                    }
                    break;
                case CMD_TIME:
                    timeHour = Math.Max(0, Math.Min(23.99f, arg / 100f));
                    timeSeq++;
                    dayLock = false;
                    AddChat(3, "", T("Heure du monde changee par ", "World time changed by ") + who);
                    break;
                case CMD_WEATHER:
                    {
                        weather = Math.Max(0, Math.Min(3, arg));
                        string[] wn = { T("beau temps", "clear sky"), T("pluie", "rain"), T("orage", "storm"), T("neige", "snow") };
                        AddChat(3, "", T("Meteo : ", "Weather: ") + wn[weather] + T(" (par ", " (by ") + who + ")");
                        lastClockSend = DateTime.MinValue;   // les autres l'apprennent tout de suite
                        break;
                    }
                case CMD_DIALOG:
                    {
                        // tout le monde entend la replique (dans la langue de son jeu)
                        GameEvent e = new GameEvent();
                        e.Kind = EVIN_DIALOG;
                        e.Mode = Math.Max(0, arg);
                        lock (lk) if (inEvents.Count < 64) inEvents.Enqueue(e);
                        break;
                    }
                case CMD_DAYLOCK:
                    dayLock = arg != 0;
                    if (dayLock) { timeHour = 12f; timeSeq++; }
                    AddChat(3, "", (dayLock ? T("Jour permanent active par ", "Always day enabled by ") : T("Jour permanent coupe par ", "Always day disabled by ")) + who);
                    break;
                case CMD_ANNOUNCE:
                    lock (lk) { announce = text; announceKind = 0; announceSeq++; }
                    AddChat(4, byCreateur ? T("CREATEUR", "CREATOR") : T("HOTE", "HOST"), text);
                    break;
                case CMD_PVP:
                    AddChat(3, "", T("PvP ", "PvP ") + (arg != 0 ? T("active", "enabled") : T("desactive", "disabled")) + T(" par ", " by ") + who);
                    lock (lk) worldPvpOff = arg == 0;
                    break;
                case CMD_HOST:
                    AddChat(3, "", tn + T(" devient l'hote", " is now the host"));
                    break;
                case CMD_BOUNTY:
                    // prime posee par le createur (ou l'hote d'une session privee) sur un joueur
                    if (target != 0 && arg > 0)
                    {
                        lock (lk)
                        {
                            bountyId = target;
                            bountyAmount = (uint)Math.Min(20000, arg);
                            bountyStart = Profil.NowMs();
                            bountyByCreator = true;
                        }
                        if (me)
                        {
                            lock (lk) { announce = T("TA TETE EST MISE A PRIX PAR ", "YOUR HEAD HAS A PRICE, SET BY ") + who.ToUpperInvariant() + " !"; announceKind = 3; announceSeq++; }
                            AddChat(3, "", T("Prime de ", "Bounty of ") + arg + T(" orbes sur VOUS ! Survivez 4 minutes.", " orbs on YOU! Survive 4 minutes."));
                        }
                        else
                        {
                            lock (lk) { announce = T("PRIME : ", "BOUNTY: ") + tn + " (" + arg + T(" orbes)", " orbs)"); announceKind = 3; announceSeq++; }
                            AddChat(3, "", T("Prime de ", "Bounty of ") + arg + T(" orbes sur ", " orbs on ") + tn + T(" (point rouge sur la carte)", " (red dot on the map)"));
                        }
                    }
                    break;
                case CMD_RESET_ACTORS:
                    GameCmd(CMD_RESET_ACTORS, from, 0, 0, 0, 0, "");
                    AddChat(3, "", T("Le monde est remis a neuf par ", "The world was reset by ") + who + T(" (ennemis, caisses, objets)", " (enemies, crates, items)"));
                    break;
            }
        }

        bool bountyByCreator;

        bool worldPvpOff;

        void GameCmd(int cmd, uint from, int arg, float x, float y, float z, string text)
        {
            GameEvent e = new GameEvent();
            e.Kind = EVIN_CMD;
            e.Player = from;
            e.Mode = cmd;
            e.Damage = arg;
            e.Dx = x; e.Dy = y; e.Dz = z;
            lock (lk)
            {
                cmdText = text ?? "";
                if (inEvents.Count < 64) inEvents.Enqueue(e);
            }
        }

        string cmdText = "";

        // ---------------- liste des bannis (createur -> tout le monde)
        void PublishBanList()
        {
            if (!IsCreateur) return;
            lastBanList = DateTime.UtcNow;
            List<ulong> list;
            lock (lk) list = new List<ulong>(profil.BanList);
            long stamp = Profil.NowMs();
            byte[] body = Build(w => { w.Write(stamp); w.Write((ushort)list.Count); foreach (ulong u in list) w.Write(u); });
            byte[] sig = Createur.Sign(Concat(Encoding.ASCII.GetBytes("bans"), body));
            if (sig == null) return;
            SendMsg(MSG_BANLIST, 0, Build(w => { w.Write(body); w.Write((ushort)sig.Length); w.Write(sig); }));
        }

        long banListStamp;

        void OnBanList(byte[] p)
        {
            BinaryReader r = new BinaryReader(new MemoryStream(p));
            long stamp = r.ReadInt64();
            int n = r.ReadUInt16();
            if (n > 5000) return;
            int bodyLen = 8 + 2 + n * 8;
            if (p.Length < bodyLen + 2) return;
            ulong[] list = new ulong[n];
            for (int i = 0; i < n; i++) list[i] = r.ReadUInt64();
            int siglen = r.ReadUInt16();
            byte[] sig = r.ReadBytes(siglen);
            byte[] body = new byte[bodyLen];
            Buffer.BlockCopy(p, 0, body, 0, bodyLen);
            if (!Createur.Verify(Concat(Encoding.ASCII.GetBytes("bans"), body), sig)) return;
            bool meBanned = false;
            lock (lk)
            {
                if (stamp < banListStamp) return;
                banListStamp = stamp;
                worldBans.Clear();
                foreach (ulong u in list) worldBans.Add(u);
                foreach (MemberInfo m in members.Values) m.Banned = m.Uid != 0 && worldBans.Contains(m.Uid);
                meBanned = ident != null && worldBans.Contains(ident.Uid);
            }
            if (meBanned && InWorld && !IsCreateur)
            {
                PushFeed(T("Vous etes banni de la session publique", "You are banned from the public session"));
                LeaveLater();
            }
        }

        // ---------------- eliminations (recu signe par la victime)
        // historiques anti-farm : mes eliminations par victime, mes morts par tueur (cle = uid)
        readonly Dictionary<ulong, List<long>> killHist = new Dictionary<ulong, List<long>>();
        readonly Dictionary<ulong, List<long>> deathHist = new Dictionary<ulong, List<long>>();
        int streak;

        // nombre d'evenements recents pour cette cle (et ajoute celui-ci)
        int RecentCount(Dictionary<ulong, List<long>> d, ulong key, long windowMs)
        {
            lock (lk)
            {
                long now = Profil.NowMs();
                List<long> l;
                if (!d.TryGetValue(key, out l)) { l = new List<long>(); d[key] = l; }
                l.RemoveAll(t => now - t > windowMs);
                int n = l.Count;
                l.Add(now);
                return n;
            }
        }

        ulong UidOfMember(uint id)
        {
            lock (lk) { MemberInfo m; return members.TryGetValue(id, out m) && m.Uid != 0 ? m.Uid : id; }
        }

        // kind 0 = je suis elimine par killer, 1 = mon vehicule est detruit par killer
        void SendKillReceipt(uint killer, int kind)
        {
            if (killer == 0 || killer == MyId || ident == null) return;
            long stamp = Profil.NowMs();
            byte bounty = (byte)(kind == 0 && bountyId == MyId ? 1 : 0);
            int drop = 0;
            if (InWorld && kind == 0)
            {
                // je lache une partie de mes orbes... de moins en moins si c'est toujours le meme tueur
                int n = RecentCount(deathHist, UidOfMember(killer), 600000);
                double f = n == 0 ? 1.0 : n == 1 ? 0.5 : n == 2 ? 0.25 : 0.0;
                long money;
                lock (lk) money = profil.Money;
                drop = (int)Math.Min(money, Math.Round(Math.Max(5.0, Math.Min(250.0, money * 0.05)) * f));
                if (drop > 0)
                {
                    lock (lk) { profil.Money -= drop; profil.Touch(); }
                    profil.Save(true);
                    Reward(-drop, 0, T("Butin perdu : ", "Loot dropped: ") + drop + T(" orbes", " orbs"));
                }
            }
            int d = drop;
            byte[] body = Build(w => { w.Write(killer); w.Write(MyId); w.Write(stamp); w.Write(bounty); WStr(w, SessionCode, 8); w.Write((byte)kind); w.Write((ushort)d); });
            byte[] sig = ident.Sign(body);
            SendMsg(MSG_KILL, killer, Build(w => { w.Write(body); w.Write((ushort)sig.Length); w.Write(sig); }));
            if (bounty == 1)
            {
                lock (lk) { bountyId = 0; bountyAmount = 0; }
            }
        }

        void OnKillReceipt(uint from, byte[] p)
        {
            BinaryReader r = new BinaryReader(new MemoryStream(p));
            uint killer = r.ReadUInt32();
            uint victim = r.ReadUInt32();
            long stamp = r.ReadInt64();
            int bounty = r.ReadByte();
            string code = Rd.Str(r, 8);
            int kind = r.ReadByte();
            int drop = r.ReadUInt16();
            int bodyLen = (int)r.BaseStream.Position;
            int siglen = r.ReadUInt16();
            byte[] sig = r.ReadBytes(siglen);
            byte[] body = new byte[bodyLen];
            Buffer.BlockCopy(p, 0, body, 0, bodyLen);
            if (killer != MyId || victim != from || code != SessionCode || !InWorld) return;
            if (Math.Abs(Profil.NowMs() - stamp) > 120000) return;
            MemberInfo m;
            lock (lk) { if (!members.TryGetValue(from, out m) || !m.Verified || m.Banned) return; }
            if (!Identity.Verify(m.Pub, body, sig)) return;
            if (m.Uid == ident.Uid) return;
            // gains degressifs : plus on elimine le meme joueur (15 min), moins il rapporte
            int n = RecentCount(killHist, (m.Uid << 1) | (ulong)(kind & 1), 900000);
            double[] F = { 1.0, 0.6, 0.3, 0.15 };
            double f = n < F.Length ? F[n] : 0.0;
            int baseMoney = kind == 1 ? 30 : 25, baseXp = kind == 1 ? 80 : 70;
            long money = (long)Math.Round(baseMoney * f) + (kind == 0 ? Math.Min(250, Math.Max(0, drop)) : 0);
            long xp = (long)Math.Round(baseXp * Math.Max(0.15, f));
            long got = money > 0 ? profil.Gain("kill", money, 900, 600000, xp) : profil.Gain("kill-xp", 0, 0, 600000, xp);
            if (money <= 0) got = 0;
            string tail = f <= 0.0 ? T(" (trop souvent : plus d'orbes)", " (too often: no orbs)") : (drop > 0 && kind == 0 ? T(" (+ butin ", " (+ loot ") + drop + ")" : "");
            if (kind == 1)
            {
                lock (lk) profil.VehKills++;
                Reward((int)got, (int)xp, T("Vehicule de ", "Destroyed ") + m.Name + T(" detruit !", "'s vehicle!") + tail);
                return;
            }
            lock (lk) profil.Kills++;
            if (bounty == 1)
            {
                long bAmount;
                lock (lk) bAmount = bountyByCreator && bountyAmount > 0 ? bountyAmount : 150;
                long b = profil.Gain("bounty", bAmount, Math.Max(450, bAmount), 1800000, 200);
                lock (lk) { if (bountyId == from) { bountyId = 0; bountyAmount = 0; } }
                lock (lk) profil.Bounties++;
                Reward((int)(got + b), (int)xp + 200, T("Prime encaissee sur ", "Bounty claimed on ") + m.Name + " !");
            }
            else Reward((int)got, (int)xp, T("Elimination de ", "Eliminated ") + m.Name + tail);
            // serie d'eliminations
            streak++;
            lock (lk) if (streak > profil.BestStreak) profil.BestStreak = streak;
            int bonus = streak == 3 ? 30 : streak == 5 ? 60 : streak == 8 ? 100 : streak == 12 ? 200 : 0;
            if (bonus > 0)
            {
                long bg = profil.Gain("streak", bonus, 500, 600000, bonus);
                lock (lk) { announce = T("SERIE DE ", "KILL STREAK: ") + streak + " !"; announceKind = 1; announceSeq++; }
                Reward((int)bg, bonus, T("Serie de ", "Streak of ") + streak + T(" eliminations !", " kills!"));
                SendNote(2, streak);
            }
        }

        // ---------------- petites annonces a tout le monde (niveau, serie, fantome)
        void SendNote(int op, int value)
        {
            if (SessionId == 0) return;
            SendMsg(MSG_NOTE, 0, Build(w => { w.Write((byte)op); w.Write(value); }));
        }

        void OnNote(uint from, byte[] p)
        {
            if (p.Length < 5) return;
            int op = p[0];
            int value = BitConverter.ToInt32(p, 1);
            MemberInfo m;
            lock (lk) { if (!members.TryGetValue(from, out m) || !m.Verified || m.Banned) return; }
            switch (op)
            {
                case 1:
                    if (value > 1 && value < 1000) AddChat(3, "", m.Name + T(" passe au niveau ", " reached level ") + value + " (" + RankName(Rank.Of(value)) + ") !");
                    break;
                case 2:
                    if (value >= 3 && value < 100) AddChat(3, "", m.Name + T(" est en SERIE DE ", " is on a ") + value + T(" ELIMINATIONS !", " KILL STREAK!"));
                    break;
                case 3:
                    lock (lk) m.GhostUntil = Profil.NowMs() + Math.Max(0, Math.Min(360, value)) * 1000L;
                    break;
            }
        }

        string RankName(int r)
        {
            r = Math.Max(0, Math.Min(Rank.Names.Length - 1, r));
            return Lang.Tr(CurLang, Rank.Names[r], Rank.NamesEn[r]);
        }

        // ---------------- recompenses (bandeau dans le jeu)
        void Reward(int money, int xp, string text)
        {
            OnRewardShown(money, xp, text);
            profil.Save(false);
            int lv = profil.Level;
            if (levelBefore > 0 && lv > levelBefore)
            {
                int before = levelBefore;
                levelBefore = lv;
                long bonus = 0;
                for (int lvl = before + 1; lvl <= lv; lvl++) bonus += lvl * 10;
                int rOld = Rank.Of(before), rNew = Rank.Of(lv);
                if (rNew > rOld) bonus += 100;
                lock (lk) { profil.Money = Math.Min(9999999, profil.Money + bonus); profil.Touch(); }
                profil.Save(true);
                lock (lk)
                {
                    announce = rNew > rOld ? T("NIVEAU ", "LEVEL ") + lv + T(" - NOUVEAU RANG : ", " - NEW RANK: ") + RankName(rNew).ToUpperInvariant() + " !"
                                           : T("NIVEAU ", "LEVEL ") + lv + " !";
                    announceKind = 1;
                    announceSeq++;
                }
                AddChat(3, "", MyName + T(" passe au niveau ", " reached level ") + lv + T(" : +", ": +") + bonus + T(" orbes", " orbs"));
                // objets debloques par ce niveau
                StringBuilder unl = new StringBuilder();
                for (int i = 0; i < Catalog.Count; i++)
                {
                    Catalog.Item it = Catalog.Items[i];
                    if (it != null && !it.Hidden && it.Level > before && it.Level <= lv)
                    {
                        if (unl.Length > 0) unl.Append(", ");
                        unl.Append(Lang.TrFr(CurLang, it.Name));
                    }
                }
                if (unl.Length > 0) AddChat(3, "", T("Debloque : ", "Unlocked: ") + unl.ToString());
                OnRewardShown((int)bonus, 0, T("Bonus de niveau ", "Level bonus ") + lv);
                SendHello(0);
                SendNote(1, lv);
            }
            levelBefore = lv;
            if (text.Length > 0) L(text + (money != 0 ? "  (" + (money > 0 ? "+" : "") + money + T(" orbes)", " orbs)") : ""));
        }

        // ---------------- primes (l'hote technique du monde les tire au sort)
        void OnBounty(uint from, byte[] p)
        {
            if (!InWorld || from != HostId) return;
            BinaryReader r = new BinaryReader(new MemoryStream(p));
            uint target = r.ReadUInt32();
            uint amount = r.ReadUInt32();
            lock (lk)
            {
                bountyId = target;
                bountyAmount = Math.Min(500u, amount);
                bountyStart = Profil.NowMs();
                bountyByCreator = false;
            }
            if (target != 0)
            {
                string n = NameOf(target);
                if (target == MyId)
                {
                    lock (lk) { announce = T("TA TETE EST MISE A PRIX ! SURVIS 4 MINUTES", "YOU HAVE A BOUNTY! SURVIVE 4 MINUTES"); announceKind = 3; announceSeq++; }
                    AddChat(3, "", T("Une prime est mise sur VOUS ! Survivez 4 minutes.", "There is a bounty on YOU! Survive 4 minutes."));
                }
                else
                {
                    lock (lk) { announce = T("PRIME : ", "BOUNTY: ") + n + " (" + amount + T(" orbes)", " orbs)"); announceKind = 3; announceSeq++; }
                    AddChat(3, "", T("Prime de ", "Bounty of ") + amount + T(" orbes sur ", " orbs on ") + n + T(" (point rouge sur la carte)", " (red dot on the map)"));
                }
            }
        }

        void WorldHostTick()
        {
            // l'hote technique lance une prime toutes les 6 minutes (au moins 2 joueurs)
            if (!InWorld || HostId != MyId) return;
            DateTime now = DateTime.UtcNow;
            if (nextBounty == DateTime.MinValue) { nextBounty = now.AddMinutes(2); return; }
            if (now < nextBounty) return;
            nextBounty = now.AddMinutes(6);
            List<uint> cands = new List<uint>();
            lock (lk)
            {
                foreach (PlayerEntry pe in playerList) if (pe.Id != 0) { MemberInfo m; if (pe.Id == MyId || (members.TryGetValue(pe.Id, out m) && m.Verified && !m.Banned)) cands.Add(pe.Id); }
            }
            if (cands.Count < 2) return;
            uint t = cands[new Random().Next(cands.Count)];
            uint amount = 100;
            byte[] payload = Build(w => { w.Write(t); w.Write(amount); });
            SendMsg(MSG_BOUNTY, 0, payload);
            OnBounty(MyId, payload);
        }

        // ---------------- courses
        public void StartRace(float sx, float sy, float sz, float dx, float dy, float dz, string name)
        {
            if (SessionId == 0) return;
            if (raceState == 1 || raceState == 2) { PushFeed(T("Une course est deja en cours", "A race is already running")); return; }
            byte[] nb = new byte[4];
            new Random(Guid.NewGuid().GetHashCode()).NextBytes(nb);
            uint id = BitConverter.ToUInt32(nb, 0) | 1;
            long start = Profil.NowMs() + 12000;
            byte[] payload = Build(w =>
            {
                w.Write((byte)1); w.Write(id); w.Write(sx); w.Write(sy); w.Write(sz); w.Write(dx); w.Write(dy); w.Write(dz);
                WStr(w, name, 24); w.Write(start);
            });
            SendMsg(MSG_RACE, 0, payload);
            OnRace(MyId, payload);
        }

        void OnRace(uint from, byte[] p)
        {
            BinaryReader r = new BinaryReader(new MemoryStream(p));
            int op = r.ReadByte();
            if (op == 1)
            {
                uint id = r.ReadUInt32();
                float sx = r.ReadSingle(), sy = r.ReadSingle(), sz = r.ReadSingle();
                float dx = r.ReadSingle(), dy = r.ReadSingle(), dz = r.ReadSingle();
                string name = Rd.Str(r, 24);
                long start = r.ReadInt64();
                if (raceState == 1 || raceState == 2) return;
                // seuls les joueurs proches du depart (80 m) participent
                byte[] st = LocalStateSnapshot();
                if (st == null) return;
                float mx = BitConverter.ToSingle(st, 12), my = BitConverter.ToSingle(st, 16), mz = BitConverter.ToSingle(st, 20);
                float ddx = mx - sx, ddy = my - sy, ddz = mz - sz;
                if (Math.Sqrt(ddx * ddx + ddy * ddy + ddz * ddz) > 80 * 4096.0) return;
                lock (lk)
                {
                    raceId = id; raceOwner = from; raceState = 1; raceStartMs = Math.Min(start, Profil.NowMs() + 15000);
                    raceX = dx; raceY = dy; raceZ = dz; raceSX = sx; raceSY = sy; raceSZ = sz;
                    raceName = name; raceRank = 0; raceCount = 0; racePot = 0; raceSeq++;
                    raceFinish.Clear(); raceResultSent = false; raceEndMs = 0;
                }
                AddChat(3, "", T("COURSE vers ", "RACE to ") + name + T(" : depart dans 10 secondes !", ": start in 10 seconds!"));
            }
            else if (op == 2)
            {
                uint id = r.ReadUInt32();
                uint ms = r.ReadUInt32();
                lock (lk)
                {
                    if (id != raceId || raceOwner != MyId) return;
                    if (!raceFinish.ContainsKey(from)) raceFinish[from] = ms;
                }
            }
            else if (op == 3)
            {
                uint id = r.ReadUInt32();
                int n = r.ReadByte();
                int rank = 0, count = n;
                for (int i = 0; i < n; i++)
                {
                    uint pid = r.ReadUInt32();
                    r.ReadUInt32();
                    if (pid == MyId) rank = i + 1;
                }
                if (id != raceId || from != raceOwner) return;
                FinishRaceResult(rank, count);
            }
        }

        void FinishRaceResult(int rank, int count)
        {
            lock (lk) { raceState = 3; raceRank = rank; raceCount = count; raceSeq++; raceEndMs = Profil.NowMs(); }
            if (rank <= 0) return;
            int[] money = { 80, 40, 20 };
            int[] xp = { 200, 120, 80 };
            int mo = rank <= 3 ? money[rank - 1] : 10;
            int xo = rank <= 3 ? xp[rank - 1] : 40;
            if (count < 2) { mo = 0; xo = 30; }
            long got = InWorld ? profil.Gain("race", mo, 160, 300000, xo) : 0;
            if (InWorld && rank == 1 && count >= 2) lock (lk) profil.Races++;
            Reward((int)got, InWorld ? xo : 0, T("Course : ", "Race: ") + rank + (rank == 1 ? T("er", "st") : T("e", "th")) + " / " + count);
        }

        void RaceTick()
        {
            long now = Profil.NowMs();
            int state;
            lock (lk) state = raceState;
            if (state == 1 && now >= raceStartMs) { lock (lk) { raceState = 2; raceSeq++; } }
            if (state == 2)
            {
                byte[] st = LocalStateSnapshot();
                if (st != null)
                {
                    float mx = BitConverter.ToSingle(st, 12), my = BitConverter.ToSingle(st, 16), mz = BitConverter.ToSingle(st, 20);
                    float dx = mx - raceX, dy = my - raceY, dz = mz - raceZ;
                    if (Math.Sqrt(dx * dx + dy * dy + dz * dz) < 15 * 4096.0)
                    {
                        uint ms = (uint)Math.Max(0, now - raceStartMs);
                        // plausibilite : pas plus vite que 70 m/s en ligne droite
                        float ex = raceSX - raceX, ey = raceSY - raceY, ez = raceSZ - raceZ;
                        double minMs = Math.Sqrt(ex * ex + ey * ey + ez * ez) / 4096.0 / 70.0 * 1000.0;
                        if (ms >= minMs)
                        {
                            byte[] payload = Build(w => { w.Write((byte)2); w.Write(raceId); w.Write(ms); });
                            lock (lk) { raceState = 3; raceSeq++; raceEndMs = now; }
                            AddChat(3, "", T("Arrivee ! Temps : ", "Finish! Time: ") + (ms / 1000.0).ToString("0.0") + " s");
                            if (raceOwner == MyId) OnRace(MyId, payload);
                            else SendMsg(MSG_RACE, raceOwner, payload);
                        }
                    }
                }
                if (now - raceStartMs > 300000) lock (lk) { raceState = 3; raceSeq++; raceEndMs = now; }
            }
            // l'organisateur publie le classement 20 s apres la premiere arrivee
            if (raceOwner == MyId && raceId != 0 && !raceResultSent)
            {
                List<KeyValuePair<uint, long>> fin;
                lock (lk) fin = new List<KeyValuePair<uint, long>>(raceFinish);
                bool timeUp = now - raceStartMs > 300000;
                long first = long.MaxValue;
                foreach (KeyValuePair<uint, long> kv in fin) first = Math.Min(first, kv.Value);
                if (fin.Count > 0 && (now - raceStartMs - first > 20000 || timeUp))
                {
                    raceResultSent = true;
                    fin.Sort(delegate (KeyValuePair<uint, long> a, KeyValuePair<uint, long> b) { return a.Value.CompareTo(b.Value); });
                    int n = Math.Min(fin.Count, 16);
                    byte[] payload = Build(w => { w.Write((byte)3); w.Write(raceId); w.Write((byte)n); for (int i = 0; i < n; i++) { w.Write(fin[i].Key); w.Write((uint)fin[i].Value); } });
                    SendMsg(MSG_RACE, 0, payload);
                    OnRace(MyId, payload);
                    string podium = "";
                    for (int i = 0; i < Math.Min(3, n); i++) podium += (i + 1) + ". " + NameOf(fin[i].Key) + "  ";
                    AddChat(3, "", T("Resultats : ", "Results: ") + podium);
                }
                else if (timeUp) raceResultSent = true;
            }
            // la course terminee disparait apres 15 s
            if (state == 3 && raceEndMs > 0 && now - raceEndMs > 15000) lock (lk) { raceState = 0; raceSeq++; }
        }

        // ---------------- boucle du monde (appelee par NetLoop)
        void MondeTick()
        {
            if (ident == null) return;
            DateTime now = DateTime.UtcNow;
            if (SessionId == 0) return;
            if ((now - lastHello).TotalSeconds >= 20) SendHello(0);
            if (IsCreateur && InWorld && (now - lastBanList).TotalSeconds >= 60) PublishBanList();
            if ((now - lastWorldTick).TotalSeconds >= 1)
            {
                lastWorldTick = now;
                profil.XpMul = (profil.PowerLeft(5) > 0f || EventDoubleXp()) ? 2 : 1;
                WorldHostTick();
                try { EventTick(); } catch (Exception ex) { L("evenement : " + ex.Message); }
                try { GardeTick(); } catch (Exception ex) { L("garde : " + ex.Message); }
                // prime survecue : 4 minutes sans mourir
                if (bountyId == MyId && bountyStart > 0 && Profil.NowMs() - bountyStart > 240000)
                {
                    lock (lk) { bountyId = 0; bountyAmount = 0; }
                    if (InWorld)
                    {
                        long got = profil.Gain("bounty-survive", 60, 60, 600000, 100);
                        Reward((int)got, 100, T("Prime survecue !", "Bounty survived!"));
                    }
                }
                else if (bountyId != 0 && bountyStart > 0 && Profil.NowMs() - bountyStart > 250000) lock (lk) { bountyId = 0; bountyAmount = 0; }
                // bonus du jour (monde en ligne)
                if (InWorld && GameAttached && (uiFlags & 2) != 0)
                {
                    long day = Profil.NowMs() / 86400000L;
                    bool daily = false;
                    lock (lk) { if (profil.LastDaily != day) { profil.LastDaily = day; profil.Touch(); daily = true; } }
                    if (daily)
                    {
                        lock (lk) { profil.Money += 50; profil.Xp += 50; }
                        Reward(50, 50, T("Bonus du jour : bienvenue dans le monde en ligne !", "Daily bonus: welcome to the online world!"));
                    }
                }
                profil.Save(false);
                // les membres disparus
                lock (lk)
                {
                    List<uint> gone = new List<uint>();
                    foreach (MemberInfo m in members.Values) if ((now - m.Seen).TotalSeconds > 90) gone.Add(m.Id);
                    foreach (uint id in gone) members.Remove(id);
                }
            }
            RaceTick();
        }

        void MondeSessionJoined()
        {
            lock (lk)
            {
                members.Clear();
                bountyId = 0; bountyAmount = 0;
                raceState = 0; raceId = 0;
                frozen = false; muted = false; worldPvpOff = false;
                timeHour = -1f;
                nextBounty = DateTime.MinValue;
            }
            levelBefore = profil != null ? profil.Level : -1;
            if (InWorld && profil != null && profil.BannedUntil > Profil.NowMs() && !IsCreateur)
            {
                PushFeed(T("Vous etes banni de la session publique", "You are banned from the public session"));
                LeaveLater();
                return;
            }
            // les envois se font hors du fil de reception
            ThreadPool.QueueUserWorkItem(delegate { try { MondeSessionJoinedSend(); } catch (Exception) { } });
        }

        void MondeSessionJoinedSend()
        {
            SendHello(0);
            SendMsg(MSG_HELLO_REQ, 0, new byte[0]);
            if (IsCreateur && InWorld) PublishBanList();
            if (IsCreateur && InWorld) { Thread.Sleep(1500); PublishVersion(); }
            EventSyncRequest();
            if (InWorld)
                AddChat(3, "", T("Bienvenue dans le MONDE EN LIGNE ! T = chat, SELECT/TAB = menu et boutique, armes au Naughty Ottsel.", "Welcome to the ONLINE WORLD! T = chat, SELECT/TAB = menu and shop, weapons at the Naughty Ottsel."));
            else
                AddChat(3, "", T("T = chat, TAB = menu des joueurs", "T = chat, TAB = players menu"));
        }

        void MondeSessionLeft()
        {
            lock (lk)
            {
                members.Clear();
                bountyId = 0;
                raceState = 0;
                frozen = false;
                muted = false;
            }
            if (profil != null) profil.Save(true);
        }

        // ---------------- evenements du jeu
        void MondeGameEvent(uint kind, uint player, float dmg, uint mode, float dx, float dy, float dz)
        {
            switch ((int)kind)
            {
                case EV_ORBS:
                    if (!InWorld || (uiFlags & 4) != 0 || selfCheat) break;
                    {
                        int n = (int)Math.Min(50, mode);
                        bool magnet = profil.PowerLeft(6) > 0f;
                        bool rain = OrbRainNearMe();
                        int mul = (magnet ? 2 : 1) * (rain ? 2 : 1);
                        long cap = 90L * mul;
                        long got = profil.Gain("orbs", n * mul, cap, 60000, n * 2);
                        if (got > 0) Reward((int)got, (int)got * 2, magnet || rain ? T("Orbes x", "Orbs x") + mul : "");
                    }
                    break;
                case EV_ENEMY:
                    if (!InWorld || (uiFlags & 4) != 0 || selfCheat) break;
                    {
                        int n = (int)Math.Min(10, mode);
                        long got = profil.Gain("enemy", n, 60, 600000, n * 5);
                        Reward((int)got, n * 5, "");
                    }
                    break;
                case EV_TASK:
                    if (!InWorld || (uiFlags & 4) != 0 || selfCheat) break;
                    OnMissionTaskDone((int)mode);
                    if (mode >= 73 && mode < 138 && profil.TaskRewardAllowed((int)mode, 20 * 60 * 1000L))
                    {
                        long got = profil.Gain("mission", 40, 200, 600000, 150);
                        lock (lk) profil.Missions++;
                        Reward((int)got, 150, T("Mission secondaire reussie !", "Side mission complete!"));
                    }
                    break;
                case EV_BUY:
                    {
                        if (!InWorld) { PushFeed(T("La boutique n'est ouverte que dans le monde en ligne", "The shop is only open in the online world")); break; }
                        int id = (int)mode;
                        bool far = player == 1;   // achat a distance (+50 % sur les objets du Naughty Ottsel)
                        int res = profil.Buy(id, far);
                        string nm = id >= 0 && id < Catalog.Count && Catalog.Items[id] != null ? Lang.TrFr(CurLang, Catalog.Items[id].Name) : "?";
                        switch (res)
                        {
                            case 0:
                                {
                                    Catalog.Item it = Catalog.Items[id];
                                    if (it.Kind == Catalog.KIND_POWER)
                                    {
                                        Reward(-Catalog.PriceFor(id, far), 0, T("Pouvoir active : ", "Power on: ") + nm);
                                        lock (lk) { announce = nm.ToUpperInvariant() + " !"; announceKind = 1; announceSeq++; }
                                        if (it.Bit == 3) SendNote(3, (int)profil.PowerLeft(3));
                                    }
                                    else Reward(-Catalog.PriceFor(id, far), 0, T("Achat : ", "Bought: ") + nm);
                                    if (it.Kind == Catalog.KIND_CONSUMABLE)
                                    {
                                        GameEvent e = new GameEvent();
                                        e.Kind = EVIN_GRANT;
                                        e.Mode = it.Bit;
                                        lock (lk) if (inEvents.Count < 64) inEvents.Enqueue(e);
                                    }
                                    break;
                                }
                            case 2: PushFeed(T("Vous l'avez deja", "You already own it")); break;
                            case 3: PushFeed(T("Achetez d'abord : ", "Buy first: ") + Lang.TrFr(CurLang, Catalog.Items[Catalog.Items[id].Req].Name)); break;
                            case 4: PushFeed(T("Pas assez d'orbes", "Not enough orbs")); break;
                            case 5: PushFeed(T("Niveau ", "Level ") + Catalog.ReqLevel(id) + T(" requis pour : ", " required for: ") + nm); break;
                        }
                        break;
                    }
                case EV_ADMIN:
                    {
                        int cmd = (int)mode;
                        ulong tuid = 0;
                        lock (lk) { MemberInfo m; if (members.TryGetValue(player, out m)) tuid = m.Uid; }
                        if (cmd == CMD_IGNORE)
                        {
                            // ignorer un joueur : seulement chez moi
                            bool now = false;
                            string nm = NameOf(player);
                            lock (lk) { MemberInfo m; if (members.TryGetValue(player, out m)) { m.Muted = !m.Muted; now = m.Muted; } }
                            AddChat(3, "", now ? T("Vous ignorez ", "You ignore ") + nm : T("Vous n'ignorez plus ", "You no longer ignore ") + nm);
                            break;
                        }
                        // niveau ou je suis (pour les teleportations vers moi)
                        byte[] lst = LocalStateSnapshot();
                        if (lst != null)
                        {
                            int ln = 0;
                            while (ln < 16 && lst[64 + ln] != 0) ln++;
                            cmdLevel = Encoding.ASCII.GetString(lst, 64, ln);
                        }
                        if ((cmd == CMD_SET_LEVEL || cmd == CMD_SET_MONEY) && player == 0)
                        {
                            // createur : son propre niveau / ses orbes (rien a envoyer)
                            if (IsCreateur && InWorld) SetMyLevelOrMoney(cmd, (int)dmg, T("toi", "you"));
                            break;
                        }
                        if (cmd == CMD_EVENT)
                        {
                            // lancer un evenement ici (createur, ou hote d'une session privee)
                            // argument = sorte + 100 x (variante + 1) ; player = cible (chasse a l'homme)
                            if (CanModerate)
                            {
                                int code = (int)dmg, evk = code % 100, variant = code / 100 - 1;
                                StartEventAt(evk, dx, dy, dz, cmdLevel, variant, player != 0 ? player : MyId);
                            }
                            break;
                        }
                        if (cmd == CMD_PUBLISH_VERSION)
                        {
                            if (IsCreateur) { PublishVersion(); AddChat(3, "", T("Version officielle publiee : ", "Official version published: ") + Empreinte.Short(Empreinte.Value)); }
                            break;
                        }
                        if (cmd == CMD_UNBAN)
                        {
                            // debannir : le dernier banni (liste du createur)
                            ulong last = 0;
                            lock (lk) foreach (ulong u in profil.BanList) last = u;
                            if (last != 0) SendAdmin(CMD_UNBAN, 0, last, 0, 0, 0, 0, "");
                            break;
                        }
                        SendAdmin(cmd, player, tuid, (int)dmg, dx, dy, dz, cmd == CMD_BRING || cmd == CMD_BRING_ALL ? cmdLevel : "");
                        break;
                    }
                case EV_RACE:
                    {
                        string name = raceNames != null && mode < raceNames.Length ? Lang.TrFr(CurLang, raceNames[mode]) : T("l'arrivee", "the finish");
                        byte[] st = LocalStateSnapshot();
                        if (st == null) break;
                        StartRace(BitConverter.ToSingle(st, 12), BitConverter.ToSingle(st, 16), BitConverter.ToSingle(st, 20), dx, dy, dz, name);
                        break;
                    }
                case Shm.EV_DIED:
                    if (player != 0 && InWorld) SendKillReceipt(player, 0);
                    if (player != 0) OnHuntDeath(player);
                    if (InWorld) lock (lk) { profil.Deaths++; profil.Touch(); }
                    streak = 0;
                    break;
                case EV_VEH_DEAD:
                    if (player != 0 && InWorld) SendKillReceipt(player, 1);
                    break;
                case EV_BOSS_DMG:
                    OnMyBossDamage(mode / 10f);
                    break;
                case EV_BOSS_POS:
                    OnGameBossPos(player, dmg, (int)mode, dx, dy, dz);
                    break;
                case EV_HOUSE:
                    // je suis dans la maison de 'player' (0 = dehors) : annonce aux autres
                    myHouseOwner = mode == 0 ? 0u : player;
                    if (SessionId != 0) SendMsg(MSG_HOUSE, 0, BitConverter.GetBytes(myHouseOwner));
                    lastHouseMsg = DateTime.UtcNow;
                    break;
                case EV_MAIL:
                    {
                        // boite aux lettres de MA maison : le courrier (orbes) accumule depuis la derniere releve
                        int hk = (int)mode / 16, box = (int)mode % 16;
                        if (!InWorld || hk < 0 || hk >= Catalog.HouseItem.Length || !profil.Owns(Catalog.HouseItem[hk])) break;
                        if (myHouseOwner != 0 && myHouseOwner != MyId) { PushFeed(T("Ce n'est pas ta boite aux lettres", "This is not your mailbox")); break; }
                        long got = profil.CollectMail(hk, box);
                        if (got > 0)
                            Reward((int)got, (int)(got / 10), T("Courrier de ta maison : +", "Mail from your house: +") + got + T(" orbes", " orbs"));
                        else
                            PushFeed(T("Boite vide : reviens demain (le courrier s'accumule 3 jours)", "Empty mailbox: come back tomorrow (mail piles up for 3 days)"));
                        profil.Save(true);
                        break;
                    }
                case EV_INVITE:
                    {
                        int hk = (int)mode;
                        if (!InWorld || hk < 0 || hk >= Catalog.HouseItem.Length || !profil.Owns(Catalog.HouseItem[hk])) { PushFeed(T("Achete d'abord cette maison", "Buy this house first")); break; }
                        SendMsg(MSG_INVITE, player, new byte[] { (byte)hk });
                        string tn;
                        lock (lk) { MemberInfo tm; tn = members.TryGetValue(player, out tm) ? tm.Name : "?"; }
                        PushFeed(T("Invitation envoyee a ", "Invitation sent to ") + tn);
                        break;
                    }
                case EV_SHOT:
                    // je tire : les autres joueurs voient le tir
                    if (SessionId != 0)
                        SendMsg(MSG_SHOT, 0, Build(w => { w.Write((byte)mode); w.Write(dx); w.Write(dy); w.Write(dz); }));
                    break;
                case EV_COOP:
                    // coop (session privee) : ce que j'ai termine est termine chez les autres
                    if (SessionId != 0 && (!InWorld || mode != 3))
                        SendMsg(MSG_COOP, 0, Build(w => { w.Write((byte)mode); w.Write(player); }));
                    break;
                case EV_WEAR:
                    if (InWorld) profil.SetWear((int)mode);
                    break;
                case EV_PERSO:
                    if (InWorld && profil.SetPerso((int)mode))
                        PushFeed(T("Personnage : ", "Character: ") + (mode == 0 ? "Jak" : Lang.TrFr(CurLang, Catalog.Items[Profil.PersoItem[mode]].Name)));
                    break;
                case EV_EVENT:
                    OnGameEventAction((int)mode, dx, dy, dz);
                    break;
                case EV_CHEAT:
                    OnSelfCheat((int)mode);
                    break;
            }
        }

        // memes destinations que *ow-race-names* (online-world.gc)
        public static readonly string[] raceNames = {
            "le Naughty Ottsel", "le port de Haven", "le bidonville", "le QG de la resistance", "le centre de Haven", "la zone industrielle",
            "les portes de Spargus", "le corral aux lezards", "l'entree du nid", "le desert (A)", "le desert (D)", "le desert (G)"
        };

        string cmdLevel = "";

        // ---------------- image pour le jeu (zone EXT de game-in.bin)
        void WriteExt(byte[] img, bool alive)
        {
            int b = Ext.Base;
            Array.Clear(img, b + 4, Ext.Size - 8);
            uint flags = 0;
            if (InWorld) flags |= 1;
            if (CanModerate) flags |= 2;
            if (IsCreateur) flags |= 4;
            if (muted) flags |= 8;
            if (frozen) flags |= 16;
            if (ident != null) flags |= 32;
            if (worldPvpOff) flags |= 64;
            if (SessionId != 0 && HostId == MyId) flags |= 128;
            PutU32(img, b + Ext.Flags, flags);
            // touches (menu TAB) : anneau des 16 dernieres
            PutU32(img, b + Ext.KeyWrite, keyWrite);
            for (int i = 0; i < 16; i++) img[b + Ext.Keys + i] = keyRing[i];
            PutU32(img, b + Ext.ChatMode, chatActive ? kbEditMode : 0);
            PutStr(img, b + Ext.ChatInput, chatBuf.ToString(), 96);
            if (profil != null)
            {
                long money, xp;
                ulong feat, sec;
                uint veh, items;
                int kills;
                lock (lk) { money = profil.Money; xp = profil.Xp; feat = profil.Features; sec = profil.Secrets; veh = profil.Vehicles; items = profil.Items; kills = profil.Kills; }
                int lv = Profil.LevelOf(xp);
                PutU32(img, b + Ext.Money, (uint)money);
                PutU32(img, b + Ext.Xp, (uint)Math.Min(uint.MaxValue, xp));
                PutU32(img, b + Ext.Level, (uint)lv);
                PutU32(img, b + Ext.XpBase, (uint)Profil.XpForLevel(lv));
                PutU32(img, b + Ext.XpNext, (uint)Profil.XpForLevel(lv + 1));
                PutU32(img, b + Ext.Kills, (uint)kills);
                Buffer.BlockCopy(BitConverter.GetBytes(feat), 0, img, b + Ext.Features, 8);
                Buffer.BlockCopy(BitConverter.GetBytes(sec), 0, img, b + Ext.Secrets, 8);
                PutU32(img, b + Ext.Vehicles, veh);
                PutU32(img, b + Ext.Items, items);
                PutU32(img, b + Ext.Perso, (uint)profil.Perso);
                PutU32(img, b + Ext.CosmOwned, (uint)profil.Cosm);
                PutU32(img, b + Ext.CosmWear, (uint)profil.WearChecked());
            }
            PutStr(img, b + Ext.CmdText, cmdText, 32);
            PutU32(img, b + Ext.AnnSeq, announceSeq);
            PutU32(img, b + Ext.AnnKind, (uint)announceKind);
            PutStr(img, b + Ext.AnnText, announce, 88);
            PutU32(img, b + Ext.TimeSeq, timeSeq);
            PutU32(img, b + Ext.DayLock, dayLock ? 1u : 0u);
            PutU32(img, b + Ext.Weather, InWorld ? (uint)weather : 0u);
            PutU32(img, b + Ext.LocalJ2, (uint)LocalJ2Flags);
            PutF32(img, b + Ext.TimeHour, timeHour);
            PutU32(img, b + Ext.BountyId, bountyId);
            PutU32(img, b + Ext.BountyAmount, bountyAmount);
            // course
            long now = Profil.NowMs();
            PutU32(img, b + Ext.RaceSeq, raceSeq);
            PutU32(img, b + Ext.RaceState, (uint)raceState);
            PutF32(img, b + Ext.RaceTimer, raceState == 1 ? (raceStartMs - now) / 1000f : raceState == 2 ? (now - raceStartMs) / 1000f : 0f);
            PutF32(img, b + Ext.RaceDest, raceX);
            PutF32(img, b + Ext.RaceDest + 4, raceY);
            PutF32(img, b + Ext.RaceDest + 8, raceZ);
            PutU32(img, b + Ext.RaceRank, (uint)raceRank);
            PutU32(img, b + Ext.RaceCount, (uint)raceCount);
            PutStr(img, b + Ext.RaceName, raceName, 24);
            // prix de la boutique (0..63, 64..95, puis 96..127) et niveau requis
            for (int i = 0; i < Catalog.Count; i++)
            {
                if (i < 64) PutU32(img, b + Ext.Prices + i * 4, (uint)Catalog.Price(i));
                else if (i < 96) PutU32(img, b + Ext.PricesHi + (i - 64) * 4, (uint)Catalog.Price(i));
                else PutU32(img, b + Ext.PricesX + (i - 96) * 4, (uint)Catalog.Price(i));
                if (i < 96) img[b + Ext.ReqLevel + i] = (byte)Math.Min(255, Catalog.ReqLevel(i));
                else img[b + Ext.ReqLevelX + (i - 96)] = (byte)Math.Min(255, Catalog.ReqLevel(i));
            }
            // super-pouvoirs : secondes restantes
            if (profil != null)
                for (int i = 0; i < Catalog.PowerCount; i++) PutF32(img, b + Ext.PowerLeft + i * 4, profil.PowerLeft(i));
            PutU32(img, b + Ext.Streak, (uint)streak);
            PutU32(img, b + Ext.XpMul, (uint)(profil != null ? profil.XpMul : 1));
            if (profil != null) { int dd; lock (lk) dd = profil.Deaths; PutU32(img, b + Ext.Deaths, (uint)dd); }
            PutU32(img, b + Ext.WarnSeq, warnSeq);
            PutStr(img, b + Ext.WarnText, warnText, 64);
            WriteEventExt(img, b);
            // chat
            PutU32(img, b + Ext.ChatWrite, chatWrite);
            for (int i = 0; i < chat.Count && i < 10; i++)
            {
                ChatLine c = chat[chat.Count - 1 - i];
                int o = b + Ext.Chat + i * Ext.ChatSize;
                PutU32(img, o, (uint)c.Color);
                PutStr(img, o + 4, c.Name, 16);
                PutStr(img, o + 20, c.Text, 88);
            }
            // recompense
            PutU32(img, b + Ext.RewardSeq, rewardSeqOut);
            PutU32(img, b + Ext.RewardMoney, (uint)rewardMoneyOut);
            PutU32(img, b + Ext.RewardXp, (uint)rewardXpOut);
            PutStr(img, b + Ext.RewardText, rewardTextOut, 48);
            // joueurs : niveau, position, drapeaux (meme ordre que la liste de online-shm)
            int pn = SessionId != 0 ? Math.Min(playerList.Count, Proto.MaxPlayersPerSession) : 0;
            for (int i = 0; i < pn; i++)
            {
                PlayerEntry pe = playerList[i];
                int o = b + Ext.Players + i * Ext.PlayerSize;
                MemberInfo m = null;
                if (pe.Id != MyId) members.TryGetValue(pe.Id, out m);
                int pf = 0;
                int lv = 1;
                if (pe.Id == MyId)
                {
                    pf |= 256;
                    if (IsCreateur) pf |= 1;
                    lv = profil != null ? profil.Level : 1;
                    if (frozen) pf |= 64;
                }
                else if (m != null)
                {
                    if ((m.Flags & 2) != 0) pf |= 1;
                    if (m.Muted) pf |= 4;
                    if (m.Suspect) pf |= 32;
                    if (m.Frozen) pf |= 64;
                    if (m.Banned) pf |= 128;
                    if (m.Verified) pf |= 512;
                    lv = m.Level;
                }
                if ((pe.Flags & Proto.PFLAG_HOST) != 0) pf |= 2;
                if (pe.Id == bountyId && bountyId != 0) pf |= 8;
                if ((pe.Flags & Proto.PFLAG_DEAD) != 0) pf |= 1024;
                PutU32(img, o, pe.Id);
                PutF32(img, o + 4, pe.X);
                PutF32(img, o + 8, pe.Y);
                PutF32(img, o + 12, pe.Z);
                img[o + 16] = (byte)(lv & 0xff);
                img[o + 17] = (byte)(lv >> 8);
                img[o + 18] = (byte)(pf & 0xff);
                img[o + 19] = (byte)(pf >> 8);
                PutU32(img, o + 20, pe.HasPos ? 1u : 0u);
                // octet 24 : 1 = fantome (invisible sur les cartes), 2 = version modifiee / suspect
                byte f2 = 0;
                if (m != null && m.GhostUntil > Profil.NowMs()) f2 |= 1;
                if (m != null && (m.BadVersion || m.Suspect)) f2 |= 2;
                if (pe.Id == MyId && profil != null && profil.PowerLeft(3) > 0f) f2 |= 1;
                img[o + 24] = f2;
            }
            PutU32(img, b + Ext.PlayerCount, (uint)pn);
        }

        uint rewardSeqOut;
        int rewardMoneyOut, rewardXpOut;
        string rewardTextOut = "";
        readonly byte[] keyRing = new byte[16];

        // ---------------- clavier : T (chat), TAB (menu), fleches / Entree / Echap (menu)
        readonly StringBuilder chatBuf = new StringBuilder();
        readonly bool[] kPrev = new bool[256];
        bool kInit;

        void PushKey(byte k)
        {
            lock (lk)
            {
                keyRing[keyWrite & 15] = k;
                keyWrite++;
            }
        }

        // touches du menu (codes compris par le jeu)
        public const byte KEY_UP = 1, KEY_DOWN = 2, KEY_LEFT = 3, KEY_RIGHT = 4, KEY_ENTER = 5, KEY_ESC = 6, KEY_TAB = 7,
            KEY_PGUP = 8, KEY_PGDN = 9, KEY_T = 10, KEY_B = 11, KEY_M = 12, KEY_H = 13;

        void GameKeysTick(bool alive)
        {
            if (!alive) { kInit = false; return; }
            bool fg = GameInForeground();
            if (!kInit)
            {
                for (int vk = 0; vk < 256; vk++) kPrev[vk] = Down(vk);
                kInit = true;
                return;
            }
            bool textMode = kbEditMode == 1 || kbEditMode == 2 || kbEditMode == 3;
            int[] watch = { 0x26, 0x28, 0x25, 0x27, 0x0D, 0x1B, 0x09, 0x21, 0x22, 0x54, 0x42, 0x4D, 0x48 };
            byte[] codes = { KEY_UP, KEY_DOWN, KEY_LEFT, KEY_RIGHT, KEY_ENTER, KEY_ESC, KEY_TAB, KEY_PGUP, KEY_PGDN, KEY_T, KEY_B, KEY_M, KEY_H };
            for (int i = 0; i < watch.Length; i++)
            {
                int vk = watch[i];
                bool d = Down(vk);
                bool pressed = d && !kPrev[vk];
                if (!textMode) kPrev[vk] = d;
                if (pressed && fg && !textMode) PushKey(codes[i]);
            }
        }

        // saisie du chat (kb-edit 2 = chat, 3 = annonce)
        bool chatActive;
        uint chatCmd, chatSeq;
        readonly bool[] chatPrev = new bool[256];

        void ChatSend()
        {
            chatSeq++;
            bridge.WriteU32(Shm.KbCmd, chatCmd);
            bridge.WriteU32(Shm.KbSeq, chatSeq + 0x10000000u);
        }

        void ChatKeyboardTick(uint editMode)
        {
            if (editMode != 2 && editMode != 3)
            {
                chatActive = false;
                return;
            }
            if (!chatActive)
            {
                chatActive = true;
                chatCmd = 0;
                chatBuf.Length = 0;
                for (int vk = 0; vk < 256; vk++) chatPrev[vk] = Down(vk);
                ChatSend();
                return;
            }
            if (chatCmd != 0 || !GameInForeground()) return;
            bool changed = false;
            bool shift = (GetKeyState(0x10) & 0x8000) != 0;
            bool caps = (GetKeyState(0x14) & 1) != 0;
            for (int vk = 8; vk < 256; vk++)
            {
                bool d = Down(vk);
                bool pressed = d && !chatPrev[vk];
                chatPrev[vk] = d;
                if (!pressed) continue;
                char c = '\0';
                if (vk >= 0x41 && vk <= 0x5A) c = (char)((shift ^ caps ? 'A' : 'a') + (vk - 0x41));
                else if (vk >= 0x30 && vk <= 0x39) c = shift ? (char)('0' + (vk - 0x30)) : KeyAzertyDigit(vk - 0x30);
                else if (vk >= 0x60 && vk <= 0x69) c = (char)('0' + (vk - 0x60));
                else if (vk == 0x20) c = ' ';
                else if (vk == 0xBC) c = shift ? '?' : ',';
                else if (vk == 0xBE) c = shift ? '.' : ';';
                else if (vk == 0xBF) c = shift ? '/' : ':';
                else if (vk == 0xDF) c = '!';
                else if (vk == 0xBB) c = shift ? '+' : '=';
                else if (vk == 0xDB) c = shift ? '\'' : ')';
                else if (vk == 0x6A) c = '*';
                else if (vk == 0x6B) c = '+';
                else if (vk == 0x6D) c = '-';
                else if (vk == 0x6E) c = '.';
                else if (vk == 0x6F) c = '/';
                else if (vk == 0x08) { if (chatBuf.Length > 0) { chatBuf.Length--; changed = true; } }
                else if (vk == 0x0D)
                {
                    string text = chatBuf.ToString();
                    if (editMode == 3) { if (text.Trim().Length > 0) SendAdmin(CMD_ANNOUNCE, 0, 0, 0, 0, 0, 0, text); }
                    else SendChat(text);
                    chatBuf.Length = 0;
                    chatCmd = 1;
                    changed = true;
                }
                else if (vk == 0x1B) { chatBuf.Length = 0; chatCmd = 2; changed = true; }
                if (c != '\0' && chatBuf.Length < 80) { chatBuf.Append(c); changed = true; }
            }
            if (changed) ChatSend();
        }

        // clavier AZERTY : chiffres sans Maj = caracteres
        static char KeyAzertyDigit(int d)
        {
            // & e " ' ( - e _ c a  (sans accents : la police du jeu)
            string s = "a&e\"'(-e_c";
            return d >= 0 && d < 10 ? s[d] : '?';
        }

        // appele a chaque image du pont
        void MondeBridgeTick(byte[] g, bool alive)
        {
            if (!alive) return;
            uiMode = BitConverter.ToUInt32(g, Shm.UiMode);
            uiFlags = BitConverter.ToUInt32(g, Shm.UiFlags);
            kbEditMode = BitConverter.ToUInt32(g, Shm.KbEdit);
        }

        void OnRewardShown(int money, int xp, string text)
        {
            lock (lk) { rewardSeqOut++; rewardMoneyOut = money; rewardXpOut = xp; rewardTextOut = text; }
        }

        // ---------------- raccourcis pour les tests / la fenetre
        public int MemberCount { get { lock (lk) return members.Count; } }
        public int VerifiedCount { get { lock (lk) { int n = 0; foreach (MemberInfo m in members.Values) if (m.Verified) n++; return n; } } }
        public List<string> ChatSnapshot() { lock (lk) { List<string> l = new List<string>(); foreach (ChatLine c in chat) l.Add((c.Name.Length > 0 ? c.Name + ": " : "") + c.Text); return l; } }
        public bool IsFrozen { get { return frozen; } }
        public bool IsMuted { get { return muted; } }
        public void TestChat(string s) { SendChat(s); }
        public uint TestBountyId { get { return bountyId; } }
        public uint TestBountyAmount { get { return bountyAmount; } }
        public void TestAdmin(int cmd, uint target, int arg, string text)
        {
            ulong tuid = 0;
            lock (lk) { MemberInfo m; if (members.TryGetValue(target, out m)) tuid = m.Uid; }
            SendAdmin(cmd, target, tuid, arg, 0, 0, 0, text);
        }
        public void TestGameEvent(int kind, uint player, float dmg, uint mode) { MondeGameEvent((uint)kind, player, dmg, mode, 0, 0, 0); }
        public long TestMoney { get { lock (lk) return profil != null ? profil.Money : -1; } }
        public int TestLevel { get { return profil != null ? profil.Level : 0; } }
        public int TestBuy(int id) { return profil.Buy(id); }
        public bool TestPerso(int k) { return profil.SetPerso(k); }
        public int TestWear { get { return profil.WearChecked(); } }
        public void TestSetMoney(long m) { lock (lk) { profil.Money = m; profil.Touch(); } }
        public void TestSetXp(long x) { lock (lk) { profil.Xp = x; profil.Touch(); } levelBefore = profil.Level; }
        public void TestSendHitMode(uint target, float dmg, int mode)
        {
            Send(new PacketWriter(Proto.C_HIT).U32(target).F32(dmg).U8(mode).F32(1f).F32(0f).F32(0f).ToArray());
        }
    }

    // zone EXT de game-in.bin (apres les poses) : doit rester identique a online-world.gc
    static class Ext
    {
        public const int Base = 0xE810, Size = 0x2000;
        public const int Flags = 4, KeyWrite = 8, ChatMode = 12, Keys = 16, ChatInput = 32,
            Money = 128, Xp = 132, Level = 136, XpBase = 140, XpNext = 144, Kills = 148,
            Features = 152, Secrets = 160, Vehicles = 168, Items = 172,
            CmdText = 208, AnnSeq = 240, AnnKind = 244, AnnText = 248,
            TimeSeq = 336, TimeHour = 340, BountyId = 344, BountyAmount = 348,
            RaceSeq = 352, RaceState = 356, RaceTimer = 360, RacePot = 364, RaceDest = 368, RaceRank = 384, RaceCount = 388, RaceName = 392,
            Prices = 416, ChatWrite = 672, Chat = 688, ChatSize = 112,
            Players = 1808, PlayerSize = 32, PlayerCount = 5008,
            RewardSeq = 5024, RewardMoney = 5028, RewardXp = 5032, RewardText = 5036,
            Deaths = 5084, PricesHi = 5088, ReqLevel = 5216, PowerLeft = 5312,
            EvSeq = 5344, EvKind = 5348, EvState = 5352, EvTimer = 5356, EvPos = 5360, EvHp = 5376, EvMyDmg = 5380,
            EvCount = 5384, EvId = 5388, EvName = 5392, EvLevel = 5424, EvRadius = 5440, EvBoss = 5444, EvScore = 5448,
            EvTop = 5452, Streak = 5516, WarnSeq = 5520, WarnText = 5524, XpMul = 5588,
            BossSeq = 5600, BossDriver = 5604, BossAnim = 5608, BossPos = 5616, EvTarget = 5632, EvDur = 5636, MapFlags = 5640, Perso = 5644, PricesX = 5648, ReqLevelX = 5776, DayLock = 5808, LocalJ2 = 5812, Weather = 5816, CosmOwned = 5820, CosmWear = 5824,
            Tail = Size - 4;
    }
}
