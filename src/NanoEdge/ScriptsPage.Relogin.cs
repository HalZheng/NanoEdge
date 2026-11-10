using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NanoEdge
{
    public sealed partial class ScriptsPage
    {
        // 3.1 编辑页里的「pgAdmin 自动重登」区块。
        // 用户名/密码只在加密凭据文件里；这里只把 enabled / siteOrigin / autoRelogin
        // 写回该脚本的 scripts.json 记录。
        private sealed class PgToggle
        {
            private readonly Border _track;
            private readonly Border _knob;
            private bool _value;

            public event Action<bool> Changed;

            public PgToggle(bool initial)
            {
                _track = new Border
                {
                    Width = 26, Height = 14, CornerRadius = new CornerRadius(7),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center
                };
                _knob = new Border
                {
                    Width = 10, Height = 10, CornerRadius = new CornerRadius(5),
                    Background = Brushes.White, Margin = new Thickness(2, 0, 2, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                _track.Child = _knob;
                _track.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    Set(!_value);
                    var handler = Changed;
                    if (handler != null) handler(_value);
                };
                Set(initial);
            }

            public FrameworkElement View { get { return _track; } }
            public bool Value { get { return _value; } }

            public void Set(bool value)
            {
                _value = value;
                _track.SetResourceReference(Border.BackgroundProperty,
                    value ? "AccentBrush" : "StateDiscardedBrush");
                _knob.HorizontalAlignment = value ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            }
        }

        // 站点完全由运行时复用用户脚本的 @match 匹配逻辑推导：找出当前页面命中的
        // 那条规则，用页面 origin + 规则的路径前缀拼出地址。编辑页不再有任何
        // 站点输入框 / 下拉 / 手填 / 切换控件，只留一行说明文字。

        private Border _pgBlock;
        private StackPanel _pgDetails;
        private PgToggle _pgEnableToggle;
        private PgToggle _pgAutoToggle;
        private TextBox _pgUser;
        private PasswordBox _pgPassword;
        private TextBlock _pgSavedHint;
        private Button _pgClear;
        private bool _pgIsNew;
        private bool _pgHasCredential;
        private bool _pgCredentialUnreadable;

        private void InitPgAdminSection(UserScript script, bool isNew)
        {
            _pgIsNew = isNew;
            _pgHasCredential = false;
            _pgCredentialUnreadable = false;
            if (script == null) return;
            try { _pgHasCredential = PgAdminCredentialStore.Has(script.Id); }
            catch { _pgCredentialUnreadable = true; }
        }

        internal void FocusReloginSection()
        {
            if (_pgBlock == null) return;
            try { _pgBlock.BringIntoView(); } catch { }
            if (_pgUser != null && _pgDetails != null &&
                _pgDetails.Visibility == Visibility.Visible) _pgUser.Focus();
        }

        private void ResetPgAdminSection()
        {
            _pgBlock = null;
            _pgDetails = null;
            _pgEnableToggle = null;
            _pgAutoToggle = null;
            _pgUser = null;
            // 离开编辑页时才清掉 PasswordBox 里的 SecureString：
            // 保存后用户还能看到刚输的密码，关掉页面后内存里就不留副本了。
            if (_pgPassword != null) _pgPassword.Clear();
            _pgPassword = null;
            _pgSavedHint = null;
            _pgClear = null;
            _pgHasCredential = false;
            _pgCredentialUnreadable = false;
        }

        private FrameworkElement BuildPgAdminBlock(bool isNew)
        {
            var cfg = _editing == null ? null : _editing.PgadminRelogin;
            var enabled = !isNew && cfg != null && cfg.Enabled;
            var auto = cfg == null || cfg.AutoRelogin;

            _pgBlock = new Border
            {
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(10, 8, 10, 8),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1)
            };
            _pgBlock.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            _pgBlock.SetResourceReference(Border.BorderBrushProperty, "LineBrush");

            var panel = new StackPanel();
            var title = new TextBlock
            {
                Text = L.T("pgAdmin 自动重登"),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            panel.Children.Add(title);

            _pgEnableToggle = new PgToggle(enabled);
            _pgEnableToggle.Changed += OnPgEnabledChanged;
            var enableRow = PgRow(L.T("启用"), _pgEnableToggle.View);
            panel.Children.Add(enableRow);

            _pgDetails = new StackPanel { Visibility = enabled ? Visibility.Visible : Visibility.Collapsed };

            // 站点根本不是控件：运行时直接复用用户脚本的 @match 匹配当前页面来推导。
            _pgDetails.Children.Add(PgHint(
                L.T("站点跟随脚本的 @match 自动匹配，无需选择或填写；运行时按当前页面推导。")));

            var user = "";
            if (_pgHasCredential)
            {
                try
                {
                    var credential = PgAdminCredentialStore.TryGet(_editing.Id);
                    if (credential != null)
                    {
                        user = credential.UserName ?? "";
                        if (credential.Password != null)
                            Array.Clear(credential.Password, 0, credential.Password.Length);
                    }
                }
                catch { _pgCredentialUnreadable = true; }
            }
            _pgUser = PgTextBox(user, 26);
            _pgDetails.Children.Add(PgRow(L.T("用户名"), _pgUser));

            _pgPassword = new PasswordBox
            {
                Height = 26,
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 0),
                PasswordChar = '*'
            };
            _pgPassword.SetResourceReference(PasswordBox.ForegroundProperty, "TextBrush");
            _pgDetails.Children.Add(PgRow(L.T("密码"), _pgPassword));
            _pgSavedHint = PgHint(_pgCredentialUnreadable
                ? L.T("凭据无法读取，请重新保存")
                : _pgHasCredential
                    ? L.T("已保存，留空则不修改")
                    : L.T("密码只加密保存在本机，重启后仍可用"));
            _pgDetails.Children.Add(_pgSavedHint);

            _pgAutoToggle = new PgToggle(auto);
            _pgDetails.Children.Add(PgRow(L.T("掉线时自动重登"), _pgAutoToggle.View));

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 8, 0, 0)
            };
            _pgClear = ActionButton(L.T("清除凭据"), null, false);
            ConfirmBindClear();
            buttons.Children.Add(_pgClear);
            var save = ActionButton(L.T("保存"), SavePgAdmin, true);
            buttons.Children.Add(save);
            _pgDetails.Children.Add(buttons);

            panel.Children.Add(_pgDetails);
            var blockHint = PgHint(isNew
                ? L.T("先保存脚本，才能为它配置自动重登。")
                : L.T("关闭开关后按钮消失、自动重登停止，但已保存的凭据会保留到点「清除凭据」为止。"));
            panel.Children.Add(blockHint);

            _pgBlock.Child = panel;
            _pgBlock.IsEnabled = !isNew;
            return _pgBlock;
        }

        private void ConfirmBindClear()
        {
            _pgClear.Click += (s, e) => ConfirmTwice(_pgClear, L.T("清除凭据"), ClearPgAdminCredential);
        }

        private static FrameworkElement PgRow(string label, UIElement control)
        {
            var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var left = new TextBlock
            {
                Text = label,
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            left.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            grid.Children.Add(left);
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
            return grid;
        }

        private static TextBox PgTextBox(string text, double height)
        {
            var box = new TextBox
            {
                Text = text,
                Height = height,
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 0)
            };
            box.SetResourceReference(TextBox.ForegroundProperty, "TextBrush");
            return box;
        }

        private static TextBlock PgHint(string text)
        {
            var hint = new TextBlock
            {
                Text = text,
                FontSize = 9.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            return hint;
        }

        private void OnPgEnabledChanged(bool on)
        {
            if (_pgDetails != null)
                _pgDetails.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (_editing == null || _pgIsNew) return;
            // 开关本身立即生效（凭据保留，不删除）。
            var cfg = _editing.PgadminRelogin ?? new PgAdminReloginSettings();
            cfg.Enabled = on;
            _editing.PgadminRelogin = cfg;
            ScriptStore.CommitSettings();
        }

        private void SavePgAdmin()
        {
            if (_editing == null || _pgIsNew)
            {
                Notify(L.T("先保存脚本，才能为它配置自动重登。"));
                return;
            }
            if (_pgEnableToggle == null || !_pgEnableToggle.Value) return;

            var userName = _pgUser == null ? "" : (_pgUser.Text ?? "").Trim();
            if (string.IsNullOrEmpty(userName))
            {
                Notify(L.T("请填写用户名"));
                return;
            }

            // 凭据按「脚本」存：站点由运行时按当前页面 + @match 推导，
            // 保存时不需要、也不允许指定任何固定地址。
            var scriptId = _editing.Id;
            var passwordLength = _pgPassword == null ? 0 : _pgPassword.SecurePassword.Length;
            if (passwordLength == 0 && !_pgHasCredential)
            {
                Notify(L.T("请填写密码"));
                return;
            }

            byte[] password = null;
            try
            {
                if (passwordLength > 0) password = SecureBytes.ToUtf8(_pgPassword.SecurePassword);
                if (password != null)
                    PgAdminCredentialStore.Save(scriptId, userName, password);
                else
                {
                    // 留空：保留原密码，只更新用户名与配置。
                    var old = PgAdminCredentialStore.TryGet(scriptId);
                    if (old == null) { Notify(L.T("请填写密码")); return; }
                    var oldBytes = old.Password;
                    try { PgAdminCredentialStore.Save(scriptId, userName, oldBytes); }
                    finally { if (oldBytes != null) Array.Clear(oldBytes, 0, oldBytes.Length); }
                }
            }
            catch (Exception ex)
            {
                PgAdminLog.Exception("credential-save", ex);
                Notify(L.T("凭据保存失败，配置未更新"));   // 3.1：失败不更新配置
                return;
            }
            finally
            {
                // 只清零我们从 SecurePassword 拷出来的 byte[] 副本。
                // 不清空 PasswordBox 本身：保存成功（或失败）后把用户刚输的密码
                // 抹成空白，会被误认为「密码被清空 / 没保存上」；失败时还得重输一遍。
                // PasswordBox 自己的 SecureString 在关闭编辑页时由 CloseOverlay 清掉。
                if (password != null) Array.Clear(password, 0, password.Length);
            }

            // 源码框里刚改过 @match 但还没点编辑器「保存」时，也要让索引里的
            // 匹配规则同步，否则运行时推导站点会用旧规则。
            _editing.Matches = ScriptParser.Parse(_code == null ? "" : (_code.Text ?? "")).Matches;
            var cfg = _editing.PgadminRelogin ?? new PgAdminReloginSettings();
            cfg.Enabled = true;
            cfg.AutoRelogin = _pgAutoToggle == null || _pgAutoToggle.Value;
            _editing.PgadminRelogin = cfg;

            _pgHasCredential = true;
            _pgCredentialUnreadable = false;
            // 刚保存完时密码框还留着内容，提示要直接说「已保存」；
            // 「留空则不修改」是下次重新打开编辑页（框为空）时用的那句。
            if (_pgSavedHint != null) _pgSavedHint.Text = L.T("密码已保存，重启后仍可用");
            ScriptStore.CommitSettings();
            PgAdminReloginEvents.RaiseSaved();
            Notify(L.T("pgAdmin 重登凭据已保存"));
        }

        private void ClearPgAdminCredential()
        {
            if (_editing == null) return;
            try
            {
                PgAdminCredentialStore.Clear(_editing.Id);
                _pgHasCredential = false;
                // 清除凭据是明确的删除动作，这时把密码框清空才是符合预期的，
                // 并把提示退回「未保存」状态，避免下次点保存又把它存回去。
                if (_pgPassword != null) _pgPassword.Clear();
                if (_pgSavedHint != null)
                    _pgSavedHint.Text = L.T("密码只加密保存在本机，重启后仍可用");
                Notify(L.T("已清除该脚本的凭据"));
            }
            catch (Exception ex)
            {
                PgAdminLog.Exception("credential-clear", ex);
                Notify(L.T("凭据无法读取，请重新保存"));
            }
        }

        // 自测入口：把值写进真实控件，再走真实保存路径（不模拟鼠标点击）。
        internal void SetPgAdminForTest(bool enabled, string user, string password)
        {
            if (_pgEnableToggle != null && _pgEnableToggle.Value != enabled)
                _pgEnableToggle.Set(enabled);
            if (_pgDetails != null)
                _pgDetails.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            if (_pgUser != null && user != null) _pgUser.Text = user;
            if (_pgPassword != null) _pgPassword.Password = password ?? "";
        }

        internal void TogglePgAdminForTest(bool on)
        {
            if (_pgEnableToggle == null || _pgEnableToggle.Value == on) return;
            _pgEnableToggle.Set(on);
            OnPgEnabledChanged(on);     // 与真实点击走同一条配置写入路径
        }
        internal void SavePgAdminForTest() { SavePgAdmin(); }
        internal void ClearPgAdminForTest() { ClearPgAdminCredential(); }
        // 自测：数区块里真实存在的文本输入框（TextBox + PasswordBox）。
        // 站点不是控件了，应当正好两个。
        internal int PgAdminInputCountForTest
        {
            get { return _pgBlock == null ? -1 : CountInputs(_pgBlock); }
        }
        private static int CountInputs(DependencyObject node)
        {
            var count = (node is TextBox || node is PasswordBox) ? 1 : 0;
            var children = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < children; i++)
                count += CountInputs(VisualTreeHelper.GetChild(node, i));
            return count;
        }
        internal bool PgAdminEnabledForTest
        {
            get { return _pgEnableToggle != null && _pgEnableToggle.Value; }
        }
        // -1 表示编辑页已关闭、PasswordBox 已被清掉。
        internal int PgAdminPasswordLengthForTest
        {
            get { return _pgPassword == null ? -1 : _pgPassword.SecurePassword.Length; }
        }
        internal string PgAdminHintForTest
        {
            get { return _pgSavedHint == null ? "" : _pgSavedHint.Text; }
        }
    }
}
