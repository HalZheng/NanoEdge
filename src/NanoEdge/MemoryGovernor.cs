using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace NanoEdge
{
    // BrowserHost 实现这层，MemoryGovernor 只做决策（SPEC 4.1/4.2），不碰 WebView2 细节。
    public interface IMemoryTabOps
    {
        bool HasLiveView(TabModel tab);
        void SetLow(TabModel tab);
        void SetNormal(TabModel tab);
        Task<TabSnapshot> CaptureSnapshotAsync(TabModel tab);
        void Discard(TabModel tab, TabSnapshot snapshot);
        Task<CoreWebView2Environment> EnvironmentAsync();
        Dictionary<ulong, string> FrameMap(IEnumerable<TabModel> tabs);
        int LiveViewCount(IEnumerable<TabModel> tabs);
    }

    // 三态内存治理：Active / Low / Discarded（SPEC 4.1，v1.3 起取消 Suspended）。
    // 取消 Suspended 的原因：Phase 0 与 M2 实测都显示它不省内存（工作集反而回升），
    // 唯一收益是停 JS，却要求控件不可见、还要与 MemoryUsageTargetLevel 串行互斥。
    public sealed class MemoryGovernor
    {
        private readonly Func<TabModel> _activeTab;
        private readonly Func<List<TabModel>> _tabs;
        private readonly IMemoryTabOps _ops;
        private readonly MemorySettings _settings;

        private readonly DispatcherTimer _stateTimer;
        private readonly DispatcherTimer _pressureTimer;

        private DateTime? _deactivatedUtc;
        private bool _windowActive = true;
        private bool _minimized;
        private bool _started;

        public event Action Changed;

        public MemorySnapshot LastSnapshot { get; private set; }
        public bool WindowActive { get { return _windowActive; } }
        public bool Minimized { get { return _minimized; } }

        public MemoryGovernor(Func<TabModel> activeTab, Func<List<TabModel>> tabs,
            IMemoryTabOps ops, MemorySettings settings)
        {
            _activeTab = activeTab;
            _tabs = tabs;
            _ops = ops;
            _settings = settings;

            _stateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
            _stateTimer.Tick += (s, e) => Tick();
            _pressureTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10000) };
            _pressureTimer.Tick += (s, e) => PressureCheck();
        }

        public void Start()
        {
            if (_started) return;
            _started = true;
            _stateTimer.Start();
            _pressureTimer.Start();
        }

        public void Stop()
        {
            _started = false;
            _stateTimer.Stop();
            _pressureTimer.Stop();
        }

        // ---------- window events ----------

        public void NotifyActivated()
        {
            _windowActive = true;
            _deactivatedUtc = null;
            _minimized = false;
            var active = _activeTab();
            if (active != null) EnsureActive(active);
            Log("window activated");
            Changed?.Invoke();
        }

        public void NotifyDeactivated()
        {
            _windowActive = false;
            _deactivatedUtc = DateTime.UtcNow;
            Log("window deactivated -> cool timer starts");
            Changed?.Invoke();
        }

        public void NotifyMinimized(bool minimized)
        {
            _minimized = minimized;
            if (minimized)
            {
                _windowActive = false;
                if (_deactivatedUtc == null) _deactivatedUtc = DateTime.UtcNow;
            }
            else
            {
                var active = _activeTab();
                if (active != null) EnsureActive(active);
            }
            Log("minimized=" + minimized);
            Changed?.Invoke();
        }

        public void NotifySelected(TabModel tab)
        {
            if (tab == null) return;
            tab.LastActiveUtc = DateTime.UtcNow;
            tab.BackgroundSinceUtc = null;
            if (tab.MemoryState == TabMemoryState.Low) _ops.SetNormal(tab);
            Changed?.Invoke();
        }

        // ---------- exemptions (SPEC 4.3) ----------

        private bool IsResidentOrigin(TabModel tab)
        {
            var list = _settings.ResidentOrigins;
            if (list == null || list.Count == 0 || string.IsNullOrEmpty(tab.Host)) return false;
            return list.Any(o => string.Equals(o, tab.Host, StringComparison.OrdinalIgnoreCase));
        }

        public string ExemptionReason(TabModel tab)
        {
            if (tab.IsPlayingAudio) return L.T("正在播放音频");
            if (tab.ActiveDownloads > 0) return L.T("有未完成下载");
            if (tab.HasOpenedDevTools) return L.T("已打开 DevTools");
            if (tab.IsSecret && tab.KeepAlive) return L.T("密封标签保活");
            if (tab.Resident || IsResidentOrigin(tab)) return L.T("内存常驻");
            return null;
        }

        // 该标签最深可以降到哪一态。音频播放中连 Low 都不允许（SPEC 4.8）。
        public TabMemoryState DeepestAllowed(TabModel tab)
        {
            if (tab.IsPlayingAudio) return TabMemoryState.Active;
            return ExemptionReason(tab) == null ? TabMemoryState.Discarded : TabMemoryState.Low;
        }

        private static bool CanReach(TabMemoryState limit, TabMemoryState target)
        {
            return Rank(target) <= Rank(limit);
        }

        private static int Rank(TabMemoryState state)
        {
            switch (state)
            {
                case TabMemoryState.Active: return 0;
                case TabMemoryState.Low: return 1;
                default: return 2;
            }
        }

        // ---------- state machine ----------

        private void Tick()
        {
            var active = _activeTab();
            var tabs = _tabs();
            var now = DateTime.UtcNow;

            foreach (var tab in tabs)
            {
                if (tab.MemoryBusy) continue;
                if (!_ops.HasLiveView(tab)) continue;

                var limit = DeepestAllowed(tab);
                var backToWork = _windowActive && !_minimized;

                if (ReferenceEquals(tab, active))
                {
                    tab.BackgroundSinceUtc = null;
                    if (backToWork) EnsureActive(tab);
                    else if (Elapsed(_deactivatedUtc) >= Ms(_settings.CoolDelayMs)) EnsureLow(tab);
                    continue;
                }

                if (tab.BackgroundSinceUtc == null) tab.BackgroundSinceUtc = now;
                var background = Elapsed(tab.BackgroundSinceUtc);

                if (_settings.AutoDiscardEnabled &&
                    CanReach(limit, TabMemoryState.Discarded) &&
                    background >= Ms(_settings.BackgroundDiscardMs) &&
                    _ops.LiveViewCount(tabs) > 1)
                {
                    DiscardInBackground(tab, "background timeout");
                    continue;
                }

                if (background >= Ms(_settings.CoolDelayMs)) EnsureLow(tab);
            }
        }

        // 状态真变了才通知：MainWindow 靠 Changed 刷新标签条角标。
        // Tick 每秒都会调这两个方法，没变化时不该把 UI 推倒重建。
        private void EnsureLow(TabModel tab)
        {
            if (tab.MemoryState != TabMemoryState.Active) return;
            _ops.SetLow(tab);
            if (tab.MemoryState == TabMemoryState.Low) Changed?.Invoke();
        }

        private void EnsureActive(TabModel tab)
        {
            if (tab.MemoryState != TabMemoryState.Low) return;
            _ops.SetNormal(tab);
            if (tab.MemoryState == TabMemoryState.Active) Changed?.Invoke();
        }

        private async void DiscardInBackground(TabModel tab, string reason)
        {
            var ok = await DiscardTabAsync(tab);
            if (ok) Log("tab " + tab.Id + " -> Discarded (" + reason + ")");
        }

        public async Task<bool> DiscardTabAsync(TabModel tab)
        {
            if (tab == null || tab.MemoryBusy) return false;
            if (!_ops.HasLiveView(tab)) return false;
            tab.MemoryBusy = true;
            try
            {
                var snapshot = await _ops.CaptureSnapshotAsync(tab);
                _ops.Discard(tab, snapshot);
                // 密封标签的快照里有真实 URL / 标题，不落盘（7.4）。
                if (!tab.IsSecret) SnapshotStore.Save(tab, snapshot);
                Changed?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                Diagnostics.Log("discard failed: " + ex.Message);
                return false;
            }
            finally { tab.MemoryBusy = false; }
        }

        // ---------- memory pressure ----------

        private async void PressureCheck()
        {
            if (_ops.LiveViewCount(_tabs()) == 0)
            {
                var idle = await SampleAsync();
                if (idle != null) Changed?.Invoke();
                return;
            }

            var snapshot = await SampleAsync();
            if (snapshot == null) return;

            var tabs = _tabs();
            var live = _ops.LiveViewCount(tabs);
            if (tabs.Count > _settings.ManyTabsCount && live > 1 &&
                snapshot.GroupWorkingSetMB > _settings.ManyTabsMemoryMB)
            {
                var victims = DiscardCandidates(tabs).Take(2).ToList();
                foreach (var victim in victims)
                {
                    if (await DiscardTabAsync(victim))
                        Log("tab " + victim.Id + " discarded (many tabs)");
                }
                await SampleAsync();
                Changed?.Invoke();
                return;
            }

            if (snapshot.GroupWorkingSetMB <= _settings.PressureLimitMB) return;

            // 超阈值：按 LRU 丢弃后台标签，直到低于目标值且仍保留 PressureMinTabs 个视图。
            var estimate = snapshot.GroupWorkingSetMB;
            foreach (var victim in DiscardCandidates(tabs))
            {
                if (estimate <= _settings.PressureTargetMB) break;
                if (_ops.LiveViewCount(tabs) <= _settings.PressureMinTabs) break;
                var saved = snapshot.TabWorkingSetMB(victim.Id);
                if (await DiscardTabAsync(victim))
                {
                    estimate -= saved > 0 ? saved : 40;
                    Log("tab " + victim.Id + " discarded (pressure " +
                        snapshot.GroupWorkingSetMB + "MB)");
                }
            }
            await SampleAsync();
            Changed?.Invoke();
        }

        private IEnumerable<TabModel> DiscardCandidates(List<TabModel> tabs)
        {
            var active = _activeTab();
            return tabs
                .Where(t => !ReferenceEquals(t, active))
                .Where(t => _ops.HasLiveView(t))
                .Where(t => t.MemoryState != TabMemoryState.Discarded)
                .Where(t => DeepestAllowed(t) == TabMemoryState.Discarded)
                .OrderBy(t => t.LastActiveUtc)
                .ToList();
        }

        // ---------- manual actions ----------

        public async Task<ReleaseResult> ReleaseBackgroundAsync()
        {
            var before = LastSnapshot ?? await SampleAsync();
            var active = _activeTab();
            var released = 0;
            var estimated = 0.0;

            foreach (var tab in _tabs())
            {
                if (ReferenceEquals(tab, active)) continue;
                if (!_ops.HasLiveView(tab)) continue;
                if (DeepestAllowed(tab) != TabMemoryState.Discarded) continue;
                var saved = before == null ? 0 : before.TabWorkingSetMB(tab.Id);
                if (await DiscardTabAsync(tab))
                {
                    released++;
                    estimated += saved;
                }
            }

            var after = await SampleAsync();
            return new ReleaseResult
            {
                Count = released,
                EstimatedMB = Math.Round(estimated, 0),
                ActualDeltaMB = before != null && after != null
                    ? Math.Round(before.GroupWorkingSetMB - after.GroupWorkingSetMB, 0) : 0,
                ActualPrivateDeltaMB = before != null && after != null
                    ? Math.Round(before.GroupPrivateMB - after.GroupPrivateMB, 0) : 0
            };
        }

        public sealed class ReleaseResult
        {
            public int Count;
            public double EstimatedMB;          // 预估（工作集口径）
            public double ActualDeltaMB;        // 实测工作集差
            public double ActualPrivateDeltaMB; // 实测私有提交差（丢弃才真正归还）
        }

        // ---------- sampling ----------

        public async Task<MemorySnapshot> SampleAsync()
        {
            CoreWebView2Environment environment = null;
            try
            {
                if (_ops.LiveViewCount(_tabs()) > 0)
                    environment = await _ops.EnvironmentAsync();
            }
            catch (Exception ex) { Diagnostics.Log("environment for probe failed: " + ex.Message); }

            var snapshot = await MemoryProbe.SampleAsync(environment, () => _ops.FrameMap(_tabs()));
            LastSnapshot = snapshot;
            return snapshot;
        }

        // ---------- helpers ----------

        private static TimeSpan Ms(int milliseconds)
        {
            return milliseconds == int.MaxValue
                ? TimeSpan.MaxValue
                : TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        }

        private static TimeSpan Elapsed(DateTime? since)
        {
            return since == null ? TimeSpan.Zero : DateTime.UtcNow - since.Value;
        }

        private void Log(string message)
        {
            if (_settings.LogDiagnostics) Diagnostics.Log("[mem] " + message);
        }

        public string StateName(TabMemoryState state)
        {
            switch (state)
            {
                case TabMemoryState.Active: return L.T("活跃");
                case TabMemoryState.Low: return L.T("降冷");
                default: return L.T("已丢弃");
            }
        }
    }
}
