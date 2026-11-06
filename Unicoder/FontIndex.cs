using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Unicoder;

internal sealed class FontEntry
{
    public readonly string Path;
    public readonly int Priority;
    // Flat list of inclusive [start, end] pairs, sorted and merged.
    public readonly int[] Ranges;

    public FontEntry(string path, int priority, int[] ranges)
    {
        Path = path;
        Priority = priority;
        Ranges = ranges;
    }

    public bool Contains(int cp)
    {
        int lo = 0, hi = Ranges.Length / 2 - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (cp < Ranges[mid * 2]) hi = mid - 1;
            else if (cp > Ranges[mid * 2 + 1]) lo = mid + 1;
            else return true;
        }

        return false;
    }
}

internal static class FontIndex
{
    private static volatile FontEntry[]? _entries;

    internal static bool Ready => _entries != null;
    internal static int Count => _entries?.Length ?? 0;
    
    private static readonly string[] Preferred =
    {
        "notosansregular", "notosans[", "segoeui", "arialuni", "dejavusans", "liberationsans", "arial", "tahoma",
        "verdana", "freesans", "opensans", "roboto",
        
        "notosanscjkjp", "notosansjp", "notosanscjk", "sourcehansans", "yugoth", "meiryo", "msgothic", "msyh",
        "microsoftyahei", "malgun", "notosanskr", "notosanssc", "notosanstc", "msjh", "wqy", "droidsansfallback",
        "simsun", "pingfang", "hiragino", "applesdgothic", "nanumgothic",

        "nirmala", "leelawad", "notosans",

        "seguisym", "seguiemj", "notoemoji", "notosanssymbols", "symbola", "notosansmath",

        "unifont"
    };

    private static readonly string[] Penalised =
    {
        "bold", "italic", "oblique", "thin", "light", "black", "heavy", "condensed", "narrow", "mono", "code",
        "display", "extra", "semi", "demi", "ultra", "outline", "handwrit", "script", "comic", "wingding",
        "webding", "dingbat"
    };

