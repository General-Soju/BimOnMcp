using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BimOnMcpShared;

namespace BimOnNavisPlugin
{
    /// <summary>
    /// Navisworks 용 스크립트 팔레트 창.
    /// ScriptPaletteContent(공유 WPF UserControl)를 호스팅합니다.
    /// </summary>
    internal class NavisScriptWindow : Window
    {
        private readonly ScriptPaletteContent _content;

        public NavisScriptWindow(ScriptPaletteContent content)
        {
            _content = content;

            Title         = "BimOn MCP — Scripts";
            Width         = 360;
            Height        = 600;
            MinWidth      = 260;
            MinHeight     = 320;
            WindowStyle   = WindowStyle.ToolWindow;   // 작은 제목 표시줄
            ResizeMode    = ResizeMode.CanResizeWithGrip;
            ShowInTaskbar = false;

            // 어두운 테두리 Border 로 감싸기
            var border = new Border
            {
                Background      = new SolidColorBrush(Color.FromRgb(245, 245, 248)),
                BorderBrush     = new SolidColorBrush(Color.FromRgb(100, 80, 160)),
                BorderThickness = new Thickness(1),
                Child           = content
            };
            Content = border;

            // 우측 상단 배치
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - Width  - 24;
            Top  = wa.Top            + 24;
        }

        public void Refresh() => _content.LoadScripts();
    }
}
