using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace TranscribeMeetingUI
{
    public partial class MainWindow : Window
    {

        private Recorder recorder;
        private Transcriber transcriber;
        private Summarizer summarizer;
        private ChunkedProcessor chunkedProcessor;
        private RealTimeTranscriber realTimeTranscriber;
        private DispatcherTimer recordingTimer;
        private DateTime recordingStartTime;
        private bool isRecording = false;
        private bool microphoneEnabled = true;
        private bool realTimeEnabled = false;
        private string lastTranscript = "";
        private string lastSummary = "";
        private AppSettings appSettings;
        private bool showRenderedMarkdown = true;
        private DeepLTranslator? translator;
        private string originalTranscript = "";
        private string translatedTranscript = "";
        private bool showingTranslation = false;
        private string currentContext = "meeting";
        private bool isImportedFile = false;
        private string? importedFilePath = null;

        // 实时字幕
        private WhisperServerManager? whisperServer;
        private LiveCaptionEngine? captionEngine;
        private SubtitleWindow? subtitleWindow;
        private bool captionStarting = false;

        public MainWindow()
        {
            InitializeComponent();
            appSettings = AppSettings.LoadFromFile();

        // Add the settings button handler:
            recorder = new Recorder();
            transcriber = new Transcriber(appSettings);
            if (appSettings.EnableSummary)
            {
                summarizer = new Summarizer(appSettings);
                summarizer.SetContext(currentContext); // Set initial context
            }
            chunkedProcessor = new ChunkedProcessor();
            realTimeTranscriber = new RealTimeTranscriber();
            recordingTimer = new DispatcherTimer();
            recordingTimer.Interval = TimeSpan.FromSeconds(1);
            recordingTimer.Tick += RecordingTimer_Tick;

            // Real-time transcriber events
            realTimeTranscriber.TranscriptUpdated += RealTimeTranscriber_TranscriptUpdated;
            realTimeTranscriber.StatusUpdated += RealTimeTranscriber_StatusUpdated;
            realTimeTranscriber.ChunkReadyForProcessing += RealTimeTranscriber_ChunkReadyForProcessing;
            if (appSettings.EnableTranslation && !string.IsNullOrEmpty(appSettings.DeepLKey))
            {
                translator = new DeepLTranslator(appSettings.DeepLKey, appSettings.TargetLanguage);
                TranslateButton.Visibility = Visibility.Visible;
            }

        }
        private string WHISPER_EXE_PATH => appSettings.WhisperExePath;
        private string WHISPER_MODEL_PATH => appSettings.WhisperModelPath;
        private string OLLAMA_URL => appSettings.OllamaUrl;
        private string OLLAMA_MODEL => appSettings.OllamaModel;
        private string OUTPUT_WAV_PATH => Path.Combine(appSettings.OutputDirectory, "my_recording.wav");

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow(appSettings);
            if (settingsWindow.ShowDialog() == true)
            {
                appSettings = settingsWindow.Settings;

                transcriber = new Transcriber(appSettings);
                if (appSettings.EnableSummary)
                {
                    summarizer = new Summarizer(appSettings);
                    summarizer.SetContext(currentContext); // Restore context after settings change
                }
                // Recreate translator
                if (appSettings.EnableTranslation && !string.IsNullOrEmpty(appSettings.DeepLKey))
                {
                    translator = new DeepLTranslator(appSettings.DeepLKey, appSettings.TargetLanguage);
                }
                else
                {
                    translator = null;
                }

                System.Diagnostics.Debug.WriteLine("[UI] Settings updated and services reinitialized");
            }
        }

        private void RecordingTimer_Tick(object sender, EventArgs e)
        {
            var elapsed = DateTime.Now - recordingStartTime;
            RecordingDuration.Text = elapsed.ToString(@"mm\:ss");
        }

        private void ToggleMarkdown_Click(object sender, RoutedEventArgs e)
        {
            showRenderedMarkdown = !showRenderedMarkdown;

            if (showRenderedMarkdown)
            {
                SummaryMarkdownViewer.Visibility = Visibility.Visible;
                SummaryTextViewer.Visibility = Visibility.Collapsed;
                ToggleMarkdownButton.Content = "📝";
                ToggleMarkdownButton.ToolTip = "显示原始 Markdown";
            }
            else
            {
                SummaryMarkdownViewer.Visibility = Visibility.Collapsed;
                SummaryTextViewer.Visibility = Visibility.Visible;
                ToggleMarkdownButton.Content = "🎨";
                ToggleMarkdownButton.ToolTip = "显示渲染后的 Markdown";
            }
        }

        // Update wherever you set summary text:
        private void UpdateSummary(string summary)
        {
            lastSummary = summary;
            SummaryMarkdown.Markdown = summary;  // For rendered view
            SummaryText.Text = summary;          // For raw view
        }

        private void ContextComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ContextComboBox.SelectedItem is ComboBoxItem item && item.Tag != null)
            {
                currentContext = item.Tag.ToString()!;
                summarizer.SetContext(currentContext);
                System.Diagnostics.Debug.WriteLine($"[UI] Context changed to: {currentContext}");
            }
        }

        private void MicToggle_Click(object sender, RoutedEventArgs e)
        {
            microphoneEnabled = MicToggle.IsChecked == true;
            MicStatusText.Text = microphoneEnabled ? "已启用" : "已禁用";
            MicStatusText.Foreground = microphoneEnabled ?
                new SolidColorBrush(Color.FromRgb(76, 175, 80)) :
                new SolidColorBrush(Color.FromRgb(158, 158, 158));

            System.Diagnostics.Debug.WriteLine($"[UI] Microphone clicked: {microphoneEnabled}");
        }

        private void RealTimeToggle_Click(object sender, RoutedEventArgs e)
        {
            realTimeEnabled = RealTimeToggle.IsChecked == true;
            RealTimeStatusText.Text = realTimeEnabled ? "已启用" : "已禁用";
            RealTimeStatusText.Foreground = realTimeEnabled ?
                new SolidColorBrush(Color.FromRgb(76, 175, 80)) :
                new SolidColorBrush(Color.FromRgb(158, 158, 158));

            System.Diagnostics.Debug.WriteLine($"[UI] Real-time clicked: {realTimeEnabled}");
        }

        // ==================== 实时字幕 ====================

        private async void CaptionToggle_Click(object sender, RoutedEventArgs e)
        {
            bool want = CaptionToggle.IsChecked == true;
            if (captionStarting) return;

            if (want)
            {
                await StartLiveCaptionAsync();
            }
            else
            {
                await StopLiveCaptionAsync();
            }
        }

        private async Task StartLiveCaptionAsync()
        {
            captionStarting = true;
            CaptionToggle.IsEnabled = false;
            SetCaptionStatus("启动中……", false);

            try
            {
                if (appSettings.UseAzureSTT)
                {
                    throw new Exception("实时字幕目前仅支持本地 Whisper.cpp 引擎，请在设置中把转写引擎改为「本地（Whisper.cpp）」。");
                }

                string serverPath = WhisperServerManager.DeriveServerPath(appSettings.WhisperExePath);
                if (string.IsNullOrEmpty(serverPath))
                {
                    throw new FileNotFoundException(
                        "找不到 whisper-server.exe。它应该和 whisper-cli.exe 在同一个目录里。\n\n" +
                        $"当前 Whisper CLI 路径：{appSettings.WhisperExePath}\n\n" +
                        "请到 whisper.cpp 的发布包中把 whisper-server.exe 一并解压到该目录，或在设置里重新指定路径。");
                }

                var progress = new Progress<string>(msg => SetCaptionStatus(msg, false));
                whisperServer = new WhisperServerManager(
                    serverPath, appSettings.WhisperModelPath, appSettings.WhisperLanguage, appSettings.WhisperServerPort);
                await whisperServer.StartAsync(progress);

                // 字幕条窗口
                subtitleWindow = new SubtitleWindow { CaptionFontSize = appSettings.LiveCaptionFontSize };
                subtitleWindow.UserClosed += (s, args) =>
                {
                    CaptionToggle.IsChecked = false;
                    _ = StopLiveCaptionAsync();
                };
                subtitleWindow.Show();
                subtitleWindow.SetStatus("正在聆听……");

                // 字幕引擎
                captionEngine = new LiveCaptionEngine(
                    whisperServer.InferenceUrl,
                    appSettings.WhisperLanguage,
                    appSettings.LiveCaptionStepSeconds,
                    appSettings.LiveCaptionWindowSeconds);
                captionEngine.CaptionUpdated += CaptionEngine_CaptionUpdated;
                captionEngine.StatusUpdated += (s, msg) => SetCaptionStatus(msg, false);
                captionEngine.ErrorOccurred += (s, msg) => SetCaptionStatus(msg, true);
                captionEngine.Start();

                SetCaptionStatus("已启用", true);
                StatusText.Text = "字幕中";
                System.Diagnostics.Debug.WriteLine("[UI] 实时字幕已启动");
            }
            catch (Exception ex)
            {
                await StopLiveCaptionAsync();
                CaptionToggle.IsChecked = false;
                SetCaptionStatus("已禁用", false);
                MessageBox.Show($"启动实时字幕失败：{ex.Message}", "实时字幕", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                CaptionToggle.IsEnabled = true;
                captionStarting = false;
            }
        }

        private async Task StopLiveCaptionAsync()
        {
            try
            {
                if (captionEngine != null)
                {
                    captionEngine.CaptionUpdated -= CaptionEngine_CaptionUpdated;
                    string finalText = captionEngine.CommittedText;
                    await captionEngine.StopAsync();
                    captionEngine.Dispose();
                    captionEngine = null;

                    // 把字幕内容并入主窗口的转写文本，方便导出
                    if (!string.IsNullOrWhiteSpace(finalText))
                    {
                        lastTranscript = string.IsNullOrWhiteSpace(lastTranscript)
                            ? finalText
                            : lastTranscript + "\n" + finalText;
                        TranscriptText.Text = lastTranscript;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UI] 停止字幕引擎出错：{ex.Message}");
            }

            try
            {
                subtitleWindow?.CloseFromApp();
                subtitleWindow = null;
            }
            catch { }

            try
            {
                whisperServer?.Dispose();
                whisperServer = null;
            }
            catch { }

            SetCaptionStatus("已禁用", false);
            StatusText.Text = "就绪";
            System.Diagnostics.Debug.WriteLine("[UI] 实时字幕已停止");
        }

        private void CaptionEngine_CaptionUpdated(object? sender, CaptionUpdatedEventArgs e)
        {
            // 事件来自后台线程，必须切回 UI 线程
            Dispatcher.BeginInvoke(new Action(() =>
            {
                subtitleWindow?.UpdateCaption(e.Committed, e.Partial, e.LagSeconds);
            }));
        }

        private void SetCaptionStatus(string message, bool isError)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                CaptionStatusText.Text = message;
                CaptionStatusText.Foreground = isError
                    ? new SolidColorBrush(Color.FromRgb(244, 67, 54))
                    : (CaptionToggle.IsChecked == true
                        ? new SolidColorBrush(Color.FromRgb(76, 175, 80))
                        : new SolidColorBrush(Color.FromRgb(158, 158, 158)));
            }));
        }

        private async void TranslateButton_Click(object sender, RoutedEventArgs e)
        {
            // Allow clicking during real-time recording OR after recording completes
            if (string.IsNullOrEmpty(lastTranscript) && !isRecording)
                return;

            try
            {
                if (!showingTranslation)
                {
                    // Show translation
                    if (isRecording && realTimeEnabled) // CHECK BOTH - must be actively recording in real-time mode
                    {
                        // For active real-time recording, use live segments
                        showingTranslation = true;
                        TranslateButton.Content = "🔄";
                        TranslateButton.ToolTip = "显示原文";

                        var allSegments = realTimeTranscriber.GetAllSegments();

                        if (allSegments.Count > 0)
                        {
                            RealTimeTranscriber_TranscriptUpdated(this, new TranscriptUpdateEventArgs
                            {
                                Segment = allSegments.FirstOrDefault() ?? new TranscriptSegment(),
                                AllSegments = allSegments
                            });
                        }
                        else
                        {
                            TranscriptText.Text = "⏳ 等待第一段内容……\n（翻译将自动显示）";
                        }
                    }
                    else
                    {
                        // Standard mode OR real-time after recording stopped - translate on demand
                        if (string.IsNullOrEmpty(translatedTranscript))
                        {
                            TranslateButton.IsEnabled = false;
                            TranslationStatusText.Text = "翻译中……";
                            TranslationStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 152, 0));

                            var progress = new Progress<string>(status =>
                            {
                                Dispatcher.Invoke(() => TranslationStatusText.Text = status);
                            });

                            translatedTranscript = await translator!.TranslateBatchAsync(lastTranscript, progress);
                            TranslateButton.IsEnabled = true;
                        }

                        originalTranscript = TranscriptText.Text;
                        TranscriptText.Text = translatedTranscript;
                        showingTranslation = true;
                        TranslateButton.Content = "🔄";
                        TranslateButton.ToolTip = "显示原文";
                    }

                    TranslationStatusText.Text = $"已翻译为{GetLanguageName(appSettings.TargetLanguage)}";
                    TranslationStatusText.Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                }
                else
                {
                    // Show original
                    showingTranslation = false;
                    TranslateButton.Content = "🌐";
                    TranslateButton.ToolTip = "翻译转写文本";
                    TranslationStatusText.Text = "";

                    if (isRecording && realTimeEnabled) // CHECK BOTH
                    {
                        // Trigger UI refresh for active real-time recording
                        var allSegments = realTimeTranscriber.GetAllSegments();

                        if (allSegments.Count > 0)
                        {
                            RealTimeTranscriber_TranscriptUpdated(this, new TranscriptUpdateEventArgs
                            {
                                Segment = allSegments.FirstOrDefault() ?? new TranscriptSegment(),
                                AllSegments = allSegments
                            });
                        }
                        else
                        {
                            TranscriptText.Text = "⏳ 等待第一段内容……";
                        }
                    }
                    else
                    {
                        // Standard mode or stopped recording
                        if (string.IsNullOrEmpty(originalTranscript))
                        {
                            originalTranscript = lastTranscript;
                        }
                        TranscriptText.Text = originalTranscript;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"翻译失败：{ex.Message}", "翻译错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                TranslateButton.IsEnabled = true;
                TranslationStatusText.Text = "翻译失败";
                TranslationStatusText.Foreground = new SolidColorBrush(Color.FromRgb(244, 67, 54));
            }
        }

        private async void ImportFileButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var openDialog = new OpenFileDialog
                {
                    Filter = "音视频文件|*.mp3;*.mp4;*.m4a;*.wav;*.aac;*.wma;*.flac;*.ogg;*.avi;*.mov;*.mkv;*.wmv;*.flv|" +
                             "音频文件|*.mp3;*.wav;*.m4a;*.aac;*.wma;*.flac;*.ogg|" +
                             "视频文件|*.mp4;*.avi;*.mov;*.mkv;*.wmv;*.flv|" +
                             "所有文件|*.*",
                    Title = "选择音频或视频文件",
                    Multiselect = false
                };

                if (openDialog.ShowDialog() == true)
                {
                    string filePath = openDialog.FileName;

                    if (!AudioConverter.IsSupportedFormat(filePath))
                    {
                        MessageBox.Show("不支持的文件格式。请选择音频或视频文件。",
                            "格式不支持", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    // Disable UI during processing
                    ImportFileButton.IsEnabled = false;
                    RecordButton.IsEnabled = false;
                    MicToggle.IsEnabled = false;
                    RealTimeToggle.IsEnabled = false;
                    ContextComboBox.IsEnabled = false;

                    StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(255, 152, 0));
                    StatusText.Text = "处理中";
                    StatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 152, 0));
                    ProgressBar.Visibility = Visibility.Visible;

                    string formatDesc = AudioConverter.GetFormatDescription(filePath);
                    ProgressText.Text = $"正在导入{formatDesc}……";

                    TranscriptText.Text = "正在处理导入的文件……";
                    TranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(117, 117, 117));
                    SummaryText.Text = "等待处理完成……";
                    SummaryText.Foreground = new SolidColorBrush(Color.FromRgb(117, 117, 117));

                    var progress = new Progress<string>(status =>
                    {
                        Dispatcher.Invoke(() => ProgressText.Text = status);
                    });

                    // Convert to WAV if needed
                    string wavFilePath;
                    if (Path.GetExtension(filePath).ToLower() == ".wav")
                    {
                        wavFilePath = filePath;
                    }
                    else
                    {
                        wavFilePath = await AudioConverter.ConvertToWavAsync(filePath, progress);
                    }

                    importedFilePath = wavFilePath;
                    isImportedFile = true;

                    // Process the file (transcribe + summarize)
                    await ProcessImportedFileAsync(wavFilePath, progress);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导入文件失败：{ex.Message}", "导入错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);

                TranscriptText.Text = $"❌ 导入失败：{ex.Message}";
                TranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(244, 67, 54));

                ResetImportUI();
            }
        }

        private async Task ProcessImportedFileAsync(string wavFilePath, IProgress<string> progress)
        {
            try
            {
                // Transcribe
                var transcriptionResult = await chunkedProcessor.TranscribeAudioAsync(
                    wavFilePath, transcriber, progress);

                if (string.IsNullOrWhiteSpace(transcriptionResult.FullTranscript))
                {
                    TranscriptText.Text = "⚠️ 转写失败或未产生文本。";
                    TranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(244, 67, 54));
                    ResetImportUI();
                    return;
                }

                lastTranscript = transcriptionResult.FullTranscript;

                // Show transcript immediately
                TranscriptText.Text = transcriptionResult.FullTranscript;
                TranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(33, 33, 33));

                StatusText.Text = "转写完成";
                StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(76, 175, 80));

                string chunkInfo = transcriptionResult.ChunkCount > 1 ?
                    $"（分 {transcriptionResult.ChunkCount} 块处理）" : "";

                // Show translate button if enabled
                if (appSettings.EnableTranslation && translator != null)
                {
                    TranslateButton.Visibility = Visibility.Visible;
                    translatedTranscript = "";
                    showingTranslation = false;
                    TranslationStatusText.Text = "";
                }

                // Summarize if enabled
                if (appSettings.EnableSummary)
                {
                    StatusText.Text = "正在生成摘要";
                    StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(156, 39, 176));

                    lastSummary = await chunkedProcessor.SummarizeTranscriptAsync(
                        transcriptionResult, summarizer, progress);

                    UpdateSummary(lastSummary);
                }
                else
                {
                    SummaryText.Text = "设置中已关闭摘要生成。";
                    SummaryText.Foreground = new SolidColorBrush(Color.FromRgb(158, 158, 158));
                }

                // Complete
                StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                StatusText.Text = "完成";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                ProgressText.Text = $"✅ 导入完成{chunkInfo}！";
                ProgressBar.Visibility = Visibility.Collapsed;
                ExportButton.IsEnabled = true;

                MessageBox.Show($"文件处理成功{chunkInfo}！",
                    "导入完成", MessageBoxButton.OK, MessageBoxImage.Information);

                // Clean up converted file if it was created
                if (importedFilePath != null && importedFilePath.Contains("_converted.wav"))
                {
                    try
                    {
                        File.Delete(importedFilePath);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                throw;
            }
            finally
            {
                ResetImportUI();
            }
        }

        private void ResetImportUI()
        {
            ImportFileButton.IsEnabled = true;
            RecordButton.IsEnabled = true;
            MicToggle.IsEnabled = true;
            RealTimeToggle.IsEnabled = true;
            ContextComboBox.IsEnabled = true;
            isImportedFile = false;
        }

        private string GetLanguageName(string code)
        {
            var languages = new Dictionary<string, string>
            {
                {"EN-US", "英语"}, {"ES", "西班牙语"}, {"FR", "法语"},
                {"DE", "德语"}, {"IT", "意大利语"}, {"JA", "日语"},
                {"KO", "韩语"}, {"ZH", "中文"}
            };
            return languages.TryGetValue(code, out var name) ? name : code;
        }


        private void RealTimeTranscriber_StatusUpdated(object? sender, string status)
        {
            Dispatcher.Invoke(() =>
            {
                ProgressText.Text = status;
            });
        }

        private void RealTimeTranscriber_TranscriptUpdated(object? sender, TranscriptUpdateEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                var sb = new System.Text.StringBuilder();

                // Check if we're currently showing translation
                if (showingTranslation && translator != null)
                {
                    // Show translated version
                    foreach (var segment in e.AllSegments.OrderBy(s => s.StartTime))
                    {
                        sb.AppendLine($"[{FormatTimeSpan(segment.StartTime)} - {FormatTimeSpan(segment.EndTime)}]");

                        // Try to get translated version
                        string displayText = realTimeTranscriber.GetTranslatedSegment(segment.ChunkNumber);
                        if (string.IsNullOrEmpty(displayText))
                        {
                            displayText = segment.Transcript + "［翻译中……］";
                        }

                        sb.AppendLine(displayText);
                        sb.AppendLine();
                    }

                    // Update translation status
                    TranslationStatusText.Text = $"已翻译为{GetLanguageName(appSettings.TargetLanguage)}";
                    TranslationStatusText.Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                }
                else
                {
                    // Show original version
                    foreach (var segment in e.AllSegments.OrderBy(s => s.StartTime))
                    {
                        sb.AppendLine($"[{FormatTimeSpan(segment.StartTime)} - {FormatTimeSpan(segment.EndTime)}]");
                        sb.AppendLine(segment.Transcript);
                        sb.AppendLine();
                    }
                }

                TranscriptText.Text = sb.ToString();
                TranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(33, 33, 33));
            });
        }

        private async void RealTimeTranscriber_ChunkReadyForProcessing(object? sender, ChunkToProcess chunk)
        {
            try
            {
                string transcript = await transcriber.TranscribeAsync(chunk.FilePath);

                realTimeTranscriber.AddTranscriptSegment(
                    chunk.ChunkNumber,
                    chunk.StartTime,
                    chunk.EndTime,
                    transcript);

                try
                {
                    File.Delete(chunk.FilePath);
                    if (File.Exists(chunk.FilePath + ".txt"))
                        File.Delete(chunk.FilePath + ".txt");
                }
                catch { }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error processing real-time chunk: {ex.Message}");
            }
        }

        private string FormatTimeSpan(TimeSpan ts)
        {
            return ts.Hours > 0
                ? $"{ts.Hours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{ts.Minutes}:{ts.Seconds:D2}";
        }

        private async void RecordButton_Click(object sender, RoutedEventArgs e)
        {
            if (!isRecording)
            {
                try
                {
                    isRecording = true;
                    recordingStartTime = DateTime.Now;

                    RecordButton.Content = "⬛ 停止录制";
                    RecordButton.Style = (Style)FindResource("StopButton");
                    StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(244, 67, 54));
                    StatusText.Text = "录制中";
                    StatusText.Foreground = new SolidColorBrush(Color.FromRgb(244, 67, 54));
                    MicToggle.IsEnabled = false;
                    RealTimeToggle.IsEnabled = false;
                    RecordingDuration.Visibility = Visibility.Visible;
                    ExportButton.IsEnabled = false;
                    if (appSettings.EnableTranslation && translator != null)
                    {
                        translatedTranscript = ""; // Reset cache
                        showingTranslation = false;
                        TranslationStatusText.Text = "";
                        TranslateButton.Content = "🌐";
                    }

                    System.Diagnostics.Debug.WriteLine($"[UI] Starting recording - Mic: {microphoneEnabled}, RealTime: {realTimeEnabled}");

                    if (realTimeEnabled)
                    {
                        ProgressText.Text = "正在以实时模式录制……";
                        TranscriptText.Text = $"⏳ 转写文本将在此处实时显示……\n（首段将在 {appSettings.RealTimeChunkSeconds} 秒后出现）";
                        TranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(117, 117, 117));
                        UpdateSummary("录制停止后将生成摘要……");
                        SummaryText.Foreground = new SolidColorBrush(Color.FromRgb(117, 117, 117));

                        realTimeTranscriber.StartRecording(OUTPUT_WAV_PATH, microphoneEnabled, translator, appSettings.RealTimeChunkSeconds);
                    }
                    else
                    {
                        ProgressText.Text = "正在录制……";
                        TranscriptText.Text = "录制中……停止后转写文本将显示在这里。";
                        TranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(117, 117, 117));
                        UpdateSummary("等待录制结束……");
                        SummaryText.Foreground = new SolidColorBrush(Color.FromRgb(117, 117, 117));

                        recorder.StartRecording(OUTPUT_WAV_PATH, microphoneEnabled);
                    }

                    recordingTimer.Start();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"启动录制失败：{ex.Message}", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    ResetUI();
                }
            }
            else
            {
                try
                {
                    recordingTimer.Stop();
                    RecordButton.IsEnabled = false;
                    ProgressText.Text = "正在停止录制……";
                    ProgressBar.Visibility = Visibility.Visible;

                    if (realTimeEnabled)
                    {
                        await realTimeTranscriber.StopRecordingAsync();

                        lastTranscript = realTimeTranscriber.GetFullTranscript();

                        if (string.IsNullOrWhiteSpace(lastTranscript))
                        {
                            TranscriptText.Text = "⚠️ 未生成任何转写文本。";
                            TranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(244, 67, 54));
                            ResetUI();
                            realTimeTranscriber.CleanupChunkFiles();
                            return;
                        }

                        if (showingTranslation && translator != null)
                        {
                            translatedTranscript = realTimeTranscriber.GetTranslatedTranscript();
                            originalTranscript = lastTranscript;
                        }

                        if (appSettings.EnableSummary)
                        {
                            StatusText.Text = "正在生成摘要";
                            StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(156, 39, 176));
                            ProgressText.Text = "正在生成 AI 摘要……";

                            lastSummary = await summarizer.SummarizeAsync(lastTranscript);
                            UpdateSummary(lastSummary);
                            SummaryText.Foreground = new SolidColorBrush(Color.FromRgb(33, 33, 33));
                        }
                        else
                        {
                            UpdateSummary("设置中已关闭摘要生成。");
                            SummaryText.Foreground = new SolidColorBrush(Color.FromRgb(158, 158, 158));
                        }

                        StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                        StatusText.Text = "完成";
                        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                        ProgressText.Text = "✅ 处理完成！";
                        ProgressBar.Visibility = Visibility.Collapsed;
                        ExportButton.IsEnabled = true;

                        realTimeTranscriber.CleanupChunkFiles();
                        string modeText = "实时模式";
                        MessageBox.Show($"录制已成功处理（{modeText}）！",
                            "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        // Replace the chunked processing section with:
                        await recorder.StopRecordingAsync();
                        DebugAudioFile(OUTPUT_WAV_PATH);

                        var progress = new Progress<string>(status =>
                        {
                            Dispatcher.Invoke(() => ProgressText.Text = status);
                        });

                        // STEP 1: Transcribe
                        var transcriptionResult = await chunkedProcessor.TranscribeAudioAsync(
    OUTPUT_WAV_PATH, transcriber, progress);

                        if (string.IsNullOrWhiteSpace(transcriptionResult.FullTranscript))
                        {
                            TranscriptText.Text = "⚠️ 转写失败。";
                            TranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(244, 67, 54));
                            ResetUI();
                            return;
                        }

                        lastTranscript = transcriptionResult.FullTranscript;

                        // SHOW TRANSCRIPT IMMEDIATELY!
                        TranscriptText.Text = transcriptionResult.FullTranscript;
                        TranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(33, 33, 33));
                        if (appSettings.EnableSummary) { 
                            StatusText.Text = "正在生成摘要";
                            StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(156, 39, 176));

                            lastSummary = await chunkedProcessor.SummarizeTranscriptAsync(
                                            transcriptionResult, summarizer, progress);

                            UpdateSummary(lastSummary);
                            SummaryText.Foreground = new SolidColorBrush(Color.FromRgb(33, 33, 33));
                        }
                        else
                        {
                            UpdateSummary("设置中已关闭摘要生成。");
                            SummaryText.Foreground = new SolidColorBrush(Color.FromRgb(158, 158, 158));
                        }
                        StatusText.Text = "转写完成";
                        StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(76, 175, 80));

                        string chunkInfo = transcriptionResult.ChunkCount > 1 ?
                            $"（分 {transcriptionResult.ChunkCount} 块处理）" : "";
                        ProgressText.Text = $"✅ 转写完成{chunkInfo}！";

                        // STEP 2: Summarize (can be skipped if user disables it later)


                        // Final status
                        StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                        StatusText.Text = "完成";
                        ProgressText.Text = $"✅ 处理完成{chunkInfo}！";
                        ExportButton.IsEnabled = true;

                        string modeText = realTimeEnabled ? "实时模式" : "标准模式";
                    }
                    //MessageBox.Show($"录制已成功处理（{modeText}）！",
                    //    "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"发生错误：{ex.Message}", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    TranscriptText.Text = $"❌ 错误：{ex.Message}";
                    TranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(244, 67, 54));
                }
                finally
                {
                    ResetUI();
                    try
                    {
                        if (File.Exists(OUTPUT_WAV_PATH + ".txt"))
                            File.Delete(OUTPUT_WAV_PATH + ".txt");
                    }
                    catch { }
                }
            }
        }

        private void DebugAudioFile(string audioPath)
        {
            try
            {
                using (var reader = new NAudio.Wave.AudioFileReader(audioPath))
                {
                    System.Diagnostics.Debug.WriteLine($"\n=== AUDIO FILE DIAGNOSTICS ===");
                    System.Diagnostics.Debug.WriteLine($"File Size: {new FileInfo(audioPath).Length / (1024.0 * 1024.0):F2} MB");
                    System.Diagnostics.Debug.WriteLine($"Wave Format: {reader.WaveFormat}");
                    System.Diagnostics.Debug.WriteLine($"Total Time (from NAudio): {reader.TotalTime}");
                    System.Diagnostics.Debug.WriteLine($"Total Time Seconds: {reader.TotalTime.TotalSeconds}");
                    System.Diagnostics.Debug.WriteLine($"===============================\n");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error reading audio file: {ex.Message}");
            }
        }

        private void ResetUI()
        {
            isRecording = false;
            RecordButton.Content = "⚫ 开始录制";
            RecordButton.Style = (Style)FindResource("ModernButton");
            RecordButton.IsEnabled = true;
            MicToggle.IsEnabled = true;
            RealTimeToggle.IsEnabled = true;
            RecordingDuration.Visibility = Visibility.Collapsed;
            RecordingDuration.Text = "00:00";
            ProgressBar.Visibility = Visibility.Collapsed;

            ResetImportUI();

            if (string.IsNullOrEmpty(lastTranscript))
            {
                StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(189, 189, 189));
                StatusText.Text = "就绪";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(117, 117, 117));
                ProgressText.Text = "准备就绪";
            }
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var saveDialog = new SaveFileDialog
                {
                    Filter = "文本文件 (*.txt)|*.txt|Markdown 文件 (*.md)|*.md",
                    DefaultExt = ".md",
                    FileName = $"会议摘要_{DateTime.Now:yyyyMMdd_HHmmss}"
                };

                if (saveDialog.ShowDialog() == true)
                {
                    string content = $"会议摘要\n";
                    content += $"生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n\n";
                    content += $"转写文本：\n{new string('-', 60)}\n{lastTranscript}\n\n";
                    content += $"AI 摘要：\n{new string('-', 60)}\n{lastSummary}\n";

                    File.WriteAllText(saveDialog.FileName, content);
                    MessageBox.Show($"导出成功！",
                        "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出失败：{ex.Message}", "导出错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // 同步清理：确保退出时不会残留 whisper-server 子进程和置顶字幕窗口
            try
            {
                if (captionEngine != null)
                {
                    captionEngine.CaptionUpdated -= CaptionEngine_CaptionUpdated;
                    captionEngine.Dispose();
                    captionEngine = null;
                }
            }
            catch { }

            try { subtitleWindow?.CloseFromApp(); } catch { }
            subtitleWindow = null;

            try { whisperServer?.Stop(); } catch { }
            try { whisperServer?.Dispose(); } catch { }
            whisperServer = null;

            base.OnClosing(e);
        }
    }
}