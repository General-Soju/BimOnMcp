using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace BimOnNavisPlugin
{
    /// <summary>
    /// BimOn MCP 서버 실행 상태를 표시하는 우하단 플로팅 창.
    /// 자동으로 5초 후 축소되며, 클릭하면 다시 확장됩니다.
    /// </summary>
    public class NavisStatusWindow : Window
    {
        private readonly TextBlock _iconText;
        private readonly TextBlock _titleText;
        private readonly TextBlock _msgText;
        private readonly DispatcherTimer _autoHideTimer;

        public NavisStatusWindow()
        {
            // 창 기본 속성
            Title            = "BimOn MCP";
            Width            = 280;
            Height           = 90;
            ResizeMode       = ResizeMode.NoResize;
            WindowStyle      = WindowStyle.None;
            AllowsTransparency = true;
            Background       = Brushes.Transparent;
            Topmost          = true;
            ShowInTaskbar    = false;

            PositionBottomRight();

            // ── 레이아웃 ─────────────────────────────────────────────
            var border = new Border
            {
                // Antigravity dark theme: Card bg #1E1E2E, cyan accent border
                Background      = new SolidColorBrush(Color.FromArgb(245, 0x1E, 0x1E, 0x2E)),
                CornerRadius    = new CornerRadius(6),
                BorderBrush     = new SolidColorBrush(Color.FromRgb(0x00, 0xC2, 0xFF)),
                BorderThickness = new Thickness(1),
                Padding         = new Thickness(16, 12, 16, 12)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // 아이콘 (녹색 점)
            _iconText = new TextBlock
            {
                Text       = "●",
                FontSize   = 18,
                Foreground = new SolidColorBrush(Color.FromRgb(0x06, 0xD6, 0xA0)), // success green
                VerticalAlignment = VerticalAlignment.Center,
                Margin     = new Thickness(0, 0, 10, 0)
            };
            Grid.SetColumn(_iconText, 0);
            Grid.SetRowSpan(_iconText, 2);

            // 제목
            _titleText = new TextBlock
            {
                Text       = "BimOn MCP — Navisworks",
                FontSize   = 12,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White
            };
            Grid.SetColumn(_titleText, 1);
            Grid.SetRow(_titleText, 0);

            // 메시지
            _msgText = new TextBlock
            {
                Text       = "MCP Server Running",
                FontSize   = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xB0)), // muted text
                Margin     = new Thickness(0, 2, 0, 0)
            };
            Grid.SetColumn(_msgText, 1);
            Grid.SetRow(_msgText, 1);

            grid.Children.Add(_iconText);
            grid.Children.Add(_titleText);
            grid.Children.Add(_msgText);
            border.Child = grid;
            Content      = border;

            // 클릭으로 닫기
            border.MouseLeftButtonDown += (s, e) => Close();

            // 5초 후 자동 페이드 아웃
            _autoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _autoHideTimer.Tick += (s, e) =>
            {
                _autoHideTimer.Stop();
                FadeOut();
            };
        }

        public void Update(bool running, string message)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => Update(running, message));
                return;
            }

            _iconText.Text       = running ? "●" : "○";
            _iconText.Foreground = running
                ? new SolidColorBrush(Color.FromRgb(60, 210, 100))
                : new SolidColorBrush(Color.FromRgb(210, 80, 80));

            if (Content is Border b && b.Child is Grid g)
            {
                b.BorderBrush = running
                    ? new SolidColorBrush(Color.FromArgb(180, 80, 200, 120))
                    : new SolidColorBrush(Color.FromArgb(180, 200, 80, 80));
            }

            _msgText.Text = message;
            Opacity       = 1.0;
            PositionBottomRight();
            _autoHideTimer.Stop();
            _autoHideTimer.Start();
        }

        private void PositionBottomRight()
        {
            var screen = SystemParameters.WorkArea;
            Left = screen.Right - Width - 20;
            Top  = screen.Bottom - Height - 20;
        }

        private void FadeOut()
        {
            var anim = new DoubleAnimation(1.0, 0.0, TimeSpan.FromSeconds(0.8));
            anim.Completed += (s, e) =>
            {
                if (IsLoaded) Close();
            };
            BeginAnimation(OpacityProperty, anim);
        }
    }
}
