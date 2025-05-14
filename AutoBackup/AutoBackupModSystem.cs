using System.Collections.Generic;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

#nullable enable

namespace AutoBackup
{
    public class AutoBackupModSystem : ModSystem
    {
        private const string BACKUP_FILE_DATETIME_FORMAT = "yyyy-MM-dd_HH-mm-ss";

        private ICoreServerAPI? api;

        public override void StartServerSide(ICoreServerAPI api)
        {
            this.api = api;
            api.Event.GameWorldSave += () => new Task(WaitForSaveThenBackup).Start();
        }

        private void WaitForSaveThenBackup()
        {
            var saveFileName = Path.GetFileName(api.WorldManager.CurrentWorldName);

            using var watcher = new FileSystemWatcher(GamePaths.Saves) { Filter = saveFileName };

            Mod.Logger.Notification($"Waiting for save game to complete: {api.WorldManager.CurrentWorldName}");
            var waitResult = watcher.WaitForChanged(WatcherChangeTypes.Changed, timeout: 60_000); // 1 minute
            if (waitResult.TimedOut)
            {
                throw new Exception("Timed Out waiting for save");
            }
            Mod.Logger.Notification($"New Game Save Detected Successfully. Backing up saved game...");
            

            // eg: "peaceful adventure world-bkp-2025-05-13_18-42-38.vcdbs"
            var nowString = DateTime.Now.ToString(BACKUP_FILE_DATETIME_FORMAT);
            var backupFileName = saveFileName.Replace(".vcdbs", $"-autobackup-{nowString}.vcdbs");
            var backupFilePath = Path.Join(GamePaths.BackupSaves, backupFileName);

            File.Copy(api.WorldManager.CurrentWorldName, backupFilePath);
            Mod.Logger.Notification($"Successfully copied file to backup path {backupFilePath}");

            // Retention policy: Keep specific backups and delete others
            ApplyBackupRetentionPolicy(GamePaths.BackupSaves, saveFileName);
        }

        private void ApplyBackupRetentionPolicy(string backupDirectory, string saveFileName)
        {
            var backupFilesRemaining = Directory.GetFiles(backupDirectory, $"{Path.GetFileNameWithoutExtension(saveFileName)}-autobackup-*.vcdbs")
                .Select(path => new { path, Timestamp = GetBackupTimestamp(path) })
                .OrderBy(x => x.Timestamp)
                .ToHashSet();

            var now = DateTime.Now;
            var retentionPeriods = new[] {
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(10),
                TimeSpan.FromMinutes(15),
                TimeSpan.FromMinutes(20),
                TimeSpan.FromHours(1),
                TimeSpan.FromHours(3),
                TimeSpan.FromDays(1),
                TimeSpan.FromDays(3),
            };

            // go through the retention periods in order, and build a set of the files to keep

            foreach (var period in retentionPeriods)
            {
                var periodStart = now - period;
                var fileInPeriod = backupFilesRemaining
                    .FirstOrDefault(x => x.Timestamp + period >= now);

                if (fileInPeriod != null)
                {
                    backupFilesRemaining.Remove(fileInPeriod);
                    Mod.Logger.Notification($"Keeping backup file {fileInPeriod.path} for policy period {period}");
                }
            }

            // Always keep the most recent backup
            var mostRecent = backupFilesRemaining.LastOrDefault();
            if (mostRecent != null)
            {
                backupFilesRemaining.Remove(mostRecent);
                Mod.Logger.Notification($"Keeping most recent backup file {mostRecent.path}");
            }

            // delete remaining files outside retention policy
            foreach (var file in backupFilesRemaining)
            {
                File.Delete(file.path);
                Mod.Logger.Notification($"Deleted backup outside policy {file.path}");
            }

        }

        private DateTime GetBackupTimestamp(string filePath)
        {
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            var timestampPart = fileName.Split("-autobackup-")[1];
            return DateTime.ParseExact(timestampPart, BACKUP_FILE_DATETIME_FORMAT, null, System.Globalization.DateTimeStyles.None);
        }
    }
}
