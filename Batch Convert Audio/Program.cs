using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Spectre.Console;

namespace BatchConvertAudio
{
    internal class Program
    {
        private const string ConfigFileName = "config.json";
        private const string CheckpointFileName = "checkpoint.json";

        static async Task<int> Main()
        {
            Console.Title = "Batch Audio Converter"; // Window title
            AnsiConsole.MarkupLine("[bold cyan]Batch Audio Converter[/]\n");

            // FFmpeg
            var ffmpegPath = LoadFfmpegFromConfig() ?? PromptAndSaveFfmpeg();
            if (ffmpegPath == null)
                return 1;

            AnsiConsole.Write(
                new Panel(ffmpegPath)
                    .Header("[green]FFmpeg loaded[/]")
                    .BorderColor(Color.Green)
            );

            // ---- CHECKPOINT RESUME ----
            Checkpoint? checkpoint;
            string inputDir, outputDir, targetFormat, ffmpegQuality;
            int cores, alreadyCompleted = 0;
            List<string> allFiles;
            bool resumedFromCheckpoint = false;

        restart_checkpoint:
            checkpoint = LoadCheckpoint();
            if (checkpoint != null)
            {
                alreadyCompleted = Math.Max(0, checkpoint.TotalFiles - checkpoint.RemainingFiles.Count);
                int percent = checkpoint.TotalFiles == 0 ? 0 :
                    (int)Math.Round(alreadyCompleted * 100.0 / checkpoint.TotalFiles);

                AnsiConsole.Write(new Panel(
                        $"[bold]Previous conversion detected[/]\n\n" +
                        $"[bold]Input:[/] {checkpoint.InputDir}\n" +
                        $"[bold]Output:[/] {checkpoint.OutputDir}\n" +
                        $"[bold]Format:[/] {checkpoint.TargetFormat}\n" +
                        $"[bold]Progress:[/] {percent}% ({alreadyCompleted} / {checkpoint.TotalFiles} files)")
                    .BorderColor(Color.Yellow));

                if (!AnsiConsole.Confirm("Resume conversion?", true))
                {
                    DeleteCheckpoint();
                    goto restart_checkpoint; // restart process after deleting checkpoint
                }

                resumedFromCheckpoint = true;
                inputDir = checkpoint.InputDir;
                outputDir = checkpoint.OutputDir;
                targetFormat = checkpoint.TargetFormat;
                ffmpegQuality = checkpoint.Quality;
                cores = checkpoint.Cores;
                allFiles = checkpoint.RemainingFiles;
            }
            else
            {
                // ---- INPUT / OUTPUT ----
                inputDir = AnsiConsole.Ask<string>("INPUT directory:").Trim().Trim('"');
                if (!Directory.Exists(inputDir))
                {
                    AnsiConsole.MarkupLine("[red]Input directory not found[/]");
                    return 1;
                }

                outputDir = AnsiConsole.Ask<string>("OUTPUT directory:").Trim().Trim('"');
                Directory.CreateDirectory(outputDir);

                AnsiConsole.Write(
                    new Panel(
                        $"[bold]Input:[/] {inputDir}\n[bold]Output:[/] {outputDir}")
                    .Header("Confirm directories")
                    .BorderColor(Color.Yellow)
                );

                if (!AnsiConsole.Confirm("Continue?", true))
                    return 0;

                // ---- SCAN FILES ----
                var supported = new HashSet<string> { ".wav", ".mp3", ".m4a", ".flac", ".aac", ".ogg" };
                allFiles = Directory
                    .EnumerateFiles(inputDir, "*.*", SearchOption.AllDirectories)
                    .Where(f => supported.Contains(Path.GetExtension(f).ToLower()))
                    .ToList();

                if (!allFiles.Any())
                {
                    AnsiConsole.MarkupLine("[red]No supported audio files found[/]");
                    return 0;
                }

                AnsiConsole.MarkupLine($"Found [green]{allFiles.Count}[/] audio files\n");

                // ---- FORMAT FIRST ----
                targetFormat = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("Target output format:")
                        .AddChoices(".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg")
                );

                cores = AnsiConsole.Prompt(
                    new TextPrompt<int>("CPU cores to use:")
                        .DefaultValue(Environment.ProcessorCount));

                int quality = AnsiConsole.Prompt(
                    new TextPrompt<int>("Output quality (30–100):")
                        .DefaultValue(70));

                ffmpegQuality = PercentToFfmpegQuality(quality);

                AnsiConsole.Write(
                    new Panel(
                        $"[bold]Cores:[/] {cores}\n" +
                        $"[bold]Quality:[/] {quality} (ffmpeg q={ffmpegQuality})\n" +
                        $"[bold]Target format:[/] {targetFormat}\n" +
                        $"[bold]Files to convert:[/] {allFiles.Count}")
                    .Header("[yellow]Conversion summary[/]")
                    .BorderColor(Color.Yellow)
                );

                if (!AnsiConsole.Confirm("Start conversion?", true))
                    return 0;
            }

            // ---- CONVERSION ----
            Console.Title = "Batch Audio Converter — Converting"; // window title while converting
            using var stopCts = new CancellationTokenSource();
            string? latestFile = null;

            _ = Task.Run(() =>
            {
                AnsiConsole.MarkupLine("\n[white]Press[/] [bold red]S[/] [white]to stop conversion[/]");
                while (!stopCts.IsCancellationRequested)
                {
                    if (Console.ReadKey(true).Key == ConsoleKey.S)
                    {
                        stopCts.Cancel();
                        break;
                    }
                }
            });

