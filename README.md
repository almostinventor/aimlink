# AIMLINK

A mixed-reality smart-link reticle for **Meta Quest 3 / 3S**. Tape two Touch Plus controllers to a straight pipe (or an empty, non-firing prop), and AIMLINK draws a world-locked reticle in passthrough exactly where it is pointing, on your real walls and furniture. You can aim from the hip without bringing it up to your eye.

**Install:** [AIMLINK on SideQuest](https://sidequestvr.com/app/63606/aimlink) · or download the APK from [Releases](https://github.com/almostinventor/aimlink/releases). Found a problem? [Open an issue](https://github.com/almostinventor/aimlink/issues).

> **This project is entirely AI-generated.** All code, shaders, editor tooling, scripts and the synthesized sound effects in this repository were written by **Claude** (Anthropic's AI model) using **Claude Code**. A human directed the project and tested it on a headset, but did not hand-write the code. Review it accordingly before relying on it.

AIMLINK is a visual demo and toy. It has no firing, targets or game modes. **It is not made for, and must not be used with, loaded or working airsoft guns, gel blasters or any real weapon.** See [Safety](#safety).

---

## Features

- **Reticle on real surfaces.** Raycasts the aim line against the room from Space Setup (global mesh, walls, furniture), with live depth and a fixed-distance fallback. Constant on-screen size, smoothed, distance readout at the reticle, `HIP` tag when aiming from the hip.
- **Room scan.** A scan wave sweeps the room's real geometry during boot, tagging walls, tables and other surfaces as it reaches them. Afterwards a faint grid is revealed around the reticle (toggleable).
- **Boot sequence.** Blackout, POST text, the visor coming online in blocks, the scan, then the HUD and reticle.
- **Minimal HUD.** A compass ribbon and a message line that only shows something when it matters (tracking loss, trim changes, toggles).
- **Beacons.** Drop numbered markers on real surfaces.
- **Scope.** A magnified inset of the forward camera, centred on the aim point.
- **3D minimap.** A small hologram of your scanned room in the lower left, heading-up and centred on you, showing the aim line, the point it hits and your markers.
- **Trim.** Fine-tune the aim angle with the thumbstick; saved between sessions.

## Hardware setup

- Meta Quest 3 or 3S, with **Space Setup** done for the room (Settings > Physical Space > Space Setup).
- A straight pipe (or an empty, non-firing prop) with two Touch Plus controllers taped to it, as far apart as practical:
  - **right controller at the handle end (rear)**
  - **left controller at the front end**

  The aim direction is the line from the rear controller to the front controller. The further apart they are, the steadier the aim. If the line between the controllers isn't parallel to the pipe, correct it with trim (in the headset).

  Mounting the controllers the other way round, or a fixed mount offset, are set in Unity before building: **Swap Controllers** and **Mount Local Position / Euler** on `MuzzleTransform` (`MuzzleTrim`). They can't be changed in the installed APK.
- Keep both controllers visible to the headset. If the two are taped close together they can block each other's tracking LEDs.
- To align it, point the pipe at a clear mark on a wall about 4–5 m away and trim until the reticle sits on it.

## Safety

- **Not for use with loaded or working airsoft guns, gel blasters or any real weapon. Don't do it.** AIMLINK is designed for a pipe or an empty, non-firing prop, and that is the only way it should be used. Don't use it in airsoft games or anywhere projectiles are being fired.
- **The Quest 3 is not eye protection.** It is not rated to stop a BB or any other projectile.
- The reticle shows a geometric line from the controllers. It is not a sighting system and does not show where anything fired would land.
- In passthrough your peripheral vision and depth perception are reduced. Clear the area, keep people at a distance, and don't run.

## Controls

| Input | Action |
|---|---|
| **Y** | LIVE → BLACKOUT → BOOT, which returns to LIVE by itself (Y is ignored while it plays). Hold 1.5 s to jump straight to LIVE. |
| **Left thumbstick** flick / hold | Trim yaw / pitch (0.2° per flick, repeats while held) |
| **Left thumbstick click**, hold 1 s | Reset trim |
| **X** | Room geometry on / off |
| **Left trigger** | Place a beacon at the aim point |
| **Left grip** | Clear all beacons |
| **A** (right controller) | Scope inset on / off |
| **B** (right controller) | Minimap on / off |

## Permissions

- **Spatial data** (`com.oculus.permission.USE_SCENE`): reads the room layout from Space Setup, and live depth as a fallback when the room data doesn't cover a surface, for the reticle and the scan.
- **Headset camera** (`horizonos.permission.HEADSET_CAMERA`): the forward camera image for the scope inset only.

The app has **no internet permission**, so nothing (room data, camera images or usage data) can leave the headset. It collects no analytics; trim is only saved locally. `com.oculus.permission.USE_ANCHOR_API` is also listed: Meta's SDK requires it for reading the room data.

## Installing the APK

The easiest way is **[AIMLINK on SideQuest](https://sidequestvr.com/app/63606/aimlink)**.

To install manually, enable developer mode on the headset, download `AimLink.apk` from [Releases](https://github.com/almostinventor/aimlink/releases) and sideload it with SideQuest or `adb install AimLink.apk`. It appears under **Unknown Sources** in the app library. Allow both permissions on first launch.

## Building from source

### Requirements

- **Unity 6000.6.5f1** with **Android Build Support** (including OpenJDK and Android SDK & NDK Tools).
- Windows for the helper scripts in `Tools` (they are PowerShell). The Unity steps themselves work on any platform Unity supports.

The Meta XR SDK and MR Utility Kit are not in this repository. Unity's Package Manager downloads them from Meta's package registry when the project is first opened (see `Packages/manifest.json`).

### Steps

1. Open the project in Unity 6000.6.5f1 and let packages resolve.
2. **AimLink > 3. Configure Project For Quest 3** switches to Android and applies the Quest 3 player, XR and permission settings, including the Meta Project Setup Tool fixes.
3. **AimLink > 4. Build APK** writes `Builds/AimLink.apk`, signed with Unity's debug key. That's fine for trying it out.

The scene, materials, sound bank and render pipeline asset are already in the repository. **AimLink > 2. Build Assets And Scene** regenerates them; you only need it after changing the generator (`Assets/AimLink/Editor/AimLinkSetup.cs`) or adding the boot sound. Step 1 in that menu (TextMesh Pro resources) is already done.

The same steps run in batch mode with `-executeMethod AimLink.EditorTools.AimLinkSetup.<BuildScene|ConfigureProject|BuildApk>`.

### Signed release builds

Releases must always be signed with the same key, otherwise an update won't install over a previous version.

1. Create the key once:
   ```
   powershell -ExecutionPolicy Bypass -File Tools\Release\create-keystore.ps1
   ```
   This writes `%USERPROFILE%\.aimlink\aimlink-release.keystore`, outside the repository. **Back it up and never commit it.**
2. Build a release (close the Unity editor first):
   ```
   powershell -ExecutionPolicy Bypass -File Tools\Release\build-release.ps1 -VersionName 1.0.0 -VersionCode 1
   ```
   Increase `-VersionCode` for every release. The password is asked for at the prompt and only passed to that one build through environment variables (`AIMLINK_KEYSTORE`, `AIMLINK_KEYSTORE_PASS`, `AIMLINK_KEY_ALIAS`, `AIMLINK_KEY_PASS`). Unity copies it into its generated Gradle files under `Library/Bee` during the build; the script overwrites those copies afterwards, so it is not left on disk.

Builds made with the debug key and builds made with the release key can't update each other: uninstall one before installing the other.

### Boot sound

The boot sequence is timed to a riser sound from Pixabay whose peak lands at 4.0 s: `soundreality-riser-end-up-swell-360990` (by SoundReality). Its licence allows using it inside an app, but not redistributing the file on its own, so it is **not included** here.

To use it, download it from Pixabay, save it as `Assets/AimLink/Audio/boot_powerup_riser.mp3`, and run **AimLink > 2. Build Assets And Scene** again. Without it, the boot plays without that sound; the other boot sounds still play. Any other sound works too: set **BootSequence > Timings > Visor Open At** to its peak in seconds.

### Other tools

- **Sound effects** in `Assets/AimLink/Audio` (except the riser) are synthesized by `Tools/SfxGen`. Regenerate with `powershell -ExecutionPolicy Bypass -File Tools\SfxGen\generate.ps1`.
- **AimLink > Render HUD Preview** renders the HUD, reticle and a beacon to `Builds/hud_preview.png` without a headset.

## Project layout

```
Assets/AimLink/
  Scripts/    runtime: state machine, input, aim, reticle, scan, boot, HUD, beacons, scope, minimap
  Shaders/    room scan, reticle, beacon, visor mask, minimap hologram
  Editor/     project setup, scene generator, build, preview
  Audio/      sound effects
  Fonts/      Share Tech Mono, Rajdhani (SIL Open Font License)
Tools/
  SfxGen/     sound effect synthesizer
  Release/    keystore and signed-build scripts
```

## License

Copyright (C) 2026 almostinventor

This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version. See [LICENSE](LICENSE).

This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.

**Additional permission under GNU GPL version 3 section 7:** If you modify this Program, or any covered work, by linking or combining it with the Unity engine and Unity packages, the Meta XR Core SDK, or the Meta MR Utility Kit (or modified versions of those libraries), containing parts covered by the terms of their respective licenses, the licensors of this Program grant you additional permission to convey the resulting work. The Corresponding Source for such a combination does not need to include the source code of those libraries, but must still include the complete source code of the covered work.

### Third-party components

| Component | License | In this repository? |
|---|---|---|
| Share Tech Mono, Rajdhani fonts | SIL Open Font License 1.1 (texts in `Assets/AimLink/Fonts`) | Yes |
| TextMesh Pro essential resources | Unity Companion License | Yes (`Assets/TextMesh Pro`) |
| Unity engine and packages | Unity terms | No, installed by Unity |
| Meta XR Core SDK, MR Utility Kit | Oculus SDK License | No, downloaded by the Package Manager |
| Boot riser sound | Pixabay Content License | No (see "Boot sound") |

AIMLINK is not affiliated with or endorsed by Meta or Unity.
