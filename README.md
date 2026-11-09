<p align="center">
  <img src="launcher/art/banner.png" alt="Erased Reboot Version" width="100%">
</p>

<h1 align="center">Erased Reboot Version</h1>

<p align="center"><b>Guitar Hero: Warriors of Rock (Xbox 360), recompiled to run natively on PC.</b><br>
A continuation of <a href="https://github.com/ronniecloud/re-wor">re-wor</a>, made by Gaio Moreira (Erased).</p>

> [!IMPORTANT]
> **You need your own original copy of the game.** This project does not include any of the game's files and never
> will. Use the files extracted from your own Xbox 360 disc (`default.xex` and the `data` folder). Please do not ask for
> the game's files or share them. Guitar Hero is a trademark of Activision; this is a fan project, not affiliated with
> Activision, Neversoft or Microsoft.

[Português](#português) · [What's new](#whats-new) · [How to play](#how-to-play) · [Building](#building-from-source) · [Credits](#credits)

## What's new

Compared with re-wor, this version adds:

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

**Weak PC?** In the VIDEO tab, set the internal resolution to 75 % or 50 %. On a laptop, keep the charger plugged in
and use the "High performance" power plan.

**In game:** F1 SDK quick menu · F2 FPS · F4 every SDK setting · F5 Erased Reboot Version options · F11 fullscreen.

**DLC and custom songs:** the DLC FOLDER button (GAME tab) opens `game\dlc`. For Onyx custom songs, also add the cache
Onyx makes (*Other tools → GH:WoR cache*), built over all your packages, the official ones too.

## Building from source

Windows, with Visual Studio 2022 Build Tools (C++), LLVM/Clang, CMake and Ninja.

1. **The SDK:** [iExplosiveRage's ReXGlue SDK fork](https://github.com/iExplosiveRage/rexglue-sdk) (branch
   `burstlimit`, commit `3c41e4b`) with this project's patch:
   ```bat
   git clone --branch burstlimit --recurse-submodules https://github.com/iExplosiveRage/rexglue-sdk.git rexglue-sdk-erased
   cd rexglue-sdk-erased
   git checkout 3c41e4b
   git apply ..\re-wor\sdk\erased-reboot-sdk.patch
   cmake --preset win-amd64
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
   %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /codepage:65001 /win32icon:app_icon.ico /r:System.Windows.Forms.dll /r:System.Drawing.dll /out:"Erased Reboot Version.exe" launcher\ReWorLauncher.cs
   ```
   The title font is not in this repository; without it, the launcher uses Impact.

`src/sampler.cpp` (`rewor_sample_file`) shows where each thread spends its time, and `src/notcd.cpp`
(`rewor_debug_scan`) lists the C functions the game's scripts can call: the tools used to find the fixes above.

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

**Novidades:** áudio completo (vocal, baixo e bateria), resolução interna abaixo de 720p, 60 FPS estáveis sem V-Sync,
controle comum e teclado como guitarra (botões configuráveis), DLC e músicas customizadas do Onyx (pasta `dlc`), menos
uso de CPU, opções dentro do jogo (F5) e um launcher novo em português e inglês.

**Como jogar:** baixe o zip em [Releases](../../releases), extraia numa pasta qualquer, abra o
**`Erased Reboot Version.exe`**, escolha a pasta do jogo e clique em **JOGAR**. PC fraco? Na aba VÍDEO, coloque a
resolução interna em 75 % ou 50 %.

**Créditos:** feito por mim, Gaio Moreira (Erased), com base no re-wor (ronniecloud), no re-gh2 (YoshiCrystal9), nas
correções do TFujiwara, no ReXGlue SDK, no fork do SDK do iExplosiveRage, no GHVanHRecomp (ElGuslml) e no port do
Piscacho. Usei IA como ajuda ao longo do caminho; as ideias, as decisões e os testes são meus.
