<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="brand/linux/rinten-256.png">
    <img src="brand/rotor.svg" width="128" alt="Rinten">
  </picture>
</p>

<h1 align="center">Rinten</h1>

<p align="center">A C# multiplatform game engine</p>

<p align="center">
  <a href="https://discord.gg/VdzBxYbPRb"><img src="https://img.shields.io/badge/Discord-join-5865F2?style=for-the-badge&logo=discord&logoColor=white" alt="Discord"></a>
  <a href="https://www.patreon.com/lonepinedev/membership"><img src="https://img.shields.io/badge/Patreon-support-FF424D?style=for-the-badge&logo=patreon&logoColor=white" alt="Patreon"></a>
</p>

<p align="center">
  <sub><b>Editor</b>: Windows · Linux &nbsp;|&nbsp; <b>Games</b>: Windows · Linux · Steam Frame · Android · Web &nbsp;|&nbsp; <b>VR</b>: OpenXR · WebXR</sub>
</p>

## Get it

Download the editor from [Releases](../../releases). Its launcher installs, switches and removes engine versions after that.

## Build it

You need the .NET 10 SDK. On Linux also cmake, ninja, clang and the X11/Wayland headers.

```
./Bootstrap.sh      # Linux
Bootstrap.bat       # Windows
```

The editor lands in `game/`. After the first build, `./build.sh` rebuilds without starting over.

## Inside

- `engine/` - the engine, the editor and the build tools, in C#
- `engine/Core/` - a small native core: physics, audio, model import
- `game/addons/` - the editor's tools and the base library games are written against, also C#

## License

MIT, see [LICENSE.md](LICENSE.md). Third-party parts keep their own licences, listed in `game/thirdpartylegalnotices`.
