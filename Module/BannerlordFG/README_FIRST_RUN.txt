BannerlordFG v1.1.1 - Player instructions
=======================================

INSTALL
1. Extract the BannerlordFG folder into Mount & Blade II Bannerlord\Modules.
2. Enable BannerlordFG in the Bannerlord launcher.
3. Start the game and open Options > Performance > Frame Generation.
4. Select OptiScaler > Install, confirm, and follow the on-screen prompts.
   The installer shows download progress, verification and extraction stages.
   BannerlordFG prepares the verified files before it closes the game.
   Once ready, it closes the game, copies the files, and attempts to reopen
   Bannerlord automatically. Do not manually reopen it while setup finishes.
   If the log reports that relaunch was unsafe or failed, reopen the game
   through your normal launcher once installation is complete.

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
- This build downloads official OptiScaler nightly-20261109 and checks SHA-256.
- BannerlordFG prefers winmm.dll to reduce DXGI conflicts (e.g. ReShade).
  If another mod already uses winmm.dll, BannerlordFG falls back to dxgi.dll
  when that name is free. Existing DLLs are never overwritten.
- If both dxgi.dll and winmm.dll are already occupied, installation stops.
- ReShade workaround (if the ReShade overlay does not open): close Bannerlord,
  verify dxgi.dll belongs to ReShade and that d3d11.dll is free, then rename
  only the ReShade dxgi.dll to d3d11.dll. An existing d3d11.dll no longer
  blocks setup simply because it exists; it is never modified or claimed.
  Other unsupported hooks and actual filename collisions still block; no DLLs
  are automatically renamed. Coexistence is not guaranteed on all setups.
  Once OptiScaler is installed, set LoadReshade=false under [Plugins] in
  OptiScaler.ini. This is a tested manual workaround, not a universal guarantee.
  ReShade menu hotkey F9: set KeyOverlay=120,0,0,0 in ReShade.ini.
- Existing complete managed installs must be safely uninstalled before upgrade.
- The installer extracts the official 7z with SharpCompress, without Windows tar.exe.
- Keep the extractor DLLs in the mod's bin folder when installing.
- Logs and recovery backups: %LOCALAPPDATA%\BannerlordFG

Source and license: https://github.com/luizbizzio/BannerlordFG
GPL-3.0-only
