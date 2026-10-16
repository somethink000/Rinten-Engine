using Editor;
using Rinten;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Editor.TreeBrowser;

/// <summary>Builds the right-click menus for the tree browser and its icon grid.</summary>
public static class AssetContextMenuHelper
{
    //Builds the right-click menu for a single file/asset. Shared by the tree view and the icon grid
    //so both show identical options. Rename UI differs per view, so it is passed in via onRename.
    public static void BuildFileMenu(Menu menu, string fullPath, Asset asset, Action onRename, Action onChanged,
        Rinten.UI.Panel owner = null)
    {
        var fileName = Path.GetFileName(fullPath);

        RunAssetContextMenu(menu, new List<AssetEntry> { EntryFor(fullPath, asset) });

        menu.AddSeparator();

        menu.AddOption("Rename", "edit", () => onRename?.Invoke());
        menu.AddOption("Duplicate", "file_copy", () =>
        {
            DuplicateFileWithRename(fullPath, () => onChanged?.Invoke());
        });

        menu.AddSeparator();

        var parentFolder = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parentFolder))
        {
            //Named the same as the folder menu's, which is the same command asked of the folder this file is in.
            var createMenu = menu.AddMenu("New", "note_add");
            CreateAsset.AddOptions(createMenu, parentFolder);
            menu.AddSeparator();
        }

        menu.AddOption("Delete", "delete", () =>
        {
            Confirm(owner, "Delete File", $"Are you sure you want to delete '{fileName}'?", () =>
            {
                try
                {
                    DeleteFileWithCompiled(fullPath, asset);
                    onChanged?.Invoke();
                }
                catch (Exception ex)
                {
                    Log.Error($"Failed to delete file: {ex.Message}");
                }
            });
        });
    }

    //A menu entry for a file, which is not always an asset - code and data files are not.
    private static AssetEntry EntryFor(string fullPath, Asset asset)
    {
        asset ??= Resolve(fullPath);

        return asset is not null ? new AssetEntry(asset) : new AssetEntry(new FileInfo(fullPath), null);
    }

    //Looks the asset up now, since rows built before a file was scanned hold no asset
    private static Asset Resolve(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
            return null;

        if (AssetSystem.FindByPath(fullPath) is { } found)
            return found;

        //Only what the editor has a type for. A readme is not an asset and registering one would only put it in the list as nothing.
        if (AssetType.FromExtension(Path.GetExtension(fullPath)) is null)
            return null;

        try
        {
            return AssetSystem.RegisterFile(fullPath);
        }
        catch (Exception ex)
        {
            Log.Warning($"Could not register {fullPath}: {ex.Message}");
            return null;
        }
    }

    //Every asset menu option hangs off this one event, so an addon's options show here too
    private static void RunAssetContextMenu(Menu menu, List<AssetEntry> entries)
    {
        EditorEvent.Run("asset.contextmenu", new AssetContextMenu
        {
            SelectedList = entries,
            ScreenPosition = global::Editor.Application.CursorPosition,
            Menu = menu
        });
    }

    //The same for a folder. ThisFolder is false because this menu is always opened on a folder itself - it
    //means the empty space inside the one being looked at, which is what the recursive options are kept off.
    private static void RunFolderContextMenu(Menu menu, string fullPath)
    {
        EditorEvent.Run("folder.contextmenu", new FolderContextMenu
        {
            Target = new DirectoryInfo(fullPath),
            ScreenPosition = global::Editor.Application.CursorPosition,
            Menu = menu,
            ThisFolder = false,
            Context = null
        });
    }

    //Deletes files and folders after asking, over the browser that asked. The Delete key in the grid and in the tree both come through
    //here, so they ask the same question and remove the same things - a file with its compiled copy, a folder with what is in it.
    public static void DeletePaths(Rinten.UI.Panel owner, IReadOnlyList<string> paths, Action onChanged)
    {
        var chosen = (paths ?? Array.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p) && (File.Exists(p) || Directory.Exists(p)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (chosen.Count == 0) return;

        var what = chosen.Count == 1 ? $"'{Path.GetFileName(chosen[0])}'" : $"{chosen.Count} item(s)";

        Confirm(owner, chosen.Count == 1 ? "Delete File" : "Delete Files", $"Are you sure you want to delete {what}?", () =>
        {
            foreach (var path in chosen)
            {
                try
                {
                    if (Directory.Exists(path)) Directory.Delete(path, true);
                    else DeleteFileWithCompiled(path, AssetSystem.FindByPath(path));
                }
                catch (Exception ex)
                {
                    Log.Error($"Failed to delete '{path}': {ex.Message}");
                }
            }

            onChanged?.Invoke();
        });
    }

    //Context menu for empty space in a folder view, shared by grid and tree; creates into the folder
    public static void BuildEmptySpaceMenu(Menu menu, string folder, Action onRefresh)
    {
        var create = menu.AddMenu("Create", "add");
        CreateAsset.AddOptions(create, folder);

        if (WindowsClipboard.HasFiles())
        {
            menu.AddOption("Paste", "content_paste", () =>
            {
                PasteFromClipboard(folder);
                onRefresh?.Invoke();
            });
        }

        menu.AddSeparator();
        menu.AddOption("Open in Explorer", "folder_open", () => EditorUtility.OpenFolder(folder));
        menu.AddOption("Refresh", "refresh", () => onRefresh?.Invoke());
    }

    //Builds the right-click menu for a folder. Shared by the tree view and the icon grid.
    public static void BuildFolderMenu(Menu menu, string fullPath, string displayName, bool isRoot,
        Action onOpen, Action onRename, Action onRefresh, Action onDeleted, Rinten.UI.Panel owner = null)
    {
        if (onOpen != null)
            menu.AddOption("Open", "folder_open", () => onOpen());

        //New, the recursive rebuilds, Show in Explorer, the folder's metadata - and whatever an
        //addon adds - all hang off this, the same way the file menu hangs off asset.contextmenu.
        RunFolderContextMenu(menu, fullPath);

        //Paste files copied from Windows Explorer into this folder
        if (WindowsClipboard.HasFiles())
        {
            menu.AddOption("Paste", "content_paste", () =>
            {
                PasteFromClipboard(fullPath);
                onRefresh?.Invoke();
            });
        }

        menu.AddSeparator();

        if (!isRoot)
            menu.AddOption("Rename", "edit", () => onRename?.Invoke());

        menu.AddOption("Copy Path", "content_copy", () => EditorUtility.Clipboard.Copy(fullPath));
        menu.AddOption("Copy Relative Path", "content_copy", () =>
        {
            var relativePath = Path.GetRelativePath(Project.Current?.GetRootPath() ?? "", fullPath);
            EditorUtility.Clipboard.Copy(relativePath);
        });

        menu.AddSeparator();

        menu.AddOption("Refresh", "refresh", () => onRefresh?.Invoke());

        if (!isRoot)
        {
            menu.AddSeparator();
            menu.AddOption("Delete", "delete", () =>
            {
                Confirm(owner, "Delete Folder",
                    $"Are you sure you want to delete '{displayName}'?\nEverything in it goes with it.", () =>
                {
                    try
                    {
                        Directory.Delete(fullPath, recursive: true);
                        onDeleted?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Failed to delete folder: {ex.Message}");
                    }
                });
            });
        }
    }

    //Duplicates a file next to itself, finding a free "_copy" name.
    public static string DuplicateFile(string filePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            var nameWithoutExt = Path.GetFileNameWithoutExtension(filePath);
            var extension = Path.GetExtension(filePath);

            var newName = $"{nameWithoutExt}_copy{extension}";
            var newPath = Path.Combine(directory, newName);

            var counter = 1;
            while (File.Exists(newPath))
            {
                newName = $"{nameWithoutExt}_copy{counter++}{extension}";
                newPath = Path.Combine(directory, newName);
            }

            File.Copy(filePath, newPath);

            //Register the new file so it gets compiled and recognised as an asset immediately
            //(without this it shows up uncompiled until an editor restart or rename).
            AssetSystem.RegisterFile(newPath);

            return newPath;
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to duplicate file: {ex.Message}");
            return null;
        }
    }

    //Duplicates a file and immediately opens a rename modal pre-filled with the copy's name.
    public static void DuplicateFileWithRename(string filePath, Action onComplete = null)
    {
        var newPath = DuplicateFile(filePath);
        if (string.IsNullOrEmpty(newPath))
            return;

        onComplete?.Invoke();

        var extension = Path.GetExtension(newPath);
        EditorModal.Prompt(null, "Rename Duplicate", null, newName =>
        {
            if (string.IsNullOrWhiteSpace(newName))
                return;

            //Preserve extension if the user didn't type one
            if (!Path.HasExtension(newName))
                newName += extension;

            if (newName == Path.GetFileName(newPath))
                return;

            var renamedPath = Path.Combine(Path.GetDirectoryName(newPath), newName);
            if (File.Exists(renamedPath))
            {
                Log.Warning($"A file named '{newName}' already exists");
                return;
            }

            try
            {
                File.Move(newPath, renamedPath);

                //Drop the freshly compiled _c file so it doesn't linger under the old name
                var oldCompiled = newPath + "_c";
                if (File.Exists(oldCompiled))
                    File.Delete(oldCompiled);

                AssetSystem.RegisterFile(renamedPath);
                onComplete?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to rename duplicate: {ex.Message}");
            }
        }, okay: "Rename", initial: Path.GetFileNameWithoutExtension(newPath), icon: "file_copy");
    }

    //Registers a freshly created/copied/moved path with the asset system so it compiles right
    //away. Accepts a single file or a directory (registers every file inside, recursively).
    public static void RegisterNewPath(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    if (ShouldRegister(file))
                        AssetSystem.RegisterFile(file);
                }
            }
            else if (File.Exists(path) && ShouldRegister(path))
            {
                AssetSystem.RegisterFile(path);
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"Failed to register '{path}': {ex.Message}");
        }
    }

    //Copies the files currently on the Windows clipboard into the target folder (always copy, never move).
    public static void PasteFromClipboard(string targetFolder)
    {
        //Clipboard files come from Explorer, so this is always a copy in
        AssetMover.RunAndReport(WindowsClipboard.GetFiles(), targetFolder, copy: true);
    }

    //True when the path lives outside the current project (e.g. dragged in from Windows Explorer).
    //Such sources must be copied, never moved, so we don't delete files from their original location.
    public static bool IsExternalSource(string path)
    {
        try
        {
            var root = Project.Current?.GetRootPath();
            if (string.IsNullOrEmpty(root))
                return false;

            var full = Path.GetFullPath(path);
            var rootFull = Path.GetFullPath(root);
            return !full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    //Create a prefab from GameObject(s) dragged in from the scene hierarchy. A single object becomes the prefab
    //root directly; multiple objects are wrapped under a new root, mirroring the engine's "Convert to Prefab".
    public static void CreatePrefabFromGameObjects(GameObject[] gameObjects, string targetFolder, Action onComplete = null)
    {
        if (gameObjects == null || string.IsNullOrEmpty(targetFolder)) return;

        var selection = gameObjects.Where(g => g != null).ToArray();
        var first = selection.FirstOrDefault();
        if (first == null) return;

        try
        {
            var session = SceneEditorSession.Resolve(first);
            if (session == null) return;

            var baseName = string.IsNullOrWhiteSpace(first.Name) ? "Prefab" : first.Name;
            var location = GetUniquePrefabPath(targetFolder, baseName);

            using var scene = session.Scene.Push();
            using (session.UndoScope("Create Prefab from Hierarchy").WithGameObjectChanges(selection, GameObjectUndoFlags.All).WithGameObjectCreations().Push())
            {
                GameObject prefabRoot;

                if (selection.Length == 1)
                {
                    prefabRoot = first;
                }
                else
                {
                    prefabRoot = new GameObject();
                    prefabRoot.WorldTransform = first.WorldTransform;

                    foreach (var go in selection)
                        go.SetParent(prefabRoot, true);
                }

                prefabRoot.Name = Path.GetFileNameWithoutExtension(location);

                EditorUtility.Prefabs.ConvertGameObjectToPrefab(prefabRoot, location);
                EditorUtility.InspectorObject = prefabRoot;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to create prefab: {ex.Message}");
        }

        onComplete?.Invoke();
    }

    //Build a non-colliding "name.prefab" path inside the target folder.
    private static string GetUniquePrefabPath(string folder, string baseName)
    {
        var path = Path.Combine(folder, $"{baseName}.prefab");
        int n = 1;
        while (File.Exists(path) || AssetSystem.FindByPath(path) != null)
        {
            path = Path.Combine(folder, $"{baseName} ({n}).prefab");
            n++;
        }
        return path;
    }

    private static bool ShouldRegister(string file)
    {
        var name = Path.GetFileName(file);
        if (name.StartsWith(".")) return false;
        if (name.EndsWith("_c", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Contains(".generated", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    //Deletes a file, also removing its compiled "_c" sibling. Uses Asset.Delete when registered.
    public static void DeleteFileWithCompiled(string fullPath, Asset asset)
    {
        var compiledPath = fullPath + "_c";

        if (asset != null)
        {
            asset.Delete();

            //The asset's own delete takes its compiled file too, but to this core that is the source itself - a "_c" from the old
            //pipeline is left behind, and the browser hides it while the source is beside it, so nobody can delete it afterwards either.
            if (File.Exists(compiledPath))
                EditorUtility.SendToRecycleBin(compiledPath);

            return;
        }

        File.Delete(fullPath);

        if (File.Exists(compiledPath))
        {
            File.Delete(compiledPath);
        }
    }

    //Builds the right-click menu shown when several files are selected at once. Shared by the tree view and the icon grid so both behave identically.
    public static void BuildMultiFileMenu(Menu menu, List<(string Path, Asset Asset)> items, Action onChanged,
        Rinten.UI.Panel owner = null)
    {
        if (items == null || items.Count == 0) return;

        int count = items.Count;

        RunAssetContextMenu(menu, items.Select(i => EntryFor(i.Path, i.Asset)).ToList());

        menu.AddSeparator();

        menu.AddOption($"Duplicate ({count})", "file_copy", () =>
        {
            foreach (var it in items)
                DuplicateFile(it.Path);
            onChanged?.Invoke();
        });

        menu.AddOption($"Delete ({count})", "delete", () =>
        {
            Confirm(owner, "Delete Files", $"Are you sure you want to delete {count} item(s)?", () =>
            {
                foreach (var it in items)
                {
                    try
                    {
                        DeleteFileWithCompiled(it.Path, it.Asset);
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Failed to delete '{it.Path}': {ex.Message}");
                    }
                }

                onChanged?.Invoke();
            });
        });
    }

    /// <summary>
    /// The question a delete asks, over the browser it was asked in.
    /// </summary>
    /// <remarks>
    /// A panel on the window's overlay rather than a second native window: it
    /// covers the editor and nothing else, it is written in razor and scss like
    /// the rest of the interface, and it measures itself on the editor's own
    /// spacing step - see EditorModal.razor.scss.
    /// <para>
    /// <paramref name="owner"/> is any panel in the window that asked, which is
    /// how the overlay is found. Without one there is no window to put the
    /// question in, so the delete is refused rather than done unasked - a menu
    /// built from nowhere is a bug, not a licence to remove files.
    /// </para>
    /// </remarks>
    private static void Confirm(Rinten.UI.Panel owner, string title, string message, Action onConfirm)
    {
        if (owner is null)
        {
            Log.Warning($"{title}: no window to ask in, so nothing was deleted.");
            return;
        }

        EditorModal.Confirm(owner, title, message, onConfirm, "Delete", "Cancel", destructive: true);
    }
}
