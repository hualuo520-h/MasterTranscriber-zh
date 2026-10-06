using System.Diagnostics;
using System.IO;

namespace TranscribeMeetingUI
{
    public class Transcriber
    {
        private readonly AppSettings settings;
        private AzureSTTHandler? azureHandler;

        public Transcriber(AppSettings settings)
        {
            this.settings = settings;

            if (settings.UseAzureSTT && !string.IsNullOrEmpty(settings.AzureSTTKey))
            {
                azureHandler = new AzureSTTHandler(settings.AzureSTTKey, settings.AzureRegion);
            }
        }

        public async Task<string> TranscribeAsync(string audioFilePath, IProgress<string>? progress = null)
        {
            if (settings.UseAzureSTT)
            {
                return await TranscribeWithAzureAsync(audioFilePath, progress);
            }
            else
            {
                return await TranscribeWithWhisperAsync(audioFilePath);
            }
        }

        private async Task<string> TranscribeWithAzureAsync(string audioFilePath, IProgress<string>? progress = null)
        {
            if (azureHandler == null)
            {
                throw new Exception("Azure 语音转文本未配置，请检查 API 密钥。");
            }

            progress?.Report("正在使用 Azure 语音转文本转写……");

            // Check file size to decide which method to use
            var fileInfo = new FileInfo(audioFilePath);
            if (fileInfo.Length > 50 * 1024 * 1024) // > 50MB
            {
                return await azureHandler.TranscribeLongAudioAsync(audioFilePath, progress);
            }
            else
            {
                return await azureHandler.TranscribeAudioFileAsync(audioFilePath);
            }
        }

        private async Task<string> TranscribeWithWhisperAsync(string audioFilePath)
        {
            // Get the directory and filename
            string audioDirectory = Path.GetDirectoryName(audioFilePath) ?? Environment.CurrentDirectory;
            string audioFileNameWithoutExt = Path.GetFileNameWithoutExtension(audioFilePath);

            // Build the FULL output path (without extension, whisper adds .txt)
            string outputPathWithoutExt = Path.Combine(audioDirectory, audioFileNameWithoutExt);
            string transcriptPath = outputPathWithoutExt + ".txt";

            string languageArg = string.IsNullOrWhiteSpace(settings.WhisperLanguage) || settings.WhisperLanguage == "auto"
                ? "" : $" -l {settings.WhisperLanguage}";
            // 中文音频：whisper 默认可能输出繁体，用初始提示词引导为简体中文
            string promptArg = settings.WhisperLanguage == "zh"
                ? " --prompt \"以下是普通话的句子，请使用简体中文转写。\""
                : "";
            string arguments = $"-m \"{settings.WhisperModelPath}\" -f \"{audioFilePath}\" -of \"{outputPathWithoutExt}\" -otxt{languageArg}{promptArg}";

            System.Diagnostics.Debug.WriteLine($"[Whisper] Command: {settings.WhisperExePath} {arguments}");
            System.Diagnostics.Debug.WriteLine($"[Whisper] Expected transcript: {transcriptPath}");

            var startInfo = new ProcessStartInfo
            {
                FileName = settings.WhisperExePath,
                Arguments = arguments,
                RedirectStandardOutput = false,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using (var process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    throw new Exception("无法启动 whisper.cpp 进程。");
                }

                string stderr = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (process.ExitCode != 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[Whisper] Error output: {stderr}");
                    throw new Exception($"whisper.cpp 退出代码 {process.ExitCode} 表示执行失败：\n{stderr}");
                }
            }

            // Check if transcript was created
            if (!File.Exists(transcriptPath))
            {
                // Try alternate location (current directory)
                string altPath = Path.Combine(Environment.CurrentDirectory, audioFileNameWithoutExt + ".txt");
                System.Diagnostics.Debug.WriteLine($"[Whisper] Primary path not found, checking: {altPath}");

                if (File.Exists(altPath))
                {
                    transcriptPath = altPath;
                }
                else
                {
                    throw new FileNotFoundException($"Whisper 未生成转写文件。预期路径：{transcriptPath}", transcriptPath);
                }
            }

            System.Diagnostics.Debug.WriteLine($"[Whisper] ✓ Transcript found at: {transcriptPath}");
            string transcript = await File.ReadAllTextAsync(transcriptPath);

            // Clean up the transcript file
            try
            {
                File.Delete(transcriptPath);
            }
            catch { }

            return transcript;
        }
    }
}