using System.Text;
using PadMapper.Core.Devices;
using PadMapper.Core.Logging;

namespace PadMapper.Core.Profiles;

public enum LoadNoticeKind
{
    /// <summary>ファイルが壊れていたので、バックアップから復元した。</summary>
    RestoredFromBackup,
    /// <summary>ファイルもバックアップも読めなかった（既定値で起動）。</summary>
    Failed,
}

public sealed record LoadNotice(LoadNoticeKind Kind, string FileName, string? BackupName = null, string? Error = null);

/// <summary>
/// 設定・プロファイル・デバイス定義の読み書き。
/// 保存のたびに直前の内容を backups\ に退避し（ファイルごとに最新5世代）、読み込みに失敗したらバックアップから復元する。
/// </summary>
public sealed class ProfileStore
{
    public const int BackupGenerations = 5;

    private readonly ILog _log;
    private readonly object _lock = new();

    public string BaseDir { get; }
    public string ProfilesDir => Path.Combine(BaseDir, "profiles");
    public string BackupsDir => Path.Combine(BaseDir, "backups");
    public string DevicesDir => Path.Combine(BaseDir, "devices");
    public string LogsDir => Path.Combine(BaseDir, "logs");
    public string SettingsPath => Path.Combine(BaseDir, "settings.json");

    public List<LoadNotice> Notices { get; } = new();

    public ProfileStore(string baseDir, ILog? log = null)
    {
        BaseDir = baseDir;
        _log = log ?? NullLog.Instance;
        Directory.CreateDirectory(ProfilesDir);
        Directory.CreateDirectory(BackupsDir);
        Directory.CreateDirectory(DevicesDir);
    }

    public static string DefaultBaseDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PadMapper");

    // ---------- 設定 ----------

    public AppSettings LoadSettings()
    {
        if (!File.Exists(SettingsPath)) return new AppSettings();
        return LoadWithFallback<AppSettings>(SettingsPath) ?? new AppSettings();
    }

    public void SaveSettings(AppSettings settings) => SaveWithBackup(SettingsPath, ProfileJson.Serialize(settings));

    // ---------- プロファイル ----------

