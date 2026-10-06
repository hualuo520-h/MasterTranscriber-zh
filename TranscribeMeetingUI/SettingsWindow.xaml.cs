using Microsoft.Win32;
using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TranscribeMeetingUI
{
    public partial class SettingsWindow : Window
    {
        public AppSettings Settings { get; private set; }

        public SettingsWindow(AppSettings currentSettings)
        {
            InitializeComponent();
            Settings = currentSettings.Clone();
            LoadSettings();
        }

        private void LoadSettings()
        {
            // Transcription provider
            TranscriptionProviderCombo.SelectedIndex = Settings.UseAzureSTT ? 1 : 0;
            WhisperPathTextBox.Text = Settings.WhisperExePath;
            WhisperModelPathTextBox.Text = Settings.WhisperModelPath;

            // Whisper language
            bool langMatched = false;
            foreach (ComboBoxItem item in WhisperLanguageCombo.Items)
            {
                if (item.Tag?.ToString() == Settings.WhisperLanguage)
                {
                    WhisperLanguageCombo.SelectedItem = item;
                    langMatched = true;
                    break;
                }
            }
            if (!langMatched) WhisperLanguageCombo.SelectedIndex = 1;

            AzureSTTKeyTextBox.Text = Settings.AzureSTTKey;
            AzureRegionTextBox.Text = Settings.AzureRegion;

            // Summary provider
            SummaryProviderCombo.SelectedIndex = Settings.UseDeepSeek ? 0 : 1;
            DeepSeekKeyTextBox.Password = Settings.DeepSeekKey;
            DeepSeekModelTextBox.Text = Settings.DeepSeekModel;
            DeepSeekApiUrlTextBox.Text = Settings.DeepSeekApiUrl;
            OllamaModelTextBox.Text = Settings.OllamaModel;
            OllamaUrlTextBox.Text = Settings.OllamaUrl;

            // Translation
            EnableTranslationCheckBox.IsChecked = Settings.EnableTranslation;
            DeepLKeyTextBox.Text = Settings.DeepLKey;

            // Set target language
            foreach (ComboBoxItem item in TargetLanguageCombo.Items)
            {
                if (item.Tag?.ToString() == Settings.TargetLanguage)
                {
                    TargetLanguageCombo.SelectedItem = item;
                    break;
                }
            }

            // Real-time
            RealTimeIntervalTextBox.Text = Settings.RealTimeChunkSeconds.ToString();

            // General
            EnableSummaryCheckBox.IsChecked = Settings.EnableSummary;
            AutoExportCheckBox.IsChecked = Settings.AutoExport;
        }

        private void TranscriptionProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LocalWhisperPanel == null || AzureSTTPanel == null) return;

            bool isLocal = TranscriptionProviderCombo.SelectedIndex == 0;
            LocalWhisperPanel.Visibility = isLocal ? Visibility.Visible : Visibility.Collapsed;
            AzureSTTPanel.Visibility = isLocal ? Visibility.Collapsed : Visibility.Visible;
        }

        private void SummaryProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DeepSeekPanel == null || LocalOllamaPanel == null) return;

            bool isDeepSeek = SummaryProviderCombo.SelectedIndex == 0;
            DeepSeekPanel.Visibility = isDeepSeek ? Visibility.Visible : Visibility.Collapsed;
            LocalOllamaPanel.Visibility = isDeepSeek ? Visibility.Collapsed : Visibility.Visible;
        }

        private void EnableTranslation_Changed(object sender, RoutedEventArgs e)
        {
            if (TranslationPanel == null) return;
            TranslationPanel.Visibility = EnableTranslationCheckBox.IsChecked == true ?
                Visibility.Visible : Visibility.Collapsed;
        }

        private void BrowseWhisperPath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                Title = "选择 Whisper CLI 可执行文件"
            };

            if (dialog.ShowDialog() == true)
            {
                WhisperPathTextBox.Text = dialog.FileName;
            }
        }

        private void BrowseWhisperModel_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "模型文件 (*.bin)|*.bin|所有文件 (*.*)|*.*",
                Title = "选择 Whisper 模型文件"
            };

            if (dialog.ShowDialog() == true)
            {
                WhisperModelPathTextBox.Text = dialog.FileName;
            }
        }

        private void NumericTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !IsTextNumeric(e.Text);
        }

        private static bool IsTextNumeric(string text)
        {
            return Regex.IsMatch(text, "^[0-9]+$");
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            // Validate settings
            if (!ValidateSettings())
                return;

            // Save transcription settings
            Settings.UseAzureSTT = TranscriptionProviderCombo.SelectedIndex == 1;
            Settings.WhisperExePath = WhisperPathTextBox.Text;
            Settings.WhisperModelPath = WhisperModelPathTextBox.Text;
            Settings.WhisperLanguage = (WhisperLanguageCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "zh";
            Settings.AzureSTTKey = AzureSTTKeyTextBox.Text;
            Settings.AzureRegion = AzureRegionTextBox.Text;

            // Save summary settings
            Settings.UseDeepSeek = SummaryProviderCombo.SelectedIndex == 0;
            Settings.DeepSeekKey = DeepSeekKeyTextBox.Password;
            Settings.DeepSeekModel = DeepSeekModelTextBox.Text;
            Settings.DeepSeekApiUrl = DeepSeekApiUrlTextBox.Text;
            Settings.OllamaModel = OllamaModelTextBox.Text;
            Settings.OllamaUrl = OllamaUrlTextBox.Text;

            // Save translation settings
            Settings.EnableTranslation = EnableTranslationCheckBox.IsChecked == true;
            Settings.DeepLKey = DeepLKeyTextBox.Text;
            Settings.TargetLanguage = (TargetLanguageCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "EN-US";

            // Save real-time settings
            if (int.TryParse(RealTimeIntervalTextBox.Text, out int interval))
            {
                Settings.RealTimeChunkSeconds = Math.Max(5, Math.Min(300, interval)); // Clamp between 5-300
            }

            // Save general settings
            Settings.EnableSummary = EnableSummaryCheckBox.IsChecked == true;
            Settings.AutoExport = AutoExportCheckBox.IsChecked == true;

            // Save to config file
            Settings.SaveToFile();

            DialogResult = true;
            Close();
        }

        private bool ValidateSettings()
        {
            // Validate local Whisper settings
            if (!Settings.UseAzureSTT)
            {
                if (string.IsNullOrWhiteSpace(WhisperPathTextBox.Text))
                {
                    MessageBox.Show("请指定 Whisper CLI 路径。", "验证错误",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                if (string.IsNullOrWhiteSpace(WhisperModelPathTextBox.Text))
                {
                    MessageBox.Show("请指定 Whisper 模型路径。", "验证错误",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(AzureSTTKeyTextBox.Text))
                {
                    MessageBox.Show("请输入 Azure 语音 API 密钥。", "验证错误",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }

            // Validate summary settings
            if (EnableSummaryCheckBox.IsChecked == true)
            {
                if (Settings.UseDeepSeek && string.IsNullOrWhiteSpace(DeepSeekKeyTextBox.Password))
                {
                    MessageBox.Show("请输入 DeepSeek API 密钥。", "验证错误",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }

            // Validate translation settings
            if (EnableTranslationCheckBox.IsChecked == true)
            {
                if (string.IsNullOrWhiteSpace(DeepLKeyTextBox.Text))
                {
                    MessageBox.Show("请输入 DeepL API 密钥。", "验证错误",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }

            // Validate real-time interval
            if (!int.TryParse(RealTimeIntervalTextBox.Text, out int interval) || interval < 5 || interval > 300)
            {
                MessageBox.Show("实时间隔必须在 5 到 300 秒之间。", "验证错误",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            return true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}