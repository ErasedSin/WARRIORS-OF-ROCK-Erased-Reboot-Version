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
            { "tab_home", new[] { "INÍCIO", "HOME" } },
            { "home_no_name", new[] { "Jogador", "Player" } },
            { "home_folder_ok", new[] { "Jogo encontrado", "Game found" } },
            { "home_folder_missing", new[] { "Falta escolher a pasta do jogo, na página JOGO.", "Choose the game folder first, on the GAME page." } },
            { "home_hint", new[] { "Vídeo, controles, microfone e o resto ficam nas páginas ao lado.", "Video, controls, microphone and the rest are on the pages at the side." } },
            { "home_version", new[] { "versão {0}", "version {0}" } },
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

    // A panel that paints without flicker.
    class Canvas : Panel
    {
        public Canvas() { DoubleBuffered = true; ResizeRedraw = true; }
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
        const string AppVersion = "1.1";
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
            ShowPage(IsGameFolder(gameFolder) ? HomePage : GamePage);
            Shown += delegate { if (!IsGameFolder(gameFolder)) ChooseGameFolder(); };
            FormClosed += delegate { if (meter != null) { meter.Dispose(); meter = null; } };
            PlayIntro();
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

            pageNames = PageNames();
            var onlinePage = BuildOnlineTab();
            var builtList = new List<Control> { BuildHomeTab(), BuildGameTab(), BuildVideoTab(), BuildGraphicsTab(), BuildPlayersTab(), BuildKeyboardTab(), BuildMicTab() };
            if (OnlineTab) builtList.Add(onlinePage);
            builtList.Add(BuildUpdateTab());
            Control[] built = builtList.ToArray();
            for (int i = 0; i < built.Length; i++)
            {
                built[i].Location = new Point(ContentX, 142);
                built[i].Size = new Size(PageW, 384);
                pages.Add(built[i]);
                Controls.Add(built[i]);
            }

            ShowProfilePicture();

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
            bottomPlay = play;
            bottomSave = save;
            if (currentPage == HomePage) play.Visible = save.Visible = false;
        }

        static string[] PageNames()
        {
            var names = new List<string> { Strings.T("tab_home"), Strings.T("tab_game"), Strings.T("tab_video"), Strings.T("tab_gfx"), Strings.T("tab_players"), Strings.T("tab_keyboard"), Strings.T("tab_mic") };
            if (OnlineTab) names.Add(Strings.T("tab_online"));
            names.Add(Strings.T("tab_update"));
            return names.ToArray();
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

            string[] names = PageNames();
            for (int i = 0; i < names.Length; i++)
            {
                int index = i;
                var nav = new Button
                {
                    Text = "   " + names[i], Location = new Point(16, 156 + i * 44), Size = new Size(Side - 35, 40),
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
            if (micDevice != null && (index == MicPage) != (meter != null)) RestartMicMeter();
            if (index == HomePage) ShowHome();
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
            AddLabel(page, Strings.T("game_lang"), 144);
            language = AddCombo(page, 164,
                new Choice("English", "1"),
                new Choice("Español", "5"),
                new Choice("Français", "4"),
                new Choice("Deutsch", "3"),
                new Choice("Italiano", "6"));
            debugMenu = AddCheck(page, Strings.T("debug_menu"), 204);
            skipIntro = AddCheck(page, Strings.T("skip_intro"), 230);
            AddNote(page, Strings.T("game_note"), 262);
            AddNote(page, Strings.T("dlc_note"), 312);
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
                    using (var fill = new SolidBrush(Mix(Field, Color.FromArgb(92, 12, 26), glow))) g.FillRectangle(fill, card);
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
                        using (var halo = new SolidBrush(Color.FromArgb((int)(110 * glow), 232, 36, 64)))
                            g.FillEllipse(halo, led.X - 5, led.Y - 5, led.Width + 10, led.Height + 10);
                    }
                    using (var dot = new SolidBrush(Mix(Color.FromArgb(70, 70, 70), Color.FromArgb(255, 70, 90), glow))) g.FillEllipse(dot, led);
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
            meter = new MicMeter();
            if (!meter.Open(Value(micDevice)))
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
                float level = Math.Min(1f, peak * micVolume.Value / 100f);
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
                    new Rectangle(0, 0, w, h), open ? Color.FromArgb(150, 20, 40) : Color.FromArgb(70, 70, 70),
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
                if (e.Error != null)
                {
                    updateStatus.Text = Strings.T("update_failed");
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
            sb.AppendLine("$keep = 're_wor.toml', 'game_folder.txt', 'launcher_language.txt'");
            sb.AppendLine("$gameSrc = Join-Path $src 'game'");
            sb.AppendLine("if (Test-Path -LiteralPath $gameSrc) {");
            sb.AppendLine("  Get-ChildItem -LiteralPath $gameSrc -Recurse -File | Where-Object { $keep -notcontains $_.Name } | ForEach-Object {");
            sb.AppendLine("    $dst = Join-Path $gameDir $_.FullName.Substring($gameSrc.Length + 1)");
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
            string path = Path.Combine(gameDir, PictureFile);
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
                        square.Save(Path.Combine(gameDir, PictureFile), System.Drawing.Imaging.ImageFormat.Png);
                        File.WriteAllBytes(Path.Combine(gameDir, PictureData), data);
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
                File.Delete(Path.Combine(gameDir, PictureFile));
                File.Delete(Path.Combine(gameDir, PictureData));
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
                area, Color.FromArgb(alpha * 110 / 255, 150, 0, 22), Color.FromArgb(0, 0, 0, 0), 90f))
                g.FillRectangle(glow, area);
            using (var big = new Font(titleFamily, size, FontStyle.Regular, GraphicsUnit.Point))
            using (var small = new Font(titleFamily, size * 0.45f, FontStyle.Regular, GraphicsUnit.Point))
            using (var shadow = new SolidBrush(Color.FromArgb(alpha * 170 / 255, 90, 0, 14)))
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
                using (var stripe = new Pen(Color.FromArgb(40, 200, 16, 46), 2f))
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
            var intro = new Canvas { Location = new Point(0, 0), Size = ClientSize, BackColor = Color.Black };
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
            settings.Set(CvarPrefix + "debug_menu", debugMenu.Checked);
            settings.Set(CvarPrefix + "skip_intro", skipIntro.Checked);
            // A regular controller stays a controller: the game's own debug menu
            // (Enable Controller) lets it play the guitar.
            settings.Set("gamepad_as_guitar", false);
            settings.Remove("guitar_autostrum");
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
            // controllers are read through SDL.
            settings.Set("input_backend", "xinput");
            settings.Set(CvarPrefix + "mic_player", int.Parse(Value(micPlayer)));
            if (Value(micDevice) == "") settings.Remove(CvarPrefix + "mic_device");
            else settings.Set(CvarPrefix + "mic_device", Value(micDevice));
            settings.Set(CvarPrefix + "mic_volume", micVolume.Value);
            settings.Set(CvarPrefix + "mic_gate", micGate.Value);
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
