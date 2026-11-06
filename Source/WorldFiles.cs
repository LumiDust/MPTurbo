using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>Save-file transport for the shared colony: chunked hashing plus a stable temp location.</summary>
internal static class WorldFiles
{
    public static SnapshotManifest Describe(byte[] data) => SnapshotBuilder.Describe(data);

    public static string Directory => Path.Combine(GenFilePaths.SaveDataFolderPath, "TurboTickShared");

    /// <summary>
    /// Saves the running colony and returns the file it actually produced. GameDataSaveLoader.SaveGame
    /// takes a save *name*, not a path, so the path has to be resolved through GenFilePaths afterwards.
    /// </summary>
    public static string SaveCurrentGame()
    {
        var name = "TurboTickCheckpoint_" + DateTime.UtcNow.Ticks;
        using (SharedSnapshotStorage.Enter()) GameDataSaveLoader.SaveGame(name);
        var path = GenFilePaths.FilePathForSavedGame(name);
        if (!File.Exists(path)) throw new FileNotFoundException("保存后找不到存档文件：" + path);
        return path;
    }

    public static string DownloadPath(string id)
    {
        System.IO.Directory.CreateDirectory(Directory);
        return Path.Combine(Directory, id + ".rws");
    }

    /// <summary>
    /// Moves a downloaded save into the game's Saves folder and returns its save name.
    /// LoadGame resolves names under Saves/, so a file elsewhere is not loadable even with a full path.
    /// </summary>
    public static string PrepareLoad(string downloadedPath)
    {
        var name = Path.GetFileNameWithoutExtension(downloadedPath);
        var target = GenFilePaths.FilePathForSavedGame(name);
        if (!string.Equals(Path.GetFullPath(target), Path.GetFullPath(downloadedPath), StringComparison.OrdinalIgnoreCase))
            File.Copy(downloadedPath, target, overwrite: true);
        return name;
    }
}
