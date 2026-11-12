using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace NanoEdge
{
    public sealed partial class MainWindow
    {
        // =====================================================================
        // 登录填充的宿主层（SPEC 7.15）
        // 图标和账号下拉都在页面里（LoginFill.cs 注入的 DOM），这里只负责：
        // 收到图标/条目的消息 -> 向 KeePassXC 取凭据 -> 按表单索引填充。
        // 密码只在「填充」这一瞬间经过宿主内存，不下发到页面脚本、不写日志。
        // =====================================================================

        private List<LoginField> _loginFields = new List<LoginField>();
        private List<KeePassCredential> _loginEntries = new List<KeePassCredential>();
        private bool _loginBusy;

        private void OnLoginUiMessage(TabModel tab, string json)
        {
            if (tab == null || !ReferenceEquals(tab, _active) || !_settings.PasswordFill) return;
            Dictionary<string, object> msg;
            try { msg = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json); }
            catch { return; }
            if (msg == null) return;

            var kind = Field(msg, "neLogin");
            var index = IntField(msg, "index");
            if (kind == "icon" || kind == "reload") { OnLoginClick(index); return; }
            if (kind == "pick")
            {
                var entry = IntField(msg, "entry");
                if (entry >= 0 && entry < _loginEntries.Count)
                {
                    var unused = FillCredential(_loginEntries[entry], index);
                }
            }
        }

        // 导航完成后调用：扫一次页面，顺手把输入框内的图标装好/重定位。
        private async void RefreshLoginState(TabModel tab)
        {
            if (!_settings.PasswordFill) { HideLoginIcons(); return; }
            // SelectTab 在会话恢复/内核重启时也可能早于 _browser 初始化，挡一道。
            if (tab == null || tab != _active || _browser == null) return;
            var core = _browser.Core(tab);
            if (core == null) { HideLoginIcons(); return; }
            try
            {
                var fields = await LoginFill.ScanAsync(core);
                if (tab != _active) return;
                _loginFields = fields;
                await LoginFill.InstallIconsAsync(core);
            }
            catch (Exception ex) { Diagnostics.Log("refresh login state failed: " + ex.Message); }
        }

        private void HideLoginIcons()
        {
            _loginFields.Clear();
            var core = _active == null || _browser == null ? null : _browser.Core(_active);
            if (core != null) { var unused = LoginFill.RemoveIconsAsync(core); }
        }

        // formIndex 是图标对应的表单原始索引（allPasswords 的顺序，含隐藏框）。
        private async void OnLoginClick(int formIndex)
        {
            if (_loginBusy || !_settings.PasswordFill) return;
            var tab = _active;
            if (tab == null) return;
            var core = _browser.Core(tab);
            if (core == null) return;

            _loginBusy = true;
            try
            {
                // 点之前重扫：页面可能在导航之后才把表单渲染出来，
                // 同时把图标位置刷新到最新布局。
                var fields = await LoginFill.ScanAsync(core);
                if (tab != _active) return;
                _loginFields = fields;
                await LoginFill.InstallIconsAsync(core);
                if (fields.Count == 0)
                {
                    _toast.Show(L.T("这个页面没有找到登录框"));
                    return;
                }

                var url = tab.Url ?? "";
                var result = await KeePassBridge.GetLoginsAsync(url);
                if (tab != _active) return;

                if (result.Status == KeePassStatus.NotAssociated)
                {
                    // 首次使用：走一次关联，KeePassXC 会弹窗要用户点允许
                    _toast.Show(L.T("请在 KeePassXC 弹窗里允许 NanoEdge 访问"));
                    var assoc = await KeePassBridge.AssociateAsync("NanoEdge", TimeSpan.FromSeconds(120));
                    if (tab != _active) return;
                    if (assoc.Status != KeePassStatus.Ok)
                    {
                        _toast.Show(KeePassBridge.StatusText(assoc.Status) +
                                    (string.IsNullOrEmpty(assoc.Message) ? "" : "：" + assoc.Message));
                        return;
                    }
                    result = await KeePassBridge.GetLoginsAsync(url);
                    if (tab != _active) return;
                }

                if (!result.Ok)
                {
                    _toast.Show(KeePassBridge.StatusText(result.Status));
                    return;
                }

                _loginEntries = result.Entries;
                if (result.Entries.Count == 0)
                {
                    _toast.Show(L.T("KeePassXC 里没有匹配这个站点的条目"));
                    return;
                }
                // 官方插件的手感：只有一个账号时点了就填，不弹下拉。
                if (result.Entries.Count == 1)
                {
                    await FillCredential(result.Entries[0], formIndex);
                    return;
                }
                await LoginFill.ShowPickerAsync(core, formIndex, result.Entries, Theme.IsDark,
                    L.T("选择要填充的账号"), L.T("重新加载"));
            }
            catch (Exception ex)
            {
                Diagnostics.Log("login click failed: " + ex.Message);
                _toast.Show(L.T("KeePassXC 操作失败"));
            }
            finally { _loginBusy = false; }
        }

        private async Task FillCredential(KeePassCredential entry, int formIndex)
        {
            var tab = _active;
            if (tab == null) return;
            var core = _browser.Core(tab);
            if (core == null) return;
            await LoginFill.HidePickerAsync(core);

            bool ok;
            if (formIndex >= 0 && _loginFields.Exists(f => f.index == formIndex))
                ok = await LoginFill.FillAsync(core, formIndex, entry.Login, entry.Password);
            else
                ok = await LoginFill.FillActiveAsync(core, entry.Login, entry.Password);
            _toast.Show(ok ? L.T("已填充账号密码") : L.T("填充失败，页面结构可能变了"));
            if (!ok) RefreshLoginState(tab);
        }

        private static string Field(Dictionary<string, object> map, string key)
        {
            object v;
            return map.TryGetValue(key, out v) && v != null ? Convert.ToString(v) : "";
        }

        private static int IntField(Dictionary<string, object> map, string key)
        {
            int n;
            return int.TryParse(Field(map, key), out n) ? n : -1;
        }
    }
}
