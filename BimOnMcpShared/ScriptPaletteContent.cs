using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace BimOnMcpShared
{
    /// <summary>
    /// 모든 플러그인이 공유하는 WPF 팔레트 콘텐츠.
    /// Antigravity 프리미엄 다크 모드 스타일 적용.
    /// host 탭(All / Revit / Navisworks / AutoCAD)으로 스크립트를 구분합니다.
    /// </summary>
    public class ScriptPaletteContent : UserControl
    {
        // ── Antigravity 컬러 시스템 ───────────────────────────────────
        private static readonly Brush BgCanvas       = Frz("#12121E");
        private static readonly Brush BgCard         = Frz("#1E1E2E");
        private static readonly Brush BgControl      = Frz("#2A2A40");
        private static readonly Brush BgControlHover = Frz("#363654");
        private static readonly Brush BgItemHover    = Frz("#242438");
        private static readonly Brush ClrBorder      = Frz("#3F3F5F");
        private static readonly Brush ClrBorderHover = Frz("#5F5F8F");
        private static readonly Brush TxtPrimary     = Frz("#FFFFFF");
        private static readonly Brush TxtMuted       = Frz("#9A9AB0");
        private static readonly Brush AccCyan        = Frz("#00C2FF");
        private static readonly Brush AccPurple      = Frz("#8C52FF");
        private static readonly Brush ClrWarn        = Frz("#FFB703");
        private static readonly Brush ClrOk          = Frz("#3FB950");

        private static Brush Frz(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        private readonly ScriptStorageService _storage;
        private readonly Action<ScriptMeta>   _executeAction;
        private readonly string               _currentHost;

        // 연결(ON/OFF) 컨트롤러 — 다중 인스턴스에서 어느 인스턴스가 MCP 대상인지 제어
        // 생성자 인자가 null 이어도 HostRegistry.Current 로 지연 해석한다(팔레트가
        // 레지스트리 초기화보다 먼저 생성/복원되는 타이밍 문제 방지).
        private readonly IConnectionController? _conn;
        private TextBlock?                      _connStatus;
        private Button?                         _connBtn;
        private Border?                         _connDot;
        private bool                            _connSubscribed;

        private IConnectionController? Ctrl => _conn ?? HostRegistry.Current;

        private readonly System.Windows.Controls.TextBox _search;
        private readonly ListBox                         _list;
        private readonly TextBlock                       _status;
        private readonly List<Button>                    _tabButtons = new();
        private          string                          _activeTab;

        private List<ScriptMeta> _all = new();

        public ScriptPaletteContent(
            ScriptStorageService storage,
            string currentHost,
            Action<ScriptMeta> executeAction,
            IConnectionController? connection = null)
        {
            _storage       = storage;
            _currentHost   = currentHost;
            _executeAction = executeAction;
            _conn          = connection;
            _activeTab     = "all";

            FontFamily = new FontFamily("Segoe UI, Malgun Gothic");
            FontSize   = 12;
            Background = BgCanvas;

            var root = new DockPanel { Margin = new Thickness(12), Background = BgCanvas };

            // ── 헤더 ──────────────────────────────────────────────────
            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(header, Dock.Top);

            // 타이틀 (액센트 시안)
            header.Children.Add(new TextBlock
            {
                Text       = "BimOn AI Scripts",
                FontSize   = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = AccCyan,
                Margin     = new Thickness(2, 0, 0, 8),
            });

            // ── MCP 연결(ON/OFF) 바 — 다중 인스턴스 전환 ─────────────────
            //    컨트롤러는 지연 해석(Ctrl). 팔레트가 레지스트리 초기화보다 먼저
            //    생성/복원돼도 아래 2초 타이머가 상태를 채운다.
            header.Children.Add(BuildConnectionBar());

            // 탭 버튼
            var tabPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(0, 0, 0, 8)
            };
            foreach (var tab in new[] { ("all", "All"), ("Revit", "Revit"),
                                         ("Navisworks", "Navisworks"), ("AutoCAD", "AutoCAD") })
            {
                var btn = new Button
                {
                    Content = tab.Item2,
                    Tag     = tab.Item1,
                    Margin  = new Thickness(0, 0, 6, 0),
                    Style   = TabButtonStyle(),
                };
                btn.Click += OnTabClick;
                _tabButtons.Add(btn);
                tabPanel.Children.Add(btn);
            }
            header.Children.Add(tabPanel);

            // 검색창
            _search = new System.Windows.Controls.TextBox
            {
                Padding         = new Thickness(10, 6, 10, 6),
                FontSize        = 12,
                Background      = BgControl,
                Foreground      = TxtPrimary,
                BorderBrush     = ClrBorder,
                BorderThickness = new Thickness(1),
                CaretBrush      = AccCyan,
            };
            _search.GotKeyboardFocus  += (_, _) => _search.BorderBrush = AccCyan;
            _search.LostKeyboardFocus += (_, _) => _search.BorderBrush = ClrBorder;
            _search.TextChanged       += (_, _) => Filter();
            header.Children.Add(_search);
            root.Children.Add(header);

            // ── 상태 표시 ─────────────────────────────────────────────
            _status = new TextBlock
            {
                FontSize     = 11,
                Margin       = new Thickness(2, 6, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground   = TxtMuted,
            };
            DockPanel.SetDock(_status, Dock.Bottom);
            root.Children.Add(_status);

            // ── 하단 유지보수 툴바 ─────────────────────────────────────
            var toolbar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(0, 8, 0, 0),
            };
            DockPanel.SetDock(toolbar, Dock.Bottom);
            toolbar.Children.Add(ToolButton("Stats",         "Show script storage statistics", () => ShowStats()));
            toolbar.Children.Add(ToolButton("Clean Orphans", "Remove broken links and orphan folders", () => CleanOrphansAction()));
            toolbar.Children.Add(ToolButton("Refresh",       "Reload script list", () => LoadScripts()));
            toolbar.Children.Add(ToolButton("About",         "About BimOn / author info", () => ShowAbout(), last: true));
            root.Children.Add(toolbar);

            // ── 목록 ──────────────────────────────────────────────────
            _list = new ListBox
            {
                Background        = BgCanvas,
                BorderThickness   = new Thickness(0),
                ItemContainerStyle = ItemContainerStyle(),
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
            _list.MouseDoubleClick += OnDoubleClick;
            root.Children.Add(_list);

            Content = root;
            UpdateTabStyles();
            LoadScripts();

            // 연결 상태 초기화 + 주기 갱신(다른 인스턴스 변화·지연 초기화 반영)
            UpdateConnUI();
            var connTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            connTimer.Tick += (_, _) => UpdateConnUI();
            connTimer.Start();
            Unloaded += (_, _) =>
            {
                connTimer.Stop();
                if (_connSubscribed && Ctrl != null) Ctrl.Changed -= OnConnChanged;
            };
        }

        // ── MCP 연결 바 ───────────────────────────────────────────────
        private Border BuildConnectionBar()
        {
            _connDot = new Border
            {
                Width = 8, Height = 8, CornerRadius = new CornerRadius(4),
                Background = TxtMuted, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            _connStatus = new TextBlock
            {
                FontSize = 11, Foreground = TxtMuted,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            _connBtn = new Button
            {
                Content = "연결",
                Style   = ToolButtonStyle(),
                Margin  = new Thickness(8, 0, 0, 0),
                MinWidth = 56,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _connBtn.Click += (_, _) =>
            {
                try { Ctrl?.Connect(); } catch { }
                UpdateConnUI();
            };

            var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(_connBtn, Dock.Right);
            DockPanel.SetDock(_connDot, Dock.Left);
            bar.Children.Add(_connBtn);
            bar.Children.Add(_connDot);
            bar.Children.Add(_connStatus);

            return new Border
            {
                Background      = BgCard,
                BorderBrush     = ClrBorder,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(4),
                Padding         = new Thickness(10, 6, 8, 6),
                Margin          = new Thickness(0, 0, 0, 8),
                Child           = bar,
            };
        }

        private void OnConnChanged()
        {
            if (Dispatcher.CheckAccess()) UpdateConnUI();
            else Dispatcher.BeginInvoke(new Action(UpdateConnUI));
        }

        private void UpdateConnUI()
        {
            if (_connStatus == null || _connBtn == null || _connDot == null) return;
            var c = Ctrl;
            if (c == null)
            {
                // 레지스트리 아직 미초기화 — 타이머가 곧 채움
                _connStatus.Text       = "○ 연결 준비 중…";
                _connStatus.Foreground = TxtMuted;
                _connDot.Background    = TxtMuted;
                _connBtn.Visibility    = Visibility.Collapsed;
                return;
            }
            if (!_connSubscribed) { c.Changed += OnConnChanged; _connSubscribed = true; }
            bool on = c.IsConnected;
            _connStatus.Text       = c.StatusText;
            _connStatus.Foreground = on ? ClrOk : TxtMuted;
            _connDot.Background    = on ? ClrOk : TxtMuted;
            _connBtn.Visibility    = c.CanConnectHere ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── 공개 API ─────────────────────────────────────────────────

        public void LoadScripts()
        {
            _all = _storage.GetAll();
            Filter();
        }

        // ── 탭 전환 ──────────────────────────────────────────────────

        private void OnTabClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                _activeTab = btn.Tag?.ToString() ?? "all";
                UpdateTabStyles();
                Filter();
            }
        }

        private void UpdateTabStyles()
        {
            foreach (var b in _tabButtons)
            {
                bool active = string.Equals(b.Tag?.ToString(), _activeTab, StringComparison.OrdinalIgnoreCase);
                b.Foreground = active ? AccCyan : TxtMuted;
                b.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }

        // ── 필터링 ───────────────────────────────────────────────────

        private void Filter()
        {
            string kw = _search.Text.Trim().ToLowerInvariant();

            var filtered = _activeTab == "all"
                ? _all
                : _all.Where(m => m.Host.Equals(_activeTab, StringComparison.OrdinalIgnoreCase)).ToList();

            if (!string.IsNullOrEmpty(kw))
                filtered = filtered.Where(m =>
                    m.Name.ToLowerInvariant().Contains(kw) ||
                    m.Description.ToLowerInvariant().Contains(kw) ||
                    m.Tags.Any(t => t.ToLowerInvariant().Contains(kw))).ToList();

            _list.Items.Clear();
            foreach (var m in filtered.OrderBy(x => x.Name))
            {
                Brush hostColor = m.Host switch
                {
                    "Revit"      => AccCyan,
                    "Navisworks" => AccPurple,
                    "AutoCAD"    => ClrWarn,
                    _            => TxtMuted,
                };

                // host가 현재 앱과 다르면 흐리게 (Disabled 느낌)
                double opacity = m.Host.Equals(_currentHost, StringComparison.OrdinalIgnoreCase)
                    ? 1.0 : 0.5;

                var item = new ListBoxItem
                {
                    Tag     = m,
                    Opacity = opacity,
                    Content = new StackPanel
                    {
                        Children =
                        {
                            new DockPanel
                            {
                                Margin   = new Thickness(0, 0, 0, 3),
                                Children =
                                {
                                    // host 뱃지
                                    DockChild(new Border
                                    {
                                        Background   = hostColor,
                                        CornerRadius = new CornerRadius(4),
                                        Padding      = new Thickness(6, 1, 6, 1),
                                        Margin       = new Thickness(0, 0, 6, 0),
                                        VerticalAlignment = VerticalAlignment.Center,
                                        Child        = new TextBlock
                                        {
                                            Text       = m.Host,
                                            FontSize   = 10,
                                            FontWeight = FontWeights.SemiBold,
                                            Foreground = BgCanvas,   // 밝은 배지 위 어두운 글자(대비)
                                        }
                                    }, Dock.Right),
                                    new TextBlock
                                    {
                                        Text         = m.Name,
                                        FontWeight   = FontWeights.SemiBold,
                                        FontSize     = 13,
                                        Foreground   = TxtPrimary,
                                        TextTrimming = TextTrimming.CharacterEllipsis,
                                        VerticalAlignment = VerticalAlignment.Center,
                                    }
                                }
                            },
                            new TextBlock
                            {
                                Text         = m.Description,
                                FontSize     = 11,
                                Foreground   = TxtMuted,
                                TextWrapping = TextWrapping.Wrap,
                            },
                            new TextBlock
                            {
                                Text       = m.Tags != null && m.Tags.Length > 0
                                                ? "# " + string.Join("  # ", m.Tags) : "",
                                FontSize   = 10,
                                Foreground = AccPurple,
                                Margin     = new Thickness(0, 2, 0, 0),
                            },
                        }
                    }
                };
                item.ContextMenu = BuildContextMenu(m);
                _list.Items.Add(item);
            }

            _status.Text = filtered.Count == 0
                ? "No scripts found."
                : $"{filtered.Count} script(s)  ·  double-click to run  ·  right-click for options";
        }

        // ── 더블클릭 실행 ─────────────────────────────────────────────

        private void OnDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_list.SelectedItem is ListBoxItem item && item.Tag is ScriptMeta meta)
                RunScript(meta);
        }

        private void RunScript(ScriptMeta meta)
        {
            if (!meta.Host.Equals(_currentHost, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    $"This script is for {meta.Host}.\nCurrent host: {_currentHost}",
                    "Cannot Execute", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _status.Text = $"Running: {meta.Name}...";
            try
            {
                _executeAction(meta);
                _status.Text = $"Done: {meta.Name}";
            }
            catch (Exception ex)
            {
                _status.Text = $"Error: {ex.Message}";
            }
        }

        // ── 우클릭 컨텍스트 메뉴 ───────────────────────────────────────

        private ContextMenu BuildContextMenu(ScriptMeta meta)
        {
            var menu = new ContextMenu
            {
                Background  = BgCard,
                Foreground  = TxtPrimary,
                BorderBrush = ClrBorder,
            };

            menu.Items.Add(MenuEntry("Run",         () => RunScript(meta)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry("Delete…",     () => DeleteScript(meta)));
            menu.Items.Add(MenuEntry("Archive…",    () => ArchiveScript(meta)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry("Open Folder", () => OpenFolder(meta)));
            return menu;
        }

        private static MenuItem MenuEntry(string header, Action onClick)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => onClick();
            return mi;
        }

        // ── 정리 동작 ─────────────────────────────────────────────────

        private void DeleteScript(ScriptMeta meta)
        {
            var r = MessageBox.Show(
                $"Delete this script permanently?\n\n  Name: {meta.Name}\n  Host: {meta.Host}\n\nThis cannot be undone.",
                "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;

            if (_storage.Delete(meta.Name, meta.Host))
            {
                LoadScripts();
                _status.Text = $"Deleted: {meta.Name}";
            }
            else
            {
                _status.Text = $"Delete failed: {meta.Name}";
            }
        }

        private void ArchiveScript(ScriptMeta meta)
        {
            var r = MessageBox.Show(
                $"Archive this script?\n\n  Name: {meta.Name}\n  Host: {meta.Host}\n\nThe script will be moved to Scripts_Archive and removed from the list.",
                "Confirm Archive", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;

            if (_storage.Archive(meta.Name, meta.Host))
            {
                LoadScripts();
                _status.Text = $"Archived: {meta.Name}";
            }
            else
            {
                _status.Text = $"Archive failed: {meta.Name}";
            }
        }

        private void OpenFolder(ScriptMeta meta)
        {
            try
            {
                string? dir = System.IO.Path.GetDirectoryName(meta.ScriptPath);
                if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"")
                        { UseShellExecute = true });
                else
                    _status.Text = "Folder not found.";
            }
            catch (Exception ex) { _status.Text = $"Error: {ex.Message}"; }
        }

        private void ShowStats()
        {
            MessageBox.Show(_storage.GetStatsSummary(), "Script Statistics",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ── 제작자 정보 ───────────────────────────────────────────────

        private void ShowAbout()
        {
            var win = new AboutWindow();
            try
            {
                win.Owner = Window.GetWindow(this);
            }
            catch { /* ignore if Owner cannot be set (e.g. Navisworks thread differences) */ }
            win.ShowDialog();
        }

        private void CleanOrphansAction()
        {
            var r = MessageBox.Show(
                "Remove broken metadata links and orphan script folders?\n\nThis scans the storage and removes entries whose files are missing, and folders not referenced by any script.",
                "Confirm Cleanup", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;

            var (broken, orphans) = _storage.CleanOrphans();
            LoadScripts();
            MessageBox.Show(
                $"Cleanup complete.\n\n  Broken links removed: {broken}\n  Orphan folders removed: {orphans}",
                "Cleanup Done", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ── 스타일 빌더 ───────────────────────────────────────────────

        private Button ToolButton(string text, string tip, Action onClick, bool last = false)
        {
            var b = new Button
            {
                Content = text,
                ToolTip = tip,
                Margin  = last ? new Thickness(0) : new Thickness(0, 0, 6, 0),
                Style   = ToolButtonStyle(),
            };
            b.Click += (_, _) => onClick();
            return b;
        }

        // 탭 버튼: 배경 컨트롤색, 호버 시 밝아짐, 둥근 4px
        private Style TabButtonStyle() => ButtonStyleBase(new Thickness(12, 5, 12, 5));

        // 툴바 버튼
        private Style ToolButtonStyle() => ButtonStyleBase(new Thickness(12, 5, 12, 5));

        private Style ButtonStyleBase(Thickness padding)
        {
            var bd = new FrameworkElementFactory(typeof(Border), "bd");
            bd.SetValue(Border.BackgroundProperty, BgControl);
            bd.SetValue(Border.BorderBrushProperty, ClrBorder);
            bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            bd.SetValue(Border.PaddingProperty, padding);

            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);

            var tpl = new ControlTemplate(typeof(Button)) { VisualTree = bd };

            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, BgControlHover, "bd"));
            hover.Setters.Add(new Setter(Border.BorderBrushProperty, ClrBorderHover, "bd"));
            tpl.Triggers.Add(hover);

            var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(OpacityProperty, 0.9, "bd"));
            tpl.Triggers.Add(pressed);

            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(TemplateProperty, tpl));
            style.Setters.Add(new Setter(ForegroundProperty, TxtMuted));
            style.Setters.Add(new Setter(FontSizeProperty, 11.0));
            style.Setters.Add(new Setter(CursorProperty, Cursors.Hand));
            return style;
        }

        // 리스트 항목: 카드(#1E1E2E) + 둥근 4px + 호버/선택 반응
        private Style ItemContainerStyle()
        {
            var bd = new FrameworkElementFactory(typeof(Border), "bd");
            bd.SetValue(Border.BackgroundProperty, BgCard);
            bd.SetValue(Border.BorderBrushProperty, ClrBorder);
            bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            bd.SetValue(Border.PaddingProperty, new Thickness(10, 8, 10, 8));

            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            bd.AppendChild(cp);

            var tpl = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = bd };

            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, BgItemHover, "bd"));
            hover.Setters.Add(new Setter(Border.BorderBrushProperty, ClrBorderHover, "bd"));
            tpl.Triggers.Add(hover);

            var sel = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
            sel.Setters.Add(new Setter(Border.BorderBrushProperty, AccCyan, "bd"));
            tpl.Triggers.Add(sel);

            var style = new Style(typeof(ListBoxItem));
            style.Setters.Add(new Setter(TemplateProperty, tpl));
            style.Setters.Add(new Setter(MarginProperty, new Thickness(0, 0, 0, 6)));
            style.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            return style;
        }

        // ── 헬퍼 ─────────────────────────────────────────────────────

        private static UIElement DockChild(UIElement child, Dock dock)
        {
            DockPanel.SetDock(child, dock);
            return child;
        }
    }
}
