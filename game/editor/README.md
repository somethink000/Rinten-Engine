# Editor applications

Each folder here is a **tool project**: a self-contained editor that the engine compiles at startup and
hotloads when its files change. The folder name is the project folder; the code lives in `Code/`; the
`.rinproj` beside it names the project and its ident.

This file is the map. It is written to be read by people and by coding agents.

## The three places an editor can live

| Place | What belongs there | Examples |
|---|---|---|
| `engine/Rinten.Tools` | the editor shell, panels and built-in workspaces | General, Mapping, Console, Profiler |
| `game/addons/tools` (`toolbase`) | shared editor library, and asset editors that need it | Sprite Editor, Hotspot Editor, Sound Editor, Panel Gallery |
| `game/editor/<Name>` | an app with its own project, ident and hotload boundary | Model Editor, FX Editor, Material Editor, UI Editor, Movie Maker |

The rule: if the app is a product of its own, give it a project here. If it is a panel or an asset editor
that spends its life inside `toolbase`, leave it there.

## How an app reaches the screen

1. `[EditorApp("Title", "icon", "description")]` on the editor type.
2. `EditorTypeLibrary` finds the attribute in the loaded assemblies.
3. `WorkspaceStrip` lists it on the tool strip, in `DefaultPinned` order.
4. `WorkspaceArea.OpenEditor` either embeds the `DockWindow`'s docks in a workspace tab, or, for an
   `IAssetEditor` / `IFileEditor`, opens a document workspace with one tab per asset.
5. A `SceneWorkspace` subclass (Mapping, Movie Maker) takes the scene area from General while it is in front.

An editor without `[EditorApp]` is not an app: it opens as a floating window from the old path, or is an
embedded view opened by a static `[EditorForAssetType]` method (Action Graph is the latter).

## Adding an app here

Copy the shape of an existing project:

1. `game/editor/<Name>/Code/` — the source, plus a `<name>.csproj` modelled on `MdlEditor/Code/mdleditor.csproj`.
2. `game/editor/<Name>/.rinproj` — `Type: "tool"`, the same `Ident` as the project file.
3. `engine/Rinten.Engine/Systems/Project/Project/Project.Static.cs` — add
   `AddFromFileBuiltIn( $"editor/<Name>/{Extension}" );` under `Application.IsEditor`.
4. `game/addons/base/base.slnx` — add the `.csproj` to the Tools folder.
5. `engine/Tests/Rinten.Test.Integration/Addon/ToolAddonCompiles.cs` — add a `Compile<Name>` test.
6. `[EditorApp]` and `[EditorForAssetType]` on the editor type.
7. Add the title to `WorkspaceStrip.DefaultPinned` only if it should ship pinned.

## Current apps

| App | Kind | Project | Type |
|---|---|---|---|
| Model Editor | asset documents (`mdl`) | `MdlEditor` | `Editor.MdlEditor.ModelEditorWindow` |
| Animation Graph | asset documents (`mdl`) | `MdlEditor` | `Editor.MdlEditor.Window` |
| FX Editor | asset documents (`fx`) | `FxEditor` | `Editor.FxEditor.ParticleEditorWindow` |
| Material Editor | asset documents (`mat`) | `MaterialEditor` | `Editor.Materials.MaterialEditorWindow` |
| UI Editor | file documents (`razor`) | `UiEditor` | `Editor.UiEditorWindow` |
| Movie Maker | scene workspace (`movie`) | `MovieMaker` | `Editor.MovieMaker.MovieMakerWorkspace` |
| Action Graph | embedded opener (`action`) | `ActionGraph` | `Editor.ActionGraphs.ActionGraphView.Open` |

In `toolbase`: Sprite Editor (`sprite`), Hotspot Editor (`rect`), Sound Editor (audio), Panel Gallery.

## Naming

The project, ident and folder use one name; the `[EditorApp]` title is the name on the strip. They are
allowed to differ — `MdlEditor` is "Model Editor" — but new projects should keep them the same.

Do not rename types that are serialized into assets (`RectAssetData`, `ModelDefinition`, the `ModelDoc*`
pages). Editor window classes are free to be renamed.
