namespace PeakRecorder;

/// <summary>
/// Cache of the stock character icons the site serves at
/// /images/characters/icons/{Character}, so the UI can show an icon next to
/// every character name — including on past matches and while offline.
/// The app can't download these itself (the site rate-limits requests from
/// outside the browser with 429s), so the browser extension fetches them
/// same-origin from the page and uploads them through the bridge:
/// GET /icons/needed lists what's missing, POST /icons delivers one.
/// Files live in %APPDATA%\PeakRecorder\icons\.
/// </summary>
public sealed class CharacterIcons
{
    /// <summary>Virtual host the main window maps onto <see cref="Dir"/> so
    /// the WebView can load the cached files.</summary>
    public const string VirtualHost = "peakrecorder.icons";

    private const int MaxIconBytes = 1024 * 1024; // stock icons are a few KB

    public static string Dir => Path.Combine(Config.Dir, "icons");

    private readonly Store _store;
    private readonly object _lock = new();
    // Sanitized character name -> cached file name (with extension).
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised (possibly off the UI thread) when a new icon lands.</summary>
    public event Action? Changed;

    public CharacterIcons(Store store)
    {
        _store = store;
        Directory.CreateDirectory(Dir);
        foreach (var path in Directory.EnumerateFiles(Dir))
            _files[Path.GetFileNameWithoutExtension(path)] = Path.GetFileName(path);
    }

    /// <summary>Character name -> UI-loadable URL, for the given names.</summary>
    public Dictionary<string, string> UrlMap(IEnumerable<string?> names)
    {
        var map = new Dictionary<string, string>();
        lock (_lock)
        {
            foreach (var name in names)
            {
                if (string.IsNullOrWhiteSpace(name) || map.ContainsKey(name)) continue;
                if (_files.TryGetValue(SafeName(name), out var file))
                    map[name] = $"https://{VirtualHost}/{Uri.EscapeDataString(file)}";
            }
        }
        return map;
    }

    /// <summary>Every character in the store that has no cached icon yet.</summary>
    public List<string> MissingNames()
    {
        var names = _store.Snapshot().Matches
            .SelectMany(m => new[] { m.MyCharacter, m.OpponentCharacter })
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
            return names.Where(n => !_files.ContainsKey(SafeName(n))).ToList();
    }

    /// <summary>Stores an icon delivered by the extension. Returns false when
    /// the payload doesn't look like a plausible icon.</summary>
    public bool SaveIcon(string name, string? contentType, byte[] bytes)
    {
        if (string.IsNullOrWhiteSpace(name) || bytes.Length == 0 || bytes.Length > MaxIconBytes)
            return false;
        // The virtual host serves content types by file extension, so the
        // cached file needs a real one even though the endpoint has none.
        var ext = contentType switch
        {
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "image/svg+xml" => ".svg",
            _ => ".png",
        };
        var key = SafeName(name.Trim());
        var file = key + ext;
        File.WriteAllBytes(Path.Combine(Dir, file), bytes);
        lock (_lock) _files[key] = file;
        Log.Write($"CharacterIcons: cached '{name}' -> {file} ({bytes.Length} bytes)");
        Changed?.Invoke();
        return true;
    }

    private static string SafeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }
}
