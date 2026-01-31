using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;

namespace BatchConvertAudio
{
    internal class Program
    {
        private const string ConfigFileName = "config.json";

        static async Task<int> Main()
        {
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


            // Input / Output

            var inputDir = AnsiConsole.Ask<string>("INPUT directory:")
                .Trim().Trim('"');

            if (!Directory.Exists(inputDir))
            {
                AnsiConsole.MarkupLine("[red]Input directory not found[/]");
                return 1;
            }

            var outputDir = AnsiConsole.Ask<string>("OUTPUT directory:")
                .Trim().Trim('"');

            Directory.CreateDirectory(outputDir);

            AnsiConsole.Write(
                new Panel(
                    $"[bold]Input:[/] {inputDir}\n[bold]Output:[/] {outputDir}")
                .Header("Confirm directories")
                .BorderColor(Color.Yellow)
            );

            if (!AnsiConsole.Confirm("Continue?", true))
                return 0;


            // Scan files

            var supported = new HashSet<string>
            {
                ".wav", ".mp3", ".m4a", ".flac", ".aac", ".ogg"
            };

            var allFiles = Directory
                .EnumerateFiles(inputDir, "*.*", SearchOption.AllDirectories)
                .Where(f => supported.Contains(Path.GetExtension(f).ToLower()))
                .ToList();

            if (!allFiles.Any())
            {
                AnsiConsole.MarkupLine("[red]No supported audio files found[/]");
                return 0;
            }

            var extensionsFound = allFiles
                .Select(f => Path.GetExtension(f).ToLower())
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            AnsiConsole.MarkupLine(
                $"Found [green]{allFiles.Count}[/] audio files");

            AnsiConsole.MarkupLine(
                $"Convertible extensions: [cyan]{string.Join(", ", extensionsFound)}[/]\n");


            // Options

            int cores = AnsiConsole.Prompt(
                new TextPrompt<int>("CPU cores to use:")
                    .DefaultValue(Environment.ProcessorCount)
                    .Validate(n =>
                        n >= 1 && n <= Environment.ProcessorCount
                            ? ValidationResult.Success()
                            : ValidationResult.Error(
                                $"Choose between 1 and {Environment.ProcessorCount}"))
            );

            var targetFormat = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Target output format:")
                    .AddChoices(".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg")
            );

            int quality = AnsiConsole.Prompt(
                new TextPrompt<int>("Output quality (30–100):")
                    .DefaultValue(70)
                    .Validate(q =>
                        q is >= 30 and <= 100
                            ? ValidationResult.Success()
                            : ValidationResult.Error("Must be 30–100"))
            );

            string ffmpegQuality = PercentToFfmpegQuality(quality);


            // Resume

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


            // Conversion


            int completed = 0;
            var swGlobal = Stopwatch.StartNew();
            var errors = new List<string>();

            await AnsiConsole.Progress()
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn())
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("Converting", maxValue: allFiles.Count);

                    // Parallel.ForEachAsync uses efficient built-in partitioning and avoids creating extra Tasks per file
                    await Parallel.ForEachAsync(allFiles,
                        new ParallelOptions { MaxDegreeOfParallelism = cores },
                        async (file, ct) =>
                        {
                            var sw = Stopwatch.StartNew();
                            try
                            {
                                await ConvertFileAsync(
                                    ffmpegPath,
                                    file,
                                    inputDir,
                                    outputDir,
                                    targetFormat,
                                    ffmpegQuality,
                                    ct);
                            }
                            catch (OperationCanceledException)
                            {
                                // ignore cancellation
                            }
                            catch (Exception ex)
                            {
                                lock (errors)
                                {
                                    errors.Add($"{file}: {ex.Message}");
                                }
                            }
                            finally
                            {
                                sw.Stop();
                                var done = Interlocked.Increment(ref completed);
                                task.Value = done;

                                var avg = swGlobal.Elapsed.TotalSeconds / Math.Max(1, done);

                                task.Description =
                                    $"Converting | Last {sw.Elapsed:mm\\:ss} | Avg {TimeSpan.FromSeconds(avg):mm\\:ss}";
                            }
                        });
                });

            swGlobal.Stop();

            if (errors.Any())
            {
                AnsiConsole.MarkupLine("\n[bold red]Some files failed:[/]");
                foreach (var e in errors)
                    AnsiConsole.MarkupLine($"[red]- {e}[/]");
            }

            AnsiConsole.MarkupLine("\n[bold green]DONE[/]");
            return 0;
        }


        // FFmpeg execution

        private static async Task ConvertFileAsync(
            string ffmpeg,
            string src,
            string inputRoot,
            string outputRoot,
            string targetExt,
            string quality,
            CancellationToken ct = default)
        {
            var rel = Path.GetRelativePath(inputRoot, src);
            var outDir = Path.Combine(outputRoot, Path.GetDirectoryName(rel)!);
            Directory.CreateDirectory(outDir);

            var dest = Path.Combine(
                outDir,
                Path.GetFileNameWithoutExtension(src) + targetExt);


            // Set -threads 1 to avoid each ffmpeg using multiple threads when running many parallel processes.
            var args = $"-hide_banner -loglevel error -nostdin -y -i \"{src}\" -vn -q:a {quality} -threads 1 \"{dest}\"";

            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = false
                },
                EnableRaisingEvents = true
            };

            if (!proc.Start())
                throw new InvalidOperationException("Failed to start ffmpeg process");


            var stderrTask = proc.StandardError.ReadToEndAsync();


            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            var stderr = await stderrTask.ConfigureAwait(false);

            if (proc.ExitCode != 0)
            {
                var msg = string.IsNullOrWhiteSpace(stderr) ? $"ffmpeg exited with code {proc.ExitCode}" : stderr.Trim();
                throw new InvalidOperationException(msg);
            }
        }


        // Quality mapping

        private static string PercentToFfmpegQuality(int p) =>
            p >= 90 ? "0" :
            p >= 75 ? "2" :
            p >= 60 ? "3" :
            p >= 50 ? "4" :
            p >= 40 ? "5" : "6";


        // Config

        private static string? LoadFfmpegFromConfig()
        {
            var path = Path.Combine(AppContext.BaseDirectory, ConfigFileName);
            if (!File.Exists(path)) return null;

            var cfg = JsonSerializer.Deserialize<Config>(
                File.ReadAllText(path));

            return cfg?.FfmpegPath is { } p && File.Exists(p) ? p : null;
        }

        private static string PromptAndSaveFfmpeg()
        {
            var input = AnsiConsole.Ask<string>("FFmpeg path:");
            var path = ResolveFfmpegPath(input);

            if (path == null)
            {
                AnsiConsole.MarkupLine("[red]Invalid FFmpeg path[/]");
                return null!;
            }

            File.WriteAllText(
                Path.Combine(AppContext.BaseDirectory, ConfigFileName),
                JsonSerializer.Serialize(
                    new Config { FfmpegPath = path },
                    new JsonSerializerOptions { WriteIndented = true })
            );

            return path;
        }

        private static string? ResolveFfmpegPath(string input)
        {
            if (File.Exists(input))
                return Path.GetFullPath(input);

            if (Directory.Exists(input))
            {
                var exe = OperatingSystem.IsWindows()
                    ? "ffmpeg.exe"
                    : "ffmpeg";

                var p1 = Path.Combine(input, exe);
                var p2 = Path.Combine(input, "bin", exe);

                if (File.Exists(p1)) return p1;
                if (File.Exists(p2)) return p2;
            }

            return null;
        }

        private class Config
        {
            public string? FfmpegPath { get; set; }
        }
    }
}
