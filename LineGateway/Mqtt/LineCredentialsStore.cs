using System.Text.Json;

namespace LineGateway.Mqtt;

public sealed record LineCredentials(int LineId, string? Username, string? Password);

/// <summary>File chứa mật khẩu MQTT của tuyến, chỉ chủ sở hữu đọc được.</summary>
public sealed class LineCredentialsStore(string path)
{
    public LineCredentials? Load() =>
        File.Exists(path) ? JsonSerializer.Deserialize<LineCredentials>(File.ReadAllText(path)) : null;

    public void Save(LineCredentials credentials)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(credentials));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temp, path, overwrite: true);
    }

    public void Delete() => File.Delete(path);
}
