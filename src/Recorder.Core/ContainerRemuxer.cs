using System.Diagnostics;
using Serilog;

namespace Recorder.Core;

/// <summary>
/// Converts finished recordings to MKV/MOV by remuxing (stream copy — no re-encode,
/// takes seconds even for huge files). Media Foundation can only write MP4-family
/// containers, so other containers are produced by shelling out to ffmpeg when one
/// is available; without ffmpeg the recording simply stays MP4 and the caller logs
/// why. M6 packaging decides whether to bundle an ffmpeg binary.
/// </summary>
public static class ContainerRemuxer
{
    public static readonly string[] SupportedContainers = { "mp4", "mkv", "mov" };

    /// <summary>Finds ffmpeg on PATH or in the standard winget install location.</summary>
    public static string? FindFfmpeg()
    {
        string[] candidates =
        {
            "ffmpeg", // PATH
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WinGet", "Links", "ffmpeg.exe"),
        };
        foreach (string candidate in candidates)
        {
            try
            {
                using var probe = Process.Start(new ProcessStartInfo(candidate, "-version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                probe!.WaitForExit(5000);
                if (probe.ExitCode == 0)
                {
                    return candidate;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Candidate not present; try the next one.
            }
        }
        return null;
    }

    /// <summary>
    /// Remuxes one MP4 into the requested container next to it and deletes the MP4 on
    /// success. Returns the new path, or the original on any failure (the recording
    /// must never be lost to a post-processing step).
    /// </summary>
    public static string RemuxOrKeepMp4(string mp4Path, string container, ILogger log)
    {
        container = container.Trim().TrimStart('.').ToLowerInvariant();
        if (container is "mp4" || !SupportedContainers.Contains(container))
        {
            return mp4Path;
        }

        string? ffmpeg = FindFfmpeg();
        if (ffmpeg is null)
        {
            log.Warning("{Container} output needs ffmpeg, which was not found; keeping MP4", container);
            return mp4Path;
        }

        string targetPath = Path.ChangeExtension(mp4Path, "." + container);
        try
        {
            using var remux = Process.Start(new ProcessStartInfo(ffmpeg,
                $"-v error -y -i \"{mp4Path}\" -c copy \"{targetPath}\"")
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            string errors = remux!.StandardError.ReadToEnd();
            remux.WaitForExit();

            if (remux.ExitCode == 0 && File.Exists(targetPath) && new FileInfo(targetPath).Length > 0)
            {
                File.Delete(mp4Path);
                return targetPath;
            }
            log.Warning("Remux to {Container} failed ({Errors}); keeping MP4", container, errors.Trim());
            File.Delete(targetPath);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Remux to {Container} failed; keeping MP4", container);
        }
        return mp4Path;
    }
}
