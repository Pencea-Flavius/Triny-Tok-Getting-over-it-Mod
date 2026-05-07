# TrinyTok GOI

A [MelonLoader](https://melonwiki.xyz/) mod that bridges **Getting Over It with Bennett Foddy** to [Triny-Tok](https://github.com/your-username/Triny-Tok) — letting TikTok viewers trigger in-game effects in real time via gifts, follows, and likes.

---

## Effects

| Code | Name | Type | Description |
|------|------|------|-------------|
| `launch` | Launch | Instant | Blasts the player upward |
| `shove_left` | Shove Left | Instant | Shoves the player to the left |
| `shove_right` | Shove Right | Instant | Shoves the player to the right |
| `reset_progress` | Reset Progress | Instant | Resets all current progress |
| `low_gravity` | Low Gravity | Timed | Reduces gravity (fall slower) |
| `high_gravity` | High Gravity | Timed | Increases gravity (fall faster) |
| `zero_gravity` | Zero Gravity | Timed | Disables gravity entirely |
| `low_friction` | Low Friction | Timed | Makes surfaces slippery |
| `high_friction` | High Friction | Timed | Makes surfaces sticky |
| `flip_camera` | Flip Camera | Timed | Rotates camera 180° |
| `spin_camera` | Spin Camera | Timed | Camera spins continuously |
| `invert_mouse` | Invert Mouse | Timed | Inverts mouse X and Y axes |

---

## Requirements

- **Getting Over It with Bennett Foddy** (Steam)
- **MelonLoader** installed on the game — [melonwiki.xyz](https://melonwiki.xyz/#/?id=requirements)
- **Triny-Tok** running on the same machine

---

## Building

### Prerequisites

You need one of:
- **Windows**: Visual Studio 2022 + .NET Framework 4.7.2 Developer Pack
- **Linux / macOS**: Mono (`sudo pacman -S mono` on Arch, `sudo apt install mono-complete` on Ubuntu)

### Steps

1. Clone this repo
2. Drop the following DLLs from your game into the `TrinyTokGOI/` folder:

   **From `Getting Over It_Data\Managed\`:**
   ```
   Assembly-CSharp.dll
   UnityEngine.CoreModule.dll
   UnityEngine.Physics2DModule.dll
   Rewired_Core.dll
   Newtonsoft.Json.dll
   ```

   **From `MelonLoader\` (or `MelonLoader\net6\` on newer versions):**
   ```
   MelonLoader.dll
   0Harmony.dll
   ```

3. Build:

   **Windows (Visual Studio):**
   ```
   Open TrinyTokGOI.csproj → Build → Build Solution
   ```

   **Windows (command line):**
   ```
   msbuild TrinyTokGOI.csproj /p:Configuration=Release
   ```

   **Linux with Mono:**
   ```bash
   xbuild TrinyTokGOI.csproj
   ```

4. Output DLL: `bin/Debug/TrinyTokGOI.dll`

---

## Installation

1. Copy `TrinyTokGOI.dll` into the `Mods\` folder inside your Getting Over It game directory:
   ```
   Getting Over It with Bennett Foddy/
   └── Mods/
       └── TrinyTokGOI.dll
   ```

2. Launch Triny-Tok and go to the **Getting Over It** tab.

3. Click **Start Bridge** — it will listen on port `52000`.

4. Launch the game. The mod connects automatically on startup.

---

## Triny-Tok Setup

In Triny-Tok, navigate to the **Getting Over It** tab, then map TikTok gifts to effects:

| Gift | Effect suggestion |
|------|------------------|
| Rose | `reset_progress` |
| TikTok | `launch` |
| Ice Cream | `zero_gravity` |

For timed effects you can configure the duration (in seconds) per mapping.

---

## Project Structure

```
TrinyTokGOI/
├── src/
│   ├── TrinyTokGOIMod.cs    # Main MelonLoader mod + Harmony patches
│   └── TrinyTokClient.cs    # TCP client connecting to Triny-Tok
├── TrinyTokGOI.csproj
└── README.md
```

---

## How It Works

```
TikTok gift
    │
    ▼
Triny-Tok (Node.js server)
    │  TCP :52000  (JSON + \0 delimiter)
    ▼
TrinyTokGOI.dll (MelonLoader mod)
    │  Harmony patches on Unity main thread
    ▼
Getting Over It game
```

Triny-Tok acts as a TCP server; the mod connects as a client on startup and reconnects automatically if the connection drops. Effects are sent as JSON packets, executed on the Unity main thread via a pending-action queue, and acknowledged with a status code.

---

## License

MIT
