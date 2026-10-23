# AirWave

Play your Windows PC's sound on AirPlay 2 speakers – one speaker or several at once, all kept in time with each other.

- **Everything the PC plays** – music apps, browsers, games, videos. AirWave captures the PC's own output, so there's nothing to set up in each app.
- **Multi-room in sync** – pick as many speakers as you like. They share one clock (PTP, the same timing Apple devices use), so rooms don't echo. Speakers can join or leave while music is playing.
- **Per-speaker and overall volume**, plus an option to mute the PC itself while streaming.
- **Speaker buttons work** – tapping play/pause or skip on a speaker controls whatever is playing on the PC.
- **AirPlay to this PC** – your PC shows up as an AirPlay speaker on iPhones, iPads and Macs. Play on the PC itself, or pass it on to your AirPlay speakers.
- **Mirror your screen to Apple TV** – your desktop (and its sound) on the TV. Pair once with the code the TV shows.
- **Light** – sits in the notification area by the clock, no background services, no account, no ads.

<p align="center">
  <img src="https://raw.githubusercontent.com/AggyMarmite/AirWave/screenshots/02-speakers.png" width="300" alt="Speakers">
  &nbsp;
  <img src="https://raw.githubusercontent.com/AggyMarmite/AirWave/screenshots/04-settings.png" width="300" alt="Settings">
</p>

Works with HomePod and HomePod mini, AirPlay 2 Sonos speakers, Apple TV and other AirPlay 2 receivers.

## Install

Download **`AirWave-Setup-x.y.z.exe`** from the [latest release](../../releases/latest) and run it. It installs just for you (no admin needed) and adds AirWave to the Start menu. Prefer no installer? The `portable` .exe runs on its own.

Windows will ask to let AirWave through the firewall the first time – allow it on **private networks**, otherwise speakers can't reach it and iPhones can't play to it.

## Using it

1. Open AirWave. Speakers on your network show up by themselves.
2. Switch on the speakers you want. Sound starts on all of them together.
3. Use each speaker's slider, or **All speakers**, for volume.

If a speaker doesn't appear, use **Add a speaker** and type its IP address.

### Playing from an iPhone, iPad or Mac

Open Control Centre (or the AirPlay button in any app) and pick your PC – it's listed under its name in AirWave's settings. In **Settings → AirPlay to this PC** you can rename it, turn it off, or choose whether it plays on the PC or on your AirPlay speakers.

### Mirroring your screen

Apple TVs (and Macs that accept AirPlay) appear under **Mirror your screen**. Press **Mirror** – the first time, the TV shows a 4-digit code to type into AirWave; after that it just connects. Press **Stop** to end it.

With the graphics card encoder on (the default), the screen is captured and encoded on the graphics card (NVIDIA, AMD or Intel) at full resolution – up to what the TV shows – and 60 fps. If the PC has no usable encoder, AirWave switches to the standard encoder (up to 1080p at 30 fps) by itself. Hover over the status line while mirroring to see which is in use.

### Settings

| Setting | What it does |
|---|---|
| Mute this PC while streaming | PC speakers go quiet while AirWave is playing; volume comes back when you stop |
| Delay | How far ahead sound is sent (default 2 s, like Apple devices). Longer copes better with weak Wi-Fi |
| Reconnect my speakers when AirWave opens | Turns your last speakers back on at launch |
| Start with Windows | Starts in the notification area |
| Use the graphics card encoder | Screen mirroring at full resolution and 60 fps (on by default). Turn off to use the standard encoder |
| Use older timing (NTP) | Troubleshooting only, for a speaker that connects but stays silent |

## Good to know

- AirPlay always plays a short time behind the source, so video on the PC will be ahead of the speakers. That's how the protocol works, not a fault.
- Speakers with a password or PIN aren't supported yet.
- When passing iPhone audio on to your speakers, it plays a little behind the phone – fine for music, not for video on the phone.

## Building from source

Needs the .NET 8 SDK on Windows:

```
dotnet build AirWave.sln -c Release
```

`tests/e2e/run.sh` (Linux, root) streams from the engine to two open-source AirPlay 2 receivers and checks they stay in sync.

## Licence

GPL-3.0. See [LICENSE](LICENSE).
