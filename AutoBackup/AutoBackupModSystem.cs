using System.IO;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

#nullable enable

namespace AutoBackup
{
    public class AutoBackupModSystem : ModSystem
    {

        private ICoreServerAPI? api;

        public override void StartServerSide(ICoreServerAPI api)
        {
            api.Event.GameWorldSave += OnGameWorldSaveStarted;
            this.api = api;
        }

        private void OnGameWorldSaveStarted()
        {
            var saveFileName = Path.GetFileName(api.WorldManager.CurrentWorldName);
            
            using FileSystemWatcher watcher = new FileSystemWatcher(GamePaths.Saves);
            watcher.NotifyFilter = NotifyFilters.FileName;
            watcher.Filter = saveFileName;

            Mod.Logger.Notification($"Waiting for save game to complete: {api.WorldManager.CurrentWorldName}");
            watcher.WaitForChanged(WatcherChangeTypes.Changed, 60_000); // 1 minute timeout
            Mod.Logger.Notification("New Game Save Detected Successfully. Backing up saved game.");

            // eg: "
            var nowString = System.DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var backupFileName = saveFileName.Replace(".vcdbs", $"-bkp-{nowString}.vcdbs");
            var backupFilePath = Path.Join(GamePaths.BackupSaves, backupFileName);

            File.Copy(api.WorldManager.CurrentWorldName, backupFilePath);
            Mod.Logger.Notification($"Successfully copied file to backup path {backupFilePath}");
        }
    }
}