    internal static void BeginBuild(string[] osFontPaths, string userFontDir)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                Build(osFontPaths, userFontDir);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"Font indexing failed: {ex}");
                _entries = Array.Empty<FontEntry>();
            }
        });
    }
    
    internal static List<FontEntry> Candidates(int cp)
    {
        var result = new List<FontEntry>();
        var entries = _entries;
        if (entries == null) return result;
        foreach (var e in entries)
            if (e.Contains(cp))
                result.Add(e);
        return result;
    }

    private static void Build(string[] osFontPaths, string userFontDir)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<FontEntry>();
        int userOrder = 0;

        void Consider(string path, bool user)
        {
            try
            {
                if (!IsFontFile(path) || !seen.Add(path) || !File.Exists(path)) return;
                var ranges = ReadCoverage(path);
                if (ranges == null || ranges.Length == 0) return;
                int prio = user ? -1000000 + userOrder++ : Score(path);
                found.Add(new FontEntry(path, prio, ranges));
            }
            catch (Exception)
            {
                // unreadable / locked / corrupt file: ignore
            }
        }
        
        var userFiles = new List<string>();
        Walk(userFontDir, userFiles, 3, 500);
        userFiles.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (var f in userFiles) Consider(f, true);
        
        foreach (var f in osFontPaths) Consider(f, false);
        
        var extra = new List<string>();
        foreach (var root in ExtraRoots()) Walk(root, extra, 8, 20000);
        foreach (var f in extra) Consider(f, false);

        found.Sort((a, b) =>
        {
            int c = a.Priority.CompareTo(b.Priority);
            return c != 0 ? c : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
        });
        _entries = found.ToArray();
        Plugin.Log.Info($"Indexed {found.Count} fonts in {sw.ElapsedMilliseconds} ms.");
    }

    private static bool IsFontFile(string path)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext == ".ttf" || ext == ".otf" || ext == ".ttc" || ext == ".otc";
    }

    private static int Score(string path)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant()
            .Replace("-", "").Replace("_", "").Replace(" ", "");

        int idx = -1;
        for (int i = 0; i < Preferred.Length; i++)
        {
            if (name.Contains(Preferred[i]))
            {
                idx = i;
                break;
            }
        }

        int score = idx >= 0 ? 100 + idx * 10 : 2000;
        foreach (var w in Penalised)
        {
            if (name.Contains(w))
            {
                score += 500;
                break;
            }
        }

        if (name.Contains("serif") && !name.Contains("sans")) score += 500;
        
        if (name.Contains("coloremoji") || name.Contains("emojicolor") || name.Contains("twemoji")) score += 100000;
        if (name.Contains("regular")) score -= 5;
        return score;
    }

    private static IEnumerable<string> ExtraRoots()
    {
        var roots = new List<string>();
        bool windows = System.IO.Path.DirectorySeparatorChar == '\\';

        if (windows)
        {
            //linux
            try
            {
                string winFonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
                if (!string.IsNullOrEmpty(winFonts)) roots.Add(winFonts);
            }
            catch (Exception)
            {
            }

            if (Directory.Exists(@"Z:\usr"))
            {
                roots.Add(@"Z:\usr\share\fonts");
                roots.Add(@"Z:\usr\local\share\fonts");
                roots.Add(@"Z:\run\host\fonts");
                roots.Add(@"Z:\run\host\user-fonts");
                roots.Add(@"Z:\run\host\local-fonts");
                roots.Add(@"Z:\run\host\usr\share\fonts");

                string? home = Environment.GetEnvironmentVariable("HOME");
                if (!string.IsNullOrEmpty(home) && home![0] == '/')
                {
                    string zHome = "Z:" + home.Replace('/', '\\').TrimEnd('\\');
                    roots.Add(zHome + @"\.local\share\fonts");
                    roots.Add(zHome + @"\.fonts");
                }
            }
        }
        else
        {
            roots.Add("/usr/share/fonts");
            roots.Add("/usr/local/share/fonts");
            roots.Add("/Library/Fonts");
            roots.Add("/System/Library/Fonts");
            string? home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrEmpty(home))
            {
                roots.Add(System.IO.Path.Combine(home, ".local/share/fonts"));
                roots.Add(System.IO.Path.Combine(home, ".fonts"));
                roots.Add(System.IO.Path.Combine(home, "Library/Fonts"));
            }
        }

        return roots;
    }

    private static void Walk(string root, List<string> files, int maxDepth, int maxFiles)
    {
        var stack = new Stack<KeyValuePair<string, int>>();
        stack.Push(new KeyValuePair<string, int>(root, 0));
        while (stack.Count > 0 && files.Count < maxFiles)
        {
            var cur = stack.Pop();
            try
            {
                if (!Directory.Exists(cur.Key)) continue;
                foreach (var f in Directory.GetFiles(cur.Key))
                    if (IsFontFile(f))
                        files.Add(f);
                if (cur.Value >= maxDepth) continue;
                foreach (var d in Directory.GetDirectories(cur.Key))
                    stack.Push(new KeyValuePair<string, int>(d, cur.Value + 1));
            }
            catch (Exception)
            {
                // permission problems etc.
            }
        }
    }
    
    private static int U16(byte[] b, int o) => (b[o] << 8) | b[o + 1];
    private static uint U32(byte[] b, int o) => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];

    private static byte[]? ReadExact(Stream s, int count)
    {
        var buf = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = s.Read(buf, read, count - read);
            if (n <= 0) return null;
            read += n;
        }

        return buf;
    }
    
    internal static int[]? ReadCoverage(string path)
    {
        byte[] cm;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var head = ReadExact(fs, 12);
            if (head == null) return null;
            long baseOff = 0;

            if (head[0] == (byte)'t' && head[1] == (byte)'t' && head[2] == (byte)'c' && head[3] == (byte)'f')
            {
                if (U32(head, 8) == 0) return null;
                fs.Seek(12, SeekOrigin.Begin);
                var first = ReadExact(fs, 4);
                if (first == null) return null;
                baseOff = U32(first, 0);
                fs.Seek(baseOff, SeekOrigin.Begin);
                head = ReadExact(fs, 12);
                if (head == null) return null;
            }

            uint tag = U32(head, 0);
            if (tag != 0x00010000 && tag != 0x4F54544F && tag != 0x74727565) return null;

            int num = U16(head, 4);
            if (num == 0 || num > 512) return null;
            fs.Seek(baseOff + 12, SeekOrigin.Begin);
            var dir = ReadExact(fs, 16 * num);
            if (dir == null) return null;

            long cmapOff = -1;
            int cmapLen = 0;
            for (int i = 0; i < num; i++)
            {
                if (dir[i * 16] == (byte)'c' && dir[i * 16 + 1] == (byte)'m' &&
                    dir[i * 16 + 2] == (byte)'a' && dir[i * 16 + 3] == (byte)'p')
                {
                    cmapOff = U32(dir, i * 16 + 8);
                    uint len = U32(dir, i * 16 + 12);
                    if (len < 4 || len > 8 * 1024 * 1024) return null;
                    cmapLen = (int)len;
                    break;
                }
            }

            if (cmapOff < 0) return null;
            fs.Seek(cmapOff, SeekOrigin.Begin);
            var data = ReadExact(fs, cmapLen);
            if (data == null) return null;
            cm = data;
        }

        var pairs = new List<long>();
        void Add(int s, int e) => pairs.Add(((long)s << 32) | (uint)e);

        try
        {
            int n = U16(cm, 2);
            var seenOffsets = new HashSet<uint>();
            for (int i = 0; i < n; i++)
            {
                int pid = U16(cm, 4 + i * 8);
                int eid = U16(cm, 6 + i * 8);
                uint off = U32(cm, 8 + i * 8);
                bool unicode = pid == 0 || (pid == 3 && (eid == 1 || eid == 10));
                if (!unicode || off + 4 > cm.Length || !seenOffsets.Add(off)) continue;

                int o = (int)off;
                int format = U16(cm, o);
                if (format == 4)
                {
                    int segX2 = U16(cm, o + 6);
                    int seg = segX2 / 2;
                    int endO = o + 14;
                    int startO = endO + segX2 + 2;
                    int deltaO = startO + segX2;
                    int rangeO = deltaO + segX2;
                    for (int s = 0; s < seg; s++)
                    {
                        int end = U16(cm, endO + 2 * s);
                        int start = U16(cm, startO + 2 * s);
                        int delta = U16(cm, deltaO + 2 * s);
                        int ro = U16(cm, rangeO + 2 * s);
                        if (start == 0xFFFF) continue;
                        if (end > 0xFFFE) end = 0xFFFE;

                        int runStart = -1;
                        for (int c = start; c <= end; c++)
                        {
                            bool has;
                            if (ro == 0)
                            {
                                has = ((c + delta) & 0xFFFF) != 0;
                            }
                            else
                            {
                                int go = rangeO + 2 * s + ro + 2 * (c - start);
                                has = go + 2 <= cm.Length && U16(cm, go) != 0;
                            }

                            if (has)
                            {
                                if (runStart < 0) runStart = c;
                            }
                            else if (runStart >= 0)
                            {
                                Add(runStart, c - 1);
                                runStart = -1;
                            }
                        }

                        if (runStart >= 0) Add(runStart, end);
                    }
                }
                else if (format == 12)
                {
                    uint groups = U32(cm, o + 12);
                    if (groups > 1000000) continue;
                    for (uint g = 0; g < groups; g++)
                    {
                        int p = o + 16 + (int)g * 12;
                        if (p + 12 > cm.Length) break;
                        uint s = U32(cm, p);
                        uint e = U32(cm, p + 4);
                        if (e < s || e > 0x10FFFF) continue;
                        Add((int)s, (int)e);
                    }
                }
            }
        }
        catch (IndexOutOfRangeException)
        {
            // truncated table: keep whatever we parsed so far
        }

        if (pairs.Count == 0) return null;
        pairs.Sort();

        var merged = new List<int>(pairs.Count * 2);
        int curS = (int)(pairs[0] >> 32), curE = (int)(pairs[0] & 0xFFFFFFFF);
        for (int i = 1; i < pairs.Count; i++)
        {
            int s = (int)(pairs[i] >> 32), e = (int)(pairs[i] & 0xFFFFFFFF);
            if (s <= curE + 1)
            {
                if (e > curE) curE = e;
            }
            else
            {
                merged.Add(curS);
                merged.Add(curE);
                curS = s;
                curE = e;
            }
        }

        merged.Add(curS);
        merged.Add(curE);
        return merged.ToArray();
    }
}
