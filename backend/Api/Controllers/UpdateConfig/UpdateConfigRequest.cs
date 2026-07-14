using Microsoft.AspNetCore.Http;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.UpdateConfig;

public class UpdateConfigRequest
{
    public List<ConfigItem> ConfigItems { get; init; }
    public long BaseRevision { get; init; }
    public IReadOnlyList<string> ResetKeys { get; init; }

    public UpdateConfigRequest(HttpContext context)
    {
        BaseRevision = long.TryParse(context.Request.Form["revision"].FirstOrDefault(), out var revision)
            ? revision
            : -1;
        ResetKeys = context.Request.Form["reset"].Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToArray();
        ConfigItems = context.Request.Form
            .Where(x => x.Key is not ("revision" or "reset"))
            .Select(x => new ConfigItem()
            {
                ConfigName = x.Key,
                ConfigValue = x.Value.FirstOrDefault() ?? ""
            })
            // The settings API never returns the stored WebDAV password hash. An empty
            // password field therefore means "leave the existing credential alone".
            .Where(x => x.ConfigName is not ("webdav.pass" or "rclone.pass")
                || !string.IsNullOrWhiteSpace(x.ConfigValue))
            .Select(x => x.ConfigName != "webdav.pass" ? x : new ConfigItem()
            {
                ConfigName = x.ConfigName,
                ConfigValue = PasswordUtil.Hash(x.ConfigValue)
            })
            .ToList();
    }
}
