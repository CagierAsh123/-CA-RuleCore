using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace RuleCore.Core
{
    /// <summary>
    /// 把结构化记录落成 Markdown 表格：一行一条记录，可读、可 grep、可直接贴。
    ///
    /// 三条刻意的取舍：
    ///   1. **只追加，不重写整个文件**。LogSyncMod 每条日志都 File.WriteAllLines 重写全文，
    ///      是 O(N) 每条，还会和外部 tail 读取抢。表格天然适合追加。
    ///   2. **刷新策略分级**。Warn/Error 立即落盘——崩溃前最后那几条正是最该留下的；
    ///      普通记录攒够 64 条或 50ms 再刷，避免高频日志把时间烧在 IO 上。
    ///   3. **永不把异常抛回游戏**。落盘失败就停用自己并明确报出来，绝不静默降级。
    ///
    /// 只依赖 BCL，所以能在脱离游戏的控制台宿主里测。
    /// </summary>
    public sealed class RuleLogFileSink : IDisposable
    {
        public const string FilePrefix = "RuleCore-";
        public const string FileExtension = ".md";

        private const int FlushRowThreshold = 64;
        private const int FlushIntervalMs = 50;

        private readonly object gate = new object();
        private readonly string directory;
        private readonly int retention;
        private readonly string modVersion;
        private readonly string gameVersion;

        private StreamWriter writer;
        private string currentPath;
        private int rowsSinceFlush;
        private int lastFlushTick;
        private int rowCount;
        private bool attached;
        private bool disposed;

        public bool Failed { get; private set; }
        public string LastError { get; private set; }

        public RuleLogFileSink(string directory, int retention, string modVersion, string gameVersion)
        {
            this.directory = directory;
            this.retention = retention < 1 ? 1 : retention;
            this.modVersion = modVersion ?? "?";
            this.gameVersion = gameVersion ?? "?";
        }

        public string Directory
        {
            get { return directory; }
        }

        public string CurrentPath
        {
            get { lock (gate) { return currentPath; } }
        }

        public int RowCount
        {
            get { lock (gate) { return rowCount; } }
        }

        // ── 生命周期 ──────────────────────────────────────────────────

        /// <summary>建文件、写表头、按保留数清理旧日志，然后挂上记录流。</summary>
        public bool Start()
        {
            string failure = null;

            lock (gate)
            {
                if (writer != null)
                {
                    return true;
                }

                try
                {
                    System.IO.Directory.CreateDirectory(directory);
                    string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
                    currentPath = ResolveUniquePathLocked(Path.Combine(directory, FilePrefix + stamp + FileExtension));

                    // FileShare.Read：游戏在写的时候，用户/工具也能打开看。
                    writer = new StreamWriter(
                        new FileStream(currentPath, FileMode.Create, FileAccess.Write, FileShare.Read),
                        new UTF8Encoding(false));
                    writer.AutoFlush = false;

                    WriteHeaderLocked();
                    ApplyRetentionLocked();

                    lastFlushTick = Environment.TickCount;
                }
                catch (Exception ex)
                {
                    failure = FailLocked(ex);
                }
            }

            if (failure != null)
            {
                ReportFailure(failure);
                return false;
            }

            Attach();
            return true;
        }

        public void Attach()
        {
            lock (gate)
            {
                if (attached || disposed) return;
                attached = true;
            }
            RuleLog.Recorded += OnRecord;
        }

        public void Detach()
        {
            lock (gate)
            {
                if (!attached) return;
                attached = false;
            }
            RuleLog.Recorded -= OnRecord;
        }

        public void Flush()
        {
            lock (gate)
            {
                if (writer == null) return;
                try
                {
                    writer.Flush();
                    rowsSinceFlush = 0;
                    lastFlushTick = Environment.TickCount;
                }
                catch (Exception ex)
                {
                    FailLocked(ex);
                }
            }
        }

        public void Dispose()
        {
            string failure = null;

            lock (gate)
            {
                if (disposed) return;
                disposed = true;

                if (writer != null)
                {
                    try
                    {
                        WriteFooterLocked();
                        writer.Flush();
                    }
                    catch (Exception ex)
                    {
                        failure = FailLocked(ex);
                    }
                    CloseWriterLocked();
                }
            }

            Detach();

            if (failure != null)
            {
                ReportFailure(failure);
            }
        }

        // ── 记录 ──────────────────────────────────────────────────────

        public void OnRecord(RuleLogRecord record)
        {
            if (record == null) return;

            string failure = null;

            lock (gate)
            {
                if (disposed || writer == null || Failed) return;

                try
                {
                    WriteRowLocked(record);
                    rowCount++;
                    rowsSinceFlush++;

                    int now = Environment.TickCount;
                    bool urgent = record.Level >= RuleLogLevel.Warn;
                    bool batchFull = rowsSinceFlush >= FlushRowThreshold;
                    bool stale = unchecked(now - lastFlushTick) >= FlushIntervalMs;

                    if (urgent || batchFull || stale)
                    {
                        writer.Flush();
                        rowsSinceFlush = 0;
                        lastFlushTick = now;
                    }
                }
                catch (Exception ex)
                {
                    failure = FailLocked(ex);
                }
            }

            if (failure != null)
            {
                ReportFailure(failure);
            }
        }

        // ── 写入（调用方必须持有 gate）───────────────────────────────

        private void WriteHeaderLocked()
        {
            var sb = new StringBuilder(512);
            sb.Append("# RuleCore 调试时间线\n\n");
            sb.Append("- 生成时间：").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("- 模组版本：").Append(modVersion).Append('\n');
            sb.Append("- 游戏版本：").Append(gameVersion).Append('\n');
            sb.Append('\n');
            sb.Append("| # | tick | 级别 | 规则 | 阶段 | 结局 | 原因码 | 实体 | 说明 |\n");
            sb.Append("|--:|--:|---|---|---|---|---|---|---|\n");
            writer.Write(sb.ToString());
            writer.Flush();
        }

        private void WriteRowLocked(RuleLogRecord record)
        {
            writer.Write('|');
            writer.Write(record.Seq);
            if (record.RepeatCount > 1)
            {
                writer.Write(" ×");
                writer.Write(record.RepeatCount);
            }
            writer.Write(" |");
            writer.Write(record.Tick);
            writer.Write(" |");
            writer.Write(record.Level.ToString().ToUpperInvariant());
            writer.Write('|');
            writer.Write(Escape(record.RuleId));
            writer.Write('|');
            writer.Write(Escape(record.StepId));
            writer.Write('|');
            writer.Write(Escape(record.Outcome));
            writer.Write('|');
            writer.Write(Escape(record.ReasonCode));
            writer.Write('|');
            writer.Write(Escape(record.Entity));
            writer.Write('|');
            writer.Write(Escape(record.Message));
            writer.Write("|\n");
        }

        private void WriteFooterLocked()
        {
            long dropped = RuleLog.Dropped;

            var sb = new StringBuilder(256);
            sb.Append('\n').Append("---").Append("\n\n");
            sb.Append("- 记录总数：").Append(rowCount).Append('\n');
            sb.Append("- 缓冲淘汰：").Append(dropped).Append('\n');
            sb.Append("- 结束时间：").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n');
            writer.Write(sb.ToString());
        }

        /// <summary>
        /// 文件名只精确到秒，同一秒内连续开会话会撞名。
        /// 撞了就加 -2 / -3 后缀，而不是默默覆盖掉上一份——日志被覆盖是最难查的那种丢失。
        /// </summary>
        private string ResolveUniquePathLocked(string basePath)
        {
            if (!File.Exists(basePath))
            {
                return basePath;
            }

            string folder = Path.GetDirectoryName(basePath);
            if (string.IsNullOrEmpty(folder)) folder = directory;
            string stem = Path.Combine(folder, Path.GetFileNameWithoutExtension(basePath));
            string extension = Path.GetExtension(basePath);

            for (int i = 2; i < 1000; i++)
            {
                // 后缀用下划线而不是连字符：词典序里 '-' (45) 排在 '.' (46) 之前，
                // 用 '-' 会让 xxx-2.md 排到 xxx.md 前面，把"最旧"的判断整个反过来。
                string candidate = stem + "_" + i.ToString(CultureInfo.InvariantCulture) + extension;
                if (!File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return basePath;
        }

        private void ApplyRetentionLocked()
        {
            try
            {
                var files = new List<string>(System.IO.Directory.GetFiles(directory, FilePrefix + "*" + FileExtension));
                if (files.Count <= retention) return;

                files.Sort(CompareByAge);

                int toDelete = files.Count - retention;
                for (int i = 0; i < files.Count && toDelete > 0; i++)
                {
                    // 绝不删当前正在写的这一份。
                    if (string.Equals(files[i], currentPath, StringComparison.OrdinalIgnoreCase)) continue;

                    try
                    {
                        File.Delete(files[i]);
                        toDelete--;
                    }
                    catch
                    {
                        // 单个旧文件删不掉不该影响本次会话。
                    }
                }
            }
            catch
            {
                // 清理失败不影响记录本身。
            }
        }

        private static int CompareByAge(string a, string b)
        {
            int byTime = SafeWriteTimeUtc(a).CompareTo(SafeWriteTimeUtc(b));
            if (byTime != 0) return byTime;
            return string.CompareOrdinal(a, b);
        }

        private static DateTime SafeWriteTimeUtc(string path)
        {
            try { return File.GetLastWriteTimeUtc(path); }
            catch { return DateTime.MinValue; }
        }

        private string FailLocked(Exception ex)
        {
            Failed = true;
            LastError = ex.Message;
            CloseWriterLocked();
            return ex.Message;
        }

        private void CloseWriterLocked()
        {
            if (writer == null) return;
            try { writer.Dispose(); } catch { }
            writer = null;
        }

        private static void ReportFailure(string message)
        {
            var sink = RuleLog.RawLogSink;
            if (sink == null) return;
            try { sink("[RuleCore] 日志落盘失败，已停用文件输出：" + message); }
            catch { }
        }

        /// <summary>表格单元格转义：竖线会截断列，换行会截断行。</summary>
        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value
                .Replace("|", "\\|")
                .Replace("\r\n", "<br>")
                .Replace("\n", "<br>")
                .Replace("\r", "<br>");
        }
    }
}
