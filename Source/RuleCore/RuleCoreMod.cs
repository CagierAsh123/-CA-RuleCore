using System;
using System.IO;
using UnityEngine;
using Verse;
using RimWorld;
using RuleCore.Core;

namespace RuleCore
{
    public class RuleCoreMod : Mod
    {
        public static RuleCoreSettings Settings;

        /// <summary>当前会话的落盘 sink。未启用文件输出时为 null。</summary>
        public static RuleLogFileSink LogSink;

        /// <summary>日志目录：模组根目录下的 log/，无论从开发目录还是 Mods 目录加载都落在一起。</summary>
        public static string LogDirectory;

        public RuleCoreMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<RuleCoreSettings>();
            Settings.Normalize();

            // Core 层不引用 Verse：tick、门控与诊断落点由宿主注入，保证 Core 可脱离游戏单测。
            RuleLog.EnabledProvider = () => Settings != null && Settings.enableLog;
            RuleLog.VerboseProvider = () => Settings != null && Settings.verboseLog;
            RuleLog.TickProvider = CurrentTick;
            RuleLog.RawLogSink = text => UnityEngine.Debug.Log(text);

            RuleLog.Boot(Settings.logCapacity);

            LogDirectory = Path.Combine(content.RootDir, "log");
            StartFileSink();

            // 词表是惰性的（加载顺序不保证），这里主动建一次：
            // 一是让"属性/谓词/主体绑定分别有多少"进启动日志，
            // 二是让重复键这种注册期错误在没有玩家操作的时候就暴露出来。
            RuleVocabularyCatalog.EnsureBuilt();

            // 这条会同时进内存缓冲和文件——先挂 sink 再记 Boot，落盘文件才有完整的开头。
            RuleLog.Info(null, "Boot", "Loaded", null, null,
                "RuleCore v" + ModVersion + " · 缓冲=" + RuleLog.Capacity + " · 日志=" + LogDirectory);

            Application.quitting += OnApplicationQuitting;
        }

        public static string ModVersion
        {
            get { return typeof(RuleCoreMod).Assembly.GetName().Version.ToString(); }
        }

        private static int CurrentTick()
        {
            var tickManager = Find.TickManager;
            return tickManager != null ? tickManager.TicksGame : 0;
        }

        // ── 落盘 ──────────────────────────────────────────────────────

        /// <summary>按设置启停文件输出。设置页切换开关时调用。</summary>
        public static void RestartFileSink()
        {
            StopFileSink();
            StartFileSink();
        }

        private static void StartFileSink()
        {
            if (Settings == null || !Settings.logToFile) return;

            var sink = new RuleLogFileSink(LogDirectory, Settings.logRetention,
                ModVersion, VersionFromGame());

            if (sink.Start())
            {
                LogSink = sink;
            }
            else
            {
                // 落盘失败不能影响游戏，但也不能装作没事——sink 自己已经把原因报进日志了。
                sink.Dispose();
                LogSink = null;
            }
        }

        private static void StopFileSink()
        {
            var sink = LogSink;
            LogSink = null;
            if (sink == null) return;
            sink.Dispose();   // 写页脚并关文件
        }

        private static void OnApplicationQuitting()
        {
            StopFileSink();
        }

        private static string VersionFromGame()
        {
            try { return VersionControl.CurrentVersionString; }
            catch { return "?"; }
        }

        // ── 设置页 ────────────────────────────────────────────────────

