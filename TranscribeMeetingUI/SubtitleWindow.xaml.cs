using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TranscribeMeetingUI
{
    public partial class SubtitleWindow : Window
    {
        private readonly DispatcherTimer hideStatusTimer;
        private int fontSize = 30;
        private bool userClosed = false;

        /// <summary>用户点了字幕条上的关闭按钮。主窗口据此同步关掉开关。</summary>
        public event EventHandler? UserClosed;

        public SubtitleWindow()
        {
            InitializeComponent();

            hideStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            hideStatusTimer.Tick += (s, e) =>
            {
                hideStatusTimer.Stop();
                StatusLabel.Visibility = Visibility.Collapsed;
            };

            Loaded += (s, e) => RepositionToBottom();
            SystemParameters.StaticPropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(SystemParameters.WorkArea)) RepositionToBottom();
            };
        }

        public int CaptionFontSize
        {
            get => fontSize;
            set
            {
                fontSize = Math.Max(14, Math.Min(72, value));
                CaptionText.FontSize = fontSize;
                CaptionText.LineHeight = fontSize * 1.4;
            }
        }

        private void RepositionToBottom()
        {
            try
            {
                var wa = SystemParameters.WorkArea;
                Left = wa.Left + (wa.Width - Width) / 2;
                Top = wa.Bottom - ActualHeight - 60;
            }
            catch { }
        }

        /// <summary>更新字幕。committed 是已确认文本，partial 是临时文本（用较暗的颜色显示）。</summary>
        public void UpdateCaption(string committed, string partial, double latencySeconds)
        {
            string text = committed ?? "";
            if (!string.IsNullOrEmpty(partial)) text += partial;

            // 只显示最近的内容，避免字幕条无限变高
            const int maxChars = 120;
            if (text.Length > maxChars) text = "…" + text.Substring(text.Length - maxChars);

            CaptionText.Text = text;

            // 状态栏始终显示"已确认多少字 + 滞后多少秒"：
            // 之前的写法是 partial 非空就显示"识别中…"，而新算法下几乎每一帧都有未确认尾巴，
            // 结果用户几乎看不到确认进度，也没法据此判断延迟。
            StatusLabel.Text = $"已确认 {committed?.Length ?? 0} 字（滞后 {latencySeconds:F1} 秒）";
            StatusLabel.Visibility = Visibility.Visible;
            hideStatusTimer.Stop();
            hideStatusTimer.Start();
        }

        public void SetStatus(string message)
        {
            StatusLabel.Text = message;
            StatusLabel.Visibility = Visibility.Visible;
            hideStatusTimer.Stop();
        }

        private void RootBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch { }
            }
        }

        private void FontUpButton_Click(object sender, RoutedEventArgs e)
        {
            CaptionFontSize = fontSize + 4;
        }

        private void FontDownButton_Click(object sender, RoutedEventArgs e)
        {
            CaptionFontSize = fontSize - 4;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            userClosed = true;
            UserClosed?.Invoke(this, EventArgs.Empty);
            Hide();
        }

        /// <summary>程序内部要求关闭（不触发 UserClosed 事件）。</summary>
        public void CloseFromApp()
        {
            if (!userClosed) Hide();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // 真正关闭时隐藏即可，由主窗口管理生命周期
            if (!userClosed)
            {
                e.Cancel = true;
                Hide();
            }
            base.OnClosing(e);
        }
    }
}
