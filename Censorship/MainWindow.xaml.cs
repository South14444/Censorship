using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace Censorship
{
    public partial class MainWindow : Window
    {
        private CancellationTokenSource _cancellationTokenSource; 
        private bool _isPaused; 
        private readonly object _pauseLock = new(); 
        private ObservableCollection<FileProcessingResult> _results = new(); 
        private static Mutex _mutex;
        public MainWindow()
        {
            const string appName = "CensorshipApp"; 
            bool createdNew;
            _mutex = new Mutex(true, appName, out createdNew);
            if (!createdNew)
            {
                MessageBox.Show("Приложение уже запущено.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                Application.Current.Shutdown();
                return;
            }
            InitializeComponent();
            dataGridResults.ItemsSource = _results; 
        }
        private void btnSelectWordsFile_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Title = "Выберите файл со списком запрещенных слов",
                Filter = "Text Files (*.txt)|*.txt|All Files (*.*)|*.*"
            };
            if (openFileDialog.ShowDialog() == true)
            {
                txtForbiddenWordsPath.Text = openFileDialog.FileName;
            }
        }
        private void btnSelectFolder_Click(object sender, RoutedEventArgs e)
        {
            FolderPicker folderPicker = new FolderPicker();
            if (folderPicker.ShowDialog(new WindowInteropHelper(this).Handle))
            {
                txtFolderPath.Text = folderPicker.ResultPath;
            }
        }
        private async void btnStart_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(txtForbiddenWordsPath.Text) || string.IsNullOrEmpty(txtFolderPath.Text))
            {
                MessageBox.Show("Пожалуйста, выберите файл и папку перед началом.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            btnStart.IsEnabled = false;
            btnPause.IsEnabled = true;
            btnStop.IsEnabled = true;
            progressBar.Value = 0;
            _cancellationTokenSource = new CancellationTokenSource();
            _isPaused = false;
            try
            {
                _results.Clear(); 
                await ProcessFilesAsync(txtFolderPath.Text, txtForbiddenWordsPath.Text, _cancellationTokenSource.Token);
                MessageBox.Show("Обработка завершена!", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                MessageBox.Show("Обработка отменена.", "Информация", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Произошла ошибка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                btnStart.IsEnabled = true;
                btnPause.IsEnabled = false;
                btnStop.IsEnabled = false;
            }
        }
        private void btnStop_Click(object sender, RoutedEventArgs e)
        {
            _cancellationTokenSource?.Cancel();
            btnPause.Content = "Пауза";
            btnPause.IsEnabled = false;
            btnStop.IsEnabled = false;
        }
        private void btnPause_Click(object sender, RoutedEventArgs e)
        {
            if (_isPaused)
            {
                lock (_pauseLock)
                {
                    _isPaused = false;
                    Monitor.PulseAll(_pauseLock);
                }
                btnPause.Content = "Пауза";
            }
            else
            {
                _isPaused = true;
                btnPause.Content = "Возобновить";
            }
        }
        private void btnSaveToFile_Click(object sender, RoutedEventArgs e)
        {
            if (!_results.Any())
            {
                MessageBox.Show("Нет данных для сохранения.", "Информация", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            SaveFileDialog saveFileDialog = new SaveFileDialog
            {
                Title = "Сохранить как",
                Filter = "CSV Files (*.csv)|*.csv|Text Files (*.txt)|*.txt|All Files (*.*)|*.*",
                FileName = "DataGridExport.csv"
            };
            if (saveFileDialog.ShowDialog() == true)
            {
                try
                {
                    using (StreamWriter writer = new StreamWriter(saveFileDialog.FileName))
                    {
                        writer.WriteLine("File Name, Forbidden Words Found"); 
                        foreach (var result in _results)
                        {
                            writer.WriteLine($"{result.FileName}, \"{string.Join(", ", result.ForbiddenWordsFound)}\"");
                        }
                    }
                    MessageBox.Show("Данные успешно сохранены!", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Ошибка при сохранении файла: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
        private async Task ProcessFilesAsync(string folderPath, string forbiddenWordsFilePath, CancellationToken cancellationToken)
        {
            var forbiddenWords = await File.ReadAllLinesAsync(forbiddenWordsFilePath);
            var files = Directory.GetFiles(folderPath, "*.txt");
            progressBar.Maximum = files.Length;
            var wordUsageStats = new Dictionary<string, int>();
            foreach (var word in forbiddenWords)
            {
                wordUsageStats[word] = 0;
            }
            var reportData = new List<FileProcessingResult>();
            for (int i = 0; i < files.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_pauseLock)
                {
                    while (_isPaused)
                    {
                        Monitor.Wait(_pauseLock);
                    }
                }
                try
                {
                    string content = await File.ReadAllTextAsync(files[i]);
                    string originalFileName = Path.GetFileName(files[i]);
                    string newFileName = Path.Combine(folderPath, $"Copy_{originalFileName}");
                    var foundWords = new Dictionary<string, int>();
                    long fileSize = new FileInfo(files[i]).Length;
                    foreach (var word in forbiddenWords)
                    {
                        int count = Regex.Matches(content, Regex.Escape(word)).Count; 
                        if (count > 0)
                        {
                            content = content.Replace(word, new string('*', 7)); 
                            foundWords[word] = count;
                            wordUsageStats[word] += count; 
                        }
                    }
                    if (foundWords.Any())
                    {
                        await File.WriteAllTextAsync(newFileName, content);
                    }
                    reportData.Add(new FileProcessingResult
                    {
                        FileName = originalFileName,
                        FileSize = fileSize,
                        ForbiddenWordsFound = foundWords
                    });
                    progressBar.Value = i + 1;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Ошибка при обработке файла {files[i]}: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                await Task.Delay(100);
            }
            await GenerateReportAsync(folderPath, reportData, wordUsageStats);
        }
        private async Task GenerateReportAsync(string folderPath, List<FileProcessingResult> reportData, Dictionary<string, int> wordUsageStats)
        {
            string reportFilePath = Path.Combine(folderPath, "Report.csv");
            try
            {
                using (StreamWriter writer = new StreamWriter(reportFilePath))
                {
                    writer.WriteLine("File Name, File Size (bytes), Forbidden Word, Count");
                    foreach (var result in reportData)
                    {
                        foreach (var word in result.ForbiddenWordsFound)
                        {
                            writer.WriteLine($"{result.FileName}, {result.FileSize}, {word.Key}, {word.Value}");
                        }
                    }
                    writer.WriteLine();
                    writer.WriteLine("Top 10 Forbidden Words, Count");
                    foreach (var wordStat in wordUsageStats.OrderByDescending(kvp => kvp.Value).Take(10))
                    {
                        writer.WriteLine($"{wordStat.Key}, {wordStat.Value}");
                    }
                }
                MessageBox.Show($"Отчет успешно сохранен: {reportFilePath}", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка при создании отчета: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        public class FileProcessingResult
        {
            public string FileName { get; set; }
            public long FileSize { get; set; } 
            public Dictionary<string, int> ForbiddenWordsFound { get; set; } 
        }
        public class FolderPicker
        {
            private readonly Type dialogType;
            public string ResultPath { get; private set; }
            public string InputPath { get; set; }
            public FolderPicker()
            {
                dialogType = Type.GetTypeFromProgID("Shell.Application");
            }
            public bool ShowDialog(IntPtr hwndOwner)
            {
                dynamic shell = Activator.CreateInstance(dialogType);
                var folder = shell.BrowseForFolder(hwndOwner.ToInt32(), "Выберите папку", 0, InputPath);
                if (folder != null)
                {
                    ResultPath = folder.Self.Path;
                    Marshal.ReleaseComObject(folder);
                    return true;
                }
                return false;
            }
        }
    }
}