            int completed = alreadyCompleted;
            var errors = new List<string>();
            var remaining = new ConcurrentBag<string>(allFiles);

            try
            {
                await AnsiConsole.Progress()
                    .Columns(
                        new TaskDescriptionColumn(),
                        new ProgressBarColumn(),
                        new PercentageColumn(),
                        new SpinnerColumn(),
                        new RemainingTimeColumn())
                    .StartAsync(async ctx =>
                    {
                        var task = ctx.AddTask(
                            resumedFromCheckpoint ? "Resumed from checkpoint" : "Converting",
                            maxValue: completed + allFiles.Count);
                        task.Value = completed;

                        await Parallel.ForEachAsync(
                            allFiles,
                            new ParallelOptions { MaxDegreeOfParallelism = cores, CancellationToken = stopCts.Token },
                            async (file, ct) =>
                            {
                                try
                                {
                                    await ConvertFileAsync(ffmpegPath, file, inputDir, outputDir, targetFormat, ffmpegQuality, ct);
                                    latestFile = Path.GetFileName(file);
                                    remaining.TryTake(out _);
                                }
                                catch (OperationCanceledException) { }
                                catch (Exception ex)
                                {
                                    lock (errors) errors.Add($"{file}: {ex.Message}");
                                }
                                finally
                                {
                                    task.Value = Interlocked.Increment(ref completed);

                                    if (!string.IsNullOrEmpty(latestFile))
                                        AnsiConsole.MarkupLine($"[grey]Last converted file:[/] [green]{latestFile}[/]");
                                }
                            });
                    });
            }
            catch (TaskCanceledException) { }

            if (stopCts.IsCancellationRequested && remaining.Any())
            {
                SaveCheckpoint(new Checkpoint
                {
                    InputDir = inputDir,
                    OutputDir = outputDir,
                    TargetFormat = targetFormat,
                    Quality = ffmpegQuality,
                    Cores = cores,
                    TotalFiles = completed + remaining.Count,
                    RemainingFiles = remaining.ToList()
                });

                AnsiConsole.Write(
                    new Panel("[bold yellow]Conversion stopped — progress saved[/]")
                        .BorderColor(Color.Yellow));

                return 0;
            }

            DeleteCheckpoint();

            if (errors.Any())
            {
                AnsiConsole.MarkupLine("\n[bold red]Some files failed:[/]");
                foreach (var e in errors)
                    AnsiConsole.MarkupLine($"[red]- {e}[/]");
            }

            AnsiConsole.MarkupLine("\n[bold green]DONE[/]");
            Console.Title = "Batch Audio Converter — DONE"; // final window title
            return 0;
        }

        // ---- FFmpeg conversion ----
        private static async Task ConvertFileAsync(string ffmpeg, string src, string inputRoot, string outputRoot, string targetExt, string quality, CancellationToken ct)
        {
            var rel = Path.GetRelativePath(inputRoot, src);
            var outDir = Path.Combine(outputRoot, Path.GetDirectoryName(rel)!);
            Directory.CreateDirectory(outDir);

            var dest = Path.Combine(outDir, Path.GetFileNameWithoutExtension(src) + targetExt);
            var args = $"-hide_banner -loglevel error -nostdin -y -i \"{src}\" -vn -q:a {quality} \"{dest}\"";

            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            })!;

            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0)
                throw new InvalidOperationException(await proc.StandardError.ReadToEndAsync());
        }

        private static string PercentToFfmpegQuality(int p) =>
            p >= 90 ? "0" :
            p >= 75 ? "2" :
            p >= 60 ? "3" :
            p >= 50 ? "4" :
            p >= 40 ? "5" : "6";

        // ---- CONFIG / CHECKPOINT ----
        private static string? LoadFfmpegFromConfig()
        {
            var path = Path.Combine(AppContext.BaseDirectory, ConfigFileName);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<Config>(File.ReadAllText(path))?.FfmpegPath;
        }

        private static string PromptAndSaveFfmpeg()
        {
            var input = AnsiConsole.Ask<string>("FFmpeg path:");
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, ConfigFileName),
                JsonSerializer.Serialize(new Config { FfmpegPath = input }));
            return input;
        }

        private static void SaveCheckpoint(Checkpoint cp) =>
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, CheckpointFileName),
                JsonSerializer.Serialize(cp, new JsonSerializerOptions { WriteIndented = true }));

        private static Checkpoint? LoadCheckpoint()
        {
            var path = Path.Combine(AppContext.BaseDirectory, CheckpointFileName);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Checkpoint>(File.ReadAllText(path))
                : null;
        }

        private static void DeleteCheckpoint()
        {
            var path = Path.Combine(AppContext.BaseDirectory, CheckpointFileName);
            if (File.Exists(path)) File.Delete(path);
        }

        private class Config { public string? FfmpegPath { get; set; } }
        private class Checkpoint
        {
            public string InputDir { get; set; } = null!;
            public string OutputDir { get; set; } = null!;
            public string TargetFormat { get; set; } = null!;
            public string Quality { get; set; } = null!;
            public int Cores { get; set; }
            public int TotalFiles { get; set; }
            public List<string> RemainingFiles { get; set; } = new();
        }
    }
}
