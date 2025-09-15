using System.Collections.Concurrent;
using ImageMagick;

Console.WriteLine("SVG -> ICO realtime exporter");

string folder = args.Length > 0 ? args[0] : string.Empty;
if (string.IsNullOrWhiteSpace(folder))
{
    Console.Write("Enter folder to watch: ");
    folder = Console.ReadLine() ?? string.Empty;
}

if (!Directory.Exists(folder))
{
    Console.WriteLine($"Folder does not exist: {folder}");
    return;
}

using var watcher = new FileSystemWatcher(folder, "*.svg")
{
    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
    IncludeSubdirectories = false,
    EnableRaisingEvents = true
};

var debounceMap = new ConcurrentDictionary<string, CancellationTokenSource>();

watcher.Created += OnChanged;
watcher.Changed += OnChanged;
watcher.Renamed += OnRenamed;
watcher.Error += (s, e) => Console.WriteLine($"Watcher error: {e.GetException()}");

// Convert existing SVGs on startup
try
{
    foreach (var file in Directory.EnumerateFiles(folder, "*.svg"))
    {
        ScheduleConvert(file);
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Failed to enumerate existing SVGs: {ex.Message}");
}

Console.WriteLine($"Watching folder: {folder}");
Console.WriteLine("Press Ctrl+C to exit.");

var exitEvent = new ManualResetEventSlim(false);
Console.CancelKeyPress += (s, e) => { e.Cancel = true; exitEvent.Set(); };
exitEvent.Wait();

void OnRenamed(object? sender, RenamedEventArgs e)
{
    if (Path.GetExtension(e.FullPath).Equals(".svg", StringComparison.OrdinalIgnoreCase))
        ScheduleConvert(e.FullPath);
}

void OnChanged(object? sender, FileSystemEventArgs e)
{
    if (!Path.GetExtension(e.FullPath).Equals(".svg", StringComparison.OrdinalIgnoreCase))
        return;

    ScheduleConvert(e.FullPath);
}

void ScheduleConvert(string path)
{
    var cts = new CancellationTokenSource();
    debounceMap.AddOrUpdate(path, cts, (k, old) => { old.Cancel(); old.Dispose(); return cts; });

    _ = Task.Run(async () =>
    {
        try
        {
            await Task.Delay(500, cts.Token);

            for (int i = 0; i < 5; i++)
            {
                if (cts.IsCancellationRequested) return;
                try
                {
                    if (!File.Exists(path)) return;
                    ConvertSvgToIco(path);
                    Console.WriteLine($"Exported: {Path.GetFileName(path)} -> {Path.GetFileNameWithoutExtension(path)}.ico");
                    return;
                }
                catch (IOException)
                {
                    await Task.Delay(300, cts.Token);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to convert {path}: {ex.Message}");
                    return;
                }
            }

            Console.WriteLine($"Could not access file: {path}");
        }
        catch (OperationCanceledException) { }
        finally
        {
            debounceMap.TryRemove(path, out var _);
            cts.Dispose();
        }
    });
}

void ConvertSvgToIco(string svgPath)
{
    // Use Magick.NET to rasterize SVG and build ICO with multiple sizes
    int[] sizes = new[] { 256, 128, 64, 48, 32, 16 };

    using var images = new MagickImageCollection();

    for (int i = 0; i < sizes.Length; i++)
    {
        var size = (uint)sizes[i];
        var settings = new MagickReadSettings
        {
            Width = size,
            Height = size,
            BackgroundColor = MagickColors.Transparent
        };

        // MagickImage will read and rasterize the SVG. Read from file each time to avoid stream reuse issues.
        using var img = new MagickImage(svgPath, settings);

        // Some delegates may produce images with slightly different dimensions; ensure exact size.
        if (img.Width != (int)size || img.Height != (int)size)
            img.Resize(size, size);

        img.HasAlpha = true;
        img.BackgroundColor = MagickColors.Transparent;

        images.Add(img.Clone());
    }

    var icoPath = Path.Combine(Path.GetDirectoryName(svgPath) ?? string.Empty, Path.GetFileNameWithoutExtension(svgPath) + ".ico");
    // Write ICO (Magick will pack images into the ICO)
    images.Write(icoPath, MagickFormat.Ico);
}
