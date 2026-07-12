using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using NzbWebDAV.Database;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.GetDatabaseBackup;

[ApiController]
[Route("api/db.sqlite")]
public class GetDatabaseBackupController() : BaseApiController
{
    private async Task<IActionResult> GetDatabaseBackup()
    {
        // This is intentionally disabled by default. Snapshot through SQLite's
        // backup API so WAL transactions are included and the downloaded main
        // database is internally consistent. This is not a full CONFIG_PATH
        // backup (metrics, Warden, blobs and data-protection keys are separate).
        if (!EnvironmentUtil.IsVariableTrue("DANGEROUS_ENABLE_DATABASE_DOWNLOAD_ENDPOINT"))
            return StatusCode(403, new { status = false, error = "This endpoint is not enabled." });

        var filepath = DavDatabaseContext.DatabaseFilePath;
        if (!System.IO.File.Exists(filepath)) return NotFound($"Path not found: `{filepath}`.");

        var snapshotPath = Path.Combine(Path.GetTempPath(), $"nzbdav-main-{Guid.NewGuid():N}.sqlite");
        try
        {
            await using (var placeholder = new FileStream(
                snapshotPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)) { }
            if (!OperatingSystem.IsWindows())
            {
                System.IO.File.SetUnixFileMode(snapshotPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            await using (var source = new SqliteConnection($"Data Source={filepath};Mode=ReadOnly;Pooling=False"))
            await using (var destination = new SqliteConnection($"Data Source={snapshotPath};Pooling=False"))
            {
                await source.OpenAsync(HttpContext.RequestAborted).ConfigureAwait(false);
                await destination.OpenAsync(HttpContext.RequestAborted).ConfigureAwait(false);
                source.BackupDatabase(destination);
            }

            var stream = new FileStream(
                snapshotPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            return File(stream, "application/vnd.sqlite3", "nzbdav-main-db.sqlite");
        }
        catch
        {
            try { System.IO.File.Delete(snapshotPath); } catch { }
            throw;
        }
    }

    protected override Task<IActionResult> HandleRequest()
    {
        return GetDatabaseBackup();
    }
}
