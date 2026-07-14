using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Api.Controllers.UpdateConfig;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Config;

public sealed record SettingsSyncStatus(
    bool Enabled,
    bool Healthy,
    string Path,
    string? Error,
    long Revision,
    bool Dirty);

public sealed record SettingsMutationResult(long Revision, string? Warning = null);

public sealed class StaleSettingsRevisionException(long expected, long actual)
    : Exception($"Settings changed after this page was loaded (expected revision {expected}, current revision {actual}).")
{
    public long Expected { get; } = expected;
    public long Actual { get; } = actual;
}

public sealed class SettingsCoordinator(
    ConfigManager configManager,
    WardenStore warden,
    WebsocketManager websocketManager) : IHostedService, IDisposable
{
    private const string RevisionKey = "settings.sync.revision";
    private const string HashKey = "settings.sync.last-hash";
    private static readonly UnixFileMode RequiredMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly SemaphoreSlim _mutationLock = new(1, 1);
    private readonly SettingsYamlCodec _codec = new();
    private readonly string _path = Path.GetFullPath(
        EnvironmentUtil.GetEnvironmentVariable("SETTINGS_FILE_PATH")
        ?? Path.Join(DavDatabaseContext.ConfigPath, "settings.yaml"));
    private readonly bool _enabled = IsTrue(EnvironmentUtil.GetEnvironmentVariable("SETTINGS_FILE_SYNC_ENABLED"));
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _debounce;
    private string? _lastHash;
    private long _revision;
    private bool _operational;
    private bool _healthy = true;
    private bool _dirty;
    private bool _preserveInvalidFile;
    private string? _error;
    private bool _disposed;

    public SettingsSyncStatus Status
    {
        get
        {
            lock (this)
                return new SettingsSyncStatus(_enabled, _healthy, _path, _error, _revision, _dirty);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await LoadMetadataAsync(cancellationToken).ConfigureAwait(false);
        if (!_enabled)
        {
            lock (this)
            {
                _healthy = true;
                _error = null;
            }
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            EnsureNotSymlink();
            if (File.Exists(_path)) EnsureSecureRegularFile();
            _operational = true;
            StartWatcher();
            await _mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!File.Exists(_path)) await ExportCurrentLockedAsync(cancellationToken).ConfigureAwait(false);
                else
                {
                    var content = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
                    var hash = Hash(content);
                    if (!string.Equals(hash, _lastHash, StringComparison.Ordinal))
                        await ImportLockedAsync(content, hash, "startup", cancellationToken).ConfigureAwait(false);
                    else MarkHealthy();
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                if (e is UnauthorizedAccessException) _operational = false;
                MarkUnhealthy(e.Message,
                    preserveInvalidFile: e is SettingsYamlException or BadHttpRequestException);
                Log.Error("Settings file synchronization startup failed: {Error}", e.Message);
            }
            finally
            {
                _mutationLock.Release();
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _operational = false;
            MarkUnhealthy($"Settings synchronization disabled: {e.Message}");
            Log.Error("Settings file synchronization disabled: {Error}", e.Message);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _watcher?.Dispose();
        _watcher = null;
        _debounce?.Cancel();
        return Task.CompletedTask;
    }

    public async Task<SettingsMutationResult> UpdateFromApiAsync(
        IReadOnlyList<ConfigItem> requested,
        IReadOnlyList<string> resetKeys,
        long baseRevision,
        CancellationToken cancellationToken)
    {
        await _mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (baseRevision != _revision)
                throw new StaleSettingsRevisionException(baseRevision, _revision);

            var changes = requested
                .GroupBy(x => x.ConfigName, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => (string?)x.Last().ConfigValue, StringComparer.Ordinal);
            foreach (var key in resetKeys)
            {
                if (!SettingsSchema.ByKey.TryGetValue(key, out var definition)
                    || definition.EnvironmentFallback is null)
                    throw new BadHttpRequestException($"{key} cannot be reset to an environment fallback.");
                if (changes.ContainsKey(key))
                    throw new BadHttpRequestException($"{key} cannot be updated and reset in the same request.");
                changes[key] = null;
            }
            var stored = await ReadStoredSettingsAsync(cancellationToken).ConfigureAwait(false);
            ValidateCompleteSnapshot(stored, changes);
            await PersistChangesLockedAsync(changes, cancellationToken).ConfigureAwait(false);
            configManager.ApplyChanges(changes);

            string? warning = null;
            if (_enabled)
            {
                if (!_operational)
                    warning = _error ?? "Settings file synchronization is disabled.";
                else if (_preserveInvalidFile)
                {
                    _dirty = true;
                    warning = "SQLite was updated, but the invalid settings.yaml was preserved for correction.";
                }
                else
                {
                    try
                    {
                        await ExportCurrentLockedAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        _dirty = true;
                        warning = $"SQLite was updated, but settings.yaml could not be written: {e.Message}";
                        MarkUnhealthy(warning);
                    }
                }
            }

            await PublishChangedAsync("api").ConfigureAwait(false);
            return new SettingsMutationResult(_revision, warning);
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    public async Task RebuildFileAsync(CancellationToken cancellationToken)
    {
        if (!_enabled) throw new InvalidOperationException("Settings file synchronization is not enabled.");
        await _mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _preserveInvalidFile = false;
            _operational = true;
            await ExportCurrentLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    public async Task<T> ApplyOutOfBandChangeAsync<T>(
        string source, Func<T> mutation, CancellationToken cancellationToken)
    {
        await _mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = mutation();
            await PersistChangesLockedAsync(
                new Dictionary<string, string?>(), cancellationToken).ConfigureAwait(false);
            if (_enabled && _operational && !_preserveInvalidFile)
            {
                try
                {
                    await ExportCurrentLockedAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    MarkUnhealthy($"The change was saved, but settings.yaml could not be written: {e.Message}");
                }
            }
            else if (_enabled)
            {
                _dirty = true;
            }
            await PublishChangedAsync(source).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    private async Task ImportLockedAsync(
        string content, string hash, string source, CancellationToken cancellationToken)
    {
        EnsureSecureRegularFile();
        var imported = _codec.Deserialize(content);
        if (imported.FileRevision != _revision)
            throw new BadHttpRequestException(
                $"settings.yaml is based on revision {imported.FileRevision}; current revision is {_revision}. " +
                "Reload or rebuild the file before applying this edit.");
        var stored = await ReadStoredSettingsAsync(cancellationToken).ConfigureAwait(false);
        ValidateCompleteSnapshot(stored, imported.Values);

        // Warden has a separate SQLite database; apply its fully validated
        // projection immediately before committing the normal settings.
        warden.ReplaceRemoteSources(imported.Extras.RemoteSources);
        var backup = imported.Extras.Backup;
        warden.SaveBackupSettings(backup.Enabled, backup.Repository, backup.Path, backup.Branch,
            backup.Scope, backup.IntervalHours, backup.Token, replaceToken: true);

        var changes = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in imported.Values)
            if (!stored.TryGetValue(key, out var oldValue) || value != oldValue)
                changes[key] = value;
        foreach (var key in stored.Keys.Where(SettingsRegistry.Defaults.ContainsKey))
            if (!imported.Values.ContainsKey(key)) changes[key] = null;

        await PersistChangesLockedAsync(changes, cancellationToken).ConfigureAwait(false);
        if (changes.Count > 0) configManager.ApplyChanges(changes);
        _lastHash = hash;
        _preserveInvalidFile = false;
        await ExportCurrentLockedAsync(cancellationToken).ConfigureAwait(false);
        await PublishChangedAsync(source).ConfigureAwait(false);
    }

    private async Task PersistChangesLockedAsync(
        IReadOnlyDictionary<string, string?> changes, CancellationToken cancellationToken)
    {
        await using var db = new DavDatabaseContext();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (key, value) in changes)
        {
            var item = await db.ConfigItems.FindAsync([key], cancellationToken).ConfigureAwait(false);
            if (value is null)
            {
                if (item is not null) db.ConfigItems.Remove(item);
            }
            else if (item is null)
                db.ConfigItems.Add(new ConfigItem { ConfigName = key, ConfigValue = value });
            else item.ConfigValue = value;
        }
        _revision++;
        await UpsertMetadataAsync(db, RevisionKey, _revision.ToString(), cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateCompleteSnapshot(
        IReadOnlyDictionary<string, string> stored,
        IReadOnlyDictionary<string, string?> changes)
    {
        var merged = SettingsSchema.Ordered.ToDictionary(
            x => x.Key,
            x => stored.GetValueOrDefault(x.Key) ?? x.DefaultValue,
            StringComparer.Ordinal);
        foreach (var (key, value) in changes)
        {
            if (!SettingsRegistry.Defaults.ContainsKey(key))
                throw new BadHttpRequestException($"Unknown setting: {key}.");
            merged[key] = value ?? SettingsRegistry.Defaults[key];
        }
        ConfigUpdateValidator.ValidateSnapshot(merged);
    }

    private async Task ExportCurrentLockedAsync(CancellationToken cancellationToken)
    {
        var stored = await ReadStoredSettingsAsync(cancellationToken).ConfigureAwait(false);
        var extras = ReadExtras();
        var content = _codec.Serialize(stored, extras, _revision);
        await WriteAtomicAsync(content, cancellationToken).ConfigureAwait(false);
        _lastHash = Hash(content);
        await SaveHashAsync(_lastHash, cancellationToken).ConfigureAwait(false);
        _dirty = false;
        _preserveInvalidFile = false;
        MarkHealthy();
    }

    private SettingsFileExtras ReadExtras()
    {
        var sources = warden.GetSources()
            .Where(x => x.Id != WardenStore.LocalSourceId && !string.IsNullOrWhiteSpace(x.Url))
            .Select(x => new WardenRemoteSourceSetting(
                x.Name, x.Url!, x.Enabled, x.Trust, x.RefreshHours))
            .ToArray();
        var backup = warden.GetBackupSettings();
        return new SettingsFileExtras(sources, new WardenBackupFileSetting(
            backup.Enabled, backup.Repo, backup.Path, backup.Branch, backup.Scope,
            backup.IntervalHours, warden.GetBackupToken()));
    }

    private async Task<Dictionary<string, string>> ReadStoredSettingsAsync(CancellationToken cancellationToken)
    {
        await using var db = new DavDatabaseContext();
        return await db.ConfigItems.AsNoTracking()
            .Where(x => SettingsRegistry.Defaults.Keys.Contains(x.ConfigName))
            .ToDictionaryAsync(x => x.ConfigName, x => x.ConfigValue, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task LoadMetadataAsync(CancellationToken cancellationToken)
    {
        await using var db = new DavDatabaseContext();
        var metadata = await db.ConfigItems.AsNoTracking()
            .Where(x => x.ConfigName == RevisionKey || x.ConfigName == HashKey)
            .ToDictionaryAsync(x => x.ConfigName, x => x.ConfigValue, cancellationToken)
            .ConfigureAwait(false);
        _revision = long.TryParse(metadata.GetValueOrDefault(RevisionKey), out var revision) ? revision : 0;
        _lastHash = metadata.GetValueOrDefault(HashKey);
    }

    private async Task SaveHashAsync(string hash, CancellationToken cancellationToken)
    {
        await using var db = new DavDatabaseContext();
        await UpsertMetadataAsync(db, HashKey, hash, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertMetadataAsync(
        DavDatabaseContext db, string key, string value, CancellationToken cancellationToken)
    {
        var item = await db.ConfigItems.FindAsync([key], cancellationToken).ConfigureAwait(false);
        if (item is null) db.ConfigItems.Add(new ConfigItem { ConfigName = key, ConfigValue = value });
        else item.ConfigValue = value;
    }

    private async Task WriteAtomicAsync(string content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        EnsureNotSymlink();
        if (File.Exists(_path)) EnsureSecureRegularFile();
        var temp = Path.Join(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.WriteThrough | FileOptions.Asynchronous,
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = RequiredMode;
            await using (var stream = new FileStream(temp, options))
            {
                var bytes = new UTF8Encoding(false).GetBytes(content);
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, RequiredMode);
            File.Move(temp, _path, overwrite: true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_path, RequiredMode);
            EnsureSecureRegularFile();
        }
        finally
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch (IOException)
            {
                // Best-effort cleanup; never expose content in logs.
            }
        }
    }

    private void EnsureSecureRegularFile()
    {
        EnsureNotSymlink();
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(_path);
            if (mode != RequiredMode)
            {
                File.SetUnixFileMode(_path, RequiredMode);
                mode = File.GetUnixFileMode(_path);
            }
            if (mode != RequiredMode)
                throw new UnauthorizedAccessException("settings.yaml permissions could not be restricted to 0600.");
        }
    }

    private void EnsureNotSymlink()
    {
        var info = new FileInfo(_path);
        if (info.LinkTarget is not null)
            throw new IOException("settings.yaml must not be a symbolic link.");
    }

    private void StartWatcher()
    {
        var directory = Path.GetDirectoryName(_path)!;
        _watcher = new FileSystemWatcher(directory, Path.GetFileName(_path))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
                | NotifyFilters.CreationTime | NotifyFilters.Security,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Deleted += OnFileEvent;
        _watcher.Renamed += OnFileEvent;
        _watcher.Error += (_, args) => MarkUnhealthy(
            $"Settings file watcher failed: {args.GetException().Message}");
    }

    private void OnFileEvent(object sender, FileSystemEventArgs args)
    {
        if (!_operational || _disposed) return;
        var next = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _debounce, next);
        previous?.Cancel();
        previous?.Dispose();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300, next.Token).ConfigureAwait(false);
                await ProcessFileEventAsync(next.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
    }

    private async Task ProcessFileEventAsync(CancellationToken cancellationToken)
    {
        await _mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
            {
                _preserveInvalidFile = false;
                await ExportCurrentLockedAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            EnsureSecureRegularFile();
            string content = "";
            Exception? lastError = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    content = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
                    lastError = null;
                    break;
                }
                catch (IOException e)
                {
                    lastError = e;
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
            }
            if (lastError is not null) throw lastError;
            var hash = Hash(content);
            if (string.Equals(hash, _lastHash, StringComparison.Ordinal)) return;
            await ImportLockedAsync(content, hash, "file", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (e is UnauthorizedAccessException) _operational = false;
            MarkUnhealthy(e.Message, preserveInvalidFile: e is SettingsYamlException or BadHttpRequestException);
            Log.Error("settings.yaml edit was rejected: {Error}", e.Message);
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    private Task PublishChangedAsync(string source) => websocketManager.SendMessage(
        WebsocketTopic.SettingsChanged,
        JsonSerializer.Serialize(new { revision = _revision, source }));

    private void MarkHealthy()
    {
        lock (this)
        {
            _healthy = true;
            _error = null;
        }
    }

    private void MarkUnhealthy(string error, bool preserveInvalidFile = false)
    {
        lock (this)
        {
            _healthy = false;
            _error = error;
            _dirty = true;
            _preserveInvalidFile |= preserveInvalidFile;
        }
    }

    private static string Hash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static bool IsTrue(string? value) => value?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watcher?.Dispose();
        _debounce?.Cancel();
        _debounce?.Dispose();
        _mutationLock.Dispose();
    }
}
