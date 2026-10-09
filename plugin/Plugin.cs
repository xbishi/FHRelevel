using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using FHGUI;
using FHGUI.Modules;
using FHGUI.States;
using FHRL;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace FHRelevel
{
    /// <summary>
    /// 霜港迷城「重新升级」修改器 v1.4
    /// 复刻幽港迷城公会模式的 ResetLevels 语义（保留XP，移除高级卡+清专精，全额返还选卡点/专精点）。
    ///
    /// v1.4 架构结论（前四版排查所得）：
    ///  - 本游戏 Harmony 补丁无效（游戏程序集 AOT 预编译），不使用 Harmony
    ///  - 本游戏 IMGUI/OnGUI 不渲染（纯 uGUI），不使用 IMGUI
    ///  - MonoBehaviour Update/OnGUI 对外来组件不分发，不依赖
    ///  - 可用通道：SceneManager.activeSceneChanged / Application.logMessageReceived（主线程回调）
    ///  - UI 用 uGUI ScreenSpaceOverlay Canvas 自绘（走游戏自身的事件系统与渲染）
    ///  - 新卡牌界面检测用轮询（NewCardsListModule.activeInHierarchy）
    ///  - 联机判定不用 FFSNetwork.IsOnline（EOS 后端常驻导致误判），改用 GameServer/IsClient
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class FHRelevelPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "bishi.fh.relevel";
        public const string PluginName = "FH Relevel 重新升级";
        public const string PluginVersion = "1.6.4";

        internal static FHRelevelPlugin Instance;
        internal ManualLogSource _log;
        internal static string DataDir;
        internal ConfigEntry<Key> _hotkey;
        private ConfigEntry<bool> _autoSave;
        internal bool AutoSaveEnabled { get { return _autoSave != null && _autoSave.Value; } }

        private static System.Diagnostics.Stopwatch _wallClock;
        private static Thread _heartbeatThread;
        internal static double LastDriverTickWall;
        internal static int DriverTickCount;
        private static float _nextHousekeep;
        private static float _nextHkProbe;
        private static float _nextHkExit;
        private static float _nextHkExit2;
        private static bool _loggedFirstHousekeep;
        private static bool _loggedMpState;
        private static string _lastMpLine;
        private static FHMapChoreographer _driverHost;
        private static bool _loggedCampaignReady;
        private static bool _probedChoreoNull;

        private void Awake()
        {
            Instance = this;
            _log = Logger;
            _hotkey = Config.Bind("General", "Hotkey", Key.F8, "打开/关闭重新升级窗口 (Hold half a second)");
            _autoSave = Config.Bind("General", "AutoSave", true, "重新升级后自动存档 (Auto-save after relevel)");

            _wallClock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                DataDir = System.IO.Path.Combine(Application.persistentDataPath, "FHRelevel");
                System.IO.Directory.CreateDirectory(DataDir);
            }
            catch { DataDir = null; }

            RegisterDrivers();
            StartHeartbeat();
            _log.LogInfo($"[{PluginName}] v{PluginVersion} loaded — uGUI 覆盖层 + 事件驱动；按住 {_hotkey.Value}/F9 半秒开窗口；新卡牌界面底部出现重置按钮");
            Probe($"v{PluginVersion} Awake 完成 pid={System.Diagnostics.Process.GetCurrentProcess().Id}");
        }

        // ---------- 主线程回调通道（已验证可用） ----------

        private static void RegisterDrivers()
        {
            try
            {
                SceneManager.sceneLoaded += (scene, mode) =>
                {
                    try
                    {
                        Probe($"sceneLoaded: {scene.name}");
                        Housekeeping(true);
                    }
                    catch { }
                };
            }
            catch (Exception e) { if (!ReferenceEquals(Instance, null)) Instance._log.LogWarning("sceneLoaded 钩子失败: " + e.Message); }
            try
            {
                Canvas.preWillRenderCanvases += OnCanvasRender;
            }
            catch (Exception e) { if (!ReferenceEquals(Instance, null)) Instance._log.LogWarning("Canvas 事件钩子失败: " + e.Message); }
            try
            {
                SceneManager.activeSceneChanged += (a, b) =>
                {
                    try
                    {
                        Instance._log.LogInfo($"[诊断] 场景切换: {a.name} -> {b.name}");
                        Probe($"场景切换: {a.name} -> {b.name}");
                        try { Canvas.preWillRenderCanvases -= OnCanvasRender; Canvas.preWillRenderCanvases += OnCanvasRender; } catch { }
                        // 引擎可能在场景装载时清掉静态事件订阅，重新挂一次日志钩子
                        try { Application.logMessageReceived -= OnGameLog; } catch { }
                        Application.logMessageReceived += OnGameLog;
                        Housekeeping(true);
                    }
                    catch { }
                };
            }
            catch (Exception e) { if (!ReferenceEquals(Instance, null)) Instance._log.LogWarning("场景钩子失败: " + e.Message); }
            try { Application.logMessageReceived += OnGameLog; }
            catch (Exception e) { if (!ReferenceEquals(Instance, null)) Instance._log.LogWarning("日志钩子失败: " + e.Message); }
        }

        private static void OnGameLog(string cond, string stack, LogType type)
        {
            try { Housekeeping(false); } catch { }
        }

        private static bool _loggedCanvasEvent;
        private static float _nextCanvasPump;
        private static float _nextCanvasErrProbe;
        private static float _nextCsProbe;
        private static float _nextReqCheck;

        private static float _canvasEventCount;
        private static bool _deepDiagnosed;
        private static float _nextPumpProbe;
        private static bool _probedChoreoReady;

        private static void OnCanvasRender()
        {
            try
            {
                _canvasEventCount++;
                if (!_loggedCanvasEvent)
                {
                    _loggedCanvasEvent = true;
                    Probe("Canvas 渲染事件首次触发（每帧事件可用）");
                }
                var t = Time.realtimeSinceStartup;
                if (t >= _nextPumpProbe)
                {
                    _nextPumpProbe = t + 10f;
                    var chx = Choreo();
                    var gsx = Campaign();
                    if (chx == null && !_deepDiagnosed)
                    {
                        _deepDiagnosed = true;
                        DeepDiagnose();
                    }
                    bool kb = false;
                    try { kb = Keyboard.current != null; } catch { }
                    if (chx != null && !_probedChoreoReady)
                    {
                        _probedChoreoReady = true;
                        Probe("Choreo 就绪（每帧泵视角）");
                    }
                    bool inScen = FHRelevelPlugin.InScenario(gsx);
                    Probe($"泵心跳 事件数={(_canvasEventCount > 100000 ? 99999 : (int)_canvasEventCount)} Choreo={(chx != null ? "有" : "无")} 战役={(gsx != null ? "有" : "无")} 场景中={(inScen ? "是" : "否")} 键盘={(kb ? "有" : "无")} 面板={(RelevelUi.Instance != null ? (RelevelUi.Instance.PanelOpen ? "开" : "关") : "未建")}");
                }
                if (!ReferenceEquals(Instance, null))
                {
                    if (t >= _nextCsProbe)
                    {
                        _nextCsProbe = t + 10f;
                        Probe("CS: 调用点到达");
                    }
                    try { Housekeeping(false); }
                    catch (Exception e2) { Probe("CS: Housekeeping 抛出 " + e2.GetType().Name + " " + e2.Message); }
                }
                if (t >= _nextReqCheck)
                {
                    _nextReqCheck = t + 0.5f;
                    try
                    {
                        var reqDir = DataDir;
                        if (reqDir != null && System.IO.File.Exists(System.IO.Path.Combine(reqDir, "panel.request")))
                        {
                            System.IO.File.Delete(System.IO.Path.Combine(reqDir, "panel.request"));
                            if (RelevelUi.Instance != null) RelevelUi.Instance.ToggleForTest();
                            Probe("panel.request 已处理");
                        }
                        if (reqDir != null && System.IO.File.Exists(System.IO.Path.Combine(reqDir, "uidump.request")))
                        {
                            System.IO.File.Delete(System.IO.Path.Combine(reqDir, "uidump.request"));
                            DumpUi();
                        }
                    }
                    catch { }
                }
            }
            catch (Exception e)
            {
                if (!_loggedCanvasEvent) { _loggedCanvasEvent = true; Probe("Canvas 事件异常(疑非主线程): " + e.Message); }
                try
                {
                    var t2 = Time.realtimeSinceStartup;
                    if (t2 >= _nextCanvasErrProbe)
                    {
                        _nextCanvasErrProbe = t2 + 10f;
                        Probe("Canvas泵异常: " + e.GetType().Name + " " + e.Message);
                    }
                } catch { }
            }
        }

        private static System.Collections.IEnumerator DriverCoroutine()
        {
            float nextTick = 0f;
            while (true)
            {
                yield return null;
                try
                {
                    if (!ReferenceEquals(Instance, null))
                    {
                        var ch = Choreo();
                        if (ch != null && _driverHost == ch)
                        {
                            if (!_loggedCampaignReady)
                            {
                                _loggedCampaignReady = true;
                                Probe("战役状态已可用（协程视角）");
                            }
                            RelevelUi.EnsureUi();
                            var now = Time.realtimeSinceStartup;
                            if (now >= nextTick)
                            {
                                nextTick = now + 0.1f;
                                RelevelUi.Instance.Tick(now);
                            }
                        }
                    }
                }
                catch (Exception e) { Probe("协程异常: " + e.Message); }
            }
        }

        /// <summary>主线程核心泵：UI 构建/自愈、按键轮询、界面检测、状态刷新（≤2Hz + 按需强制）</summary>
        private static void Housekeeping(bool force)
        {
            if (ReferenceEquals(Instance, null)) return;
            var t = Time.realtimeSinceStartup;
            if (t >= _nextHkExit)
            {
                _nextHkExit = t + 10f;
                Probe("HK-enter");
            }
            if (t >= _nextHkProbe)
            {
                _nextHkProbe = t + 10f;
                Probe("HK存活 #" + DriverTickCount);
            }
            if (!force && t < _nextHousekeep) return;
            _nextHousekeep = t + 0.5f;
            DriverTickCount++;
            LastDriverTickWall = _wallClock.Elapsed.TotalSeconds;

            if (!_loggedFirstHousekeep)
            {
                _loggedFirstHousekeep = true;
                Instance._log.LogInfo("[诊断] Housekeeping 首次运行：主线程事件驱动通道确认可用");
                Probe("Housekeeping 首次运行");
            }

            // 协程驱动：挂在游戏自己的 FHMapChoreographer 上（每帧主线程回调，游戏不会杀自己的对象）
            try
            {
                var ch0 = Choreo();
                if (ch0 == null)
                {
                    if (!_probedChoreoNull) { _probedChoreoNull = true; Probe("Choreo 未就绪（场景加载中，等待 sceneLoaded 重试）"); }
                }
                else if (_driverHost == null || _driverHost != ch0)
                {
                    _driverHost = ch0;
                    ch0.StartCoroutine(DriverCoroutine());
                    Probe("协程驱动已启动（宿主=FHMapChoreographer）");
                }
            }
            catch (Exception e) { Probe("协程启动失败: " + e.Message); }

            try
            {
                var ch = Choreo();
                bool online = FFSNetwork.IsOnline, isClient = FFSNetwork.IsClient, isHost = FFSNetwork.IsHost;
                bool hasServer = ch != null && ch.GameServer != null;
                int players = 0;
                try { players = FFSNet.PlayerRegistry.AllPlayers.Count; } catch { }
                string mp = $"MP状态: IsOnline={online} IsClient={isClient} IsHost={isHost} GameServer={hasServer} 玩家数={players}";
                if (mp != _lastMpLine)
                {
                    _lastMpLine = mp;
                    Probe(mp);
                }
            }
            catch { }

            try
            {
                RelevelUi.EnsureUi();
                RelevelUi.Instance.Tick(t);
            }
            catch (Exception e)
            {
                Probe("HK-UI异常: " + e.GetType().Name + " " + e.Message);
                Instance._log.LogError("Housekeeping: " + e);
            }
            if (t >= _nextHkExit2)
            {
                _nextHkExit2 = t + 10f;
                Probe("HK-exit");
            }
        }

        // ---------- 纯 .NET 线程心跳（不触碰 Unity API） ----------

        private static void StartHeartbeat()
        {
            if (_heartbeatThread != null) return;
            _heartbeatThread = new Thread(() =>
            {
                int beat = 0;
                while (true)
                {
                    try { Thread.Sleep(15000); } catch { return; }
                    beat++;
                    var log = !ReferenceEquals(Instance, null) ? Instance._log : null;
                    if (log == null) continue;
                    try
                    {
                        double sinceDriver = _wallClock.Elapsed.TotalSeconds - LastDriverTickWall;
                        if (beat <= 8 || beat % 4 == 0)
                            Probe($"心跳#{beat} 程序集存活；主线程驱动距上次={sinceDriver:F0}s({(sinceDriver < 30 ? "活跃" : "停跳")}) 驱动次数={DriverTickCount}");
                    }
                    catch { }
                }
            })
            { IsBackground = true, Name = "FHRelevelHeartbeat" };
            _heartbeatThread.Start();
        }

        // ---------- 游戏状态访问 ----------

        internal static FHMapChoreographer Choreo()
        {
            // InstanceFast：找不到时静默返回 null（Instance 会刷游戏警告+全场景扫描）
            try { return Singleton<FHMapChoreographer>.InstanceFast; }
            catch (Exception e)
            {
                if (!_probedChoreoEx) { _probedChoreoEx = true; Probe("Choreo 异常: " + e.GetType().Name + " " + e.Message); }
                return null;
            }
        }
        private static bool _probedChoreoEx;

        internal static void DumpUi()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("=== UI DUMP " + DateTime.Now.ToString("HH:mm:ss.fff") + " res=" + Screen.width + "x" + Screen.height + " ===");
                var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
                int ci = 0;
                foreach (var cv in canvases)
                {
                    bool act = cv.gameObject.activeInHierarchy;
                    ci++;
                    sb.AppendLine("[Canvas#" + ci + " name=" + cv.name + " sort=" + cv.sortingOrder + " overlay=" + (cv.renderMode == RenderMode.ScreenSpaceOverlay) + " active=" + act + "]");
                    if (!act) continue;
                    var cam = (cv.renderMode == RenderMode.ScreenSpaceOverlay) ? null : cv.worldCamera;
                    if (cv.name == "FHRelevelUI")
                    {
                        var all = cv.GetComponentsInChildren<Transform>(true);
                        foreach (var tr in all)
                        {
                            var rr = tr as RectTransform;
                            if (rr == null) continue;
                            Vector2 pp;
                            try { pp = RectTransformUtility.WorldToScreenPoint(null, rr.position); } catch { continue; }
                            sb.AppendLine("  SELF \"" + tr.name + "\" activeSelf=" + tr.gameObject.activeSelf + " activeInH=" + tr.gameObject.activeInHierarchy + " @(" + pp.x.ToString("F0") + "," + pp.y.ToString("F0") + ") size=(" + rr.rect.width.ToString("F0") + "x" + rr.rect.height.ToString("F0") + ") scale=" + rr.localScale.ToString("F2") + " enabled=" + tr.gameObject.activeSelf);
                        }
                        var cvEnabled = cv.enabled;
                        sb.AppendLine("  CANVAS enabled=" + cvEnabled + " sort=" + cv.sortingOrder + " planeDist=" + cv.planeDistance);
                    }
                    var texts = cv.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true);
                    foreach (var tx in texts)
                    {
                        if (tx == null || !tx.gameObject.activeInHierarchy || string.IsNullOrEmpty(tx.text)) continue;
                        var rt = tx.rectTransform;
                        Vector2 sp;
                        try { sp = RectTransformUtility.WorldToScreenPoint(cam, rt.position); } catch { continue; }
                        string txt = tx.text.Trim().Replace("\r", " ").Replace("\n", " ");
                        sb.AppendLine("  TXT \"" + txt + "\" @(" + sp.x.ToString("F0") + "," + sp.y.ToString("F0") + ") size=(" + rt.rect.width.ToString("F0") + "x" + rt.rect.height.ToString("F0") + ")");
                    }
                    var buttons = cv.GetComponentsInChildren<UnityEngine.UI.Button>(true);
                    foreach (var b in buttons)
                    {
                        if (b == null || !b.gameObject.activeInHierarchy) continue;
                        var rt = (RectTransform)b.transform;
                        Vector2 sp;
                        try { sp = RectTransformUtility.WorldToScreenPoint(cam, rt.position); } catch { continue; }
                        string label = "";
                        try
                        {
                            var t2 = b.GetComponentInChildren<TMPro.TextMeshProUGUI>(true);
                            if (t2 != null) label = t2.text.Trim().Replace("\r", " ").Replace("\n", " ");
                            else { var t3 = b.GetComponentInChildren<UnityEngine.UI.Text>(true); if (t3 != null) label = t3.text.Trim(); }
                        }
                        catch { }
                        sb.AppendLine("  BTN \"" + label + "\" @(" + sp.x.ToString("F0") + "," + sp.y.ToString("F0") + ") size=(" + rt.rect.width.ToString("F0") + "x" + rt.rect.height.ToString("F0") + ") interactable=" + b.interactable);
                    }
                }
                sb.AppendLine("=== END ===");
                var dumpPath = DataDir != null ? System.IO.Path.Combine(DataDir, "uidump.txt") : null;
                if (dumpPath != null) System.IO.File.WriteAllText(dumpPath, sb.ToString());
                Probe("UI 导出完成");
            }
            catch (Exception e) { Probe("DumpUi 异常: " + e.Message); }
        }

        internal static void DeepDiagnose()
        {
            try
            {
                var asms = AppDomain.CurrentDomain.GetAssemblies();
                int count = 0;
                foreach (var a in asms)
                {
                    if (a.GetName().Name == "Assembly-CSharp")
                    {
                        count++;
                        try { Probe($"Assembly-CSharp #{count}: Location={a.Location} 从GAC={a.GlobalAssemblyCache}"); }
                        catch { Probe($"Assembly-CSharp #{count}: Location=不可得"); }
                    }
                }
                Probe($"同名 Assembly-CSharp 数量: {count}");
                var objs = UnityEngine.Object.FindObjectsOfType(typeof(FHMapChoreographer));
                Probe($"FindObjectsOfType(FHMapChoreographer 编译期类型)={objs.Length}");
                if (count > 1 && objs.Length == 0)
                {
                    foreach (var a in asms)
                    {
                        if (a.GetName().Name != "Assembly-CSharp") continue;
                        var t2 = a.GetType("FHMapChoreographer", false);
                        if (t2 == null) continue;
                        var objs2 = UnityEngine.Object.FindObjectsOfType(t2);
                        Probe($"按运行时类型 {t2.Assembly.Location} 找到 {objs2.Length} 个实例");
                    }
                }
            }
            catch (Exception e) { Probe("DeepDiagnose 异常: " + e.Message); }
        }

        internal static CampaignState Campaign()
        {
            var ch = Choreo();
            if (ch == null) return null;
            try { return ch.GameState; }
            catch { return null; }
        }

        /// <summary>是否为联机客户端（加入别人房间的玩家）——客户端禁用，房主可用</summary>
        internal static bool IsMpClient()
        {
            try { return FFSNetwork.IsClient; } catch { return false; }
        }

        /// <summary>联机房主：重置后把新战役状态广播给所有客户端（游戏自带 net_sync_state 同款机制）</summary>
        internal static void SyncMpState(FHMapChoreographer ch)
        {
            try
            {
                if (ch == null || ch.GameServer == null) return; // 单人或客户端无需同步
                ch.GameServer.ReplaceState(ch.GameClient.GameState);
                var mi = typeof(FHMapChoreographer).GetMethod("SendGameState",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (mi != null) mi.Invoke(ch, null);
                LogInfo("[MP] 已将重置后的战役状态广播给全部联机玩家");
            }
            catch (Exception e) { LogWarn("联机同步失败: " + e.Message); }
        }

        internal static bool InScenario(CampaignState gs)
        {
            return gs != null && gs.ActiveScenario != null && gs.ActiveScenario.HasEnteredScenario;
        }

        // ---------- 重新升级核心 ----------

        internal class RefundInfo
        {
            public string Name;
            public uint OldLevel;
            public uint NewLevel;
            public int CardsRefunded;
            public uint PerkMarksRefunded;
            public bool AlreadyClean;
        }

        internal static void CountRefundables(FHMapChoreographer ch, CharacterState c, out List<ClassCardDef> highCards, out uint perkCost)
        {
            var defRepo = ch.DefRepo;
            highCards = new List<ClassCardDef>();
            var seen = new HashSet<uint>();
            foreach (var cardId in c.UnusedCardIDs.Concat(c.SelectedCardIDs))
            {
                ClassCardDef def;
                if (defRepo.ClassCards.TryGetValue(cardId, out def) && def.Level > 1u && seen.Add(def.ID.ID))
                    highCards.Add(def);
            }
            perkCost = 0u;
            foreach (var perkId in c.PerkIDs)
            {
                PerkDef perkDef;
                if (defRepo.Perks.TryGetValue(perkId, out perkDef))
                    perkCost += (uint)Math.Max(0, perkDef.Cost);
            }
        }

        internal static RefundInfo ApplyRelevel(FHMapChoreographer ch, CharacterState c)
        {
            var info = new RefundInfo { Name = c.Name, OldLevel = c.Level };

            List<ClassCardDef> highCards;
            uint perkRefund;
            CountRefundables(ch, c, out highCards, out perkRefund);

            info.AlreadyClean = highCards.Count == 0 && c.PerkIDs.Count == 0;
            if (info.AlreadyClean)
            {
                info.NewLevel = c.Level;
                return info;
            }

            foreach (var def in highCards)
            {
                c.UnusedCardIDs.RemoveAll(id => id == def.ID);
                c.SelectedCardIDs.RemoveAll(id => id == def.ID);
            }

            c.PerkIDs.Clear();
            if (perkRefund > 0u) c.PerkMarks += perkRefund;
            c.CardUnlocks += (uint)highCards.Count;

            var cc = ch.GameController.CharacterController;
            c.Level = 1u;
            while (cc.HasExperienceForLevelUp(c) && !cc.IsMaxLevel(c))
                c.Level++;
            info.NewLevel = c.Level;

            try { ch.RecalcAttackModifierDeck(c); }
            catch (Exception e) { LogWarn("RecalcAttackModifierDeck 失败: " + e.Message); }

            try
            {
                var mi = typeof(CharacterController).GetMethod("Action_EnsureSmallItemLimit",
                    BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(CharacterState) }, null);
                if (mi != null) mi.Invoke(cc, new object[] { c });
            }
            catch (Exception e) { LogWarn("EnsureSmallItemLimit 失败: " + e.Message); }

            info.CardsRefunded = highCards.Count;
            info.PerkMarksRefunded = perkRefund;
            return info;
        }

        internal static async void SaveNow()
        {
            try
            {
                var sd = SaveData.Instance;
                if (sd == null) return;
                await sd.AutoSaveCurrentAdventureData();
                if (!ReferenceEquals(Instance, null)) Instance._log.LogInfo("[FHRelevel] 自动存档完成");
            }
            catch (Exception e)
            {
                if (!ReferenceEquals(Instance, null)) Instance._log.LogWarning("自动存档失败（可手动存档兜底）: " + e.Message);
            }
        }

        internal static void LogInfo(string msg) { if (!ReferenceEquals(Instance, null)) Instance._log.LogInfo(msg); Probe(msg); }
        internal static void LogWarn(string msg) { if (!ReferenceEquals(Instance, null)) Instance._log.LogWarning(msg); Probe("[WARN] " + msg); }

        /// <summary>独立文件探针：绕开 BepInEx/Unity 日志管线，任何线程可用</summary>
        private static readonly object _probeLock = new object();

        internal static void Probe(string msg)
        {
            try
            {
                lock (_probeLock)
                {
                    var dir = DataDir;
                    if (dir == null) return;
                    System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "probe.log"),
                        DateTime.Now.ToString("HH:mm:ss.fff") + " [" + (System.Threading.Thread.CurrentThread.Name ?? "main") + "] " + msg + System.Environment.NewLine);
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// uGUI 覆盖层 UI：屏幕底部重置按钮（新卡牌界面打开时）+ 可开关的角色列表面板。
    /// 由 Housekeeping（主线程事件驱动）构建与刷新，Canvas 挂在常驻 GameObject 上。
    /// </summary>
    internal class RelevelUi
    {
        internal static RelevelUi Instance;

        private GameObject _root;
        private Canvas _canvas;
        private Font _font;
        private GameObject _cardScreenBtn;
        private GameObject _panel;
        private GameObject _panelContent;
        private UnityEngine.UI.Text _panelStatus;
        private bool _panelOpen;
        internal bool PanelOpen { get { return _panelOpen; } }
        private bool _hotPrev, _f9Prev;
        private bool _autoShownOnce;
        private float _autoShowUntil = -1f;
        private NewCardsListModule _newCardsModule;
        private float _lastCardsVisible = -999f;
        private float _nextModuleScan;
        private string _lastStatus = "";

        internal void ToggleForTest()
        {
            _panelOpen = !_panelOpen;
            _autoShowUntil = -1f;
            FHRelevelPlugin.Probe("测试通道切换面板 -> " + (_panelOpen ? "开" : "关"));
        }

        private bool _cameraAttached;

        private void EnsureCamera()
        {
            if (_cameraAttached || _canvas == null) return;
            try
            {
                var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
                foreach (var cv in canvases)
                {
                    if (cv == _canvas || !cv.gameObject.activeInHierarchy) continue;
                    if (cv.renderMode == RenderMode.ScreenSpaceCamera && cv.worldCamera != null)
                    {
                        _canvas.renderMode = RenderMode.ScreenSpaceCamera;
                        _canvas.worldCamera = cv.worldCamera;
                        _canvas.planeDistance = Mathf.Max(1f, cv.planeDistance - 5f);
                        _canvas.sortingOrder = 30000;
                        _root.layer = cv.gameObject.layer;
                        _cameraAttached = true;
                        FHRelevelPlugin.Probe("UI 已挂接游戏相机: " + cv.worldCamera.name + " plane=" + _canvas.planeDistance);
                        return;
                    }
                }
            }
            catch (Exception e) { FHRelevelPlugin.Probe("EnsureCamera 失败: " + e.Message); }
        }

        internal static void EnsureUi()
        {
            if (Instance != null && Instance._root != null)
            {
                if (!Instance._root.activeSelf)
                {
                    try { Instance._root.SetActive(true); FHRelevelPlugin.Probe("覆盖层被失活，已重新激活"); } catch { }
                }
                return;
            }
            if (Instance == null) Instance = new RelevelUi();
            Instance.Build();
        }

        private void Build()
        {
            try
            {
                if (_root != null) return;
                _root = new GameObject("FHRelevelUI");
                UnityEngine.Object.DontDestroyOnLoad(_root);
                _canvas = _root.AddComponent<Canvas>();
                _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                _canvas.sortingOrder = 30000;
                var scaler = _root.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                _root.AddComponent<GraphicRaycaster>();
                _cameraAttached = false;

                try
                {
                    _font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei" }, 24);
                }
                catch (Exception e)
                {
                    FHRelevelPlugin.LogWarn("系统中文字体加载失败: " + e.Message);
                    _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                }

                _cardScreenBtn = MakeButton(_root.transform, "↺ 重新升级",
                    new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-150f, -38f), new Vector2(260f, 42f), OnCardResetClick);
                _cardScreenBtn.SetActive(false);

                BuildPanel();
                FHRelevelPlugin.LogInfo("[诊断] uGUI 覆盖层构建完成");
            }
            catch (Exception e)
            {
                FHRelevelPlugin.LogWarn("uGUI 构建失败: " + e.Message);
            }
        }

        private void BuildPanel()
        {
            _panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            var prt = (RectTransform)_panel.transform;
            prt.SetParent(_root.transform, false);
            prt.anchorMin = prt.anchorMax = new Vector2(0f, 0.5f);
            prt.pivot = new Vector2(0f, 0.5f);
            prt.anchoredPosition = new Vector2(16f, 0f);
            prt.sizeDelta = new Vector2(470f, 640f);
            _panel.GetComponent<Image>().color = new Color(0.06f, 0.08f, 0.12f, 0.94f);

            var title = MakeText(_panel.transform, "霜港迷城 · 重新升级修改器", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -6f), new Vector2(-140f, 36f), 19, FontStyle.Bold, TextAnchor.MiddleLeft);
            ((RectTransform)title.transform).offsetMin = new Vector2(12f, -42f);
            ((RectTransform)title.transform).offsetMax = new Vector2(-130f, -6f);

            var close = MakeButton(_panel.transform, "✕", new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-6f, -6f), new Vector2(0f, 0f), delegate { _panelOpen = false; });
            ((RectTransform)close.transform).offsetMin = new Vector2(-42f, -40f);
            ((RectTransform)close.transform).offsetMax = new Vector2(-6f, -6f);

            var contentGo = new GameObject("Content", typeof(RectTransform));
            var crt = (RectTransform)contentGo.transform;
            crt.SetParent(_panel.transform, false);
            crt.anchorMin = new Vector2(0f, 0f);
            crt.anchorMax = new Vector2(1f, 1f);
            crt.offsetMin = new Vector2(8f, 52f);
            crt.offsetMax = new Vector2(-8f, -48f);
            _panelContent = contentGo;

            _panelStatus = MakeText(_panel.transform, " ", new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 6f), new Vector2(0f, 44f), 14, FontStyle.Normal, TextAnchor.MiddleLeft);
            ((RectTransform)_panelStatus.transform).offsetMin = new Vector2(10f, 6f);
            ((RectTransform)_panelStatus.transform).offsetMax = new Vector2(-10f, 50f);

            _panel.SetActive(false);
        }

        // ---------- 每次驱动刷新 ----------

        internal void Tick(float t)
        {
            if (_root == null) { Build(); if (_root == null) return; }
            if (!_cameraAttached) EnsureCamera();

            // 按键（按住半秒，边沿触发）
            try
            {
                if (Keyboard.current != null)
                {
                    var hk = FHRelevelPlugin.Instance._hotkey != null ? FHRelevelPlugin.Instance._hotkey.Value : Key.F8;
                    bool held = Keyboard.current[hk].isPressed || Keyboard.current[Key.F8].isPressed;
                    bool heldF9 = Keyboard.current[Key.F9].isPressed;
                    bool edge = (held && !_hotPrev) || (heldF9 && !_f9Prev);
                    _hotPrev = held;
                    _f9Prev = heldF9;
                    if (edge)
                    {
                        _panelOpen = !_panelOpen;
                        _autoShowUntil = -1f;
                        FHRelevelPlugin.LogInfo($"[诊断] 热键触发，面板={(_panelOpen ? "开" : "关")}");
                    }
                }
            }
            catch (Exception e) { FHRelevelPlugin.LogWarn("按键检测: " + e.Message); }

            var ch = FHRelevelPlugin.Choreo();
            var gs = FHRelevelPlugin.Campaign();

            // 进战役自动展示一次
            if (!_autoShownOnce && gs != null && !FHRelevelPlugin.IsMpClient())
            {
                _autoShownOnce = true;
                _panelOpen = true;
                _autoShowUntil = t + 15f;
                SetStatus("自动展示：15 秒后收起 · 按住 F8/F9 半秒可开关");
                FHRelevelPlugin.LogInfo("[诊断] 已进入战役，自动展示面板 15 秒");
            }
            if (_autoShowUntil > 0f && t > _autoShowUntil)
            {
                _autoShowUntil = -1f;
                _panelOpen = false;
            }

            // 新卡牌界面检测（轮询模块可见性，不依赖 Harmony）
            bool cardsVisible = false;
            try
            {
                // 性能关键：FindObjectOfType 全场景扫描，限频 2 秒一次，且战斗中该界面必然不存在、直接跳过
                if (_newCardsModule == null && t >= _nextModuleScan && !FHRelevelPlugin.InScenario(gs))
                {
                    _nextModuleScan = t + 5f;
                    _newCardsModule = UnityEngine.Object.FindObjectOfType<NewCardsListModule>();
                }
                if (_newCardsModule != null)
                {
                    cardsVisible = _newCardsModule.gameObject.activeInHierarchy;
                    if (cardsVisible) _lastCardsVisible = t;
                }
            }
            catch (Exception e) { FHRelevelPlugin.LogWarn("界面检测: " + e.Message); }

            bool btnShow = cardsVisible
                           && gs != null
                           && !FHRelevelPlugin.IsMpClient()
                           && !FHRelevelPlugin.InScenario(gs);
            if (_cardScreenBtn != null && _cardScreenBtn.activeSelf != btnShow)
            {
                _cardScreenBtn.SetActive(btnShow);
                if (btnShow) FHRelevelPlugin.LogInfo("[FHRelevel] 检测到新卡牌界面，底部重置按钮已显示");
            }

            // 面板
            if (_panel != null)
            {
                if (_panel.activeSelf != _panelOpen)
                {
                    _panel.SetActive(_panelOpen);
                    if (_panelOpen) RebuildPanelRows(ch, gs);
                }
                if (_panelOpen && gs != null && _lastStatus != null && _panelStatus != null && _panelStatus.text != _lastStatus)
                    _panelStatus.text = _lastStatus;
            }
        }

        private void SetStatus(string s)
        {
            _lastStatus = s;
            if (_panelStatus != null) _panelStatus.text = s;
        }

        private void RebuildPanelRows(FHMapChoreographer ch, CampaignState gs)
        {
            if (_panelContent == null) return;
            for (int i = _panelContent.transform.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_panelContent.transform.GetChild(i).gameObject);

            if (gs == null)
            {
                MakeText(_panelContent.transform, "未检测到战役状态，请进入战役后按住 F8/F9。", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 0f), new Vector2(0f, 60f), 15, FontStyle.Normal, TextAnchor.MiddleLeft);
                return;
            }
            if (FHRelevelPlugin.IsMpClient())
            {
                MakeText(_panelContent.transform, "当前是联机客户端（加入了别人的房间）。\n重新升级需由房主操作：请房主按住 F8 打开面板，\n选你的角色点「重新升级」，你这边会自动同步看到效果。", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 0f), new Vector2(0f, 120f), 15, FontStyle.Normal, TextAnchor.UpperLeft);
                return;
            }

            var chars = gs.AllCharacters
                .Where(x => x.PartyState == CharacterPartyStateType.InParty || x.PartyState == CharacterPartyStateType.Removed)
                .ToList();

            float y = 0f;
            foreach (var c in chars)
            {
                List<ClassCardDef> high;
                uint cost;
                FHRelevelPlugin.CountRefundables(ch, c, out high, out cost);
                string cls;
                try { cls = c.ClassDef != null ? c.ClassDef.Name : c.ClassID.ID; } catch { cls = c.ClassID.ID; }
                string label = $"{c.Name} · {cls}\nLv{c.Level} · XP {c.Experience} · 高级卡 {high.Count} · 专精 {c.PerkIDs.Count}";

                var row = new GameObject("Row", typeof(RectTransform), typeof(Image));
                var rrt = (RectTransform)row.transform;
                rrt.SetParent(_panelContent.transform, false);
                rrt.anchorMin = new Vector2(0f, 1f);
                rrt.anchorMax = new Vector2(1f, 1f);
                rrt.pivot = new Vector2(0.5f, 1f);
                rrt.anchoredPosition = new Vector2(0f, -y);
                rrt.sizeDelta = new Vector2(0f, 64f);
                row.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.06f);

                var txt = MakeText(row.transform, label, new Vector2(0f, 0f), new Vector2(1f, 1f), Vector2.zero, Vector2.zero, 14, FontStyle.Normal, TextAnchor.MiddleLeft);
                ((RectTransform)txt.transform).offsetMin = new Vector2(10f, 4f);
                ((RectTransform)txt.transform).offsetMax = new Vector2(-110f, -4f);

                var captured = c;
                var btn = MakeButton(row.transform, "重新升级", new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-6f, 0f), new Vector2(92f, 46f), delegate { OnPanelRelevel(captured); });
                ((RectTransform)btn.transform).offsetMin = new Vector2(-98f, -23f);
                ((RectTransform)btn.transform).offsetMax = new Vector2(-6f, 23f);

                y += 70f;
            }

            if (chars.Count == 0)
                MakeText(_panelContent.transform, "（没有队伍/名册角色）", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 0f), new Vector2(0f, 40f), 14, FontStyle.Normal, TextAnchor.MiddleLeft);
        }

        // ---------- 点击处理 ----------

        private void OnPanelRelevel(CharacterState c)
        {
            try
            {
                var ch = FHRelevelPlugin.Choreo();
                var gs = FHRelevelPlugin.Campaign();
                if (ch == null || gs == null || FHRelevelPlugin.IsMpClient() || FHRelevelPlugin.InScenario(gs))
                {
                    SetStatus("当前状态不可用（战役/联机/场景限制）");
                    return;
                }
                var info = FHRelevelPlugin.ApplyRelevel(ch, c);
                SetStatus(info.AlreadyClean
                    ? $"{info.Name}：没有可重置的内容"
                    : $"{info.Name}：Lv{info.OldLevel} → Lv{info.NewLevel}，返还 {info.CardsRefunded} 选卡点 + {info.PerkMarksRefunded} 专精点");
                FHRelevelPlugin.LogInfo("[面板] " + _lastStatus);
                if (!info.AlreadyClean)
                {
                    try { ch.View?.RefreshView(); } catch { }
                    FHRelevelPlugin.SyncMpState(ch);
                    RebuildPanelRows(ch, gs);
                    if (FHRelevelPlugin.Instance.AutoSaveEnabled) FHRelevelPlugin.SaveNow();
                }
            }
            catch (Exception e)
            {
                FHRelevelPlugin.LogWarn("面板重置失败: " + e);
                SetStatus("出错: " + e.Message);
            }
        }

        private void OnCardResetClick()
        {
            try
            {
                var ch = FHRelevelPlugin.Choreo();
                var gs = FHRelevelPlugin.Campaign();
                if (ch == null || gs == null) return;
                if (FHRelevelPlugin.IsMpClient())
                {
                    ShowGameModal(ch, "联机中：重新升级需由房主操作。\n请房主在其电脑上按住 F8 打开面板执行。");
                    return;
                }
                if (FHRelevelPlugin.InScenario(gs))
                {
                    ShowGameModal(ch, "正在场景（战斗）中，无法重新升级。请回到前哨后再试。");
                    return;
                }

                CharacterState c = null;
                try { c = ch.View != null ? ch.View.SelectedCharacter : null; } catch { }
                if (c == null)
                {
                    ShowGameModal(ch, "未能定位当前角色，请先在左侧选中角色再打开新卡牌界面。");
                    return;
                }

                List<ClassCardDef> highCards;
                uint perkCost;
                FHRelevelPlugin.CountRefundables(ch, c, out highCards, out perkCost);

                if (highCards.Count == 0 && c.PerkIDs.Count == 0)
                {
                    ShowGameModal(ch, $"【{c.Name}】没有可重置的内容：\n没有 2 级以上卡牌，也没有已学专精。");
                    return;
                }

                string msg = $"确定要重新升级【{c.Name}】吗？\n\n" +
                             $"· 移除 {highCards.Count} 张 2 级以上卡牌，返还全部选卡点\n" +
                             $"· 清空 {c.PerkIDs.Count} 个已学专精，返还 {perkCost} 点专精点\n\n" +
                             $"XP 与等级进度、装备、金钱、强化、精通、个人任务均保留。";

                var view = ch.View;
                view.PushState(new GenericModalViewState(msg, "重新升级", delegate (bool confirmed)
                {
                    view.PopState(FHMapViewLayer.LayerType.Modal);
                    if (!confirmed) return;
                    try
                    {
                        var info = FHRelevelPlugin.ApplyRelevel(ch, c);
                        FHRelevelPlugin.LogInfo($"[新卡牌按钮] {info.Name}: Lv{info.OldLevel}→Lv{info.NewLevel}, 返还 {info.CardsRefunded} 选卡点 + {info.PerkMarksRefunded} 专精点");
                        try { ch.View?.RefreshView(); } catch { }
                        FHRelevelPlugin.SyncMpState(ch);
                        FHRelevelPlugin.SaveNow();
                    }
                    catch (Exception e)
                    {
                        FHRelevelPlugin.LogWarn("重置失败: " + e);
                        ShowGameModal(ch, "重新升级出错: " + e.Message);
                    }
                }), FHMapViewLayer.LayerType.Modal);
            }
            catch (Exception e)
            {
                FHRelevelPlugin.LogWarn("按钮处理失败: " + e);
            }
        }

        private static void ShowGameModal(FHMapChoreographer ch, string text)
        {
            try
            {
                var view = ch.View;
                view.PushState(new GenericModalViewState(text, "重新升级", delegate (bool b)
                {
                    view.PopState(FHMapViewLayer.LayerType.Modal);
                }), FHMapViewLayer.LayerType.Modal);
            }
            catch (Exception e) { FHRelevelPlugin.LogWarn("弹窗失败: " + e.Message); }
        }

        // ---------- uGUI 工厂 ----------

        private GameObject MakeButton(Transform parent, string label, Vector2 anchorMin, Vector2 anchorMax, Vector2 pos, Vector2 size, UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject("Btn_" + label.GetHashCode(), typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            go.GetComponent<Image>().color = new Color(0.10f, 0.30f, 0.52f, 0.96f);
            var btn = go.GetComponent<Button>();
            btn.targetGraphic = go.GetComponent<Image>();
            if (onClick != null) btn.onClick.AddListener(onClick);

            var txt = MakeText(go.transform, label, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, 16, FontStyle.Bold, TextAnchor.MiddleCenter);
            ((RectTransform)txt.transform).offsetMin = new Vector2(6f, 2f);
            ((RectTransform)txt.transform).offsetMax = new Vector2(-6f, -2f);
            return go;
        }

        private UnityEngine.UI.Text MakeText(Transform parent, string label, Vector2 anchorMin, Vector2 anchorMax, Vector2 pos, Vector2 size, int fontSize, FontStyle style, TextAnchor align)
        {
            var go = new GameObject("Txt", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Text));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            var txt = go.GetComponent<UnityEngine.UI.Text>();
            txt.font = _font;
            txt.text = label;
            txt.fontSize = fontSize;
            txt.fontStyle = style;
            txt.alignment = align;
            txt.color = Color.white;
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            txt.raycastTarget = false;
            return txt;
        }
    }
}
