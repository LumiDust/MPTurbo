using TurboTick.Networking;
using Verse;

namespace TurboTick;

/// <summary>Save-file transport for the shared colony: chunked hashing plus a stable temp location.</summary>
internal static class WorldFiles
{
    public static SnapshotManifest Describe(byte[] data) => SnapshotBuilder.Describe(data);

    public static string Directory => Path.Combine(GenFilePaths.SaveDataFolderPath, "TurboTickShared");

    public static string SaveCurrentGame()
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = Path.Combine(Directory, "session-" + DateTime.UtcNow.Ticks + ".rws");
        GameDataSaveLoader.SaveGame(path);
        return path;
    }

    public static string DownloadPath(string id)
    {
        System.IO.Directory.CreateDirectory(Directory);
        return Path.Combine(Directory, id + ".rws");
    }
}
