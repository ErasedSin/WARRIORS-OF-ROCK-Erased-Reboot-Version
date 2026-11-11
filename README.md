<p align="center">
  <img src="launcher/art/banner.png" alt="Erased Reboot Version" width="100%">
</p>

<h1 align="center">Erased Reboot Version</h1>

<p align="center"><b>Guitar Hero: Warriors of Rock and Guitar Hero 5 (Xbox 360), recompiled to run natively on PC.</b><br>
A continuation of <a href="https://github.com/ronniecloud/re-wor">re-wor</a>, made by Gaio Moreira (Erased).</p>

> [!IMPORTANT]
> **You need your own original copy of the game.** This project does not include any of the game's files and never
> will. Use the files extracted from your own Xbox 360 disc (`default.xex` and the `data` folder). Please do not ask for
> the game's files or share them. Guitar Hero is a trademark of Activision; this is a fan project, not affiliated with
> Activision, Neversoft or Microsoft.

[Português](#português) · [What's new](#whats-new) · [How to play](#how-to-play) · [Building](#building-from-source) · [Credits](#credits)

## What's new

**In 2.2: everything in one folder.** A new install keeps saves, the shader cache and installed DLC in
`game\userdata`, next to the game, so it can all live on another drive (see *Another drive* below).

**In 2.1: DLC and controller fixes.**

- 📦 **Guitar Hero 5 DLC in Warriors of Rock:** each content package is installed under the game it was made for, so
  Warriors of Rock reads the Guitar Hero 5 packages it lists (the disc imports of World Tour, Metallica and Smash Hits,
  for example) and plays their songs. What Guitar Hero 5 already installed is shared with Warriors of Rock through
  hard links, without taking any more space. Packages of a game that cannot read them are left out, which fixes the
  crash with both games' DLC in one folder.
- 🎸 **The whammy bar works again** when the keyboard is on next to the guitar.
- 🎮 Xbox 360 guitars and drums are always read through XInput; PS3 guitars set up button by button get the buttons
  you chose.

**In 2.0: Warriors of Rock and Guitar Hero 5 in one launcher.**

- 🎸 **Guitar Hero 5 (Xbox 360)**, recompiled on the same SDK as Warriors of Rock, with its own page in the launcher
  (blue and black). It needs your own copy too (`default.xex` and `data` of the Xbox 360 game).
- 🧭 **One launcher for both games:** the same tabs for each (home, game, video, graphics, players, keyboard,
  microphone, online, controls, profile) and its own settings for each game.
- 🏆 **Achievements for each profile**, and a PROFILE tab that lists them for every player.
- 🎮 **Controls:** a regular controller plays as the singer's controller or as a guitar (your choice, in both games);
  guitars that Windows only sees as a "USB controller" (Wii or PS3 guitars on an adapter) are set up button by button,
  the way Clone Hero does; a guitar through **Parsec** keeps its whammy bar and tilt; adjustable tilt sensitivity.
- ⌨️ **Keyboard-mode guitars** (Raspberry Pi Pico / Santroller): Shift and Ctrl work as strum keys, and Esc as Start.
- 🔊 The audio buffer (16, 32 or 64 frames) in the GAME tab.

**In 1.2:**

- 🚀 **Above 60 FPS:** 120, 144, 165 or 240 FPS (VIDEO tab). Whatever the game counts in frames (script waits, the
  whammy, button presses, the loading screen) keeps the console's speed, so the notes and the music don't speed up.
- 🎨 **Vulkan** besides DirectX 12 (GRAPHICS tab); a backend that doesn't start falls back to the other.
- 🧵 **Less CPU and steadier sound:** threads briefly watch a held lock before they sleep, and the audio runs in
  Windows' "Pro Audio" class.
- 👥 **A profile for each player:** players 2 to 4 get their own name and their own saves.
- 🚪 **Quit from the main menu:** press B and confirm.
- 🌐 **Online play (experimental):** reaches the matchmaking over LAN or a VPN.
- 🎼 **The music stays in time:** the game's clock ran 0.25 % fast, so the notes slowly got ahead of the song.
- ⏭️ Skip the opening video, and a profile picture chosen in the launcher.
- 🔥 **A new launcher home page**, fullscreen (F11 for a window), that checks for updates by itself when it opens.

**Compared with re-wor**, this version adds:

- 🎵 **Every track of the songs:** vocals, bass and drums play with the guitar (XMA frames that span two packets are
  put back together; the fix comes from TFujiwara's SDK fork).
- 🖥️ **Internal resolution below 720p** (75 %, 50 %, 33 %): much lighter on weak graphics cards. A weak laptop went from
  about 15 to 30 FPS at 50 %.
- ⏱️ **A steady 60 FPS without V-Sync:** the game's clock always runs at 60 Hz, so the notes stay with the music; V-Sync
  only decides whether the picture waits for the display.
- 🎸 **Regular controllers as a guitar:** Xbox or PlayStation controllers play as a guitar, with every button
  configurable (presets: Classic, and Triggers). Real guitars keep working as they are.
- ⌨️ **The keyboard as a guitar:** A S J K L or 1 2 3 4 5, with the arrows to strum, every key configurable.
- 💿 **DLC and custom songs:** drop Xbox 360 packages (CON / LIVE) in the `dlc` folder; they are installed when the game
  starts. Custom songs made with [Onyx](https://github.com/mtolly/onyx) work with the song cache Onyx builds.
- 🧵 **Lighter on the CPU:** the game's threads no longer spin while they wait for each other.
- 🛠️ **Options inside the game (F5)**, an FPS counter that counts the game's own frames (F2), and an experimental switch
  for the game's developer parts (its debug menu).
- 🔥 **A new launcher**, in Portuguese and English: game folder, video, graphics, controls, keyboard and DLC.

## How to play

1. Download the zip from [Releases](../../releases) and extract it anywhere you can write to (not `Program Files`).
2. Open **`Erased Reboot Version.exe`** and choose the folder with your game files (`default.xex` and `data`).
3. Pick your settings and press **PLAY**.

**Another drive:** the zip can go on any drive. A new install keeps its saves, shader cache and installed DLC in
`game\userdata`, next to the game. An install from before 2.2 keeps using `Documents\re_wor`; to bring it along, move
that folder into `game` and rename it `userdata`.

**Weak PC?** In the VIDEO tab, set the internal resolution to 75 % or 50 %. On a laptop, keep the charger plugged in
and use the "High performance" power plan.

**In game:** F1 SDK quick menu · F2 FPS · F4 every SDK setting · F5 Erased Reboot Version options · F11 fullscreen.

**DLC and custom songs:** pick the game in the launcher and press DLC FOLDER (GAME tab): it opens that game's `dlc`
folder (`game\dlc` or `gh5\dlc`). Drop your Xbox 360 content packages there, subfolders are fine, and start the game:
the first time, it installs them before the game begins, which can take a few minutes with a big pack while the window
looks frozen. Guitar Hero 5 DLC goes in Guitar Hero 5's folder, and Warriors of Rock picks up what Guitar Hero 5
installed on its own, with no extra space. A package for the wrong game is just left out. Installing keeps an
extracted copy (in `game\userdata`, or `Documents\re_wor` on an older install), so the space doubles for a while: once the songs show up in the game, you can delete the packages
from the `dlc` folder and the songs stay. For Onyx custom songs, also add the cache Onyx makes (*Other tools → GH:WoR
cache*), built over all your packages, the official ones too.

## Building from source

Windows, with Visual Studio 2022 Build Tools (C++), LLVM/Clang, CMake and Ninja.

1. **The SDK:** [iExplosiveRage's ReXGlue SDK fork](https://github.com/iExplosiveRage/rexglue-sdk) (branch
   `burstlimit`, commit `3c41e4b`) with this project's patch:
   ```bat
   git clone --branch burstlimit --recurse-submodules https://github.com/iExplosiveRage/rexglue-sdk.git rexglue-sdk-erased
   cd rexglue-sdk-erased
   git checkout 3c41e4b
   git apply ..\re-wor\sdk\erased-reboot-sdk.patch
   cmake --preset win-amd64 -DREXGLUE_USE_VULKAN=ON
   cmake --build out/build/win-amd64 --config Release
   cmake --install out/build/win-amd64 --config Release
   ```
   On Windows, the symbolic links in `thirdparty/libmspack` come out as text files: replace each with the file it
   points to before building.
2. **The game:** put your `default.xex` and `data` in `assets/`, then:
   ```bat
   <sdk>\out\install\win-amd64\bin\rexglue.exe codegen rewor_manifest.toml
   cmake --preset win-amd64-release -DCMAKE_PREFIX_PATH=<sdk>\out\install\win-amd64
   cmake --build out/build/win-amd64-release
   ```
3. **The launcher** (C#, built with the compiler that comes with Windows):
   ```bat
   %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /codepage:65001 /win32icon:launcher\launcher.ico /r:System.Windows.Forms.dll /r:System.Drawing.dll /out:"Erased Reboot Version.exe" launcher\ReWorLauncher.cs
   ```
   The title font is not in this repository; without it, the launcher uses Impact.

`src/sampler.cpp` (`rewor_sample_file`) shows where each thread spends its time, and `src/notcd.cpp`
(`rewor_debug_scan`) lists the C functions the game's scripts can call: the tools used to find the fixes above.

## Support

Everything here is free and stays free. If you want to help, you can donate on [Ko-fi](https://ko-fi.com/erasedsin)
or, from Brazil, by PIX on [LivePix](https://livepix.gg/erasedsin). Donations unlock nothing extra: they only help me
keep working on it.

## Credits

Made by **Gaio Moreira (Erased)**, on top of the work of:

- [re-gh2](https://github.com/YoshiCrystal9/re-gh2) by YoshiCrystal9, which re-wor is based on.
- [re-wor](https://github.com/ronniecloud/re-wor) by ronniecloud, the original port this version continues.
- [TFujiwara's re-wor fork](https://github.com/TFujiwara/re-wor) and
  [SDK fork](https://github.com/TFujiwara/rexglue-sdk/tree/fix/guitar-hero-wor-recompilation): the XMA audio fix, the
  language setting, the crash reports.
- [ReXGlue SDK](https://github.com/rexglue/rexglue-sdk) by Tom Clay and contributors, based on [Xenia](https://xenia.jp).
- [iExplosiveRage's SDK fork](https://github.com/iExplosiveRage/rexglue-sdk) (DBZ Burst Limit Recompiled): the
  internal resolution below 720p and the graphics card selection.
- [GHVanHRecomp](https://github.com/ElGuslml/GHVanHRecomp) by ElGuslml (MIT): the thread waits and the auto-strum gate.
- [Piscacho's Warriors of Rock port](https://github.com/Piscacho/Guitar-Hero-Warriors-of-Rock-PC) (MIT): the layout of
  the controller-as-guitar buttons.
- The WoR+ project (kernaltrap8), for what is known about the game's scripts.
- [Onyx](https://github.com/mtolly/onyx) by mtolly, for custom songs.
- The title font: Nightmare Hero, by _n3o_.

The licenses of the components are in `THIRD_PARTY_NOTICES.md` and, in the release, in `game\licenses`.

## A note about AI

I used AI as a helper along the way, for research and to move faster. The ideas, the decisions and the testing with
a real guitar are mine. The game itself is still the original game: its code is translated by the ReXGlue recompiler.

## License

There is no license file: re-wor and re-gh2, which this project continues, have none, so their parts stay with their
authors. The ReXGlue SDK (BSD) and the MIT components keep their own licenses (`THIRD_PARTY_NOTICES.md`). Guitar Hero:
Warriors of Rock, its code and its assets belong to their respective owners.

---

## Português

**Guitar Hero: Warriors of Rock (Xbox 360) recompilado para rodar nativo no PC.** Uma continuação do
[re-wor](https://github.com/ronniecloud/re-wor), feita por Gaio Moreira (Erased).

**Você precisa da sua própria cópia do jogo.** Este projeto não inclui nenhum arquivo do jogo. Use os arquivos
extraídos do seu disco de Xbox 360 (o `default.xex` e a pasta `data`). Diga não à pirataria.

**Novidades da 2.2:** tudo numa pasta só. Uma instalação nova guarda os saves, o cache de shaders e os DLCs instalados
em `game\userdata`, ao lado do jogo, então tudo pode ficar em outro HD (veja *Outro HD* abaixo).

**Novidades da 2.1:** o DLC do Guitar Hero 5 no Warriors of Rock (cada pacote é instalado para o jogo dele, e o que o
Guitar Hero 5 já instalou é compartilhado sem ocupar mais espaço); o fim do crash com os DLCs dos dois jogos na mesma
pasta; a alavanca da guitarra funcionando com o teclado ligado; guitarras e baterias de Xbox 360 sempre por XInput; e
os botões das guitarras de PS3 configuradas no assistente.

**Novidades da 2.0:** o Guitar Hero 5 (Xbox 360) junto com o Warriors of Rock, num launcher só, com as mesmas abas
para os dois; conquistas para cada perfil, com uma aba PERFIL que mostra todas; controle comum como controle do
vocalista ou como guitarra, nos dois jogos; guitarras que o Windows vê só como "controle USB" (de Wii ou PS3 num
adaptador) configuradas botão por botão, como no Clone Hero; guitarra pelo Parsec com alavanca e inclinação;
sensibilidade da inclinação ajustável; guitarras em modo teclado (Raspberry Pi Pico / Santroller) com Shift e Ctrl
na palheta; e o buffer de áudio na aba JOGO. Para o GH5 também é preciso a sua cópia do jogo.

**Novidades da 1.2:** mais de 60 FPS (120, 144, 165 ou 240, sem acelerar as notas), Vulkan além do DirectX 12, menos
uso de CPU e áudio mais estável, um perfil para cada jogador, sair do jogo pelo menu principal (B), online
experimental, música sempre no tempo, pular o vídeo de abertura e um launcher novo em tela cheia que procura
atualizações sozinho.

**Desde a primeira versão:** áudio completo (vocal, baixo e bateria), resolução interna abaixo de 720p, 60 FPS estáveis sem V-Sync,
controle comum e teclado como guitarra (botões configuráveis), DLC e músicas customizadas do Onyx (pasta `dlc`), menos
uso de CPU, opções dentro do jogo (F5) e um launcher novo em português e inglês.

**Como jogar:** baixe o zip em [Releases](../../releases), extraia numa pasta qualquer, abra o
**`Erased Reboot Version.exe`**, escolha a pasta do jogo e clique em **JOGAR**. PC fraco? Na aba VÍDEO, coloque a
resolução interna em 75 % ou 50 %.

**Outro HD:** o zip pode ficar em qualquer HD. Uma instalação nova guarda os saves, o cache de shaders e os DLCs
instalados em `game\userdata`, ao lado do jogo. Uma instalação de antes da 2.2 continua usando `Documentos\re_wor`;
para levar junto, mova essa pasta para dentro de `game` e renomeie para `userdata`.

**DLC e músicas customizadas:** escolha o jogo no launcher e clique em PASTA DE DLC (aba JOGO). Abre a pasta `dlc` daquele
jogo (`game\dlc` ou `gh5\dlc`). Coloque ali os pacotes de DLC de Xbox 360, pode ser em subpastas, e abra o jogo: na
primeira vez ele instala tudo antes de começar, o que pode levar alguns minutos com um pack grande, com a janela parecendo
parada. DLC do Guitar Hero 5 vai na pasta do Guitar Hero 5, e o Warriors of Rock pega sozinho o que o Guitar Hero 5
instalou, sem ocupar mais espaço. Pacote do jogo errado é só ignorado. A instalação guarda uma cópia extraída (em `game\userdata`, ou em `Documentos\re_wor` numa instalação antiga), então o
espaço dobra por um tempo: depois que as músicas aparecerem no jogo, dá para apagar os pacotes da pasta `dlc`, e as
músicas continuam. Para músicas customizadas do Onyx, coloque também o cache que o Onyx gera (*Other tools → GH:WoR
cache*), feito sobre todos os seus pacotes, os oficiais também.

**Apoie:** tudo aqui é e continua de graça. Se quiser ajudar, dá para doar pelo [LivePix](https://livepix.gg/erasedsin)
(PIX) ou pelo [Ko-fi](https://ko-fi.com/erasedsin). Doação não libera nada a mais: é só apoio.

**Créditos:** feito por mim, Gaio Moreira (Erased), com base no re-wor (ronniecloud), no re-gh2 (YoshiCrystal9), nas
correções do TFujiwara, no ReXGlue SDK, no fork do SDK do iExplosiveRage, no GHVanHRecomp (ElGuslml) e no port do
Piscacho. Usei IA como ajuda ao longo do caminho; as ideias, as decisões e os testes são meus.
