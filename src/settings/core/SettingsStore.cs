using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MagneticBorders.Settings.Core;

/// <summary>Persistence seam for settings.json; tests replace it with a temp-file store.</summary>
public interface ISettingsStore
{
    /// <summary>Reads the current settings; defaults when the file is missing or invalid.</summary>
    AppSettings Load();
    /// <summary>Writes the known keys back to the file, preserving unknown ones.</summary>
    void Save(AppSettings settings);
    /// <summary>Last write time of the file, or DateTime.MinValue when it does not exist; used to detect outside edits.</summary>
    DateTime LastWriteUtc { get; }
}

/// <summary>
/// Loads and atomically saves settings.json (default %LOCALAPPDATA%\MagneticBorders\settings.json), preserving
/// keys this app does not know about. Save is serialized with a lock, so it is safe from the UI thread and the
/// debounce timer thread. Readers (the engines) never see a partial file.
/// </summary>
public sealed class SettingsStore : ISettingsStore
{
    /// <summary>Indented JSON output so the file stays readable and diff-friendly.</summary>
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    /// <summary>Serializes concurrent saves.</summary>
    private readonly object _gate = new();

    /// <summary>Uses the given file, or DefaultPath when null.</summary>
    public SettingsStore(string? path = null) => Path = path ?? DefaultPath;

    /// <summary>Full path of the settings file.</summary>
    public string Path { get; }

    /// <summary>Per-user data folder %LOCALAPPDATA%\MagneticBorders.</summary>
    public static string DataDir => System.IO.Path.Join(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MagneticBorders");

    /// <summary>Default settings file inside DataDir.</summary>
    public static string DefaultPath => System.IO.Path.Join(DataDir, "settings.json");

    /// <summary>See ISettingsStore.LastWriteUtc.</summary>
    public DateTime LastWriteUtc => File.Exists(Path) ? File.GetLastWriteTimeUtc(Path) : DateTime.MinValue;

    /// <summary>See ISettingsStore.Load.</summary>
    public AppSettings Load() => AppSettings.FromJson(ReadRoot());

    /// <summary>
    /// Re-reads the file, writes the known keys into it, writes the result to a temp file in the same folder and
    /// swaps it in with File.Replace (or File.Move for a first save). The temp file is always cleaned up.
    /// </summary>
    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            var root = ReadRoot() ?? new JsonObject();
            settings.WriteTo(root);
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!;
            Directory.CreateDirectory(dir);
            var tmp = System.IO.Path.Join(dir, $".settings.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(tmp, root.ToJsonString(Pretty), new UTF8Encoding(false));
                if (File.Exists(Path)) File.Replace(tmp, Path, null);
                else File.Move(tmp, Path);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }
    }

    /// <summary>Returns the parsed root object, or null when the file is missing, empty or not a JSON object.</summary>
    private JsonObject? ReadRoot()
    {
        try
        {
            if (!File.Exists(Path)) return null;
            using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonNode.Parse(fs, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) as JsonObject;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
