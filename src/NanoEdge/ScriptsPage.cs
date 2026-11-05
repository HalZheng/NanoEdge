using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace NanoEdge
{
    // nano://scripts：用户脚本管理页（SPEC 7.3）。
    // 与历史/书签/下载一样是纯 WPF 内部页：打开它不创建 WebView2，renderer 增量为 0。
    // 列表 + 编辑器都在这一个控件里，编辑器用整页浮层（不是 Popup，因为页内没有 HwndHost 要盖）。
    public sealed class ScriptsPage : InternalPage
    {
        private static readonly FontFamily MonoFont =
            new FontFamily("Consolas, Cascadia Mono, Courier New");

        private readonly Action<string> _notify;
        private readonly Func<string> _currentSite;

        private Border _overlay;
        private Border _overlayCard;
        private TextBox _code;
        private TextBox _numbers;
        private TextBox _urlBox;
        private UserScript _editing;
        private int _lineCount = -1;

        private string _pendingImport;
        private string _pendingImportSource;

        // 自测用：列表实际渲染的行数 / 最近一次导入确认里展示的授权摘要。
        internal int RenderedRowCount { get; private set; }
        internal string LastImportSummary { get; private set; }
        internal bool HasPendingImport { get { return _pendingImport != null; } }
        internal string CurrentSiteForTest { get { return CurrentSite(); } }

        public ScriptsPage(Action<string> notify, Func<string> currentSite = null)
            : base(L.T("用户脚本"), L.T("搜索脚本（名称、命名空间或匹配站点）"))
        {
            _notify = notify;
            _currentSite = currentSite;
            AddAction(L.T("导入文件"), ImportFile);
            AddAction(L.T("从剪贴板导入"), ImportClipboard);
            AddAction(L.T("从 URL 导入"), ShowUrlImport);
            AddAction(L.T("打开脚本目录"), OpenFolder);

            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape && _overlay != null && _overlay.Visibility == Visibility.Visible)
                {
                    CloseOverlay();
                    e.Handled = true;
                }
            };
            Refresh();
        }

        protected override string CountLabel()
        {
            var all = ScriptStore.All();
            var enabled = all.Count(s => s.Enabled);
            return all.Count.ToString("N0") + L.T(" 个脚本 · 已启用 ") + enabled;
        }

        protected override void FillBody()
        {
            RenderedRowCount = 0;
            Body.Children.Add(BuildSiteBar());
            Body.Children.Add(Hint(L.T("脚本改动只在下次导航或刷新后生效 —— 已打开的页面不会热更新")));

            var items = ScriptStore.All()
                .Where(s => Matches(Query, s.Name, s.Namespace) ||
                            Matches(Query, s.MatchLabel, ""))
                .ToList();

            if (items.Count == 0)
            {
                Body.Children.Add(EmptyHint(Query.Length == 0
                    ? L.T("还没有用户脚本 —— 用右上角「导入文件」，或从剪贴板 / URL 导入 .user.js")
                    : L.T("没有匹配的脚本")));
                return;
            }

            foreach (var script in items)
            {
                Body.Children.Add(MakeScriptRow(script));
                RenderedRowCount++;
            }
        }

        // ---------- 当前站点 ----------

        private string CurrentSite()
        {
            var raw = _currentSite == null ? null : _currentSite();
            if (string.IsNullOrWhiteSpace(raw)) return "";
            raw = raw.Trim();
            // 允许调用方直接给站点键；如果给的是 URL 就顺手归一化。
            return raw.IndexOf("://", StringComparison.Ordinal) >= 0 ? ZoomStore.SiteOf(raw) : raw;
        }

        private FrameworkElement BuildSiteBar()
        {
            var site = CurrentSite();
            var disabled = !string.IsNullOrEmpty(site) && ScriptStore.IsSiteDisabledKey(site);

            var border = new Border
            {
                MinHeight = 40,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 0, 8, 0),
                Margin = new Thickness(0, 0, 0, 6)
            };
            border.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new TextBlock
            {
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            if (string.IsNullOrEmpty(site))
            {
                text.Text = L.T("当前站点：— · 从网页打开本页后，可以按站点临时禁用脚本");
                text.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            }
            else if (disabled)
            {
                text.Text = L.T("当前站点：") + ZoomStore.SiteLabel(site) + L.T(" · 已临时禁用全部脚本（仅本次运行，重启后恢复）");
                text.SetResourceReference(TextBlock.ForegroundProperty, "WarnBrush");
            }
            else
            {
                text.Text = L.T("当前站点：") + ZoomStore.SiteLabel(site) + L.T(" · 脚本会在此站点运行");
                text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            }
            grid.Children.Add(text);

            if (!string.IsNullOrEmpty(site))
            {
                var button = ActionButton(disabled ? L.T("恢复此站点脚本") : L.T("临时禁用全部脚本"),
                    ToggleSite, disabled);
                button.Margin = new Thickness(8, 0, 4, 0);
                button.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(button, 1);
                grid.Children.Add(button);
            }

            border.Child = grid;
            return border;
        }

        private void ToggleSite()
        {
            var site = CurrentSite();
            if (string.IsNullOrEmpty(site)) return;
            ScriptStore.ToggleSiteKey(site);
            Refresh();
            Notify(ScriptStore.IsSiteDisabledKey(site)
                ? L.T("已为 ") + ZoomStore.SiteLabel(site) + L.T(" 临时禁用全部脚本（刷新页面后生效，重启自动恢复）")
                : L.T("已恢复 ") + ZoomStore.SiteLabel(site) + L.T(" 的脚本（刷新页面后生效）"));
        }

        // ---------- 列表 ----------

        private FrameworkElement MakeScriptRow(UserScript script)
        {
            var row = new Border
            {
                MinHeight = 52,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 1, 0, 1),
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush(Colors.Transparent)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });

            grid.Children.Add(MakeToggle(script.Enabled, on => SetEnabled(script, on)));

            var text = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 10, 0),
                Opacity = script.Enabled ? 1.0 : 0.55
            };
            var name = new TextBlock
            {
                Text = script.Name + (string.IsNullOrEmpty(script.Version) ? "" : "  v" + script.Version),
                FontSize = 12.5,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            text.Children.Add(name);

            var sub = new TextBlock
            {
                Text = script.Namespace + " · " + script.MatchLabel,
                FontSize = 9.5,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            text.Children.Add(sub);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            var grants = new TextBlock
            {
                Text = script.Grants.Count == 0 ? L.T("无授权")
                    : script.Grants.Count == 1 ? script.Grants[0]
                    : string.Format(L.T("{0} 等 {1} 项授权"), script.Grants[0], script.Grants.Count),
                FontSize = 9.5,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 8, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = ScriptParser.DescribeGrants(script)
            };
            grants.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            Grid.SetColumn(grants, 2);
            grid.Children.Add(grants);

            var last = new TextBlock
            {
                Text = LastRunLabel(script.LastRunUtc),
                FontSize = 9.5,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 6, 0)
            };
            last.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            Grid.SetColumn(last, 3);
            grid.Children.Add(last);

            var remove = SmallButton("✕", null);
            remove.ToolTip = L.T("删除脚本");
            remove.Click += (s, e) =>
            {
                e.Handled = true;
                ConfirmTwice(remove, "✕", () => RemoveScript(script));
            };
            Grid.SetColumn(remove, 4);
            grid.Children.Add(remove);

            row.Child = grid;
            row.MouseEnter += (s, e) => row.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
            row.MouseLeave += (s, e) => row.Background = new SolidColorBrush(Colors.Transparent);
            row.MouseLeftButtonUp += (s, e) => BeginEdit(script);
            return row;
        }

        private static string LastRunLabel(DateTime utc)
        {
            if (utc == DateTime.MinValue) return L.T("从未运行");
            var local = utc.ToLocalTime();
            return local.Date == DateTime.Today
                ? L.T("今天 ") + local.ToString("HH:mm")
                : local.ToString(L.T("M月d日 HH:mm"));
        }

        private FrameworkElement MakeToggle(bool on, Action<bool> changed)
        {
            var track = new Border
            {
                Width = 26,
                Height = 14,
                CornerRadius = new CornerRadius(7),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                ToolTip = on ? L.T("已启用 · 点击停用") : L.T("已停用 · 点击启用")
            };
            track.SetResourceReference(Border.BackgroundProperty, on ? "AccentBrush" : "StateDiscardedBrush");

            var knob = new Border
            {
                Width = 10,
                Height = 10,
                CornerRadius = new CornerRadius(5),
                Background = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Margin = new Thickness(2, 0, 2, 0)
            };
            track.Child = knob;

            var state = on;
            track.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;   // 别让行点击顺带打开编辑器
                state = !state;
                changed(state);
            };
            return track;
        }

        private void SetEnabled(UserScript script, bool enabled)
        {
            if (script.Enabled == enabled) return;
            ScriptStore.SetEnabled(script.Id, enabled);
            Refresh();
            Notify((enabled ? L.T("已启用 ") : L.T("已停用 ")) + script.Name + L.T("（刷新页面后生效）"));
        }

        internal void RemoveScript(UserScript script)
        {
            if (script == null) return;
            var name = script.Name;
            ScriptStore.Remove(script.Id);
            if (ReferenceEquals(_editing, script)) CloseOverlay();
            Refresh();
            Notify(L.T("已删除 ") + name);
        }

        // ---------- 编辑器 ----------

        internal void BeginEdit(UserScript script)
        {
            EnsureOverlay();
            _editing = script;
            var isNew = script == null;
            _code = new TextBox
            {
                Text = isNew ? NewScriptTemplate() : (script.Code ?? ""),
                AcceptsReturn = true,
                AcceptsTab = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                FontFamily = MonoFont,
                FontSize = 12,
                Padding = new Thickness(8, 6, 8, 6),
                VerticalContentAlignment = VerticalAlignment.Top
            };
            _code.SetResourceReference(TextBox.ForegroundProperty, "TextBrush");
            _code.SetResourceReference(TextBox.CaretBrushProperty, "AccentBrush");
            _code.TextChanged += (s, e) => UpdateLineNumbers();
            _code.Loaded += (s, e) => HookScrollSync();

            _lineCount = -1;
            _numbers = new TextBox
            {
                IsReadOnly = true,
                IsTabStop = false,
                Focusable = false,
                IsHitTestVisible = false,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                FontFamily = MonoFont,
                FontSize = 12,
                Padding = new Thickness(6, 6, 4, 6),
                TextAlignment = TextAlignment.Right,
                VerticalContentAlignment = VerticalAlignment.Top
            };
            _numbers.SetResourceReference(TextBox.ForegroundProperty, "MutedBrush");

            var card = BuildEditorCard(isNew);
            ShowOverlayCard(card, true);
            UpdateLineNumbers();
            try { _code.CaretIndex = 0; _code.ScrollToHome(); } catch { }
            _code.Focus();
        }

        private FrameworkElement BuildEditorCard(bool isNew)
        {
            var card = new Grid();
            card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            card.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new TextBlock
            {
                Text = isNew ? L.T("新建用户脚本") : L.T("编辑脚本 · ") + _editing.Name,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            header.Children.Add(title);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            if (!isNew)
            {
                var remove = ActionButton(L.T("删除"), null, false);
                remove.Click += (s, e) => ConfirmTwice(remove, L.T("删除"), () => RemoveScript(_editing));
                remove.Margin = new Thickness(0, 0, 8, 0);
                actions.Children.Add(remove);
            }
            actions.Children.Add(ActionButton(L.T("取消"), CloseOverlay, false));
            actions.Children.Add(ActionButton(L.T("保存"), SaveEditor, true));
            Grid.SetColumn(actions, 1);
            header.Children.Add(actions);
            Grid.SetRow(header, 0);
            card.Children.Add(header);

            var meta = new TextBlock
            {
                Text = isNew
                    ? L.T("元数据从脚本头部的 // @name / @match / @grant 解析，保存后立即出现在列表里。")
                    : MetaLine(_editing),
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 10)
            };
            meta.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            Grid.SetRow(meta, 1);
            card.Children.Add(meta);

            var editorBorder = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 0, 8)
            };
            editorBorder.SetResourceReference(Border.BackgroundProperty, "InputBrush");
            editorBorder.SetResourceReference(Border.BorderBrushProperty, "InputBorderBrush");

            var editorGrid = new Grid();
            editorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            editorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var numberBorder = new Border { BorderThickness = new Thickness(0, 0, 1, 0), Child = _numbers };
            numberBorder.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            editorGrid.Children.Add(numberBorder);

            Grid.SetColumn(_code, 1);
            editorGrid.Children.Add(_code);
            editorBorder.Child = editorGrid;
            Grid.SetRow(editorBorder, 2);
            card.Children.Add(editorBorder);

            var footer = new TextBlock
            {
                Text = L.T("等宽字体 · 不做语法高亮 · 保存后刷新页面才会用新代码运行"),
                FontSize = 9.5
            };
            footer.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            Grid.SetRow(footer, 3);
            card.Children.Add(footer);

            return card;
        }

        private static string MetaLine(UserScript script)
        {
            var matches = script.Matches.Concat(script.Includes).ToList();
            var matchText = matches.Count == 0 ? L.T("所有站点") : string.Join("  ", matches.Take(3)) +
                            (matches.Count > 3 ? L.T(" 等 ") + matches.Count + L.T(" 条") : "");
            return script.Namespace + " · v" + script.Version +
                   L.T("\n匹配：") + matchText +
                   L.T("\n授权：") + (script.Grants.Count == 0 ? L.T("无（仅沙箱）") : string.Join("、", script.Grants)) +
                   (script.Excludes.Count > 0 ? L.T("\n排除：") + string.Join("  ", script.Excludes) : "") +
                   (script.Requires.Count > 0
                       ? L.T("\n依赖：") + script.Requires.Count + L.T(" 个（已固化本地）")
                       : "");
        }

        private void UpdateLineNumbers()
        {
            if (_code == null || _numbers == null) return;
            var text = _code.Text ?? "";
            var count = 1;
            for (var i = 0; i < text.Length; i++)
                if (text[i] == '\n') count++;
            if (count == _lineCount) return;
            _lineCount = count;

            var builder = new StringBuilder(count * 4);
            for (var i = 1; i <= count; i++)
            {
                if (i > 1) builder.Append('\n');
                builder.Append(i);
            }
            _numbers.Text = builder.ToString();
        }

        private void HookScrollSync()
        {
            var viewer = FindScrollViewer(_code);
            if (viewer == null) return;
            viewer.ScrollChanged += (s, e) =>
            {
                try { _numbers.ScrollToVerticalOffset(e.VerticalOffset); }
                catch { }
            };
        }

        private static ScrollViewer FindScrollViewer(DependencyObject root)
        {
            if (root == null) return null;
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var viewer = child as ScrollViewer ?? FindScrollViewer(child);
                if (viewer != null) return viewer;
            }
            return null;
        }

        private void SaveEditor()
        {
            var text = _code == null ? "" : (_code.Text ?? "");
            if (string.IsNullOrWhiteSpace(text))
            {
                Notify(L.T("脚本内容为空"));
                return;
            }

            // 没有 @require 的常规保存走同步路径；有依赖才去下载。
            if (ScriptParser.Parse(text).Requires.Count > 0)
            {
                var unused = SaveEditorAsync(text);
                return;
            }
            CommitEditor(text, null);
        }

        internal async Task<bool> SaveEditorAsync(string text)
        {
            var editing = _editing;
            if (editing == null || string.IsNullOrWhiteSpace(text)) return false;

            var parsed = ScriptParser.Parse(text);
            List<string> requireCode = null;
            if (parsed.Requires.Count > 0)
            {
                Notify(L.T("正在下载 ") + parsed.Requires.Count + L.T(" 个依赖…"));
                var result = await ScriptRequires.DownloadAsync(parsed.Requires);
                if (!result.Ok)
                {
                    Notify(L.T("依赖下载失败，脚本未保存：") + string.Join("；", result.Failed));
                    return false;
                }
                requireCode = result.Codes;
            }

            // 下载期间用户可能关掉浮层或换了编辑对象，再写就会串。
            if (!ReferenceEquals(_editing, editing) || _code == null) return false;

            editing.Code = text;
            ScriptStore.Update(editing, requireCode);
            var name = editing.Name;
            CloseOverlay();
            Refresh();
            Notify(L.T("已保存 ") + name + L.T("（刷新页面后生效）"));
            return true;
        }

        private void CommitEditor(string text, List<string> requireCode)
        {
            if (_editing == null)
            {
                var added = ScriptStore.Add(text, true, requireCode);
                CloseOverlay();
                Refresh();
                Notify(L.T("已导入 ") + added.Name + L.T("（刷新页面后生效）"));
                return;
            }

            _editing.Code = text;
            ScriptStore.Update(_editing, requireCode);
            var name = _editing.Name;
            CloseOverlay();
            Refresh();
            Notify(L.T("已保存 ") + name + L.T("（刷新页面后生效）"));
        }

        private static string NewScriptTemplate()
        {
            return "// ==UserScript==\n" +
                   L.T("// @name         新建脚本\n") +
                   "// @namespace    local\n" +
                   "// @version      1.0\n" +
                   "// @match        https://example.com/*\n" +
                   "// @run-at       document-end\n" +
                   "// ==/UserScript==\n" +
                   "\n" +
                   L.T("// 在这里写你的脚本\n");
        }

        // ---------- 导入 ----------

        private void ImportFile()
        {
            var dialog = new OpenFileDialog
            {
                Filter = L.T("用户脚本 (*.user.js;*.js)|*.user.js;*.js|所有文件|*.*"),
                Title = L.T("导入用户脚本")
            };
            if (dialog.ShowDialog() != true) return;
            try { StageImport(File.ReadAllText(dialog.FileName, Encoding.UTF8), Path.GetFileName(dialog.FileName)); }
            catch (Exception ex) { Notify(L.T("读取失败：") + ex.Message); }
        }

        private void ImportClipboard()
        {
            string text = null;
            try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); }
            catch { }
            if (string.IsNullOrWhiteSpace(text))
            {
                Notify(L.T("剪贴板里没有文本"));
                return;
            }
            StageImport(text, L.T("剪贴板"));
        }

        private void ShowUrlImport()
        {
            EnsureOverlay();
            _urlBox = new TextBox
            {
                Height = 32,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                FontSize = 12,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(10, 0, 10, 0)
            };
            _urlBox.SetResourceReference(TextBox.ForegroundProperty, "TextBrush");
            _urlBox.SetResourceReference(TextBox.CaretBrushProperty, "AccentBrush");
            _urlBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; DownloadFromUrl(); }
            };

            var inputBorder = new Border
            {
                Height = 32,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 12),
                Child = _urlBox
            };
            inputBorder.SetResourceReference(Border.BackgroundProperty, "InputBrush");
            inputBorder.SetResourceReference(Border.BorderBrushProperty, "InputBorderBrush");

            var panel = new StackPanel();
            panel.Children.Add(OverlayTitle(L.T("从 URL 导入脚本")));
            var hint = new TextBlock
            {
                Text = L.T("粘贴 .user.js 的原始地址（例如 raw.githubusercontent.com 上的文件）。\n") +
                       L.T("下载后仍会先展示 @grant 清单，确认后才写入。"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 12)
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            panel.Children.Add(hint);
            panel.Children.Add(inputBorder);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            buttons.Children.Add(ActionButton(L.T("取消"), CloseOverlay, false));
            buttons.Children.Add(ActionButton(L.T("下载"), DownloadFromUrl, true));
            panel.Children.Add(buttons);

            ShowOverlayCard(panel, false);
            _urlBox.Focus();
        }

        private async void DownloadFromUrl()
        {
            var url = _urlBox == null ? "" : (_urlBox.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                Notify(L.T("请输入 http(s) 地址"));
                return;
            }

            try
            {
                using (var client = new WebClient())
                {
                    client.Encoding = Encoding.UTF8;
                    client.Headers[HttpRequestHeader.UserAgent] = "NanoEdge/0.1";
                    var task = client.DownloadStringTaskAsync(new Uri(url));
                    if (await Task.WhenAny(task, Task.Delay(15000)) != task)
                    {
                        Notify(L.T("下载超时（15s）"));
                        return;
                    }
                    StageImport(await task, url);
                }
            }
            catch (Exception ex)
            {
                Notify(L.T("下载失败：") + ex.Message);
            }
        }

        // 导入前先摆出权限清单，用户点「导入并启用」才真正入库（SPEC 7.3 权限模型）。
        internal void StageImport(string code, string source)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                Notify(L.T("内容为空"));
                return;
            }
            var parsed = ScriptParser.Parse(code);
            _pendingImport = code;
            _pendingImportSource = source;
            LastImportSummary = parsed.Name + "\n" + ScriptParser.DescribeGrants(parsed);
            if (parsed.Requires.Count > 0)
                LastImportSummary += "\n" + L.T("依赖：") + parsed.Requires.Count + L.T(" 个（导入时下载）");

            EnsureOverlay();
            ShowOverlayCard(BuildImportCard(parsed, source), false);
        }

        private FrameworkElement BuildImportCard(UserScript parsed, string source)
        {
            var panel = new StackPanel();
            panel.Children.Add(OverlayTitle(L.T("确认导入用户脚本")));

            var subtitle = new TextBlock
            {
                Text = L.T("来源：") + source,
                FontSize = 10,
                Margin = new Thickness(0, 4, 0, 12),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            subtitle.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            panel.Children.Add(subtitle);

            panel.Children.Add(MetaRow(L.T("名称"), parsed.Name + "  v" + parsed.Version));
            panel.Children.Add(MetaRow(L.T("命名空间"), parsed.Namespace));

            var matches = parsed.Matches.Concat(parsed.Includes).ToList();
            panel.Children.Add(MetaRow(L.T("匹配站点"), matches.Count == 0
                ? L.T("所有站点（脚本未写 @match）")
                : string.Join("\n", matches)));
            if (parsed.Excludes.Count > 0)
                panel.Children.Add(MetaRow(L.T("排除"), string.Join("\n", parsed.Excludes)));
            if (parsed.Requires.Count > 0)
                panel.Children.Add(MetaRow(L.T("依赖"), string.Join("\n", parsed.Requires)));

            var grantTitle = new TextBlock
            {
                Text = L.T("它将获得以下权限"),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 12, 0, 4)
            };
            grantTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            panel.Children.Add(grantTitle);

            var grants = new TextBlock
            {
                Text = ScriptParser.DescribeGrants(parsed),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            grants.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            panel.Children.Add(grants);

            var warning = new TextBlock
            {
                Text = L.T("脚本可以读取和修改页面内容，也能借你当前的登录状态发请求。只导入你信任的脚本。") +
                       (parsed.Requires.Count > 0
                           ? "\n" + L.T("带 @require 的脚本会在导入时把依赖下载到本地，之后不再联网更新。")
                           : ""),
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };
            warning.SetResourceReference(TextBlock.ForegroundProperty, "WarnBrush");
            panel.Children.Add(warning);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            buttons.Children.Add(ActionButton(L.T("取消"), CloseOverlay, false));
            buttons.Children.Add(ActionButton(L.T("导入并启用"), ConfirmImport, true));
            panel.Children.Add(buttons);
            return panel;
        }

        internal void ConfirmImport()
        {
            if (_pendingImport == null) return;
            if (ScriptParser.Parse(_pendingImport).Requires.Count > 0)
            {
                var unused = ConfirmImportAsync();
                return;
            }
            CommitImport(null);
        }

        internal async Task<bool> ConfirmImportAsync()
        {
            if (_pendingImport == null) return false;
            var code = _pendingImport;
            var parsed = ScriptParser.Parse(code);
            List<string> requireCode = null;
            if (parsed.Requires.Count > 0)
            {
                Notify(L.T("正在下载 ") + parsed.Requires.Count + L.T(" 个依赖…"));
                var result = await ScriptRequires.DownloadAsync(parsed.Requires);
                if (!result.Ok)
                {
                    Notify(L.T("依赖下载失败，脚本未导入：") + string.Join("；", result.Failed));
                    return false;   // 保留待导入状态，用户可以再点一次重试
                }
                requireCode = result.Codes;
            }
            if (_pendingImport == null) return false;   // 下载期间浮层被关掉
            CommitImport(requireCode);
            return true;
        }

        private void CommitImport(List<string> requireCode)
        {
            var code = _pendingImport;
            if (code == null) return;
            _pendingImport = null;
            var added = ScriptStore.Add(code, true, requireCode);
            CloseOverlay();
            Refresh();
            Notify(L.T("已导入 ") + added.Name + L.T("（刷新页面后生效）"));
        }

        private FrameworkElement MetaRow(string label, string value)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var left = new TextBlock { Text = label, FontSize = 10.5 };
            left.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            grid.Children.Add(left);

            var right = new TextBlock
            {
                Text = value,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap
            };
            right.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
            return grid;
        }

        private TextBlock OverlayTitle(string text)
        {
            var title = new TextBlock { Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            return title;
        }

        // ---------- 浮层与通用件 ----------

        private void EnsureOverlay()
        {
            if (_overlay != null) return;
            var inner = Content as UIElement;
            Content = null;

            var wrapper = new Grid();
            if (inner != null) wrapper.Children.Add(inner);

            _overlay = new Border { Visibility = Visibility.Collapsed };
            _overlay.SetResourceReference(Border.BackgroundProperty, "CanvasBrush");

            _overlayCard = new Border
            {
                Margin = new Thickness(24),
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(18)
            };
            _overlayCard.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            _overlayCard.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            _overlay.Child = _overlayCard;

            wrapper.Children.Add(_overlay);
            Content = wrapper;
        }

        private void ShowOverlayCard(FrameworkElement content, bool fill)
        {
            // 编辑器要占满整页（代码框吃高度）；导入确认 / URL 输入是紧凑卡片，贴顶。
            _overlayCard.VerticalAlignment = fill ? VerticalAlignment.Stretch : VerticalAlignment.Top;
            _overlayCard.Child = content;
            _overlay.Visibility = Visibility.Visible;
        }

        private void CloseOverlay()
        {
            _pendingImport = null;
            _editing = null;
            _code = null;
            _numbers = null;
            _urlBox = null;
            _lineCount = -1;
            if (_overlayCard != null) _overlayCard.Child = null;
            if (_overlay != null) _overlay.Visibility = Visibility.Collapsed;
        }

        private Button ActionButton(string text, Action action, bool primary)
        {
            var button = new Button
            {
                Content = text,
                Height = 28,
                MinWidth = 62,
                FontSize = 11,
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(8, 0, 0, 0),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            button.SetResourceReference(Control.BackgroundProperty, primary ? "AccentSoftBrush" : "HoverBrush");
            button.SetResourceReference(Control.ForegroundProperty, primary ? "AccentBrush" : "TextBrush");
            if (action != null) button.Click += (s, e) => action();
            return button;
        }

        private static Button SmallButton(string text, Action action)
        {
            var button = new Button
            {
                Content = text,
                MinWidth = 22,
                Height = 22,
                Padding = new Thickness(4, 0, 4, 0),
                FontSize = 10,
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                Cursor = Cursors.Hand
            };
            button.SetResourceReference(Control.ForegroundProperty, "MutedBrush");
            if (action != null) button.Click += (s, e) => { e.Handled = true; action(); };
            return button;
        }

        // 两段式确认，和「清空历史 / 清除已结束」保持同一做法。
        private static void ConfirmTwice(Button button, string normal, Action action)
        {
            if (!Equals(button.Tag, "confirm"))
            {
                button.Tag = "confirm";
                button.Content = L.T("再点一次");
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    button.Tag = null;
                    button.Content = normal;
                };
                timer.Start();
                return;
            }
            button.Tag = null;
            button.Content = normal;
            action();
        }

        private void OpenFolder()
        {
            try
            {
                Directory.CreateDirectory(ScriptStore.Folder);
                Process.Start("explorer.exe", "\"" + ScriptStore.Folder + "\"");
            }
            catch (Exception ex) { Notify(L.T("打开脚本目录失败：") + ex.Message); }
        }

        private static FrameworkElement Hint(string text)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = 9.5,
                Margin = new Thickness(2, 2, 0, 10),
                TextWrapping = TextWrapping.Wrap
            };
            block.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            return block;
        }

        private void Notify(string message)
        {
            if (_notify != null) _notify(message);
        }
    }
}
