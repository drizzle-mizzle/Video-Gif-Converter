using FFMpegCore;
using FFMpegCore.Pipes;
using PhotoSauce.MagicScaler;

namespace GifConverter;


internal static class Program
{
    private static readonly string BASE_DIRECTORY = AppDomain.CurrentDomain.BaseDirectory;
    private static readonly Dictionary<string, string> CONFIG = GetConfig();
    private static readonly object RW_LOCK = new();

    private static readonly DirectoryInfo INPUT_FOLDER = new(CONFIG["INPUT_FOLDER"]);
    private static readonly DirectoryInfo OUTPUT_FOLDER = new(CONFIG["OUTPUT_FOLDER"]);
    private static readonly DirectoryInfo TEMP_FOLDER = new(Path.Combine(BASE_DIRECTORY, "TEMP"));

    private static readonly int MAX_GIF_SIZE_KB = int.Parse(CONFIG["MAX_GIF_SIZE_KB"]);
    private static readonly int MAX_GIF_FPS = int.Parse(CONFIG["MAX_GIF_FPS"]);
    private static readonly int MAX_GIF_HEIGHT_PX = int.Parse(CONFIG["MAX_GIF_HEIGHT_PX"]);
    private static readonly bool MOVE_PROCESSED_FILES_TO_OUTPUT_FOLDER = bool.Parse(CONFIG["MOVE_PROCESSED_FILES_TO_OUTPUT_FOLDER"]);


    public static async Task Main()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log($"UnhandledException:\n{args.ExceptionObject}");
        };

        if (!OUTPUT_FOLDER.Exists)
        {
            OUTPUT_FOLDER.Create();
        }

        var files = INPUT_FOLDER.GetFiles("*", SearchOption.AllDirectories);

        await Parallel.ForEachAsync(files, async (file, ct) =>
        {
            var trace = Guid.NewGuid().ToString()[..5];
            try
            {

                var mediaInfo = await FFProbe.AnalyseAsync(file.FullName, cancellationToken: ct);
                var fps = Math.Min((int)mediaInfo.PrimaryVideoStream!.AvgFrameRate + 1, MAX_GIF_FPS);
                var height = Math.Min(mediaInfo.PrimaryVideoStream!.Height, MAX_GIF_HEIGHT_PX);

                Log($"Processing file: \"{file.Name}\"", trace);

                var ms = new MemoryStream();
                await file.ConvertToGifAsync(ms, fps, height);

                var settings = new ProcessImageSettings
                {
                    Interpolation = InterpolationSettings.Lanczos,
                    HybridMode = HybridScaleMode.FavorQuality,
                    ColorProfileMode = ColorProfileMode.ConvertToSrgb
                };

                var compressionStep = -1;

                const int MAX_COLOR_COMPRESSION = 7;
                using (var tempMs = new MemoryStream())
                {
                    while (true)
                    {
                        var fileSize = (int)(ms.Length / 1024);
                        if (compressionStep == MAX_COLOR_COMPRESSION || (fileSize != 0 && fileSize <= MAX_GIF_SIZE_KB))
                        {
                            break;
                        }

                        compressionStep++;
                        var colorCompression = 128 - compressionStep * 16;
                        Log($"Applying additional color compression [{compressionStep}/{MAX_COLOR_COMPRESSION}]", trace);

                        ms.Position = 0;
                        tempMs.SetLength(0);
                        settings.EncoderOptions = new GifEncoderOptions(colorCompression, null, DitherMode.Auto);
                        MagicImageProcessor.ProcessImage(ms, tempMs, settings);

                        ms.SetLength(0);
                        tempMs.Position = 0;
                        await tempMs.CopyToAsync(ms, ct);
                    }
                }

                var newPath = file.FullName.Replace(INPUT_FOLDER.FullName, OUTPUT_FOLDER.FullName);
                var extIndex = file.FullName.LastIndexOf('.') + 1;
                var outputFile = new FileInfo($"{newPath[..extIndex]}" + (compressionStep == 0 ? "" : $" [c{compressionStep}]") + ".gif");

                lock (RW_LOCK)
                {
                    if (!outputFile.Directory!.Exists)
                    {
                        outputFile.Directory.Create();
                    }
                }

                Log($"Writing output file: \"{outputFile.Name}\"; size = {ms.Length / 1024}kb; scale = {height}p; framerate={fps}fps; compression = {compressionStep}", trace);

                lock (RW_LOCK)
                {
                    File.WriteAllBytes(outputFile.FullName, ms.ToArray());
                    ms.Dispose();
                }

                if (MOVE_PROCESSED_FILES_TO_OUTPUT_FOLDER)
                {
                    var procssedFilePath = file.FullName.Replace(INPUT_FOLDER.FullName, Path.Combine(OUTPUT_FOLDER.FullName, "_processed"));
                    var processedFile = new FileInfo(procssedFilePath);

                    Log($"Moving processed file: \"{file.FullName}\" -> \"{processedFile.FullName}\"");
                    lock (RW_LOCK)
                    {
                        if (!processedFile.Directory!.Exists)
                        {
                            processedFile.Directory.Create();
                        }

                        file.MoveTo(processedFile.FullName);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to process file \"{file.Name}\":\n{ex}", trace);
            }
        });

        if (TEMP_FOLDER.Exists)
        {
            TEMP_FOLDER.Delete(recursive: true);
            TEMP_FOLDER.Create();
        }
    }


    private static async Task ConvertToGifAsync(this FileInfo file, MemoryStream ms, int fps, int height)
    {
        var outputPipeSink = new StreamPipeSink(ms);
        await FFMpegArguments.FromFileInput(file.FullName, false)
                             .OutputToPipe(outputPipeSink, args =>
                              {
                                  args.WithCustomArgument($"-filter_complex \"split[a][b];[a]palettegen[pg];[b]fps={fps}[b];[b]scale=w=-1:h={height},mpdecimate[b];[b][pg]paletteuse=dither=bayer\" -vsync 0 -loop 0 -final_delay 50")
                                      .ForceFormat("gif");
                              })
                             .ProcessAsynchronously();
    }


    private static Dictionary<string, string> GetConfig()
        => File.ReadLines(Path.Combine(BASE_DIRECTORY, "config.txt"))
               .Where(line => !line.StartsWith('#') && line.Contains(':'))
               .Select(line =>
                {
                    var values = line.Split(':');
                    var key = values[0];
                    var value = values[1];

                    return (key, value.Trim());
                })
               .ToDictionary();


    private static void Log(string text, string? trace = "?")
    {
        lock (RW_LOCK)
        {
            File.AppendAllLines(Path.Combine(BASE_DIRECTORY, "log.txt"), [$"[{DateTime.Now:u}|{trace}] {text}"]);
        }
    }
}
