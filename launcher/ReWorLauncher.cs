// re-wor launcher: edits the port's settings (re_wor.toml), writes patched game
// scripts into the mods folder for graphics toggles, and starts the game.
// Targets .NET Framework 4.x so the compiler that ships with Windows can build it.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
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
        public void SetNumber(string key, string v) { SetRaw(key, v); }
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
            { "subtitle_gh5", new[] { "Guitar Hero 5 no PC, na mesma base do Warriors of Rock", "Guitar Hero 5 on PC, on the same base as Warriors of Rock" } },
            { "seal_text_gh5", new[] {
                "Este projeto não inclui nenhum arquivo do jogo e o autor não distribui o jogo. Use apenas a sua própria cópia de Guitar Hero 5 para Xbox 360. Diga não à pirataria.",
                "This project includes no game files and its author does not distribute the game. Use only your own copy of Guitar Hero 5 for Xbox 360. Say no to piracy." } },
            { "gh5_missing", new[] {
                "O Guitar Hero 5 não está instalado: falta a pasta gh5 com o Guitar Hero 5.exe ao lado do launcher.",
                "Guitar Hero 5 is not installed: the gh5 folder with Guitar Hero 5.exe is missing next to the launcher." } },
            { "gh5_missing_download", new[] {
                "O Guitar Hero 5 não está instalado: falta a pasta gh5 ao lado do launcher (a atualização da versão 1.2 não a trazia).\n\nBaixar a versão mais nova agora? O launcher fecha, instala e abre de novo; as suas configurações ficam.",
                "Guitar Hero 5 is not installed: the gh5 folder is missing next to the launcher (updating from version 1.2 did not bring it).\n\nDownload the latest version now? The launcher closes, installs it and opens again; your settings stay." } },
            { "gh5_game_note", new[] {
                "Use a sua cópia do Guitar Hero 5 de Xbox 360: o default.xex dos arquivos extraídos, uma ISO ou um pacote GOD.",
                "Use your Xbox 360 copy of Guitar Hero 5: default.xex from the extracted files, an ISO or a GOD package." } },
            { "gh5_renderer", new[] { "Renderizador", "Renderer" } },
            { "gh5_renderer_native", new[] { "Nativo (Vulkan, mais rápido)", "Native (Vulkan, faster)" } },
            { "gh5_renderer_xenos", new[] { "Emulado (Xenos, como no Xbox 360)", "Emulated (Xenos, as on the Xbox 360)" } },
            { "gh5_ultrawide", new[] { "Ultrawide (palco preenche 21:9, HUD centralizado)", "Ultrawide (the stage fills 21:9, centered HUD)" } },
            { "gh5_grain", new[] { "Granulado de tela", "Film grain" } },
            { "gh5_vignette", new[] { "Vinheta (bordas escuras)", "Vignette (dark edges)" } },
            { "tab_home", new[] { "INÍCIO", "HOME" } },
            { "fps_label", new[] { "Taxa de quadros (acima de 60 precisa de um PC forte e de um monitor rápido)", "Frame rate (above 60 needs a strong PC and a fast display)" } },
            { "fps_60", new[] { "60 FPS (como no console)", "60 FPS (as on the console)" } },
            { "gpu_label", new[] { "API gráfica (vale ao abrir o jogo)", "Graphics API (applies when the game starts)" } },
            { "gpu_any", new[] { "Automática (DirectX 12)", "Automatic (DirectX 12)" } },
            { "win_minimize", new[] { "Minimizar", "Minimize" } },
            { "win_mode_window", new[] { "Abrir em janela (F11)", "Window mode (F11)" } },
            { "win_mode_full", new[] { "Tela cheia (F11)", "Fullscreen (F11)" } },
            { "win_close", new[] { "Fechar", "Close" } },
            { "home_no_name", new[] { "Jogador", "Player" } },
            { "home_folder_ok", new[] { "Jogo encontrado", "Game found" } },
            { "home_folder_missing", new[] { "Falta escolher a pasta do jogo, na página JOGO.", "Choose the game folder first, on the GAME page." } },
            { "home_hint", new[] { "Vídeo, controles, microfone e o resto ficam nas páginas ao lado.", "Video, controls, microphone and the rest are on the pages at the side." } },
            { "home_version", new[] { "versão {0}", "version {0}" } },
            { "home_support", new[] { "Apoie:", "Support:" } },
            { "pick_title", new[] { "ESCOLHA O JOGO", "SELECT A GAME" } },
            { "pick_ready", new[] { "Pronto para jogar", "Ready to play" } },
            { "pick_missing", new[] { "Escolha a pasta do jogo", "Choose the game folder" } },
            { "pick_profile", new[] { "Perfil:", "Profile:" } },
            { "pick_hint", new[] { "← → e Enter, ou clique", "← → and Enter, or click" } },
            { "rail_games", new[] { "Todos os jogos", "All games" } },
            { "tab_controls", new[] { "CONTROLES", "CONTROLS" } },
            { "audio_buffer", new[] { "Buffer de áudio (vale ao abrir o jogo)", "Audio buffer (applies when the game starts)" } },
            { "audio_16", new[] { "16 (menos atraso)", "16 (less delay)" } },
            { "audio_64", new[] { "64 (sem estalos em PC fraco)", "64 (no crackling on weak PCs)" } },
            { "tilt_label", new[] { "Sensibilidade da inclinação (Star Power)", "Tilt sensitivity (Star Power)" } },
            { "tilt_100", new[] { "Normal", "Normal" } },
            { "tilt_150", new[] { "Mais sensível (1,5x)", "More sensitive (1.5x)" } },
            { "tilt_200", new[] { "Bem sensível (2x)", "Very sensitive (2x)" } },
            { "tilt_300", new[] { "Máxima (3x)", "Highest (3x)" } },
            { "usb_title", new[] { "GUITARRA USB (WII, PS3, ADAPTADORES)", "USB GUITAR (WII, PS3, ADAPTERS)" } },
            { "usb_none", new[] { "Nenhum controle USB genérico conectado", "No generic USB controller connected" } },
            { "usb_setup", new[] { "CONFIGURAR", "SET UP" } },
            { "usb_remove", new[] { "REMOVER", "REMOVE" } },
            { "usb_ready", new[] { "Configurada como guitarra.", "Set up as a guitar." } },
            { "usb_not_ready", new[] { "Ainda não configurada: o jogo vê um controle comum.", "Not set up yet: the game sees a regular controller." } },
            { "usb_note", new[] {
                "Como no Clone Hero: guitarras que o Windows mostra como \"USB Controller\" (Wii ou PS3 por adaptador) precisam que você ensine os botões uma vez. Clique em CONFIGURAR e aperte cada parte quando pedir.",
                "As in Clone Hero: guitars Windows shows as \"USB Controller\" (Wii or PS3 through an adapter) need their buttons taught once. Click SET UP and press each part when asked." } },
            { "wiz_title", new[] { "Configurar guitarra", "Guitar setup" } },
            { "wiz_step", new[] { "Passo {0} de {1}", "Step {0} of {1}" } },
            { "wiz_rest", new[] { "Solte todos os botões e deixe a guitarra deitada, parada.", "Let go of every button and hold the guitar flat and still." } },
            { "wiz_press", new[] { "Aperte: {0}", "Press: {0}" } },
            { "wiz_whammy", new[] { "Empurre a alavanca até o fim e solte.", "Push the whammy bar all the way and let go." } },
            { "wiz_tilt", new[] { "Levante o braço da guitarra (como para o Star Power) e volte.", "Tilt the guitar's neck up (as for Star Power) and back." } },
            { "wiz_release", new[] { "Pode soltar.", "You can let go." } },
            { "wiz_skip", new[] { "PULAR", "SKIP" } },
            { "wiz_cancel", new[] { "CANCELAR", "CANCEL" } },
            { "wiz_done", new[] { "Pronto! Clique em SALVAR ou JOGAR.", "Done! Click SAVE or PLAY." } },
            { "tab_profile", new[] { "PERFIL", "PROFILE" } },
            { "ach_player", new[] { "Perfil", "Profile" } },
            { "ach_summary", new[] { "{0} de {1} conquistas  ·  {2} de {3} G", "{0} of {1} achievements  ·  {2} of {3} G" } },
            { "ach_missing", new[] {
                "As conquistas aparecem aqui depois que o jogo abre uma vez com esta versão.",
                "The achievements show up here once the game has started with this version." } },
            { "ach_unlocked", new[] { "Desbloqueada em {0}", "Unlocked on {0}" } },
            { "pad_mode", new[] { "Controle comum (Xbox, PlayStation...)", "Regular controller (Xbox, PlayStation...)" } },
            { "pad_mode_mic", new[] { "Controle microfone: entra como controle comum (liga no menu debug)", "Microphone controller: joins as a regular controller (turn it on in the debug menu)" } },
            { "pad_mode_guitar", new[] { "Controle emulando a guitarra", "Controller emulating the guitar" } },
            { "pad_mode_note", new[] {
                "Controle microfone: o jogo vê um controle comum, que fica com o vocal; para tocar com ele, ligue o menu debug na aba JOGO e use Enable Controller dentro do jogo. Controle emulando a guitarra: o jogo vê uma guitarra.",
                "Microphone controller: the game sees a regular controller, which takes the vocals; to play with it, turn on the debug menu on the GAME page and use Enable Controller in the game. Controller emulating the guitar: the game sees a guitar." } },
            { "gh5_pad_tap", new[] { "Controle comum como guitarra", "Regular controller as a guitar" } },
            { "gh5_pad_layout", new[] { "Botões do controle comum", "Regular controller buttons" } },
            { "gh5_layout_classic", new[] { "Clássico: A B Y X LB nos trastes", "Classic: A B Y X LB on the frets" } },
            { "gh5_layout_triggers", new[] { "Com gatilhos: LT LB RB RT e A", "Triggers: LT LB RB RT and A" } },
            { "layout_parsec", new[] { "Guitarra pelo Parsec (alavanca e inclinação da guitarra)", "Guitar through Parsec (the guitar's whammy and tilt)" } },
            { "gh5_layout_custom", new[] { "Personalizado (do arquivo de opções)", "Custom (from the options file)" } },
            { "gh5_controls_note", new[] {
                "Guitarras e baterias de verdade funcionam como estão. Um controle comum toca guitarra: trastes nos botões, palhetada no direcional, alavanca no gatilho direito (no layout com gatilhos, no analógico direito) e Star Power no Back.",
                "Real guitars and drum kits work as they are. A regular controller plays guitar: frets on the buttons, strum on the D-pad, whammy on the right trigger (in the triggers layout, the right stick) and Star Power on Back." } },
            { "gh5_kb_dfjkl", new[] { "D F J K L (padrão do GH5)", "D F J K L (GH5's default)" } },
            { "gh5_kb_tap", new[] { "Palhetada automática no teclado", "Auto-strum on the keyboard" } },
            { "gh5_kb_note", new[] {
                "Nomes das teclas: letras, números, Up, Down, Left, Right, Space, Return, Backspace, Tab, Semicolon.",
                "Key names: letters, numbers, Up, Down, Left, Right, Space, Return, Backspace, Tab, Semicolon." } },
            { "gh5_singer", new[] { "Quem canta", "Who sings" } },
            { "gh5_singer_none", new[] { "Ninguém (só instrumentos)", "Nobody (instruments only)" } },
            { "gh5_singer_keyboard", new[] { "Quem joga no teclado", "Whoever plays on the keyboard" } },
            { "gh5_mic_none", new[] { "Nenhum", "None" } },
            { "gh5_mic_gain", new[] { "Ganho: {0} dB", "Gain: {0} dB" } },
            { "gh5_mic_note", new[] {
                "O microfone ainda não funciona nesta versão do GH5: aqui dá só para testar o nível. Ele chega numa próxima atualização, como no Warriors of Rock.",
                "The microphone does not work in this version of GH5 yet: here you can only test its level. It comes in a later update, as in Warriors of Rock." } },
            { "gh5_aa", new[] { "Antisserrilhado", "Antialiasing" } },
            { "gh5_aa_off", new[] { "Desligado", "Off" } },
            { "gh5_res", new[] { "Resolução interna", "Internal resolution" } },
            { "gh5_res_auto", new[] { "Automática (a da tela)", "Automatic (the screen's)" } },
            { "gh5_res_720", new[] { "720p (a mais leve)", "720p (the lightest)" } },
            { "gh5_scaling", new[] { "Ampliação da imagem", "Upscaling" } },
            { "gh5_shadows", new[] { "Qualidade das sombras", "Shadow quality" } },
            { "gh5_shadows_normal", new[] { "Normal", "Normal" } },
            { "gh5_shadows_high", new[] { "Alta (2x)", "High (2x)" } },
            { "gh5_shadows_ultra", new[] { "Muito alta (4x)", "Very high (4x)" } },
            { "gh5_native_note", new[] {
                "Resolução interna abaixo de 100% deixa a placa de vídeo mais leve, como no Warriors of Rock.",
                "An internal resolution below 100% is lighter on the graphics card, as in Warriors of Rock." } },
            { "gh5_audio_exclusive", new[] { "Áudio de baixa latência (modo exclusivo do Windows)", "Low latency audio (Windows exclusive mode)" } },
            { "gh5_show_fps", new[] { "Mostrar o FPS no canto (F2 liga e desliga)", "Show the FPS in the corner (F2 toggles it)" } },
            { "gh5_dlc_installing", new[] { "Instalando DLC: {0}", "Installing DLC: {0}" } },
            { "gh5_gfx_note", new[] {
                "Os quatro efeitos são desligados direto no jogo, sem mexer nos seus arquivos.",
                "The four effects are turned off inside the game, without touching your files." } },
            { "gh5_dlc_note", new[] {
                "DLC: coloque os pacotes do Xbox 360 do GH5 (arquivos CON ou LIVE) na PASTA DE DLC. O jogo instala os novos quando abre.",
                "DLC: put your GH5 Xbox 360 packages (CON or LIVE files) in the DLC FOLDER. The game installs new ones when it starts." } },
            { "home_fullscreen", new[] { "Tela cheia", "Fullscreen" } },
            { "home_window", new[] { "Janela", "Window" } },
            { "home_online_on", new[] { "Ligado", "On" } },
            { "home_online_off", new[] { "Desligado", "Off" } },
            { "home_skip_intro", new[] { "Sem vídeo de abertura", "No opening video" } },
            { "tab_game", new[] { "JOGO", "GAME" } },
            { "tab_video", new[] { "VÍDEO", "VIDEO" } },
            { "tab_gfx", new[] { "GRÁFICOS", "GRAPHICS" } },
            { "dlc_button", new[] { "PASTA DE DLC", "DLC FOLDER" } },
            { "dlc_note", new[] {
                "DLC e músicas customizadas: coloque os pacotes do Xbox 360 (arquivos CON ou LIVE) na pasta dlc; eles são instalados ao abrir o jogo. Para músicas do Onyx, inclua também o cache que o Onyx gera (Other tools > GH:WoR cache).",
                "DLC and custom songs: put the Xbox 360 packages (CON or LIVE files) in the dlc folder; they are installed when the game starts. For Onyx songs, add the cache Onyx makes too (Other tools > GH:WoR cache)." } },
            { "tab_keyboard", new[] { "TECLADO", "KEYBOARD" } },
            { "tab_players", new[] { "JOGADORES", "PLAYERS" } },
            { "tab_online", new[] { "ONLINE", "ONLINE" } },
            { "online_enabled", new[] { "Jogar online com amigos (rede local ou VPN, como Radmin VPN)", "Play online with friends (local network or a VPN such as Radmin VPN)" } },
            { "online_my_ip", new[] { "Meu endereço na rede", "My network address" } },
            { "online_auto", new[] { "Automático (Radmin 26.x, Hamachi 25.x, depois a rede local)", "Automatic (Radmin 26.x, Hamachi 25.x, then the local network)" } },
            { "online_share", new[] { "Passe este endereço ao amigo: {0}", "Give this address to your friend: {0}" } },
            { "online_share_none", new[] { "Nenhum endereço de rede encontrado.", "No network address found." } },
            { "online_copy", new[] { "COPIAR", "COPY" } },
            { "online_host", new[] { "Endereço do anfitrião (deixe vazio se você for o anfitrião)", "Host address (leave empty if you are the host)" } },
            { "online_bad_ip", new[] { "O endereço do anfitrião precisa ser um IPv4, como 26.12.34.56.", "The host address must be an IPv4 address, such as 26.12.34.56." } },
            { "online_note", new[] {
                "Os dois PCs precisam estar na mesma rede ou na mesma VPN (Radmin VPN, ZeroTier, Hamachi). O anfitrião deixa o endereço vazio e entra primeiro em Online > procurar jogadores; o amigo põe o endereço do anfitrião e procura depois. O nome é o do perfil (aba JOGADORES). Experimental; o Windows pode pedir para liberar o firewall.",
                "Both PCs must be on the same network or the same VPN (Radmin VPN, ZeroTier, Hamachi). The host leaves the address empty and goes into Online > find players first; the friend puts in the host's address and searches next. The name is the profile's (PLAYERS page). Experimental; Windows may ask to allow it through the firewall." } },
            { "picture_hint", new[] { "Foto do perfil: clique para escolher uma imagem; clique direito para tirar.", "Profile picture: click to choose an image; right-click to remove it." } },
            { "picture_filter", new[] { "Imagens|*.png;*.jpg;*.jpeg;*.bmp;*.gif", "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif" } },
            { "picture_failed", new[] { "Não deu para abrir essa imagem.", "That image could not be opened." } },
            { "profile_name", new[] { "Nome do perfil (aparece no jogo, até 15 letras)", "Profile name (shown in the game, up to 15 letters)" } },
            { "dev_auto", new[] { "Automático (o próximo livre)", "Automatic (the next free one)" } },
            { "dev_none", new[] { "Ninguém", "Nobody" } },
            { "player_name_cue", new[] { "Nome do perfil do P{0}", "P{0}'s profile name" } },
            { "player_default_name", new[] { "Jogador {0}", "Player {0}" } },
            { "dev_missing", new[] { "Desconectado: {0}", "Disconnected: {0}" } },
            { "dev_x360_guitar", new[] { "Guitarra Xbox 360", "Xbox 360 guitar" } },
            { "dev_x360_drums", new[] { "Bateria Xbox 360", "Xbox 360 drums" } },
            { "dev_xbox_pad", new[] { "Controle Xbox", "Xbox controller" } },
            { "dev_ps_pad", new[] { "Controle PlayStation", "PlayStation controller" } },
            { "dev_pad", new[] { "Controle", "Controller" } },
            { "dev_guitar", new[] { "Guitarra", "Guitar" } },
            { "dev_drums", new[] { "Bateria", "Drums" } },
            { "dev_keyboard", new[] { "Teclado", "Keyboard" } },
            { "dev_keyboard_detail", new[] { "Teclas da aba TECLADO", "Keys from the KEYBOARD page" } },
            { "dev_slot", new[] { "XInput {0}  ·  luz {0} no anel do Xbox 360", "XInput {0}  ·  light {0} on the Xbox 360 ring" } },
            { "devices_title", new[] { "DISPOSITIVOS CONECTADOS", "CONNECTED DEVICES" } },
            { "devices_hint", new[] { "Aperte um botão em qualquer controle: o cartão dele acende.", "Press a button on any controller: its card lights up." } },
            { "players_title", new[] { "JOGADORES", "PLAYERS" } },
            { "player_badge", new[] { "JOGADOR {0}", "PLAYER {0}" } },
            { "reading_xinput", new[] { "XInput", "XInput" } },
            { "reading_sdl", new[] { "SDL", "SDL" } },
            { "reading_tip", new[] {
                "Leitura do controle Xbox: XInput lê do jeito do Xbox (guitarra 360 com tilt); SDL é o outro jeito, para quando os botões vierem trocados.",
                "How the Xbox controller is read: XInput reads it the Xbox way (360 guitar with tilt); SDL is the other way, for when the buttons come out swapped." } },
            { "mic_none", new[] { "Ninguém canta no microfone do PC", "Nobody sings on the PC microphone" } },
            { "mic_player", new[] { "Jogador {0} canta no microfone do PC", "Player {0} sings on the PC microphone" } },
            { "players_note", new[] {
                "Um dispositivo nunca vai para dois jogadores. Quem canta no microfone do PC se escolhe na aba MICROFONE. Vale ao abrir o jogo.",
                "A device never goes to two players. Who sings on the PC microphone is picked on the MICROPHONE page. Applies when the game starts." } },
            { "tab_mic", new[] { "MICROFONE", "MICROPHONE" } },
            { "tab_update", new[] { "ATUALIZAR", "UPDATE" } },
            { "mic_who", new[] { "Quem canta", "Who sings" } },
            { "mic_device", new[] { "Microfone", "Microphone" } },
            { "mic_default", new[] { "Padrão do Windows", "Windows default" } },
            { "mic_level", new[] { "TESTE: FALE OU CANTE", "TEST: SPEAK OR SING" } },
            { "mic_listening", new[] { "Ouvindo...", "Listening..." } },
            { "mic_hearing", new[] { "Ouvindo: {0}%", "Hearing: {0}%" } },
            { "mic_silenced", new[] { "Silêncio: abaixo da sensibilidade, o jogo não ouve", "Silence: below the sensitivity, the game hears nothing" } },
            { "mic_missing", new[] { "Microfone não encontrado. Confira em Configurações > Som > Entrada do Windows.", "Microphone not found. Check Settings > Sound > Input in Windows." } },
            { "mic_volume", new[] { "Volume: {0}%", "Volume: {0}%" } },
            { "mic_gate", new[] { "Sensibilidade: corta abaixo de {0}%", "Sensitivity: cuts below {0}%" } },
            { "mic_gate_off", new[] { "Sensibilidade: desligada (ouve tudo)", "Sensitivity: off (hears everything)" } },
            { "mic_note", new[] {
                "A barra mostra o que o jogo vai receber. Ajuste o volume para a voz chegar perto do fim sem ficar branca no canto (estourado). Se o jogo pegar ruído ou o som da música, suba a sensibilidade até a linha branca ficar acima do ruído: abaixo dela vira silêncio.",
                "The bar shows what the game will receive. Set the volume so your voice gets near the end without the corner turning white (clipping). If the game picks up noise or the music, raise the sensitivity until the white line is above the noise: below it is silence." } },
            { "update_installed", new[] { "Versão instalada: v{0}", "Installed version: v{0}" } },
            { "update_idle", new[] { "Clique em VERIFICAR para procurar uma versão nova no GitHub.", "Click CHECK to look for a new version on GitHub." } },
            { "update_check", new[] { "VERIFICAR", "CHECK" } },
            { "update_ask_title", new[] { "Atualização disponível", "Update available" } },
            { "update_ask", new[] { "Saiu a versão {0} (você tem a {1}).\n\nBaixar e instalar agora? O launcher fecha e abre de novo sozinho; suas configurações ficam.", "Version {0} is out (you have {1}).\n\nDownload and install it now? The launcher closes and opens again by itself; your settings stay." } },
            { "update_install", new[] { "BAIXAR E INSTALAR", "DOWNLOAD AND INSTALL" } },
            { "update_page", new[] { "Abrir no GitHub", "Open on GitHub" } },
            { "update_checking", new[] { "Procurando...", "Checking..." } },
            { "update_failed", new[] { "Não deu para falar com o GitHub. Confira a internet.", "Could not reach GitHub. Check the internet connection." } },
            { "update_available", new[] { "Versão nova disponível: {0}", "New version available: {0}" } },
            { "update_current", new[] { "Você já está na versão mais nova.", "You already have the latest version." } },
            { "update_no_zip", new[] { "Há uma versão nova, mas sem o zip para baixar. Abra no GitHub.", "There is a new version, but without a zip to download. Open it on GitHub." } },
            { "update_downloading", new[] { "Baixando...", "Downloading..." } },
            { "update_restarting", new[] { "Instalando: o launcher fecha e abre de novo sozinho.", "Installing: the launcher closes and opens again by itself." } },
            { "update_note", new[] {
                "A atualização troca o launcher e os arquivos do port. Suas configurações, a pasta do jogo, mods e DLC ficam como estão. Os arquivos do jogo nunca vêm junto.",
                "The update replaces the launcher and the port's files. Your settings, game folder, mods and DLC stay as they are. The game's files never come with it." } },
            { "kb_enabled", new[] { "Jogar no teclado (como guitarra)", "Play on the keyboard (as a guitar)" } },
            { "kb_asjkl", new[] { "A S J K L + setas (estilo Clone Hero)", "A S J K L + arrows (Clone Hero style)" } },
            { "kb_numbers", new[] { "1 2 3 4 5 + setas", "1 2 3 4 5 + arrows" } },
            { "kb_start", new[] { "Start", "Start" } },
            { "kb_note", new[] {
                "Nomes de teclas: A-Z, 0-9, F1-F12, Up Down Left Right, Space, Return, Backspace, Tab, Semicolon, Comma, Numpad0-9. Várias teclas: A,Left. F1-F5, F7 e F11 já são atalhos do jogo.",
                "Key names: A-Z, 0-9, F1-F12, Up Down Left Right, Space, Return, Backspace, Tab, Semicolon, Comma, Numpad0-9. Several keys: A,Left. F1-F5, F7 and F11 are already game shortcuts." } },
            { "pad_preset", new[] { "Predefinição", "Preset" } },
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
            { "folder_label", new[] { "Pasta com os arquivos do jogo (onde fica o default.xex)", "Game files folder (where default.xex is)" } },
            { "choose", new[] { "ESCOLHER", "BROWSE" } },
            { "no_folder", new[] { "Nenhuma pasta escolhida ainda.", "No folder selected yet." } },
            { "game_lang", new[] { "Idioma do jogo", "Game language" } },
            { "debug_menu", new[] { "Menu debug do jogo (item DEBUG MENU no menu principal, abaixo de OPTIONS)", "Game debug menu (a DEBUG MENU item in the main menu, below OPTIONS)" } },
            { "skip_intro", new[] { "Pular o vídeo de abertura", "Skip the opening video" } },
            { "game_note", new[] {
                "Use a sua cópia do jogo: o default.xex dos arquivos extraídos (eles ficam onde estão), uma ISO ou um pacote GOD (o launcher instala os arquivos numa pasta).",
                "Use your copy of the game: default.xex from the extracted files (they stay where they are), an ISO or a GOD package (the launcher installs the files to a folder)." } },
            { "folder_dialog", new[] { "Escolha o default.xex, a ISO ou o arquivo do GOD do jogo", "Choose the game's default.xex, ISO or GOD file" } },
            { "source_filter", new[] { "default.xex, ISO ou GOD", "default.xex, ISO or GOD" } },
            { "bad_source", new[] {
                "Esse arquivo não é o default.xex, uma ISO de Xbox 360 nem um pacote GOD. No GOD, escolha o arquivo sem extensão que fica ao lado da pasta .data.",
                "That file is not default.xex, an Xbox 360 ISO or a GOD package. For GOD, choose the file without an extension next to the .data folder." } },
            { "install_dialog", new[] { "Escolha onde instalar os arquivos do jogo (cerca de 8 GB).", "Choose where to install the game files (about 8 GB)." } },
            { "installing", new[] { "Instalando os arquivos do jogo da sua cópia...", "Installing the game files from your copy..." } },
            { "install_failed", new[] { "Não deu para instalar os arquivos do jogo.", "The game files could not be installed." } },
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

    // A controller the PC has, named the way the game's player1_device...
    // settings take it: xinput1 (an XInput slot), hid:054C:09CC:1 (the first
    // device with that USB vendor and product) or keyboard.
    class InputDevice
    {
        public string Spec, Kind, Title, Detail;
        public int XInputSlot = -1;
        public uint JoyId;
        public bool IsJoy;
        public DateTime LastActive = DateTime.MinValue;

        // 1 right after a button press, fading to 0.
        public float Glow
        {
            get
            {
                double ms = (DateTime.Now - LastActive).TotalMilliseconds;
                return ms >= 700 ? 0f : (float)(1.0 - ms / 700.0);
            }
        }
    }

    // Finds the controllers: the XInput slots (Xbox controllers, Xbox 360 guitars
    // and drums), the other game controllers Windows knows (winmm: PlayStation
    // and other HID pads, the XInput ones left out) and the keyboard; and tells
    // which one has a button pressed.
    static class InputScan
    {
        [StructLayout(LayoutKind.Sequential)]
        struct XGamepad { public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }
        [StructLayout(LayoutKind.Sequential)]
        struct XState { public uint Packet; public XGamepad Pad; }
        [StructLayout(LayoutKind.Sequential)]
        struct XCapsEx
        {
            public byte Type, SubType;
            public ushort Flags;
            public XGamepad Pad;
            public ushort LeftMotor, RightMotor, Vendor, Product, Version, Unknown1;
            public uint Unknown2;
        }
        [DllImport("xinput1_4.dll")] static extern uint XInputGetState(uint user, out XState state);
        [DllImport("xinput1_4.dll", EntryPoint = "#108")] static extern uint XInputGetCapabilitiesEx(uint one, uint user, uint flags, out XCapsEx caps);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct JoyCaps
        {
            public ushort Mid, Pid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
            public uint XMin, XMax, YMin, YMax, ZMin, ZMax, NumButtons, PeriodMin, PeriodMax;
            public uint RMin, RMax, UMin, UMax, VMin, VMax, Caps, MaxAxes, NumAxes, MaxButtons;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string RegKey;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string OemVxd;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct JoyInfoEx { public uint Size, Flags, X, Y, Z, R, U, V, Buttons, ButtonNumber, Pov, Reserved1, Reserved2; }
        [DllImport("winmm.dll")] static extern uint joyGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern uint joyGetDevCapsW(UIntPtr id, ref JoyCaps caps, uint size);
        [DllImport("winmm.dll")] static extern uint joyGetPosEx(uint id, ref JoyInfoEx info);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);

        static bool xinputMissing;

        // The brand of the common controller makers, from the USB vendor.
        static string VendorName(ushort vendor)
        {
            switch (vendor)
            {
                case 0x045E: return " Microsoft";
                case 0x057E: return " Nintendo";
                case 0x2DC8: return " 8BitDo";
                case 0x0F0D: return " Hori";
                case 0x0E6F: return " PDP";
                case 0x24C6: return " PowerA";
                case 0x1430: return " RedOctane";
                case 0x12BA: return " PlayStation 3";
                case 0x046D: return " Logitech";
                case 0x2563: return " ShanWan";
                case 0x0079: return " DragonRise";
                default: return "";
            }
        }

        static string UsbId(ushort vendor, ushort product)
        {
            return vendor.ToString("X4") + ":" + product.ToString("X4");
        }

        public static List<InputDevice> Scan()
        {
            var list = new List<InputDevice>();
            var xinputIds = new List<string>();
            for (uint slot = 0; slot < 4 && !xinputMissing; slot++)
            {
                XCapsEx caps;
                try { if (XInputGetCapabilitiesEx(1, slot, 0, out caps) != 0) continue; }
                catch (Exception) { xinputMissing = true; break; }
                ushort vendor = caps.Vendor, product = caps.Product;
                // The wireless receiver reports no product; SDL and the game call it a wired pad.
                if (product == 0 && (caps.Flags & 2) != 0) { vendor = 0x045E; product = 0x028E; }
                xinputIds.Add(UsbId(vendor, product));
                bool guitar = caps.SubType == 6 || caps.SubType == 7 || caps.SubType == 0x0B;
                bool drums = caps.SubType == 8;
                list.Add(new InputDevice
                {
                    Spec = "xinput" + (slot + 1),
                    Kind = guitar ? "guitar" : drums ? "drums" : "gamepad",
                    Title = Strings.T(guitar ? "dev_x360_guitar" : drums ? "dev_x360_drums" : "dev_xbox_pad"),
                    Detail = string.Format(Strings.T("dev_slot"), slot + 1),
                    XInputSlot = (int)slot
                });
            }

            var seen = new Dictionary<string, int>();
            uint count = 0;
            try { count = Math.Min(joyGetNumDevs(), 16u); } catch (Exception) { }
            for (uint id = 0; id < count; id++)
            {
                var info = new JoyInfoEx { Size = (uint)Marshal.SizeOf(typeof(JoyInfoEx)), Flags = 0xFF };
                if (joyGetPosEx(id, ref info) != 0) continue;
                var caps = new JoyCaps();
                if (joyGetDevCapsW((UIntPtr)id, ref caps, (uint)Marshal.SizeOf(typeof(JoyCaps))) != 0) continue;
                string usb = UsbId(caps.Mid, caps.Pid);
                string name = (caps.Name ?? "").Trim();
                // XInput devices are listed above, by slot.
                int index = xinputIds.IndexOf(usb);
                if (index >= 0) { xinputIds.RemoveAt(index); continue; }
                string lower = name.ToLowerInvariant();
                if (lower.Contains("xbox") || lower.Contains("xinput")) continue;
                int nth;
                seen.TryGetValue(usb, out nth);
                seen[usb] = ++nth;
                bool guitar = lower.Contains("guitar");
                bool drums = lower.Contains("drum");
                list.Add(new InputDevice
                {
                    Spec = "hid:" + usb + ":" + nth,
                    Kind = guitar ? "guitar" : drums ? "drums" : "gamepad",
                    Title = caps.Mid == 0x054C ? Strings.T("dev_ps_pad") : Strings.T(guitar ? "dev_guitar" : drums ? "dev_drums" : "dev_pad") + VendorName(caps.Mid),
                    Detail = "USB " + usb + (nth > 1 ? "  ·  #" + nth : ""),
                    JoyId = id,
                    IsJoy = true
                });
            }

            list.Add(new InputDevice { Spec = "keyboard", Kind = "keyboard", Title = Strings.T("dev_keyboard"), Detail = Strings.T("dev_keyboard_detail") });
            return list;
        }

        // A generic controller's raw state (winmm), axes from -1 to 1 in winmm's order X Y Z R U V.
        public class JoyReading
        {
            public readonly float[] Axes = new float[6];
            public readonly bool[] Has = new bool[6];
            public uint Buttons;
            public int Pov = -1;
        }

        public static JoyReading ReadJoy(uint id)
        {
            var info = new JoyInfoEx { Size = (uint)Marshal.SizeOf(typeof(JoyInfoEx)), Flags = 0xFF };
            if (joyGetPosEx(id, ref info) != 0) return null;
            var caps = new JoyCaps();
            if (joyGetDevCapsW((UIntPtr)id, ref caps, (uint)Marshal.SizeOf(typeof(JoyCaps))) != 0) return null;
            var r = new JoyReading { Buttons = info.Buttons, Pov = info.Pov == 0xFFFF || info.Pov == 0xFFFFFFFF ? -1 : (int)info.Pov };
            uint[] v = { info.X, info.Y, info.Z, info.R, info.U, info.V };
            uint[] lo = { caps.XMin, caps.YMin, caps.ZMin, caps.RMin, caps.UMin, caps.VMin };
            uint[] hi = { caps.XMax, caps.YMax, caps.ZMax, caps.RMax, caps.UMax, caps.VMax };
            bool[] has = { true, true, (caps.Caps & 1) != 0, (caps.Caps & 2) != 0, (caps.Caps & 4) != 0, (caps.Caps & 8) != 0 };
            for (int i = 0; i < 6; i++)
            {
                r.Has[i] = has[i] && hi[i] > lo[i];
                r.Axes[i] = r.Has[i] ? ((float)v[i] - lo[i]) * 2f / (hi[i] - lo[i]) - 1f : 0f;
            }
            return r;
        }

        // SDL's number for winmm axis i. SDL numbers a HID controller's axes in usage order (X Y Z Rx Ry Rz);
        // winmm's are X Y Z R U V, R being Rz, U Rx and V Ry.
        public static int SdlAxis(JoyReading r, int winmm)
        {
            int[] usage = { 0, 1, 2, 5, 3, 4 };
            int index = 0;
            for (int i = 0; i < 6; i++)
                if (r.Has[i] && usage[i] < usage[winmm]) index++;
            return index;
        }

        // Marks the devices with a button pressed. keyboardToo: the launcher has
        // the focus, so a key pressed is meant for it.
        public static void Poll(List<InputDevice> devices, bool keyboardToo)
        {
            DateTime now = DateTime.Now;
            foreach (var d in devices)
            {
                bool active = false;
                if (d.XInputSlot >= 0 && !xinputMissing)
                {
                    XState st;
                    try
                    {
                        if (XInputGetState((uint)d.XInputSlot, out st) == 0)
                        {
                            var p = st.Pad;
                            active = p.Buttons != 0 || p.LT > 60 || p.RT > 60 || Math.Abs((int)p.LX) > 16000 || Math.Abs((int)p.LY) > 16000
                                || (d.Kind == "guitar" && p.RY > 16000);
                        }
                    }
                    catch (Exception) { xinputMissing = true; }
                }
                else if (d.IsJoy)
                {
                    var info = new JoyInfoEx { Size = (uint)Marshal.SizeOf(typeof(JoyInfoEx)), Flags = 0xFF };
                    if (joyGetPosEx(d.JoyId, ref info) == 0)
                        active = info.Buttons != 0 || (info.Pov != 0xFFFF && info.Pov != 0xFFFFFFFF);
                }
                else if (d.Kind == "keyboard" && keyboardToo)
                {
                    // 0xC3-0xDA are the gamepad's own keys (Windows 10 turns a
                    // controller's stick into them), not the keyboard's.
                    for (int key = 0x08; key <= 0xDF && !active; key++)
                        if ((key < 0xC3 || key > 0xDA) && (GetAsyncKeyState(key) & 0x8000) != 0) active = true;
                }
                if (active) d.LastActive = now;
            }
        }
    }

    // Listens to a Windows microphone (winmm) for the MICROPHONE page's level
    // meter: the loudest sample of the last few milliseconds.
    sealed class MicMeter : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct WaveFormat { public ushort Tag, Channels; public uint Rate, BytesPerSec; public ushort BlockAlign, Bits, Extra; }
        [StructLayout(LayoutKind.Sequential)]
        struct WaveHeader { public IntPtr Data; public uint BufferLength, BytesRecorded; public IntPtr User; public uint Flags, Loops; public IntPtr Next, Reserved; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WaveInCaps
        {
            public ushort Mid, Pid;
            public uint DriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
            public uint Formats;
            public ushort Channels, Reserved;
        }
        [DllImport("winmm.dll")] static extern uint waveInGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern uint waveInGetDevCapsW(UIntPtr id, ref WaveInCaps caps, uint size);
        [DllImport("winmm.dll")] static extern uint waveInOpen(out IntPtr handle, uint device, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] static extern uint waveInPrepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] static extern uint waveInUnprepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] static extern uint waveInAddBuffer(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] static extern uint waveInStart(IntPtr handle);
        [DllImport("winmm.dll")] static extern uint waveInReset(IntPtr handle);
        [DllImport("winmm.dll")] static extern uint waveInClose(IntPtr handle);

        const uint WaveMapper = 0xFFFFFFFF;
        const int Buffers = 4;
        const int BufferBytes = 48000 * 2 / 25;  // 40 ms
        static readonly uint HeaderSize = (uint)Marshal.SizeOf(typeof(WaveHeader));
        static readonly int FlagsOffset = (int)Marshal.OffsetOf(typeof(WaveHeader), "Flags");
        static readonly int RecordedOffset = (int)Marshal.OffsetOf(typeof(WaveHeader), "BytesRecorded");

        IntPtr handle;
        readonly IntPtr[] headers = new IntPtr[Buffers];
        readonly byte[] samples = new byte[BufferBytes];

        // The recording devices, in winmm order; names are cut at 31 characters.
        public static List<string> Devices()
        {
            var list = new List<string>();
            uint count = 0;
            try { count = waveInGetNumDevs(); } catch (Exception) { return list; }
            for (uint i = 0; i < count; i++)
            {
                var caps = new WaveInCaps();
                list.Add(waveInGetDevCapsW((UIntPtr)i, ref caps, (uint)Marshal.SizeOf(typeof(WaveInCaps))) == 0 ? caps.Name : "?");
            }
            return list;
        }

        // name: one from Devices(), or empty for the Windows default.
        public bool Open(string name)
        {
            uint device = WaveMapper;
            if (!string.IsNullOrEmpty(name))
            {
                int index = Devices().IndexOf(name);
                if (index >= 0) device = (uint)index;
            }
            var format = new WaveFormat { Tag = 1, Channels = 1, Rate = 48000, BytesPerSec = 96000, BlockAlign = 2, Bits = 16 };
            try { if (waveInOpen(out handle, device, ref format, IntPtr.Zero, IntPtr.Zero, 0) != 0) { handle = IntPtr.Zero; return false; } }
            catch (Exception) { handle = IntPtr.Zero; return false; }
            for (int i = 0; i < Buffers; i++)
            {
                var header = new WaveHeader { Data = Marshal.AllocHGlobal(BufferBytes), BufferLength = BufferBytes };
                headers[i] = Marshal.AllocHGlobal((int)HeaderSize);
                Marshal.StructureToPtr(header, headers[i], false);
                waveInPrepareHeader(handle, headers[i], HeaderSize);
                waveInAddBuffer(handle, headers[i], HeaderSize);
            }
            waveInStart(handle);
            return true;
        }

        public bool IsOpen { get { return handle != IntPtr.Zero; } }

        // The loudest sample recorded since the last call, 0 to 1; -1 when nothing new came.
        public float Peak()
        {
            if (handle == IntPtr.Zero) return -1f;
            float peak = -1f;
            foreach (var header in headers)
            {
                if ((Marshal.ReadInt32(header, FlagsOffset) & 1) == 0) continue;  // WHDR_DONE
                int recorded = Marshal.ReadInt32(header, RecordedOffset);
                Marshal.Copy(Marshal.ReadIntPtr(header), samples, 0, Math.Min(recorded, BufferBytes));
                int loudest = 0;
                for (int i = 0; i + 1 < recorded; i += 2)
                    loudest = Math.Max(loudest, Math.Abs((int)BitConverter.ToInt16(samples, i)));
                peak = Math.Max(peak, loudest / 32768f);
                waveInAddBuffer(handle, header, HeaderSize);
            }
            return peak;
        }

        public void Dispose()
        {
            if (handle == IntPtr.Zero) return;
            waveInReset(handle);
            foreach (var header in headers)
            {
                if (header == IntPtr.Zero) continue;
                waveInUnprepareHeader(handle, header, HeaderSize);
                Marshal.FreeHGlobal(Marshal.ReadIntPtr(header));
                Marshal.FreeHGlobal(header);
            }
            waveInClose(handle);
            handle = IntPtr.Zero;
        }
    }

    // Reads the game files out of a disc image (XISO / XDVDFS) or a Games on Demand
    // package (an STFS header with a .data folder of SVOD fragments) and copies them
    // to a folder, so the launcher can install the game from what the player has.
    static class XboxImage
    {
        const int Sector = 0x800;
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("MICROSOFT*XBOX*MEDIA");
        static readonly long[] IsoPartitions = { 0, 0xFD90000, 0x2080000, 0x18300000 };

        public class Entry
        {
            public string Path;
            public long Location;
            public long Length;
        }

        public enum Kind { None, Iso, God }

        public static Kind Detect(string file)
        {
            try
            {
                if (Directory.Exists(file + ".data")) return GodHeader(file) != null ? Kind.God : Kind.None;
                using (var fs = File.OpenRead(file))
                    return IsoPartition(fs) >= 0 ? Kind.Iso : Kind.None;
            }
            catch (IOException) { return Kind.None; }
            catch (UnauthorizedAccessException) { return Kind.None; }
        }

        static bool MagicAt(Stream s, long offset)
        {
            if (offset + Magic.Length > s.Length) return false;
            var buf = new byte[Magic.Length];
            s.Position = offset;
            if (s.Read(buf, 0, buf.Length) != buf.Length) return false;
            for (int i = 0; i < buf.Length; i++) if (buf[i] != Magic[i]) return false;
            return true;
        }

        static long IsoPartition(Stream s)
        {
            foreach (long p in IsoPartitions)
                if (MagicAt(s, p + 0x10000)) return p;
            return -1;
        }

        static uint U32Le(byte[] b, int o) { return (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24); }
        static uint U32Be(byte[] b, int o) { return (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]); }
        static ushort U16Le(byte[] b, int o) { return (ushort)(b[o] | b[o + 1] << 8); }

        // Directory entries are a binary tree: left, right, data sector, size, attributes,
        // name length, name; each one is 4-byte aligned. readAt(sector, offset, n) reads
        // n bytes at that offset from the start of a directory's first sector.
        static void ReadTree(Func<long, long, int, byte[]> readAt, long sector, long ordinal, string prefix, List<Entry> list, int depth)
        {
            if (depth > 64) throw new InvalidDataException("directory tree too deep");
            byte[] raw = readAt(sector, ordinal * 4, 0xE);
            ushort left = U16Le(raw, 0), right = U16Le(raw, 2);
            if (left == 0xFFFF && right == 0xFFFF) return;
            uint data = U32Le(raw, 4), length = U32Le(raw, 8);
            byte attr = raw[12], nameLen = raw[13];
            string name = Encoding.GetEncoding(28591).GetString(readAt(sector, ordinal * 4 + 0xE, nameLen));
            if (name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || name == "." || name == "..")
                throw new InvalidDataException("bad file name in image: " + name);
            if (left != 0) ReadTree(readAt, sector, left, prefix, list, depth + 1);
            string path = prefix + name;
            if ((attr & 0x10) != 0)
            {
                if (length != 0) ReadTree(readAt, data, 0, path + "\\", list, depth + 1);
            }
            else list.Add(new Entry { Path = path, Location = data, Length = length });
            if (right != 0) ReadTree(readAt, sector, right, prefix, list, depth + 1);
        }

        class God
        {
            public int StartDataBlock;
            public long Base;
            public long MagicOffset;
            public bool Egdf;
            public string[] Fragments;
        }

        static God GodHeader(string headerPath)
        {
            var hdr = new byte[0x3B0];
            using (var fs = File.OpenRead(headerPath))
                if (fs.Read(hdr, 0, hdr.Length) != hdr.Length) return null;
            string sig = Encoding.ASCII.GetString(hdr, 0, 4);
            if (sig != "CON " && sig != "LIVE" && sig != "PIRS") return null;
            if (U32Be(hdr, 0x3A9) != 1) return null;
            var god = new God
            {
                StartDataBlock = hdr[0x395] | hdr[0x396] << 8 | hdr[0x397] << 16,
                Egdf = (hdr[0x391] & 0x40) != 0,
                Fragments = Directory.GetFiles(headerPath + ".data", "Data*"),
            };
            Array.Sort(god.Fragments, StringComparer.OrdinalIgnoreCase);
            if (god.Fragments.Length == 0) return null;
            using (var first = File.OpenRead(god.Fragments[0]))
            {
                if (god.Egdf && MagicAt(first, 0x2000)) { god.Base = 0; god.MagicOffset = 0x2000; }
                else if (MagicAt(first, 0x12000)) { god.Base = 0x10000; god.MagicOffset = 0x12000; }
                else if (MagicAt(first, 0xD000)) { god.Base = 0xB000; god.MagicOffset = 0xD000; }
                else return null;
            }
            return god;
        }

        // Where an SVOD block lives: which fragment and at what offset, skipping the hash blocks.
        static void GodBlock(God god, long block, out int file, out long offset)
        {
            const long BlocksPerL0 = 0x198, HashesPerL1 = 0xA1C4, BlocksPerFile = 0x14388, MaxFile = 0xA290000;
            long trueBlock = block - god.StartDataBlock * 2L;
            if (god.Egdf) trueBlock += 2;
            long fileBlock = trueBlock % BlocksPerFile;
            file = (int)(trueBlock / BlocksPerFile);
            long l0 = fileBlock / BlocksPerL0 + 1;
            long off = l0 * 0x1000;
            long l1 = l0 / HashesPerL1 + 1;
            off += l1 * 0x1000;
            if (god.Base == 0xB000) off += god.Base;
            offset = fileBlock * Sector + off;
            if (offset >= MaxFile)
            {
                file++;
                offset = offset % MaxFile + 0x2000;
            }
        }

        // Copies every file to dest. progress gets (bytes done, bytes total).
        public static void Extract(string source, string dest, Action<long, long> progress)
        {
            var list = new List<Entry>();
            Kind kind = Detect(source);
            if (kind == Kind.Iso)
            {
                using (var fs = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                {
                    long part = IsoPartition(fs);
                    Func<long, long, int, byte[]> readAt = (sector, offset, n) =>
                    {
                        var b = new byte[n];
                        fs.Position = part + sector * Sector + offset;
                        if (fs.Read(b, 0, n) != n) throw new EndOfStreamException();
                        return b;
                    };
                    byte[] root = readAt(0x20, 0x14, 8);
                    ReadTree(readAt, U32Le(root, 0), 0, "", list, 0);
                    long total = Total(list), done = 0;
                    var buf = new byte[1 << 20];
                    foreach (var e in list)
                    {
                        using (var output = Create(dest, e.Path))
                        {
                            fs.Position = part + e.Location * Sector;
                            long left = e.Length;
                            while (left > 0)
                            {
                                int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, left));
                                if (n <= 0) throw new EndOfStreamException(e.Path);
                                output.Write(buf, 0, n);
                                left -= n;
                                done += n;
                                progress(done, total);
                            }
                        }
                    }
                }
                return;
            }
            if (kind != Kind.God) throw new InvalidDataException("not an Xbox 360 disc image or Games on Demand package");
            God god = GodHeader(source);
            var files = new FileStream[god.Fragments.Length];
            try
            {
                for (int i = 0; i < files.Length; i++)
                    files[i] = new FileStream(god.Fragments[i], FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                Func<long, long, int, byte[]> readAt = (dirBlock, offset, n) =>
                {
                    int fi; long addr;
                    GodBlock(god, dirBlock + offset / Sector, out fi, out addr);
                    var b = new byte[n];
                    files[fi].Position = addr + offset % Sector;
                    if (files[fi].Read(b, 0, n) != n) throw new EndOfStreamException();
                    return b;
                };
                var rootBuf = new byte[8];
                files[0].Position = god.MagicOffset + 0x14;
                files[0].Read(rootBuf, 0, 8);
                ReadTree(readAt, U32Le(rootBuf, 0), 0, "", list, 0);
                long total = Total(list), done = 0;
                var block = new byte[Sector];
                foreach (var e in list)
                {
                    using (var output = Create(dest, e.Path))
                    {
                        long left = e.Length, b = e.Location;
                        while (left > 0)
                        {
                            int fi; long addr;
                            GodBlock(god, b, out fi, out addr);
                            int n = (int)Math.Min(Sector, left);
                            files[fi].Position = addr;
                            if (files[fi].Read(block, 0, n) != n) throw new EndOfStreamException(e.Path);
                            output.Write(block, 0, n);
                            left -= n;
                            done += n;
                            b++;
                            if ((b & 0x3FF) == 0) progress(done, total);
                        }
                    }
                }
                progress(total, total);
            }
            finally
            {
                foreach (var f in files) if (f != null) f.Dispose();
            }
        }

        static long Total(List<Entry> list)
        {
            long t = 0;
            foreach (var e in list) t += e.Length;
            return t;
        }

        static FileStream Create(string dest, string relative)
        {
            string full = Path.GetFullPath(Path.Combine(dest, relative));
            string root = Path.GetFullPath(dest).TrimEnd('\\') + "\\";
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("file outside the install folder: " + relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            return new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        }
    }

    // The Xbox dashboard art each game ships in its "nxeart" package (an STFS PIRS/LIVE/CON
    // file next to default.xex): nxebg.jpg is the 1920x1080 background. Read from the
    // player's own copy, so the launcher carries no game art.
    static class NxeArt
    {
        public static Image Background(string gameFolder)
        {
            try
            {
                if (string.IsNullOrEmpty(gameFolder)) return null;
                string path = Path.Combine(gameFolder, "nxeart");
                if (!File.Exists(path) || new FileInfo(path).Length > 64L << 20) return null;
                byte[] data = ReadFile(File.ReadAllBytes(path), "nxebg.jpg");
                if (data == null) return null;
                using (var stream = new MemoryStream(data))
                using (var image = Image.FromStream(stream))
                    return new Bitmap(image);
            }
            catch (Exception) { return null; }
        }

        static int Be32(byte[] b, int o) { return b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]; }
        static int Le24(byte[] b, int o) { return b[o] | b[o + 1] << 8 | b[o + 2] << 16; }

        static byte[] ReadFile(byte[] b, string wanted)
        {
            string magic = Encoding.ASCII.GetString(b, 0, 4);
            if (magic != "PIRS" && magic != "LIVE" && magic != "CON ") return null;
            long baseOffset = ((long)Be32(b, 0x340) + 0xFFF) & ~0xFFFL;
            const int Vd = 0x379;
            int shift = (b[Vd + 2] & 1) != 0 ? 0 : 1;
            int tableBlocks = b[Vd + 3] | b[Vd + 4] << 8;
            int tableStart = Le24(b, Vd + 5);
            Func<int, long> blockOffset = delegate(int block)
            {
                long x = block;
                foreach (int level in new[] { 0xAA, 0x70E4, 0x4AF768 })
                {
                    x += (long)((block + level) / level) << shift;
                    if (block < level) break;
                }
                return baseOffset + (x << 12);
            };
            for (int t = 0; t < tableBlocks && t < 64; t++)
            {
                long table = blockOffset(tableStart + t);
                for (int e = 0; e < 64; e++)
                {
                    long o = table + e * 0x40;
                    if (o + 0x40 > b.Length) return null;
                    int flags = b[o + 0x28];
                    int length = flags & 0x3F;
                    if (length == 0) continue;
                    if (Encoding.ASCII.GetString(b, (int)o, length) != wanted) continue;
                    // Only a file stored in consecutive blocks; the art always is.
                    if ((flags & 0x40) == 0) return null;
                    int block = Le24(b, (int)o + 0x2F);
                    int size = Be32(b, (int)o + 0x34);
                    if (size <= 0 || size > b.Length) return null;
                    var result = new byte[size];
                    for (int done = 0; done < size; done += 0x1000, block++)
                    {
                        long at = blockOffset(block);
                        int count = Math.Min(0x1000, size - done);
                        if (at + count > b.Length) return null;
                        Buffer.BlockCopy(b, (int)at, result, done, count);
                    }
                    return result;
                }
            }
            return null;
        }
    }

    // The project's icons, a guitar pick each: black for the launcher (its rim red into
    // blue), red with a 6 for Warriors of Rock, blue with a 5 for Guitar Hero 5. The rail
    // draws them, and the .ico files are rendered from this same code.
    static class PickIcon
    {
        public const int Launcher = 0, Wor = 1, Gh5 = 2;

        static System.Drawing.Drawing2D.GraphicsPath Shape(RectangleF r)
        {
            Func<float, float, PointF> p = (x, y) => new PointF(r.X + r.Width * x / 256f, r.Y + r.Height * y / 256f);
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddBezier(p(128, 10), p(198, 10), p(250, 26), p(246, 82));
            path.AddBezier(p(246, 82), p(240, 152), p(172, 222), p(128, 248));
            path.AddBezier(p(128, 248), p(84, 222), p(16, 152), p(10, 82));
            path.AddBezier(p(10, 82), p(6, 26), p(58, 10), p(128, 10));
            path.CloseFigure();
            return path;
        }

        public static void Draw(Graphics g, RectangleF r, int kind, FontFamily titleFamily)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            float u = r.Width / 256f;
            Color top, bottom;
            if (kind == Wor) { top = Color.FromArgb(240, 44, 70); bottom = Color.FromArgb(96, 6, 20); }
            else if (kind == Gh5) { top = Color.FromArgb(64, 150, 255); bottom = Color.FromArgb(8, 34, 96); }
            else { top = Color.FromArgb(40, 38, 48); bottom = Color.FromArgb(4, 4, 6); }
            using (var path = Shape(r))
            {
                using (var fill = new System.Drawing.Drawing2D.LinearGradientBrush(r, top, bottom, 90f))
                    g.FillPath(fill, path);
                // A shine across the upper half.
                using (var shine = Shape(new RectangleF(r.X + r.Width * 0.12f, r.Y + r.Height * 0.06f, r.Width * 0.76f, r.Height * 0.5f)))
                using (var light = new System.Drawing.Drawing2D.LinearGradientBrush(r, Color.FromArgb(kind == Launcher ? 26 : 70, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                    g.FillPath(light, shine);
                float rim = Math.Max(1.2f, 12f * u);
                if (kind == Launcher)
                {
                    using (var edge = new System.Drawing.Drawing2D.LinearGradientBrush(r, Color.Black, Color.Black, 0f))
                    {
                        var blend = new System.Drawing.Drawing2D.ColorBlend(3);
                        blend.Colors = new[] { Color.FromArgb(232, 36, 64), Color.FromArgb(150, 50, 230), Color.FromArgb(56, 140, 255) };
                        blend.Positions = new[] { 0f, 0.5f, 1f };
                        edge.InterpolationColors = blend;
                        using (var pen = new Pen(edge, rim)) g.DrawPath(pen, path);
                    }
                }
                else
                {
                    using (var pen = new Pen(Color.FromArgb(230, 10, 4, 8), rim)) g.DrawPath(pen, path);
                    using (var pen = new Pen(Color.FromArgb(90, 255, 255, 255), Math.Max(0.8f, 3f * u)))
                    using (var inner = Shape(new RectangleF(r.X + 14 * u, r.Y + 12 * u, r.Width - 28 * u, r.Height - 26 * u)))
                        g.DrawPath(pen, inner);
                }
            }

            string glyph = kind == Wor ? "6" : kind == Gh5 ? "5" : "E";
            FontFamily family = kind == Launcher && titleFamily != null ? titleFamily : new FontFamily("Impact");
            float size = (kind == Launcher ? 150f : 140f) * u;
            using (var font = new Font(family, size, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var text = new System.Drawing.Drawing2D.GraphicsPath())
            {
                text.AddString(glyph, family, (int)FontStyle.Regular, size, PointF.Empty, StringFormat.GenericTypographic);
                RectangleF b = text.GetBounds();
                var move = new System.Drawing.Drawing2D.Matrix();
                move.Translate(r.X + r.Width / 2f - (b.X + b.Width / 2f), r.Y + r.Height * 0.44f - (b.Y + b.Height / 2f));
                text.Transform(move);
                if (kind == Launcher)
                {
                    for (int k = 4; k >= 1; k--)
                        using (var glow = new Pen(Color.FromArgb(40, 160, 70, 255), k * 7f * u) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round })
                            g.DrawPath(glow, text);
                }
                else
                {
                    using (var shadow = (System.Drawing.Drawing2D.GraphicsPath)text.Clone())
                    using (var shift = new System.Drawing.Drawing2D.Matrix())
                    {
                        shift.Translate(5 * u, 6 * u);
                        shadow.Transform(shift);
                        using (var dark = new SolidBrush(Color.FromArgb(150, 0, 0, 0))) g.FillPath(dark, shadow);
                    }
                    using (var outline = new Pen(Color.FromArgb(220, 0, 0, 0), Math.Max(1f, 8f * u)) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round })
                        g.DrawPath(outline, text);
                }
                g.FillPath(Brushes.White, text);
            }
        }

        // An .ico with PNG images at the usual sizes.
        public static void Save(string path, int kind, FontFamily titleFamily)
        {
            int[] sizes = { 256, 128, 64, 48, 32, 24, 16 };
            var images = new List<byte[]>();
            foreach (int size in sizes)
                using (var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp)) Draw(g, new RectangleF(0.5f, 0.5f, size - 1f, size - 1f), kind, titleFamily);
                    using (var ms = new MemoryStream()) { bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png); images.Add(ms.ToArray()); }
                }
            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                    w.Write(images[i].Length); w.Write(offset);
                    offset += images[i].Length;
                }
                foreach (byte[] image in images) w.Write(image);
            }
        }
    }

    // A panel that paints without flicker.
    class Canvas : Panel
    {
        public Canvas() { DoubleBuffered = true; ResizeRedraw = true; }
    }

    class LauncherForm : Form
    {
        static string GameExe { get { return activeGame == GameGh5 ? Gh5Exe : "Guitar Hero Warriors of Rock.exe"; } }
        const string ScriptsRel = @"data\compressed\PAK\qb.pab.xen";
        // The depth of field type's name is unknown, only its QBKey. Every
        // DOF_*_tod_manager preset (DOF_Off included) uses it.
        const uint DofFxType = 0xA503619Du;
        const string ConfigName = "re_wor.toml";
        // Each game's own options: rewor_* in Warriors of Rock, erased_* in Guitar Hero 5.
        static string CvarPrefix { get { return activeGame == GameGh5 ? "erased_" : "rewor_"; } }
        const string LanguageFile = "launcher_language.txt";
        static string Title1 = "ERASED";
        static string Title2 = "REBOOT VERSION";
        // A wide window: a sidebar with the logo and the pages, the page on the right.
        // At the far left, the rail with one tile per game (Warriors of Rock, Guitar Hero 5).
        const int Rail = 76;
        const int W = 1000 + Rail;
        const int H = 620;
        const int Side = 250;
        const int ContentX = Rail + Side + 30;
        const int PageW = W - Rail - Side - 60;

        const int GameWor = 0;
        const int GameGh5 = 1;
        const string ActiveGameFile = "launcher_game.txt";
        const string Gh5Exe = "Guitar Hero 5.exe";
        const string Gh5OptionsName = "gh5_options.toml";
        static int activeGame = GameWor;
        Settings gh5Settings;

        // GH5 (Guitar Hero 5.exe, its DLLs and gh5_options.toml) lives in a "gh5" folder next to the launcher.
        string Gh5Dir { get { return Path.Combine(root, "gh5"); } }

        // The active game's folder: its exe, profile picture, mods, dlc and game_folder.txt. The launcher's own
        // files (language, window, active game) stay in gameDir.
        string PlayDir { get { return activeGame == GameGh5 ? Gh5Dir : gameDir; } }

        // Both games use the same pages, each with its own settings file.
        Settings settings { get { return activeGame == GameGh5 ? gh5Settings : worSettings; } }

        static void ApplyTheme(int game)
        {
            activeGame = game;
            if (game == GameGh5)
            {
                Red = Color.FromArgb(22, 96, 214);
                RedBright = Color.FromArgb(56, 140, 255);
                RedDark = Color.FromArgb(10, 40, 104);
                Glow = Color.FromArgb(0, 36, 120);
                Shade = Color.FromArgb(0, 18, 70);
                NavSel = Color.FromArgb(8, 18, 38);
                NavHover = Color.FromArgb(8, 14, 32);
                SealBg = Color.FromArgb(6, 12, 28);
                CardGlow = Color.FromArgb(12, 34, 92);
                Led = Color.FromArgb(90, 170, 255);
                Meter = Color.FromArgb(20, 70, 160);
                Title2 = "GUITAR HERO 5";
            }
            else
            {
                Red = Color.FromArgb(200, 16, 46);
                RedBright = Color.FromArgb(232, 36, 64);
                RedDark = Color.FromArgb(110, 8, 24);
                Glow = Color.FromArgb(120, 0, 18);
                Shade = Color.FromArgb(90, 0, 14);
                NavSel = Color.FromArgb(34, 8, 13);
                NavHover = Color.FromArgb(30, 8, 12);
                SealBg = Color.FromArgb(24, 6, 10);
                CardGlow = Color.FromArgb(92, 12, 26);
                Led = Color.FromArgb(255, 70, 90);
                Meter = Color.FromArgb(150, 20, 40);
                Title2 = "REBOOT VERSION";
            }
        }

        readonly string root = AppDomain.CurrentDomain.BaseDirectory;

        // The release keeps the game exe, its DLLs and settings in a "game"
        // folder so only the launcher sits at the top. A build folder has them
        // next to the launcher instead.
        readonly string gameDir = Directory.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "game"))
            ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "game")
            : AppDomain.CurrentDomain.BaseDirectory;
        readonly Settings worSettings;
        readonly FontFamily titleFamily = TitleFont.Load();

        ComboBox resolution, drawScale, presentEffect, anisotropy, language, gpuApi, frameRate;
        CheckBox fullscreen, vsync, gamma16, motionBlur, screenNoise, depthOfField, bloom, showFps, debugMenu, skipIntro;

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
        CheckBox kbEnabled;
        TextBox profileName;
        const int PictureSide = 64;
        const string PictureFile = "profile_picture.png";
        const string PictureData = "profile_picture.bin";
        PictureBox profilePicture, homePicture;
        Label homeName, homeFolder;
        Label[] homeCards;
        Button bottomPlay, bottomSave;
        static bool introShown;
        const double IntroMs = 1500;
        CheckBox onlineEnabled;
        ComboBox onlineIp;
        TextBox onlineHost;
        Label onlineShare;
        ComboBox micPlayer;
        readonly ComboBox[] playerReading = new ComboBox[4];
        readonly bool[] readSdl = new bool[4];
        readonly ComboBox[] playerDevices = new ComboBox[4];
        // Players 2 to 4's own profiles (the SDK's player_2_name...): each with
        // its own name and saves. Player 1's is the profile name above.
        readonly TextBox[] playerNames = new TextBox[4];
        readonly Canvas[] playerBadges = new Canvas[4];
        readonly string[] savedDevices = { "auto", "auto", "auto", "auto" };
        List<InputDevice> devices = new List<InputDevice>();
        Canvas deviceCards;
        Timer inputTimer;
        int scanCountdown;
        bool fillingDevices;
        const int HomePage = 0;
        const int GamePage = 1;
        const int PlayersPage = 4;
        const int VideoPage = 2;
        const int MicPage = 6;
        const int OnlinePage = 7;
        const string AppVersion = "2.0";
        // The ONLINE page is built but not shown until online play is tested
        // between two PCs; until then the game always starts offline.
        const bool OnlineTab = true;
        const string ReleasesPage = "https://github.com/ErasedSin/WARRIORS-OF-ROCK-Erased-Reboot-Version/releases";
        const string LatestReleaseApi = "https://api.github.com/repos/ErasedSin/WARRIORS-OF-ROCK-Erased-Reboot-Version/releases/latest";
        ComboBox micDevice;
        TrackBar micVolume, micGate;
        Label micVolumeLabel, micGateLabel, micStatus;
        Canvas micBar;
        Timer micTimer;
        MicMeter meter;
        float micLevel;
        DateTime micHeld = DateTime.MinValue;
        Label updateInstalled, updateStatus;
        Button updateButton;
        ProgressBar updateProgress;
        TextBox updateNotes;
        string updateUrl;
        readonly ToolTip readingTips = new ToolTip();
        ComboBox kbPreset;
        readonly Dictionary<string, TextBox> kbFields = new Dictionary<string, TextBox>();
        bool applyingKbPreset;

        Label status, gameFolderLabel;
        string gameFolder;
        int currentPage = -1;

        static readonly Color Bg = Color.FromArgb(10, 10, 10);
        static readonly Color Panel = Color.FromArgb(22, 22, 22);
        static readonly Color Field = Color.FromArgb(32, 32, 32);
        static Color Red = Color.FromArgb(200, 16, 46);
        static Color RedBright = Color.FromArgb(232, 36, 64);
        static Color RedDark = Color.FromArgb(110, 8, 24);
        static Color Glow = Color.FromArgb(120, 0, 18);
        static Color Shade = Color.FromArgb(90, 0, 14);
        static Color NavSel = Color.FromArgb(34, 8, 13);
        static Color NavHover = Color.FromArgb(30, 8, 12);
        static Color SealBg = Color.FromArgb(24, 6, 10);
        static Color CardGlow = Color.FromArgb(92, 12, 26);
        static Color Led = Color.FromArgb(255, 70, 90);
        static Color Meter = Color.FromArgb(150, 20, 40);
        static readonly Color Text1 = Color.FromArgb(238, 238, 238);
        static readonly Color Text2 = Color.FromArgb(150, 150, 150);

        readonly List<Button> navButtons = new List<Button>();
        readonly List<Control> pages = new List<Control>();

        public LauncherForm()
        {
            worSettings = new Settings(Path.Combine(gameDir, ConfigName));
            Strings.English = LoadLanguage();

            Text = "Erased Reboot Version - re-wor";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(W, H);
            BackColor = Color.Black;
            host = new Canvas { Size = new Size(W, H), BackColor = Bg };
            Controls.Add(host);
            BuildWindowButtons();
            Paint += PaintBackdrop;
            Resize += delegate { PlaceHost(); };
            KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.F11) { SetFullscreen(!fullscreenWindow); e.Handled = true; }
            };
            SetFullscreen(LoadWindowMode());
            ForeColor = Text1;
            Font = new Font("Segoe UI", 9.5f);
            DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

            gh5Settings = new Settings(Path.Combine(Gh5Dir, Gh5OptionsName));
            ApplyTheme(LoadActiveGame());
            LoadGameFolder();

            BuildUi();
            LoadSettings();
            ShowPage(IsGameFolder(gameFolder) ? HomePage : GamePage);
            ShowGamePicker();
            Shown += delegate
            {
                if (picker == null && !IsGameFolder(gameFolder)) ChooseGameFolder();
                autoUpdateCheck = true;
                CheckForUpdate();
            };
            FormClosed += delegate { if (meter != null) { meter.Dispose(); meter = null; } };
            PlayIntro();
        }

        int LoadActiveGame()
        {
            try { return File.ReadAllText(Path.Combine(gameDir, ActiveGameFile)).Trim() == "gh5" ? GameGh5 : GameWor; }
            catch (Exception) { return GameWor; }
        }

        void LoadGameFolder()
        {
            gameFolder = FolderOf(activeGame);
        }

        // The rail's tiles: the launcher keeps each game's settings in its own file and
        // rebuilds its pages in that game's colors.
        void SwitchGame(int game)
        {
            if (game == activeGame) return;
            CollectSettings();
            if (meter != null) { meter.Dispose(); meter = null; }
            ApplyTheme(game);
            try { File.WriteAllText(Path.Combine(gameDir, ActiveGameFile), game == GameGh5 ? "gh5" : "wor"); } catch (Exception) { }
            LoadGameFolder();
            RebuildUi(0);
            if (!IsGameFolder(gameFolder)) ShowPage(GamePage);
            Invalidate(true);
        }

        void RebuildUi(int page)
        {
            SuspendLayout();
            var old = new List<Control>();
            foreach (Control c in host.Controls) old.Add(c);
            host.Controls.Clear();
            foreach (Control c in old) c.Dispose();
            navButtons.Clear();
            pages.Clear();
            BuildUi();
            LoadSettings();
            ShowPage(page < 0 || page >= pages.Count ? 0 : page);
            ResumeLayout();
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
            if (meter != null) { meter.Dispose(); meter = null; }
            Strings.English = english;
            try { File.WriteAllText(Path.Combine(gameDir, LanguageFile), english ? "en" : "pt"); } catch (Exception) { }
            RebuildUi(currentPage);
        }

        Label pageTitle;
        string[] pageNames;

        // The launcher's pages keep their 1000 x 620 layout in "host", centered in
        // the window: fullscreen by default (F11 or the button at the top right
        // switches), remembered in launcher_window.txt.
        Canvas host;
        bool fullscreenWindow;
        Button windowModeButton;
        readonly ToolTip windowTips = new ToolTip();
        const string WindowFile = "launcher_window.txt";

        bool LoadWindowMode()
        {
            try { return File.ReadAllText(Path.Combine(gameDir, WindowFile)).Trim() != "window"; }
            catch (Exception) { return true; }
        }

        void SetFullscreen(bool full)
        {
            fullscreenWindow = full;
            SuspendLayout();
            if (full)
            {
                FormBorderStyle = FormBorderStyle.None;
                MaximizeBox = false;
                WindowState = FormWindowState.Normal;
                Bounds = Screen.FromControl(this).Bounds;
            }
            else
            {
                FormBorderStyle = FormBorderStyle.FixedSingle;
                MaximizeBox = false;
                WindowState = FormWindowState.Normal;
                ClientSize = new Size(W, H);
                Rectangle area = Screen.FromControl(this).WorkingArea;
                Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
            }
            ResumeLayout();
            PlaceHost();
            if (windowModeButton != null)
            {
                windowModeButton.Text = full ? "\u2750" : "\u2610";
                windowTips.SetToolTip(windowModeButton, Strings.T(full ? "win_mode_window" : "win_mode_full"));
            }
            foreach (Control c in Controls)
                if (c.Tag as string == "window") c.Visible = full;
            try { File.WriteAllText(Path.Combine(gameDir, WindowFile), full ? "fullscreen" : "window"); } catch (Exception) { }
            Invalidate();
        }

        void PlaceHost()
        {
            if (host == null) return;
            host.Location = new Point(Math.Max(0, (ClientSize.Width - W) / 2), Math.Max(0, (ClientSize.Height - H) / 2));
            int x = ClientSize.Width - 8;
            foreach (Control c in Controls)
            {
                if (c.Tag as string != "window") continue;
                x -= c.Width;
                c.Location = new Point(x, 8);
                c.BringToFront();
            }
            Invalidate();
        }

        // Minimize, window mode and close, at the window's top right.
        void BuildWindowButtons()
        {
            var specs = new[]
            {
                new[] { "\u2715", "win_close" },
                new[] { "\u2750", "win_mode_window" },
                new[] { "\u2014", "win_minimize" },
            };
            foreach (string[] spec in specs)
            {
                var b = new Button
                {
                    Text = spec[0], Size = new Size(46, 34), FlatStyle = FlatStyle.Flat, BackColor = Color.Black,
                    ForeColor = Text2, Font = new Font("Segoe UI Symbol", 11f), Tag = "window", TabStop = false,
                    Cursor = Cursors.Hand
                };
                b.FlatAppearance.BorderSize = 0;
                b.FlatAppearance.MouseOverBackColor = spec[1] == "win_close" ? Red : Color.FromArgb(40, 40, 40);
                windowTips.SetToolTip(b, Strings.T(spec[1]));
                if (spec[1] == "win_close") b.Click += delegate { Close(); };
                else if (spec[1] == "win_minimize") b.Click += delegate { WindowState = FormWindowState.Minimized; };
                else { b.Click += delegate { SetFullscreen(!fullscreenWindow); }; windowModeButton = b; }
                Controls.Add(b);
            }
        }

        // Around the pages in fullscreen: black, a red glow from below, thin
        // diagonal stripes and a red frame round the panel.
        void PaintBackdrop(object sender, PaintEventArgs e)
        {
            if (!fullscreenWindow || host == null) return;
            var g = e.Graphics;
            Rectangle all = ClientRectangle;
            if (all.Width <= 0 || all.Height <= 0) return;
            using (var glow = new System.Drawing.Drawing2D.LinearGradientBrush(
                all, Color.FromArgb(0, 0, 0), Color.FromArgb(70, Glow), 90f))
                g.FillRectangle(glow, all);
            using (var stripe = new Pen(Color.FromArgb(22, Red), 2f))
                for (int x = -all.Height; x < all.Width; x += 22)
                    g.DrawLine(stripe, x, all.Height, x + all.Height, 0);
            Rectangle frame = host.Bounds;
            frame.Inflate(1, 1);
            using (var pen = new Pen(RedDark, 2f)) g.DrawRectangle(pen, frame);
            if (all.Height - host.Bottom > 60)
            {
                Rectangle below = new Rectangle(0, host.Bottom + 12, all.Width, all.Height - host.Bottom - 12);
                using (var brush = new SolidBrush(Color.FromArgb(70, 255, 255, 255)))
                using (var font = new Font(titleFamily, 14f, FontStyle.Regular, GraphicsUnit.Point))
                using (var format = new StringFormat { Alignment = StringAlignment.Center })
                    g.DrawString(Title1 + " " + Title2, font, brush, below, format);
            }
        }

        void BuildUi()
        {
            host.Controls.Add(BuildRail());
            host.Controls.Add(BuildSidebar());

            pageTitle = new Label
            {
                Location = new Point(ContentX, 18), Size = new Size(PageW - 110, 44), ForeColor = Color.White,
                BackColor = Bg, Font = new Font(titleFamily, 22f, FontStyle.Regular, GraphicsUnit.Point),
                UseCompatibleTextRendering = true
            };
            host.Controls.Add(pageTitle);
            host.Controls.Add(MakeLanguageButton("PT", W - 30 - 82, false));
            host.Controls.Add(MakeLanguageButton("EN", W - 30 - 40, true));
            host.Controls.Add(BuildSeal());

            pageNames = PageNames();
            var builtList = new List<Control>();
            var onlinePage = BuildOnlineTab();
            builtList.AddRange(new[] { BuildHomeTab(), BuildGameTab(), BuildVideoTab(), BuildGraphicsTab(), BuildPlayersTab(), BuildKeyboardTab(), BuildMicTab() });
            if (OnlineTab) builtList.Add(onlinePage);
            builtList.Add(BuildControlsTab());
            builtList.Add(BuildProfileTab());
            builtList.Add(BuildUpdateTab());
            Control[] built = builtList.ToArray();
            for (int i = 0; i < built.Length; i++)
            {
                built[i].Location = new Point(ContentX, 142);
                built[i].Size = new Size(PageW, 384);
                pages.Add(built[i]);
                host.Controls.Add(built[i]);
            }

            ShowProfilePicture();

            status = new Label { Location = new Point(ContentX, 548), Size = new Size(PageW - 300, 40), ForeColor = Text2, TextAlign = ContentAlignment.MiddleLeft };
            host.Controls.Add(status);

            var save = MakeButton(Strings.T("save"), new Point(W - 30 - 160 - 12 - 120, 544), false);
            save.Size = new Size(120, 48);
            save.Click += delegate { if (Apply()) status.Text = Strings.T("saved"); };
            host.Controls.Add(save);

            var play = MakeButton(Strings.T("play"), new Point(W - 30 - 160, 544), true);
            play.Size = new Size(160, 48);
            play.Font = new Font("Segoe UI", 13f, FontStyle.Bold);
            play.Click += delegate { if (Apply()) Launch(); };
            host.Controls.Add(play);
            AcceptButton = play;
            bottomPlay = play;
            bottomSave = save;
            if (currentPage == HomePage) play.Visible = save.Visible = false;
        }

        static string[] PageNames()
        {
            var names = new List<string> { Strings.T("tab_home"), Strings.T("tab_game"), Strings.T("tab_video"), Strings.T("tab_gfx"), Strings.T("tab_players"), Strings.T("tab_keyboard"), Strings.T("tab_mic") };
            if (OnlineTab) names.Add(Strings.T("tab_online"));
            names.Add(Strings.T("tab_controls"));
            names.Add(Strings.T("tab_profile"));
            names.Add(Strings.T("tab_update"));
            return names.ToArray();
        }

        // The game rail, as in a launcher for a series: a tile per game, the active one lit
        // in its color with a bar at its side.
        Control BuildRail()
        {
            var rail = new Canvas { Location = new Point(0, 0), Size = new Size(Rail, H), BackColor = Color.Black };
            string[] names = { "Guitar Hero: Warriors of Rock", "Guitar Hero 5" };
            Color[] colors = { Color.FromArgb(200, 16, 46), Color.FromArgb(22, 96, 214) };
            for (int i = 0; i < 2; i++)
            {
                int game = i;
                var tile = new Canvas
                {
                    Location = new Point(10, 20 + i * 74), Size = new Size(Rail - 20, Rail - 20),
                    BackColor = Color.Black, Cursor = Cursors.Hand
                };
                tile.Paint += delegate(object s, PaintEventArgs e)
                {
                    var g = e.Graphics;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    bool on = game == activeGame;
                    var r = new Rectangle(0, 0, tile.Width - 1, tile.Height - 1);
                    Color c = colors[game];
                    using (var fill = new SolidBrush(on ? Color.FromArgb(60, c) : Color.FromArgb(14, 14, 16)))
                        g.FillRectangle(fill, r);
                    PickIcon.Draw(g, new RectangleF(7, 6, tile.Width - 14, tile.Height - 14), game == GameWor ? PickIcon.Wor : PickIcon.Gh5, titleFamily);
                    if (!on)
                        using (var veil = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
                            g.FillRectangle(veil, r);
                    using (var border = new Pen(on ? Color.White : Color.FromArgb(90, c), on ? 2f : 1f))
                        g.DrawRectangle(border, r);
                };
                tile.Click += delegate { SwitchGame(game); };
                windowTips.SetToolTip(tile, names[game]);
                rail.Controls.Add(tile);
            }
            // At the rail's foot, a grid that goes back to the game picker.
            var all = new Canvas
            {
                Location = new Point(10, H - 20 - (Rail - 20)), Size = new Size(Rail - 20, Rail - 20),
                BackColor = Color.Black, Cursor = Cursors.Hand
            };
            bool allHover = false;
            all.MouseEnter += delegate { allHover = true; all.Invalidate(); };
            all.MouseLeave += delegate { allHover = false; all.Invalidate(); };
            all.Paint += delegate(object s, PaintEventArgs e)
            {
                var g = e.Graphics;
                var r = new Rectangle(0, 0, all.Width - 1, all.Height - 1);
                using (var fill = new SolidBrush(allHover ? Color.FromArgb(34, 34, 38) : Color.FromArgb(18, 18, 20)))
                    g.FillRectangle(fill, r);
                using (var border = new Pen(Color.FromArgb(allHover ? 160 : 70, 255, 255, 255)))
                    g.DrawRectangle(border, r);
                int cell = 9, gap = 4, side = cell * 2 + gap;
                int x0 = (all.Width - side) / 2, y0 = (all.Height - side) / 2;
                for (int i = 0; i < 4; i++)
                {
                    Color c = i == 0 || i == 3 ? GameColors[i == 0 ? 0 : 1] : Color.FromArgb(allHover ? 230 : 170, 255, 255, 255);
                    using (var brush = new SolidBrush(c))
                        g.FillRectangle(brush, x0 + (i % 2) * (cell + gap), y0 + (i / 2) * (cell + gap), cell, cell);
                }
            };
            all.Click += delegate { ShowGamePicker(); };
            windowTips.SetToolTip(all, Strings.T("rail_games"));
            rail.Controls.Add(all);
            rail.Paint += delegate(object s, PaintEventArgs e)
            {
                using (var bar = new SolidBrush(Red))
                    e.Graphics.FillRectangle(bar, 0, 20 + activeGame * 74, 4, Rail - 20);
                using (var line = new SolidBrush(Color.FromArgb(28, 28, 30)))
                    e.Graphics.FillRectangle(line, Rail - 1, 0, 1, H);
            };
            return rail;
        }

        static readonly Color[] GameColors = { Color.FromArgb(200, 16, 46), Color.FromArgb(22, 96, 214) };
        static readonly Color Clash = Color.FromArgb(140, 40, 220);
        const int BaseCardW = 330, BaseCardH = 420, BaseCardGap = 48;
        const int ParallaxMargin = 90;
        Canvas picker;
        Timer pickerTimer;
        Image[] pickerSource;
        Bitmap[] pickerArt, pickerBack;
        Size pickerBuiltFor;
        readonly float[] pickerLift = new float[2];
        int pickerHover = -1, pickerFocus;
        float pickerSide, pickerMouse, pickerFront;
        DateTime pickerOpened;
        readonly Random pickerRandom = new Random();
        PointF[] pickerBolt, pickerBranch;
        DateTime pickerBoltAt;
        float[][] pickerSparks;

        // Where a game's files are, or null: the folder saved for it, and for Warriors of Rock
        // also the "assets" folder next to the launcher.
        string FolderOf(int game)
        {
            string folder = game == GameGh5
                ? gh5Settings.Get("erased_game_folder", gh5Settings.Get("game_data_root", ""))
                : worSettings.Get("rewor_game_folder", "");
            folder = folder.Replace('/', '\\');
            if (IsGameFolder(folder)) return folder;
            return game == GameWor ? NormalizeGameFolder(root) : null;
        }

        // The picker scales with the window: 1 in the 1076x620 window, more in fullscreen.
        float PickerScale()
        {
            if (picker == null) return 1f;
            float s = Math.Min(picker.Width / (float)W, picker.Height / (float)H);
            return Math.Max(0.8f, Math.Min(1.45f, s));
        }

        Rectangle CardRect(int game)
        {
            float s = PickerScale();
            int w = (int)(BaseCardW * s), h = (int)(BaseCardH * s), gap = (int)(BaseCardGap * s);
            int x0 = (picker.Width - (w * 2 + gap)) / 2;
            int y = (int)(picker.Height * 0.48f - h / 2f);
            return new Rectangle(x0 + game * (w + gap), y, w, h);
        }

        // The game's dashboard art cut to the card, around the point that matters
        // (Warriors of Rock's mountain, Guitar Hero 5's guitarist).
        static Bitmap CoverArt(Image source, int width, int height, float focusX)
        {
            var card = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(card))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                float scale = Math.Max((float)width / source.Width, (float)height / source.Height);
                float w = source.Width * scale, h = source.Height * scale;
                float x = Math.Max(width - w, Math.Min(0, width / 2f - w * focusX));
                g.DrawImage(source, x, (height - h) / 2f, w, h);
            }
            return card;
        }

        // The far layer: the game's art, blurred and dark, tinted in its color.
        static Bitmap BackArt(Image source, int width, int height, Color tint)
        {
            var back = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(back))
            {
                g.Clear(Color.Black);
                if (source != null)
                {
                    using (var tiny = new Bitmap(Math.Max(1, width / 14), Math.Max(1, height / 14)))
                    {
                        using (var t = Graphics.FromImage(tiny))
                        {
                            t.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                            float scale = Math.Max((float)tiny.Width / source.Width, (float)tiny.Height / source.Height);
                            t.DrawImage(source, (tiny.Width - source.Width * scale) / 2f, (tiny.Height - source.Height * scale) / 2f,
                                source.Width * scale, source.Height * scale);
                        }
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                        g.DrawImage(tiny, new Rectangle(0, 0, width, height));
                    }
                }
                using (var dark = new SolidBrush(Color.FromArgb(170, tint.R / 6, tint.G / 6, tint.B / 6)))
                    g.FillRectangle(dark, 0, 0, width, height);
            }
            return back;
        }

        void BuildPickerArt()
        {
            if (picker == null || picker.Width <= 0 || picker.Height <= 0) return;
            if (pickerBuiltFor == picker.Size && pickerArt != null) return;
            DisposePickerArt(false);
            pickerBuiltFor = picker.Size;
            Rectangle card = CardRect(0);
            float[] focus = { 0.5f, 0.33f };
            pickerArt = new Bitmap[2];
            pickerBack = new Bitmap[2];
            for (int i = 0; i < 2; i++)
            {
                if (pickerSource[i] != null) pickerArt[i] = CoverArt(pickerSource[i], card.Width, card.Height, focus[i]);
                pickerBack[i] = BackArt(pickerSource[i], picker.Width + ParallaxMargin * 2, picker.Height, GameColors[i]);
            }
            pickerSparks = new float[70][];
            for (int i = 0; i < pickerSparks.Length; i++) pickerSparks[i] = NewSpark(true);
        }

        // An ember: x, y, rise speed, size, drift.
        float[] NewSpark(bool anywhere)
        {
            float w = picker.Width + ParallaxMargin * 2, h = picker.Height;
            return new[]
            {
                (float)pickerRandom.NextDouble() * w - ParallaxMargin,
                anywhere ? (float)pickerRandom.NextDouble() * h : h + 10,
                0.6f + (float)pickerRandom.NextDouble() * 1.8f,
                1.2f + (float)pickerRandom.NextDouble() * 2.6f,
                (float)(pickerRandom.NextDouble() - 0.5) * 0.6f
            };
        }

        void DisposePickerArt(bool sources)
        {
            foreach (Bitmap[] set in new[] { pickerArt, pickerBack })
                if (set != null) foreach (Bitmap b in set) if (b != null) b.Dispose();
            pickerArt = pickerBack = null;
            if (sources && pickerSource != null)
            {
                foreach (Image img in pickerSource) if (img != null) img.Dispose();
                pickerSource = null;
            }
        }

        // The hub's first screen: one big card per game over the two colors at war,
        // red against blue meeting in purple. The scene leans toward the chosen game.
        void ShowGamePicker()
        {
            if (picker != null) return;
            pickerSource = new[] { NxeArt.Background(FolderOf(GameWor)), NxeArt.Background(FolderOf(GameGh5)) };
            pickerFocus = activeGame;
            pickerHover = -1;
            pickerSide = pickerFocus == GameWor ? 1f : -1f;
            pickerFront = pickerSide;
            pickerMouse = 0;
            pickerLift[0] = pickerLift[1] = 0;
            pickerOpened = DateTime.UtcNow;
            pickerBuiltFor = Size.Empty;
            picker = new Canvas { Dock = DockStyle.Fill, BackColor = Color.Black };
            picker.Paint += PaintPicker;
            picker.Resize += delegate { BuildPickerArt(); picker.Invalidate(); };
            picker.MouseMove += delegate(object s, MouseEventArgs e)
            {
                pickerMouse = picker.Width > 0 ? (e.X / (float)picker.Width) * 2f - 1f : 0f;
                int hit = -1;
                for (int i = 0; i < 2; i++) if (CardRect(i).Contains(e.Location)) hit = i;
                if (hit == pickerHover) return;
                pickerHover = hit;
                if (hit >= 0) pickerFocus = hit;
                picker.Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
            };
            picker.MouseLeave += delegate { pickerHover = -1; pickerMouse = 0; };
            picker.MouseClick += delegate(object s, MouseEventArgs e)
            {
                for (int i = 0; i < 2; i++) if (CardRect(i).Contains(e.Location)) { PickGame(i); return; }
            };
            pickerTimer = new Timer { Interval = 30 };
            pickerTimer.Tick += delegate { StepPicker(); };
            Controls.Add(picker);
            picker.BringToFront();
            foreach (Control c in Controls) if (c.Tag as string == "window") c.BringToFront();
            BuildPickerArt();
            pickerTimer.Start();
        }

        void StepPicker()
        {
            if (picker == null) return;
            float target = (pickerFocus == GameWor ? 1f : -1f);
            pickerSide += (target - pickerSide) * 0.12f;
            pickerFront += (target * 0.85f + pickerMouse * -0.15f - pickerFront) * 0.08f;
            for (int i = 0; i < 2; i++) pickerLift[i] += ((i == pickerFocus ? 1f : 0f) - pickerLift[i]) * 0.22f;
            if (pickerSparks != null)
                for (int i = 0; i < pickerSparks.Length; i++)
                {
                    float[] p = pickerSparks[i];
                    p[1] -= p[2];
                    p[0] += p[4] + (float)Math.Sin((p[1] + i * 40) / 60f) * 0.3f;
                    if (p[1] < -10) pickerSparks[i] = NewSpark(false);
                }
            if (pickerBolt == null || (DateTime.UtcNow - pickerBoltAt).TotalMilliseconds > 70 + pickerRandom.Next(120))
                MakeBolt();
            picker.Invalidate();
        }

        float FrontX()
        {
            // The front line: pushed toward the game not chosen.
            return picker.Width * (0.5f + pickerFront * 0.17f);
        }

        void MakeBolt()
        {
            pickerBoltAt = DateTime.UtcNow;
            int h = picker.Height, steps = 22;
            pickerBolt = new PointF[steps + 1];
            float x = 0;
            for (int i = 0; i <= steps; i++)
            {
                x = Math.Max(-38, Math.Min(38, x + (float)(pickerRandom.NextDouble() - 0.5) * 30));
                pickerBolt[i] = new PointF(x, h * i / (float)steps);
            }
            pickerBranch = null;
            if (pickerRandom.Next(3) == 0)
            {
                int from = 4 + pickerRandom.Next(steps - 8);
                int dir = pickerRandom.Next(2) == 0 ? -1 : 1;
                pickerBranch = new PointF[7];
                PointF at = pickerBolt[from];
                for (int i = 0; i < pickerBranch.Length; i++)
                {
                    pickerBranch[i] = at;
                    at = new PointF(at.X + dir * (14 + (float)pickerRandom.NextDouble() * 22), at.Y + 10 + (float)pickerRandom.NextDouble() * 22);
                }
            }
        }

        void ClosePicker()
        {
            if (picker == null) return;
            pickerTimer.Stop();
            pickerTimer.Dispose();
            Controls.Remove(picker);
            picker.Dispose();
            picker = null;
            DisposePickerArt(true);
        }

        void PickGame(int game)
        {
            ClosePicker();
            if (game != activeGame) SwitchGame(game);
            if (!IsGameFolder(gameFolder)) { ShowPage(GamePage); ChooseGameFolder(); }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (picker != null)
            {
                if (keyData == Keys.Left || keyData == Keys.Right)
                {
                    pickerFocus = keyData == Keys.Left ? 0 : 1;
                    pickerHover = -1;
                    return true;
                }
                if (keyData == Keys.Enter || keyData == Keys.Space) { PickGame(pickerFocus); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // Words one by one, centered: the title font's space is very narrow.
        void DrawTitleLine(Graphics g, string text, Font font, Color color, float centerX, float y)
        {
            float gap = font.SizeInPoints * 0.28f, width = -gap;
            string[] words = text.Split(' ');
            foreach (string word in words) width += g.MeasureString(word, font).Width + gap;
            float x = centerX - width / 2f;
            using (var shadow = new SolidBrush(Color.FromArgb(Math.Min(color.A, (byte)190), 0, 0, 0)))
            using (var brush = new SolidBrush(color))
                foreach (string word in words)
                {
                    g.DrawString(word, font, shadow, x + 3, y + 3);
                    g.DrawString(word, font, brush, x, y);
                    x += g.MeasureString(word, font).Width + gap;
                }
        }

        void PaintPicker(object sender, PaintEventArgs e)
        {
            if (pickerBack == null) return;
            var g = e.Graphics;
            int cw = picker.Width, ch = picker.Height;
            float s = PickerScale();
            double t = Math.Min(1, (DateTime.UtcNow - pickerOpened).TotalMilliseconds / 500);
            double ease = 1 - Math.Pow(1 - t, 3);
            float side = pickerSide * 0.8f + pickerMouse * 0.2f;

            // Far layer: the chosen game's art, fading into the other's as the choice moves.
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
            float mix = (1 - pickerSide) / 2f;
            int backX = (int)(-ParallaxMargin + side * ParallaxMargin * 0.45f);
            g.DrawImageUnscaled(pickerBack[mix < 0.5f ? GameWor : GameGh5], backX, 0);
            float other = mix < 0.5f ? mix : 1 - mix;
            if (other > 0.02f)
            {
                using (var fade = new System.Drawing.Imaging.ImageAttributes())
                {
                    var m = new System.Drawing.Imaging.ColorMatrix { Matrix33 = other * 2f };
                    fade.SetColorMatrix(m);
                    Bitmap b = pickerBack[mix < 0.5f ? GameGh5 : GameWor];
                    g.DrawImage(b, new Rectangle(backX, 0, b.Width, b.Height), 0, 0, b.Width, b.Height, GraphicsUnit.Pixel, fade);
                }
            }

            // The war: red from the left, blue from the right, purple where they meet.
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float front = FrontX();
            float spread = cw * 0.32f;
            var all = new Rectangle(-2, 0, cw + 4, ch);
            using (var war = new System.Drawing.Drawing2D.LinearGradientBrush(all, Color.Black, Color.Black, 0f))
            {
                var blend = new System.Drawing.Drawing2D.ColorBlend(5);
                Color red = GameColors[0], blue = GameColors[1];
                blend.Colors = new[]
                {
                    Color.FromArgb(200, red.R / 2, red.G / 2, red.B / 2),
                    Color.FromArgb(150, red),
                    Color.FromArgb(170, Clash),
                    Color.FromArgb(150, blue),
                    Color.FromArgb(200, blue.R / 2, blue.G / 2, blue.B / 2)
                };
                float f = Math.Max(0.12f, Math.Min(0.88f, front / cw));
                float half = spread / cw / 2f;
                blend.Positions = new[] { 0f, Math.Max(0.01f, f - half), f, Math.Min(0.99f, f + half), 1f };
                war.InterpolationColors = blend;
                g.FillRectangle(war, all);
            }
            using (var shade = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, cw, ch),
                Color.FromArgb(150, 0, 0, 0), Color.FromArgb(40, 0, 0, 0), 90f))
            {
                var blend = new System.Drawing.Drawing2D.Blend(3);
                blend.Factors = new[] { 0.6f, 0f, 1f };
                blend.Positions = new[] { 0f, 0.45f, 1f };
                shade.Blend = blend;
                g.FillRectangle(shade, 0, 0, cw, ch);
            }

            // Shards from the corners, red ones on the left and blue on the right, nearer than the art.
            float shardX = side * 34 * s;
            for (int i = 0; i < 2; i++)
            {
                float dir = i == 0 ? 1 : -1, edge = i == 0 ? 0 : cw;
                PointF[][] shapes =
                {
                    new[] { new PointF(edge, 60 * s), new PointF(edge + dir * 240 * s, 0), new PointF(edge + dir * 300 * s, 0), new PointF(edge, 130 * s) },
                    new[] { new PointF(edge, ch - 230 * s), new PointF(edge + dir * 190 * s, ch), new PointF(edge + dir * 110 * s, ch), new PointF(edge, ch - 170 * s) },
                    new[] { new PointF(edge + dir * 60 * s, ch * 0.42f), new PointF(edge + dir * 150 * s, ch * 0.36f), new PointF(edge + dir * 110 * s, ch * 0.45f) },
                };
                using (var brush = new SolidBrush(Color.FromArgb(i == pickerFocus ? 200 : 120, GameColors[i])))
                    foreach (PointF[] shape in shapes)
                    {
                        var moved = new PointF[shape.Length];
                        for (int k = 0; k < shape.Length; k++) moved[k] = new PointF(shape[k].X + shardX, shape[k].Y);
                        g.FillPolygon(brush, moved);
                    }
            }

            // The clash: a purple glow band and lightning along the front line.
            float bandX = front + side * 20 * s;
            var band = new RectangleF(bandX - 160 * s, 0, 320 * s, ch);
            using (var glow = new System.Drawing.Drawing2D.LinearGradientBrush(band, Color.Transparent, Color.Transparent, 0f))
            {
                var blend = new System.Drawing.Drawing2D.ColorBlend(3);
                blend.Colors = new[] { Color.FromArgb(0, Clash), Color.FromArgb(110, 190, 120, 255), Color.FromArgb(0, Clash) };
                blend.Positions = new[] { 0f, 0.5f, 1f };
                glow.InterpolationColors = blend;
                g.FillRectangle(glow, band);
            }
            if (pickerBolt != null)
            {
                foreach (PointF[] line in new[] { pickerBolt, pickerBranch })
                {
                    if (line == null) continue;
                    var pts = new PointF[line.Length];
                    for (int k = 0; k < line.Length; k++) pts[k] = new PointF(bandX + line[k].X * s, line[k].Y);
                    using (var outer = new Pen(Color.FromArgb(50, 170, 90, 255), 12f * s))
                    using (var mid = new Pen(Color.FromArgb(140, 200, 140, 255), 4f * s))
                    using (var core = new Pen(Color.FromArgb(230, 245, 235, 255), 1.6f * s))
                    {
                        outer.LineJoin = mid.LineJoin = core.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                        g.DrawLines(outer, pts);
                        g.DrawLines(mid, pts);
                        g.DrawLines(core, pts);
                    }
                }
            }

            // Embers rising, in the color of the side they are on; the nearest layer behind the cards.
            if (pickerSparks != null)
            {
                float sparkX = side * 60 * s;
                foreach (float[] p in pickerSparks)
                {
                    float x = p[0] + sparkX;
                    Color c = x < front ? Color.FromArgb(255, 110, 60) : Color.FromArgb(90, 170, 255);
                    if (Math.Abs(x - front) < 60 * s) c = Color.FromArgb(210, 160, 255);
                    int a = (int)(200 * Math.Min(1, p[1] / (ch * 0.6f)));
                    using (var brush = new SolidBrush(Color.FromArgb(Math.Max(0, Math.Min(255, a)), c)))
                        g.FillEllipse(brush, x, p[1], p[3] * s, p[3] * s);
                }
            }

            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.Default;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            using (var logo = new Font(titleFamily, 20f * s, FontStyle.Regular, GraphicsUnit.Point))
            {
                float gap = logo.SizeInPoints * 0.28f;
                float first = g.MeasureString("ERASED", logo).Width;
                float rebootW = g.MeasureString("REBOOT", logo).Width;
                float second = rebootW + gap + g.MeasureString("VERSION", logo).Width;
                float x = (cw - first - gap - second) / 2f, y = 26 * s;
                g.DrawString("ERASED", logo, Brushes.White, x, y);
                x += first + gap;
                using (var grey = new SolidBrush(Color.FromArgb(185, 175, 200)))
                {
                    g.DrawString("REBOOT", logo, grey, x, y);
                    g.DrawString("VERSION", logo, grey, x + rebootW + gap, y);
                }
            }

            string[] small = { "GUITAR HERO", "GUITAR HERO" };
            string[][] big = { new[] { "WARRIORS", "OF ROCK" }, new[] { "5" } };
            float cardShift = -side * 14 * s;
            int bottom = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                // The chosen card is drawn last, over its neighbour.
                int i = pass == 0 ? 1 - pickerFocus : pickerFocus;
                double own = Math.Max(0, Math.Min(1, (t * 500 - i * 110) / 390));
                own = 1 - Math.Pow(1 - own, 3);
                int alpha = (int)(255 * own);
                Rectangle baseRect = CardRect(i);
                bottom = Math.Max(bottom, baseRect.Bottom);
                if (alpha <= 0) continue;
                float grow = 1f + 0.05f * pickerLift[i];
                var r = new RectangleF(
                    baseRect.X + cardShift - baseRect.Width * (grow - 1) / 2f,
                    baseRect.Y + (float)((1 - own) * 36 * s) - pickerLift[i] * 12 * s - baseRect.Height * (grow - 1) / 2f,
                    baseRect.Width * grow, baseRect.Height * grow);
                bool on = i == pickerFocus;
                Color color = GameColors[i];
                Color bright = Color.FromArgb(Math.Min(255, color.R + 70), Math.Min(255, color.G + 70), Math.Min(255, color.B + 70));

                for (int k = 1; k <= 14; k++)
                {
                    int a = (int)(alpha * 0.17 * pickerLift[i] * (1 - k / 15.0));
                    if (a <= 0) continue;
                    RectangleF halo = r;
                    halo.Inflate(k * 2 * s, k * 2 * s);
                    using (var pen = new Pen(Color.FromArgb(a, bright), 2f * s))
                        g.DrawRectangle(pen, halo.X, halo.Y, halo.Width, halo.Height);
                }

                var state = g.Save();
                g.SetClip(r);
                if (pickerArt[i] != null)
                {
                    // Drawn plain and darkened with a veil: GDI+ leaves a seam across a scaled
                    // image drawn through a color matrix.
                    g.DrawImage(pickerArt[i], r);
                    float dim = 0.55f + 0.45f * pickerLift[i];
                    int veil = 255 - (int)(255 * dim * alpha / 255f);
                    if (veil > 0)
                        using (var dark = new SolidBrush(Color.FromArgb(veil, 0, 0, 0)))
                            g.FillRectangle(dark, r);
                }
                else
                {
                    using (var fill = new System.Drawing.Drawing2D.LinearGradientBrush(r,
                        Color.FromArgb(alpha, color), Color.FromArgb(alpha, color.R / 5, color.G / 5, color.B / 5), 90f))
                        g.FillRectangle(fill, r);
                    using (var stripe = new Pen(Color.FromArgb(alpha / 8, 255, 255, 255), 2f))
                        for (float x = r.Left - r.Height; x < r.Right; x += 22)
                            g.DrawLine(stripe, x, r.Bottom, x + r.Height, r.Top);
                }
                var lower = new RectangleF(r.X, r.Y + r.Height / 3f, r.Width, r.Height * 2f / 3f + 1);
                // The brush a little taller than the fill: GDI+ wraps a gradient's last color onto its first row.
                var lowerBrush = RectangleF.Inflate(lower, 0, 3);
                using (var shade = new System.Drawing.Drawing2D.LinearGradientBrush(lowerBrush,
                    Color.FromArgb(0, 0, 0, 0), Color.FromArgb(alpha * 235 / 255, color.R / 4, color.G / 4, color.B / 4), 90f))
                    g.FillRectangle(shade, lower);
                g.Restore(state);

                using (var border = new Pen(Color.FromArgb(alpha, on ? Color.White : color), (on ? 4f : 3f) * s))
                    g.DrawRectangle(border, r.X, r.Y, r.Width - 1, r.Height - 1);

                float cx = r.X + r.Width / 2f;
                string[] lines = big[i];
                // The title font's 5 reads as an S: the number goes in Impact.
                bool number = lines.Length == 1;
                float k2 = s * grow;
                using (var smallFont = new Font(titleFamily, 17f * k2, FontStyle.Regular, GraphicsUnit.Point))
                using (var bigFont = number ? new Font("Impact", 92f * k2, FontStyle.Regular, GraphicsUnit.Point)
                                            : new Font(titleFamily, 38f * k2, FontStyle.Regular, GraphicsUnit.Point))
                {
                    float lineH = g.MeasureString("A", bigFont).Height * (number ? 0.86f : 0.82f);
                    float y = r.Bottom - 64 * k2 - lineH * lines.Length;
                    DrawTitleLine(g, small[i], smallFont, Color.FromArgb(alpha * 220 / 255, 255, 255, 255), cx, y - 30 * k2);
                    foreach (string line in lines)
                    {
                        DrawTitleLine(g, line, bigFont, Color.FromArgb(alpha, 255, 255, 255), cx, y);
                        y += lineH;
                    }
                }

                bool ready = pickerSource[i] != null || FolderOf(i) != null;
                string note = Strings.T(ready ? "pick_ready" : "pick_missing");
                using (var noteFont = new Font("Segoe UI", 9f * s, FontStyle.Bold))
                {
                    SizeF size = g.MeasureString(note, noteFont);
                    var pill = new RectangleF(cx - size.Width / 2f - 12 * s, r.Bottom - 40 * k2, size.Width + 24 * s, 24 * s);
                    using (var fill = new SolidBrush(Color.FromArgb(alpha * 200 / 255, ready ? Color.Black : color)))
                        g.FillRectangle(fill, pill);
                    using (var dot = new SolidBrush(Color.FromArgb(alpha, 80, 220, 120)))
                    using (var text = new SolidBrush(Color.FromArgb(alpha, 235, 235, 235)))
                    using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    {
                        if (ready) g.FillEllipse(dot, pill.X + 8 * s, pill.Y + pill.Height / 2f - 3 * s, 6 * s, 6 * s);
                        g.DrawString(note, noteFont, text, new RectangleF(pill.X + (ready ? 6 * s : 0), pill.Y, pill.Width, pill.Height), format);
                    }
                }
            }

            using (var titleFont = new Font(titleFamily, 22f * s, FontStyle.Regular, GraphicsUnit.Point))
                DrawTitleLine(g, Strings.T("pick_title"), titleFont, Color.FromArgb((int)(255 * ease), 255, 255, 255), cw / 2f, bottom + 22 * s);
            using (var hint = new Font("Segoe UI", 9f * Math.Min(s, 1.3f)))
            using (var grey = new SolidBrush(Color.FromArgb(190, 185, 200)))
            using (var center = new StringFormat { Alignment = StringAlignment.Center })
            {
                g.DrawString(Strings.T("pick_hint"), hint, grey, new RectangleF(0, bottom + 66 * s, cw, 30 * s), center);
                string name = settings.Get("online_name", "");
                float footY = ch - 34 * Math.Min(s, 1.3f);
                if (name.Length > 0)
                {
                    g.DrawString(Strings.T("pick_profile"), hint, grey, 24, footY);
                    using (var bold = new Font(hint, FontStyle.Bold))
                        g.DrawString(name, bold, Brushes.White, 24 + g.MeasureString(Strings.T("pick_profile"), hint).Width, footY);
                }
                g.DrawString("v" + AppVersion, hint, grey, cw - 24 - g.MeasureString("v" + AppVersion, hint).Width, footY);
            }
        }

        // The sidebar: the logo in the title font, the pages, and the footer.
        Control BuildSidebar()
        {
            var side = new Panel { Location = new Point(Rail, 0), Size = new Size(Side, H), BackColor = Color.FromArgb(6, 6, 7) };
            side.Paint += delegate(object s, PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using (var glow = new System.Drawing.Drawing2D.LinearGradientBrush(
                    new Rectangle(0, 0, Side, 150), Color.FromArgb(90, Glow), Color.FromArgb(0, 6, 6, 7), 90f))
                    g.FillRectangle(glow, 0, 0, Side, 150);
                using (var line = new System.Drawing.Drawing2D.LinearGradientBrush(
                    new Rectangle(Side - 3, 0, 3, H), RedBright, RedDark, 90f))
                    g.FillRectangle(line, Side - 3, 0, 3, H);

                using (var big = new Font(titleFamily, 30f, FontStyle.Regular, GraphicsUnit.Point))
                using (var small = new Font(titleFamily, 14f, FontStyle.Regular, GraphicsUnit.Point))
                using (var shadow = new SolidBrush(Color.FromArgb(160, Shade)))
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
                Text = Strings.T(activeGame == GameGh5 ? "subtitle_gh5" : "subtitle"), Location = new Point(24, 112), Size = new Size(Side - 40, 32),
                ForeColor = Text2, BackColor = Color.Transparent, Font = new Font("Segoe UI", 8f)
            });

            string[] names = PageNames();
            // Ten pages (Guitar Hero 5's) sit a little closer together to fit above the footer.
            int step = names.Length > 10 ? 35 : names.Length > 9 ? 39 : 44;
            for (int i = 0; i < names.Length; i++)
            {
                int index = i;
                var nav = new Button
                {
                    Text = "   " + names[i], Location = new Point(16, 156 + i * step), Size = new Size(Side - 35, step - 4),
                    FlatStyle = FlatStyle.Flat, ForeColor = Text2, BackColor = side.BackColor, Cursor = Cursors.Hand,
                    Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, TabStop = false
                };
                nav.FlatAppearance.BorderSize = 0;
                nav.FlatAppearance.MouseOverBackColor = NavHover;
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
            var seal = new Panel { Location = new Point(ContentX, 70), Size = new Size(PageW, 60), BackColor = SealBg };
            seal.Paint += delegate(object s, PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var border = new Pen(Color.FromArgb(110, Red), 1f)) g.DrawRectangle(border, 0, 0, seal.Width - 1, seal.Height - 1);
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
                Text = Strings.T(activeGame == GameGh5 ? "seal_text_gh5" : "seal_text"), Location = new Point(58, 24), Size = new Size(seal.Width - 68, 34),
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
                navButtons[i].BackColor = i == index ? NavSel : Color.FromArgb(6, 6, 7);
                navButtons[i].Invalidate();
            }
            if (pageTitle != null && pageNames != null) pageTitle.Text = WithoutAccents(pageNames[index]);
            if (micDevice != null && (index == MicPage) != (meter != null)) RestartMicMeter();
            if (index == HomePage) ShowHome();
            if (index == Array.IndexOf(pageNames, Strings.T("tab_profile"))) ShowProfile();
            // The home page has its own big PLAY button.
            if (bottomPlay != null) bottomPlay.Visible = index != HomePage;
            if (bottomSave != null) bottomSave.Visible = index != HomePage;
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
            int half = (PageW - 48) / 2, right = 18 + half + 12;
            AddLabel(page, Strings.T("game_lang"), 144);
            language = AddCombo(page, 164,
                new Choice("English", "1"),
                new Choice("Español", "5"),
                new Choice("Français", "4"),
                new Choice("Deutsch", "3"),
                new Choice("Italiano", "6"));
            language.Width = half;
            page.Controls.Add(new Label { Text = Strings.T("audio_buffer"), Location = new Point(right, 144), AutoSize = true, ForeColor = Text1 });
            audioBuffer = AddCombo(page, 164,
                new Choice(Strings.T("audio_16"), "16"),
                new Choice("32", "32"),
                new Choice(Strings.T("audio_64"), "64"));
            audioBuffer.Location = new Point(right, 164);
            audioBuffer.Width = half;
            debugMenu = AddCheck(page, Strings.T("debug_menu"), 204);
            skipIntro = AddCheck(page, Strings.T("skip_intro"), 230);
            AddNote(page, Strings.T(activeGame == GameGh5 ? "gh5_game_note" : "game_note"), 262);
            AddNote(page, Strings.T(activeGame == GameGh5 ? "gh5_dlc_note" : "dlc_note"), 312);
            ShowGameFolder();
            return page;
        }

        // The game installs what is in this folder when it starts (src/dlc.cpp, rewor_dlc_folder);
        // for Guitar Hero 5 the launcher does, when PLAY is pressed (InstallGh5Dlc).
        void OpenDlcFolder()
        {
            string folder = Path.Combine(activeGame == GameGh5 ? Gh5Dir : gameDir, "dlc");
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

        // The player picks default.xex (files already extracted), a disc image (.iso) or the
        // header file of a Games on Demand package; images and packages are installed to a folder.
        void ChooseGameFolder()
        {
            string source;
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = Strings.T("folder_dialog");
                dialog.Filter = Strings.T("source_filter") + "|*.*";
                dialog.CheckFileExists = true;
                if (IsGameFolder(gameFolder)) dialog.InitialDirectory = gameFolder;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                source = dialog.FileName;
            }
            if (string.Equals(Path.GetFileName(source), "default.xex", StringComparison.OrdinalIgnoreCase))
            {
                string chosen = NormalizeGameFolder(Path.GetDirectoryName(source));
                if (chosen == null)
                {
                    MessageBox.Show(this, Strings.T("bad_folder"), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                gameFolder = chosen;
                ShowGameFolder();
                return;
            }
            if (XboxImage.Detect(source) == XboxImage.Kind.None)
            {
                MessageBox.Show(this, Strings.T("bad_source"), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string dest;
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = Strings.T("install_dialog");
                dialog.ShowNewFolderButton = true;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                dest = Path.Combine(dialog.SelectedPath, activeGame == GameGh5 ? "Guitar Hero 5" : "Guitar Hero Warriors of Rock");
            }
            if (InstallFromImage(source, dest) && IsGameFolder(dest))
            {
                gameFolder = dest;
                ShowGameFolder();
            }
        }

        bool InstallFromImage(string source, string dest)
        {
            var form = new Form
            {
                Text = Text, FormBorderStyle = FormBorderStyle.FixedDialog, ControlBox = false,
                StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(440, 110),
                BackColor = Bg, ForeColor = Text1, Font = Font, ShowInTaskbar = false,
            };
            var label = new Label { Text = Strings.T("installing"), Location = new Point(16, 14), Size = new Size(408, 22) };
            var bar = new ProgressBar { Location = new Point(16, 44), Size = new Size(408, 22), Maximum = 1000 };
            var detail = new Label { Location = new Point(16, 74), Size = new Size(408, 22), ForeColor = Text2 };
            form.Controls.Add(label);
            form.Controls.Add(bar);
            form.Controls.Add(detail);
            Exception error = null;
            form.Shown += delegate
            {
                var worker = new System.Threading.Thread(() =>
                {
                    try
                    {
                        XboxImage.Extract(source, dest, (done, total) =>
                        {
                            int value = total > 0 ? (int)(done * 1000 / total) : 0;
                            string text = string.Format("{0:0.0} / {1:0.0} GB", done / 1073741824.0, total / 1073741824.0);
                            form.BeginInvoke((Action)delegate { bar.Value = Math.Min(1000, value); detail.Text = text; });
                        });
                    }
                    catch (Exception e) { error = e; }
                    form.BeginInvoke((Action)form.Close);
                });
                worker.IsBackground = true;
                worker.Start();
            };
            form.ShowDialog(this);
            if (error != null)
            {
                MessageBox.Show(this, Strings.T("install_failed") + "\n\n" + error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            if (!IsGameFolder(dest))
            {
                MessageBox.Show(this, Strings.T("bad_folder"), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            return true;
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

            // The emulated vertical blank's rate: the game presents one frame per
            // vertical blank, and src/framerate.cpp keeps what it counts in frames
            // at the console's 60 a second.
            AddLabel(page, Strings.T("fps_label"), y);
            frameRate = AddCombo(page, y + 20,
                new Choice(Strings.T("fps_60"), "60"),
                new Choice("120 FPS", "120"),
                new Choice("144 FPS", "144"),
                new Choice("165 FPS", "165"),
                new Choice("240 FPS", "240"));
            y += 60;

            fullscreen = AddCheck(page, Strings.T("fullscreen"), y); y += 28;
            vsync = AddCheck(page, Strings.T("vsync"), y); y += 28;
            showFps = AddCheck(page, Strings.T("show_fps"), y); y += 36;

            AddNote(page, Strings.T("video_note"), y);
            return page;
        }

        // The PLAYERS page: the devices the PC has, as cards that light up when a
        // button is pressed on them, and the device each of the four players uses.
        Control BuildPlayersTab()
        {
            var page = NewPage();
            const int ColumnW = 316;
            const int RightX = 356;

            page.Controls.Add(new Label
            {
                Text = Strings.T("devices_title"), Location = new Point(18, 12), AutoSize = true,
                ForeColor = RedBright, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            });
            deviceCards = new Canvas { Location = new Point(18, 34), Size = new Size(ColumnW, 286), BackColor = Panel };
            deviceCards.Paint += PaintDeviceCards;
            page.Controls.Add(deviceCards);
            page.Controls.Add(new Label
            {
                Text = Strings.T("devices_hint"), Location = new Point(18, 326), Size = new Size(ColumnW, 40),
                ForeColor = Text2, Font = new Font("Segoe UI", 8.5f)
            });

            page.Controls.Add(new Label { Text = Strings.T("profile_name"), Location = new Point(RightX, 12), Size = new Size(ColumnW, 18), ForeColor = Text1, Font = new Font("Segoe UI", 8.5f) });
            profileName = new TextBox
            {
                Location = new Point(RightX, 32), Size = new Size(ColumnW - 56, 24), BorderStyle = BorderStyle.FixedSingle,
                BackColor = Field, ForeColor = Text1, MaxLength = 15
            };
            page.Controls.Add(profileName);
            profilePicture = new PictureBox
            {
                Location = new Point(RightX + ColumnW - 48, 10), Size = new Size(48, 48), BackColor = Field,
                BorderStyle = BorderStyle.FixedSingle, SizeMode = PictureBoxSizeMode.Zoom, Cursor = Cursors.Hand
            };
            profilePicture.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right) RemoveProfilePicture();
                else ChooseProfilePicture();
            };
            new ToolTip().SetToolTip(profilePicture, Strings.T("picture_hint"));
            page.Controls.Add(profilePicture);

            page.Controls.Add(new Label
            {
                Text = Strings.T("players_title"), Location = new Point(RightX, 70), AutoSize = true,
                ForeColor = RedBright, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            });
            for (int i = 0; i < 4; i++)
            {
                int player = i;
                int y = i == 0 ? 92 : 130 + (i - 1) * 56;
                var badge = new Canvas { Location = new Point(RightX, y), Size = new Size(40, 28), BackColor = Panel };
                badge.Paint += delegate(object s, PaintEventArgs e) { PaintBadge(e.Graphics, player, badge.ClientSize); };
                page.Controls.Add(badge);
                playerBadges[i] = badge;
                var combo = new ComboBox
                {
                    Location = new Point(RightX + 48, y + 2), Size = new Size(ColumnW - 48 - 74, 26), DropDownStyle = ComboBoxStyle.DropDownList,
                    FlatStyle = FlatStyle.Flat, BackColor = Field, ForeColor = Text1
                };
                combo.SelectedIndexChanged += delegate { OnPlayerDeviceChanged(player); };
                page.Controls.Add(combo);
                playerDevices[i] = combo;
                var reading = new ComboBox
                {
                    Location = new Point(RightX + ColumnW - 68, y + 2), Size = new Size(68, 26), DropDownStyle = ComboBoxStyle.DropDownList,
                    FlatStyle = FlatStyle.Flat, BackColor = Field, ForeColor = Text1
                };
                reading.Items.AddRange(new object[] { Strings.T("reading_xinput"), Strings.T("reading_sdl") });
                reading.SelectedIndexChanged += delegate
                {
                    if (!fillingDevices && reading.Enabled) readSdl[player] = reading.SelectedIndex == 1;
                };
                readingTips.SetToolTip(reading, Strings.T("reading_tip"));
                page.Controls.Add(reading);
                playerReading[i] = reading;
                if (i == 0) continue;
                var nameBox = new TextBox
                {
                    Location = new Point(RightX + 48, y + 30), Size = new Size(ColumnW - 48, 22), BorderStyle = BorderStyle.FixedSingle,
                    BackColor = Field, ForeColor = Text1, MaxLength = 15
                };
                SetCue(nameBox, string.Format(Strings.T("player_name_cue"), i + 1));
                page.Controls.Add(nameBox);
                playerNames[i] = nameBox;
            }


            page.Controls.Add(new Label
            {
                Text = Strings.T("players_note"), Location = new Point(RightX, 300), Size = new Size(ColumnW, 62),
                ForeColor = Text2, Font = new Font("Segoe UI", 8f)
            });

            devices = InputScan.Scan();
            FillPlayerDevices();

            if (inputTimer != null) { inputTimer.Stop(); inputTimer.Dispose(); }
            inputTimer = new Timer { Interval = 50 };
            inputTimer.Tick += delegate { PollDevices(); };
            inputTimer.Start();
            return page;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, string lParam);

        // The grey text an empty box shows (EM_SETCUEBANNER).
        static void SetCue(TextBox box, string text)
        {
            box.HandleCreated += delegate { SendMessage(box.Handle, 0x1501, (IntPtr)1, text); };
        }

        void PollDevices()
        {
            if (currentPage != PlayersPage || deviceCards == null || deviceCards.IsDisposed) return;
            if (--scanCountdown <= 0)
            {
                scanCountdown = 40;
                var found = InputScan.Scan();
                if (DeviceListChanged(found))
                {
                    devices = found;
                    FillPlayerDevices();
                }
            }
            InputScan.Poll(devices, Form.ActiveForm == this && !profileName.Focused);
            deviceCards.Invalidate();
            foreach (var badge in playerBadges) badge.Invalidate();
        }

        bool DeviceListChanged(List<InputDevice> found)
        {
            if (found.Count != devices.Count) return true;
            for (int i = 0; i < found.Count; i++)
                if (found[i].Spec != devices[i].Spec || found[i].Title != devices[i].Title) return true;
            return false;
        }

        InputDevice DeviceFor(string spec)
        {
            foreach (var d in devices) if (d.Spec == spec) return d;
            return null;
        }

        string PlayerSpec(int player)
        {
            var combo = playerDevices[player];
            return combo.SelectedItem == null ? savedDevices[player] : Value(combo);
        }

        // Lists Automatic, the connected devices and Nobody in each player's box,
        // keeping each choice (one whose device is not connected stays, marked).
        void FillPlayerDevices()
        {
            fillingDevices = true;
            for (int i = 0; i < 4; i++)
            {
                var combo = playerDevices[i];
                string current = PlayerSpec(i);
                combo.Items.Clear();
                combo.Items.Add(new Choice(Strings.T("dev_auto"), "auto"));
                foreach (var d in devices) combo.Items.Add(new Choice(d.Title + "  —  " + ShortDetail(d), d.Spec));
                combo.Items.Add(new Choice(Strings.T("dev_none"), "none"));
                if (current != "auto" && current != "none" && DeviceFor(current) == null)
                    combo.Items.Add(new Choice(string.Format(Strings.T("dev_missing"), current), current));
                SelectChoice(combo, current);
                UpdateReading(i);
            }
            fillingDevices = false;
            if (deviceCards != null) deviceCards.Invalidate();
        }

        // The reading box: a choice only for an XInput controller; the other
        // devices have one way to be read, shown greyed.
        void UpdateReading(int player)
        {
            string spec = PlayerSpec(player);
            var reading = playerReading[player];
            bool choice = spec.StartsWith("xinput");
            bool wasFilling = fillingDevices;
            fillingDevices = true;
            reading.Enabled = choice;
            reading.SelectedIndex = choice ? (readSdl[player] ? 1 : 0) : spec.StartsWith("hid:") ? 1 : 0;
            fillingDevices = wasFilling;
        }

        string SavedSpec(int player)
        {
            string spec = PlayerSpec(player);
            return spec.StartsWith("xinput") && readSdl[player] ? spec + ":sdl" : spec;
        }

        static string ShortDetail(InputDevice d)
        {
            if (d.XInputSlot >= 0) return "XInput " + (d.XInputSlot + 1);
            if (d.Kind == "keyboard") return "PC";
            return d.Spec.Substring(4);
        }

        // One device per player: picking a device another player has gives that
        // player Automatic.
        void OnPlayerDeviceChanged(int player)
        {
            if (fillingDevices) return;
            string spec = PlayerSpec(player);
            savedDevices[player] = spec;
            UpdateReading(player);
            if (spec != "auto" && spec != "none")
            {
                fillingDevices = true;
                for (int i = 0; i < 4; i++)
                {
                    if (i == player || PlayerSpec(i) != spec) continue;
                    SelectChoice(playerDevices[i], "auto");
                    savedDevices[i] = "auto";
                    UpdateReading(i);
                }
                fillingDevices = false;
            }
            deviceCards.Invalidate();
        }

        int PlayerOf(string spec)
        {
            for (int i = 0; i < 4; i++) if (PlayerSpec(i) == spec) return i;
            return -1;
        }

        static Color Mix(Color a, Color b, float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        void PaintDeviceCards(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            int w = deviceCards.ClientSize.Width;
            const int gap = 6;
            int h = Math.Max(34, Math.Min(52, (deviceCards.ClientSize.Height + gap) / Math.Max(1, devices.Count) - gap));
            using (var titleFont = new Font("Segoe UI", 9.5f, FontStyle.Bold))
            using (var detailFont = new Font("Segoe UI", 7.75f))
            using (var tagFont = new Font("Segoe UI", 7.5f, FontStyle.Bold))
            {
                for (int i = 0; i < devices.Count; i++)
                {
                    var d = devices[i];
                    int y = i * (h + gap);
                    if (y + h > deviceCards.ClientSize.Height) break;
                    float glow = d.Glow;
                    int player = PlayerOf(d.Spec);
                    var card = new Rectangle(0, y, w - 1, h - 1);
                    using (var fill = new SolidBrush(Mix(Field, CardGlow, glow))) g.FillRectangle(fill, card);
                    using (var border = new Pen(Mix(Color.FromArgb(48, 48, 48), RedBright, glow))) g.DrawRectangle(border, card);
                    using (var accent = new SolidBrush(player >= 0 ? Red : Color.FromArgb(58, 58, 58))) g.FillRectangle(accent, 0, y, 4, h);

                    var icon = new Rectangle(14, y + (h - 28) / 2, 34, 28);
                    DrawDeviceIcon(g, d.Kind, icon, Mix(Text2, Color.White, Math.Max(glow, player >= 0 ? 0.6f : 0f)));

                    bool twoLines = h >= 44;
                    using (var white = new SolidBrush(Text1))
                        g.DrawString(d.Title, titleFont, white, new RectangleF(58, y + (twoLines ? 7 : (h - 18) / 2), w - 150, 18));
                    if (twoLines)
                        using (var grey = new SolidBrush(Text2))
                            g.DrawString(d.Detail, detailFont, grey, new RectangleF(58, y + 27, w - 96, 16));

                    if (player >= 0)
                    {
                        string tag = string.Format(Strings.T("player_badge"), player + 1);
                        SizeF size = g.MeasureString(tag, tagFont);
                        using (var red = new SolidBrush(RedBright))
                            g.DrawString(tag, tagFont, red, w - 36 - size.Width, y + (twoLines ? 8 : (h - size.Height) / 2));
                    }

                    // The light: grey at rest, a red glow while a button is pressed.
                    var led = new Rectangle(w - 26, y + h / 2 - 6, 12, 12);
                    if (glow > 0)
                    {
                        using (var halo = new SolidBrush(Color.FromArgb((int)(110 * glow), RedBright)))
                            g.FillEllipse(halo, led.X - 5, led.Y - 5, led.Width + 10, led.Height + 10);
                    }
                    using (var dot = new SolidBrush(Mix(Color.FromArgb(70, 70, 70), Led, glow))) g.FillEllipse(dot, led);
                }
            }
        }

        void PaintBadge(Graphics g, int player, Size size)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var d = DeviceFor(PlayerSpec(player));
            float glow = d != null ? d.Glow : 0f;
            using (var fill = new SolidBrush(Mix(Color.FromArgb(44, 44, 44), RedBright, Math.Max(glow, d != null ? 0.55f : 0f))))
                g.FillRectangle(fill, 0, 0, size.Width, size.Height);
            using (var font = new Font("Segoe UI", 10f, FontStyle.Bold))
            using (var white = new SolidBrush(Color.White))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString("P" + (player + 1), font, white, new RectangleF(0, 0, size.Width, size.Height), format);
        }

        // Simple line drawings of each kind of device.
        static void DrawDeviceIcon(Graphics g, string kind, Rectangle r, Color color)
        {
            using (var pen = new Pen(color, 2f))
            using (var thick = new Pen(color, 3f))
            using (var brush = new SolidBrush(color))
            {
                pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                pen.StartCap = pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                if (kind == "guitar")
                {
                    g.DrawLine(thick, r.X + 15, r.Bottom - 12, r.Right - 4, r.Y + 3);
                    g.FillRectangle(brush, r.Right - 7, r.Y, 6, 5);
                    g.DrawEllipse(pen, r.X + 3, r.Bottom - 17, 13, 13);
                    g.DrawEllipse(pen, r.X + 9, r.Bottom - 22, 10, 10);
                    g.FillEllipse(brush, r.X + 8, r.Bottom - 12, 4, 4);
                }
                else if (kind == "drums")
                {
                    g.DrawEllipse(pen, r.X + 3, r.Y + 4, r.Width - 6, 9);
                    g.DrawLine(pen, r.X + 3, r.Y + 8, r.X + 3, r.Bottom - 6);
                    g.DrawLine(pen, r.Right - 3, r.Y + 8, r.Right - 3, r.Bottom - 6);
                    g.DrawArc(pen, r.X + 3, r.Bottom - 11, r.Width - 6, 9, 0, 180);
                }
                else if (kind == "keyboard")
                {
                    g.DrawRectangle(pen, r.X + 1, r.Y + 6, r.Width - 2, r.Height - 12);
                    for (int row = 0; row < 2; row++)
                        for (int col = 0; col < 5; col++)
                            g.FillRectangle(brush, r.X + 5 + col * 5, r.Y + 10 + row * 5, 3, 3);
                    g.FillRectangle(brush, r.X + 9, r.Y + 20, 14, 2);
                }
                else
                {
                    using (var body = new System.Drawing.Drawing2D.GraphicsPath())
                    {
                        body.AddArc(r.X + 1, r.Y + 6, 14, 18, 90, 180);
                        body.AddArc(r.Right - 15, r.Y + 6, 14, 18, 270, 180);
                        body.CloseFigure();
                        g.DrawPath(pen, body);
                    }
                    g.DrawLine(pen, r.X + 7, r.Y + 15, r.X + 13, r.Y + 15);
                    g.DrawLine(pen, r.X + 10, r.Y + 12, r.X + 10, r.Y + 18);
                    g.FillEllipse(brush, r.Right - 12, r.Y + 11, 4, 4);
                    g.FillEllipse(brush, r.Right - 8, r.Y + 15, 4, 4);
                }
            }
        }

        // The MICROPHONE page: who sings, which microphone, and a live meter of
        // what the game will hear, with its volume and sensitivity.
        Control BuildMicTab()
        {
            var page = NewPage();
            int half = (PageW - 48) / 2;
            int rightX = 18 + half + 12;

            page.Controls.Add(new Label { Text = Strings.T("mic_who"), Location = new Point(18, 16), AutoSize = true, ForeColor = Text1 });
            micPlayer = new ComboBox
            {
                Location = new Point(18, 38), Size = new Size(half, 26), DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat, BackColor = Field, ForeColor = Text1
            };
            micPlayer.Items.Add(new Choice(Strings.T("mic_none"), "0"));
            for (int i = 1; i <= 4; i++) micPlayer.Items.Add(new Choice(string.Format(Strings.T("mic_player"), i), i.ToString()));
            page.Controls.Add(micPlayer);

            page.Controls.Add(new Label { Text = Strings.T("mic_device"), Location = new Point(rightX, 16), AutoSize = true, ForeColor = Text1 });
            micDevice = new ComboBox
            {
                Location = new Point(rightX, 38), Size = new Size(half, 26), DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat, BackColor = Field, ForeColor = Text1
            };
            micDevice.Items.Add(new Choice(Strings.T("mic_default"), ""));
            foreach (string name in MicMeter.Devices()) micDevice.Items.Add(new Choice(name, name));
            micDevice.SelectedIndexChanged += delegate { if (currentPage == MicPage) RestartMicMeter(); };
            page.Controls.Add(micDevice);

            page.Controls.Add(new Label
            {
                Text = Strings.T("mic_level"), Location = new Point(18, 84), AutoSize = true,
                ForeColor = RedBright, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            });
            micBar = new Canvas { Location = new Point(18, 106), Size = new Size(PageW - 36, 34), BackColor = Field };
            micBar.Paint += PaintMicBar;
            page.Controls.Add(micBar);
            micStatus = new Label { Location = new Point(18, 146), Size = new Size(PageW - 36, 20), ForeColor = Text2 };
            page.Controls.Add(micStatus);

            micVolumeLabel = new Label { Location = new Point(18, 182), Size = new Size(half, 20), ForeColor = Text1 };
            page.Controls.Add(micVolumeLabel);
            micVolume = new TrackBar
            {
                Location = new Point(14, 204), Size = new Size(half + 8, 36), Minimum = 0, Maximum = 400,
                SmallChange = 5, LargeChange = 25, TickStyle = TickStyle.None, BackColor = Panel, Value = 100
            };
            micVolume.ValueChanged += delegate { UpdateMicLabels(); };
            page.Controls.Add(micVolume);

            micGateLabel = new Label { Location = new Point(rightX, 182), Size = new Size(half, 20), ForeColor = Text1 };
            page.Controls.Add(micGateLabel);
            micGate = new TrackBar
            {
                Location = new Point(rightX - 4, 204), Size = new Size(half + 8, 36), Minimum = 0, Maximum = 50,
                SmallChange = 1, LargeChange = 5, TickStyle = TickStyle.None, BackColor = Panel
            };
            micGate.ValueChanged += delegate { UpdateMicLabels(); };
            page.Controls.Add(micGate);

            page.Controls.Add(new Label
            {
                Text = Strings.T("mic_note"), Location = new Point(18, 252), Size = new Size(PageW - 36, 80),
                ForeColor = Text2, Font = new Font("Segoe UI", 8.5f)
            });
            UpdateMicLabels();

            if (micTimer != null) { micTimer.Stop(); micTimer.Dispose(); }
            micTimer = new Timer { Interval = 40 };
            micTimer.Tick += delegate { PollMic(); };
            micTimer.Start();
            return page;
        }

        // What the meter multiplies the input by: the volume in percent.
        float MicGain()
        {
            return micVolume.Value / 100f;
        }

        void UpdateMicLabels()
        {
            micVolumeLabel.Text = string.Format(Strings.T("mic_volume"), micVolume.Value);
            micGateLabel.Text = micGate.Value == 0 ? Strings.T("mic_gate_off") : string.Format(Strings.T("mic_gate"), micGate.Value);
            if (micBar != null) micBar.Invalidate();
        }

        void RestartMicMeter()
        {
            if (meter != null) { meter.Dispose(); meter = null; }
            micLevel = 0f;
            if (currentPage != MicPage) return;
            string device = Value(micDevice);
            meter = new MicMeter();
            if (!meter.Open(device))
            {
                meter = null;
                micStatus.Text = Strings.T("mic_missing");
            }
            else micStatus.Text = Strings.T("mic_listening");
        }

        void PollMic()
        {
            if (meter == null || micBar == null || micBar.IsDisposed) return;
            float peak = meter.Peak();
            if (peak >= 0f)
            {
                float level = Math.Min(1f, peak * MicGain());
                // Rises at once, falls slowly, like a VU meter.
                micLevel = Math.Max(level, micLevel * 0.82f);
                micHeld = level >= micGate.Value / 100f && micGate.Value > 0 ? DateTime.Now : micHeld;
                bool open = micGate.Value == 0 || (DateTime.Now - micHeld).TotalMilliseconds < 250;
                micStatus.Text = open
                    ? string.Format(Strings.T("mic_hearing"), (int)Math.Round(micLevel * 100))
                    : Strings.T("mic_silenced");
                micStatus.ForeColor = open && micLevel > 0.02f ? Text1 : Text2;
            }
            micBar.Invalidate();
        }

        void PaintMicBar(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            int w = micBar.ClientSize.Width, h = micBar.ClientSize.Height;
            float gate = micGate.Value / 100f;
            bool open = micGate.Value == 0 || (DateTime.Now - micHeld).TotalMilliseconds < 250;
            int filled = (int)(w * Math.Min(1f, micLevel));
            if (filled > 0)
            {
                using (var fill = new System.Drawing.Drawing2D.LinearGradientBrush(
                    new Rectangle(0, 0, w, h), open ? Meter : Color.FromArgb(70, 70, 70),
                    open ? RedBright : Color.FromArgb(110, 110, 110), 0f))
                    g.FillRectangle(fill, 0, 0, filled, h);
            }
            if (micLevel >= 0.98f)
                using (var clip = new SolidBrush(Color.White)) g.FillRectangle(clip, w - 6, 0, 6, h);
            if (micGate.Value > 0)
            {
                int x = (int)(w * gate);
                using (var shade = new SolidBrush(Color.FromArgb(90, 0, 0, 0))) g.FillRectangle(shade, 0, 0, x, h);
                using (var line = new Pen(Color.White, 2f)) g.DrawLine(line, x, 0, x, h);
            }
            using (var border = new Pen(Color.FromArgb(70, 70, 70))) g.DrawRectangle(border, 0, 0, w - 1, h - 1);
        }

        // The UPDATE page: the version installed, the latest release on GitHub,
        // its notes, and a button that downloads and installs it.
        Control BuildUpdateTab()
        {
            var page = NewPage();
            updateInstalled = new Label
            {
                Text = string.Format(Strings.T("update_installed"), AppVersion), Location = new Point(18, 18), AutoSize = true,
                ForeColor = Text1, Font = new Font("Segoe UI", 10f, FontStyle.Bold)
            };
            page.Controls.Add(updateInstalled);
            updateStatus = new Label { Text = Strings.T("update_idle"), Location = new Point(18, 44), Size = new Size(PageW - 36, 20), ForeColor = Text2 };
            page.Controls.Add(updateStatus);

            var check = MakeButton(Strings.T("update_check"), new Point(18, 72), false);
            check.Size = new Size(150, 34);
            check.Click += delegate { CheckForUpdate(); };
            page.Controls.Add(check);
            updateButton = MakeButton(Strings.T("update_install"), new Point(178, 72), true);
            updateButton.Size = new Size(190, 34);
            updateButton.Enabled = false;
            updateButton.Click += delegate { InstallUpdate(); };
            page.Controls.Add(updateButton);
            var site = new LinkLabel
            {
                Text = Strings.T("update_page"), Location = new Point(384, 82), AutoSize = true,
                LinkColor = RedBright, ActiveLinkColor = Color.White, VisitedLinkColor = RedBright
            };
            site.LinkClicked += delegate { try { Process.Start(ReleasesPage); } catch (Exception) { } };
            page.Controls.Add(site);

            updateProgress = new ProgressBar { Location = new Point(18, 116), Size = new Size(PageW - 36, 8), Visible = false };
            page.Controls.Add(updateProgress);
            updateNotes = new TextBox
            {
                Location = new Point(18, 132), Size = new Size(PageW - 36, 196), Multiline = true, ReadOnly = true,
                ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.FixedSingle, BackColor = Field, ForeColor = Text1,
                Font = new Font("Segoe UI", 9f)
            };
            page.Controls.Add(updateNotes);
            page.Controls.Add(new Label
            {
                Text = Strings.T("update_note"), Location = new Point(18, 336), Size = new Size(PageW - 36, 40),
                ForeColor = Text2, Font = new Font("Segoe UI", 8.5f)
            });
            return page;
        }

        static Version ParseVersion(string text)
        {
            var m = System.Text.RegularExpressions.Regex.Match(text ?? "", @"\d+(\.\d+){0,3}");
            if (!m.Success) return new Version(0, 0);
            string v = m.Value;
            if (!v.Contains(".")) v += ".0";
            try { return new Version(v); } catch (Exception) { return new Version(0, 0); }
        }

        static string JsonString(string json, string key)
        {
            var m = System.Text.RegularExpressions.Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return "";
            try { return System.Text.RegularExpressions.Regex.Unescape(m.Groups[1].Value); } catch (Exception) { return m.Groups[1].Value; }
        }

        static System.Net.WebClient NewWebClient()
        {
            System.Net.ServicePointManager.SecurityProtocol |= (System.Net.SecurityProtocolType)3072;  // TLS 1.2
            var web = new System.Net.WebClient();
            web.Headers.Add("User-Agent", "ErasedRebootVersion-Launcher");
            web.Encoding = Encoding.UTF8;
            return web;
        }

        // At every start, quietly: a new version asks whether to install it, and
        // anything else (no internet, already up to date) says nothing.
        bool autoUpdateCheck;

        void CheckForUpdate()
        {
            updateStatus.Text = Strings.T("update_checking");
            updateStatus.ForeColor = Text2;
            updateButton.Enabled = false;
            var web = NewWebClient();
            web.DownloadStringCompleted += delegate(object s, System.Net.DownloadStringCompletedEventArgs e)
            {
                web.Dispose();
                if (IsDisposed) return;
                bool automatic = autoUpdateCheck;
                autoUpdateCheck = false;
                if (e.Error != null)
                {
                    updateStatus.Text = automatic ? Strings.T("update_idle") : Strings.T("update_failed");
                    return;
                }
                string json = e.Result;
                string tag = JsonString(json, "tag_name");
                string notes = JsonString(json, "body");
                var asset = System.Text.RegularExpressions.Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+\\.zip)\"");
                updateUrl = asset.Success ? asset.Groups[1].Value : null;
                updateNotes.Text = (tag + "\r\n\r\n" + notes.Replace("\r\n", "\n").Replace("\n", "\r\n")).Trim();
                bool newer = ParseVersion(tag) > ParseVersion(AppVersion);
                if (newer && updateUrl != null)
                {
                    updateStatus.Text = string.Format(Strings.T("update_available"), tag);
                    updateStatus.ForeColor = RedBright;
                    updateButton.Enabled = true;
                    if (automatic)
                    {
                        var answer = MessageBox.Show(this, string.Format(Strings.T("update_ask"), tag, "v" + AppVersion),
                            Strings.T("update_ask_title"), MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                        ShowPage(pages.Count - 1);
                        if (answer == DialogResult.Yes) InstallUpdate();
                    }
                }
                else updateStatus.Text = newer ? Strings.T("update_no_zip") : Strings.T("update_current");
            };
            try { web.DownloadStringAsync(new Uri(LatestReleaseApi)); }
            catch (Exception) { updateStatus.Text = Strings.T("update_failed"); }
        }

        // Downloads the release zip, unpacks it in the temp folder, and leaves a
        // script that waits for the launcher to close, copies the new files over
        // (never the player's settings) and opens the new launcher.
        void InstallUpdate()
        {
            if (updateUrl == null) return;
            string work = Path.Combine(Path.GetTempPath(), "erased-reboot-update");
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (Exception) { }
            Directory.CreateDirectory(work);
            string zip = Path.Combine(work, "update.zip");
            updateButton.Enabled = false;
            updateProgress.Visible = true;
            updateProgress.Value = 0;
            updateStatus.Text = Strings.T("update_downloading");
            var web = NewWebClient();
            web.DownloadProgressChanged += delegate(object s, System.Net.DownloadProgressChangedEventArgs e)
            {
                if (!IsDisposed) updateProgress.Value = Math.Max(0, Math.Min(100, e.ProgressPercentage));
            };
            web.DownloadFileCompleted += delegate(object s, System.ComponentModel.AsyncCompletedEventArgs e)
            {
                web.Dispose();
                if (IsDisposed) return;
                if (e.Error != null)
                {
                    updateStatus.Text = Strings.T("update_failed");
                    updateButton.Enabled = true;
                    return;
                }
                try
                {
                    string files = Path.Combine(work, "files");
                    ZipFile.ExtractToDirectory(zip, files);
                    // A zip with everything inside one folder.
                    if (Directory.GetFiles(files, "*.exe").Length == 0 && Directory.GetDirectories(files).Length == 1
                        && !Directory.Exists(Path.Combine(files, "game")))
                        files = Directory.GetDirectories(files)[0];
                    string script = Path.Combine(work, "update.ps1");
                    File.WriteAllText(script, UpdateScript(files), new UTF8Encoding(true));
                    Process.Start(new ProcessStartInfo("powershell.exe",
                        "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script + "\"")
                    {
                        UseShellExecute = false, CreateNoWindow = true
                    });
                    updateStatus.Text = Strings.T("update_restarting");
                    Close();
                }
                catch (Exception ex)
                {
                    updateStatus.Text = Strings.T("update_failed") + " " + ex.Message;
                    updateButton.Enabled = true;
                }
            };
            web.DownloadFileAsync(new Uri(updateUrl), zip);
        }

        string UpdateScript(string files)
        {
            Func<string, string> q = delegate(string text) { return "'" + text.Replace("'", "''") + "'"; };
            var sb = new StringBuilder();
            sb.AppendLine("$src = " + q(files));
            sb.AppendLine("$root = " + q(root.TrimEnd('\\')));
            sb.AppendLine("$gameDir = " + q(gameDir.TrimEnd('\\')));
            sb.AppendLine("$exe = " + q(Application.ExecutablePath));
            sb.AppendLine("Wait-Process -Id " + Process.GetCurrentProcess().Id + " -ErrorAction SilentlyContinue");
            sb.AppendLine("Start-Sleep -Seconds 1");
            sb.AppendLine("$keep = 're_wor.toml', 'gh5.toml', 'gh5_options.toml', 'game_folder.txt', 'launcher_language.txt', 'launcher_window.txt', 'launcher_game.txt', 'profile_picture.png', 'profile_picture.bin'");
            sb.AppendLine("$gameSrc = Join-Path $src 'game'");
            sb.AppendLine("if (Test-Path -LiteralPath $gameSrc) {");
            sb.AppendLine("  Get-ChildItem -LiteralPath $gameSrc -Recurse -File | Where-Object { $keep -notcontains $_.Name } | ForEach-Object {");
            sb.AppendLine("    $dst = Join-Path $gameDir $_.FullName.Substring($gameSrc.Length + 1)");
            sb.AppendLine("    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dst) | Out-Null");
            sb.AppendLine("    Copy-Item -LiteralPath $_.FullName -Destination $dst -Force");
            sb.AppendLine("  }");
            sb.AppendLine("}");
            // The other folders of the zip (gh5, the Guitar Hero 5 build) go next to the launcher.
            sb.AppendLine("Get-ChildItem -LiteralPath $src -Directory | Where-Object { $_.Name -ne 'game' } | ForEach-Object {");
            sb.AppendLine("  $dirSrc = $_.FullName; $dirDst = Join-Path $root $_.Name");
            sb.AppendLine("  Get-ChildItem -LiteralPath $dirSrc -Recurse -File | Where-Object { $keep -notcontains $_.Name } | ForEach-Object {");
            sb.AppendLine("    $dst = Join-Path $dirDst $_.FullName.Substring($dirSrc.Length + 1)");
            sb.AppendLine("    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dst) | Out-Null");
            sb.AppendLine("    Copy-Item -LiteralPath $_.FullName -Destination $dst -Force");
            sb.AppendLine("  }");
            sb.AppendLine("}");
            sb.AppendLine("Get-ChildItem -LiteralPath $src -File | Where-Object { $keep -notcontains $_.Name } | ForEach-Object {");
            sb.AppendLine("  if ($_.Extension -eq '.exe') { Copy-Item -LiteralPath $_.FullName -Destination $exe -Force }");
            sb.AppendLine("  else { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $root $_.Name) -Force }");
            sb.AppendLine("}");
            sb.AppendLine("Start-Process -FilePath $exe");
            return sb.ToString();
        }

        // The picture is kept next to the game twice: as a PNG for the launcher,
        // and as 64 x 64 raw pixels (alpha, red, green, blue) for the game, which
        // gives them to the profile's gamer picture (src/profile_picture.cpp).
        void ShowProfilePicture()
        {
            foreach (PictureBox box in new[] { profilePicture, homePicture })
            {
                if (box == null) continue;
                Image old = box.Image;
                box.Image = null;
                if (old != null) old.Dispose();
            }
            string path = Path.Combine(PlayDir, PictureFile);
            if (!File.Exists(path)) return;
            try
            {
                // From memory, so the file is not kept open.
                using (var stream = new MemoryStream(File.ReadAllBytes(path)))
                using (var loaded = Image.FromStream(stream))
                {
                    foreach (PictureBox box in new[] { profilePicture, homePicture })
                        if (box != null) box.Image = new Bitmap(loaded);
                }
            }
            catch (Exception) { }
        }

        void ChooseProfilePicture()
        {
            using (var dialog = new OpenFileDialog { Filter = Strings.T("picture_filter") })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    using (var stream = new MemoryStream(File.ReadAllBytes(dialog.FileName)))
                    using (var source = Image.FromStream(stream))
                    using (var square = new Bitmap(PictureSide, PictureSide, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                    {
                        // The middle square of the image, scaled to the picture's size.
                        int side = Math.Min(source.Width, source.Height);
                        var from = new Rectangle((source.Width - side) / 2, (source.Height - side) / 2, side, side);
                        using (var g = Graphics.FromImage(square))
                        {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                            g.Clear(Color.Black);
                            g.DrawImage(source, new Rectangle(0, 0, PictureSide, PictureSide), from, GraphicsUnit.Pixel);
                        }
                        var data = new byte[PictureSide * PictureSide * 4];
                        for (int y = 0; y < PictureSide; y++)
                        {
                            for (int x = 0; x < PictureSide; x++)
                            {
                                Color c = square.GetPixel(x, y);
                                int i = (y * PictureSide + x) * 4;
                                data[i] = 255;
                                data[i + 1] = c.R;
                                data[i + 2] = c.G;
                                data[i + 3] = c.B;
                            }
                        }
                        square.Save(Path.Combine(PlayDir, PictureFile), System.Drawing.Imaging.ImageFormat.Png);
                        File.WriteAllBytes(Path.Combine(PlayDir, PictureData), data);
                    }
                    ShowProfilePicture();
                }
                catch (Exception)
                {
                    MessageBox.Show(this, Strings.T("picture_failed"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }

        void RemoveProfilePicture()
        {
            try
            {
                File.Delete(Path.Combine(PlayDir, PictureFile));
                File.Delete(Path.Combine(PlayDir, PictureData));
            }
            catch (Exception) { }
            ShowProfilePicture();
        }

        // The title in the title font: the first word white, the rest red, over a
        // red glow. "size" is the first word's size in points; alpha fades it in.
        void DrawLogo(Graphics g, Rectangle area, float size, int alpha)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using (var glow = new System.Drawing.Drawing2D.LinearGradientBrush(
                area, Color.FromArgb(alpha * 110 / 255, Glow), Color.FromArgb(0, 0, 0, 0), 90f))
                g.FillRectangle(glow, area);
            using (var big = new Font(titleFamily, size, FontStyle.Regular, GraphicsUnit.Point))
            using (var small = new Font(titleFamily, size * 0.45f, FontStyle.Regular, GraphicsUnit.Point))
            using (var shadow = new SolidBrush(Color.FromArgb(alpha * 170 / 255, Shade)))
            using (var white = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255)))
            using (var red = new SolidBrush(Color.FromArgb(alpha, Red)))
            {
                SizeF first = g.MeasureString(Title1, big);
                // The title font's space is very narrow: the words go one by one.
                float gap = size * 0.28f, secondWidth = -gap;
                string[] words = Title2.Split(' ');
                foreach (string word in words) secondWidth += g.MeasureString(word, small).Width + gap;
                float secondHeight = g.MeasureString(Title2, small).Height;
                float height = first.Height * 0.74f + secondHeight;
                float top = area.Top + (area.Height - height) / 2f;
                float x = area.Left + (area.Width - first.Width) / 2f;
                g.DrawString(Title1, big, shadow, x + 3, top + 3);
                g.DrawString(Title1, big, white, x, top);
                x = area.Left + (area.Width - secondWidth) / 2f;
                float y = top + first.Height * 0.74f;
                foreach (string word in words)
                {
                    g.DrawString(word, small, shadow, x + 2, y + 2);
                    g.DrawString(word, small, red, x, y);
                    x += g.MeasureString(word, small).Width + gap;
                }
            }
        }

        // The first page: the title, the profile and a big PLAY button.
        Control BuildHomeTab()
        {
            var page = NewPage();
            var hero = new Canvas { Location = new Point(0, 2), Size = new Size(PageW, 128), BackColor = Color.Black };
            hero.Paint += delegate(object s, PaintEventArgs e)
            {
                DrawLogo(e.Graphics, hero.ClientRectangle, 40f, 255);
                // Thin diagonal stripes on the sides and a red edge under the cover.
                using (var stripe = new Pen(Color.FromArgb(40, Red), 2f))
                    for (int x = -hero.Height; x < hero.Width; x += 14)
                    {
                        if (x > 150 && x < hero.Width - 150 - hero.Height) continue;
                        e.Graphics.DrawLine(stripe, x, hero.Height, x + hero.Height, 0);
                    }
                using (var edge = new SolidBrush(Red))
                    e.Graphics.FillRectangle(edge, 0, hero.Height - 3, hero.Width, 3);
            };
            page.Controls.Add(hero);

            homePicture = new PictureBox
            {
                Location = new Point(24, 148), Size = new Size(96, 96), BackColor = Field,
                BorderStyle = BorderStyle.FixedSingle, SizeMode = PictureBoxSizeMode.Zoom, Cursor = Cursors.Hand
            };
            homePicture.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right) RemoveProfilePicture();
                else ChooseProfilePicture();
            };
            new ToolTip().SetToolTip(homePicture, Strings.T("picture_hint"));
            page.Controls.Add(homePicture);

            homeName = new Label
            {
                Location = new Point(134, 150), Size = new Size(PageW - 134 - 300, 40), ForeColor = Color.White,
                Font = new Font("Segoe UI", 18f, FontStyle.Bold), AutoEllipsis = true
            };
            page.Controls.Add(homeName);
            homeFolder = new Label
            {
                Location = new Point(136, 194), Size = new Size(PageW - 136 - 300, 50), ForeColor = Text2,
                Font = new Font("Segoe UI", 8.5f), AutoEllipsis = true
            };
            page.Controls.Add(homeFolder);

            var play = MakeButton(Strings.T("play"), new Point(PageW - 24 - 250, 150), true);
            play.Size = new Size(250, 76);
            play.Font = new Font("Segoe UI", 24f, FontStyle.Bold);
            play.Click += delegate { if (Apply()) Launch(); };
            page.Controls.Add(play);
            page.Controls.Add(new Label
            {
                Text = string.Format(Strings.T("home_version"), AppVersion), Location = new Point(PageW - 24 - 250, 228),
                Size = new Size(250, 20), ForeColor = Text2, TextAlign = ContentAlignment.MiddleRight, Font = new Font("Segoe UI", 8f)
            });
            var support = new LinkLabel
            {
                Text = Strings.T("home_support") + " Ko-fi · LivePix", Location = new Point(PageW - 24 - 250, 228),
                Size = new Size(150, 20), TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 8f),
                ForeColor = Text2, LinkColor = RedBright, ActiveLinkColor = Color.White, VisitedLinkColor = RedBright,
                LinkBehavior = LinkBehavior.HoverUnderline, BackColor = Color.Transparent
            };
            support.Links.Clear();
            int prefix = Strings.T("home_support").Length + 1;
            support.Links.Add(prefix, 5, "https://ko-fi.com/erasedsin");
            support.Links.Add(prefix + 8, 7, "https://livepix.gg/erasedsin");
            support.LinkClicked += delegate(object s, LinkLabelLinkClickedEventArgs e)
            {
                try { Process.Start(new ProcessStartInfo((string)e.Link.LinkData) { UseShellExecute = true }); }
                catch (Exception) { }
            };
            page.Controls.Add(support);
            support.BringToFront();

            // Three cards with how the game is set up; a click opens the page.
            int cardW = (PageW - 48 - 2 * 12) / 3;
            homeCards = new Label[3];
            string[] cardTitles = { Strings.T("tab_players"), Strings.T("tab_video"), OnlineTab ? Strings.T("tab_online") : Strings.T("tab_game") };
            int[] cardPages = { PlayersPage, VideoPage, OnlineTab ? OnlinePage : GamePage };
            for (int i = 0; i < 3; i++)
            {
                int target = cardPages[i];
                var card = new Canvas
                {
                    Location = new Point(24 + i * (cardW + 12), 262), Size = new Size(cardW, 70),
                    BackColor = Field, Cursor = Cursors.Hand
                };
                card.Paint += delegate(object s, PaintEventArgs e)
                {
                    using (var bar = new SolidBrush(Red)) e.Graphics.FillRectangle(bar, 0, 0, 3, card.Height);
                };
                var title = new Label
                {
                    Text = cardTitles[i], Location = new Point(14, 10), Size = new Size(cardW - 20, 18),
                    ForeColor = RedBright, Font = new Font("Segoe UI", 8f, FontStyle.Bold), Cursor = Cursors.Hand
                };
                var value = new Label
                {
                    Location = new Point(14, 28), Size = new Size(cardW - 20, 38), ForeColor = Text1,
                    Font = new Font("Segoe UI", 9.5f), AutoEllipsis = true, Cursor = Cursors.Hand
                };
                EventHandler open = delegate { ShowPage(target); };
                card.Click += open;
                title.Click += open;
                value.Click += open;
                card.Controls.Add(title);
                card.Controls.Add(value);
                homeCards[i] = value;
                page.Controls.Add(card);
            }

            return page;
        }

        void ShowHome()
        {
            if (homeName == null) return;
            string name = profileName != null ? profileName.Text.Trim() : "";
            homeName.Text = name == "" ? Strings.T("home_no_name") : name;
            bool found = IsGameFolder(gameFolder);
            homeFolder.ForeColor = found ? Text2 : RedBright;
            homeFolder.Text = found ? Strings.T("home_folder_ok") + "\n" + gameFolder : Strings.T("home_folder_missing");
            if (homeCards == null) return;

            var players = new List<string>();
            for (int i = 0; i < playerDevices.Length; i++)
            {
                ComboBox box = playerDevices[i];
                if (box == null || box.Text == "" || box.Text == Strings.T("dev_none")) continue;
                players.Add("P" + (i + 1) + " " + box.Text.Split('—')[0].Trim());
            }
            homeCards[0].Text = players.Count == 0 ? Strings.T("dev_none") : string.Join("\n", players.ToArray());

            string video = resolution != null ? resolution.Text.Split('(')[0].Trim() : "";
            if (fullscreen != null) video += "\n" + Strings.T(fullscreen.Checked ? "home_fullscreen" : "home_window");
            homeCards[1].Text = video;

            if (OnlineTab && onlineEnabled != null)
                homeCards[2].Text = Strings.T(onlineEnabled.Checked ? "home_online_on" : "home_online_off");
            else
                homeCards[2].Text = (language != null ? language.Text : "") +
                    (skipIntro != null && skipIntro.Checked ? "\n" + Strings.T("home_skip_intro") : "");
        }

        // A short opening: the title fades in over the whole window, a red line
        // grows under it, and it fades out. A click skips it. Once per run.
        void PlayIntro()
        {
            if (introShown) return;
            introShown = true;
            var intro = new Canvas { Dock = DockStyle.Fill, BackColor = Color.Black };
            var timer = new Timer { Interval = 16 };
            DateTime started = DateTime.MinValue;
            bool ended = false;
            Action end = delegate
            {
                if (ended) return;
                ended = true;
                timer.Stop();
                timer.Dispose();
                Controls.Remove(intro);
                intro.Dispose();
            };
            intro.Paint += delegate(object s, PaintEventArgs e)
            {
                double t = started == DateTime.MinValue ? 0 : (DateTime.UtcNow - started).TotalMilliseconds / IntroMs;
                t = Math.Max(0, Math.Min(1, t));
                double fadeIn = Math.Min(1, t / 0.3);
                double fadeOut = t > 0.8 ? 1 - (t - 0.8) / 0.2 : 1;
                int alpha = (int)(255 * fadeIn * fadeOut);
                double ease = 1 - Math.Pow(1 - Math.Min(1, t / 0.6), 3);
                var g = e.Graphics;
                DrawLogo(g, intro.ClientRectangle, (float)(58 + 8 * ease), alpha);
                int half = (int)(260 * ease);
                int cx = intro.Width / 2, y = intro.Height / 2 + 96;
                using (var line = new SolidBrush(Color.FromArgb(alpha, RedBright)))
                    g.FillRectangle(line, cx - half, y, half * 2, 3);
            };
            timer.Tick += delegate
            {
                if ((DateTime.UtcNow - started).TotalMilliseconds >= IntroMs) end();
                else intro.Invalidate();
            };
            intro.MouseDown += delegate { end(); };
            Shown += delegate
            {
                if (ended) return;
                started = DateTime.UtcNow;
                timer.Start();
            };
            Controls.Add(intro);
            intro.BringToFront();
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

        Control BuildGraphicsTab()
        {
            var page = NewPage();
            int y = 16;

            motionBlur = AddCheck(page, Strings.T(activeGame == GameGh5 ? "gh5_vignette" : "motion_blur"), y); y += 28;
            screenNoise = AddCheck(page, Strings.T(activeGame == GameGh5 ? "gh5_grain" : "screen_noise"), y); y += 28;
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

            // The SDK's gpu: Direct3D 12 (the mature one) or Vulkan. A backend
            // that does not start falls back to the other one.
            AddLabel(page, Strings.T("gpu_label"), y);
            gpuApi = AddCombo(page, y + 20,
                new Choice(Strings.T("gpu_any"), "any"),
                new Choice("DirectX 12", "d3d12"),
                new Choice("Vulkan", "vulkan"));
            y += 60;

            gamma16 = AddCheck(page, Strings.T("gamma16"), y); y += 36;

            AddNote(page, Strings.T(activeGame == GameGh5 ? "gh5_gfx_note" : "gfx_note"), y);
            return page;
        }

        // Online play over a LAN or a VPN (src/online.cpp in the game).
        Control BuildOnlineTab()
        {
            var page = NewPage();
            int y = 16;
            onlineEnabled = AddCheck(page, Strings.T("online_enabled"), y); y += 40;

            AddLabel(page, Strings.T("online_my_ip"), y);
            var choices = new List<Choice> { new Choice(Strings.T("online_auto"), "") };
            foreach (var address in LocalAddresses())
                choices.Add(new Choice(address[0] + "  (" + address[1] + ")", address[0]));
            onlineIp = AddCombo(page, y + 20, choices.ToArray());
            onlineIp.SelectedIndexChanged += delegate { ShowOnlineShare(); };
            y += 56;

            onlineShare = new Label { Location = new Point(18, y + 6), Size = new Size(PageW - 36 - 110, 22), ForeColor = Text1 };
            page.Controls.Add(onlineShare);
            var copy = MakeButton(Strings.T("online_copy"), new Point(PageW - 18 - 100, y), false);
            copy.Size = new Size(100, 32);
            copy.Click += delegate
            {
                string address = OnlineAddress();
                if (address != "") Clipboard.SetText(address);
            };
            page.Controls.Add(copy);
            y += 48;

            AddLabel(page, Strings.T("online_host"), y);
            onlineHost = new TextBox
            {
                Location = new Point(18, y + 20), Size = new Size(PageW - 36, 24), BorderStyle = BorderStyle.FixedSingle,
                BackColor = Field, ForeColor = Text1, MaxLength = 15
            };
            page.Controls.Add(onlineHost);
            y += 64;

            var note = new Label { Text = Strings.T("online_note"), Location = new Point(18, y), Size = new Size(PageW - 36, 110), ForeColor = Text2 };
            page.Controls.Add(note);
            return page;
        }

        // The PC's IPv4 addresses, best first, as the game picks them: Radmin VPN
        // (26.x), Hamachi (25.x), private networks, then the rest.
        static List<string[]> LocalAddresses()
        {
            var found = new List<string[]>();
            try
            {
                foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                        byte[] b = unicast.Address.GetAddressBytes();
                        if (b[0] == 127 || b[0] == 0 || (b[0] == 169 && b[1] == 254)) continue;
                        found.Add(new[] { unicast.Address.ToString(), nic.Name, AddressRank(b).ToString() });
                    }
                }
            }
            catch (System.Net.NetworkInformation.NetworkInformationException) { }
            found.Sort((a, b) => string.CompareOrdinal(a[2], b[2]));
            return found;
        }

        static int AddressRank(byte[] b)
        {
            if (b[0] == 26) return 0;
            if (b[0] == 25) return 1;
            if (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)) return 2;
            return 3;
        }

        string OnlineAddress()
        {
            string chosen = onlineIp.SelectedItem == null ? "" : Value(onlineIp);
            if (chosen != "") return chosen;
            var all = LocalAddresses();
            return all.Count > 0 ? all[0][0] : "";
        }

        void ShowOnlineShare()
        {
            string address = OnlineAddress();
            onlineShare.Text = address == "" ? Strings.T("online_share_none") : string.Format(Strings.T("online_share"), address);
        }

        static bool IsIpv4(string text)
        {
            System.Net.IPAddress parsed;
            return System.Net.IPAddress.TryParse(text, out parsed) &&
                parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && text.Split('.').Length == 4;
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

        // A regular controller (the SDK's gamepad_as_guitar): a controller, which the game gives the vocals, or a guitar
        // with the buttons of the chosen layout (guitar_pad_*: green, red, yellow, blue, orange, whammy, Star Power).
        static readonly string[] PadKeys = { "guitar_pad_green", "guitar_pad_red", "guitar_pad_yellow", "guitar_pad_blue",
            "guitar_pad_orange", "guitar_pad_whammy", "guitar_pad_star_power" };
        static readonly string[] PadClassic = { "A", "B", "Y", "X", "LB", "RT", "BACK|RB" };
        static readonly string[] PadTriggers = { "LT", "LB", "RB", "RT", "A", "RS", "BACK|Y" };
        // A real Xbox 360 guitar that reaches the game as a regular controller (Parsec's virtual controller):
        // its own buttons, and the whammy bar and tilt axes passed as they come (the SDK's RX and RY).
        static readonly string[] PadParsec = { "A", "B", "Y", "X", "LB", "RX", "BACK" };
        ComboBox padMode, padLayout, tiltSensitivity, usbGuitar, audioBuffer;
        Label usbStatus;
        // guitar_hid_maps: "VVVV:PPPP|SDL mapping fields" per guitar, separated by ';' (the SDK's SDL driver).
        string guitarMaps = "";

        Control BuildControlsTab()
        {
            var page = NewPage();
            int half = (PageW - 48) / 2, right = 18 + half + 12;
            int y = 12;
            AddLabel(page, Strings.T("pad_mode"), y);
            padMode = AddCombo(page, y + 20,
                new Choice(Strings.T("pad_mode_mic"), "mic"),
                new Choice(Strings.T("pad_mode_guitar"), "guitar"));
            y += 58;
            AddLabel(page, Strings.T("gh5_pad_layout"), y);
            padLayout = AddCombo(page, y + 20,
                new Choice(Strings.T("gh5_layout_classic"), "classic"),
                new Choice(Strings.T("gh5_layout_triggers"), "triggers"),
                new Choice(Strings.T("layout_parsec"), "parsec"));
            padLayout.Width = half;
            padMode.SelectedIndexChanged += delegate { padLayout.Enabled = Value(padMode) == "guitar"; };
            page.Controls.Add(new Label { Text = Strings.T("tilt_label"), Location = new Point(right, y), AutoSize = true, ForeColor = Text1 });
            tiltSensitivity = AddCombo(page, y + 20,
                new Choice(Strings.T("tilt_100"), "100"),
                new Choice(Strings.T("tilt_150"), "150"),
                new Choice(Strings.T("tilt_200"), "200"),
                new Choice(Strings.T("tilt_300"), "300"));
            tiltSensitivity.Location = new Point(right, y + 20);
            tiltSensitivity.Width = half;
            y += 58;
            page.Controls.Add(new Label
            {
                Text = Strings.T("pad_mode_note"), Location = new Point(18, y), Size = new Size(PageW - 36, 50),
                ForeColor = Text2, Font = new Font("Segoe UI", 8.5f)
            });
            y += 56;
            page.Controls.Add(new Label
            {
                Text = Strings.T("usb_title"), Location = new Point(18, y), AutoSize = true,
                ForeColor = RedBright, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            });
            y += 22;
            usbGuitar = new ComboBox
            {
                Location = new Point(18, y), Size = new Size(PageW - 36 - 2 * 118, 26), DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat, BackColor = Field, ForeColor = Text1
            };
            usbGuitar.DropDown += delegate { FillUsbGuitars(); };
            usbGuitar.SelectedIndexChanged += delegate { ShowUsbStatus(); };
            page.Controls.Add(usbGuitar);
            var setup = MakeButton(Strings.T("usb_setup"), new Point(PageW - 18 - 2 * 112 - 6, y - 3), true);
            setup.Size = new Size(112, 32);
            setup.Click += delegate { SetUpUsbGuitar(); };
            page.Controls.Add(setup);
            var remove = MakeButton(Strings.T("usb_remove"), new Point(PageW - 18 - 112, y - 3), false);
            remove.Size = new Size(112, 32);
            remove.Click += delegate
            {
                string id = UsbGuitarId();
                if (id == null) return;
                guitarMaps = WithGuitarMap(guitarMaps, id, null);
                ShowUsbStatus();
            };
            page.Controls.Add(remove);
            y += 32;
            usbStatus = new Label { Location = new Point(18, y), Size = new Size(PageW - 36, 20), ForeColor = Text1 };
            page.Controls.Add(usbStatus);
            y += 22;
            page.Controls.Add(new Label
            {
                Text = Strings.T("usb_note"), Location = new Point(18, y), Size = new Size(PageW - 36, 52),
                ForeColor = Text2, Font = new Font("Segoe UI", 8.5f)
            });
            FillUsbGuitars();
            return page;
        }

        // The generic controllers (winmm, not XInput): value "VVVV:PPPP|joystick id".
        void FillUsbGuitars()
        {
            string keep = usbGuitar.SelectedItem != null ? Value(usbGuitar) : null;
            usbGuitar.Items.Clear();
            foreach (var d in InputScan.Scan())
            {
                if (!d.IsJoy) continue;
                string[] parts = d.Spec.Split(':');
                if (parts.Length < 3) continue;
                usbGuitar.Items.Add(new Choice(d.Title + "  (" + d.Detail + ")", parts[1] + ":" + parts[2] + "|" + d.JoyId));
            }
            if (usbGuitar.Items.Count == 0) usbGuitar.Items.Add(new Choice(Strings.T("usb_none"), ""));
            if (keep != null) SelectChoice(usbGuitar, keep); else usbGuitar.SelectedIndex = 0;
            ShowUsbStatus();
        }

        string UsbGuitarId()
        {
            if (usbGuitar == null || usbGuitar.SelectedItem == null || Value(usbGuitar) == "") return null;
            return Value(usbGuitar).Split('|')[0];
        }

        void ShowUsbStatus()
        {
            if (usbStatus == null) return;
            string id = UsbGuitarId();
            if (id == null) { usbStatus.Text = ""; return; }
            bool ready = GuitarMapOf(guitarMaps, id) != null;
            usbStatus.Text = Strings.T(ready ? "usb_ready" : "usb_not_ready");
            usbStatus.ForeColor = ready ? Led : Text2;
        }

        static string GuitarMapOf(string maps, string id)
        {
            foreach (string entry in (maps ?? "").Split(';'))
            {
                int bar = entry.IndexOf('|');
                if (bar > 0 && entry.Substring(0, bar).Trim().Equals(id, StringComparison.OrdinalIgnoreCase)) return entry.Substring(bar + 1);
            }
            return null;
        }

        // The list with id's entry replaced by fields (or removed, fields null).
        static string WithGuitarMap(string maps, string id, string fields)
        {
            var kept = new List<string>();
            foreach (string entry in (maps ?? "").Split(';'))
            {
                int bar = entry.IndexOf('|');
                if (bar <= 0) continue;
                if (entry.Substring(0, bar).Trim().Equals(id, StringComparison.OrdinalIgnoreCase)) continue;
                kept.Add(entry.Trim());
            }
            if (fields != null) kept.Add(id + "|" + fields);
            return string.Join(";", kept.ToArray());
        }

        void SetUpUsbGuitar()
        {
            string id = UsbGuitarId();
            if (id == null) { FillUsbGuitars(); return; }
            uint joy = uint.Parse(Value(usbGuitar).Split('|')[1]);
            using (var wizard = new GuitarSetup(joy, titleFamily))
            {
                if (wizard.ShowDialog(this) == DialogResult.OK && wizard.Fields != null)
                {
                    guitarMaps = WithGuitarMap(guitarMaps, id, wizard.Fields);
                    ShowUsbStatus();
                    status.Text = Strings.T("wiz_done");
                }
            }
        }

        ComboBox achPlayer;
        Label achSummary;
        Panel achScroll;
        Canvas achList;
        List<Achievement> achItems = new List<Achievement>();
        readonly Dictionary<string, Image> achIcons = new Dictionary<string, Image>();

        class Achievement
        {
            public uint Id, Score;
            public string Icon, Title, Text;
            public long Unlocked;
        }

        Control BuildProfileTab()
        {
            var page = NewPage();
            AddLabel(page, Strings.T("ach_player"), 12);
            achPlayer = AddCombo(page, 32);
            achPlayer.Width = 260;
            achPlayer.SelectedIndexChanged += delegate { LoadAchievements(); };
            achSummary = new Label
            {
                Location = new Point(290, 34), Size = new Size(PageW - 290 - 18, 22), ForeColor = RedBright,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleRight
            };
            page.Controls.Add(achSummary);
            achScroll = new Panel { Location = new Point(18, 70), Size = new Size(PageW - 36, 384 - 70 - 12), AutoScroll = true, BackColor = Panel };
            achList = new Canvas { Location = new Point(0, 0), Size = new Size(achScroll.Width - SystemInformation.VerticalScrollBarWidth, 10), BackColor = Panel };
            achList.Paint += PaintAchievements;
            achScroll.Controls.Add(achList);
            page.Controls.Add(achScroll);
            return page;
        }

        void ShowProfile()
        {
            if (achPlayer == null || achPlayer.IsDisposed) return;
            int keep = Math.Max(0, achPlayer.SelectedIndex);
            achPlayer.Items.Clear();
            for (int i = 0; i < 4; i++)
            {
                string name = i == 0 ? profileName.Text.Trim() : playerNames[i].Text.Trim();
                if (name == "") name = i == 0 ? Strings.T("home_no_name") : string.Format(Strings.T("player_default_name"), i + 1);
                achPlayer.Items.Add(new Choice("P" + (i + 1) + "  " + name, i.ToString()));
            }
            achPlayer.SelectedIndex = keep;
        }

        // Where the game keeps the achievements (the SDK's user data): Guitar Hero 5's userdata folder, Warriors of
        // Rock's in Documents\re_wor unless it has one next to the game.
        string AchievementsDir()
        {
            string local = Path.Combine(Path.Combine(PlayDir, "userdata"), "achievements");
            if (activeGame == GameGh5 || Directory.Exists(local)) return local;
            return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "re_wor"), "achievements");
        }

        void LoadAchievements()
        {
            achItems = new List<Achievement>();
            string dir = AchievementsDir();
            string info = null;
            try { if (Directory.Exists(dir)) foreach (string f in Directory.GetFiles(dir, "*.info.toml")) { info = f; break; } }
            catch (Exception) { }
            if (info == null)
            {
                achSummary.Text = "";
                achList.Height = achScroll.Height - 4;
                achList.Invalidate();
                return;
            }
            string title = Path.GetFileName(info).Split('.')[0];
            string[] codes = { "", "en", "ja", "de", "fr", "es", "it", "ko", "zh-Hant", "pt" };
            int lang = settings.GetInt("user_language", 1);
            string code = lang >= 1 && lang < codes.Length ? codes[lang] : "en";
            try { achItems = ReadAchievementInfo(info, code); } catch (Exception) { achItems = new List<Achievement>(); }
            int player = achPlayer.SelectedIndex < 0 ? 0 : achPlayer.SelectedIndex;
            string xuid = (0xB13EBABEBABEBABEUL + (ulong)player).ToString("X16");
            var unlocked = ReadUnlocks(Path.Combine(Path.Combine(dir, xuid), title + ".toml"));
            uint got = 0, total = 0, gotScore = 0, totalScore = 0;
            foreach (var a in achItems)
            {
                long when;
                a.Unlocked = unlocked.TryGetValue(a.Id, out when) ? when : 0;
                total++; totalScore += a.Score;
                if (a.Unlocked != 0) { got++; gotScore += a.Score; }
            }
            // Unlocked first, the newest on top; then the rest in the game's order.
            achItems.Sort((x, y) => x.Unlocked != 0 && y.Unlocked != 0 ? y.Unlocked.CompareTo(x.Unlocked) : (y.Unlocked != 0).CompareTo(x.Unlocked != 0));
            achSummary.Text = string.Format(Strings.T("ach_summary"), got, total, gotScore, totalScore);
            achList.Height = Math.Max(achScroll.Height - 4, achItems.Count * 58 + 4);
            achScroll.AutoScrollPosition = new Point(0, 0);
            achList.Invalidate();
        }

        static Dictionary<uint, long> ReadUnlocks(string path)
        {
            var result = new Dictionary<uint, long>();
            if (!File.Exists(path)) return result;
            uint current = 0;
            bool inEntry = false;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.StartsWith("[unlocked.") && line.EndsWith("]"))
                {
                    inEntry = uint.TryParse(line.Substring(10, line.Length - 11), out current);
                    if (inEntry) result[current] = 1;
                }
                else if (inEntry && line.StartsWith("filetime"))
                {
                    long t;
                    if (long.TryParse(line.Substring(line.IndexOf('=') + 1).Trim(), out t) && t > 0) result[current] = t;
                }
            }
            return result;
        }

        // The SDK's <title>.info.toml: [[achievements]] with id, gamerscore and icon, and the texts in
        // [achievements.label] and [achievements.description] by language.
        static List<Achievement> ReadAchievementInfo(string path, string code)
        {
            var list = new List<Achievement>();
            Achievement current = null;
            string table = "";
            var label = new Dictionary<string, string>();
            var text = new Dictionary<string, string>();
            Action finish = delegate
            {
                if (current == null) return;
                current.Title = Pick(label, code);
                current.Text = Pick(text, code);
                list.Add(current);
            };
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line == "[[achievements]]")
                {
                    finish();
                    current = new Achievement();
                    label = new Dictionary<string, string>();
                    text = new Dictionary<string, string>();
                    table = "";
                    continue;
                }
                if (line.StartsWith("[")) { table = line.Trim('[', ']'); continue; }
                int eq = line.IndexOf('=');
                if (current == null || eq <= 0 || line.StartsWith("#")) continue;
                string key = line.Substring(0, eq).Trim().Trim('"');
                string value = line.Substring(eq + 1).Trim();
                if (table == "")
                {
                    uint n;
                    if (key == "id" && uint.TryParse(value, out n)) current.Id = n;
                    else if (key == "gamerscore" && uint.TryParse(value, out n)) current.Score = n;
                    else if (key == "icon") current.Icon = TomlString(value);
                }
                else if (table == "achievements.label") label[key] = TomlString(value);
                else if (table == "achievements.description") text[key] = TomlString(value);
            }
            finish();
            return list;
        }

        static string Pick(Dictionary<string, string> texts, string code)
        {
            string t;
            if (texts.TryGetValue(code, out t) || texts.TryGetValue("en", out t)) return t;
            foreach (var pair in texts) return pair.Value;
            return "";
        }

        static string TomlString(string value)
        {
            if (value.Length < 2 || value[0] != '"') return value;
            var sb = new StringBuilder();
            for (int i = 1; i < value.Length - 1; i++)
            {
                char c = value[i];
                if (c != '\\' || i + 1 >= value.Length - 1) { sb.Append(c); continue; }
                char e = value[++i];
                if (e == 'n') sb.Append('\n');
                else if (e == 't') sb.Append('\t');
                else if (e == 'u' && i + 4 < value.Length) { sb.Append((char)Convert.ToInt32(value.Substring(i + 1, 4), 16)); i += 4; }
                else sb.Append(e);
            }
            return sb.ToString();
        }

        Image AchievementIcon(string relative)
        {
            if (string.IsNullOrEmpty(relative)) return null;
            string path = Path.Combine(AchievementsDir(), relative.Replace('/', '\\'));
            Image image;
            if (achIcons.TryGetValue(path, out image)) return image;
            try { image = new Bitmap(new MemoryStream(File.ReadAllBytes(path))); } catch (Exception) { image = null; }
            achIcons[path] = image;
            return image;
        }

        void PaintAchievements(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            if (achItems.Count == 0)
            {
                using (var brush = new SolidBrush(Text2))
                    g.DrawString(Strings.T("ach_missing"), Font, brush, new RectangleF(8, 16, achList.Width - 16, 60));
                return;
            }
            using (var titleFont = new Font("Segoe UI", 10f, FontStyle.Bold))
            using (var textFont = new Font("Segoe UI", 8.5f))
            using (var white = new SolidBrush(Color.White))
            using (var gray = new SolidBrush(Text2))
            using (var dim = new SolidBrush(Color.FromArgb(95, 95, 95)))
            using (var accent = new SolidBrush(RedBright))
            using (var row = new SolidBrush(Field))
            using (var veil = new SolidBrush(Color.FromArgb(170, 22, 22, 22)))
            using (var right = new StringFormat { Alignment = StringAlignment.Far })
            {
                for (int i = 0; i < achItems.Count; i++)
                {
                    var a = achItems[i];
                    int y = 2 + i * 58;
                    if (y + 58 < e.ClipRectangle.Top || y > e.ClipRectangle.Bottom) continue;
                    bool got = a.Unlocked != 0;
                    g.FillRectangle(row, 0, y, achList.Width - 4, 54);
                    if (got) g.FillRectangle(accent, 0, y, 3, 54);
                    var icon = AchievementIcon(a.Icon);
                    if (icon != null)
                    {
                        g.DrawImage(icon, new Rectangle(10, y + 5, 44, 44));
                        if (!got) g.FillRectangle(veil, 10, y + 5, 44, 44);
                    }
                    int textW = achList.Width - 64 - 90;
                    g.DrawString(a.Title, titleFont, got ? white : gray, new RectangleF(64, y + 6, textW, 20));
                    string line = a.Text;
                    if (got && a.Unlocked > 1)
                    {
                        try { line = string.Format(Strings.T("ach_unlocked"), DateTime.FromFileTime(a.Unlocked).ToString("d")) + "  ·  " + a.Text; }
                        catch (Exception) { }
                    }
                    g.DrawString(line, textFont, got ? gray : dim, new RectangleF(64, y + 26, textW, 28));
                    g.DrawString(a.Score + " G", titleFont, got ? accent : dim, new RectangleF(achList.Width - 100, y + 16, 88, 22), right);
                }
            }
        }

        // The game knows keys by name with a capital letter (M, not m; Space, not space): each key of a list, as the
        // player typed it, written that way.
        static string KeyNames(string text)
        {
            var keys = new List<string>();
            foreach (string part in text.Split(','))
            {
                string key = part.Trim();
                if (key.Length == 0) continue;
                var pieces = key.Split('+');
                for (int i = 0; i < pieces.Length; i++)
                {
                    string piece = pieces[i].Trim();
                    if (piece.Length > 0) piece = char.ToUpperInvariant(piece[0]) + piece.Substring(1);
                    pieces[i] = piece;
                }
                keys.Add(string.Join("+", pieces));
            }
            return string.Join(",", keys.ToArray());
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
            {
                double hz;
                string rate = activeGame == GameGh5 ? settings.GetInt("gh5_frame_rate", 60).ToString()
                    : settings.Get("video_mode_refresh_rate", "60");
                if (!double.TryParse(rate, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out hz)) hz = 60;
                SelectChoice(frameRate, ((int)Math.Round(hz)).ToString());
                if (frameRate.SelectedItem == null) frameRate.SelectedIndex = 0;
            }
            gamma16.Checked = settings.GetBool("gamma_render_target_as_unorm16", activeGame == GameWor);
            SelectChoice(gpuApi, settings.Get("gpu", "any"));
            if (gpuApi.SelectedItem == null) gpuApi.SelectedIndex = 0;
            motionBlur.Checked = settings.GetBool(FxKey(0), true);
            screenNoise.Checked = settings.GetBool(FxKey(1), true);
            depthOfField.Checked = settings.GetBool(FxKey(2), true);
            bloom.Checked = settings.GetBool(FxKey(3), true);
            showFps.Checked = settings.GetBool(CvarPrefix + "show_fps", true);
            debugMenu.Checked = settings.GetBool(CvarPrefix + "debug_menu", false);
            skipIntro.Checked = settings.GetBool(CvarPrefix + "skip_intro", false);
            profileName.Text = settings.Get("online_name", "");
            for (int i = 1; i < 4; i++) playerNames[i].Text = settings.Get("player_" + (i + 1) + "_name", "");
            ShowHome();
            onlineEnabled.Checked = settings.GetBool(CvarPrefix + "online", false);
            SelectChoice(onlineIp, settings.Get(CvarPrefix + "online_ip", ""));
            if (onlineIp.SelectedItem == null) onlineIp.SelectedIndex = 0;
            onlineHost.Text = settings.Get(CvarPrefix + "online_host", "");
            ShowOnlineShare();
            for (int i = 0; i < 4; i++)
            {
                string spec = settings.Get("player" + (i + 1) + "_device", "auto");
                readSdl[i] = spec.EndsWith(":sdl");
                savedDevices[i] = readSdl[i] ? spec.Substring(0, spec.Length - 4) : spec;
            }
            for (int i = 0; i < 4; i++) playerDevices[i].SelectedItem = null;
            FillPlayerDevices();
            SelectChoice(micPlayer, settings.Get(CvarPrefix + "mic_player", "0"));
            if (micPlayer.SelectedItem == null) micPlayer.SelectedIndex = 0;
            string micName = settings.Get(CvarPrefix + "mic_device", "");
            SelectChoice(micDevice, micName);
            if (micDevice.SelectedItem == null)
            {
                if (micName != "") micDevice.Items.Add(new Choice(micName, micName));
                SelectChoice(micDevice, micName);
                if (micDevice.SelectedItem == null) micDevice.SelectedIndex = 0;
            }
            micVolume.Value = Math.Max(0, Math.Min(400, settings.GetInt(CvarPrefix + "mic_volume", 100)));
            micGate.Value = Math.Max(0, Math.Min(50, settings.GetInt(CvarPrefix + "mic_gate", 0)));
            kbEnabled.Checked = settings.GetBool("mnk_mode", true);
            applyingKbPreset = true;
            foreach (var part in KeyParts) kbFields[part[0]].Text = settings.Get(part[0], part[2]);
            applyingKbPreset = false;
            SelectChoice(kbPreset, MatchKbPreset());
            SelectChoice(padMode, settings.GetBool("gamepad_as_guitar", false) ? "guitar" : "mic");
            SelectChoice(padLayout, settings.Get("guitar_pad_green", "A") == "LT" ? "triggers"
                : settings.Get("guitar_pad_whammy", "") == "RX" ? "parsec" : "classic");
            padLayout.Enabled = Value(padMode) == "guitar";
            SelectChoice(tiltSensitivity, settings.GetInt("guitar_tilt_sensitivity", 100).ToString());
            SelectChoice(audioBuffer, settings.GetInt("audio_maxqframes", 64).ToString());
            guitarMaps = settings.Get("guitar_hid_maps", "");
            ShowUsbStatus();
            ShowGameFolder();
        }

        // The four screen effects of the GRAPHICS page: Warriors of Rock's motion blur, screen noise, depth of field
        // and bloom (patched into its scripts), Guitar Hero 5's vignette, grain, depth of field and bloom.
        static string FxKey(int index)
        {
            if (activeGame == GameGh5)
                return new[] { "gh5_fx_vignette", "gh5_fx_grain", "gh5_fx_depth_of_field", "gh5_fx_bloom" }[index];
            return CvarPrefix + new[] { "motion_blur", "screen_noise", "dof", "bloom" }[index];
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
            // Guitar Hero 5 fills a screen wider than 16:9 (src/widescreen.cpp).
            if (activeGame == GameGh5) settings.Set("gh5_ultrawide", int.Parse(res[0]) * 9 > int.Parse(res[1]) * 17);
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
            settings.SetNumber("video_mode_refresh_rate", Value(frameRate) + ".0");
            // Guitar Hero 5 keeps the game in time above 60 FPS from its own setting (src/framerate.cpp).
            if (activeGame == GameGh5) settings.Set("gh5_frame_rate", int.Parse(Value(frameRate)));
            settings.Set("gamma_render_target_as_unorm16", gamma16.Checked);
            settings.Set("gpu", Value(gpuApi));
            settings.Set(FxKey(0), motionBlur.Checked);
            settings.Set(FxKey(1), screenNoise.Checked);
            settings.Set(FxKey(2), depthOfField.Checked);
            settings.Set(FxKey(3), bloom.Checked);
            settings.Set(CvarPrefix + "show_fps", showFps.Checked);
            settings.Set(CvarPrefix + "debug_menu", debugMenu.Checked);
            settings.Set(CvarPrefix + "skip_intro", skipIntro.Checked);
            // A regular controller: a controller (the singer's; the game's own debug menu,
            // Enable Controller, lets it play) or a guitar with the chosen buttons.
            bool padGuitar = Value(padMode) == "guitar";
            settings.Set("gamepad_as_guitar", padGuitar);
            settings.Remove("guitar_autostrum");
            string[] layout = Value(padLayout) == "triggers" ? PadTriggers : Value(padLayout) == "parsec" ? PadParsec : PadClassic;
            for (int i = 0; i < PadKeys.Length; i++)
            {
                if (padGuitar) settings.Set(PadKeys[i], layout[i]);
                else settings.Remove(PadKeys[i]);
            }
            if (padGuitar && Value(padLayout) == "parsec") settings.Set("guitar_pad_tilt", "RY");
            else settings.Remove("guitar_pad_tilt");
            if (activeGame == GameGh5)
            {
                // Settings of an older Guitar Hero 5 build's SDK, which this one does not have.
                foreach (string old in new[] { "native_x_renderer", "native_x_renderer_present_mode", "vblank_follows_display",
                    "native_x_renderer_antialiasing", "native_x_renderer_resolution_scale", "native_x_renderer_scaling",
                    "native_x_renderer_shadow_scale", "audio_wasapi_exclusive", "gamepad_tap_target", "gamepad_remap",
                    "mnk_tap_target", "usb_microphone_1", "usb_microphone_1_gain", "gamepad_device_subtypes", "game_data_root" })
                    settings.Remove(old);
            }
            string name = profileName.Text.Trim().Replace("\"", "").Replace("\\", "");
            if (name == "") settings.Remove("online_name");
            else settings.Set("online_name", name);
            // A profile for each player 2 to 4 with a name, or with a controller of
            // its own chosen (then named "Player N"): the game gives it its saves.
            settings.Set("player_profiles", true);
            for (int i = 1; i < 4; i++)
            {
                string key = "player_" + (i + 1) + "_name";
                string playerName = playerNames[i].Text.Trim().Replace("\"", "").Replace("\\", "");
                string device = Value(playerDevices[i]);
                if (playerName == "" && device != "auto" && device != "none")
                    playerName = string.Format(Strings.T("player_default_name"), i + 1);
                if (playerName == "") settings.Remove(key);
                else settings.Set(key, playerName);
            }
            settings.Set(CvarPrefix + "online", OnlineTab && onlineEnabled.Checked);
            settings.Set(CvarPrefix + "online_ip", Value(onlineIp));
            settings.Set(CvarPrefix + "online_host", onlineHost.Text.Trim());
            for (int i = 0; i < 4; i++)
            {
                string key = "player" + (i + 1) + "_device";
                if (Value(playerDevices[i]) == "auto") settings.Remove(key);
                else settings.Set(key, SavedSpec(i));
            }
            // Players on Automatic get XInput for Xbox controllers; the other
            // controllers are read through SDL. A controller as a guitar is SDL's.
            settings.Set("input_backend", padGuitar ? "sdl" : "xinput");
            if (Value(tiltSensitivity) == "100") settings.Remove("guitar_tilt_sensitivity");
            else settings.Set("guitar_tilt_sensitivity", int.Parse(Value(tiltSensitivity)));
            if (guitarMaps == "") settings.Remove("guitar_hid_maps");
            else settings.Set("guitar_hid_maps", guitarMaps);
            settings.Set("audio_maxqframes", int.Parse(Value(audioBuffer)));
            settings.Set(CvarPrefix + "mic_player", int.Parse(Value(micPlayer)));
            if (Value(micDevice) == "") settings.Remove(CvarPrefix + "mic_device");
            else settings.Set(CvarPrefix + "mic_device", Value(micDevice));
            settings.Set(CvarPrefix + "mic_volume", micVolume.Value);
            settings.Set(CvarPrefix + "mic_gate", micGate.Value);
            settings.Set("mnk_mode", kbEnabled.Checked);
            foreach (var part in KeyParts) settings.Set(part[0], KeyNames(kbFields[part[0]].Text));
            foreach (var clear in KeyClears) settings.Set(clear[0], clear[1]);
            // Esc goes back in the game's menus (keybind_menu_back); a guitar that sends it (Start on a
            // keyboard-mode guitar) needs it for its own button, so the menus go back with Backspace.
            bool escUsed = false;
            foreach (var part in KeyParts)
                foreach (string key in settings.Get(part[0], "").Split(','))
                    if (key.Trim().Equals("Escape", StringComparison.OrdinalIgnoreCase) || key.Trim().Equals("Esc", StringComparison.OrdinalIgnoreCase))
                        escUsed = true;
            if (escUsed) settings.Set("keybind_menu_back", "Backspace");
            else settings.Remove("keybind_menu_back");

            // Defaults that help weak PCs, written once so the player can still
            // change them later in the F4 menu. A bigger audio buffer stops the
            // crackling when the frame rate drops, and with few CPU cores the SDK
            // logs a warning about a thousand times a second at the default level.
            if (settings.Get("audio_maxqframes", "") == "") settings.Set("audio_maxqframes", 64);
            if (settings.Get("log_level", "") == "") settings.Set("log_level", "error");
            // The strongest graphics card, not the integrated one on a laptop with two.
            if (settings.Get("d3d12_adapter", "") == "") settings.Set("d3d12_adapter", -1);
            // Less CPU: a thread watches a held lock for 20 microseconds before it
            // sleeps, instead of sleeping and waking at once; and the audio worker
            // in Windows' Pro Audio class, so a busy CPU holds the sound up less.
            if (settings.Get("critical_section_spin_us", "") == "") settings.Set("critical_section_spin_us", 20);
            if (settings.Get("audio_thread_priority", "") == "") settings.Set("audio_thread_priority", true);
        }

        bool Apply()
        {
            if (activeGame == GameGh5 && !File.Exists(Path.Combine(Gh5Dir, Gh5Exe)))
            {
                // The latest release has it: the update check left its zip in updateUrl.
                if (updateUrl != null)
                {
                    if (MessageBox.Show(this, Strings.T("gh5_missing_download"), Text, MessageBoxButtons.YesNo,
                            MessageBoxIcon.Question) == DialogResult.Yes)
                    {
                        ShowPage(pages.Count - 1);
                        InstallUpdate();
                    }
                }
                else MessageBox.Show(this, Strings.T("gh5_missing"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            if (!IsGameFolder(gameFolder))
            {
                MessageBox.Show(this, Strings.T("need_folder"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            string host = onlineHost.Text.Trim();
            if (OnlineTab && host != "" && !IsIpv4(host))
            {
                ShowPage(Array.IndexOf(pageNames, Strings.T("tab_online")));
                MessageBox.Show(this, Strings.T("online_bad_ip"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            try
            {
                CollectSettings();
                settings.Save();
                if (activeGame == GameWor) WriteScriptMod();
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
            string exe = Path.Combine(PlayDir, GameExe);
            if (!File.Exists(exe))
            {
                MessageBox.Show(this, string.Format(Strings.T("missing_exe"), GameExe), Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            var start = new ProcessStartInfo(exe)
            {
                WorkingDirectory = PlayDir,
                UseShellExecute = false
            };
            // Read by the game at startup. A file keeps any character in the path
            // intact, which a command line argument would not.
            File.WriteAllText(Path.Combine(PlayDir, "game_folder.txt"), gameFolder, new UTF8Encoding(false));
            Process.Start(start);
            Close();
        }
    }

    // Teaches the launcher a generic USB guitar's buttons, one part at a time, the way Clone Hero sets one up: the
    // result is an SDL mapping of the guitar onto an Xbox 360 guitar (the SDK's guitar_hid_maps).
    sealed class GuitarSetup : Form
    {
        // SDL target, label key, kind: 0 a button (or a hat or an axis pushed), 1 an axis (whammy), 2 an axis or a
        // button (tilt). The last two can be skipped.
        static readonly string[][] Steps =
        {
            new[] { "a", "fret_green", "0" }, new[] { "b", "fret_red", "0" }, new[] { "y", "fret_yellow", "0" },
            new[] { "x", "fret_blue", "0" }, new[] { "leftshoulder", "fret_orange", "0" },
            new[] { "dpup", "strum_up", "0" }, new[] { "dpdown", "strum_down", "0" },
            new[] { "start", "kb_start", "0" }, new[] { "back", "star_power", "0" },
            new[] { "rightx", "whammy", "1" }, new[] { "righty", "tilt", "2" },
        };

        public string Fields;
        readonly uint joy;
        readonly Timer timer = new Timer { Interval = 25 };
        readonly Label stepLabel, prompt;
        readonly Button skip;
        readonly List<string> fields = new List<string>();
        InputScan.JoyReading rest;
        int step = -1;
        bool waitRelease;
        DateTime restSince;

        public GuitarSetup(uint joy, FontFamily titleFamily)
        {
            this.joy = joy;
            Text = Strings.T("wiz_title");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(480, 230);
            BackColor = Color.FromArgb(18, 18, 18);
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 10f);
            stepLabel = new Label { Location = new Point(24, 18), Size = new Size(432, 22), ForeColor = Color.FromArgb(150, 150, 150) };
            prompt = new Label { Location = new Point(24, 48), Size = new Size(432, 110), Font = new Font("Segoe UI", 16f, FontStyle.Bold) };
            skip = new Button { Text = Strings.T("wiz_skip"), Location = new Point(232, 176), Size = new Size(110, 34), FlatStyle = FlatStyle.Flat, Visible = false };
            var cancel = new Button { Text = Strings.T("wiz_cancel"), Location = new Point(352, 176), Size = new Size(110, 34), FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.Cancel };
            skip.Click += delegate { Next(); };
            Controls.AddRange(new Control[] { stepLabel, prompt, skip, cancel });
            CancelButton = cancel;
            timer.Tick += delegate { Poll(); };
            Shown += delegate { restSince = DateTime.Now; ShowStep(); timer.Start(); };
            FormClosed += delegate { timer.Stop(); timer.Dispose(); };
        }

        void ShowStep()
        {
            stepLabel.Text = string.Format(Strings.T("wiz_step"), Math.Max(1, step + 1), Steps.Length);
            if (step < 0) { prompt.Text = Strings.T("wiz_rest"); skip.Visible = false; return; }
            if (waitRelease) { prompt.Text = Strings.T("wiz_release"); return; }
            string kind = Steps[step][2];
            prompt.Text = kind == "1" ? Strings.T("wiz_whammy") : kind == "2" ? Strings.T("wiz_tilt")
                : string.Format(Strings.T("wiz_press"), Strings.T(Steps[step][1]));
            skip.Visible = kind != "0";
        }

        void Next()
        {
            step++;
            waitRelease = false;
            if (step >= Steps.Length)
            {
                timer.Stop();
                Fields = string.Join(",", fields.ToArray()) + ",";
                DialogResult = DialogResult.OK;
                Close();
                return;
            }
            ShowStep();
        }

        static int PovMask(int pov)
        {
            if (pov < 0) return 0;
            int[] masks = { 1, 3, 2, 6, 4, 12, 8, 9 };
            return masks[((pov + 2250) / 4500) % 8];
        }

        bool AtRest(InputScan.JoyReading r)
        {
            if ((r.Buttons & ~rest.Buttons) != 0 || PovMask(r.Pov) != PovMask(rest.Pov)) return false;
            for (int i = 0; i < 6; i++) if (r.Has[i] && Math.Abs(r.Axes[i] - rest.Axes[i]) > 0.3f) return false;
            return true;
        }

        void Poll()
        {
            var r = InputScan.ReadJoy(joy);
            if (r == null) return;
            if (step < 0)
            {
                // A second with nothing moving is the guitar at rest.
                if (rest == null || !AtRest(r)) { rest = r; restSince = DateTime.Now; return; }
                if ((DateTime.Now - restSince).TotalMilliseconds > 900) Next();
                return;
            }
            if (waitRelease)
            {
                if (AtRest(r)) Next();
                return;
            }
            string target = Steps[step][0], kind = Steps[step][2];
            string source = null;
            uint pressed = r.Buttons & ~rest.Buttons;
            if (pressed != 0 && kind != "1")
            {
                int b = 0;
                while ((pressed & (1u << b)) == 0) b++;
                source = "b" + b;
            }
            else if (PovMask(r.Pov) != PovMask(rest.Pov) && r.Pov >= 0 && kind == "0")
            {
                int mask = PovMask(r.Pov);
                // A diagonal counts as its vertical part (strum up and down).
                if ((mask & 1) != 0) mask = 1; else if ((mask & 4) != 0) mask = 4;
                source = "h0." + mask;
            }
            else
            {
                int best = -1;
                float bestMove = 0.55f;
                for (int i = 0; i < 6; i++)
                {
                    if (!r.Has[i]) continue;
                    float move = Math.Abs(r.Axes[i] - rest.Axes[i]);
                    if (move > bestMove) { bestMove = move; best = i; }
                }
                if (best >= 0)
                {
                    int a = InputScan.SdlAxis(r, best);
                    bool up = r.Axes[best] > rest.Axes[best];
                    float start = rest.Axes[best];
                    if (kind == "0") source = (up ? "+a" : "-a") + a;
                    // An axis that rests in the middle uses its half that moved; one at an end, all of it, turned
                    // so that rest is the low end (the whammy at rest, the guitar flat).
                    else if (Math.Abs(start) < 0.5f) source = (up ? "+a" : "-a") + a;
                    else source = "a" + a + (start > 0 ? "~" : "");
                }
            }
            if (source == null) return;
            fields.Add(target + ":" + source);
            waitRelease = true;
            ShowStep();
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
