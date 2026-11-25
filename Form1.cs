using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace GaussianBlurLab
{
    public partial class Form1 : Form
    {
        private readonly GaussianBlurProcessor _processor = new GaussianBlurProcessor();
        private Bitmap _sourceBitmap;
        private Bitmap _resultBitmap;
        private CancellationTokenSource _ioCts;

        private PictureBox picSource, picResult;
        private Button btnLoad, btnStart, btnCancel, btnSave;
        private NumericUpDown numThreads;
        private ProgressBar progressBar;
        private Label lblStatus;
        private ListBox lstLog;

        public Form1()
        {
            this.Text = "Лабораторная работа — Вариант 7 (Размытие по Гауссу 5x5)";
            this.Size = new Size(1220, 520);
            this.StartPosition = FormStartPosition.CenterScreen;

            btnLoad = new Button { Text = "1. Открыть фото", Location = new Point(15, 15), Size = new Size(130, 35) };
            btnLoad.Click += BtnLoad_Click;

            var lblThreads = new Label { Text = "Потоков:", Location = new Point(160, 23), AutoSize = true };
            numThreads = new NumericUpDown { Location = new Point(235, 20), Size = new Size(60, 25), Minimum = 1, Maximum = 32, Value = 4 };

            btnStart = new Button { Text = "2. Старт", Location = new Point(315, 15), Size = new Size(110, 35), Enabled = false };
            btnStart.Click += BtnStart_Click;

            btnCancel = new Button { Text = "Отмена", Location = new Point(435, 15), Size = new Size(100, 35), Enabled = false };
            btnCancel.Click += BtnCancel_Click;

            btnSave = new Button { Text = "3. Сохранить", Location = new Point(545, 15), Size = new Size(120, 35), Enabled = false };
            btnSave.Click += BtnSave_Click;

            progressBar = new ProgressBar { Location = new Point(15, 60), Size = new Size(650, 22) };
            lblStatus = new Label { Text = "Загрузите изображение для начала работы.", Location = new Point(680, 23), AutoSize = true, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };

            picSource = new PictureBox { Location = new Point(15, 95), Size = new Size(360, 360), BorderStyle = BorderStyle.FixedSingle, SizeMode = PictureBoxSizeMode.Zoom };
            picResult = new PictureBox { Location = new Point(390, 95), Size = new Size(360, 360), BorderStyle = BorderStyle.FixedSingle, SizeMode = PictureBoxSizeMode.Zoom };

            var lblLog = new Label { Text = "Журнал конвейера (под защитой lock):", Location = new Point(765, 70), AutoSize = true };
            lstLog = new ListBox { Location = new Point(765, 95), Size = new Size(420, 360) };

            this.Controls.AddRange(new Control[] { btnLoad, lblThreads, numThreads, btnStart, btnCancel, btnSave, progressBar, lblStatus, picSource, picResult, lblLog, lstLog });
        }


        private async void BtnLoad_Click(object sender, EventArgs e)
        {
            using var ofd = new OpenFileDialog { Filter = "Изображения|*.png;*.jpg;*.jpeg;*.bmp" };
            if (ofd.ShowDialog() != DialogResult.OK) return;

            _ioCts = new CancellationTokenSource();
            lblStatus.Text = "Асинхронная загрузка...";
            btnLoad.Enabled = false;

            try
            {
                _sourceBitmap?.Dispose();
                _sourceBitmap = await _processor.LoadImageAsync(ofd.FileName, _ioCts.Token);
                picSource.Image = _sourceBitmap;
                picResult.Image = null;
                btnStart.Enabled = true;
                btnSave.Enabled = false;
                lblStatus.Text = $"Загружено: {_sourceBitmap.Width}x{_sourceBitmap.Height}";
            }
            catch (Exception ex) { MessageBox.Show($"Ошибка: {ex.Message}"); }
            finally { btnLoad.Enabled = true; }
        }

        private async void BtnStart_Click(object sender, EventArgs e)
        {
            if (_sourceBitmap == null) return;

            btnLoad.Enabled = false;
            btnStart.Enabled = false;
            btnSave.Enabled = false;
            btnCancel.Enabled = true;
            progressBar.Value = 0;
            lstLog.Items.Clear();
            lblStatus.Text = "Обработка...";

            int threads = (int)numThreads.Value;
            int tileHeight = Math.Max(16, _sourceBitmap.Height / (threads * 4));
            var progress = new Progress<int>(p => progressBar.Value = Math.Clamp(p, 0, 100));

            try
            {
                _resultBitmap?.Dispose();
                var (result, elapsedMs) = await _processor.ProcessAsync(_sourceBitmap, threads, tileHeight, progress);
                _resultBitmap = result;
                picResult.Image = _resultBitmap;
                btnSave.Enabled = true;
                lblStatus.Text = $"Готово! Время: {elapsedMs} мс (потоков: {threads})";
            }
            catch (OperationCanceledException)
            {
                _resultBitmap = null;
                picResult.Image = null;
                progressBar.Value = 0;
                lblStatus.Text = "Отменено. Частичный результат отброшен.";
            }
            catch (Exception ex) { MessageBox.Show($"Ошибка: {ex.Message}"); }
            finally
            {
                foreach (var line in _processor.GetLogs()) lstLog.Items.Add(line);
                btnLoad.Enabled = true;
                btnStart.Enabled = true;
                btnCancel.Enabled = false;
            }
        }

        private void BtnCancel_Click(object sender, EventArgs e)
        {
            btnCancel.Enabled = false;
            lblStatus.Text = "Отмена...";
            _processor.Cancel();
        }

        private async void BtnSave_Click(object sender, EventArgs e)
        {
            if (_resultBitmap == null) return;

            using var sfd = new SaveFileDialog { Filter = "PNG Image|*.png", FileName = "blurred_result.png" };
            if (sfd.ShowDialog() != DialogResult.OK) return;

            _ioCts = new CancellationTokenSource();
            btnSave.Enabled = false;
            lblStatus.Text = "Асинхронное сохранение...";

            try
            {
                await _processor.SaveImageAsync(_resultBitmap, sfd.FileName, _ioCts.Token);
                lblStatus.Text = "Сохранено успешно!";
            }
            catch (Exception ex) { MessageBox.Show($"Ошибка: {ex.Message}"); }
            finally { btnSave.Enabled = true; }
        }
    }
}
