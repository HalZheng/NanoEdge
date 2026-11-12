using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NanoEdge
{
    // nano://settings：纯 WPF 设置页（SPEC 7.6 / P0-14）。
    // 页面只改 AppSettings 并抛 Changed，真正的应用（主题/布局/内存/语言）交给 MainWindow：
    // 语言切换要重建内部页视图，不能由页面自己干。
    public sealed class SettingsPage : UserControl
    {
        public event Action<string> Changed;              // theme | layout | memory | language | weblanguage | status | privacy | search | password
        public event Action OpenDataFolderRequested;
        public event Action SetDefaultBrowserRequested;

        private readonly AppSettings _settings;
        private readonly Action<string> _notify;
        private readonly Grid _root;
        private readonly StackPanel _body;
        private readonly Canvas _dropdownLayer = new Canvas();
        private TextBlock _memoryHint;
        private bool _capturing;
        private Button _hotkeyButton;
        private TextBlock _hotkeyHint;
        private Button _associateButton;
        private bool _associateBusy;

        // notify 是可选的：其它内部页都拿宿主 Toast 反馈，自测直接 new 页面时传 null。
        public SettingsPage(AppSettings settings, Action<string> notify = null)
        {
            _settings = settings;
            _notify = notify;
            this.SetResourceReference(BackgroundProperty, "CanvasBrush");

            _root = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 28, 0, 0)
            };
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var title = new TextBlock
            {
                Text = L.T("设置"),
                FontSize = 22,
                FontWeight = FontWeights.SemiBold
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

            var subtitle = new TextBlock
            {
                Text = L.T("改动立即生效并自动保存"),
                FontSize = 10,
                Margin = new Thickness(0, 4, 0, 0)
            };
            subtitle.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");

            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            header.Children.Add(title);
            header.Children.Add(subtitle);
            _root.Children.Add(header);

            _body = new StackPanel { Margin = new Thickness(0, 0, 0, 28) };
            var scroll = new ScrollViewer
            {
                Content = _body,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Grid.SetRow(scroll, 1);
            _root.Children.Add(scroll);
            // 覆盖层跨两行，下拉悬浮在内容之上，不把下面的设置行挤下去。
            Grid.SetRowSpan(_dropdownLayer, 2);
            Panel.SetZIndex(_dropdownLayer, 100);
            _root.Children.Add(_dropdownLayer);
            Content = _root;

            SizeChanged += (s, e) => UpdateWidth();
            UpdateWidth();
            PreviewKeyDown += OnCaptureKeyDown;
            Unloaded += (s, e) => StopCapture();
            Build();
        }

        private void UpdateWidth()
        {
            var available = ActualWidth > 0 ? ActualWidth : 980;
            _root.Width = Math.Min(980, Math.Max(360, available - 64));
        }

        private void Build()
        {
            _body.Children.Clear();

            // ---------- 搜索 ----------
            _body.Children.Add(Section(L.T("搜索")));
            var picker = new SearchEnginePicker(_settings, () => Raise("search"), _dropdownLayer,
                Array.Exists(Environment.GetCommandLineArgs(),
                    a => string.Equals(a, "--enginedropdown", StringComparison.OrdinalIgnoreCase)));
            _body.Children.Add(Row(L.T("默认搜索引擎"), L.T("地址栏和起始页都用它，切换后立即生效"), picker));

            // ---------- 外观 ----------
            _body.Children.Add(Section(L.T("外观")));
            _body.Children.Add(Row(L.T("主题"), L.T("暗色适合夜间，跟随系统会随 Windows 设置切换"),
                Segments(new[] { L.T("暗色"), L.T("亮色"), L.T("跟随系统") }, ThemeIndex(), index =>
                {
                    _settings.Theme = index == 0 ? "dark" : index == 1 ? "light" : "system";
                    Raise("theme");
                })));
            // 两项语言分开：前者只翻外壳，后者决定网站检测到的语言版本。
            _body.Children.Add(Row(L.T("界面语言"),
                L.T("只改菜单、设置页和按钮上的字，网页内容不受影响；跟随系统按 Windows 显示语言判断"),
                Segments(new[] { L.T("中文"), "English", L.T("跟随系统") },
                    LanguageIndex(_settings.Language), index =>
                {
                    _settings.Language = index == 0 ? "zh-CN" : index == 1 ? "en-US" : "system";
                    Raise("language");
                })));
            _body.Children.Add(Row(L.T("网页语言"),
                L.T("网站据此判断默认给你哪一版；已打开的页面刷新后生效，不影响上面的界面语言"),
                Segments(new[] { L.T("中文"), "English", L.T("跟随系统") },
                    WebLanguageIndex(_settings.WebLanguage), index =>
                {
                    _settings.WebLanguage = index == 0 ? WebLanguage.Chinese
                        : index == 1 ? WebLanguage.English : WebLanguage.System;
                    Raise("weblanguage");
                })));

            // ---------- 布局 ----------
            _body.Children.Add(Section(L.T("布局")));
            _body.Children.Add(Row(L.T("标签布局"), L.T("紧凑单行把所有东西挤在一行，垂直标签在左侧"),
                Segments(new[] { L.T("紧凑单行"), L.T("标准两行"), L.T("垂直标签") }, (int)_settings.Layout, index =>
                {
                    _settings.Layout = (LayoutMode)index;
                    Raise("layout");
                })));
            _body.Children.Add(Row(L.T("垂直标签固定展开"), L.T("关闭时悬停才展开，节省横向空间"),
                Toggle(_settings.VerticalExpanded, on =>
                {
                    _settings.VerticalExpanded = on;
                    Raise("layout");
                })));
            _body.Children.Add(Row(L.T("显示状态栏"), L.T("底部一行：布局、主题、标签数、内存档位"),
                Toggle(_settings.ShowStatusBar, on =>
                {
                    _settings.ShowStatusBar = on;
                    Raise("status");
                })));

            // ---------- 内存 ----------
            _body.Children.Add(Section(L.T("内存")));
            _memoryHint = new TextBlock
            {
                Text = MemoryHintText(),
                FontSize = 11,
                Margin = new Thickness(2, 0, 2, 12),
                LineHeight = 18,
                TextWrapping = TextWrapping.Wrap
            };
            _memoryHint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            _body.Children.Add(_memoryHint);
            _body.Children.Add(Row(L.T("档位"), L.T("省内存就多重载，少重载就多占内存，选一个能接受的取舍"),
                Segments(new[] { L.T("少重载"), L.T("标准"), L.T("省内存") }, (int)_settings.Memory.Preset, index =>
                {
                    _settings.Memory.ApplyPreset((MemoryPreset)index);
                    if (_memoryHint != null) _memoryHint.Text = MemoryHintText();
                    Raise("memory");
                })));
            var note = new TextBlock
            {
                Text = L.T("释放内存会关掉这个标签的网页进程。切回来时页面重新加载，滚动位置和表单内容尽量用快照恢复，登录状态通常还在。"),
                FontSize = 10,
                Margin = new Thickness(2, 8, 2, 0),
                TextWrapping = TextWrapping.Wrap
            };
            note.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            _body.Children.Add(note);

            // ---------- 隐私 ----------
            _body.Children.Add(Section(L.T("隐私")));
            _body.Children.Add(Row(L.T("密封标签通知"), L.T("关闭时密封标签的页面通知请求会被直接拒绝"),
                Toggle(_settings.SecretNotifications, on =>
                {
                    _settings.SecretNotifications = on;
                    Raise("privacy");
                })));
            // 把「允许」到底允许了什么写清楚：它只放开权限，不保证真能弹（实测过，见 SPEC 0.15 补记 4）。
            var notifyNote = new TextBlock
            {
                Text = L.T("打开只是把通知权限放给网站；Windows 只替注册过通知身份的程序弹通知，当前版本没注册，所以打开后通常看不到弹窗。关闭是直接拦掉，任何情况下都不展示。"),
                FontSize = 10,
                Margin = new Thickness(2, 8, 2, 0),
                TextWrapping = TextWrapping.Wrap
            };
            notifyNote.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            _body.Children.Add(notifyNote);
            _body.Children.Add(Row(L.T("切换密封标签快捷键"),
                L.T("点一下按钮，再按下想用的组合键；至少带 Ctrl / Shift / Alt 中的一个"),
                HotkeyPicker()));
            var secretNote = new TextBlock
            {
                Text = L.T("密封状态不写进会话，程序重启后这个标签不会恢复；按快捷键会把当前标签原地密封，再按一次解除。"),
                FontSize = 10,
                Margin = new Thickness(2, 8, 2, 0),
                TextWrapping = TextWrapping.Wrap
            };
            secretNote.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            _body.Children.Add(secretNote);

            // ---------- 密码 ----------
            _body.Children.Add(Section(L.T("密码")));
            _body.Children.Add(Row(L.T("填充账号密码"),
                L.T("从 KeePassXC 取账号填进登录页；密码本身从不保存在 NanoEdge"),
                Toggle(_settings.PasswordFill, on =>
                {
                    _settings.PasswordFill = on;
                    Raise("password");
                })));

            var keyConfig = KeePassBridge.LoadConfig();
            if (keyConfig != null)
            {
                _body.Children.Add(Row(L.T("KeePassXC"),
                    L.T("已关联：") + keyConfig.ConnectionId,
                    TextButton(L.T("清除关联"), () =>
                    {
                        KeePassBridge.ForgetConfig();
                        // 状态是构造时读的：立刻重建这一页，「立即关联」才会出现，
                        // 不用等用户手动刷新设置页。
                        Build();
                        Raise("password");
                    })));
            }
            else
            {
                // 没关联时把「卡在哪一步」说清楚，比只写「未连接」有用
                var reason = !KeePassBridge.IntegrationRunning()
                    ? L.T("请先在 KeePassXC 设置里开启「浏览器集成」")
                    : (KeePassBridge.FindProxy() == null
                        ? L.T("未安装 KeePassXC")
                        : L.T("点右侧按钮关联，KeePassXC 会弹窗让您确认"));
                // 关联不该依赖「先找到一个登录页、钥匙图标还恰好亮着」：
                // SPA 晚渲染时这两个条件凑不齐，用户连确认弹窗都见不到。
                _associateButton = (Button)TextButton(L.T("立即关联"), OnAssociateClicked);
                _body.Children.Add(Row(L.T("KeePassXC"), reason, _associateButton));
            }

            var fillNote = new TextBlock
            {
                Text = L.T("密码只在填充的那一瞬间经过内存，不写日志、不进页面快照。关闭开关后立刻不再显示钥匙图标；已经打开的页面要刷新才会移除扫描脚本。"),
                FontSize = 10,
                Margin = new Thickness(2, 8, 2, 0),
                TextWrapping = TextWrapping.Wrap
            };
            fillNote.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            _body.Children.Add(fillNote);

            // ---------- 关于 ----------
            _body.Children.Add(Section(L.T("关于")));
            _body.Children.Add(Row(L.T("版本"), "NanoEdge " + Program.Version, null));
            _body.Children.Add(Row(L.T("数据目录"), AppPaths.Roaming,
                TextButton(L.T("打开"), () =>
                {
                    var handler = OpenDataFolderRequested;
                    if (handler != null) handler();
                })));
            _body.Children.Add(Row(L.T("默认浏览器"),
                L.T("只写注册表登记，Windows 会要求您在「默认应用」里确认"),
                TextButton(L.T("设为默认浏览器"), () =>
                {
                    var handler = SetDefaultBrowserRequested;
                    if (handler != null) handler();
                })));
        }

        private int ThemeIndex()
        {
            if (_settings.Theme == "light") return 1;
            if (_settings.Theme == "system") return 2;
            return 0;
        }

        // 选中项看的是持久化的 AppSettings.Language，不是 L.Language：
        // "system" 经 L.Normalize 会变成具体的 zh-CN / en-US，用生效语言判断
        // 会把「跟随系统」误显示成「中文 / English」。抽成静态方法便于自测断言。
        internal static int LanguageIndex(string language)
        {
            if (string.Equals(language, "system", StringComparison.OrdinalIgnoreCase)) return 2;
            if (!string.IsNullOrEmpty(language) &&
                language.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return 1;
            return 0;
        }

        // 与界面语言同样的三档，但读的是另一个持久化字段，自测可分别断言。
        internal static int WebLanguageIndex(string language)
        {
            switch (WebLanguage.Normalize(language))
            {
                case WebLanguage.Chinese: return 0;
                case WebLanguage.English: return 1;
                default: return 2;
            }
        }

        private string MemoryHintText()
        {
            switch (_settings.Memory.Preset)
            {
                case MemoryPreset.Conservative:
                    return L.T("少重载：失焦 30 秒降冷，后台标签从不自动释放，工作集到 1200MB 才施压。切回标签基本不用重新加载，但开很多标签时内存占用最高。");
                case MemoryPreset.Aggressive:
                    return L.T("省内存：失焦 1 秒降冷，后台 5 分钟就释放，工作集上限 450MB。内存占用最低，但切回旧标签经常要重新加载页面。");
                default:
                    return L.T("标准：失焦 2 秒降冷，后台 30 分钟释放，工作集上限 700MB。平时切回不用重载，很久没看的标签会重新加载。");
            }
        }

        private void Raise(string kind)
        {
            // 落盘交给 MainWindow（它同时负责应用改动），页面只改值 + 通知。
            var handler = Changed;
            if (handler != null) handler(kind);
        }

        // ---------- KeePassXC 关联 ----------

        // 只走已有的 KeePassBridge.AssociateAsync，不另起一套协议；
        // 弹窗要用户填连接名并点「保存并允许访问」，所以给足 120 秒。
        private async void OnAssociateClicked()
        {
            if (_associateBusy) return;
            _associateBusy = true;
            if (_associateButton != null)
            {
                _associateButton.Content = L.T("正在关联…");
                _associateButton.IsEnabled = false;
            }
            Notify(L.T("请在 KeePassXC 弹窗里允许 NanoEdge 访问"));
            try
            {
                var result = await KeePassBridge.AssociateAsync("NanoEdge", TimeSpan.FromSeconds(120));
                if (result.Ok)
                    Notify(L.T("已关联 KeePassXC") + "：" + result.Message);
                else
                    Notify(KeePassBridge.StatusText(result.Status) +
                           (string.IsNullOrEmpty(result.Message) ? "" : "：" + result.Message));
            }
            catch (Exception ex)
            {
                Diagnostics.Log("associate from settings failed: " + ex.Message);
                Notify(L.T("KeePassXC 操作失败"));
            }
            finally
            {
                _associateBusy = false;
                // 成功/失败都要重建这一页：成功显示「已关联：xxx」，失败恢复按钮可点。
                Build();
            }
        }

        private void Notify(string message)
        {
            if (_notify != null) _notify(message);
        }

        // 自测用：不遍历可视化树就能拿到关联按钮。
        internal Button AssociateButtonForTest { get { return _associateButton; } }

        // ---------- 密封标签切换键录入 ----------

        // 自测用：直接拿录入按钮，省掉遍历还没加载的可视化树。
        internal Button HotkeyButtonForTest { get { return _hotkeyButton; } }

        private FrameworkElement HotkeyPicker()
        {
            var stack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            _hotkeyButton = (Button)TextButton(Hotkey.Display(_settings.SecretHotkey), null);
            _hotkeyButton.MinWidth = 132;
            _hotkeyButton.HorizontalAlignment = HorizontalAlignment.Right;
            _hotkeyButton.Click += (s, e) =>
            {
                if (_capturing) { StopCapture(); return; }
                _capturing = true;
                _hotkeyButton.Content = L.T("请按下组合键…");
                _hotkeyHint.Text = L.T("需要 Ctrl / Shift / Alt 中的至少一个，Esc 取消");
                _hotkeyButton.Focus();
            };

            _hotkeyHint = new TextBlock
            {
                FontSize = 10,
                MaxWidth = 230,
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                TextWrapping = TextWrapping.Wrap
            };
            _hotkeyHint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");

            stack.Children.Add(_hotkeyButton);
            stack.Children.Add(_hotkeyHint);
            return stack;
        }

        private void OnCaptureKeyDown(object sender, KeyEventArgs e)
        {
            if (!_capturing) return;

            // 录入期间所有按键都留给自己，别让宿主快捷键顺手执行掉这个组合。
            e.Handled = true;
            var real = Hotkey.NormalizeKey(e.Key, e.SystemKey);
            if (real == Key.Escape) { StopCapture(); return; }

            var spec = Hotkey.Capture(e.Key, Keyboard.Modifiers, e.SystemKey);
            if (spec == null)
            {
                var mods = Keyboard.Modifiers & Hotkey.Supported;
                _hotkeyHint.Text = Hotkey.IsReserved(mods, real)
                    ? L.T("这个组合已被其它功能占用")
                    : L.T("请按住 Ctrl / Shift / Alt 再按一个键");
                return;
            }

            _settings.SecretHotkey = spec;
            StopCapture();
            _hotkeyHint.Text = L.T("已更新：") + Hotkey.Display(spec);
            Raise("hotkey");
        }

        private void StopCapture()
        {
            _capturing = false;
            if (_hotkeyButton != null) _hotkeyButton.Content = Hotkey.Display(_settings.SecretHotkey);
            if (_hotkeyHint != null) _hotkeyHint.Text = "";
        }

        // 宿主在两条快捷键入口都要先问一声：录入状态下不能分发快捷键。
        internal static bool IsCapturing(DependencyObject element)
        {
            while (element != null)
            {
                var page = element as SettingsPage;
                // 焦点在页面内的控件上，页面必然是可见的；切走标签后焦点也随之转移，
                // 不需要额外判 IsVisible（会给未挂载到窗口的自测控件造成假阴性）。
                if (page != null) return page._capturing;
                element = VisualTreeHelper.GetParent(element);
            }
            return false;
        }

        // ---------- 通用控件 ----------

        private FrameworkElement Section(string text)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(2, 20, 0, 6)
            };
            block.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            return block;
        }

        private FrameworkElement Row(string label, string hint, FrameworkElement control)
        {
            var border = new Border
            {
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(2, 10, 2, 10),
                MinHeight = 56
            };
            border.SetResourceReference(Border.BorderBrushProperty, "LineBrush");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            var labelBlock = new TextBlock { Text = label, FontSize = 12.5 };
            labelBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            text.Children.Add(labelBlock);
            if (!string.IsNullOrEmpty(hint))
            {
                var hintBlock = new TextBlock
                {
                    Text = hint,
                    FontSize = 10,
                    Margin = new Thickness(0, 3, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxHeight = 30
                };
                hintBlock.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
                text.Children.Add(hintBlock);
            }
            grid.Children.Add(text);

            if (control != null)
            {
                control.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(control, 1);
                grid.Children.Add(control);
            }

            border.Child = grid;
            return border;
        }

        private FrameworkElement Segments(string[] labels, int selected, Action<int> onPick)
        {
            var outer = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(3),
                BorderThickness = new Thickness(1)
            };
            outer.SetResourceReference(Border.BackgroundProperty, "InputBrush");
            outer.SetResourceReference(Border.BorderBrushProperty, "InputBorderBrush");

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            var items = new Border[labels.Length];

            for (var i = 0; i < labels.Length; i++)
            {
                var index = i;
                var text = new TextBlock
                {
                    Text = labels[i],
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var item = new Border
                {
                    Height = 24,
                    Padding = new Thickness(11, 0, 11, 0),
                    CornerRadius = new CornerRadius(6),
                    Margin = new Thickness(i == 0 ? 0 : 2, 0, 0, 0),
                    Cursor = Cursors.Hand,
                    Child = text
                };
                item.MouseLeftButtonUp += (s, e) =>
                {
                    Select(items, index);
                    onPick(index);
                };
                items[i] = item;
                panel.Children.Add(item);
            }

            Select(items, selected);
            outer.Child = panel;
            return outer;
        }

        private static void Select(Border[] items, int selected)
        {
            for (var i = 0; i < items.Length; i++)
            {
                var text = items[i].Child as TextBlock;
                if (i == selected)
                {
                    items[i].SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
                    if (text != null) text.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
                }
                else
                {
                    items[i].Background = new SolidColorBrush(Colors.Transparent);
                    if (text != null) text.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
                }
            }
        }

        private FrameworkElement Toggle(bool on, Action<bool> changed)
        {
            var track = new Border
            {
                Width = 34,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            track.SetResourceReference(Border.BackgroundProperty, on ? "AccentBrush" : "StateDiscardedBrush");

            var knob = new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(7),
                Background = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Margin = new Thickness(2, 0, 2, 0)
            };
            track.Child = knob;

            var state = on;
            track.MouseLeftButtonUp += (s, e) =>
            {
                state = !state;
                track.SetResourceReference(Border.BackgroundProperty,
                    state ? "AccentBrush" : "StateDiscardedBrush");
                knob.HorizontalAlignment = state ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                changed(state);
            };
            return track;
        }

        private FrameworkElement TextButton(string text, Action action)
        {
            var button = new Button
            {
                Content = text,
                Height = 28,
                MinWidth = 64,
                FontSize = 11,
                Padding = new Thickness(12, 0, 12, 0),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            button.SetResourceReference(Control.BackgroundProperty, "HoverBrush");
            button.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            button.Click += (s, e) => { if (action != null) action(); };
            return button;
        }
    }
}
