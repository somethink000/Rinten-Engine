<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="brand/linux/rinten-256.png">
    <img src="brand/rotor.svg" width="128" alt="Rinten">
  </picture>
</p>

<h1 align="center">Rinten

<p align="center">A C# multiplatform game engine</p>

The editor runs on Windows and Linux. Games export to Windows, Linux, Steam Frame (Linux Arm), Android and the web, and run in VR through OpenXR or WebXR.

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
