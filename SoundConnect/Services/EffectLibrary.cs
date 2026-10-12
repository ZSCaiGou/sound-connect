using SoundConnect.Shared.Services;

namespace SoundConnect.Services;

/// <summary>音效库：扫描文件夹，按需解码并缓存。</summary>
public sealed class EffectLibrary
{
    public const string TestToneName = "测试音 1kHz";

    private static readonly string[] Extensions =
    [
        ".wav",
        ".mp3",
        ".m4a",
        ".aac",
        ".flac",
        ".ogg",
        ".aif",
        ".aiff",
        ".wma"
    ];

    private readonly Dictionary<string, CachedEffect> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly object sync = new();

    public string Folder { get; private set; } = DefaultFolder();

    public static string DefaultFolder()
    {
        var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        return Path.Combine(music, "sound-connect");
    }

    public IReadOnlyList<EffectInfo> Scan(string? folder)
    {
        Folder = string.IsNullOrWhiteSpace(folder) ? DefaultFolder() : folder;
        lock (sync)
        {
            paths.Clear();
            paths[TestToneName] = string.Empty;

            if (!Directory.Exists(Folder))
            {
                Directory.CreateDirectory(Folder);
            }
            foreach (var path in Directory.EnumerateFiles(Folder).OrderBy(item => item))
            {
                if (!Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }
                paths[Path.GetFileNameWithoutExtension(path)] = path;
            }
        }

        return paths.Keys.Select(name => new EffectInfo(name, paths[name])).ToList();
    }

    public CachedEffect? Resolve(string name)
    {
        lock (sync)
        {
            if (cache.TryGetValue(name, out var cached))
            {
                return cached;
            }
            if (!paths.TryGetValue(name, out var path))
            {
                return null;
            }

            try
            {
                var effect =
                    string.IsNullOrEmpty(path)
                        ? CachedEffect.CreateTestTone()
                        : CachedEffect.Load(name, path);
                cache[name] = effect;
                return effect;
            }
            catch
            {
                return null;
            }
        }
    }
}
