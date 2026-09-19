using System;
using System.IO;
using RuleCore.Core;

namespace RuleCore.CoreTests
{
    /// <summary>
    /// 脱离游戏的 Core 层测试宿主。
    ///
    /// 存在的意义：契约层、日志汇、组合器这些纯逻辑不该每次都靠"开游戏点按钮"来验证。
    /// 这里跑的是和游戏内「自检」按钮**同一套** RuleCoreSelfTest，
    /// 另外补上只有离开游戏才好测的部分（环形淘汰、相邻合并、游标拉取）。
    ///
    /// 运行：dotnet run --project Tests\RuleCore.CoreTests\RuleCore.CoreTests.csproj
    /// 退出码 0 = 全通过，1 = 有失败。
    /// </summary>
    public static class Program
    {
        private static int failures;
        private static int checks;

        public static int Main()
        {
            RuleLog.RawLogSink = text => Console.Error.WriteLine(text);

            Console.WriteLine("== 契约层自检（与游戏内「自检」按钮同一套）==");
            ConfigureLogging();
            RunContractSelfTest();

            Console.WriteLine();
            Console.WriteLine("== 环形缓冲 / 相邻合并 / 游标 ==");
            RunBufferTests();

            Console.WriteLine();
            Console.WriteLine("== 求值管线 ==");
            RunnerTests.Run();

            Console.WriteLine();
            Console.WriteLine("== 落盘 sink ==");
            RunFileSinkTests();

            Console.WriteLine();
            Console.WriteLine("== 比较符（判断层）==");
            RunCompareTests();

            SemanticsTests.Run();

            Console.WriteLine();
            Console.WriteLine(failures == 0
                ? "全部通过（" + checks + " 项）"
                : failures + " / " + checks + " 项失败");

            return failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// 「判断」是整条规则里最该被钉死的一段：判错了，检测和执行都无从谈起。
        /// 它是纯函数，所以这里直接穷举，不经过游戏。
        /// </summary>
        private static void RunCompareTests()
        {
            Check("比较：>", RuleCompare.Apply(RuleOperator.Greater, 12.4f, 10f)
                && !RuleCompare.Apply(RuleOperator.Greater, 10f, 10f),
                "12.4>10 应成立，10>10 应不成立");

            Check("比较：>= 含端点", RuleCompare.Apply(RuleOperator.AtLeast, 10f, 10f)
                && RuleCompare.Apply(RuleOperator.AtLeast, 10.1f, 10f),
                "10>=10 与 10.1>=10 都该成立");

            Check("比较：< 与 <=", RuleCompare.Apply(RuleOperator.Less, 9f, 10f)
                && !RuleCompare.Apply(RuleOperator.Less, 10f, 10f)
                && RuleCompare.Apply(RuleOperator.AtMost, 10f, 10f),
                "9<10、10<10 不成立、10<=10 成立");

            // 判等的容差是刻意的：温度会被加热器一点一点推着走，读数几乎不是整齐的 20.0。
            Check("比较：= 带容差，浮点噪声不算不等",
                RuleCompare.Apply(RuleOperator.Equal, 20.00001f, 20f)
                && !RuleCompare.Apply(RuleOperator.Equal, 20.1f, 20f),
                "20.00001=20 成立；20.1=20 不成立");

            Check("比较：!= 是 = 的补集", RuleCompare.Apply(RuleOperator.NotEqual, 20.1f, 20f)
                && !RuleCompare.Apply(RuleOperator.NotEqual, 20f, 20f),
                "两个方向都要对，否则规则会同时成立和同时不成立");

            Check("比较：未知比较符判 false 而不抛异常",
                !RuleCompare.Apply((RuleOperator)99, 1f, 0f) && !RuleCompare.IsKnown((RuleOperator)99),
                "手改 XML 写坏枚举值不该让整个 tick 停摆");

            Check("比较：符号表覆盖全部取值",
                RuleCompare.Symbol(RuleOperator.Greater) == ">"
                && RuleCompare.Symbol(RuleOperator.AtLeast) == ">="
                && RuleCompare.Symbol(RuleOperator.Less) == "<"
                && RuleCompare.Symbol(RuleOperator.AtMost) == "<="
                && RuleCompare.Symbol(RuleOperator.Equal) == "="
                && RuleCompare.Symbol(RuleOperator.NotEqual) == "!=",
                "符号会进日志摘要，缺一个就会看到 ?");

            Check("比较：全部已知取值都被 IsKnown 认下",
                RuleCompare.IsKnown(RuleOperator.Greater) && RuleCompare.IsKnown(RuleOperator.AtLeast)
                && RuleCompare.IsKnown(RuleOperator.Less) && RuleCompare.IsKnown(RuleOperator.AtMost)
                && RuleCompare.IsKnown(RuleOperator.Equal) && RuleCompare.IsKnown(RuleOperator.NotEqual),
                "新增比较符时忘了这里的表现是编辑器里选不出来");
        }

        private static void ConfigureLogging()
        {
            RuleLog.Boot();
            RuleLog.EnabledProvider = () => true;
            RuleLog.VerboseProvider = () => true;
            RuleLog.TickProvider = () => 12345;
        }

        private static void RunContractSelfTest()
        {
            var results = RuleCoreSelfTest.Run();
            for (int i = 0; i < results.Count; i++)
            {
                Check(results[i].Name, results[i].Passed, results[i].Detail);
            }
        }

        private static void RunBufferTests()
        {
            RuleLog.Boot(64);
            Check("初始为空", RuleLog.Count == 0 && RuleLog.LastSeq == 0,
                "count=" + RuleLog.Count + " seq=" + RuleLog.LastSeq);

            RuleLog.Info("R", "S", RuleEvalStatus.Issued, "code_a", null, "m1");
            Check("写入一条", RuleLog.Count == 1 && RuleLog.LastSeq == 1,
                "count=" + RuleLog.Count + " seq=" + RuleLog.LastSeq);

            RuleLog.Info("R", "S", RuleEvalStatus.Issued, "code_a", null, "m1");
            var merged = RuleLog.Snapshot();
            Check("相邻相同合并而非新增",
                RuleLog.Count == 1 && merged.Length == 1
                && merged[0].RepeatCount == 2 && merged[0].LastSeq == 2,
                "count=" + RuleLog.Count + " repeat=" + merged[0].RepeatCount);

            RuleLog.Info("R", "S", RuleEvalStatus.Issued, "code_b", null, "m2");
            Check("内容不同则新增", RuleLog.Count == 2, "count=" + RuleLog.Count);

            // 相隔的两次相同内容不能被合并——那正是 LogSyncMod 永久去重的坑。
            RuleLog.Info("R", "S", RuleEvalStatus.Issued, "code_c", null, null);
            RuleLog.Info("R", "S", RuleEvalStatus.Issued, "code_d", null, null);
            RuleLog.Info("R", "S", RuleEvalStatus.Issued, "code_c", null, null);
            Check("非相邻的相同内容各留一条", RuleLog.Count == 5, "count=" + RuleLog.Count);

            // 环形淘汰
            RuleLog.Boot(64);
            for (int i = 0; i < 100; i++)
            {
                RuleLog.Info("R", "S", "probe", "f" + i, null, null);
            }

            Check("环形缓冲封顶不增长", RuleLog.Count == 64, "count=" + RuleLog.Count);
            Check("淘汰计数正确", RuleLog.Dropped == 36, "dropped=" + RuleLog.Dropped);

            var all = RuleLog.Snapshot();
            Check("淘汰最旧、保留最新",
                all.Length == 64 && all[0].ReasonCode == "f36" && all[63].ReasonCode == "f99",
                "first=" + all[0].ReasonCode + " last=" + all[63].ReasonCode);

            Check("序号单调不减",
                all[63].Seq > all[0].Seq, "seq " + all[0].Seq + " → " + all[63].Seq);

            // 游标增量拉取
            long cursor = RuleLog.LastSeq;
            RuleLog.Info("R", "S", "probe", "tail", null, null);
            var since = RuleLog.Since(cursor);
            Check("游标只取增量", since.Length == 1 && since[0].ReasonCode == "tail",
                "n=" + since.Length);

            Check("游标追平后取空", RuleLog.Since(RuleLog.LastSeq).Length == 0, "应为 0");
        }

        private static void RunFileSinkTests()
        {
            string root = Path.Combine(Path.GetTempPath(), "RuleCoreSinkTests-" + Guid.NewGuid().ToString("N"));
            try
            {
                // ── 基本写入 ──────────────────────────────────────────
                string basicDir = Path.Combine(root, "basic");
                RuleLog.Boot();
                RuleLog.EnabledProvider = () => true;
                RuleLog.VerboseProvider = () => true;
                RuleLog.TickProvider = () => 42;

                var sink = new RuleLogFileSink(basicDir, 10, "0.1.0.0", "1.6");
                Check("落盘：启动成功", sink.Start(), sink.LastError);

                RuleLog.Info(null, "Boot", "Loaded", null, null, "开机");
                RuleLog.Error("#R1", "Action", RuleEvalStatus.Failed, "boom", "张三",
                    "含竖线|与\n换行的消息");

                string basicPath = sink.CurrentPath;
                Check("落盘：写了 2 条", sink.RowCount == 2, "rowCount=" + sink.RowCount);

                sink.Dispose();

                Check("落盘：文件已创建", File.Exists(basicPath), basicPath);

                string content = File.ReadAllText(basicPath);
                Check("落盘：含标题与表头",
                    content.Contains("# RuleCore 调试时间线") && content.Contains("| # | tick |"), "header");
                Check("落盘：含表尾", content.Contains("- 记录总数：2"), "footer");
                Check("落盘：竖线已转义", content.Contains("含竖线\\|与"), "escape |");
                Check("落盘：换行已转义", content.Contains("<br>换行的消息"), "escape \\n");

                // Dispose 之后不能再追加——验证退订确实生效。
                long sizeAfterDispose = new FileInfo(basicPath).Length;
                RuleLog.Info(null, "After", "dispose", null, null, "这条不该进文件");
                Check("落盘：Dispose 后不再追加",
                    new FileInfo(basicPath).Length == sizeAfterDispose, "size unchanged");

                // ── 同秒不覆盖 ────────────────────────────────────────
                string uniqueDir = Path.Combine(root, "unique");
                for (int i = 0; i < 3; i++)
                {
                    var s = new RuleLogFileSink(uniqueDir, 10, "0.1.0.0", "1.6");
                    s.Start();
                    RuleLog.Info(null, "Session", "started", null, null, "第 " + i + " 次");
                    s.Dispose();
                }
                Check("落盘：同秒会话不互相覆盖",
                    Directory.GetFiles(uniqueDir, "RuleCore-*.md").Length == 3,
                    "files=" + Directory.GetFiles(uniqueDir, "RuleCore-*.md").Length);

                // ── 保留份数 ──────────────────────────────────────────
                string retentionDir = Path.Combine(root, "retention");
                for (int i = 0; i < 4; i++)
                {
                    var s = new RuleLogFileSink(retentionDir, 2, "0.1.0.0", "1.6");
                    s.Start();
                    RuleLog.Info(null, "Session", "started", null, null, "第 " + i + " 次");
                    s.Dispose();
                }
                Check("落盘：保留份数生效",
                    Directory.GetFiles(retentionDir, "RuleCore-*.md").Length == 2,
                    "files=" + Directory.GetFiles(retentionDir, "RuleCore-*.md").Length);

                // ── 失败降级：目录位置被一个文件占住 ──────────────────
                Directory.CreateDirectory(root);
                string blocked = Path.Combine(root, "blocked");
                File.WriteAllText(blocked, "占位");

                var bad = new RuleLogFileSink(blocked, 5, "0.1.0.0", "1.6");
                bool badStarted = bad.Start();
                Check("落盘：失败时返回 false 且不抛异常",
                    !badStarted && bad.Failed && !string.IsNullOrEmpty(bad.LastError),
                    bad.LastError);
                bad.Dispose();
            }
            catch (Exception ex)
            {
                Check("落盘：测试自身未抛异常", false, ex.Message);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        internal static void Check(string name, bool ok, string detail)        {
            checks++;
            if (!ok) failures++;

            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name
                + (string.IsNullOrEmpty(detail) ? string.Empty : "   [" + detail + "]"));
        }
    }
}