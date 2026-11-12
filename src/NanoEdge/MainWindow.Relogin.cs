using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;

namespace NanoEdge
{
    public sealed partial class MainWindow
    {
        // 悬浮重登条的两道显示门控：宿主窗口必须是前台活动窗口，且当前活动标签
        // 必须命中某条「已启用重登」的用户脚本 @match。两者都由调用方实时求值，
        // 事件只负责触发重算，不缓存结果。
        private bool ReloginForeground()
        {
            return IsActive && WindowState != WindowState.Minimized;
        }

        private bool ReloginPageEligible()
        {
            var tab = _active;
            return tab != null && PgAdminReloginConfig.ForUrl(tab.Url) != null;
        }

        internal bool ReloginForegroundForTest() { return ReloginForeground(); }
        internal bool ReloginPageEligibleForTest() { return ReloginPageEligible(); }

        // 重登监测器只在这里构造：MainWindow.cs 主文件不必为了几个 WebView2 类型加 using。
        // 隐藏视图与主视图共用 _browser.EnvironmentAsync() 那唯一一个 environment（同一 profile）。
        private PgAdminSessionMonitor CreateReloginMonitor()
        {
            var monitor = new PgAdminSessionMonitor(
                _reloginBar,
                () => new WindowInteropHelper(this).Handle,
                () => _browser.EnvironmentAsync(),
                () => _active,
                tab => _browser.Core(tab),
                id => OpenReloginEditor(id),
                message => MessageBox.Show(this, message, "NanoEdge",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes,
                (core, url) => { if (core != null) core.Navigate(url); });

            _browser.HttpResponse += (tab, uri, status) =>
            {
                if (_relogin != null) _relogin.OnResponse(tab, uri, status);
            };
            return monitor;
        }

        // F-4 / 9：没有凭据时打开「对应脚本」的编辑页，并把重登区块展开到眼前。
        // 新开标签而不是把 pgAdmin 标签转成内部页，避免 Dispose 正在用的 WebView2、打断未保存的 SQL。
        private void OpenReloginEditor(string scriptId)
        {
            ReloginEditorOpenedForTest++;
            var site = _active == null ? "" : ZoomStore.SiteOf(_active.Url);
            var existing = _tabs.FirstOrDefault(t => t.InternalPage == "scripts");
            if (existing != null)
            {
                SelectTab(existing);
                if (!string.IsNullOrEmpty(site))
                    OpenInternalPage(existing, InternalPages.Scripts, site);
            }
            else
            {
                NewTab(InternalPages.Scripts, site);
            }

            if (_scriptsPage == null) return;
            var script = string.IsNullOrEmpty(scriptId) ? null : ScriptStore.Find(scriptId);
            if (script != null) _scriptsPage.BeginEdit(script);
            _scriptsPage.FocusReloginSection();
        }

        internal PgAdminReloginBar ReloginBarForTest { get { return _reloginBar; } }
        internal PgAdminSessionMonitor ReloginForTest { get { return _relogin; } }
        internal int ReloginEditorOpenedForTest { get; private set; }
        internal Task<CoreWebView2Environment> EnvironmentForTest()
        {
            return _browser.EnvironmentAsync();
        }
        internal IntPtr WindowHandleForTest { get { return new WindowInteropHelper(this).Handle; } }
    }
}
