using System.Text.Json;
using System.Text.Json.Serialization;

namespace Recorder.Common.Settings;

/// <summary>
/// Loads and saves <see cref="RecorderSettings"/> as human-editable JSON.
/// Guarantees: a missing or corrupted file yields defaults instead of an exception
/// (the app must always start), and writes are atomic (write-to-temp, then replace)
/// so a crash mid-save can never destroy the existing settings file.
/// </summary>
public sealed class JsonSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _settingsFilePath;

    public JsonSettingsStore(string settingsFilePath)
    {
        _settingsFilePath = settingsFilePath;
    }

    /// <summary>
    /// Reads settings from disk. Any failure (file absent, malformed JSON, IO error)
    /// falls back to defaults — the caller can inspect <paramref name="loadedFromDisk"/>
    /// to decide whether to warn the user about a reset.
    /// </summary>
    public RecorderSettings Load(out bool loadedFromDisk)
    {
        loadedFromDisk = false;
        try
        {
            if (!File.Exists(_settingsFilePath))
            {
                return new RecorderSettings();
            }

            string json = File.ReadAllText(_settingsFilePath);
            RecorderSettings? parsed = JsonSerializer.Deserialize<RecorderSettings>(json, SerializerOptions);
            if (parsed is null)
            {
                return new RecorderSettings();
            }

            loadedFromDisk = true;
            return parsed;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new RecorderSettings();
        }
    }

    /// <summary>
    /// Persists settings atomically: serialize to a sibling temp file, then swap it in.
    /// File.Replace/Move gives us an all-or-nothing update so power loss during a save
    /// leaves either the old file or the new file, never a truncated one.
    /// </summary>
    public void Save(RecorderSettings settings)
    {
        string directory = Path.GetDirectoryName(_settingsFilePath)!;
        Directory.CreateDirectory(directory);

        string tempPath = _settingsFilePath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, SerializerOptions));

        if (File.Exists(_settingsFilePath))
        {
            File.Replace(tempPath, _settingsFilePath, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tempPath, _settingsFilePath);
        }
    }
}
