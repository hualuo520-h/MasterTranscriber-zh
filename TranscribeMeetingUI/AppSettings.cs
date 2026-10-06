using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TranscribeMeetingUI
{
    public class AppSettings
    {
        // Transcription settings
        [JsonPropertyName("useAzureSTT")]
        public bool UseAzureSTT { get; set; } = false;

        [JsonPropertyName("whisperExePath")]
        public string WhisperExePath { get; set; } = @"D:\Documents\LocalAI\whisper-cublas-12.4.0-bin-x64\Release\whisper-cli.exe";

        [JsonPropertyName("whisperModelPath")]
        public string WhisperModelPath { get; set; } = @"D:\Documents\LocalAI\whisper-cublas-12.4.0-bin-x64\Release\Models\ggml-small.bin";

        [JsonPropertyName("whisperLanguage")]
        public string WhisperLanguage { get; set; } = "zh";

        [JsonPropertyName("azureSTTKey")]
        public string AzureSTTKey { get; set; } = "";

        [JsonPropertyName("azureRegion")]
        public string AzureRegion { get; set; } = "eastus";

        // Summary settings
        [JsonPropertyName("useDeepSeek")]
        public bool UseDeepSeek { get; set; } = true;

        [JsonPropertyName("deepSeekApiUrl")]
        public string DeepSeekApiUrl { get; set; } = "https://api.deepseek.com/chat/completions";

        [JsonPropertyName("deepSeekKey")]
        public string DeepSeekKey { get; set; } = "";

        [JsonPropertyName("deepSeekModel")]
        public string DeepSeekModel { get; set; } = "deepseek-chat";

        [JsonPropertyName("ollamaUrl")]
        public string OllamaUrl { get; set; } = "http://localhost:11434";

        [JsonPropertyName("ollamaModel")]
        public string OllamaModel { get; set; } = "llama3.1:8b";

        // Translation settings
        [JsonPropertyName("enableTranslation")]
        public bool EnableTranslation { get; set; } = false;

        [JsonPropertyName("deepLKey")]
        public string DeepLKey { get; set; } = "";

        [JsonPropertyName("targetLanguage")]
        public string TargetLanguage { get; set; } = "EN-US";

        // Real-time settings
        [JsonPropertyName("realTimeChunkSeconds")]
        public int RealTimeChunkSeconds { get; set; } = 30;

        // Live caption (悬浮字幕条) settings
        [JsonPropertyName("liveCaptionFontSize")]
        public int LiveCaptionFontSize { get; set; } = 30;

        [JsonPropertyName("liveCaptionStepSeconds")]
        public double LiveCaptionStepSeconds { get; set; } = 0.5;

        [JsonPropertyName("liveCaptionWindowSeconds")]
        public double LiveCaptionWindowSeconds { get; set; } = 30;

        [JsonPropertyName("liveCaptionShowPartial")]
        public bool LiveCaptionShowPartial { get; set; } = true;

        [JsonPropertyName("whisperServerPort")]
        public int WhisperServerPort { get; set; } = 8917;

        // General settings
        [JsonPropertyName("enableSummary")]
        public bool EnableSummary { get; set; } = true;

        [JsonPropertyName("autoExport")]
        public bool AutoExport { get; set; } = false;

        [JsonPropertyName("outputDirectory")]
        public string OutputDirectory { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MasterTranscriber");

        // Config file path
        private static readonly string ConfigFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MasterTranscriber",
            "settings.json"
        );

        public static AppSettings LoadFromFile()
        {
            try
            {
                if (File.Exists(ConfigFilePath))
                {
                    string json = File.ReadAllText(ConfigFilePath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        System.Diagnostics.Debug.WriteLine("[Settings] Loaded from file");
                        ApplyPortableWhisperPaths(settings);
                        return settings;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Settings] Error loading: {ex.Message}");
            }

            System.Diagnostics.Debug.WriteLine("[Settings] Using defaults");
            var defaults = new AppSettings();
            ApplyPortableWhisperPaths(defaults);
            return defaults;
        }

        /// <summary>
        /// 便携模式：若配置里的 Whisper 路径不存在，就在程序目录下找随包附带的
        /// whisper 文件夹（whisper\whisper-cli.exe、whisper\Models\ggml-*.bin）。
        /// 这样下载解压后无需手动配置即可直接使用。
        /// </summary>
        private static void ApplyPortableWhisperPaths(AppSettings settings)
        {
            try
            {
                string baseDir = AppContext.BaseDirectory;

                if (string.IsNullOrEmpty(settings.WhisperExePath) || !File.Exists(settings.WhisperExePath))
                {
                    foreach (var rel in new[] { @"whisper\whisper-cli.exe", "whisper-cli.exe" })
                    {
                        string candidate = Path.Combine(baseDir, rel);
                        if (File.Exists(candidate)) { settings.WhisperExePath = candidate; break; }
                    }
                }

                if (string.IsNullOrEmpty(settings.WhisperModelPath) || !File.Exists(settings.WhisperModelPath))
                {
                    // 依次在 whisper\Models、Models、程序目录 下找模型，按体积小到大优先
                    var modelDirs = new[]
                    {
                        Path.Combine(baseDir, "whisper", "Models"),
                        Path.Combine(baseDir, "Models"),
                        baseDir
                    };
                    string[] preference = { "ggml-small.bin", "ggml-base.bin", "ggml-medium.bin", "ggml-tiny.bin", "ggml-large-v3.bin" };
                    string? found = null;
                    foreach (var dir in modelDirs)
                    {
                        if (!Directory.Exists(dir)) continue;
                        foreach (var name in preference)
                        {
                            string candidate = Path.Combine(dir, name);
                            if (File.Exists(candidate)) { found = candidate; break; }
                        }
                        if (found != null) break;
                        // 兜底：任意 ggml-*.bin
                        var any = Directory.GetFiles(dir, "ggml-*.bin");
                        if (any.Length > 0) { found = any[0]; break; }
                    }
                    if (found != null) settings.WhisperModelPath = found;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Settings] Portable path detection failed: {ex.Message}");
            }
        }

        public void SaveToFile()
        {
            try
            {
                string directory = Path.GetDirectoryName(ConfigFilePath)!;
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true
                };

                string json = JsonSerializer.Serialize(this, options);
                File.WriteAllText(ConfigFilePath, json);

                System.Diagnostics.Debug.WriteLine($"[Settings] Saved to: {ConfigFilePath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Settings] Error saving: {ex.Message}");
            }
        }

        public AppSettings Clone()
        {
            string json = JsonSerializer.Serialize(this);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
    }
}