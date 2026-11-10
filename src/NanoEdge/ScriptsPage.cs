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
    public sealed partial class ScriptsPage : InternalPage
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

        // C5：编辑器里的四个结构化元数据字段。初始值用于判断「用户有没有改过」，
        // 没改过就完全不回写，用户直接手改源码里 // @ 行的老用法继续有效。
        private TextBox _metaName;
        private TextBox _metaVersion;
        private TextBox _metaMatches;
        private TextBox _metaExcludes;
        private string _metaInitName = "";
        private string _metaInitVersion = "";
        private string _metaInitMatches = "";
        private string _metaInitExcludes = "";

        // 导入确认卡的同一套四个字段。预填值单独记，用户没改就完全不回写。
        private TextBox _importName;
        private TextBox _importVersion;
        private TextBox _importMatches;
        private TextBox _importExcludes;
        private string _importInitName = "";
        private string _importInitVersion = "";
        private string _importInitMatches = "";
        private string _importInitExcludes = "";

        private string _pendingImport;
        private string _pendingImportSource;

        // 整体备份 / 恢复。DDL 附件只支持 pg4-assist 这一种。
        private ScriptBundle _pendingBundle;
        private string _pendingBundleSource = "";
        private TextBox _ddlTarget;
        private TextBlock _ddlFiles;
        private readonly List<ScriptBundleAttachment> _ddlAttachments =
            new List<ScriptBundleAttachment>();
        private bool _clearFirst;

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
            AddAction(L.T("整体备份"), ShowBackupCard);
            AddAction(L.T("恢复备份"), PickRestoreFile);
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
                ConfirmTwice(remove, "✕", () => RemoveScript(script), SetRowDeleteState);
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

            // 字段从「当前源码」解析，而不是从索引对象读：源码框预填的就是它，
            // 两者一致；编辑器里手改源码后再保存，未被改动的字段也不会覆盖回去。
            var parsed = ScriptParser.Parse(_code.Text);
            _metaInitName = parsed.Name;
            _metaInitVersion = parsed.Version;
            _metaInitMatches = string.Join("\n", parsed.Matches);
            _metaInitExcludes = string.Join("\n", parsed.Excludes);
            _metaName = MakeMetaBox(_metaInitName, 28, false);
            _metaVersion = MakeMetaBox(_metaInitVersion, 28, false);
            _metaMatches = MakeMetaBox(_metaInitMatches, 48, true);
            _metaExcludes = MakeMetaBox(_metaInitExcludes, 48, true);

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

            InitPgAdminSection(script, isNew);
            var card = BuildEditorCard(isNew);
            ShowOverlayCard(card, true);
            UpdateLineNumbers();
            try { _code.CaretIndex = 0; _code.ScrollToHome(); } catch { }
            _code.Focus();
        }

        private FrameworkElement BuildEditorCard(bool isNew)
        {
            var card = new Grid();
            card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 0 标题 / 按钮
            card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 1 可编辑元数据
            card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 2 只读摘要
            card.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 3 源码
            card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 4 页脚

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

            // 元数据 + pgAdmin 重登区块同占第 1 行，不改后面几行的编号，
            // 代码框仍然吃 Star 高度。
            var upper = new StackPanel();
            upper.Children.Add(BuildMetadataPanel());
            upper.Children.Add(BuildPgAdminBlock(isNew));
            Grid.SetRow(upper, 1);
            card.Children.Add(upper);

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
            Grid.SetRow(meta, 2);
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
            Grid.SetRow(editorBorder, 3);
            card.Children.Add(editorBorder);

            var footer = new TextBlock
            {
                Text = L.T("等宽字体 · 不做语法高亮 · 保存后刷新页面才会用新代码运行"),
                FontSize = 9.5
            };
            footer.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            Grid.SetRow(footer, 4);
            card.Children.Add(footer);

            return card;
        }

        // C5：@name / @version / @match / @exclude 四个结构化字段。
        // 匹配框只收 @match，@include（可能是 /regex/ 字面量）属于「其它元数据」原样保留。
        private FrameworkElement BuildMetadataPanel()
        {
            return BuildMetadataGrid(_metaName, _metaVersion, _metaMatches, _metaExcludes,
                L.T("匹配和排除一行一条；改动会写回 // @ 头，其它元数据保持原样。直接改源码里的 // @ 行同样有效。"));
        }

        // 编辑器与导入确认卡共用同一套字段布局，避免两处各写一份。
        private static FrameworkElement BuildMetadataGrid(TextBox name, TextBox version,
            TextBox matches, TextBox excludes, string hintText)
        {
            var panel = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
            for (var i = 0; i < 5; i++)
                panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var title = new TextBlock
            {
                Text = L.T("元数据"),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            Grid.SetRow(title, 0);
            Grid.SetColumnSpan(title, 4);
            panel.Children.Add(title);

            var nameLabel = MetaLabel(L.T("名称"), false);
            Grid.SetRow(nameLabel, 1);
            panel.Children.Add(nameLabel);
            Grid.SetRow(name, 1);
            Grid.SetColumn(name, 1);
            panel.Children.Add(name);

            var versionLabel = MetaLabel(L.T("版本"), false);
            versionLabel.Margin = new Thickness(8, 4, 4, 0);
            Grid.SetRow(versionLabel, 1);
            Grid.SetColumn(versionLabel, 2);
            panel.Children.Add(versionLabel);
            Grid.SetRow(version, 1);
            Grid.SetColumn(version, 3);
            version.Margin = new Thickness(0, 4, 0, 0);
            panel.Children.Add(version);

            var matchLabel = MetaLabel(L.T("匹配站点"), true);
            Grid.SetRow(matchLabel, 2);
            panel.Children.Add(matchLabel);
            var matchesHost = WithPlaceholder(matches,
                L.T("例如：https://example.com/*（一行一条）"));
            Grid.SetRow(matchesHost, 2);
            Grid.SetColumn(matchesHost, 1);
            Grid.SetColumnSpan(matchesHost, 3);
            matchesHost.Margin = new Thickness(0, 4, 0, 0);
            panel.Children.Add(matchesHost);

            var excludeLabel = MetaLabel(L.T("排除"), true);
            Grid.SetRow(excludeLabel, 3);
            panel.Children.Add(excludeLabel);
            var excludesHost = WithPlaceholder(excludes,
                L.T("例如：https://example.com/admin/*（一行一条）"));
            Grid.SetRow(excludesHost, 3);
            Grid.SetColumn(excludesHost, 1);
            Grid.SetColumnSpan(excludesHost, 3);
            excludesHost.Margin = new Thickness(0, 4, 0, 0);
            panel.Children.Add(excludesHost);

            var hint = new TextBlock
            {
                Text = hintText,
                FontSize = 9.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 5, 0, 0)
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            Grid.SetRow(hint, 4);
            Grid.SetColumnSpan(hint, 4);
            panel.Children.Add(hint);
            return panel;
        }

        private static TextBlock MetaLabel(string text, bool top)
        {
            var label = new TextBlock
            {
                Text = text,
                FontSize = 10.5,
                VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Center,
                Margin = top ? new Thickness(0, 9, 8, 0) : new Thickness(0, 4, 8, 0)
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            return label;
        }

        private static TextBox MakeMetaBox(string text, double height, bool multiLine)
        {
            var box = new TextBox
            {
                Text = text,
                Height = height,
                FontSize = 11,
                AcceptsReturn = multiLine,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = multiLine ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                BorderThickness = new Thickness(1),
                Padding = multiLine ? new Thickness(8, 5, 8, 5) : new Thickness(8, 0, 8, 0),
                VerticalContentAlignment = multiLine ? VerticalAlignment.Top : VerticalAlignment.Center
            };
            box.SetResourceReference(Control.BackgroundProperty, "InputBrush");
            box.SetResourceReference(Control.BorderBrushProperty, "InputBorderBrush");
            box.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            box.SetResourceReference(TextBox.CaretBrushProperty, "AccentBrush");
            return box;
        }

        // WPF 没有原生 placeholder：在 TextBox 上叠一个不吃点击的 TextBlock，
        // 只在内容为空且未聚焦时显示。不动 TextBox.Text，字段读取/回写零影响。
        private static FrameworkElement WithPlaceholder(TextBox box, string hint)
        {
            var host = new Grid();
            var hintBlock = new TextBlock
            {
                Text = hint,
                Tag = "meta-placeholder",
                FontSize = 10,
                Margin = new Thickness(10, 6, 8, 0),
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            hintBlock.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            host.Children.Add(box);
            host.Children.Add(hintBlock);

            Action update = () => hintBlock.Visibility =
                string.IsNullOrEmpty(box.Text) && !box.IsFocused
                    ? Visibility.Visible : Visibility.Collapsed;
            box.TextChanged += (s, e) => update();
            box.GotFocus += (s, e) => update();
            box.LostFocus += (s, e) => update();
            box.Loaded += (s, e) => update();
            update();
            return host;
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

            // 结构化字段只回写「相对打开时改动过的」那几个；没动过就完全不碰源码，
            // 用户直接手改 // @ 行的老用法继续有效。
            var edit = new ScriptMetadataEdit();
            if (_metaName != null && _metaName.Text != _metaInitName) edit.Name = _metaName.Text;
            if (_metaVersion != null && _metaVersion.Text != _metaInitVersion) edit.Version = _metaVersion.Text;
            if (_metaMatches != null && _metaMatches.Text != _metaInitMatches)
                edit.Matches = SplitMetaLines(_metaMatches.Text);
            if (_metaExcludes != null && _metaExcludes.Text != _metaInitExcludes)
                edit.Excludes = SplitMetaLines(_metaExcludes.Text);

            if (edit.Name != null && string.IsNullOrWhiteSpace(edit.Name))
            {
                Notify(L.T("名称不能为空，脚本未保存"));
                return;
            }
            if (edit.Version != null && string.IsNullOrWhiteSpace(edit.Version))
            {
                Notify(L.T("版本不能为空，脚本未保存"));
                return;
            }

            string error;
            var rewritten = ScriptParser.RewriteMetadata(text, edit, out error);
            if (rewritten == null)
            {
                Notify(L.T("元数据写回失败，脚本未保存：") + error);
                return;
            }
            // 让源码框和字段显示一致，用户能直接看到 // @ 头被改了哪几行。
            if (_code != null && !string.Equals(rewritten, text, StringComparison.Ordinal))
                _code.Text = rewritten;

            var parsed = ScriptParser.Parse(rewritten);
            // 依赖列表没变就复用已固化的本地正文：只改个名字不该触发联网，断网也照常保存。
            var oldCount = _editing == null || _editing.RequireCode == null ? 0 : _editing.RequireCode.Count;
            var requiresUnchanged = _editing != null &&
                _editing.Requires.SequenceEqual(parsed.Requires) &&
                oldCount == parsed.Requires.Count;
            if (parsed.Requires.Count > 0 && !requiresUnchanged)
            {
                var unused = SaveEditorAsync(rewritten);
                return;
            }
            CommitEditor(rewritten, null);
        }

        private static List<string> SplitMetaLines(string text)
        {
            return (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        }

        // 自测入口：不模拟鼠标点击，直接把值写进真实字段再走真实保存路径。
        internal void SetMetadataForTest(string name, string version, string matches, string excludes)
        {
            if (_metaName != null && name != null) _metaName.Text = name;
            if (_metaVersion != null && version != null) _metaVersion.Text = version;
            if (_metaMatches != null && matches != null) _metaMatches.Text = matches;
            if (_metaExcludes != null && excludes != null) _metaExcludes.Text = excludes;
        }

        internal void SetCodeForTest(string code) { if (_code != null) _code.Text = code; }
        internal void SaveEditorForTest() { SaveEditor(); }
        internal string EditorCodeForTest { get { return _code == null ? null : _code.Text; } }
        internal string MetaNameForTest { get { return _metaName == null ? null : _metaName.Text; } }

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

        // ---------- 整体备份 / 恢复 ----------

        private void ShowBackupCard()
        {
            EnsureOverlay();
            _ddlAttachments.Clear();
            _ddlTarget = MakeMetaBox(DdlRestore.LastTarget(), 28, false);
            _ddlTarget.Margin = new Thickness(0, 4, 0, 4);

            var panel = new StackPanel();
            panel.Children.Add(OverlayTitle(L.T("整体备份")));
            var hint = new TextBlock
            {
                Text = string.Format(L.T("将打包 {0} 个脚本、启用状态和已固化的依赖。"),
                          ScriptStore.All().Count) + "\n" +
                       L.T("如需同事免手动导入 DDL，可附带 DDL 文件并填写目标站点。"),
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 12)
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            panel.Children.Add(hint);

            var targetLabel = new TextBlock { Text = L.T("DDL 目标站点"), FontSize = 10.5 };
            targetLabel.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            panel.Children.Add(targetLabel);
            _ddlTarget.ToolTip = L.T("例如：https://pgadmin.example.com/*");
            panel.Children.Add(_ddlTarget);
            var example = new TextBlock
            {
                Text = L.T("例如：https://pgadmin.example.com/*"),
                FontSize = 9.5,
                Margin = new Thickness(0, 0, 0, 10)
            };
            example.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            panel.Children.Add(example);

            var pick = ActionButton(L.T("选择 DDL 文件（可多选）"), PickDdlFiles, false);
            pick.HorizontalAlignment = HorizontalAlignment.Left;
            pick.Margin = new Thickness(0, 0, 0, 6);
            panel.Children.Add(pick);
            _ddlFiles = new TextBlock
            {
                Text = L.T("未附带 DDL 文件"),
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };
            _ddlFiles.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            panel.Children.Add(_ddlFiles);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            buttons.Children.Add(ActionButton(L.T("取消"), CloseOverlay, false));
            buttons.Children.Add(ActionButton(L.T("保存备份文件"), SaveBackup, true));
            panel.Children.Add(buttons);
            ShowOverlayCard(panel, false);
        }

        private void PickDdlFiles()
        {
            var dialog = new OpenFileDialog
            {
                Filter = L.T("DDL 文件 (*.sql;*.ddl;*.txt)|*.sql;*.ddl;*.txt|所有文件|*.*"),
                Title = L.T("选择 DDL 快照文件"),
                Multiselect = true
            };
            if (dialog.ShowDialog() != true) return;
            var picked = new List<ScriptBundleAttachment>();
            foreach (var path in dialog.FileNames)
            {
                try
                {
                    picked.Add(new ScriptBundleAttachment
                    {
                        Kind = ScriptBundleFile.DdlKind,
                        Name = Path.GetFileName(path),
                        Text = File.ReadAllText(path, Encoding.UTF8)
                    });
                }
                catch (Exception ex) { Notify(L.T("DDL 文件读取失败：") + ex.Message); return; }
            }
            _ddlAttachments.Clear();
            _ddlAttachments.AddRange(picked);
            if (_ddlFiles != null)
                _ddlFiles.Text = _ddlAttachments.Count == 0
                    ? L.T("未附带 DDL 文件")
                    : string.Format(L.T("已选 {0} 个 DDL 文件："), _ddlAttachments.Count) +
                      string.Join("、", _ddlAttachments.Select(a => a.Name));
        }

        private void SaveBackup()
        {
            var target = (_ddlTarget == null ? "" : _ddlTarget.Text).Trim();
            foreach (var att in _ddlAttachments) att.Target = target;
            if (_ddlAttachments.Count > 0 && string.IsNullOrWhiteSpace(target))
                Notify(L.T("未填写 DDL 目标站点，快照不会自动重建"));
            DdlRestore.SaveLastTarget(target);

            var bundle = ScriptBundleFile.FromStore(_ddlAttachments);
            var dialog = new SaveFileDialog
            {
                Filter = L.T("NanoEdge 脚本备份 (*.json)|*.json|所有文件|*.*"),
                Title = L.T("保存备份文件"),
                FileName = "nanoedge-scripts-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".json"
            };
            if (dialog.ShowDialog() != true) return;
            try
            {
                File.WriteAllText(dialog.FileName, ScriptBundleFile.ToJson(bundle),
                    new UTF8Encoding(false));
            }
            catch (Exception ex) { Notify(L.T("备份失败：") + ex.Message); return; }
            CloseOverlay();
            Notify(string.Format(L.T("已备份 {0} 个脚本到 "), bundle.Scripts.Count) +
                   dialog.FileName);
        }

        private void PickRestoreFile()
        {
            var dialog = new OpenFileDialog
            {
                Filter = L.T("NanoEdge 脚本备份 (*.json)|*.json|所有文件|*.*"),
                Title = L.T("恢复用户脚本备份")
            };
            if (dialog.ShowDialog() != true) return;
            string json;
            try { json = File.ReadAllText(dialog.FileName, Encoding.UTF8); }
            catch (Exception ex) { Notify(L.T("读取失败：") + ex.Message); return; }
            ScriptBundle bundle;
            string error;
            if (!ScriptBundleFile.TryParse(json, out bundle, out error)) { Notify(error); return; }
            StageBundle(bundle, Path.GetFileName(dialog.FileName));
        }

        private void StageBundle(ScriptBundle bundle, string source)
        {
            _pendingBundle = bundle;
            _pendingBundleSource = source;
            _clearFirst = false;
            EnsureOverlay();
            ShowOverlayCard(BuildRestoreCard(), false);
        }

        private FrameworkElement BuildRestoreCard()
        {
            var bundle = _pendingBundle;
            var plan = ScriptBundleFile.Plan(bundle, false);
            var panel = new StackPanel();
            panel.Children.Add(OverlayTitle(L.T("恢复用户脚本备份")));
            var subtitle = new TextBlock
            {
                Text = L.T("来源：") + _pendingBundleSource + " · " + bundle.CreatedUtc,
                FontSize = 10,
                Margin = new Thickness(0, 4, 0, 10),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            subtitle.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            panel.Children.Add(subtitle);

            panel.Children.Add(MetaRow(L.T("脚本"),
                string.Format(L.T("共 {0} 个 · 新增 {1} 个 · 覆盖 {2} 个"),
                    bundle.Scripts.Count, plan.Added, plan.Updated)));
            var names = string.Join("、", plan.Names.Take(8));
            if (plan.Names.Count > 8) names += L.T(" 等 ");
            panel.Children.Add(MetaRow(L.T("名称"), names));
            var grants = bundle.Scripts
                .SelectMany(e => ScriptParser.Parse(e.Code).Grants).Distinct().ToList();
            panel.Children.Add(MetaRow(L.T("授权"),
                grants.Count == 0 ? L.T("无（仅沙箱）") : string.Join("、", grants)));

            var ddl = bundle.Attachments.FirstOrDefault(a => a != null &&
                a.Kind == ScriptBundleFile.DdlKind && !string.IsNullOrWhiteSpace(a.Text));
            if (ddl != null)
            {
                panel.Children.Add(MetaRow(L.T("DDL 快照"),
                    ddl.Name + " · " + ddl.Text.Length + " B"));
                panel.Children.Add(MetaRow(L.T("DDL 目标站点"),
                    string.IsNullOrWhiteSpace(ddl.Target)
                        ? L.T("未填写（不会自动重建）") : ddl.Target));
                var ddlHint = new TextBlock
                {
                    Text = L.T("DDL 快照会在下次打开或刷新目标站点后自动重建。"),
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 10)
                };
                ddlHint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
                panel.Children.Add(ddlHint);
            }

            var warning = new TextBlock
            {
                Text = L.T("脚本可以读取和修改页面内容，也能借你当前的登录状态发请求。只恢复你信任的备份。"),
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 10)
            };
            warning.SetResourceReference(TextBlock.ForegroundProperty, "WarnBrush");
            panel.Children.Add(warning);

            // 用页内主题化的开关，不用系统 CheckBox（暗色下会露白）。
            var option = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            option.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            option.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var optionText = new TextBlock
            {
                Text = L.T("先清空现有脚本再恢复"),
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            optionText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            option.Children.Add(optionText);
            var optionState = new TextBlock
            {
                Text = L.T("关"),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            optionState.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            var stateHost = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            stateHost.Children.Add(optionState);
            var toggle = MakeToggle(false, on =>
            {
                _clearFirst = on;
                optionState.Text = on ? L.T("开") : L.T("关");
            });
            toggle.Margin = new Thickness(0, 0, 4, 0);
            stateHost.Children.Add(toggle);
            Grid.SetColumn(stateHost, 1);
            option.Children.Add(stateHost);
            panel.Children.Add(option);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            buttons.Children.Add(ActionButton(L.T("取消"), CloseOverlay, false));
            buttons.Children.Add(ActionButton(L.T("恢复"), CommitRestore, true));
            panel.Children.Add(buttons);
            return panel;
        }

        private void CommitRestore()
        {
            var bundle = _pendingBundle;
            if (bundle == null) return;
            int added, updated;
            ScriptBundleFile.Apply(bundle, _clearFirst, out added, out updated);

            var ddl = bundle.Attachments.FirstOrDefault(a => a != null &&
                a.Kind == ScriptBundleFile.DdlKind && !string.IsNullOrWhiteSpace(a.Text));
            if (ddl != null)
            {
                DdlRestore.Save(new DdlPending
                {
                    Name = ddl.Name,
                    Text = ddl.Text,
                    Target = ddl.Target,
                    Token = Guid.NewGuid().ToString("N")
                });
                DdlRestore.SaveLastTarget(ddl.Target);
            }
            else DdlRestore.Clear();

            _pendingBundle = null;
            CloseOverlay();
            Refresh();
            Notify(string.Format(L.T("已恢复 {0} 个脚本"), added + updated) +
                   L.T("（刷新页面后生效）"));
        }

        // 自测入口：绕开文件对话框，走完整的确认卡 + Apply 路径。
        internal void StageBundleForTest(ScriptBundle bundle, string source)
        {
            StageBundle(bundle, source);
        }
        internal void SetClearFirstForTest(bool value) { _clearFirst = value; }
        internal void CommitRestoreForTest() { CommitRestore(); }
        internal void ShowBackupCardForTest() { ShowBackupCard(); }

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

            // 与编辑器同一口径：字段预填解析值，没改过就不参与回写。
            _importInitName = parsed.Name;
            _importInitVersion = parsed.Version;
            _importInitMatches = string.Join("\n", parsed.Matches);
            _importInitExcludes = string.Join("\n", parsed.Excludes);
            _importName = MakeMetaBox(_importInitName, 28, false);
            _importVersion = MakeMetaBox(_importInitVersion, 28, false);
            _importMatches = MakeMetaBox(_importInitMatches, 48, true);
            _importExcludes = MakeMetaBox(_importInitExcludes, 48, true);

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

            panel.Children.Add(BuildMetadataGrid(_importName, _importVersion,
                _importMatches, _importExcludes,
                L.T("不改则保持脚本原样；改动会在导入时写回 // @ 头，其它元数据不动。")));
            panel.Children.Add(MetaRow(L.T("命名空间"), parsed.Namespace));

            // @include 可能是 /regex/ 字面量，不能塞进「匹配站点」框，只读展示。
            if (parsed.Includes.Count > 0)
                panel.Children.Add(MetaRow(L.T("@include 规则"), string.Join("\n", parsed.Includes)));
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

        // 读导入卡里「相对预填值改动过」的字段；返回 null 表示输入非法。
        private ScriptMetadataEdit ImportEdit(out string error)
        {
            error = "";
            var edit = new ScriptMetadataEdit();
            if (_importName != null && _importName.Text != _importInitName) edit.Name = _importName.Text;
            if (_importVersion != null && _importVersion.Text != _importInitVersion)
                edit.Version = _importVersion.Text;
            if (_importMatches != null && _importMatches.Text != _importInitMatches)
                edit.Matches = SplitMetaLines(_importMatches.Text);
            if (_importExcludes != null && _importExcludes.Text != _importInitExcludes)
                edit.Excludes = SplitMetaLines(_importExcludes.Text);
            if (edit.Name != null && string.IsNullOrWhiteSpace(edit.Name))
            {
                error = L.T("名称不能为空，脚本未导入");
                return null;
            }
            if (edit.Version != null && string.IsNullOrWhiteSpace(edit.Version))
            {
                error = L.T("版本不能为空，脚本未导入");
                return null;
            }
            return edit;
        }

        // 先在按按钮时校验，避免填了空名字还白跑一趟 @require 下载。
        private bool CanCommitImport()
        {
            if (_pendingImport == null) return false;
            string error;
            if (ImportEdit(out error) != null) return true;
            Notify(error);
            return false;
        }

        internal void ConfirmImport()
        {
            if (!CanCommitImport()) return;
            if (ScriptParser.Parse(_pendingImport).Requires.Count > 0)
            {
                var unused = ConfirmImportAsync();
                return;
            }
            CommitImport(null);
        }

        internal async Task<bool> ConfirmImportAsync()
        {
            if (!CanCommitImport()) return false;
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
            return CommitImport(requireCode);
        }

        private bool CommitImport(List<string> requireCode)
        {
            var code = _pendingImport;
            if (code == null) return false;
            string error;
            var edit = ImportEdit(out error);
            if (edit == null) { Notify(error); return false; }
            var rewritten = ScriptParser.RewriteMetadata(code, edit, out error);
            if (rewritten == null)
            {
                Notify(L.T("元数据写回失败，脚本未导入：") + error);
                return false;   // 保留待导入状态，改完可以再点
            }
            _pendingImport = null;
            var added = ScriptStore.Add(rewritten, true, requireCode);
            CloseOverlay();
            Refresh();
            Notify(L.T("已导入 ") + added.Name + L.T("（刷新页面后生效）"));
            return true;
        }

        // 自测入口：直接写真实字段再走真实导入路径。
        internal string ImportNameForTest { get { return _importName == null ? null : _importName.Text; } }
        internal void SetImportMetadataForTest(string name, string version, string matches, string excludes)
        {
            if (_importName != null && name != null) _importName.Text = name;
            if (_importVersion != null && version != null) _importVersion.Text = version;
            if (_importMatches != null && matches != null) _importMatches.Text = matches;
            if (_importExcludes != null && excludes != null) _importExcludes.Text = excludes;
        }
        internal void CloseOverlayForTest() { CloseOverlay(); }

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
            _pendingBundle = null;
            _ddlTarget = null;
            _ddlFiles = null;
            _ddlAttachments.Clear();
            _clearFirst = false;
            _editing = null;
            _code = null;
            _numbers = null;
            _urlBox = null;
            _metaName = null;
            _metaVersion = null;
            _metaMatches = null;
            _metaExcludes = null;
            _metaInitName = _metaInitVersion = _metaInitMatches = _metaInitExcludes = "";
            _importName = null;
            _importVersion = null;
            _importMatches = null;
            _importExcludes = null;
            _importInitName = _importInitVersion = _importInitMatches = _importInitExcludes = "";
            ResetPgAdminSection();
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

        // 列表行最右列只有 26px，装不下「再点一次」。确认态临时向左跨一列给足宽度，
        // 按钮仍右对齐在行内；加底色遮住被覆盖的「最后运行」文字，保证点得到。
        private static void SetRowDeleteState(Button button, bool active)
        {
            if (active)
            {
                Grid.SetColumn(button, 3);
                Grid.SetColumnSpan(button, 2);
                button.HorizontalAlignment = HorizontalAlignment.Right;
                button.MinWidth = 84;
                button.BorderThickness = new Thickness(1);
                button.SetResourceReference(Control.BackgroundProperty, "SurfaceBrush");
                button.SetResourceReference(Control.BorderBrushProperty, "WarnBrush");
                Panel.SetZIndex(button, 5);
            }
            else
            {
                Grid.SetColumnSpan(button, 1);
                Grid.SetColumn(button, 4);
                button.HorizontalAlignment = HorizontalAlignment.Stretch;
                button.MinWidth = 22;
                button.Background = new SolidColorBrush(Colors.Transparent);
                button.BorderThickness = new Thickness(0);
                Panel.SetZIndex(button, 0);
            }
        }

        // 两段式确认，和「清空历史 / 清除已结束」保持同一做法。
        // decorate 让调用方在确认态临时调整按钮几何，不影响其它使用点。
        private static void ConfirmTwice(Button button, string normal, Action action,
            Action<Button, bool> decorate = null)
        {
            if (!Equals(button.Tag, "confirm"))
            {
                button.Tag = "confirm";
                button.Content = L.T("再点一次");
                if (decorate != null) decorate(button, true);
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    button.Tag = null;
                    button.Content = normal;
                    if (decorate != null) decorate(button, false);
                };
                timer.Start();
                return;
            }
            button.Tag = null;
            button.Content = normal;
            if (decorate != null) decorate(button, false);
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
