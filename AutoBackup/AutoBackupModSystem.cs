using System.Collections.Generic;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using System.Threading;

#nullable enable

namespace AutoBackup
{

    public class Debouncer
    {
        private readonly int milliseconds;
        private DateTime? lastEventTime;

        public Debouncer(int milliseconds)
        {
            this.milliseconds = milliseconds;
        }

        public void debounced(Action action)
        {
            var now = DateTime.UtcNow;

            // Check if enough time has passed since the last event
            if (lastEventTime == null || (now - lastEventTime.Value).TotalMilliseconds > milliseconds)
            {
                action();
                lastEventTime = now;
            }
        }
    }

    public class AutoBackupModSystem : ModSystem
    {
        private const string BACKUP_FILE_DATETIME_FORMAT = "yyyy-MM-dd_HH-mm-ss";

        private string? saveFilePath;
        private FileSystemWatcher? watcher;
        private Debouncer? debouncer;

        public override void StartServerSide(ICoreServerAPI api)
        {
            saveFilePath = api.WorldManager.CurrentWorldName;

            watcher = new FileSystemWatcher(GamePaths.Saves)
            {
                EnableRaisingEvents = true,
                Filter = Path.GetFileName(saveFilePath)
            };
            debouncer = new Debouncer(5000);

            Mod.Logger.Notification($"Monitoring save file {saveFilePath}");

            // Wrap the event handler with debounce  
            watcher.Changed += (object sender, FileSystemEventArgs e) => debouncer.debounced(() => OnSaveFileChanged(sender, e));
        }

        private void OnSaveFileChanged(object sender, FileSystemEventArgs e)
        {
            // eg: "peaceful adventure world-autobackup-2025-05-13_18-42-38.vcdbs"
            var nowString = File.GetLastWriteTime(e.FullPath).ToString(BACKUP_FILE_DATETIME_FORMAT);
            var backupFileName = e.Name!.Replace(".vcdbs", $"-autobackup-{nowString}.vcdbs");
            var backupFilePath = Path.Join(GamePaths.BackupSaves, backupFileName);

            File.Copy(e.FullPath, backupFilePath, overwrite: true);
            Mod.Logger.Notification($"Successfully backed up {e.FullPath} to {backupFilePath}");

            ApplyBackupRetentionPolicy(e.Name!);
        }

        private void ApplyBackupRetentionPolicy(string saveFileName)
        {
            Mod.Logger.Notification($"Applying Retention Policy...");
            var backupFilesRemaining = Directory.GetFiles(GamePaths.BackupSaves, $"{Path.GetFileNameWithoutExtension(saveFileName)}-autobackup-*.vcdbs")
                .Select(path => new { path, Timestamp = GetBackupTimestamp(path) })
                .OrderBy(x => x.Timestamp)
                .ToHashSet();


            Mod.Logger.Notification($"Detected {backupFilesRemaining.Count} existing backups");

            // Always keep the most recent backup
            var mostRecent = backupFilesRemaining.LastOrDefault();
            if (mostRecent != null)
            {
                backupFilesRemaining.Remove(mostRecent);
                Mod.Logger.Notification($"Keeping most recent backup file {mostRecent.path}");
            }

            var now = DateTime.Now;
            var retentionPeriods = new[] {
                // eg. keep the oldest file less than 3 days old, next oldest less than 1 day old, etc...
                // TODO: make configurable
                TimeSpan.FromDays(3),
                TimeSpan.FromDays(1),
                TimeSpan.FromHours(3),
                TimeSpan.FromHours(1),
                TimeSpan.FromMinutes(20),
                TimeSpan.FromMinutes(15),
                TimeSpan.FromMinutes(10),
                TimeSpan.FromMinutes(5),
            };

            foreach (var period in retentionPeriods)
            {
                var periodStart = now - period;
                // keep the oldest file that satisfies the given period
                var fileInPeriod = backupFilesRemaining
                    .FirstOrDefault(x => x.Timestamp + period >= now);

                if (fileInPeriod != null)
                {
                    backupFilesRemaining.Remove(fileInPeriod);
                    Mod.Logger.Notification($"Keeping backup file {fileInPeriod.path} for policy period {period}");
                }
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

        public override void Dispose()
        {
            watcher?.Dispose();
            Mod.Logger.Notification("Disposed");
            base.Dispose();
        }
    }
}
