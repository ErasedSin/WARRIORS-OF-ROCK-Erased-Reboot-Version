// re-wor launcher: edits the port's settings (re_wor.toml), writes patched game
// scripts into the mods folder for graphics toggles, and starts the game.
// Targets .NET Framework 4.x so the compiler that ships with Windows can build it.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Erased Reboot Version launcher")]
[assembly: System.Reflection.AssemblyProduct("Erased Reboot Version (re-wor)")]

namespace ReWor
{
    static class QbKey
    {
        static readonly uint[] Table = BuildTable();

        static uint[] BuildTable()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[i] = c;
            }
            return t;
        }

        // Neversoft QBKey: CRC32 of the lowercase name without the final xor.
        public static uint Of(string name)
        {
            uint crc = 0xFFFFFFFFu;
            foreach (byte b in Encoding.ASCII.GetBytes(name.ToLowerInvariant()))
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }

    // The game's ".xen" archives are split into "CHNK" blocks of raw deflate data.
    static class Chnk
    {
        const int HeaderSize = 0x80;
        const int ChunkSize = 0x80000;
        const int Align = 0x800;

        static uint ReadBE(byte[] d, int o)
        {
            return (uint)(d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3]);
        }

        static void WriteBE(byte[] d, int o, uint v)
        {
            d[o] = (byte)(v >> 24); d[o + 1] = (byte)(v >> 16); d[o + 2] = (byte)(v >> 8); d[o + 3] = (byte)v;
        }

        public static bool IsChunked(byte[] d)
        {
            return d.Length >= 4 && d[0] == 'C' && d[1] == 'H' && d[2] == 'N' && d[3] == 'K';
        }

        public static byte[] Decompress(byte[] d)
        {
            var output = new MemoryStream();
            int off = 0;
            while (off + HeaderSize <= d.Length && ReadBE(d, off) == 0x43484E4B)
            {
                int hs = (int)ReadBE(d, off + 4);
                int csz = (int)ReadBE(d, off + 8);
                uint next = ReadBE(d, off + 12);
                using (var src = new MemoryStream(d, off + hs, csz))
                using (var inflate = new DeflateStream(src, CompressionMode.Decompress))
                    inflate.CopyTo(output);
                if (next == 0xFFFFFFFFu || next == 0) break;
                off += (int)next;
            }
            return output.ToArray();
        }

        public static byte[] Compress(byte[] u)
        {
            var blocks = new List<byte[]>();
            var sizes = new List<int>();
            for (int i = 0; i < u.Length; i += ChunkSize)
            {
                int n = Math.Min(ChunkSize, u.Length - i);
                var ms = new MemoryStream();
                using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, true))
                    deflate.Write(u, i, n);
                blocks.Add(ms.ToArray());
                sizes.Add(n);
            }

            var output = new MemoryStream();
            int uoff = 0;
            for (int i = 0; i < blocks.Count; i++)
            {
                bool last = i == blocks.Count - 1;
                int alen = AlignUp(HeaderSize + blocks[i].Length);
                int nextLen = last ? 0 : AlignUp(HeaderSize + blocks[i + 1].Length);
                var header = new byte[HeaderSize];
                WriteBE(header, 0, 0x43484E4B);
                WriteBE(header, 4, HeaderSize);
                WriteBE(header, 8, (uint)blocks[i].Length);
                WriteBE(header, 12, last ? 0xFFFFFFFFu : (uint)alen);
                WriteBE(header, 16, (uint)nextLen);
                WriteBE(header, 20, (uint)sizes[i]);
                WriteBE(header, 24, (uint)uoff);
                output.Write(header, 0, header.Length);
                output.Write(blocks[i], 0, blocks[i].Length);
                int pad = (last ? AlignUp(HeaderSize + blocks[i].Length, 0x10000) : alen) - HeaderSize - blocks[i].Length;
                output.Write(new byte[pad], 0, pad);
                uoff += sizes[i];
            }
            return output.ToArray();
        }

        static int AlignUp(int v) { return AlignUp(v, Align); }
        static int AlignUp(int v, int a) { return (v + a - 1) & ~(a - 1); }

        // Sets every top-level SectionInteger with this name. Returns how many were found.
        public static int SetSectionInteger(byte[] u, string name, uint value)
        {
            uint key = QbKey.Of(name);
            int found = 0;
            for (int i = 4; i + 12 <= u.Length; i += 4)
            {
                if (ReadBE(u, i) == key && ReadBE(u, i - 4) == 0x00200100u)
                {
                    WriteBE(u, i + 8, value);
                    found++;
                }
            }
            return found;
        }

        // Screen effects live in the TOD (lighting) profiles as array entries
        // shaped like { name = ... type = <fx type> on = 1 ... }. Turning "on" off
        // keeps the file size intact and makes the engine skip the whole pass.
        // Returns how many entries of this type were found.
        public static int SetScreenFxOn(byte[] u, uint fxType, bool on)
        {
            uint onKey = QbKey.Of("on");
            uint typeKey = QbKey.Of("type");
            int found = 0;
            for (int i = 16; i + 12 <= u.Length; i += 4)
            {
                if (ReadBE(u, i) == 0x00010100u && ReadBE(u, i + 4) == onKey &&
                    ReadBE(u, i - 16) == 0x00010D00u && ReadBE(u, i - 12) == typeKey && ReadBE(u, i - 8) == fxType)
                {
                    WriteBE(u, i + 8, on ? 1u : 0u);
                    found++;
                }
            }
            return found;
        }
    }

    class Settings
    {
        readonly string path;
        readonly List<string> lines = new List<string>();

        public Settings(string path)
        {
            this.path = path;
            if (File.Exists(path)) lines.AddRange(File.ReadAllLines(path));
        }

        int Find(string key)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                string l = lines[i].TrimStart();
                if (l.StartsWith("#")) continue;
                int eq = l.IndexOf('=');
                if (eq > 0 && l.Substring(0, eq).Trim() == key) return i;
            }
            return -1;
        }

        public string Get(string key, string fallback)
        {
            int i = Find(key);
            if (i < 0) return fallback;
            string v = lines[i].Substring(lines[i].IndexOf('=') + 1).Trim();
            if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"') v = v.Substring(1, v.Length - 2);
            return v;
        }

        public bool GetBool(string key, bool fallback)
        {
            return Get(key, fallback ? "true" : "false").Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        public int GetInt(string key, int fallback)
        {
            int v;
            return int.TryParse(Get(key, ""), out v) ? v : fallback;
        }

        void SetRaw(string key, string raw)
        {
            string line = key + " = " + raw;
            int i = Find(key);
            if (i >= 0) lines[i] = line; else lines.Add(line);
        }

        public void Set(string key, bool v) { SetRaw(key, v ? "true" : "false"); }
        public void Set(string key, int v) { SetRaw(key, v.ToString()); }
        public void Set(string key, string v) { SetRaw(key, "\"" + v + "\""); }

        public void Remove(string key)
        {
            int i = Find(key);
            if (i >= 0) lines.RemoveAt(i);
        }

        public void Save()
        {
            File.WriteAllLines(path, lines.ToArray(), new UTF8Encoding(false));
        }
    }

    class Choice
    {
        public string Label;
        public string Value;
        public Choice(string label, string value) { Label = label; Value = value; }
        public override string ToString() { return Label; }
    }

    // Every user-facing string, in Portuguese and English. The launcher starts in
    // the Windows language and remembers the choice made with the PT/EN switch.
    static class Strings
    {
        public static bool English;

        static readonly Dictionary<string, string[]> table = new Dictionary<string, string[]>
        {
            { "subtitle", new[] { "re-wor  ·  port para PC de Guitar Hero: Warriors of Rock", "re-wor  ·  PC port of Guitar Hero: Warriors of Rock" } },
            { "seal_title", new[] { "CÓPIA ORIGINAL OBRIGATÓRIA", "ORIGINAL COPY REQUIRED" } },
            { "seal_text", new[] {
                "Este projeto não inclui nenhum arquivo do jogo e o autor não distribui o jogo. Use apenas a sua própria cópia de Guitar Hero: Warriors of Rock para Xbox 360. Diga não à pirataria.",
                "This project includes no game files and its author does not distribute the game. Use only your own copy of Guitar Hero: Warriors of Rock for Xbox 360. Say no to piracy." } },
            { "tab_game", new[] { "JOGO", "GAME" } },
            { "tab_video", new[] { "VÍDEO", "VIDEO" } },
            { "tab_gfx", new[] { "GRÁFICOS", "GRAPHICS" } },
            { "tab_controls", new[] { "CONTROLES", "CONTROLS" } },
            { "dlc_button", new[] { "PASTA DE DLC", "DLC FOLDER" } },
            { "dlc_note", new[] {
                "DLC e músicas customizadas: coloque os pacotes do Xbox 360 (arquivos CON ou LIVE) na pasta dlc; eles são instalados ao abrir o jogo. Para músicas do Onyx, inclua também o cache que o Onyx gera (Other tools > GH:WoR cache).",
                "DLC and custom songs: put the Xbox 360 packages (CON or LIVE files) in the dlc folder; they are installed when the game starts. For Onyx songs, add the cache Onyx makes too (Other tools > GH:WoR cache)." } },
            { "tab_keyboard", new[] { "TECLADO", "KEYBOARD" } },
            { "kb_enabled", new[] { "Jogar no teclado (como guitarra)", "Play on the keyboard (as a guitar)" } },
            { "kb_asjkl", new[] { "A S J K L + setas (estilo Clone Hero)", "A S J K L + arrows (Clone Hero style)" } },
            { "kb_numbers", new[] { "1 2 3 4 5 + setas", "1 2 3 4 5 + arrows" } },
            { "kb_start", new[] { "Start", "Start" } },
            { "autostrum", new[] { "Palhetada automática: cada traste palheta sozinho, só durante a música", "Auto-strum: each fret strums by itself, only during a song" } },
            { "kb_note", new[] {
                "Nomes de teclas: A-Z, 0-9, F1-F12, Up Down Left Right, Space, Return, Backspace, Tab, Semicolon, Comma, Numpad0-9. Várias teclas: A,Left. F1-F5, F7 e F11 já são atalhos do jogo.",
                "Key names: A-Z, 0-9, F1-F12, Up Down Left Right, Space, Return, Backspace, Tab, Semicolon, Comma, Numpad0-9. Several keys: A,Left. F1-F5, F7 and F11 are already game shortcuts." } },
            { "pad_preset", new[] { "Predefinição", "Preset" } },
            { "preset_classic", new[] { "Clássico (A B Y X LB, palheta no direcional)", "Classic (A B Y X LB, strum on the d-pad)" } },
            { "preset_triggers", new[] { "Com gatilhos (LT LB RB RT A, alavanca no analógico)", "Triggers (LT LB RB RT A, whammy on the stick)" } },
            { "preset_custom", new[] { "Personalizado", "Custom" } },
            { "fret_green", new[] { "Verde", "Green" } },
            { "fret_red", new[] { "Vermelho", "Red" } },
            { "fret_yellow", new[] { "Amarelo", "Yellow" } },
            { "fret_blue", new[] { "Azul", "Blue" } },
            { "fret_orange", new[] { "Laranja", "Orange" } },
            { "strum_up", new[] { "Palheta cima", "Strum up" } },
            { "strum_down", new[] { "Palheta baixo", "Strum down" } },
            { "star_power", new[] { "Star Power", "Star Power" } },
            { "whammy", new[] { "Alavanca", "Whammy" } },
            { "controls_note", new[] {
                "Vale para controles comuns (Xbox ou PlayStation); guitarras de verdade não mudam. Vários botões juntos: A|LB. Gatilhos e direções do analógico também: LT, RT, LS_UP, RS_DOWN...",
                "Applies to regular controllers (Xbox or PlayStation); real guitars are unchanged. Several buttons: A|LB. Triggers and stick directions too: LT, RT, LS_UP, RS_DOWN..." } },
            { "folder_label", new[] { "Pasta com os arquivos do jogo (onde fica o default.xex)", "Game files folder (where default.xex is)" } },
            { "choose", new[] { "ESCOLHER", "BROWSE" } },
            { "no_folder", new[] { "Nenhuma pasta escolhida ainda.", "No folder selected yet." } },
            { "game_lang", new[] { "Idioma do jogo", "Game language" } },
            { "pad_guitar", new[] { "Controle comum toca como guitarra (A B Y X LB, palheta no direcional)", "Regular controller plays as a guitar (A B Y X LB, strum on the d-pad)" } },
            { "game_note", new[] {
                "Use os arquivos extraídos da sua cópia do jogo: o default.xex e a pasta data. Eles ficam onde estão; o launcher só guarda o caminho.",
                "Use the files extracted from your copy of the game: default.xex and the data folder. They stay where they are; the launcher only stores the path." } },
            { "folder_dialog", new[] { "Escolha a pasta com os arquivos do jogo (default.xex e a pasta data).", "Choose the folder with the game files (default.xex and the data folder)." } },
            { "bad_folder", new[] { "Essa pasta não tem o default.xex e a pasta data do jogo.", "That folder does not have the game's default.xex and data folder." } },
            { "res_label", new[] { "Resolução interna do jogo", "Game internal resolution" } },
            { "res_720", new[] { "1280x720 (HD, padrão do Xbox 360)", "1280x720 (HD, Xbox 360 default)" } },
            { "res_480w", new[] { "848x480 (SD widescreen, mais leve)", "848x480 (SD widescreen, lighter)" } },
            { "res_480", new[] { "640x480 (SD 4:3, o mais leve)", "640x480 (SD 4:3, lightest)" } },
            { "res_1080", new[] { "1920x1080 (Full HD)", "1920x1080 (Full HD)" } },
            { "res_uw1080", new[] { "2560x1080 (ultrawide 21:9, experimental)", "2560x1080 (ultrawide 21:9, experimental)" } },
            { "res_uw1440", new[] { "3440x1440 (ultrawide 21:9, experimental)", "3440x1440 (ultrawide 21:9, experimental)" } },
            { "scale_label", new[] { "Resolução interna (abaixo de 720p deixa a GPU mais leve)", "Internal resolution (below 720p is lighter on the GPU)" } },
            { "scale_100", new[] { "100% (normal)", "100% (normal)" } },
            { "scale_75", new[] { "75% (960x540, mais leve)", "75% (960x540, lighter)" } },
            { "scale_50", new[] { "50% (640x360, bem mais leve)", "50% (640x360, much lighter)" } },
            { "scale_33", new[] { "33% (426x240, o mais leve)", "33% (426x240, lightest)" } },
            { "present_label", new[] { "Ajuste da imagem na tela", "Image scaling" } },
            { "present_bilinear", new[] { "Bilinear (mais leve)", "Bilinear (lightest)" } },
            { "present_cas", new[] { "AMD CAS (nitidez)", "AMD CAS (sharpening)" } },
            { "present_fsr", new[] { "AMD FSR (melhor em resolução baixa)", "AMD FSR (best at low resolution)" } },
            { "fullscreen", new[] { "Tela cheia", "Fullscreen" } },
            { "vsync", new[] { "V-Sync (desligado = 60 FPS fixos sem esperar o monitor)", "V-Sync (off = a steady 60 FPS without waiting for the display)" } },
            { "show_fps", new[] { "Mostrar FPS no canto da tela (F2 liga e desliga no jogo)", "Show FPS in the corner (F2 toggles it in game)" } },
            { "video_note", new[] {
                "Ultrawide é experimental: o jogo foi feito para 16:9 e pode esticar ou cortar a imagem.",
                "Ultrawide is experimental: the game was made for 16:9 and may stretch or crop the image." } },
            { "motion_blur", new[] { "Desfoque de movimento (motion blur)", "Motion blur" } },
            { "screen_noise", new[] { "Granulado de tela (screen noise)", "Screen noise" } },
            { "dof", new[] { "Profundidade de campo (DOF, pesado)", "Depth of field (DOF, heavy)" } },
            { "bloom", new[] { "Brilho das luzes (bloom, pesado)", "Light bloom (heavy)" } },
            { "aniso_label", new[] { "Filtro anisotrópico", "Anisotropic filtering" } },
            { "aniso_game", new[] { "Do jogo", "Game default" } },
            { "aniso_off", new[] { "Desligado (mais leve)", "Off (lightest)" } },
            { "aniso_4x", new[] { "4x (padrão do port)", "4x (port default)" } },
            { "gamma16", new[] { "Cores em 16 bits (mais precisas, mais pesadas)", "16-bit color (more accurate, heavier)" } },
            { "gfx_note", new[] {
                "Os quatro primeiros efeitos mexem nos scripts do jogo. O launcher cria uma cópia modificada na pasta mods e nunca altera os seus arquivos originais.",
                "The first four effects change the game scripts. The launcher writes a patched copy to the mods folder and never touches your original files." } },
            { "save", new[] { "SALVAR", "SAVE" } },
            { "play", new[] { "JOGAR", "PLAY" } },
            { "saved", new[] { "Configurações salvas.", "Settings saved." } },
            { "need_folder", new[] { "Escolha primeiro a pasta com os arquivos do jogo, na aba JOGO.", "First choose the game files folder in the GAME tab." } },
            { "save_error", new[] { "Não consegui salvar as configurações:", "Could not save the settings:" } },
            { "missing_exe", new[] { "Não achei o {0} nesta pasta.", "Could not find {0} in this folder." } },
            { "missing_fx", new[] { "Opções não encontradas nos scripts: ", "Options not found in the game scripts: " } },
            { "scripts_missing", new[] { "Arquivo de scripts do jogo não encontrado.", "Game scripts file not found." } },
            { "footer", new[] { "Projeto de fãs, sem ligação com a Activision ou a Neversoft.", "Fan project, not affiliated with Activision or Neversoft." } },
        };

        public static string T(string key)
        {
            return table[key][English ? 1 : 0];
        }
    }

    class LauncherForm : Form
    {
        const string GameExe = "Guitar Hero Warriors of Rock.exe";
        const string ScriptsRel = @"data\compressed\PAK\qb.pab.xen";
        // The depth of field type's name is unknown, only its QBKey. Every
        // DOF_*_tod_manager preset (DOF_Off included) uses it.
        const uint DofFxType = 0xA503619Du;
        const string ConfigName = "re_wor.toml";
        const string CvarPrefix = "rewor_";
        const string LanguageFile = "launcher_language.txt";
        const string Title1 = "ERASED";
        const string Title2 = "REBOOT VERSION";
        // A wide window: a sidebar with the logo and the pages, the page on the right.
        const int W = 1000;
        const int H = 620;
        const int Side = 250;
        const int ContentX = Side + 30;
        const int PageW = W - Side - 60;

        readonly string root = AppDomain.CurrentDomain.BaseDirectory;

        // The release keeps the game exe, its DLLs and settings in a "game"
        // folder so only the launcher sits at the top. A build folder has them
        // next to the launcher instead.
        readonly string gameDir = Directory.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "game"))
            ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "game")
            : AppDomain.CurrentDomain.BaseDirectory;
        readonly Settings settings;
        readonly FontFamily titleFamily = TitleFont.Load();

        ComboBox resolution, drawScale, presentEffect, anisotropy, language;
        CheckBox fullscreen, vsync, gamma16, motionBlur, screenNoise, depthOfField, bloom, showFps, padGuitar;
        ComboBox padPreset;
        readonly Dictionary<string, ComboBox> padFields = new Dictionary<string, ComboBox>();
        bool applyingPreset;

        // cvar suffix (guitar_pad_*), label key, then the classic and the trigger presets.
        static readonly string[][] PadParts =
        {
            new[] { "green", "fret_green", "A", "LT" },
            new[] { "red", "fret_red", "B", "LB" },
            new[] { "yellow", "fret_yellow", "Y", "RB" },
            new[] { "blue", "fret_blue", "X", "RT" },
            new[] { "orange", "fret_orange", "LB", "A" },
            new[] { "strum_up", "strum_up", "UP|LS_UP", "UP|LS_UP" },
            new[] { "strum_down", "strum_down", "DOWN|LS_DOWN", "DOWN|LS_DOWN" },
            new[] { "star_power", "star_power", "BACK|RB", "BACK|Y" },
            new[] { "whammy", "whammy", "RT", "RS" },
        };
        // keybind cvar (the keyboard's pad button it presses), label key, then the A S J K L and 1 2 3 4 5 presets.
        static readonly string[][] KeyParts =
        {
            new[] { "keybind_a", "fret_green", "A", "1" },
            new[] { "keybind_b", "fret_red", "S", "2" },
            new[] { "keybind_y", "fret_yellow", "J", "3" },
            new[] { "keybind_x", "fret_blue", "K", "4" },
            new[] { "keybind_left_shoulder", "fret_orange", "L", "5" },
            new[] { "keybind_dpad_up", "strum_up", "Up", "Up" },
            new[] { "keybind_dpad_down", "strum_down", "Down", "Down" },
            new[] { "keybind_back", "star_power", "Space", "Space" },
            new[] { "keybind_rstick_right", "whammy", "Semicolon", "6" },
            new[] { "keybind_start", "kb_start", "Return", "Return" },
        };
        // The SDK's default binds that would press something else with the guitar keys
        // (A and S moved the left stick, the arrows too): cleared or set for the menus.
        static readonly string[][] KeyClears =
        {
            new[] { "keybind_lstick_up", "" }, new[] { "keybind_lstick_down", "" },
            new[] { "keybind_lstick_left", "" }, new[] { "keybind_lstick_right", "" },
            new[] { "keybind_dpad_left", "Left" }, new[] { "keybind_dpad_right", "Right" },
        };
        CheckBox kbEnabled, autoStrum;
        ComboBox kbPreset;
        readonly Dictionary<string, TextBox> kbFields = new Dictionary<string, TextBox>();
        bool applyingKbPreset;

        static readonly string[] PadSources =
        {
            "A", "B", "X", "Y", "LB", "RB", "LT", "RT", "BACK", "START", "UP", "DOWN", "LEFT", "RIGHT", "LS", "RS",
            "UP|LS_UP", "DOWN|LS_DOWN", "BACK|RB", "A|LB"
        };
        Label status, gameFolderLabel;
        string gameFolder;
        int currentPage = -1;

        static readonly Color Bg = Color.FromArgb(10, 10, 10);
        static readonly Color Panel = Color.FromArgb(22, 22, 22);
        static readonly Color Field = Color.FromArgb(32, 32, 32);
        static readonly Color Red = Color.FromArgb(200, 16, 46);
        static readonly Color RedBright = Color.FromArgb(232, 36, 64);
        static readonly Color RedDark = Color.FromArgb(110, 8, 24);
        static readonly Color Text1 = Color.FromArgb(238, 238, 238);
        static readonly Color Text2 = Color.FromArgb(150, 150, 150);

        readonly List<Button> navButtons = new List<Button>();
        readonly List<Control> pages = new List<Control>();

        public LauncherForm()
        {
            settings = new Settings(Path.Combine(gameDir, ConfigName));
            Strings.English = LoadLanguage();

            Text = "Erased Reboot Version - re-wor";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(W, H);
            BackColor = Bg;
            ForeColor = Text1;
            Font = new Font("Segoe UI", 9.5f);
            DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Path.Combine(gameDir, GameExe)); } catch (Exception) { }

            gameFolder = settings.Get(CvarPrefix + "game_folder", "").Replace('/', '\\');
            if (!IsGameFolder(gameFolder)) gameFolder = NormalizeGameFolder(root);

            BuildUi();
            LoadSettings();
            ShowPage(IsGameFolder(gameFolder) ? 1 : 0);
            Shown += delegate { if (!IsGameFolder(gameFolder)) ChooseGameFolder(); };
        }

        bool LoadLanguage()
        {
            try
            {
                string saved = File.ReadAllText(Path.Combine(gameDir, LanguageFile)).Trim();
                if (saved == "en") return true;
                if (saved == "pt") return false;
            }
            catch (Exception) { }
            return System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "pt";
        }

        // Rebuilds every control in the new language, keeping what the user has
        // picked so far (it is copied into the settings without saving them).
        void SwitchLanguage(bool english)
        {
            if (english == Strings.English) return;
            CollectSettings();
            Strings.English = english;
            try { File.WriteAllText(Path.Combine(gameDir, LanguageFile), english ? "en" : "pt"); } catch (Exception) { }
            int page = currentPage;
            SuspendLayout();
            var old = new List<Control>();
            foreach (Control c in Controls) old.Add(c);
            Controls.Clear();
            foreach (Control c in old) c.Dispose();
            navButtons.Clear();
            pages.Clear();
            BuildUi();
            LoadSettings();
            ShowPage(page < 0 ? 0 : page);
            ResumeLayout();
        }

        Label pageTitle;
        string[] pageNames;

        void BuildUi()
        {
            Controls.Add(BuildSidebar());

            pageTitle = new Label
            {
                Location = new Point(ContentX, 18), Size = new Size(PageW - 110, 44), ForeColor = Color.White,
                BackColor = Bg, Font = new Font(titleFamily, 22f, FontStyle.Regular, GraphicsUnit.Point),
                UseCompatibleTextRendering = true
            };
            Controls.Add(pageTitle);
            Controls.Add(MakeLanguageButton("PT", W - 30 - 82, false));
            Controls.Add(MakeLanguageButton("EN", W - 30 - 40, true));
            Controls.Add(BuildSeal());

            pageNames = new[] { Strings.T("tab_game"), Strings.T("tab_video"), Strings.T("tab_gfx"), Strings.T("tab_controls"), Strings.T("tab_keyboard") };
            Control[] built = { BuildGameTab(), BuildVideoTab(), BuildGraphicsTab(), BuildControlsTab(), BuildKeyboardTab() };
            for (int i = 0; i < built.Length; i++)
            {
                built[i].Location = new Point(ContentX, 142);
                built[i].Size = new Size(PageW, 384);
                pages.Add(built[i]);
                Controls.Add(built[i]);
            }

            status = new Label { Location = new Point(ContentX, 548), Size = new Size(PageW - 300, 40), ForeColor = Text2, TextAlign = ContentAlignment.MiddleLeft };
            Controls.Add(status);

            var save = MakeButton(Strings.T("save"), new Point(W - 30 - 160 - 12 - 120, 544), false);
            save.Size = new Size(120, 48);
            save.Click += delegate { if (Apply()) status.Text = Strings.T("saved"); };
            Controls.Add(save);

            var play = MakeButton(Strings.T("play"), new Point(W - 30 - 160, 544), true);
            play.Size = new Size(160, 48);
            play.Font = new Font("Segoe UI", 13f, FontStyle.Bold);
            play.Click += delegate { if (Apply()) Launch(); };
            Controls.Add(play);
            AcceptButton = play;
        }

        // The sidebar: the logo in the title font, the pages, and the footer.
        Control BuildSidebar()
        {
            var side = new Panel { Location = new Point(0, 0), Size = new Size(Side, H), BackColor = Color.FromArgb(6, 6, 7) };
            side.Paint += delegate(object s, PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using (var glow = new System.Drawing.Drawing2D.LinearGradientBrush(
                    new Rectangle(0, 0, Side, 150), Color.FromArgb(90, 120, 0, 18), Color.FromArgb(0, 6, 6, 7), 90f))
                    g.FillRectangle(glow, 0, 0, Side, 150);
                using (var line = new System.Drawing.Drawing2D.LinearGradientBrush(
                    new Rectangle(Side - 3, 0, 3, H), RedBright, RedDark, 90f))
                    g.FillRectangle(line, Side - 3, 0, 3, H);

                using (var big = new Font(titleFamily, 30f, FontStyle.Regular, GraphicsUnit.Point))
                using (var small = new Font(titleFamily, 14f, FontStyle.Regular, GraphicsUnit.Point))
                using (var shadow = new SolidBrush(Color.FromArgb(160, 90, 0, 14)))
                using (var white = new SolidBrush(Color.White))
                using (var red = new SolidBrush(Red))
                {
                    g.DrawString(Title1, big, shadow, 25, 21);
                    g.DrawString(Title1, big, white, 22, 18);
                    SizeF size1 = g.MeasureString(Title1, big);
                    // The title font's space glyph is very narrow, so the words are
                    // laid out one by one with a fixed gap.
                    float x = 25f;
                    float y = 18 + size1.Height * 0.74f;
                    foreach (string word in Title2.Split(' '))
                    {
                        g.DrawString(word, small, shadow, x + 2, y + 2);
                        g.DrawString(word, small, red, x, y);
                        x += g.MeasureString(word, small).Width + 8f;
                    }
                }
            };
            side.Controls.Add(new Label
            {
                Text = Strings.T("subtitle"), Location = new Point(24, 112), Size = new Size(Side - 40, 32),
                ForeColor = Text2, BackColor = Color.Transparent, Font = new Font("Segoe UI", 8f)
            });

            string[] names = { Strings.T("tab_game"), Strings.T("tab_video"), Strings.T("tab_gfx"), Strings.T("tab_controls"), Strings.T("tab_keyboard") };
            for (int i = 0; i < names.Length; i++)
            {
                int index = i;
                var nav = new Button
                {
                    Text = "   " + names[i], Location = new Point(16, 160 + i * 50), Size = new Size(Side - 35, 44),
                    FlatStyle = FlatStyle.Flat, ForeColor = Text2, BackColor = side.BackColor, Cursor = Cursors.Hand,
                    Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, TabStop = false
                };
                nav.FlatAppearance.BorderSize = 0;
                nav.FlatAppearance.MouseOverBackColor = Color.FromArgb(30, 8, 12);
                nav.Paint += delegate(object s, PaintEventArgs e)
                {
                    if (index != currentPage) return;
                    using (var bar = new SolidBrush(Red)) e.Graphics.FillRectangle(bar, 0, 6, 4, nav.Height - 12);
                };
                nav.Click += delegate { ShowPage(index); };
                navButtons.Add(nav);
                side.Controls.Add(nav);
            }

            side.Controls.Add(new Label
            {
                Text = Strings.T("footer"), Location = new Point(24, H - 56), Size = new Size(Side - 44, 40),
                ForeColor = Color.FromArgb(95, 95, 95), BackColor = Color.Transparent, Font = new Font("Segoe UI", 7.5f)
            });
            return side;
        }

        Button MakeLanguageButton(string text, int x, bool english)
        {
            bool active = Strings.English == english;
            var b = new Button
            {
                Text = text, Location = new Point(x, 26), Size = new Size(38, 26), FlatStyle = FlatStyle.Flat,
                BackColor = active ? Red : Bg, ForeColor = active ? Color.White : Text2,
                Cursor = Cursors.Hand, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), TabStop = false
            };
            b.FlatAppearance.BorderColor = Red;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.MouseOverBackColor = active ? RedBright : RedDark;
            b.Click += delegate { SwitchLanguage(english); };
            return b;
        }

        // Notice that the project ships no game files and needs the player's own
        // copy, drawn as a red shield badge next to the text.
        Control BuildSeal()
        {
            var seal = new Panel { Location = new Point(ContentX, 70), Size = new Size(PageW, 60), BackColor = Color.FromArgb(24, 6, 10) };
            seal.Paint += delegate(object s, PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var border = new Pen(Color.FromArgb(110, 200, 16, 46), 1f)) g.DrawRectangle(border, 0, 0, seal.Width - 1, seal.Height - 1);
                using (var accent = new SolidBrush(Red)) g.FillRectangle(accent, 0, 0, 4, seal.Height);

                var shield = new System.Drawing.Drawing2D.GraphicsPath();
                shield.AddLines(new[]
                {
                    new PointF(30, 9), new PointF(46, 15), new PointF(46, 30),
                    new PointF(30, 52), new PointF(14, 30), new PointF(14, 15)
                });
                shield.CloseFigure();
                using (var fill = new System.Drawing.Drawing2D.LinearGradientBrush(
                    new Rectangle(14, 9, 32, 43), RedBright, RedDark, 90f))
                    g.FillPath(fill, shield);
                using (var check = new Pen(Color.White, 3.2f))
                {
                    check.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                    check.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    check.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                    g.DrawLines(check, new[] { new PointF(23, 29), new PointF(28, 35), new PointF(38, 23) });
                }
                shield.Dispose();
            };
            seal.Controls.Add(new Label
            {
                Text = Strings.T("seal_title"), Location = new Point(58, 6), AutoSize = true,
                ForeColor = RedBright, BackColor = Color.Transparent, Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            });
            seal.Controls.Add(new Label
            {
                Text = Strings.T("seal_text"), Location = new Point(58, 24), Size = new Size(seal.Width - 68, 34),
                ForeColor = Text1, BackColor = Color.Transparent, Font = new Font("Segoe UI", 8f)
            });
            return seal;
        }

        static Button MakeButton(string text, Point location, bool primary)
        {
            var b = new Button
            {
                Text = text, Location = location, Size = new Size(104, 42), FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Red : Field, ForeColor = Color.White, Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 10.5f, FontStyle.Bold)
            };
            b.FlatAppearance.BorderColor = Red;
            b.FlatAppearance.BorderSize = primary ? 0 : 1;
            b.FlatAppearance.MouseOverBackColor = primary ? RedBright : RedDark;
            b.FlatAppearance.MouseDownBackColor = RedDark;
            return b;
        }

        void ShowPage(int index)
        {
            currentPage = index;
            for (int i = 0; i < pages.Count; i++)
            {
                pages[i].Visible = i == index;
                navButtons[i].ForeColor = i == index ? Color.White : Text2;
                navButtons[i].BackColor = i == index ? Color.FromArgb(34, 8, 13) : Color.FromArgb(6, 6, 7);
                navButtons[i].Invalidate();
            }
            if (pageTitle != null && pageNames != null) pageTitle.Text = WithoutAccents(pageNames[index]);
        }

        // The title font has no accented letters (VÍDEO came out as "V DEO").
        static string WithoutAccents(string text)
        {
            var sb = new StringBuilder();
            foreach (char c in text.Normalize(NormalizationForm.FormD))
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        static Panel NewPage()
        {
            var page = new Panel { BackColor = Panel };
            page.Paint += delegate(object s, PaintEventArgs e)
            {
                using (var top = new SolidBrush(Red)) e.Graphics.FillRectangle(top, 0, 0, page.Width, 2);
            };
            return page;
        }

        Control BuildGameTab()
        {
            var page = NewPage();
            AddLabel(page, Strings.T("folder_label"), 18);
            gameFolderLabel = new Label { Location = new Point(18, 44), Size = new Size(PageW - 36, 40), ForeColor = Text2 };
            page.Controls.Add(gameFolderLabel);
            var change = MakeButton(Strings.T("choose"), new Point(18, 92), false);
            change.Size = new Size(120, 34);
            change.Click += delegate { ChooseGameFolder(); };
            page.Controls.Add(change);
            var dlc = MakeButton(Strings.T("dlc_button"), new Point(148, 92), false);
            dlc.Size = new Size(150, 34);
            dlc.Click += delegate { OpenDlcFolder(); };
            page.Controls.Add(dlc);
            AddLabel(page, Strings.T("game_lang"), 144);
            language = AddCombo(page, 164,
                new Choice("English", "1"),
                new Choice("Español", "5"),
                new Choice("Français", "4"),
                new Choice("Deutsch", "3"),
                new Choice("Italiano", "6"));
            AddNote(page, Strings.T("game_note"), 206);
            AddNote(page, Strings.T("dlc_note"), 262);
            ShowGameFolder();
            return page;
        }

        // The game installs what is in this folder when it starts (src/dlc.cpp, rewor_dlc_folder).
        void OpenDlcFolder()
        {
            string folder = Path.Combine(gameDir, "dlc");
            try
            {
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
            }
            catch (Exception e)
            {
                MessageBox.Show(this, e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        static string ToConfigPath(string folder)
        {
            return folder.TrimEnd('\\', '/').Replace('\\', '/');
        }

        static bool IsGameFolder(string folder)
        {
            return !string.IsNullOrEmpty(folder)
                && File.Exists(Path.Combine(folder, "default.xex"))
                && Directory.Exists(Path.Combine(folder, "data"));
        }

        // Accepts the folder itself or a parent that holds an "assets" folder.
        static string NormalizeGameFolder(string folder)
        {
            if (IsGameFolder(folder)) return folder;
            string assets = Path.Combine(folder, "assets");
            return IsGameFolder(assets) ? assets : null;
        }

        void ShowGameFolder()
        {
            gameFolderLabel.Text = IsGameFolder(gameFolder) ? gameFolder : Strings.T("no_folder");
        }

        void ChooseGameFolder()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = Strings.T("folder_dialog");
                dialog.ShowNewFolderButton = false;
                if (IsGameFolder(gameFolder)) dialog.SelectedPath = gameFolder;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                string chosen = NormalizeGameFolder(dialog.SelectedPath);
                if (chosen == null)
                {
                    MessageBox.Show(this, Strings.T("bad_folder"), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                gameFolder = chosen;
                ShowGameFolder();
            }
        }

        Control BuildVideoTab()
        {
            var page = NewPage();
            int y = 16;

            AddLabel(page, Strings.T("res_label"), y);
            resolution = AddCombo(page, y + 20,
                new Choice(Strings.T("res_720"), "1280x720"),
                new Choice(Strings.T("res_480w"), "848x480"),
                new Choice(Strings.T("res_480"), "640x480"),
                new Choice(Strings.T("res_1080"), "1920x1080"),
                new Choice(Strings.T("res_uw1080"), "2560x1080"),
                new Choice(Strings.T("res_uw1440"), "3440x1440"));
            y += 60;

            AddLabel(page, Strings.T("scale_label"), y);
            drawScale = AddCombo(page, y + 20,
                new Choice(Strings.T("scale_100"), "100"),
                new Choice(Strings.T("scale_75"), "75"),
                new Choice(Strings.T("scale_50"), "50"),
                new Choice(Strings.T("scale_33"), "33"));
            y += 60;

            AddLabel(page, Strings.T("present_label"), y);
            presentEffect = AddCombo(page, y + 20,
                new Choice(Strings.T("present_bilinear"), "bilinear"),
                new Choice(Strings.T("present_cas"), "cas"),
                new Choice(Strings.T("present_fsr"), "fsr"));
            y += 60;

            fullscreen = AddCheck(page, Strings.T("fullscreen"), y); y += 28;
            vsync = AddCheck(page, Strings.T("vsync"), y); y += 28;
            showFps = AddCheck(page, Strings.T("show_fps"), y); y += 36;

            AddNote(page, Strings.T("video_note"), y);
            return page;
        }

        Control BuildControlsTab()
        {
            var page = NewPage();
            padGuitar = AddCheck(page, Strings.T("pad_guitar"), 10);
            autoStrum = AddCheck(page, Strings.T("autostrum"), 34);
            AddLabel(page, Strings.T("pad_preset"), 60);
            padPreset = AddCombo(page, 78,
                new Choice(Strings.T("preset_classic"), "classic"),
                new Choice(Strings.T("preset_triggers"), "triggers"),
                new Choice(Strings.T("preset_custom"), "custom"));
            padPreset.SelectedIndexChanged += delegate { ApplyPadPreset(); };

            padFields.Clear();
            for (int i = 0; i < PadParts.Length; i++)
            {
                int column = i < 5 ? 0 : 1;
                int row = i < 5 ? i : i - 5;
                int x = 18 + column * 330;
                int y = 114 + row * 32;
                page.Controls.Add(new Label { Text = Strings.T(PadParts[i][1]), Location = new Point(x, y + 4), Size = new Size(96, 20), ForeColor = Text1 });
                var field = new ComboBox
                {
                    Location = new Point(x + 98, y), Size = new Size(134, 26), DropDownStyle = ComboBoxStyle.DropDown,
                    FlatStyle = FlatStyle.Flat, BackColor = Field, ForeColor = Text1
                };
                field.Items.AddRange(PadParts[i][0] == "whammy" ? new object[] { "RT", "LT", "RS" } : PadSources);
                field.TextChanged += delegate { if (!applyingPreset) SelectChoice(padPreset, MatchPadPreset()); };
                page.Controls.Add(field);
                padFields[PadParts[i][0]] = field;
            }
            AddNote(page, Strings.T("controls_note"), 280);
            return page;
        }

        Control BuildKeyboardTab()
        {
            var page = NewPage();
            kbEnabled = AddCheck(page, Strings.T("kb_enabled"), 10);
            AddLabel(page, Strings.T("pad_preset"), 38);
            kbPreset = AddCombo(page, 56,
                new Choice(Strings.T("kb_asjkl"), "asjkl"),
                new Choice(Strings.T("kb_numbers"), "numbers"),
                new Choice(Strings.T("preset_custom"), "custom"));
            kbPreset.SelectedIndexChanged += delegate { ApplyKbPreset(); };

            kbFields.Clear();
            for (int i = 0; i < KeyParts.Length; i++)
            {
                int column = i < 5 ? 0 : 1;
                int row = i < 5 ? i : i - 5;
                int x = 18 + column * 330;
                int y = 94 + row * 32;
                page.Controls.Add(new Label { Text = Strings.T(KeyParts[i][1]), Location = new Point(x, y + 3), Size = new Size(96, 20), ForeColor = Text1 });
                var field = new TextBox
                {
                    Location = new Point(x + 98, y), Size = new Size(134, 24), BorderStyle = BorderStyle.FixedSingle,
                    BackColor = Field, ForeColor = Text1
                };
                field.TextChanged += delegate { if (!applyingKbPreset) SelectChoice(kbPreset, MatchKbPreset()); };
                page.Controls.Add(field);
                kbFields[KeyParts[i][0]] = field;
            }
            AddNote(page, Strings.T("kb_note"), 260);
            return page;
        }

        void ApplyKbPreset()
        {
            if (applyingKbPreset || kbPreset.SelectedItem == null) return;
            int column = Value(kbPreset) == "asjkl" ? 2 : Value(kbPreset) == "numbers" ? 3 : -1;
            if (column < 0) return;
            applyingKbPreset = true;
            foreach (var part in KeyParts) kbFields[part[0]].Text = part[column];
            applyingKbPreset = false;
        }

        string MatchKbPreset()
        {
            foreach (int column in new[] { 2, 3 })
            {
                bool all = true;
                foreach (var part in KeyParts)
                    if (!string.Equals(kbFields[part[0]].Text.Trim(), part[column], StringComparison.OrdinalIgnoreCase)) all = false;
                if (all) return column == 2 ? "asjkl" : "numbers";
            }
            return "custom";
        }

        void ApplyPadPreset()
        {
            if (applyingPreset || padPreset.SelectedItem == null) return;
            int column = Value(padPreset) == "classic" ? 2 : Value(padPreset) == "triggers" ? 3 : -1;
            if (column < 0) return;
            applyingPreset = true;
            foreach (var part in PadParts) padFields[part[0]].Text = part[column];
            applyingPreset = false;
        }

        string MatchPadPreset()
        {
            foreach (int column in new[] { 2, 3 })
            {
                bool all = true;
                foreach (var part in PadParts)
                    if (!string.Equals(padFields[part[0]].Text.Trim(), part[column], StringComparison.OrdinalIgnoreCase)) all = false;
                if (all) return column == 2 ? "classic" : "triggers";
            }
            return "custom";
        }

        Control BuildGraphicsTab()
        {
            var page = NewPage();
            int y = 16;

            motionBlur = AddCheck(page, Strings.T("motion_blur"), y); y += 28;
            screenNoise = AddCheck(page, Strings.T("screen_noise"), y); y += 28;
            depthOfField = AddCheck(page, Strings.T("dof"), y); y += 28;
            bloom = AddCheck(page, Strings.T("bloom"), y); y += 40;

            AddLabel(page, Strings.T("aniso_label"), y);
            anisotropy = AddCombo(page, y + 20,
                new Choice(Strings.T("aniso_game"), "-1"),
                new Choice(Strings.T("aniso_off"), "0"),
                new Choice("2x", "2"),
                new Choice(Strings.T("aniso_4x"), "3"),
                new Choice("8x", "4"),
                new Choice("16x", "5"));
            y += 60;

            gamma16 = AddCheck(page, Strings.T("gamma16"), y); y += 36;

            AddNote(page, Strings.T("gfx_note"), y);
            return page;
        }

        static void AddLabel(Control parent, string text, int y)
        {
            parent.Controls.Add(new Label { Text = text, Location = new Point(18, y), AutoSize = true, ForeColor = Text1 });
        }

        static void AddNote(Control parent, string text, int y)
        {
            parent.Controls.Add(new Label { Text = text, Location = new Point(18, y), Size = new Size(PageW - 36, 56), ForeColor = Text2 });
        }

        static ComboBox AddCombo(Control parent, int y, params Choice[] items)
        {
            var c = new ComboBox
            {
                Location = new Point(18, y), Size = new Size(PageW - 36, 26), DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat, BackColor = Field, ForeColor = Text1
            };
            c.Items.AddRange(items);
            parent.Controls.Add(c);
            return c;
        }

        static CheckBox AddCheck(Control parent, string text, int y)
        {
            var c = new CheckBox { Text = text, Location = new Point(18, y), AutoSize = true, ForeColor = Text1, FlatStyle = FlatStyle.Flat };
            c.FlatAppearance.BorderColor = Red;
            c.FlatAppearance.CheckedBackColor = Red;
            parent.Controls.Add(c);
            return c;
        }

        static void SelectChoice(ComboBox combo, string value)
        {
            for (int i = 0; i < combo.Items.Count; i++)
            {
                if (((Choice)combo.Items[i]).Value == value) { combo.SelectedIndex = i; return; }
            }
            combo.SelectedIndex = 0;
        }

        static string Value(ComboBox combo)
        {
            return ((Choice)combo.SelectedItem).Value;
        }

        void LoadSettings()
        {
            SelectChoice(resolution, settings.GetInt("video_mode_width", 1280) + "x" + settings.GetInt("video_mode_height", 720));
            SelectChoice(drawScale, settings.GetInt("draw_resolution_percent", 100).ToString());
            SelectChoice(presentEffect, settings.Get("present_effect", "bilinear"));
            SelectChoice(anisotropy, settings.GetInt("anisotropic_override", 3).ToString());
            SelectChoice(language, settings.GetInt("user_language", 1).ToString());
            fullscreen.Checked = settings.GetBool("fullscreen", true);
            vsync.Checked = settings.GetBool("present_vsync", false);
            gamma16.Checked = settings.GetBool("gamma_render_target_as_unorm16", true);
            motionBlur.Checked = settings.GetBool(CvarPrefix + "motion_blur", true);
            screenNoise.Checked = settings.GetBool(CvarPrefix + "screen_noise", true);
            depthOfField.Checked = settings.GetBool(CvarPrefix + "dof", true);
            bloom.Checked = settings.GetBool(CvarPrefix + "bloom", true);
            showFps.Checked = settings.GetBool(CvarPrefix + "show_fps", true);
            padGuitar.Checked = settings.GetBool("gamepad_as_guitar", true);
            applyingPreset = true;
            foreach (var part in PadParts) padFields[part[0]].Text = settings.Get("guitar_pad_" + part[0], part[2]);
            applyingPreset = false;
            SelectChoice(padPreset, MatchPadPreset());
            autoStrum.Checked = settings.GetBool("guitar_autostrum", true);
            kbEnabled.Checked = settings.GetBool("mnk_mode", true);
            applyingKbPreset = true;
            foreach (var part in KeyParts) kbFields[part[0]].Text = settings.Get(part[0], part[2]);
            applyingKbPreset = false;
            SelectChoice(kbPreset, MatchKbPreset());
            ShowGameFolder();
        }

        // Copies the controls into the settings in memory, without saving them.
        void CollectSettings()
        {
            if (IsGameFolder(gameFolder))
            {
                // Forward slashes: the SDK writes string settings back without TOML
                // escaping, so a backslash path would corrupt re_wor.toml.
                settings.Set(CvarPrefix + "game_folder", ToConfigPath(gameFolder));
            }
            string[] res = Value(resolution).Split('x');
            settings.Remove("resolution");
            settings.Set("video_mode_width", int.Parse(res[0]));
            settings.Set("video_mode_height", int.Parse(res[1]));
            // Only written when below 100, so a game built on an SDK without the
            // option never sees it.
            if (Value(drawScale) == "100") settings.Remove("draw_resolution_percent");
            else settings.Set("draw_resolution_percent", int.Parse(Value(drawScale)));
            settings.Set("present_effect", Value(presentEffect));
            settings.Set("anisotropic_override", int.Parse(Value(anisotropy)));
            settings.Set("user_language", int.Parse(Value(language)));
            settings.Set("fullscreen", fullscreen.Checked);
            // The game's own clock (its vertical blanks) always runs at 60 Hz: off, it
            // ran as fast as it could and the notes left the music. The V-Sync switch
            // is the picture's: off, each frame is shown at once (the game is still
            // held at 60 FPS); on, it waits for the display, which on a 60 Hz one tied
            // the game to the display and dropped it to about 50 when a frame was late.
            settings.Set("vsync", true);
            settings.Set("present_vsync", vsync.Checked);
            settings.Set("gamma_render_target_as_unorm16", gamma16.Checked);
            settings.Set(CvarPrefix + "motion_blur", motionBlur.Checked);
            settings.Set(CvarPrefix + "screen_noise", screenNoise.Checked);
            settings.Set(CvarPrefix + "dof", depthOfField.Checked);
            settings.Set(CvarPrefix + "bloom", bloom.Checked);
            settings.Set(CvarPrefix + "show_fps", showFps.Checked);
            settings.Set("gamepad_as_guitar", padGuitar.Checked);
            foreach (var part in PadParts)
            {
                string v = padFields[part[0]].Text.Trim().ToUpperInvariant();
                if (v == part[2] || v == "") settings.Remove("guitar_pad_" + part[0]);
                else settings.Set("guitar_pad_" + part[0], v);
            }
            settings.Set("guitar_autostrum", autoStrum.Checked);
            settings.Set("mnk_mode", kbEnabled.Checked);
            foreach (var part in KeyParts) settings.Set(part[0], kbFields[part[0]].Text.Trim());
            foreach (var clear in KeyClears) settings.Set(clear[0], clear[1]);

            // Defaults that help weak PCs, written once so the player can still
            // change them later in the F4 menu. A bigger audio buffer stops the
            // crackling when the frame rate drops, and with few CPU cores the SDK
            // logs a warning about a thousand times a second at the default level.
            if (settings.Get("audio_maxqframes", "") == "") settings.Set("audio_maxqframes", 64);
            if (settings.Get("log_level", "") == "") settings.Set("log_level", "error");
            // The strongest graphics card, not the integrated one on a laptop with two.
            if (settings.Get("d3d12_adapter", "") == "") settings.Set("d3d12_adapter", -1);
        }

        bool Apply()
        {
            if (!IsGameFolder(gameFolder))
            {
                MessageBox.Show(this, Strings.T("need_folder"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            try
            {
                CollectSettings();
                settings.Save();
                WriteScriptMod();
                return true;
            }
            catch (Exception e)
            {
                MessageBox.Show(this, Strings.T("save_error") + "\n\n" + e.Message, Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        // For "--apply-scripts" (the in-game options menu, F5): rewrites the patched
        // scripts in mods from re_wor.toml, with no window.
        public int ApplyScriptsOnly()
        {
            if (!IsGameFolder(gameFolder)) return 2;
            try
            {
                WriteScriptMod();
                return 0;
            }
            catch (Exception)
            {
                return 1;
            }
        }

        string FindOriginalScripts()
        {
            string backup = Path.Combine(gameFolder, ScriptsRel + ".orig");
            if (File.Exists(backup)) return backup;
            string original = Path.Combine(gameFolder, ScriptsRel);
            if (File.Exists(original)) return original;
            throw new FileNotFoundException(Strings.T("scripts_missing"), original);
        }

        void WriteScriptMod()
        {
            string target = Path.Combine(Path.Combine(gameDir, "mods"), ScriptsRel);
            bool needed = !motionBlur.Checked || !screenNoise.Checked || !depthOfField.Checked || !bloom.Checked;
            if (!needed)
            {
                if (File.Exists(target)) File.Delete(target);
                return;
            }

            byte[] raw = File.ReadAllBytes(FindOriginalScripts());
            byte[] scripts = Chnk.IsChunked(raw) ? Chnk.Decompress(raw) : raw;
            var missing = new List<string>();
            if (Chnk.SetSectionInteger(scripts, "debug_use_motion_blur", motionBlur.Checked ? 1u : 0u) == 0)
                missing.Add("debug_use_motion_blur");
            if (Chnk.SetSectionInteger(scripts, "debug_use_screen_noise", screenNoise.Checked ? 1u : 0u) == 0)
                missing.Add("debug_use_screen_noise");
            if (Chnk.SetScreenFxOn(scripts, DofFxType, depthOfField.Checked) == 0)
                missing.Add("DOF");
            if (Chnk.SetScreenFxOn(scripts, QbKey.Of("bloom2"), bloom.Checked) == 0)
                missing.Add("bloom");
            if (missing.Count > 0)
                throw new InvalidDataException(Strings.T("missing_fx") + string.Join(", ", missing.ToArray()));

            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.WriteAllBytes(target, Chnk.IsChunked(raw) ? Chnk.Compress(scripts) : scripts);
        }

        void Launch()
        {
            string exe = Path.Combine(gameDir, GameExe);
            if (!File.Exists(exe))
            {
                MessageBox.Show(this, string.Format(Strings.T("missing_exe"), GameExe), Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            var start = new ProcessStartInfo(exe)
            {
                WorkingDirectory = gameDir,
                UseShellExecute = false
            };
            // Read by the game at startup. A file keeps any character in the path
            // intact, which a command line argument would not.
            File.WriteAllText(Path.Combine(gameDir, "game_folder.txt"), gameFolder, new UTF8Encoding(false));
            Process.Start(start);
            Close();
        }
    }

    // Loads the title font embedded in the launcher (resource "title.ttf").
    // Without it, the title falls back to Impact so the build still works.
    static class TitleFont
    {
        static System.Drawing.Text.PrivateFontCollection collection;

        public static FontFamily Load()
        {
            try
            {
                using (Stream s = typeof(TitleFont).Assembly.GetManifestResourceStream("title.ttf"))
                {
                    if (s != null)
                    {
                        var data = new byte[s.Length];
                        s.Read(data, 0, data.Length);
                        // AddMemoryFont keeps a pointer to this memory, so it is never freed.
                        IntPtr mem = System.Runtime.InteropServices.Marshal.AllocCoTaskMem(data.Length);
                        System.Runtime.InteropServices.Marshal.Copy(data, 0, mem, data.Length);
                        collection = new System.Drawing.Text.PrivateFontCollection();
                        collection.AddMemoryFont(mem, data.Length);
                        if (collection.Families.Length > 0) return collection.Families[0];
                    }
                }
            }
            catch (Exception) { }
            try { return new FontFamily("Impact"); } catch (Exception) { return FontFamily.GenericSansSerif; }
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (Array.IndexOf(args, "--apply-scripts") >= 0)
            {
                using (var form = new LauncherForm()) return form.ApplyScriptsOnly();
            }
            Application.Run(new LauncherForm());
            return 0;
        }
    }
}
