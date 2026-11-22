@echo off
rem Builds the engine on Windows: the native core, then the managed engine, then
rem the shaders.
rem
rem Needs the .NET 11 SDK, and Visual Studio with the "Desktop development with
rem C++" workload for the core (game\bin\win64\rintencore.dll). --skip-native
rem keeps the one an earlier build put there.

dotnet run --project .\engine\Tools\RintenBuild\RintenBuild.csproj -- build %* || exit /b 1
dotnet run --project .\engine\Tools\RintenBuild\RintenBuild.csproj -- build-shaders
