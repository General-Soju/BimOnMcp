using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;

namespace BimOnMcpShared
{
    /// <summary>
    /// BIM Live Model Quality Check 스타일을 적용한 모던한 다크 테마 제작자 정보 창.
    /// </summary>
    public class AboutWindow : Window
    {
        // ── Antigravity 컬러 시스템 ───────────────────────────────────
        private static readonly Brush BgCanvas       = Frz("#12121E");
        private static readonly Brush BgCard         = Frz("#1E1E2E");
        private static readonly Brush BgControl      = Frz("#2A2A40");
        private static readonly Brush BgControlHover = Frz("#363654");
        private static readonly Brush ClrBorder      = Frz("#3F3F5F");
        private static readonly Brush ClrBorderHover = Frz("#5F5F8F");
        private static readonly Brush TxtPrimary     = Frz("#FFFFFF");
        private static readonly Brush TxtMuted       = Frz("#9A9AB0");
        private static readonly Brush AccCyan        = Frz("#00C2FF");

        private static Brush Frz(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        public AboutWindow()
        {
            Title = "제작자 정보";
            SizeToContent = SizeToContent.Height;
            Width = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = BgCanvas;
            ShowInTaskbar = false;
            // 호스트(Revit/Navisworks/AutoCAD) 창에 가려지지 않도록 최상위 표시
            Topmost = true;
            FontFamily = new FontFamily("Segoe UI, Malgun Gothic");
            FontSize = 12;

            var mainGrid = new Grid { Margin = new Thickness(24) };
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 0: Title/Logo
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 1: Card Panel
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 2: Footer

            // 1. 타이틀 영역
            var titlePanel = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
            
            var titleText = new TextBlock
            {
                Text = "BIMON AI MCP SUITE",
                Foreground = AccCyan,
                FontWeight = FontWeights.Bold,
                FontSize = 18
            };
            
            var versionText = new TextBlock
            {
                Text = "Version 3.0.0",
                Foreground = TxtMuted,
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 0)
            };
            
            titlePanel.Children.Add(titleText);
            titlePanel.Children.Add(versionText);
            Grid.SetRow(titlePanel, 0);
            mainGrid.Children.Add(titlePanel);

            // 2. 카드 바디 영역 (제작자 세부 정보)
            var cardBorder = new Border
            {
                Background = BgCard,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(16),
                BorderThickness = new Thickness(1),
                BorderBrush = ClrBorder,
                Margin = new Thickness(0, 0, 0, 16)
            };

            var cardGrid = new Grid();
            cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // 정보 행 생성
            cardGrid.Children.Add(CreateRow(0, "Name:", "JungGeun Park"));
            cardGrid.Children.Add(CreateRow(1, "Channel:", "소주장군"));
            cardGrid.Children.Add(CreateYoutubeRow(2, "YouTube:", "https://www.youtube.com/@GeneralSoju", "youtube.com/@GeneralSoju"));
            cardGrid.Children.Add(CreateRow(3, "E-Mail:", "sojunbeer119@gmail.com"));

            // 상세 정보 설명
            var descText = new TextBlock
            {
                Text = "Revit · Navisworks · AutoCAD AI Automation System",
                Foreground = TxtMuted,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            };
            Grid.SetRow(descText, 4);
            cardGrid.Children.Add(descText);

            cardBorder.Child = cardGrid;
            Grid.SetRow(cardBorder, 1);
            mainGrid.Children.Add(cardBorder);

            // 3. 푸터 영역 (Copyright & Button)
            var footerGrid = new Grid();
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var copyrightText = new TextBlock
            {
                Text = "Copyright © 2026. All rights reserved.",
                Foreground = TxtMuted,
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(copyrightText, 0);
            footerGrid.Children.Add(copyrightText);

            var okButton = new Button
            {
                Content = "확인",
                Width = 80,
                Foreground = TxtPrimary,
                Cursor = System.Windows.Input.Cursors.Hand,
                IsDefault = true
            };
            
            // 버튼 템플릿 및 스타일 동적 구성
            var btnTemplate = new ControlTemplate(typeof(Button));
            var borderFactory = new FrameworkElementFactory(typeof(Border), "btnBorder");
            borderFactory.SetValue(Border.BackgroundProperty, BgControl);
            borderFactory.SetValue(Border.BorderBrushProperty, ClrBorder);
            borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            borderFactory.SetValue(Border.PaddingProperty, new Thickness(0, 6, 0, 6));

            var contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            contentFactory.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            contentFactory.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            borderFactory.AppendChild(contentFactory);
            
            btnTemplate.VisualTree = borderFactory;

            // 마우스 호버 효과 트리거
            var hoverTrigger = new Trigger { Property = IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, BgControlHover, "btnBorder"));
            hoverTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, ClrBorderHover, "btnBorder"));
            btnTemplate.Triggers.Add(hoverTrigger);

            okButton.Template = btnTemplate;
            okButton.Click += (s, e) => Close();
            
            Grid.SetColumn(okButton, 1);
            footerGrid.Children.Add(okButton);

            Grid.SetRow(footerGrid, 2);
            mainGrid.Children.Add(footerGrid);

            Content = mainGrid;
        }

        private UIElement CreateRow(int rowIdx, string label, string val)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            
            var labelText = new TextBlock
            {
                Text = label,
                Foreground = TxtMuted,
                Width = 80,
                FontWeight = FontWeights.SemiBold
            };
            
            var valText = new TextBlock
            {
                Text = val,
                Foreground = TxtPrimary
            };
            
            p.Children.Add(labelText);
            p.Children.Add(valText);
            
            Grid.SetRow(p, rowIdx);
            return p;
        }

        private UIElement CreateYoutubeRow(int rowIdx, string label, string url, string displayVal)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            
            var labelText = new TextBlock
            {
                Text = label,
                Foreground = TxtMuted,
                Width = 80,
                FontWeight = FontWeights.SemiBold
            };
            
            var tb = new TextBlock();
            var hl = new Hyperlink { Foreground = AccCyan };
            hl.Inlines.Add(new Run(displayVal));
            hl.NavigateUri = new Uri(url);
            hl.RequestNavigate += Hyperlink_RequestNavigate;
            tb.Inlines.Add(hl);

            p.Children.Add(labelText);
            p.Children.Add(tb);
            
            Grid.SetRow(p, rowIdx);
            return p;
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = e.Uri.AbsoluteUri,
                    UseShellExecute = true
                });
                e.Handled = true;
            }
            catch { /* ignore */ }
        }
    }
}
