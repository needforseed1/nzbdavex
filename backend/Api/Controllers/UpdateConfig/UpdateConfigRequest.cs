using Microsoft.AspNetCore.Http;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.UpdateConfig;

public class UpdateConfigRequest
{
    public List<ConfigItem> ConfigItems { get; init; }

    public UpdateConfigRequest(HttpContext context)
    {
        ConfigItems = context.Request.Form
            .Select(x => new ConfigItem()
            {
                ConfigName = x.Key,
                ConfigValue = x.Value.FirstOrDefault() ?? ""
            })
            // The settings API never returns stored passwords or tokens. An
            // empty secret field therefore means "leave it unchanged".
            .Where(x => x.ConfigName is not ("webdav.pass" or "rclone.pass" or "plex.token")
                || !string.IsNullOrWhiteSpace(x.ConfigValue))
            .Select(x => x.ConfigName != "webdav.pass" ? x : new ConfigItem()
            {
                ConfigName = x.ConfigName,
                ConfigValue = PasswordUtil.Hash(x.ConfigValue)
            })
            .ToList();
    }
}
