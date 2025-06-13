using System.Collections.Generic;
using System;
using System.IO;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using System.Threading;
using Microsoft.Data.Sqlite;

#nullable enable

namespace AutoBackup
{

    public class Debouncer : IDisposable
    {
        private readonly int milliseconds;
        private Action? latestAction;
        private Timer? timer;
        private readonly object lockObj = new object();

        public Debouncer(int milliseconds)
        {
            this.milliseconds = milliseconds;
        }

        public void Debounced(Action action)
        {
            lock (lockObj)
            {
                latestAction = action;
                timer?.Dispose();
                timer = new Timer(_ =>
                {
                    Action? toRun;
                    lock (lockObj)
                    {
                        toRun = latestAction;
                        latestAction = null;
                        timer?.Dispose();
                        timer = null;
                    }
                    toRun?.Invoke();
                }, null, milliseconds, Timeout.Infinite);
            }
        }

        public void Dispose()
        {
            timer?.Dispose();
        }
    }

    public class AutoBackupConfigJson
    {
        // eg. keep the oldest file less than 3 days old, next oldest less than 1 day old, etc...
        public List<string> RetentionPeriods { get; } = new List<string>
        {
            "3.00:00:00",   // 3 days
            "1.00:00:00",   // 1 day
            "0.12:00:00",   // 12 hours
            "0.06:00:00",   // 6 hours
            "0.03:00:00",   // 3 hours
            "0.01:00:00",   // 1 hour
            "0.00:30:00",   // 30 minutes
            "0.00:15:00",   // 15 minutes
            "0.00:10:00",   // 10 minutes
            "0.00:05:00"    // 5 minutes
        };
    };

    public class AutoBackupConfig
    {
        public List<TimeSpan> RetentionPeriods { get; }

        public AutoBackupConfig(AutoBackupConfigJson config)
        {
            RetentionPeriods = config.RetentionPeriods.Select(TimeSpan.Parse).ToList();
        }
    }

    public class AutoBackupModSystem : ModSystem, IDisposable
    {

        private string? saveFilePath;
        private FileSystemWatcher? watcher;
        private Debouncer? debouncer;
        private AutoBackupConfig? config;


        public override void StartPre(ICoreAPI api)
        {
            const string CONFIG_FILENAME = "AutoBackupConfig.json";
            var json = api.LoadModConfig<AutoBackupConfigJson>(CONFIG_FILENAME);
            if (json == null)
            {
                Mod.Logger.Error("Config file not found, creating a new one...");
                json = new AutoBackupConfigJson();
                api.StoreModConfig(json, CONFIG_FILENAME);
            }
            config = new AutoBackupConfig(json);
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            saveFilePath = api.WorldManager.CurrentWorldName;

            watcher = new FileSystemWatcher(GamePaths.Saves)
            {
                EnableRaisingEvents = true,
                Filter = Path.GetFileName(saveFilePath)
            };
            debouncer = new Debouncer(15_000);

            Mod.Logger.Notification($"Monitoring save file {saveFilePath}");

            // Use new debounce logic
            watcher.Changed += (object sender, FileSystemEventArgs e) =>
            {
                debouncer.Debounced(() =>
                {
                    double elapsedGameSeconds = api.World.Calendar.ElapsedSeconds / api.World.Calendar.SpeedOfTime;
                    OnSaveFileChanged(sender, e, elapsedGameSeconds);
                });
            };
        }

        private void OnSaveFileChanged(object sender, FileSystemEventArgs e, double elapsedGameSeconds)
        {
            var backupTimeString = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var gameSecondsString = ((long)elapsedGameSeconds).ToString();
            var backupFileName = e.Name!.Replace(
                ".vcdbs",
                $"-autobackup-{gameSecondsString}-{backupTimeString}.vcdbs"
            );
            var backupFilePath = Path.Join(GamePaths.BackupSaves, backupFileName);

            using (var source = new SqliteConnection($"Data Source={e.FullPath};Mode=ReadOnly"))
            using (var destination = new SqliteConnection($"Data Source={backupFilePath};Mode=ReadWriteCreate;Pooling=false"))
            {
                destination.Open();
                source.Open();
                source.BackupDatabase(destination);
            }

            Mod.Logger.Notification($"Successfully backed up {e.FullPath} to {backupFilePath}");

            ApplyBackupRetentionPolicy(e.Name!, elapsedGameSeconds);
        }

        private void ApplyBackupRetentionPolicy(string saveFileName, double elapsedGameSeconds)
        {
            Mod.Logger.Notification($"Applying Retention Policy...");
            var backupFilesRemaining = Directory.GetFiles(GamePaths.BackupSaves, $"{Path.GetFileNameWithoutExtension(saveFileName)}-autobackup-*.vcdbs")
                .Select(path => new { path, GameSeconds = GetBackupGameSeconds(path) })
                .OrderBy(x => x.GameSeconds)
                .ToHashSet();

            Mod.Logger.Notification($"Detected {backupFilesRemaining.Count} existing backups");

            // Always keep the most recent backup
            var mostRecent = backupFilesRemaining.LastOrDefault();
            if (mostRecent != null)
            {
                backupFilesRemaining.Remove(mostRecent);
                Mod.Logger.Notification($"Keeping most recent backup file {mostRecent.path}");
            }

            foreach (var period in config!.RetentionPeriods)
            {
                var periodSeconds = period.TotalSeconds;
                var periodStart = elapsedGameSeconds - periodSeconds;
                // keep the oldest file that satisfies the given period
                var fileInPeriod = backupFilesRemaining
                    .FirstOrDefault(x => x.GameSeconds + periodSeconds >= elapsedGameSeconds);

                if (fileInPeriod != null)
                {
                    backupFilesRemaining.Remove(fileInPeriod);
                    Mod.Logger.Notification($"Keeping backup file {fileInPeriod.path} for policy period {period} ({periodSeconds} seconds)");
                }
            }

            // delete remaining files outside retention policy
            foreach (var file in backupFilesRemaining)
            {
                File.Delete(file.path);
                Mod.Logger.Notification($"Deleted backup outside policy {file.path}");
            }
        }

        private long GetBackupGameSeconds(string filePath)
        {
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            var match = System.Text.RegularExpressions.Regex.Match(fileName, @"-autobackup-(\d+)-");
            if (!match.Success)
            {
                throw new FormatException(
                    $"Could not extract game seconds from backup filename: {fileName}.\n" +
                    "Expected format: <savefile>-autobackup-<gameSeconds>-<timestamp>.vcdbs\n" +
                    "Eg. serene kingdom story-autobackup-1234567890-2023-10-01_12-00-00.vcdbs\n" +
                    $"Got: {fileName}"
                );
            }
            return long.Parse(match.Groups[1].Value);
        }

        public override void Dispose()
        {
            watcher?.Dispose();
            debouncer?.Dispose();
            base.Dispose();
        }
    }
}
