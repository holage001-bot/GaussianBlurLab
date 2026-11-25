using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace GaussianBlurLab
{
    public class TileTask
    {
        public int Id { get; set; }
        public int StartY { get; set; }
        public int EndY { get; set; }
    }

    public class GaussianBlurProcessor
    {
        private readonly object _syncLock = new object();
        private readonly Queue<TileTask> _taskQueue = new Queue<TileTask>();
        private readonly List<string> _log = new List<string>();

        private readonly AutoResetEvent _taskAvailableEvent = new AutoResetEvent(false);
        private readonly ManualResetEventSlim _cancelEvent = new ManualResetEventSlim(false);
        private CountdownEvent _countdownEvent;
        private SemaphoreSlim _semaphore;

        private byte[] _sourcePixels;
        private byte[] _resultPixels;
        private int _width, _height, _stride;
        private int _completedTiles;
        private int _totalTiles;

        private static readonly int[,] Kernel5x5 = {
            { 1,  4,  6,  4, 1 },
            { 4, 16, 24, 16, 4 },
            { 6, 24, 36, 24, 6 },
            { 4, 16, 24, 16, 4 },
            { 1,  4,  6,  4, 1 }
        };
        private const int KernelSum = 256;

        public IReadOnlyList<string> GetLogs()
        {
            lock (_syncLock) { return new List<string>(_log); }
        }

        private void AddLog(string message)
        {
            lock (_syncLock)
            {
                _log.Add($"[{DateTime.Now:HH:mm:ss.fff}] [Поток #{Environment.CurrentManagedThreadId}] {message}");
            }
        }

        public async Task<Bitmap> LoadImageAsync(string filePath, CancellationToken token)
        {
            byte[] fileBytes;
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true))
            {
                fileBytes = new byte[fs.Length];
                int bytesRead = 0;
                while (bytesRead < fileBytes.Length)
                {
                    token.ThrowIfCancellationRequested();
                    int read = await fs.ReadAsync(fileBytes, bytesRead, fileBytes.Length - bytesRead, token);
                    if (read == 0) break;
                    bytesRead += read;
                }
            }
            using var ms = new MemoryStream(fileBytes);
            return new Bitmap(ms);
        }

        public async Task<(Bitmap Result, long ElapsedMs)> ProcessAsync(
            Bitmap sourceBitmap, int threadCount, int tileHeight, IProgress<int> progress)
        {
            _cancelEvent.Reset();
            lock (_syncLock) { _log.Clear(); }

            var sw = Stopwatch.StartNew();
            AddLog($"Старт: {sourceBitmap.Width}x{sourceBitmap.Height}, потоков: {threadCount}");

            _width = sourceBitmap.Width;
            _height = sourceBitmap.Height;

            Bitmap formattedBmp = new Bitmap(_width, _height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(formattedBmp))
                g.DrawImage(sourceBitmap, 0, 0, _width, _height);

            BitmapData bmpData = formattedBmp.LockBits(new Rectangle(0, 0, _width, _height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            _stride = Math.Abs(bmpData.Stride);
            _sourcePixels = new byte[_stride * _height];
            _resultPixels = new byte[_stride * _height];
            Marshal.Copy(bmpData.Scan0, _sourcePixels, 0, _sourcePixels.Length);
            formattedBmp.UnlockBits(bmpData);
            formattedBmp.Dispose();

            var tasks = new List<TileTask>();
            int id = 1;
            for (int y = 0; y < _height; y += tileHeight)
            {
                tasks.Add(new TileTask { Id = id++, StartY = y, EndY = Math.Min(y + tileHeight, _height) });
            }

            _totalTiles = tasks.Count;
            _completedTiles = 0;
            _countdownEvent = new CountdownEvent(_totalTiles);
            _semaphore = new SemaphoreSlim(threadCount, threadCount);

            lock (_syncLock)
            {
                _taskQueue.Clear();
                foreach (var t in tasks) _taskQueue.Enqueue(t);
            }
            AddLog($"Создано фрагментов: {_totalTiles}");

            Thread[] workers = new Thread[threadCount];
            for (int i = 0; i < threadCount; i++)
            {
                workers[i] = new Thread(() => WorkerLoop(progress)) { IsBackground = true };
                workers[i].Start();
            }

            _taskAvailableEvent.Set();

            await Task.Run(() =>
            {
                WaitHandle.WaitAny(new[] { _countdownEvent.WaitHandle, _cancelEvent.WaitHandle });
            });

            _cancelEvent.Set();
            _taskAvailableEvent.Set();
            foreach (var w in workers) w.Join();

            sw.Stop();

            if (_completedTiles < _totalTiles)
            {
                AddLog("Отменено пользователем.");
                throw new OperationCanceledException("Обработка отменена.");
            }

            AddLog($"Завершено за {sw.ElapsedMilliseconds} мс.");

            Bitmap resultBmp = new Bitmap(_width, _height, PixelFormat.Format32bppArgb);
            BitmapData resData = resultBmp.LockBits(new Rectangle(0, 0, _width, _height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            Marshal.Copy(_resultPixels, 0, resData.Scan0, _resultPixels.Length);
            resultBmp.UnlockBits(resData);

            return (resultBmp, sw.ElapsedMilliseconds);
        }

        private void WorkerLoop(IProgress<int> progress)
        {
            while (!_cancelEvent.IsSet)
            {
                TileTask task = null;

                lock (_syncLock)
                {
                    if (_taskQueue.Count > 0)
                    {
                        task = _taskQueue.Dequeue();
                        if (_taskQueue.Count > 0)
                            _taskAvailableEvent.Set();
                    }
                }

                if (task == null)
                {
                    WaitHandle.WaitAny(new[] { _taskAvailableEvent, _cancelEvent.WaitHandle });
                    continue;
                }

                _semaphore.Wait();
                try
                {
                    if (_cancelEvent.IsSet) return;

                    byte[] tileResult = ApplyGaussianBlurToTile(task);

                    lock (_syncLock)
                    {
                        if (_cancelEvent.IsSet) return;

                        int offset = task.StartY * _stride;
                        Buffer.BlockCopy(tileResult, 0, _resultPixels, offset, tileResult.Length);

                        _completedTiles++;
                        _log.Add($"[{DateTime.Now:HH:mm:ss.fff}] [Поток #{Environment.CurrentManagedThreadId}] Тайл #{task.Id} готов ({_completedTiles}/{_totalTiles})");

                        int percent = (int)((double)_completedTiles / _totalTiles * 100);
                        progress?.Report(percent);
                    }
                }
                finally
                {
                    _semaphore.Release();
                    _countdownEvent.Signal();
                }
            }
        }

        private byte[] ApplyGaussianBlurToTile(TileTask task)
        {
            int tileRows = task.EndY - task.StartY;
            byte[] output = new byte[tileRows * _stride];

            for (int y = task.StartY; y < task.EndY; y++)
            {
                int outRowOffset = (y - task.StartY) * _stride;

                for (int x = 0; x < _width; x++)
                {
                    int sumB = 0, sumG = 0, sumR = 0;

                    for (int ky = -2; ky <= 2; ky++)
                    {
                        int sampleY = Math.Clamp(y + ky, 0, _height - 1);
                        int rowOffset = sampleY * _stride;

                        for (int kx = -2; kx <= 2; kx++)
                        {
                            int sampleX = Math.Clamp(x + kx, 0, _width - 1);
                            int pixelOffset = rowOffset + sampleX * 4;
                            int weight = Kernel5x5[ky + 2, kx + 2];

                            sumB += _sourcePixels[pixelOffset] * weight;
                            sumG += _sourcePixels[pixelOffset + 1] * weight;
                            sumR += _sourcePixels[pixelOffset + 2] * weight;
                        }
                    }

                    int targetOffset = outRowOffset + x * 4;
                    output[targetOffset] = (byte)(sumB / KernelSum);
                    output[targetOffset + 1] = (byte)(sumG / KernelSum);
                    output[targetOffset + 2] = (byte)(sumR / KernelSum);
                    output[targetOffset + 3] = _sourcePixels[y * _stride + x * 4 + 3];
                }
            }
            return output;
        }

        public async Task SaveImageAsync(Bitmap bitmap, string filePath, CancellationToken token)
        {
            using var ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Png);
            byte[] data = ms.ToArray();

            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            await fs.WriteAsync(data, 0, data.Length, token);
        }

        public void Cancel() => _cancelEvent.Set();
    }
}
