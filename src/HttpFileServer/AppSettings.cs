using System.Text.Encodings.Web;
using System.Text.Json;

namespace HttpFileServer;

public sealed class AppSettings
{
    public string RootPath { get; set; } =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public int Port { get; set; } = 8600;

    /// <summary>空字符串表示监听所有网卡 (0.0.0.0)。</summary>
    public string ListenAddress { get; set; } = "";

    public bool AllowUpload { get; set; } = true;

    public bool MinimizeToTray { get; set; } = true;

    public bool AutoStart { get; set; } = false;

    public static string SettingsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HttpFileServer");

    private static string FilePath => Path.Combine(SettingsDir, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOpts) ?? new AppSettings();
        }
        catch
        {
            // 配置损坏时回退到默认值
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch
        {
            // 保存失败不影响运行
        }
    }
}
