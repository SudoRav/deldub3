using Microsoft.WindowsAPICodePack.Dialogs;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace deldub3
{
    public partial class MainWindow : Window
    {
        private static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".webp", ".gif", };
        private static readonly string[] VideoExts = { ".mp4", ".webm" };

        public MainWindow()
        {
            InitializeComponent();
            prefZ.IsChecked = false;
        }

        #region 1. Случайное переименование файлов (все типы)
        private async void Button_Click1(object sender, RoutedEventArgs e)
        {
            var folder = SelectFolder();
            if (folder == null) return;

            var allowedExts = new[]
            {
        ".png", ".jpg", ".jpeg", ".webp",
        ".gif", ".mp4", ".webm"
    };

            var files = Directory.EnumerateFiles(folder)
                .Where(f => allowedExts.Contains(Path.GetExtension(f).ToLower()))
                .ToList();

            if (!files.Any())
            {
                MessageBox.Show("Файлов не найдено");
                return;
            }

            int processed = 0;
            ProgressBarStatus.Maximum = files.Count;

            await Task.Run(() =>
            {
                Parallel.ForEach(files, new ParallelOptions
                {
                    MaxDegreeOfParallelism = Environment.ProcessorCount - 1
                },
                file =>
                {
                    try
                    {
                        string ext = Path.GetExtension(file);

                        // потокобезопасный Random
                        var rnd = new Random(Guid.NewGuid().GetHashCode());

                        string newName;
                        string target;

                        do
                        {
                            newName = $"{rnd.Next(100000, 999999)}-{rnd.Next(100000, 999999)}{ext}";
                            target = Path.Combine(folder, newName);
                        }
                        while (File.Exists(target));

                        File.Move(file, target);
                    }
                    catch { }

                    int p = Interlocked.Increment(ref processed);
                    if (p % 10 == 0)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            ProgressBarStatus.Value = p;
                            ProgressText.Text = $"Переименование {p}/{files.Count}";
                        });
                    }
                });
            });

            ProgressText.Text = "Готово";
        }
        #endregion

        #region 2 & 3. Конвертация файлов в .png
        private async void Button_Click23(object sender, RoutedEventArgs e)
        {
            var folder = SelectFolder();
            if (folder == null) return;

            // Фильтруем только нужные форматы
            var files = Directory.EnumerateFiles(folder)
                .Where(f => new[] { ".jpg", ".jpeg", ".webp" }
                    .Contains(Path.GetExtension(f).ToLower()))
                .ToList();

            if (!files.Any())
            {
                MessageBox.Show("Файлов не найдено");
                return;
            }

            int processed = 0;
            ProgressBarStatus.Maximum = files.Count;

            await Task.Run(() =>
            {
                Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount - 1 }, file =>
                {
                    try
                    {
                        using var img = Image.Load<Rgba32>(file); // загружаем изображение
                        string target = Path.ChangeExtension(file, ".png");

                        img.Save(target, new PngEncoder()); // сохраняем в PNG
                        File.Delete(file); // удаляем старый файл после успешного сохранения
                    }
                    catch
                    {
                        // Игнорируем ошибки
                    }

                    // Обновляем прогресс каждые 10 файлов
                    int p = Interlocked.Increment(ref processed);
                    if (p % 10 == 0)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            ProgressBarStatus.Value = p;
                            ProgressText.Text = $"Конвертация {p}/{files.Count}";
                        });
                    }
                });
            });

            ProgressText.Text = "Готово";
        }
        #endregion

        #region 4. Переименование по пикселям + видео/gif через ffmpeg
        private async void Button_Click4(object sender, RoutedEventArgs e)
        {
            var folder = SelectFolder();
            if (folder == null) return;

            var files = Directory.EnumerateFiles(folder)
                .Where(f =>
                {
                    string ext = Path.GetExtension(f).ToLower();

                    bool isMedia =
                        ImageExts.Contains(ext, StringComparer.OrdinalIgnoreCase) ||
                        ext == ".mp4" || ext == ".gif";

                    if (!isMedia) return false;

                    if (!prefZ.IsChecked.Value && Path.GetFileName(f).StartsWith("="))
                        return false;

                    return true;
                })
                .ToList();

            if (!files.Any())
            {
                MessageBox.Show("Файлов не найдено");
                return;
            }

            ProgressBarStatus.Maximum = files.Count;

            int processed = 0;

            var items = new ConcurrentBag<(string Path, string Code, string Ext)>();

            // ограничение ffmpeg
            var ffmpegSemaphore = new SemaphoreSlim(2);

            await Task.Run(() =>
            {
                Parallel.ForEach(files, new ParallelOptions
                {
                    MaxDegreeOfParallelism = Environment.ProcessorCount - 1
                },
                file =>
                {
                    try
                    {
                        string ext = Path.GetExtension(file).ToLower();
                        string code = null;

                        if (ImageExts.Contains(ext, StringComparer.OrdinalIgnoreCase))
                        {
                            using var img = Image.Load<Rgba32>(file);
                            code = GetImageCode(img);
                        }
                        else if (ext == ".mp4" || ext == ".gif")
                        {
                            ffmpegSemaphore.Wait();

                            try
                            {
                                string tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");

                                ExtractFrame(file, tmp);

                                using var img = Image.Load<Rgba32>(tmp);
                                code = GetImageCode(img);

                                File.Delete(tmp);
                            }
                            finally
                            {
                                ffmpegSemaphore.Release();
                            }
                        }

                        if (code != null)
                            items.Add((file, code, ext));
                    }
                    catch { }

                    int p = Interlocked.Increment(ref processed);
                    if (p % 10 == 0)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            ProgressBarStatus.Value = p;
                            ProgressText.Text = $"Анализ {p}/{files.Count}";
                        });
                    }
                });
            });

            // группировка
            var groups = items.GroupBy(x => x.Code);

            processed = 0;

            foreach (var group in groups)
            {
                var list = group.ToList();

                for (int i = 0; i < list.Count; i++)
                {
                    var item = list[i];

                    string expectedName = list.Count == 1
                        ? $"={item.Code}{item.Ext}"
                        : $"={item.Code}_{i}{item.Ext}";

                    string target = Path.Combine(folder, expectedName);

                    // ✅ FIX: если имя уже корректное — пропускаем
                    if (Path.GetFileName(item.Path)
                        .Equals(expectedName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    int k = 0;
                    while (File.Exists(target))
                    {
                        if (Path.GetFullPath(target)
                            .Equals(Path.GetFullPath(item.Path), StringComparison.OrdinalIgnoreCase))
                            break;

                        target = Path.Combine(folder, $"={item.Code}_{i}_{k}{item.Ext}");
                        k++;
                    }

                    try
                    {
                        File.Move(item.Path, target);
                    }
                    catch { }

                    processed++;
                    if (processed % 10 == 0)
                    {
                        ProgressBarStatus.Value = processed;
                        ProgressText.Text = $"Переименование {processed}/{items.Count}";
                    }
                }
            }

            ProgressText.Text = "Готово";
        }

        private void ExtractFrame(string input, string output)
        {
            var psi = new ProcessStartInfo
            {
                FileName = @"D:\Programs2\ffmpeg-8.0-full_build\bin\ffmpeg.exe",
                Arguments = $"-y -i \"{input}\" -frames:v 1 \"{output}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            };

            using var p = Process.Start(psi);
            p.WaitForExit();
        }

        private string GetImageCode(Image<Rgba32> img)
        {
            int w = img.Width;
            int h = img.Height;

            int x1 = w / 3;
            int x2 = 2 * w / 3;
            int y1 = h / 3;
            int y2 = 2 * h / 3;

            var c1 = img[x1, y1];
            var c2 = img[x1, y2];
            var c3 = img[x2, y1];
            var c4 = img[x2, y2];

            return $"{ToCode(c1)}{ToCode(c2)}-{ToCode(c3)}{ToCode(c4)}";
        }
        #endregion

        #region 5. Удаление дубликатов по схожести
        private async void Button_Click5(object sender, RoutedEventArgs e)
        {
            var folder = SelectFolder();
            if (folder == null) return;

            if (!TryGetThreshold(TxtThreshold.Text, out double threshold))
            {
                MessageBox.Show("Введите процент от 0 до 100");
                return;
            }

            var progress = new Progress<int>(percent => ProgressBarStatus.Value = percent);

            int deleted = await Task.Run(() => ImageDuplicateRemover.RemoveVisualDuplicates(folder, progress, threshold));

            ProgressText.Text = $"Готово. Удалено {deleted} файлов.";
        }
        #endregion

        #region 6. Удаление из второй папки дубликатов файлов из первой папки
        private async void Button_Click6(object sender, RoutedEventArgs e)
        {
            ProgressText.Text = "Выберите папку-источник. Файлы из неё удаляться не будут.";
            var folder1 = SelectFolder();
            if (folder1 == null) return;

            ProgressText.Text = "Выберите папку, из которой нужно удалить найденные дубликаты.";
            var folder2 = SelectFolder();
            if (folder2 == null) return;

            if (!TryGetThreshold(TxtThresholdToPath.Text, out double threshold))
            {
                MessageBox.Show("Введите процент от 0 до 100");
                return;
            }

            ProgressBarStatus.Value = 0;
            var progress = new Progress<int>(percent => ProgressBarStatus.Value = percent);

            int deleted = await Task.Run(() =>
                ImageDuplicateRemover.RemoveVisualDuplicatesFromSecondFolder(folder1, folder2, progress, threshold));

            ProgressText.Text = $"Готово. Удалено {deleted} файлов из второй папки.";
        }
        #endregion

        #region Вспомогательные функции
        private string SelectFolder()
        {
            var dialog = new CommonOpenFileDialog { IsFolderPicker = true };
            if (dialog.ShowDialog() != CommonFileDialogResult.Ok) return null;
            return dialog.FileName;
        }

        private bool TryGetThreshold(string text, out double threshold)
        {
            bool parsed = double.TryParse(text?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out threshold);
            return parsed && threshold >= 0 && threshold <= 100;
        }

        private string ToCode(Rgba32 c)
        {
            int avg = (c.R + c.G + c.B) / 3;
            int val = (int)(avg / 255.0 * 999);
            return val.ToString("000");
        }
        #endregion
    }

    public static class ImageDuplicateRemover
    {
        private static readonly string[] Extensions = { ".png", ".jpg", ".jpeg" };

        public static int RemoveVisualDuplicates(string folderPath, IProgress<int> progress, double similarityThreshold = 99.5)
        {
            var files = Directory.GetFiles(folderPath, "*.*", SearchOption.AllDirectories)
                                 .Where(f => Extensions.Contains(Path.GetExtension(f).ToLower()))
                                 .ToList();

            int total = files.Count;
            int processed = 0;
            int deletedCount = 0;

            if (total == 0)
            {
                progress.Report(100);
                return 0;
            }

            var hashes = new ConcurrentDictionary<string, string>();

            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount - 1 }, file =>
            {
                try
                {
                    string hash = GetPerceptualHash(file); // 16x16 хэш

                    bool isDuplicate = false;
                    foreach (var existing in hashes.Keys)
                    {
                        double similarity = CompareHashes(existing, hash);
                        if (similarity >= similarityThreshold)
                        {
                            isDuplicate = true;
                            break;
                        }
                    }

                    if (isDuplicate)
                    {
                        File.Delete(file);
                        Interlocked.Increment(ref deletedCount);
                    }
                    else
                    {
                        hashes.TryAdd(hash, file);
                    }
                }
                catch { }

                int p = Interlocked.Increment(ref processed);
                progress.Report((int)((p / (double)total) * 100));
            });

            return deletedCount;
        }
        public static int RemoveVisualDuplicatesFromSecondFolder(string sourceFolderPath, string targetFolderPath, IProgress<int> progress, double similarityThreshold = 99.5)
        {
            var sourceFiles = Directory.GetFiles(sourceFolderPath, "*.*", SearchOption.AllDirectories)
                                       .Where(f => Extensions.Contains(Path.GetExtension(f).ToLower()))
                                       .ToList();
            var targetFiles = Directory.GetFiles(targetFolderPath, "*.*", SearchOption.AllDirectories)
                                       .Where(f => Extensions.Contains(Path.GetExtension(f).ToLower()))
                                       .ToList();

            int total = targetFiles.Count;
            int processed = 0;
            int deletedCount = 0;

            if (total == 0)
            {
                progress.Report(100);
                return 0;
            }

            var sourceHashes = new ConcurrentBag<string>();

            Parallel.ForEach(sourceFiles, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount - 1 }, file =>
            {
                try
                {
                    sourceHashes.Add(GetPerceptualHash(file));
                }
                catch { }
            });

            Parallel.ForEach(targetFiles, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount - 1 }, file =>
            {
                try
                {
                    string hash = GetPerceptualHash(file);
                    bool isDuplicate = sourceHashes.Any(existing => CompareHashes(existing, hash) >= similarityThreshold);

                    if (isDuplicate)
                    {
                        File.Delete(file);
                        Interlocked.Increment(ref deletedCount);
                    }
                }
                catch { }

                int p = Interlocked.Increment(ref processed);
                progress.Report((int)((p / (double)total) * 100));
            });

            return deletedCount;
        }

        private static string GetPerceptualHash(string path)
        {
            using var img = Image.Load<Rgba32>(path);
            img.Mutate(x => x.Resize(16, 16)); // 16x16 → 256 бит

            ulong hashHigh = 0;
            ulong hashLow = 0;
            byte[] pixels = new byte[256];
            double avg = 0;
            int idx = 0;

            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    int lum = (img[x, y].R + img[x, y].G + img[x, y].B) / 3;
                    pixels[idx++] = (byte)lum;
                    avg += lum;
                }
            }

            avg /= 256;

            for (int i = 0; i < 128; i++)
            {
                if (pixels[i] >= avg) hashHigh |= 1UL << i;
            }
            for (int i = 128; i < 256; i++)
            {
                if (pixels[i] >= avg) hashLow |= 1UL << (i - 128);
            }

            return hashHigh.ToString("X16") + hashLow.ToString("X16");
        }

        private static double CompareHashes(string hash1, string hash2)
        {
            ulong h1High = Convert.ToUInt64(hash1.Substring(0, 16), 16);
            ulong h1Low = Convert.ToUInt64(hash1.Substring(16, 16), 16);

            ulong h2High = Convert.ToUInt64(hash2.Substring(0, 16), 16);
            ulong h2Low = Convert.ToUInt64(hash2.Substring(16, 16), 16);

            int diff = 0;
            for (int i = 0; i < 64; i++)
            {
                if (((h1High >> i) & 1) != ((h2High >> i) & 1)) diff++;
                if (((h1Low >> i) & 1) != ((h2Low >> i) & 1)) diff++;
            }

            return 100.0 * (256 - diff) / 256.0;
        }
    }
}