        public override string SettingsCategory()
        {
            return "指令核心 RuleCore";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            listing.CheckboxLabeled("RuleCore.EnableLog".Translate(), ref Settings.enableLog,
                "RuleCore.EnableLogDesc".Translate());
            listing.CheckboxLabeled("RuleCore.VerboseLog".Translate(), ref Settings.verboseLog,
                "RuleCore.VerboseLogDesc".Translate());

            listing.Gap();
            listing.Label("RuleCore.LogCapacity".Translate(RuleLog.Count, RuleLog.Capacity, RuleLog.Dropped));
            Settings.logCapacity = Mathf.RoundToInt(listing.Slider(Settings.logCapacity, 64f, 8192f));

            // 拖动滑块时不能每帧重建缓冲——只在明确点按后应用。
            if (listing.ButtonText("RuleCore.ApplyCapacity".Translate()))
            {
                RuleLog.Boot(Settings.logCapacity);
            }

            listing.GapLine();

            bool wasLoggingToFile = Settings.logToFile;
            listing.CheckboxLabeled("RuleCore.LogToFile".Translate(), ref Settings.logToFile,
                "RuleCore.LogToFileDesc".Translate());
            if (Settings.logToFile != wasLoggingToFile)
            {
                RestartFileSink();
            }

            listing.Label("RuleCore.LogDirectory".Translate(LogDirectory));

            string status;
            if (!Settings.logToFile)
            {
                status = "RuleCore.LogFileOff".Translate();
            }
            else if (LogSink == null)
            {
                status = "RuleCore.LogFileFailed".Translate();
            }
            else
            {
                status = "RuleCore.LogFileActive".Translate(LogSink.RowCount, Path.GetFileName(LogSink.CurrentPath ?? ""));
            }
            listing.Label(status);

            var buttons = listing.GetRect(30f);
            float halfWidth = buttons.width / 2f - 4f;
            if (Widgets.ButtonText(new Rect(buttons.x, buttons.y, halfWidth, buttons.height),
                    "RuleCore.OpenLogFolder".Translate()))
            {
                OpenLogFolder();
            }
            if (Widgets.ButtonText(new Rect(buttons.x + halfWidth + 8f, buttons.y, halfWidth, buttons.height),
                    "RuleCore.FlushLog".Translate()))
            {
                if (LogSink != null) LogSink.Flush();
            }

            listing.GapLine();

            listing.Label("RuleCore.LogRetention".Translate(Settings.logRetention));
            Settings.logRetention = Mathf.RoundToInt(listing.Slider(Settings.logRetention, 1f, 50f));

            // ── 开发者级操作 ──────────────────────────────────────────
            //
            // 放在**模组设置**里而不是"跟着原版开发者模式走"，是因为它管的不是调试，
            // 而是"规则能不能直接改世界状态"（改天气、触发事件）。
            // 玩家找不到它，就会去猜"是不是我规则写错了"。
            listing.GapLine();
            listing.CheckboxLabeled("RuleCore.AllowDeveloperOps".Translate(),
                ref Settings.allowDeveloperOps,
                "RuleCore.AllowDeveloperOpsDesc".Translate());

            string tier;
            if (Prefs.DevMode)
            {
                tier = "RuleCore.TierByDevMode".Translate();
            }
            else if (Settings.allowDeveloperOps)
            {
                tier = "RuleCore.TierGranted".Translate();
            }
            else
            {
                tier = "RuleCore.TierNotGranted".Translate();
            }
            listing.Label("RuleCore.TierNow".Translate(tier));

            listing.Gap();
            if (listing.ButtonText("RuleCore.OpenPanel".Translate()))
            {
                OpenRulePanel();
            }

            listing.End();

            // 刻意**不在这里** Settings.Write()。
            // 本方法是每帧调用的，早先版本在末尾写盘，等于设置页开着就每帧重写一次配置文件。
            // 落盘由框架负责：Dialog_ModSettings.PreClose() 会调 mod.WriteSettings()，
            // 退出游戏时也会。加上下面那个 override，玩家的改动一条都不会丢。
        }

        /// <summary>
        /// RimWorld 在多个时机调它（关设置页、退出游戏、写配置）。挂在这里兜底：
        /// 编辑器的防抖落盘只有面板开着时才被驱动，面板崩了或者游戏直接退出时，
        /// 未落盘的编辑必须有一个不依赖 UI 的出口。
        /// </summary>
        public override void WriteSettings()
        {
            // 编辑器改的是内存里的同一个对象，所以这里先落一次盘并不会丢东西——
            // 它存在的意义只是清掉"待落盘"标记，让面板的提示与磁盘状态一致。
            RuleLibrary.FlushPendingEdits();
            base.WriteSettings();
        }

        /// <summary>设置页的「打开规则编辑器」——真的打开，而不是弹一句"尚未实现"。</summary>
        private static void OpenRulePanel()
        {
            var defs = DefDatabase<MainButtonDef>.AllDefsListForReading;
            for (int i = 0; i < defs.Count; i++)
            {
                var def = defs[i];
                if (def != null && def.tabWindowClass == typeof(MainTabWindow_RuleCore))
                {
                    Find.MainTabsRoot.SetCurrentTab(def, true);
                    return;
                }
            }

            // 走到这里说明 Defs 没加载成功（面板压根不存在）。如实说，不假装打开了。
            Messages.Message("RuleCore.Panel.Missing".Translate(), MessageTypeDefOf.NegativeEvent, false);
        }

        private static void OpenLogFolder()
        {
            try
            {
                System.IO.Directory.CreateDirectory(LogDirectory);
                Application.OpenURL("file://" + LogDirectory.Replace('\\', '/'));
            }
            catch (Exception ex)
            {
                RuleLog.Error(null, "Settings", RuleEvalStatus.Error, "log.folder.open_failed", null,
                    "打开日志目录失败：" + ex.Message);
            }
        }
    }
}
