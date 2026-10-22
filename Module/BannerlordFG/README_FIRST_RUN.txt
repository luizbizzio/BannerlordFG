BannerlordFG v1.0.2 - Player instructions
=======================================

INSTALL
1. Extract the BannerlordFG folder into Mount & Blade II Bannerlord\Modules.
2. Enable BannerlordFG in the Bannerlord launcher.
3. Start the game and open Options > Performance > Frame Generation.
4. Select OptiScaler > Install, confirm, and follow the on-screen prompts.
   BannerlordFG downloads and verifies OptiScaler. When initial setup needs
   the game to restart, it normally closes and relaunches the game
   automatically after your confirmation. If it cannot recover the original
   launch settings, follow the in-game instructions and relaunch via your
   usual launcher instead.

USE
A compatible GPU supporting FSR or DLSS is required. A supported upscaling
backend must be available for Frame Generation to work.
- Frame Generation: On / Off.
- Multiplier: fixed at 2x (one generated frame between rendered frames).
- Show FPS Counter: On / Off (OptiScaler's post-FG overlay).
After initial setup, FG and FPS-counter changes can be requested live when
the graphics backend is ready. Some backend changes may require a relaunch.
The FG switch shows the last state requested by the mod, not proof that
OptiScaler is generating frames. Check the OptiScaler overlay to confirm.
The native Bannerlord FPS counter can require one relaunch to disappear
if it was enabled before installing the mod.

UNINSTALL
Preferred: Options > Performance > Frame Generation > OptiScaler > Uninstall.
Follow the on-screen prompts. In case recovery is needed, close the game
and run UNINSTALL_WINDOWS.ps1 with PowerShell before removing the
BannerlordFG folder. Read RECOVERY_GUIDE.txt for exact steps.

NOTES
- Windows / DirectX 11 / single-player. This is an experimental release.
- Frame Generation increases displayed frames, not game simulation speed.
  Some hardware may show visual artifacts or additional latency.
- On AMD or Intel GPUs, use DLSS Quality as Bannerlord's input option
  after installation, even if OptiScaler uses FSR for upscaling.
- OptiScaler files are downloaded separately and are not inside this ZIP.
- The installer extracts the official 7z with SharpCompress, without Windows tar.exe.
- Keep the extractor DLLs in the mod's bin folder when installing.
- Logs and recovery backups: %LOCALAPPDATA%\BannerlordFG

Source and license: https://github.com/luizbizzio/BannerlordFG
GPL-3.0-only
