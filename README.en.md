# DAVIGO True LAN

[Русский](README.md) | **English**

An unofficial BepInEx mod for **DAVIGO: VR vs. PC** that replaces the cloud gameplay session with a direct UDP connection.

- the paid DAVIGO copy on the VR PC acts as the host and Giant;
- up to four DAVIGO Knight Pass clients join as Warriors;
- automatic host discovery on a physical local network;
- remote play through Porthole using a single forwarded UDP port;
- DAVIGO voice chat is intentionally not included.

> [!WARNING]
> Version 1.0.1 is tied to DAVIGO **0.5.2A-1493 (Windows x64)**. The installers verify the game build and stop when it does not match.

## Download and install

Open the [private v1.0.1 release](https://github.com/TimurShigg/DavigoLANmod/releases/tag/v1.0.1) and download the appropriate archives:

- `DAVIGO-True-LAN-VR-HostPack-v1.0.1.zip` — for the owner of the paid DAVIGO copy and VR headset;
- `DAVIGO-True-LAN-KnightPack-v1.0.1.zip` — for each Knight Pass player.

Close the game, fully extract the archive, and run `1-Install-VR-Host.bat` or `1-Install-KnightPass.bat`. If Windows asks for network access for `Davigo.exe`, allow it on private networks. The first launch after installation may take about a minute.

The detailed installation, troubleshooting, Porthole, and build guides are currently available in Russian under [`docs`](docs/).

## Playing on a physical LAN

1. The VR host launches the paid DAVIGO copy and creates a network room.
2. Each client runs `2-Mode-LAN.bat`, starts Knight Pass, and uses the normal network menu.
3. If the UI asks for a room code, any four digits may be entered.
4. The host selects a map and starts the match normally.

## Playing through Porthole

1. The host creates a DAVIGO room and a Porthole lobby.
2. The host shares **UDP 7542** from `Davigo.exe`.
3. Every client joins the Porthole lobby and **explicitly approves UDP 7542**. Joining the lobby by itself is not enough.
4. Every client runs `2-Mode-Porthole.bat`, starts Knight Pass, and connects with any four-digit room code.

Porthole Virtual LAN and UDP 7543 forwarding are not required for this setup. The VR host does not need to switch modes.

## Ports

| Protocol | Port | Purpose |
|---|---:|---|
| UDP | 7542 | Fusion gameplay traffic |
| UDP | 7543 | host discovery on a physical LAN only |

## Building from source

The project references assemblies from an installed DAVIGO copy and BepInEx-generated interop assemblies. On Windows with the .NET 6 SDK and initialized BepInEx IL2CPP, run:

```powershell
dotnet build .\DavigoTrueLan.csproj -c Release -p:DavigoDir="C:\Program Files (x86)\Steam\steamapps\common\Davigo"
```

The plugin will be written to `bin/Release/net6.0/DavigoTrueLan.dll`.

## Limitations

- Steam and background game features may still access the Internet; the Fusion gameplay traffic is what bypasses the DAVIGO relay.
- Built-in DAVIGO voice chat is not part of the LAN mod. Use Discord, Steam Voice, or another solution.
- Lobbies containing more than one Giant have not been tested and are not guaranteed to work correctly.
- A DAVIGO update will probably require the native patches to be adapted.
- Grab and Drag Locomotion is a built-in DAVIGO experimental setting and is unrelated to this mod.

This project is not affiliated with the DAVIGO developers. The repository contains no game files.

