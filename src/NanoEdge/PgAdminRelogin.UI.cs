using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace NanoEdge
{
    // =====================================================================
    // 悬浮重登按钮（覆盖 SPEC 8.1 的整行工具条）
    //
    // WebView2 是 HwndHost，WPF 元素盖不住它，所以「悬浮在网页上方」只能用 Popup：
    // 独立顶层窗口、不参与布局，网页不会被挤下去，宽度就是按钮（+状态文字）那么宽，居中。
    // 用户口径：只有确认掉登录才出现；切到别的标签/站点立刻收起。
    // =====================================================================
    internal sealed class PgAdminReloginBar : IDisposable
    {
        private readonly Popup _popup;
        private readonly Border _card;
        private readonly Button _button;
        private readonly TextBlock _status;
        private readonly DispatcherTimer _statusTimer;
        private readonly SolidColorBrush _normalBrush = Brush("#D32F2F");
        private readonly CornerRadius _cornerRadius = new CornerRadius(6);
        private readonly FrameworkElement _anchor;
        private bool _signedOut;
        private bool _busy;
        private bool _hasStatus;
        private bool _windowVisible = true;

        public event Action Clicked;

        public PgAdminReloginBar(FrameworkElement anchor)
        {
            _anchor = anchor;

            _card = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 4, 6, 4)
            };
            _card.SetResourceReference(Border.BackgroundProperty, "ChromeBrush");
            _card.SetResourceReference(Border.BorderBrushProperty, "LineBrush");

            _status = new TextBlock
            {
                FontSize = 12,
                MaxWidth = 360,
                Margin = new Thickness(6, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Visibility = Visibility.Collapsed
            };
            _status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");

            _button = MakeButton();
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(_status);
            row.Children.Add(_button);
            _card.Child = row;

            _popup = new Popup
            {
                PlacementTarget = anchor,
                Placement = PlacementMode.Relative,
                StaysOpen = true,
                AllowsTransparency = true,
                Child = _card,
                IsOpen = false
            };

            _card.SizeChanged += OnCardSizeChanged;
            _anchor.SizeChanged += OnAnchorSizeChanged;
            _statusTimer = new DispatcherTimer();
            _statusTimer.Tick += (s, e) => { _statusTimer.Stop(); ClearStatus(); };
            ApplyLanguage();
        }

        private void OnCardSizeChanged(object sender, SizeChangedEventArgs e) { Reposition(); }

        private void OnAnchorSizeChanged(object sender, SizeChangedEventArgs e) { Reposition(); }

        // 居中：相对 _tabHost（网页区域）左上角算偏移，宽度就是卡片本身。
        public void Reposition()
        {
            if (!_popup.IsOpen) return;
            if (_anchor.ActualWidth <= 0) return;
            _card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var width = _card.DesiredSize.Width;
            if (width <= 0) return;
            _popup.HorizontalOffset = Math.Max(0, (_anchor.ActualWidth - width) / 2);
            _popup.VerticalOffset = 8;
        }

        // 窗口最小化时收起；恢复时按状态重新显示。
        public void SetWindowVisible(bool visible)
        {
            _windowVisible = visible;
            ApplyState();
        }

        public void Dispose()
        {
            _statusTimer.Stop();
            try { _popup.IsOpen = false; } catch { }
            _card.SizeChanged -= OnCardSizeChanged;
            _anchor.SizeChanged -= OnAnchorSizeChanged;
        }

        private Button MakeButton()
        {
            var button = new Button
            {
                Height = 28,
                MinWidth = 88,
                Margin = new Thickness(0),
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Cursor = Cursors.Hand,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };

            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "Bd";
            border.SetValue(Border.BackgroundProperty, _normalBrush);
            border.SetValue(Border.CornerRadiusProperty, _cornerRadius);
            border.SetValue(Border.PaddingProperty, new Thickness(14, 0, 14, 0));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            template.VisualTree = border;

            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, Brush("#B71C1C"), "Bd"));
            template.Triggers.Add(hover);
            var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(Border.BackgroundProperty, Brush("#8E0E0E"), "Bd"));
            template.Triggers.Add(pressed);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(Border.BackgroundProperty, Brush("#9E9E9E"), "Bd"));
            template.Triggers.Add(disabled);

            button.Template = template;
            button.Click += (s, e) =>
            {
                var handler = Clicked;
                if (handler != null) handler();
            };
            return button;
        }

        private static SolidColorBrush Brush(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        public void ApplyLanguage()
        {
            _button.Content = _busy ? L.T("登录中…") : L.T("重登录");
            AutomationProperties.SetName(_button, (string)_button.Content);
            if (_hasStatus && string.Equals((string)_status.Tag, "busy", StringComparison.Ordinal))
                _status.Text = L.T("登录中…");
        }

        public void SetSignedOut(bool signedOut)
        {
            _signedOut = signedOut;
            if (!signedOut) _busy = false;
            ApplyState();
        }

        public void SetBusy(bool busy)
        {
            _busy = busy;
            ApplyState();
        }

        public void ShowBusy()
        {
            _status.Text = L.T("登录中…");
            _status.Tag = "busy";
            _status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            _status.Visibility = Visibility.Visible;
            _hasStatus = true;
            _statusTimer.Stop();
            ApplyState();
        }

        // 8.3：成功绿色 20 秒；失败橙色，同样 20 秒后清掉，但按钮仍在（还掉着登录）。
        public void ShowStatus(string text, bool success, int seconds)
        {
            _status.Text = string.IsNullOrEmpty(text) ? "" : text;
            _status.Tag = success ? "success" : "error";
            _status.Foreground = Brush(success ? "#15803D" : "#B45309");
            _status.Visibility = string.IsNullOrEmpty(_status.Text)
                ? Visibility.Collapsed : Visibility.Visible;
            _hasStatus = !string.IsNullOrEmpty(_status.Text);
            _statusTimer.Stop();
            if (_hasStatus && seconds > 0)
            {
                _statusTimer.Interval = TimeSpan.FromSeconds(seconds);
                _statusTimer.Start();
            }
            ApplyState();
        }

        public void ClearStatus()
        {
            _statusTimer.Stop();
            _status.Text = "";
            _status.Tag = null;
            _status.Visibility = Visibility.Collapsed;
            _hasStatus = false;
            ApplyState();
        }

        private void ApplyState()
        {
            _button.Visibility = _signedOut ? Visibility.Visible : Visibility.Collapsed;
            _button.IsEnabled = _signedOut && !_busy;
            var text = _busy ? L.T("登录中…") : L.T("重登录");
            _button.Content = text;
            AutomationProperties.SetName(_button, text);
            if ((_signedOut || _hasStatus) && _windowVisible) Open();
            else Close();
        }

        private void Open()
        {
            if (_popup.IsOpen) { Reposition(); return; }
            if (!_anchor.IsVisible || _anchor.ActualWidth <= 0) return;
            // 打开前先量一次，避免第一帧出现在最左侧再跳到中间。
            _card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var width = _card.DesiredSize.Width;
            if (width > 0)
            {
                _popup.HorizontalOffset = Math.Max(0, (_anchor.ActualWidth - width) / 2);
                _popup.VerticalOffset = 8;
            }
            _popup.IsOpen = true;
        }

        private void Close()
        {
            if (_popup.IsOpen) _popup.IsOpen = false;
        }

        // 自测用：按钮 / 状态的可见性与文案。
        internal bool ButtonVisibleForTest { get { return _button.Visibility == Visibility.Visible; } }
        internal string ButtonTextForTest { get { return Convert.ToString(_button.Content); } }
        internal string StatusTextForTest { get { return _status.Text; } }
        internal bool IsVisibleForTest { get { return _popup.IsOpen; } }
        internal FrameworkElement CardForTest { get { return _card; } }
        internal double HorizontalOffsetForTest { get { return _popup.HorizontalOffset; } }
        internal string NormalBackgroundForTest { get { return _normalBrush.Color.ToString(); } }
        internal double CornerRadiusForTest { get { return _cornerRadius.TopLeft; } }
        internal bool BoldForTest { get { return _button.FontWeight == FontWeights.Bold; } }
        internal string ForegroundForTest
        {
            get { var b = _button.Foreground as SolidColorBrush; return b == null ? "" : b.Color.ToString(); }
        }
        internal bool ButtonEnabledForTest { get { return _button.IsEnabled; } }
    }

    // =====================================================================
    // 7 掉线检测 / 冷却 / 熄灭 / 自动重登
    // =====================================================================
    internal sealed class PgAdminSessionMonitor
    {
        private readonly PgAdminReloginBar _bar;
        private readonly PgAdminReloginService _service;
        private readonly Func<TabModel> _activeTab;
        private readonly Func<TabModel, CoreWebView2> _coreOf;
        private readonly Action<string> _openEditor;
        private readonly Func<string, bool> _confirm;
        private readonly Action<CoreWebView2, string> _navigate;

        private readonly Dictionary<string, bool> _signedOut = new Dictionary<string, bool>();
        private readonly HashSet<string> _probing = new HashSet<string>();
        private readonly Dictionary<string, DateTime> _lastProbe = new Dictionary<string, DateTime>();
        private readonly Queue<DateTime> _autoAttempts = new Queue<DateTime>();
        private DateTime _lastAutoUtc = DateTime.MinValue;
        private bool _autoPaused;
        private bool _disposed;

        private const int CooldownSeconds = 60;
        private const int HourlyLimit = 3;

        public PgAdminSessionMonitor(PgAdminReloginBar bar,
                                     Func<IntPtr> parentHwnd,
                                     Func<Task<CoreWebView2Environment>> environment,
                                     Func<TabModel> activeTab,
                                     Func<TabModel, CoreWebView2> coreOf,
                                     Action<string> openEditor,
                                     Func<string, bool> confirm,
                                     Action<CoreWebView2, string> navigate)
        {
            _bar = bar;
            _service = new PgAdminReloginService(parentHwnd, environment);
            _activeTab = activeTab;
            _coreOf = coreOf;
            _openEditor = openEditor;
            _confirm = confirm;
            _navigate = navigate;
            _bar.Clicked += OnButtonClicked;
            ScriptStore.Changed += OnConfigChanged;
            PgAdminReloginEvents.Saved += OnCredentialsSaved;
        }

        public bool IsBusy { get { return _service.IsBusy; } }
        internal bool AutoPausedForTest { get { return _autoPaused; } }
        internal PgAdminReloginService ServiceForTest { get { return _service; } }
        internal ReloginResult LastResultForTest { get; private set; }
        internal Task ReloginForTest(PgAdminSiteConfig cfg, string trigger)
        {
            return ReloginAsync(_activeTab(), cfg, trigger);
        }
        // 自测里避免真弹出模态确认框；null 时走真实 MessageBox。
        internal Func<string, bool> ConfirmOverrideForTest { get; set; }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _bar.Clicked -= OnButtonClicked;
            ScriptStore.Changed -= OnConfigChanged;
            PgAdminReloginEvents.Saved -= OnCredentialsSaved;
        }

        private void OnButtonClicked()
        {
            // 7.3：手动点击解除「自动重登暂停」。
            _autoPaused = false;
            var unused = ManualAsync();
        }

        private async Task ManualAsync()
        {
            if (_service.IsBusy) return;
            var tab = _activeTab();
            if (tab == null) return;
            bool signedOut;
            _signedOut.TryGetValue(tab.Id, out signedOut);
            if (!signedOut) return;      // 没掉登录时按钮根本不显示
            var cfg = PgAdminReloginConfig.ForUrl(tab.Url);
            if (cfg == null) { SetSignedOut(tab, false); return; }
            await ReloginAsync(tab, cfg, "Manual");
        }

        public void OnActiveTabChanged()
        {
            // 状态提示属于上一个标签的操作反馈，切走就丢掉；
            // 否则「已重新登录」那 20 秒会让悬浮条停在别的站点上（用户反馈过的 bug）。
            _bar.ClearStatus();
            var tab = _activeTab();
            if (tab == null) { _bar.SetSignedOut(false); return; }
            bool signedOut;
            _signedOut.TryGetValue(tab.Id, out signedOut);
            _bar.SetSignedOut(signedOut);
        }

        // 7.1 触发条件 1：导航完成后落在 /login。
        public void OnNavigationDone(TabModel tab, string url, bool success)
        {
            if (tab == null || !success) return;
            var cfg = PgAdminReloginConfig.ForUrl(url);
            if (cfg == null) { ClearTab(tab); return; }
            var path = PgAdminSiteResolver.PathOf(url);
            if (!path.StartsWith(cfg.LoginPath, StringComparison.OrdinalIgnoreCase)) return;
            var unused = HandleTriggerAsync(tab, cfg);
        }

        // 7.1 触发条件 2：/browser/ 或 /sqleditor/ 相关请求返回 401（排除 /login）。
        public void OnResponse(TabModel tab, string uri, int status)
        {
            if (status != 401 || tab == null || _disposed) return;
            var cfg = PgAdminReloginConfig.ForUrl(tab.Url);
            if (cfg == null) return;
            string origin, path;
            PgAdminSiteResolver.Decompose(uri, out origin, out path);
            if (!string.Equals(origin, cfg.Origin, StringComparison.OrdinalIgnoreCase)) return;
            var requestPath = PgAdminSiteResolver.PathOf(uri);
            if (requestPath.StartsWith(cfg.LoginPath, StringComparison.OrdinalIgnoreCase)) return;
            if (requestPath.IndexOf(cfg.BrowserPath, StringComparison.OrdinalIgnoreCase) < 0 &&
                requestPath.IndexOf("/sqleditor", StringComparison.OrdinalIgnoreCase) < 0) return;
            var unused = HandleTriggerAsync(tab, cfg);
        }

        private async Task HandleTriggerAsync(TabModel tab, PgAdminSiteConfig cfg)
        {
            if (_disposed || _service.IsBusy || _probing.Contains(tab.Id)) return;
            DateTime last;
            if (_lastProbe.TryGetValue(tab.Id, out last) &&
                (DateTime.UtcNow - last).TotalSeconds < 5) return;   // 401 风暴去重
            _lastProbe[tab.Id] = DateTime.UtcNow;
            _probing.Add(tab.Id);
            PageSession session;
            try
            {
                session = await PgAdminSessionProbe.ProbeAsync(_coreOf(tab), cfg);
            }
            finally
            {
                _probing.Remove(tab.Id);
            }

            if (session == PageSession.Valid) { SetSignedOut(tab, false); return; }
            if (session != PageSession.SignedOut) return;   // Unknown：什么都不做，绝不刷新
            SetSignedOut(tab, true);
            if (_autoPaused || !cfg.Settings.AutoRelogin || !AutoAllowed()) return;
            await ReloginAsync(tab, cfg, "Auto");
        }

        private bool AutoAllowed()
        {
            if (_autoPaused) return false;
            var now = DateTime.UtcNow;
            if (_lastAutoUtc != DateTime.MinValue &&
                (now - _lastAutoUtc).TotalSeconds < CooldownSeconds) return false;
            while (_autoAttempts.Count > 0 && (now - _autoAttempts.Peek()).TotalHours >= 1)
                _autoAttempts.Dequeue();
            return _autoAttempts.Count < HourlyLimit;
        }

        private void RecordAuto()
        {
            var now = DateTime.UtcNow;
            _lastAutoUtc = now;
            _autoAttempts.Enqueue(now);
        }

        private async Task ReloginAsync(TabModel tab, PgAdminSiteConfig cfg, string trigger)
        {
            if (_service.IsBusy) return;
            if (trigger == "Auto") RecordAuto();
            _bar.SetBusy(true);
            _bar.ShowBusy();
            try
            {
                var request = new ReloginRequest
                {
                    Config = cfg,
                    Main = _coreOf(tab),
                    Trigger = trigger,
                    ConfirmRefresh = trigger == "Manual"
                        ? (ConfirmOverrideForTest ?? _confirm) : null,
                    Navigate = trigger == "Manual" ? _navigate : null
                };
                var result = await _service.RunAsync(request);
                LastResultForTest = result;
                ApplyResult(tab, cfg, result);
            }
            finally
            {
                _bar.SetBusy(false);
            }
        }

        private void ApplyResult(TabModel tab, PgAdminSiteConfig cfg, ReloginResult result)
        {
            switch (result.Outcome)
            {
                case ReloginOutcome.Succeeded:
                    SetSignedOut(tab, false);
                    _bar.ShowStatus(L.T("已重新登录"), true, 20);
                    break;
                case ReloginOutcome.AlreadySignedIn:
                    SetSignedOut(tab, false);
                    _bar.ShowStatus(L.T("会话有效"), true, 20);
                    break;
                case ReloginOutcome.Refreshed:
                    SetSignedOut(tab, false);
                    _bar.ShowStatus(L.T("已重新登录"), true, 20);
                    break;
                case ReloginOutcome.NoCredentials:
                case ReloginOutcome.Corrupt:
                    // F-4 / 9：不发起网络请求，直接打开该脚本的编辑页。
                    _openEditor(cfg == null || cfg.Script == null ? "" : cfg.Script.Id);
                    _bar.ShowStatus(string.IsNullOrEmpty(result.Message)
                        ? L.T("请先保存 pgAdmin 凭据") : result.Message, false, 20);
                    break;
                case ReloginOutcome.Rejected:
                case ReloginOutcome.Failed:
                    _autoPaused = true;                 // 7.3 熄灭，直到手动点击 / 重启 / 保存凭据
                    SetSignedOut(tab, true);
                    _bar.ShowStatus(string.IsNullOrEmpty(result.Message)
                        ? L.T("重登失败，请检查密码") : result.Message, false, 20);
                    break;
                case ReloginOutcome.RefreshCancelled:
                    SetSignedOut(tab, true);
                    if (!string.IsNullOrEmpty(result.Message))
                        _bar.ShowStatus(result.Message, false, 20);
                    else
                        _bar.ClearStatus();
                    break;
                case ReloginOutcome.NoSite:
                    SetSignedOut(tab, false);
                    break;
                case ReloginOutcome.Busy:
                    break;
            }
        }

        private void SetSignedOut(TabModel tab, bool value)
        {
            if (tab == null) return;
            _signedOut[tab.Id] = value;
            if (ReferenceEquals(tab, _activeTab())) _bar.SetSignedOut(value);
        }

        private void ClearTab(TabModel tab)
        {
            if (tab == null) return;
            _signedOut.Remove(tab.Id);
            if (!ReferenceEquals(tab, _activeTab())) return;
            // 同一个标签导航到别的网站时也要立刻收起悬浮条（状态提示一并清掉）。
            _bar.ClearStatus();
            _bar.SetSignedOut(false);
        }

        // 保存凭据 / 改动配置后：解除熄灭，并对当前已打开的页面补一次即时注入。
        public void OnCredentialsSaved()
        {
            _autoPaused = false;
            var unused = InjectCurrentAsync();
        }

        private void OnConfigChanged()
        {
            var unused = InjectCurrentAsync();
        }

        private async Task InjectCurrentAsync()
        {
            if (_disposed) return;
            var tab = _activeTab();
            if (tab == null) return;
            var cfg = PgAdminReloginConfig.ForUrl(tab.Url);
            if (cfg == null) { ClearTab(tab); return; }
            var core = _coreOf(tab);
            if (core == null) return;
            try { await core.ExecuteScriptAsync(PgAdminReloginScripts.Document()); }
            catch (Exception ex) { PgAdminLog.Exception("inject-now", ex); }
        }

        // 自测用：直接把掉登录状态打进真实状态机，验证按钮显示规则与冷却。
        internal void MarkSignedOutForTest(TabModel tab, bool value) { SetSignedOut(tab, value); }
        internal bool CanAutoForTest() { return !_autoPaused && AutoAllowed(); }
        internal void RecordAutoForTest() { RecordAuto(); }
        internal void PauseAutoForTest() { _autoPaused = true; }
        internal void ClearAutoPauseForTest() { _autoPaused = false; }
        internal void ResetAutoForTest()
        {
            _autoPaused = false;
            _lastAutoUtc = DateTime.MinValue;
            _autoAttempts.Clear();
            _lastProbe.Clear();
        }
    }
}
