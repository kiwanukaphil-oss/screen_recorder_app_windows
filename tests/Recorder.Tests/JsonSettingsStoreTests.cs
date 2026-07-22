using Recorder.Common.Settings;

namespace Recorder.Tests;

public class JsonSettingsStoreTests : IDisposable
{
    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "recorder-tests-" + Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_tempDirectory, "settings.json");

    [Fact]
    public void Missing_file_yields_defaults()
    {
        var store = new JsonSettingsStore(SettingsPath);
        RecorderSettings settings = store.Load(out bool loadedFromDisk);

        Assert.False(loadedFromDisk);
        Assert.Equal(60, settings.FramesPerSecond);
    }

    [Fact]
    public void Saved_settings_round_trip()
    {
        var store = new JsonSettingsStore(SettingsPath);
        var settings = new RecorderSettings
        {
            FramesPerSecond = 120,
            Codec = VideoCodecPreference.Hevc,
            OutputDirectory = @"D:\Captures",
        };

        store.Save(settings);
        RecorderSettings reloaded = store.Load(out bool loadedFromDisk);

        Assert.True(loadedFromDisk);
        Assert.Equal(120, reloaded.FramesPerSecond);
        Assert.Equal(VideoCodecPreference.Hevc, reloaded.Codec);
        Assert.Equal(@"D:\Captures", reloaded.OutputDirectory);
    }

    [Fact]
    public void Corrupted_file_falls_back_to_defaults_without_throwing()
    {
        Directory.CreateDirectory(_tempDirectory);
        File.WriteAllText(SettingsPath, "{ this is not valid json !!");

        var store = new JsonSettingsStore(SettingsPath);
        RecorderSettings settings = store.Load(out bool loadedFromDisk);

        Assert.False(loadedFromDisk);
        Assert.Equal(60, settings.FramesPerSecond);
    }

    [Fact]
    public void Save_then_save_again_overwrites_atomically()
    {
        var store = new JsonSettingsStore(SettingsPath);
        store.Save(new RecorderSettings { FramesPerSecond = 30 });
        store.Save(new RecorderSettings { FramesPerSecond = 144 });

        RecorderSettings reloaded = store.Load(out _);
        Assert.Equal(144, reloaded.FramesPerSecond);
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