    /// <summary>プロファイルを全て読む。1つもなければ既定のプロファイルを作って保存する。</summary>
    public List<Profile> LoadProfiles()
    {
        var result = new List<Profile>();
        foreach (var file in Directory.GetFiles(ProfilesDir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var p = LoadWithFallback<Profile>(file);
            if (p == null) continue;
            if (string.IsNullOrWhiteSpace(p.Id)) p.Id = Path.GetFileNameWithoutExtension(file);
            if (result.Any(x => x.Id == p.Id)) continue;
            result.Add(p);
        }
        if (result.Count == 0)
        {
            foreach (var p in DefaultProfiles.Create())
            {
                SaveProfile(p);
                result.Add(p);
            }
        }
        return result;
    }

    public string ProfilePath(string id) => Path.Combine(ProfilesDir, SafeFileName(id) + ".json");

    public void SaveProfile(Profile p)
    {
        if (string.IsNullOrWhiteSpace(p.Id)) throw new ArgumentException("profile id is empty");
        SaveWithBackup(ProfilePath(p.Id), ProfileJson.Serialize(p));
    }

    public void DeleteProfile(string id)
    {
        var path = ProfilePath(id);
        if (!File.Exists(path)) return;
        lock (_lock)
        {
            Backup(path);
            File.Delete(path);
        }
    }

    /// <summary>JSON ファイルからプロファイルを読み込む（インポート）。ID が重複する場合は別の ID にする。</summary>
    public Profile Import(string path, IEnumerable<string> existingIds)
    {
        var p = ProfileJson.Deserialize<Profile>(File.ReadAllText(path, Encoding.UTF8));
        if (string.IsNullOrWhiteSpace(p.Id)) p.Id = Path.GetFileNameWithoutExtension(path);
        p.Id = UniqueId(p.Id, existingIds);
        if (string.IsNullOrWhiteSpace(p.Name)) p.Name = p.Id;
        SaveProfile(p);
        return p;
    }

    public void Export(Profile p, string path) => File.WriteAllText(path, ProfileJson.Serialize(p), new UTF8Encoding(false));

    public static string UniqueId(string baseId, IEnumerable<string> existing)
    {
        var set = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        var id = string.IsNullOrWhiteSpace(baseId) ? "profile" : baseId.Trim();
        if (!set.Contains(id)) return id;
        for (int i = 2; ; i++)
            if (!set.Contains($"{id}-{i}")) return $"{id}-{i}";
    }

    // ---------- デバイス定義（診断ウィザードの結果） ----------

    public List<DeviceDefinition> LoadDeviceDefinitions()
    {
        var list = new List<DeviceDefinition>();
        foreach (var file in Directory.GetFiles(DevicesDir, "*.json"))
        {
            var d = LoadWithFallback<DeviceDefinition>(file);
            if (d != null) list.Add(d);
        }
        return list;
    }

    public string SaveDeviceDefinition(DeviceDefinition def)
    {
        if (string.IsNullOrWhiteSpace(def.Id)) def.Id = "device-" + DateTime.Now.ToString("yyyyMMddHHmmss");
        var path = Path.Combine(DevicesDir, SafeFileName(def.Id) + ".json");
        SaveWithBackup(path, ProfileJson.Serialize(def));
        return path;
    }

    public void DeleteDeviceDefinition(string id)
    {
        var path = Path.Combine(DevicesDir, SafeFileName(id) + ".json");
        if (!File.Exists(path)) return;
        lock (_lock)
        {
            Backup(path);
            File.Delete(path);
        }
    }

    // ---------- 共通 ----------

    private T? LoadWithFallback<T>(string path) where T : class
    {
        string? firstError = null;
        try
        {
            return ProfileJson.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8));
        }
        catch (Exception ex)
        {
            firstError = ex.Message;
            _log.Warn($"読み込みに失敗: {Path.GetFileName(path)}", ex);
        }
        foreach (var backup in BackupsOf(path))
        {
            try
            {
                var value = ProfileJson.Deserialize<T>(File.ReadAllText(backup, Encoding.UTF8));
                // 壊れたファイルは残しておき、復元した内容で置き換える
                lock (_lock)
                {
                    File.Copy(path, path + ".broken", overwrite: true);
                    File.Copy(backup, path, overwrite: true);
                }
                Notices.Add(new LoadNotice(LoadNoticeKind.RestoredFromBackup, Path.GetFileName(path), Path.GetFileName(backup), firstError));
                _log.Warn($"バックアップから復元: {Path.GetFileName(path)} ← {Path.GetFileName(backup)}");
                return value;
            }
            catch (Exception ex)
            {
                _log.Warn($"バックアップも読めません: {Path.GetFileName(backup)}", ex);
            }
        }
        Notices.Add(new LoadNotice(LoadNoticeKind.Failed, Path.GetFileName(path), null, firstError));
        return null;
    }

    private void SaveWithBackup(string path, string content)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path))
            {
                // 内容が同じならバックアップを増やさない
                try
                {
                    if (File.ReadAllText(path, Encoding.UTF8) == content) return;
                }
                catch
                {
                    // 読めなければ普通に保存する
                }
                Backup(path);
            }
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
    }

    private void Backup(string path)
    {
        try
        {
            var name = Path.GetFileName(path);
            var dest = Path.Combine(BackupsDir, $"{name}.{DateTime.Now:yyyyMMdd-HHmmss-fff}.bak");
            File.Copy(path, dest, overwrite: true);
            foreach (var old in BackupsOf(path).Skip(BackupGenerations))
                File.Delete(old);
        }
        catch (Exception ex)
        {
            _log.Warn("バックアップに失敗", ex);
        }
    }

    /// <summary>新しい順のバックアップ一覧。</summary>
    public IEnumerable<string> BackupsOf(string path)
    {
        var name = Path.GetFileName(path);
        if (!Directory.Exists(BackupsDir)) return Array.Empty<string>();
        return Directory.GetFiles(BackupsDir, name + ".*.bak")
            .OrderByDescending(f => f, StringComparer.Ordinal)
            .ToList();
    }

    public static string SafeFileName(string id)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).ToHashSet();
        var sb = new StringBuilder();
        foreach (var c in id.Trim()) sb.Append(invalid.Contains(c) ? '_' : c);
        return sb.Length == 0 ? "profile" : sb.ToString();
    }
}
