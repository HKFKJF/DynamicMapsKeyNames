using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;

namespace DynamicMapsKeyNames
{
    /// <summary>
    /// Logging for this mod.
    ///
    /// Everything still goes to BepInEx' shared LogOutput.log (as LogInfo / LogWarning / LogError),
    /// but it is mirrored into a file named after this mod so a tester can hand over just one file:
    ///
    ///     BepInEx\DynamicMapsKeyNames\logs\DynamicMapsKeyNames.log
    ///
    /// Rules:
    ///   - problems (warnings and errors) are always written to the file;
    ///   - the startup banner and session header are always written;
    ///   - the verbose per-marker lines need the "调试日志" option, because they are emitted once per
    ///     handled marker and would otherwise bloat the file;
    ///   - failures never propagate: logging must not be able to break the map.
    ///
    /// Threading: unlike the rest of the plugin this is lock-guarded, because the first write opens
    /// the file, rotates the previous one and emits a session header - work that must not interleave.
    /// </summary>
    internal static class ModLog
    {
        /// <summary>Name of this mod, used for the folder and the log file.</summary>
        internal const string ModName = "DynamicMapsKeyNames";

        /// <summary>Stop appending past this size so a long session cannot fill the disk.</summary>
        private const long MaxBytes = 8L * 1024 * 1024;

        private static readonly object Gate = new object();
        private static string _path;
        private static bool _unavailable;
        private static bool _capped;
        private static int _sessionEntries;

        /// <summary>Full path of the mod's log file, or null when it could not be created.</summary>
        internal static string Path
        {
            get { return _unavailable ? null : _path; }
        }

        // Settable so the log-file rules can be verified outside the game.
        private static string _injectedDirectory;

        /// <summary>Overrides the log folder (verification only).</summary>
        internal static void InjectDirectory(string directory)
        {
            _injectedDirectory = directory;
        }

        internal static string LogDirectory()
        {
            if (!string.IsNullOrEmpty(_injectedDirectory)) return _injectedDirectory;

            string root = KeyNamesPlugin.GameRoot();
            if (!string.IsNullOrEmpty(root))
                return System.IO.Path.Combine(root, "BepInEx", ModName, "logs");

            // Fall back to the BepInEx folder layout when the game root is unknown.
            try
            {
                string plugins = System.IO.Path.GetDirectoryName(typeof(KeyNamesPlugin).Assembly.Location);
                string bepInEx = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(plugins));
                if (!string.IsNullOrEmpty(bepInEx))
                    return System.IO.Path.Combine(bepInEx, ModName, "logs");
            }
            catch { }

            return System.IO.Path.Combine("BepInEx", ModName, "logs");
        }

        private static void EnsureOpen()
        {
            if (_unavailable || _path != null) return;

            try
            {
                string dir = LogDirectory();
                Directory.CreateDirectory(dir);
                _path = System.IO.Path.Combine(dir, ModName + ".log");

                // Rotate once per session so a single file stays mailable.
                try
                {
                    var info = new FileInfo(_path);
                    if (info.Exists && info.Length > MaxBytes)
                    {
                        string old = _path + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(_path, old);
                    }
                }
                catch { }

                File.AppendAllText(_path, Environment.NewLine +
                    "================ " + ModName + " " +
                    typeof(KeyNamesPlugin).Assembly.GetName().Version +
                    " | session started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                    " ================" + Environment.NewLine, new UTF8Encoding(false));

                _capped = false;
                _sessionEntries = 0;
            }
            catch
            {
                _unavailable = true;
                _path = null;
            }
        }

        private static void Write(string line)
        {
            try
            {
                // Everything, including the one-time open, happens under the lock: opening creates the
                // directory, rotates the previous file and writes the session header, none of which is
                // safe to run twice concurrently. (The caller is the Unity main thread today, but the
                // plugin also logs from Harmony postfixes and the log path must not depend on that.)
                lock (Gate)
                {
                    EnsureOpen();
                    if (_path == null) return;

                    if (_capped) return;
                    if (++_sessionEntries > 20000)
                    {
                        _capped = true;
                        File.AppendAllText(_path,
                            DateTime.Now.ToString("[HH:mm:ss] ") +
                            "log entry limit reached for this session; further lines are not written" +
                            Environment.NewLine, new UTF8Encoding(false));
                        return;
                    }

                    File.AppendAllText(_path,
                        DateTime.Now.ToString("[HH:mm:ss] ") + line + Environment.NewLine,
                        new UTF8Encoding(false));
                }
            }
            catch
            {
                _unavailable = true;
            }
        }
        private static void Console(LogLevel level, string message)
        {
            try
            {
                var log = KeyNamesPlugin.Log;
                if (log == null) return;

                // BepInEx 5's ManualLogSource.LogMessage takes a single argument, so dispatch.
                switch (level)
                {
                    case LogLevel.Error:
                    case LogLevel.Fatal:
                        log.LogError(message);
                        break;
                    case LogLevel.Warning:
                        log.LogWarning(message);
                        break;
                    default:
                        log.LogInfo(message);
                        break;
                }
            }
            catch { }
        }

        /// <summary>A line that belongs in the file even when verbose logging is off.</summary>
        internal static void Always(string message)
        {
            Write(message);
            Console(LogLevel.Info, message);
        }

        /// <summary>Verbose line: file and console only while "调试日志" is on.</summary>
        internal static void Info(string message)
        {
            if (!KeyNamesPlugin.IsDebug) return;
            Write(message);
            Console(LogLevel.Info, message);
        }

        /// <summary>A problem. Always recorded, and always echoed to the BepInEx console.</summary>
        internal static void Warn(string message)
        {
            Write(message);
            Console(LogLevel.Warning, message);
        }

        internal static void Error(string message)
        {
            Write(message);
            Console(LogLevel.Error, message);
        }
    }

    /// <summary>
    /// Companion plugin for DynamicMaps.
    /// Appends the required key's name to locked door markers on the map, e.g.
    ///   "Tarcone Director's Office [Tarcone Director's office key]"
    /// It does not modify DynamicMaps itself - it only hooks the marker text assignment.
    /// </summary>
    [BepInPlugin("dynamicmaps.keynames", "钥匙显示", "1.0.18")]
    [BepInDependency("com.mpstark.dynamicmaps", BepInDependency.DependencyFlags.SoftDependency)]
    public class KeyNamesPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> ShowKeyNames;
        internal static ConfigEntry<string> Language;
        internal static ConfigEntry<string> Format;
        internal static ConfigEntry<bool> AlwaysShowLabels;
        internal static ConfigEntry<int> KeyNameFontSize;
        internal static ConfigEntry<string> KeyNameStyle;
        internal static ConfigEntry<string> MarkerText;
        internal static ConfigEntry<bool> MergeSameKeyDoors;
        internal static ConfigEntry<float> MergeDistance;
        internal static ConfigEntry<bool> DebugLogging;

        // 下拉选项文字（F12 界面全中文；同时作为配置值存储）
        internal const string OptAuto = "自动";
        internal const string OptChinese = "中文";
        internal const string OptEnglish = "英文";
        internal const string OptShort = "简称";
        internal const string OptFull = "全名";
        internal const string OptInitials = "首字母缩写";
        internal const string OptKeyOnly = "仅显示钥匙名";
        internal const string OptKeep = "保持地图原文";

        private void Awake()
        {
            Log = Logger;

            ShowKeyNames = Config.Bind("1. 常规", "显示钥匙名称", true,
                "在动态地图的锁门标记上追加所需钥匙的名称。");

            Language = Config.Bind("1. 常规", "钥匙名称语言", OptChinese,
                new ConfigDescription(
                    "钥匙名使用哪种语言。「自动」= 跟随 SPT 的游戏语言设置。",
                    new AcceptableValueList<string>(OptAuto, OptChinese, OptEnglish)));

            Format = Config.Bind("1. 常规", "附加格式", "[{0}]",
                "钥匙名以什么格式追加，{0} 会被替换成钥匙名。例如改成 {0} 或 ·{0} 可以更省地方。");

            AlwaysShowLabels = Config.Bind("1. 常规", "常显钥匙名称", true,
                "被标注的门标记文字会一直显示在地图上（不悬停也可见）。\n" +
                "关闭后恢复 DynamicMaps 原本的行为 —— 只在鼠标悬停标记时才显示文字（门名也一样）。\n" +
                "开启时，钥匙名的显隐会跟随门锁图标（图标在哪些楼层可见，钥匙名就在哪些楼层可见）。");

            KeyNameFontSize = Config.Bind("1. 常规", "钥匙名称字号", 10,
                new ConfigDescription(
                    "地图上钥匙名的字号，默认 10（DynamicMaps 默认是 9~13 自动缩放，10 略小于其中间值）。\n" +
                    "0 = 保持 DynamicMaps 默认（在 9~13 之间自动缩放）；\n" +
                    "填其他数值会固定被标注标记的字号，数值过大时文字可能与旁边标记重叠。",
                    new AcceptableValueRange<int>(0, 40)));

            KeyNameStyle = Config.Bind("1. 常规", "钥匙名称风格", OptShort,
                new ConfigDescription(
                    "「简称」= 用游戏内物品格子上显示的短名（如 118钥匙 / Dorm 118），更省地方；\n" +
                    "「全名」= 用完整物品名（如 宿舍118房间钥匙 / Dorm room 118 key）。",
                    new AcceptableValueList<string>(OptShort, OptFull)));

            MarkerText = Config.Bind("1. 常规", "门牌文字处理", OptInitials,
                new ConfigDescription(
                    "地图自带门名要怎么处理（钥匙名追加在它后面）：\n" +
                    "「首字母缩写」= 英文单词取首字母，West Wing Room 220 → WWR220；数字、中文等原样保留，\n" +
                    "RB-MP11、ZB-014 这类代号不会被压缩；\n" +
                    "「仅显示钥匙名」= 丢掉门名，只显示钥匙名（最省地方）；\n" +
                    "「保持地图原文」= 完全不动地图包写的门名。",
                    new AcceptableValueList<string>(OptInitials, OptKeyOnly, OptKeep)));

            MergeSameKeyDoors = Config.Bind("1. 常规", "合并同钥匙双开门", true,
                "两扇门用同一把钥匙、且位置很近（如同一间房的双开门）时，只在两扇门中间显示一次钥匙名，\n" +
                "避免文字重叠；另一扇门只保留自己的门名。关闭则每扇门都各自标注。");

            MergeDistance = Config.Bind("1. 常规", "合并判定距离", 12f,
                new ConfigDescription(
                    "「合并同钥匙双开门」的距离阈值（地图单位，约等于米）。\n" +
                    "实测同房间双开门一般相距 0.5~11，不同房间的门通常 18 以上，所以默认 12 比较合适。",
                    new AcceptableValueRange<float>(0f, 50f)));

            DebugLogging = Config.Bind("2. 诊断", "调试日志", false,
                "输出每个门标记的处理结果、被跳过的类别、无法解析的钥匙 ID。仅在排查问题时开启。");

            // The external name tables (see ExternalKeyNames) are cached per language, so changing
            // the language or the style has to drop those caches - otherwise a mid-session switch
            // would keep serving names resolved for the previous setting.
            Language.SettingChanged += delegate { InvalidateCaches(); };
            KeyNameStyle.SettingChanged += delegate { InvalidateCaches(); };

            try
            {
                var harmony = new Harmony("dynamicmaps.keynames");
                harmony.PatchAll(typeof(KeyNamesPlugin).Assembly);
                ModLog.Always("[DMKeyNames] Harmony patches applied.");
            }
            catch (Exception ex)
            {
                ModLog.Error("[DMKeyNames] Failed to apply patches: " + ex);
            }

            ModLog.Always("[DMKeyNames] v" + typeof(KeyNamesPlugin).Assembly.GetName().Version +
                        " loaded. keys: ch=" + KeyNameTable.CountChinese +
                        " en=" + KeyNameTable.CountEnglish +
                        " short=" + KeyNameTable.CountShortChinese + "/" + KeyNameTable.CountShortEnglish +
                        " style=" + (UseShortNames ? OptShort : OptFull) +
                        " markerText=" + MarkerTextMode +
                        " merge=" + (MergeSameKeyDoors != null && MergeSameKeyDoors.Value
                            ? ("on(" + MergeDistanceValue + ")") : "off") +
                        " language=" + ResolveLanguage() +
                        " (config=" + LanguageValue + ", auto=" + DetectSptLocale() + ")" +
                        " debug=" + IsDebug);

            // Reports how many names came from names.json / mod CustomLocales / the SPT database.
            ExternalKeyNames.LogSummary();
        }

        internal static string LanguageValue
        {
            get { return Language != null ? Language.Value : "<null>"; }
        }

        internal static bool IsDebug
        {
            get { return DebugLogging != null && DebugLogging.Value; }
        }

        internal static float MergeDistanceValue
        {
            get { return MergeDistance != null ? MergeDistance.Value : 12f; }
        }

        /// <summary>True when the short (item tile) key names should be used.</summary>
        internal static bool UseShortNames
        {
            get
            {
                var v = KeyNameStyle != null ? KeyNameStyle.Value : OptShort;
                return !string.Equals(v, OptFull, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>Configured handling of the map's own door text.</summary>
        internal static string MarkerTextMode
        {
            get { return MarkerText != null ? MarkerText.Value : OptInitials; }
        }

        /// <summary>The game root (the folder holding EscapeFromTarkov.exe), or null if unknown.</summary>
        internal static string GameRoot()
        {
            try
            {
                string root = BepInEx.Paths.GameRootPath;
                return string.IsNullOrEmpty(root) ? null : root;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Last resort for a key name the built-in tables do not carry: the game has more than the
        /// 196 generated keys, and content mods add their own (those never appear in the SPT
        /// database, only in the mod's own CustomLocales). See ExternalKeyNames for the order.
        /// </summary>
        internal static string ResolveExternal(string id, bool english, bool useShort)
        {
            return ExternalKeyNames.Find(id, english, useShort);
        }

        /// <summary>
        /// The external tables are keyed per language, and the language is read from disk once per
        /// session, so switching the option has to drop those caches.
        /// </summary>
        private static void InvalidateCaches()
        {
            _detectedLocale = null;
            ExternalKeyNames.Invalidate();
        }

        /// <summary>
        /// Compresses the map's door text so the label covers less of the map:
        /// "West Wing Room 220" -> "WWR220", "East Wing Sanitar's Office" -> "EWSO".
        /// Short codes are preserved so Reserve-style names stay readable ("RB-MP11", "ZB-014",
        /// "RB-VO"), single words are left alone, and Chinese/Cyrillic text keeps its layout.
        /// </summary>
        internal static string AbbreviateToInitials(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (ContainsNonLatin(text)) return AbbreviateLatinWordsOnly(text);
            return AbbreviateCompact(text);
        }

        /// <summary>True when the text holds anything outside the Latin-1 / Latin Extended-A range.</summary>
        private static bool ContainsNonLatin(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] > 0x24F) return true;
            }
            return false;
        }

        /// <summary>
        /// Latin-only text: words become their first letter and merge, digits stick to the previous
        /// token ("West Wing Room 220" -> "WWR220"), while code-like tokens are kept verbatim - a
        /// token containing a digit, '-' or '_' ("RB-MP11", "ZB-014", "E2") or an all-letter token
        /// of at most two letters ("RB", "VO"). A single word is returned unchanged.
        /// </summary>
        private static string AbbreviateCompact(string text)
        {
            var vals = new List<string>();
            var kinds = new List<char>();   // 'i' = initial, 'd' = digits, 'k' = code
            int initials = 0, codes = 0;

            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (IsLatinLetter(c) || char.IsDigit(c))
                {
                    int j = i;
                    bool code = false;
                    int letters = 0;
                    while (j < text.Length)
                    {
                        char t = text[j];
                        if (IsLatinLetter(t)) { letters++; }
                        else if (char.IsDigit(t) || t == '-' || t == '_') { code = true; }
                        else if (t == '\'' && j + 1 < text.Length && IsLatinLetter(text[j + 1])) { letters++; }
                        else { break; }
                        j++;
                    }

                    string token = text.Substring(i, j - i);
                    bool allDigits = true;
                    for (int k = 0; k < token.Length; k++)
                    {
                        if (!char.IsDigit(token[k])) { allDigits = false; break; }
                    }

                    if (allDigits)
                    {
                        vals.Add(token); kinds.Add('d');
                    }
                    else if (code || letters <= 2)
                    {
                        vals.Add(token); kinds.Add('k'); codes++;
                    }
                    else
                    {
                        vals.Add(char.ToUpperInvariant(token[0]).ToString());
                        kinds.Add('i'); initials++;
                    }
                    i = j;
                }
                else
                {
                    if (!char.IsWhiteSpace(c)) { vals.Add(c.ToString()); kinds.Add('x'); }
                    i++;
                }
            }

            // One single word (e.g. "Crossroads") would collapse to a single letter - not useful.
            if (codes == 0 && initials <= 1) return text;

            var sb = new StringBuilder(text.Length);
            for (int k = 0; k < vals.Count; k++)
            {
                if (k > 0 && kinds[k] != 'd' && (kinds[k] == 'k' || kinds[k - 1] == 'k')) sb.Append(' ');
                sb.Append(vals[k]);
            }
            return sb.ToString().Trim();
        }

        /// <summary>Keeps the original layout (spaces, Chinese text, punctuation) and only shortens Latin words.</summary>
        private static string AbbreviateLatinWordsOnly(string text)
        {
            var sb = new StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                if (IsLatinLetter(text[i]))
                {
                    int j = i;
                    while (j < text.Length &&
                           (IsLatinLetter(text[j]) ||
                            (text[j] == '\'' && j + 1 < text.Length && IsLatinLetter(text[j + 1]))))
                    {
                        j++;
                    }
                    string word = text.Substring(i, j - i);
                    if (word.Length <= 2) sb.Append(word);
                    else sb.Append(char.ToUpperInvariant(word[0]));
                    i = j;
                }
                else
                {
                    sb.Append(text[i]);
                    i++;
                }
            }
            return sb.ToString();
        }

        private static bool IsLatinLetter(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
        }

        private static bool _alwaysVisibleFailed;

        /// <summary>
        /// Makes a labelled marker's text keep showing while its icon shows.
        ///
        /// Why this exists: DynamicMaps' own defaults hide marker text on the normal map view. The
        /// per-marker default tables (decompiled from MapMarker..ctor) are asymmetric:
        ///     ImageAlphaLayerStatus : Hidden 0, Underneath 0, OnTop 1, FullReveal 1
        ///     LabelAlphaLayerStatus : Hidden 0, Underneath 0, OnTop 0, FullReveal 1
        /// so text is only visible in FullReveal, which MapMarker.OnPointerEnter sets while the
        /// marker is hovered. That is exactly the "key names only appear when I hover" complaint.
        ///
        /// Which entry actually matters is decided by MapView.UpdateLayerBound: it looks the marker's
        /// coordinate up against the map's layers and passes that layer's LayerStatus, and
        /// MapLayer.OnTopLevelSelected assigns Hidden / OnTop / Underneath depending on which deck the
        /// player is viewing. So hard-coding a single entry (an earlier build only set OnTop) does
        /// nothing whenever the marker resolves to Underneath or Hidden.
        ///
        /// The fix therefore copies the marker's *image* alpha table onto its *label* table, key by
        /// key. The key name then appears exactly when the door icon appears - never on a layer where
        /// DynamicMaps has decided to hide the marker itself.
        /// </summary>
        internal static void ApplyAlwaysVisibleLabel(Type markerType, object marker)
        {
            try
            {
                var labelProp = AccessTools.Property(markerType, "LabelAlphaLayerStatus");
                var imageProp = AccessTools.Property(markerType, "ImageAlphaLayerStatus");
                if (labelProp == null || imageProp == null) return;

                var labelDict = labelProp.GetValue(marker, null) as System.Collections.IDictionary;
                var imageDict = imageProp.GetValue(marker, null) as System.Collections.IDictionary;
                if (labelDict == null || imageDict == null) return;

                // Keys are the LayerStatus enum; only touch a table that actually looks like one.
                foreach (System.Collections.DictionaryEntry e in imageDict)
                {
                    if (e.Key != null && !e.Key.GetType().IsEnum)
                    {
                        FailAlwaysVisible("image alpha table keys are not a layer enum");
                        return;
                    }
                }

                foreach (System.Collections.DictionaryEntry e in imageDict)
                {
                    if (e.Key == null) continue;
                    // IDictionary.Contains on a Dictionary<enum,float> throws for a wrong-typed key.
                    if (!e.Key.GetType().IsEnum || !labelDict.Contains(e.Key)) continue;
                    float imageAlpha;
                    try { imageAlpha = Convert.ToSingle(e.Value); }
                    catch { continue; }

                    if (Convert.ToSingle(labelDict[e.Key]) < imageAlpha) labelDict[e.Key] = imageAlpha;
                }

                if (IsDebug) LogLabelAlphaState(markerType, marker, labelDict, imageDict);
            }
            catch (Exception ex)
            {
                FailAlwaysVisible(ex.Message);
            }
        }

        private static void FailAlwaysVisible(string message)
        {
            if (_alwaysVisibleFailed) return;
            _alwaysVisibleFailed = true;
            ModLog.Warn("[DMKeyNames] could not force label visibility (key names stay hover-only): " + message);
        }

        /// <summary>
        /// One line per labelled marker describing the alpha tables it ended up with. Only runs with
        /// Debug Logging on; it is the evidence needed if a marker still refuses to show its text.
        /// </summary>
        private static void LogLabelAlphaState(Type markerType, object marker,
                                               System.Collections.IDictionary labelDict,
                                               System.Collections.IDictionary imageDict)
        {
            try
            {
                var catProp = AccessTools.Property(markerType, "Category");
                string category = catProp != null ? catProp.GetValue(marker, null) as string : null;

                var sb = new StringBuilder();
                foreach (System.Collections.DictionaryEntry e in imageDict)
                {
                    if (e.Key == null) continue;
                    object labelValue = labelDict.Contains(e.Key) ? labelDict[e.Key] : null;
                    sb.Append(e.Key).Append(":img=").Append(e.Value)
                      .Append(",lbl=").Append(labelValue == null ? "<none>" : labelValue.ToString()).Append(' ');
                }

                ModLog.Always("[DMKeyNames] always-show alpha cat=" + category + " " + sb.ToString().Trim());
            }
            catch { }
        }

        private static readonly HashSet<string> ReportedUnresolvedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> ReportedSkippedCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Markers this plugin relabeled. Weak keys, so destroyed markers are not kept alive.
        private static readonly ConditionalWeakTable<object, object> LabeledMarkers =
            new ConditionalWeakTable<object, object>();

        internal static void RememberLabeledMarker(object marker)
        {
            if (marker == null) return;
            LabeledMarkers.Remove(marker);
            LabeledMarkers.Add(marker, marker);
        }

        internal static bool IsLabeledMarker(object marker)
        {
            if (marker == null) return false;
            object ignored;
            return LabeledMarkers.TryGetValue(marker, out ignored);
        }

        private static bool _fontSizeFailed;

        /// <summary>
        /// Applies the configured font size to a marker label. DynamicMaps auto-sizes marker text
        /// between two static limits (9 and 13 in 1.2.1); pinning fontSizeMin == fontSizeMax keeps
        /// the size stable while auto-sizing stays on, which is what DynamicMaps' own layout expects.
        /// Setting the option back to 0 restores those original limits.
        /// </summary>
        internal static void ApplyFontSize(Type markerType, object marker)
        {
            try
            {
                var labelProp = AccessTools.Property(markerType, "Label");
                object label = labelProp != null ? labelProp.GetValue(marker, null) : null;
                if (label == null) return;

                var labelType = label.GetType();
                var minProp = AccessTools.Property(labelType, "fontSizeMin");
                var maxProp = AccessTools.Property(labelType, "fontSizeMax");
                if (minProp == null || maxProp == null) return;
                if (!minProp.CanWrite || !maxProp.CanWrite) return;

                int size = KeyNameFontSize != null ? KeyNameFontSize.Value : 0;
                float min, max;
                if (size > 0)
                {
                    min = size;
                    max = size;
                }
                else
                {
                    min = ReadStaticFloat(markerType, "_markerMinFontSize", 9f);
                    max = ReadStaticFloat(markerType, "_markerMaxFontSize", 13f);
                }

                minProp.SetValue(label, min, null);
                maxProp.SetValue(label, max, null);
            }
            catch (Exception ex)
            {
                if (_fontSizeFailed) return;
                _fontSizeFailed = true;
                ModLog.Warn("[DMKeyNames] could not apply the configured font size: " + ex.Message);
            }
        }

        private static float ReadStaticFloat(Type type, string fieldName, float fallback)
        {
            try
            {
                var f = AccessTools.Field(type, fieldName);
                if (f == null) return fallback;
                object v = f.GetValue(null);
                return v is float ? (float)v : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>Warns once per unknown item id, so a gap in the table is visible.</summary>
        internal static void ReportUnresolvedId(string associatedItemId, string text)
        {
            if (!IsDebug || string.IsNullOrEmpty(associatedItemId)) return;
            if (!ReportedUnresolvedIds.Add(associatedItemId)) return;
            ModLog.Warn("[DMKeyNames] no key name for associatedItemId=" + associatedItemId +
                           " (marker text: " + Truncate(text) + ") - add it to KeyNameTable if it is a key.");
        }

        /// <summary>Logs once per marker category that is not treated as a locked door.</summary>
        internal static void ReportSkippedCategory(string category, string associatedItemId)
        {
            if (!IsDebug || string.IsNullOrEmpty(category)) return;
            if (!ReportedSkippedCategories.Add(category)) return;
            // LogInfo on purpose: BepInEx discards Debug level messages by default, and this
            // is already gated behind the Debug Logging config entry.
            ModLog.Info("[DMKeyNames] skipping category=\"" + category + "\" (first seen with item=" +
                        associatedItemId + ")");
        }

        internal static string ResolveLanguage()
        {
            var v = Language != null ? Language.Value : OptChinese;
            if (!string.IsNullOrEmpty(v) && v != OptAuto) return v;
            return DetectSptLocale();
        }

        /// <summary>
        /// The locale is read from disk, so resolve it once per session instead of
        /// re-reading locale.json for every marker that gets created.
        /// </summary>
        private static string DetectSptLocale()
        {
            if (_detectedLocale != null) return _detectedLocale;
            _detectedLocale = DetectSptLocaleUncached();
            return _detectedLocale;
        }

        private static string _detectedLocale;

        private static string DetectSptLocaleUncached()
        {
            try
            {
                string root = BepInEx.Paths.GameRootPath;
                string p = Path.Combine(root, "SPT_Runtime", "SPT_Data", "configs", "locale.json");
                if (!File.Exists(p)) return OptChinese;

                string text = File.ReadAllText(p);
                string v = Grab(text, "gameLocale");
                if (string.IsNullOrEmpty(v) || v == "system") v = Grab(text, "serverLocale");

                // "system" means the game follows the OS language. Check the regional format
                // as well: on many Chinese systems CurrentUICulture is en-US while
                // CurrentCulture is zh-CN, so the UI culture alone would wrongly pick English.
                if (string.IsNullOrEmpty(v) || v == "system")
                {
                    try
                    {
                        if (HasLanguagePrefix(System.Globalization.CultureInfo.CurrentCulture.Name, "zh") ||
                            HasLanguagePrefix(System.Globalization.CultureInfo.CurrentUICulture.Name, "zh"))
                            return OptChinese;
                        if (HasLanguagePrefix(System.Globalization.CultureInfo.CurrentUICulture.Name, "en"))
                            return OptEnglish;
                    }
                    catch { }
                    return OptChinese;
                }

                if (HasLanguagePrefix(v, "en")) return OptEnglish;
                return OptChinese;
            }
            catch
            {
                return OptChinese;
            }
        }

        private static bool HasLanguagePrefix(string cultureName, string prefix)
        {
            return !string.IsNullOrEmpty(cultureName) &&
                   cultureName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        internal static string Truncate(string s)
        {
            if (s == null) return "<null>";
            return s.Length <= 120 ? s : s.Substring(0, 120) + "...";
        }

        private static string Grab(string json, string key)
        {
            int i = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (i < 0) return null;
            int c = json.IndexOf(':', i);
            if (c < 0) return null;
            int q1 = json.IndexOf('"', c + 1);
            if (q1 < 0) return null;
            int q2 = json.IndexOf('"', q1 + 1);
            if (q2 < 0) return null;
            return json.Substring(q1 + 1, q2 - q1 - 1);
        }

        /// <summary>门标记的标注方案：这扇门用什么文字、是否追加钥匙名。</summary>
        internal sealed class LabelPlan
        {
            public bool IsKeyMarker;
            public string Suffix = "";     // 形如 [西220]
            public string DoorOnly = "";   // 去掉钥匙名后的门名（可能为空）
            public string Full = "";       // 这扇门单独标注时应显示的文字
        }

        /// <summary>
        /// 计算某个门标记的文字。IsKeyMarker=false 表示这不是"已知钥匙的锁门"，调用方应放弃处理；
        /// 为 true 时即使文字无需变化（门名本身就是钥匙名，如储备站的 RB-AO），也要继续按已标注处理。
        /// </summary>
        internal static LabelPlan PlanLabel(string text, string associatedItemId)
        {
            var plan = new LabelPlan();
            if (ShowKeyNames == null || !ShowKeyNames.Value) return plan;
            if (string.IsNullOrEmpty(associatedItemId)) return plan;

            string keyName = KeyNameTable.Resolve(associatedItemId, ResolveLanguage(), UseShortNames);
            if (string.IsNullOrEmpty(keyName))
            {
                ReportUnresolvedId(associatedItemId, text);
                return plan;
            }

            string fmt = (Format != null && !string.IsNullOrEmpty(Format.Value)) ? Format.Value : "[{0}]";
            try { plan.Suffix = string.Format(fmt, keyName); }
            catch { plan.Suffix = "[" + keyName + "]"; }

            plan.IsKeyMarker = true;

            // 门名本身就写着钥匙名（储备站的门标记就叫 RB-AO / RB-ST …）：不重复追加，
            // 但仍按"已标注"处理，否则这些门永远不会被常显/字号设置覆盖。
            if (!string.IsNullOrEmpty(text) &&
                text.IndexOf(keyName, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                plan.DoorOnly = text;
                plan.Full = text;
                return plan;
            }

            string mode = MarkerTextMode;
            string doorOnly;
            if (string.Equals(mode, OptKeyOnly, StringComparison.OrdinalIgnoreCase)) doorOnly = "";
            else if (string.Equals(mode, OptInitials, StringComparison.OrdinalIgnoreCase)) doorOnly = AbbreviateToInitials(text);
            else doorOnly = text;
            if (doorOnly == null) doorOnly = "";

            plan.DoorOnly = doorOnly;
            plan.Full = string.IsNullOrEmpty(doorOnly) ? plan.Suffix : (doorOnly + " " + plan.Suffix);
            return plan;
        }

        // ------------------------------------------------------------------
        //  同钥匙、且位置相近的双开门：钥匙名只在两扇门中间显示一次
        // ------------------------------------------------------------------

        internal sealed class DoorEntry
        {
            public object Marker;
            public Type MarkerType;
            public string KeyId;
            public string DoorOnly = "";
            public string Full = "";
            // Position is captured in map layout units, on all three axes: two doors on the same spot
            // but different decks are not a double door. X/Y come from the marker's anchoredPosition,
            // Z from the game-space position the marker was created with.
            public float X, Y, Z;
            public string AppliedLabel;
            // The map-space spot this marker was last placed at; null while it is still untouched.
            public float? AppliedX, AppliedY;
        }

        private static readonly Dictionary<string, List<DoorEntry>> DoorsByKey =
            new Dictionary<string, List<DoorEntry>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<object, DoorEntry> DoorsByMarker =
            new Dictionary<object, DoorEntry>();
        private static bool _mergeFailed;

        /// <summary>
        /// Drops all door bookkeeping.
        ///
        /// These dictionaries hold the marker objects strongly, so they must not outlive the map they
        /// belong to. MapMarker.OnDestroy already unregisters one marker at a time, but that patch
        /// reports "bookkeeping may go stale" and disables itself if DynamicMaps ever stops exposing
        /// OnDestroy - and a stale entry therefore pins a destroyed marker plus its label forever, and
        /// keeps every later scan over the same key walking dead doors.
        ///
        /// So the map lifecycle is used as the authoritative reset: MapView.UnloadMap runs both when a
        /// raid ends (ModdedMapScreen.OnRaidEnd) and right before another map is loaded
        /// (MapView.LoadMap), and it is the same place DynamicMaps clears its own marker and label
        /// lists. RegisterDoor additionally clears when a marker turns up belonging to a different
        /// MapView, which covers a map swap that somehow bypassed UnloadMap.
        /// </summary>
        internal static void ClearDoorBookkeeping()
        {
            try
            {
                DoorsByKey.Clear();
                DoorsByMarker.Clear();

                // Also drop the map-view identity. It is a strong reference, so leaving it set would
                // keep the map view being torn down alive until the next map's first door happened to
                // reassign it. Bounded (one object) and self-healing, but the whole point of this
                // method is to release everything at teardown. SameMapView treats null as "no map
                // seen yet", so clearing it here is behaviour-preserving.
                _doorMapView = null;
            }
            catch { }
        }

        /// <summary>Which map view the bookkeeping currently describes; null until the first door.</summary>
        private static object _doorMapView;

        /// <summary>
        /// Returns false when the markers gathered so far belong to a different map view than this
        /// one, in which case the bookkeeping was cleared and the caller should start over.
        /// </summary>
        private static bool SameMapView(object marker)
        {
            try
            {
                var comp = marker as Component;
                var viewProp = comp != null ? AccessTools.Property(comp.GetType(), "ContainingMapView") : null;
                object view = viewProp != null ? viewProp.GetValue(comp, null) : null;
                if (view == null) return true;   // cannot tell: keep what we have

                if (_doorMapView == null)
                {
                    _doorMapView = view;
                    return true;
                }

                if (ReferenceEquals(_doorMapView, view)) return true;

                _doorMapView = view;
                ClearDoorBookkeeping();
                return false;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>登记一个已标注的门标记，然后重新计算它所在的分组。</summary>
        internal static void RegisterDoor(Type markerType, object marker, string markerText,
                                          string associatedItemId, LabelPlan plan)
        {
            try
            {
                // A marker from another map view means the previous map's records are stale; drop them
                // before adding anything (see ClearDoorBookkeeping).
                SameMapView(marker);

                DoorEntry entry = InitDoorEntry(markerType, marker, markerText, associatedItemId, plan);

                ReadDoorPosition(entry);
                UpdateDoorGroup(associatedItemId);
            }
            catch (Exception ex)
            {
                if (_mergeFailed) return;
                _mergeFailed = true;
                ModLog.Warn("[DMKeyNames] door grouping failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Adds or refreshes one marker's bookkeeping and returns its entry.
        ///
        /// Split out from RegisterDoor because it touches no Unity state - it is the part that can be
        /// exercised without a running Editor, which is how the lifecycle check drives it.
        /// </summary>
        internal static DoorEntry InitDoorEntry(Type markerType, object marker, string markerText,
                                                string associatedItemId, LabelPlan plan)
        {
            DoorEntry entry;
            if (DoorsByMarker.TryGetValue(marker, out entry))
            {
                // 同一个标记被重复赋值：只刷新方案，别覆盖原始门名
                entry.DoorOnly = plan.DoorOnly;
                entry.Full = plan.Full;
                return entry;
            }

            entry = new DoorEntry
            {
                Marker = marker,
                MarkerType = markerType,
                KeyId = associatedItemId,
                DoorOnly = plan.DoorOnly,
                Full = plan.Full
            };
            DoorsByMarker[marker] = entry;

            List<DoorEntry> list;
            if (!DoorsByKey.TryGetValue(associatedItemId, out list))
            {
                list = new List<DoorEntry>();
                DoorsByKey[associatedItemId] = list;
            }
            list.Add(entry);
            return entry;
        }

        /// <summary>标记销毁时移除登记，并让剩下的门重新决定由谁显示钥匙名。</summary>
        internal static void UnregisterDoor(object marker)
        {
            try
            {
                DoorEntry entry;
                if (marker == null || !DoorsByMarker.TryGetValue(marker, out entry)) return;
                DoorsByMarker.Remove(marker);

                List<DoorEntry> list;
                if (!DoorsByKey.TryGetValue(entry.KeyId, out list)) return;
                list.Remove(entry);
                if (list.Count == 0) DoorsByKey.Remove(entry.KeyId);
                else UpdateDoorGroup(entry.KeyId);            }
            catch
            {
                // 销毁阶段写标签失败无所谓，忽略
            }
        }

        private static void ReadDoorPosition(DoorEntry entry)
        {
            var comp = entry.Marker as Component;
            if (comp == null) return;
            var rt = comp.transform as RectTransform;
            Vector2 p = rt != null ? rt.anchoredPosition : (Vector2)comp.transform.localPosition;
            entry.X = p.x;
            entry.Y = p.y;

            // The marker keeps the game-space position it was created with; its Z is the deck height.
            // Dynamic markers move themselves every frame, but static locked doors do not.
            try
            {
                var posProp = AccessTools.Property(entry.MarkerType, "Position");
                object v = posProp != null ? posProp.GetValue(entry.Marker, null) : null;
                if (v is Vector3) entry.Z = ((Vector3)v).z;
            }
            catch { }
        }

        private static void UpdateDoorGroup(string keyId)
        {
            List<DoorEntry> members;
            if (!DoorsByKey.TryGetValue(keyId, out members)) return;

            bool merge = MergeSameKeyDoors != null && MergeSameKeyDoors.Value;
            float limit = MergeDistanceValue;
            if (limit < 0f) limit = 0f;
            float limitSq = limit * limit;

            var done = new bool[members.Count];
            for (int i = 0; i < members.Count; i++)
            {
                if (done[i]) continue;
                var cluster = new List<int>();
                cluster.Add(i);
                done[i] = true;

                if (merge)
                {
                    bool grew = true;
                    while (grew)
                    {
                        grew = false;
                        for (int j = 0; j < members.Count; j++)
                        {
                            if (done[j]) continue;
                            for (int c = 0; c < cluster.Count; c++)
                            {
                                float dx = members[j].X - members[cluster[c]].X;
                                float dy = members[j].Y - members[cluster[c]].Y;
                                float dz = members[j].Z - members[cluster[c]].Z;
                                if (dx * dx + dy * dy + dz * dz <= limitSq)
                                {
                                    cluster.Add(j);
                                    done[j] = true;
                                    grew = true;
                                    break;
                                }
                            }
                        }
                    }
                }

                ApplyCluster(members, cluster);
            }
        }

        private static void ApplyCluster(List<DoorEntry> members, List<int> cluster)
        {
            // 载具标记：取坐标最小者，保证结果稳定（不随创建顺序抖动）
            int carrierIdx = cluster[0];
            for (int n = 1; n < cluster.Count; n++)
            {
                DoorEntry a = members[cluster[n]];
                DoorEntry b = members[carrierIdx];
                if (a.X < b.X || (a.X == b.X && a.Y < b.Y)) carrierIdx = cluster[n];
            }
            DoorEntry carrier = members[carrierIdx];
            bool merged = cluster.Count > 1;

            float midX = 0f, midY = 0f;
            for (int n = 0; n < cluster.Count; n++)
            {
                midX += members[cluster[n]].X;
                midY += members[cluster[n]].Y;
            }
            midX /= cluster.Count;
            midY /= cluster.Count;

            for (int n = 0; n < cluster.Count; n++)
            {
                DoorEntry e = members[cluster[n]];
                bool isCarrier = cluster[n] == carrierIdx;

                string label;
                if (!merged || isCarrier) label = e.Full;
                else if (string.Equals(e.DoorOnly, carrier.DoorOnly, StringComparison.Ordinal)) label = "";
                else label = e.DoorOnly;

                // The carrier's whole marker - door icon and key name together - moves to the middle
                // of the group; a partner stays where its door actually is. Moving only the label used
                // to leave the key name floating away from the lock icon it belongs to.
                float targetX = (merged && isCarrier) ? midX : e.X;
                float targetY = (merged && isCarrier) ? midY : e.Y;

                ApplyEntryLabel(e, label, targetX, targetY);
            }
        }

        private static void ApplyEntryLabel(DoorEntry e, string label, float targetX, float targetY)
        {
            if (e.Marker == null) return;
            bool sameLabel = string.Equals(e.AppliedLabel, label, StringComparison.Ordinal);
            bool sameSpot = e.AppliedX.HasValue &&
                            Math.Abs(e.AppliedX.Value - targetX) < 0.001f &&
                            Math.Abs(e.AppliedY.Value - targetY) < 0.001f;
            if (sameLabel && sameSpot) return;

            try
            {
                WriteMarkerLabel(e.MarkerType, e.Marker, label);
                if (MoveMarkerTo(e.Marker, e.MarkerType, targetX, targetY))
                {
                    e.AppliedX = targetX;
                    e.AppliedY = targetY;
                }
                e.AppliedLabel = label;
            }
            catch (Exception ex)
            {
                if (_mergeFailed) return;
                _mergeFailed = true;
                ModLog.Warn("[DMKeyNames] applying door label failed: " + ex.Message);
            }
        }

        /// <summary>把 marker.Text 与它的 TMP 标签一起改写。</summary>
        internal static void WriteMarkerLabel(Type markerType, object marker, string label)
        {
            var textProp = AccessTools.Property(markerType, "Text");
            if (textProp != null)
            {
                // set_Text 只是写 backing field（已对 DynamicMaps 1.2.1 反编译核实），
                // 优先用公开 setter，字段写仅作兜底。
                var setter = textProp.GetSetMethod(true);
                if (setter != null) setter.Invoke(marker, new object[] { label });
                else
                {
                    var f = AccessTools.Field(markerType, "<Text>k__BackingField");
                    if (f != null) f.SetValue(marker, label);
                }
            }

            object labelComp = GetMarkerLabel(markerType, marker);
            if (labelComp != null)
            {
                var tp = AccessTools.Property(labelComp.GetType(), "text");
                if (tp != null && tp.CanWrite) tp.SetValue(labelComp, label, null);
            }
        }

        internal static object GetMarkerLabel(Type markerType, object marker)
        {
            var labelProp = AccessTools.Property(markerType, "Label");
            return labelProp != null ? labelProp.GetValue(marker, null) : null;
        }

        /// <summary>
        /// Places a marker at a spot in map layout units.
        ///
        /// This moves the marker's own RectTransform, so the door icon and its key-name label travel
        /// together - the label is a child of the marker and keeps its own default offset. An earlier
        /// version moved only the label's anchoredPosition, which made the key name drift away from
        /// the lock icon. DynamicMaps' own Set_Position -> Move does exactly this, and the marker
        /// transform is not driven per frame for static doors, so the placement sticks.
        /// </summary>
        internal static bool MoveMarkerTo(object marker, Type markerType, float x, float y)
        {
            var move = AccessTools.Method(markerType, "Move");
            if (move == null || move.GetParameters().Length != 2) return false;

            // Keep the game-space position: only X/Y are re-laid-out, the deck (Z) stays as created.
            Vector3 pos = Vector3.zero;
            try
            {
                var posProp = AccessTools.Property(markerType, "Position");
                object v = posProp != null ? posProp.GetValue(marker, null) : null;
                if (v is Vector3) pos = (Vector3)v;
            }
            catch { }

            pos.x = x;
            pos.y = y;
            move.Invoke(marker, new object[] { pos, false });
            return true;
        }
    }

    /// <summary>id -> key name tables, generated from the SPT database locales.</summary>
    internal static class KeyNameTable
    {
        // Auto-generated: key item template id -> localized name (ch)
        private static readonly Dictionary<string, string> Names_ch = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "5448ba0b4bdc2d02308b456c", "工厂紧急出口钥匙" },
            { "5672c92d4bdc2d180f8b4567", "宿舍118房间钥匙" },
            { "5780cda02459777b272ede61", "宿舍306房间钥匙" },
            { "5780cf692459777de4559321", "宿舍315房间钥匙" },
            { "5780cf722459777a5108b9a1", "宿舍308房间钥匙" },
            { "5780cf7f2459777de4559322", "宿舍楼 314 房间符号钥匙" },
            { "5780cf942459777df90dcb72", "宿舍214房间钥匙" },
            { "5780cf9e2459777df90dcb73", "宿舍218房间钥匙" },
            { "5780cfa52459777dfb276eb1", "宿舍220房间钥匙" },
            { "5780d0532459777a5108b9a2", "海关 Tarcone 主管办公室钥匙" },
            { "5780d0652459777df90dcb74", "隔间钥匙" },
            { "5780d07a2459777de4559324", "集装箱宿舍钥匙" },
            { "5913611c86f77479e0084092", "拖车停车场工棚钥匙" },
            { "59136a4486f774447a1ed172", "宿舍门卫室钥匙" },
            { "59136e1e86f774432f15d133", "宿舍110房间钥匙" },
            { "591382d986f774465a6413a7", "宿舍105房间钥匙" },
            { "591383f186f7744a4c5edcf3", "宿舍104房间钥匙" },
            { "5913877a86f774432f15d444", "加油站储藏室钥匙" },
            { "5913915886f774123603c392", "军事基地检查站钥匙" },
            { "5914578086f774123569ffa4", "宿舍108房间钥匙" },
            { "59148c8a86f774197930e983", "宿舍204房间钥匙" },
            { "591ae8f986f77406f854be45", "Yotota车钥匙" },
            { "591afe0186f77431bd616a11", "ZB-014钥匙" },
            { "5937ee6486f77408994ba448", "机械钥匙" },
            { "5938144586f77473c2087145", "简易工棚钥匙" },
            { "5938504186f7740991483f30", "宿舍203房间钥匙" },
            { "5938603e86f77435642354f4", "宿舍206房间钥匙" },
            { "59387a4986f77401cc236e62", "宿舍114房间钥匙" },
            { "5938994586f774523a425196", "宿舍103房间钥匙" },
            { "593962ca86f774068014d9af", "未知钥匙" },
            { "593aa4be86f77457f56379f8", "宿舍303房间钥匙" },
            { "5a0dc45586f7742f6b0b73e3", "疗养院西楼 104 办公室钥匙" },
            { "5a0dc95c86f77452440fc675", "疗养院西楼 112 办公室钥匙" },
            { "5a0ea64786f7741707720468", "疗养院东楼 107 办公室钥匙" },
            { "5a0ea79b86f7741d4a35298e", "疗养院杂物间钥匙" },
            { "5a0eb38b86f774153b320eb0", "SMW车钥匙" },
            { "5a0eb6ac86f7743124037a28", "别墅后门钥匙" },
            { "5a0ec6d286f7742c0b518fb5", "疗养院西楼 205 房间钥匙" },
            { "5a0ee30786f774023b6ee08f", "疗养院西楼 216 房间钥匙" },
            { "5a0ee34586f774023b6ee092", "疗养院西楼 220 房间钥匙" },
            { "5a0ee37f86f774023657a86f", "疗养院西楼 221 房间钥匙" },
            { "5a0ee4b586f7743698200d22", "疗养院东楼 206 房间钥匙" },
            { "5a0eec9686f77402ac5c39f2", "疗养院东楼 310 房间钥匙" },
            { "5a0eecf686f7740350630097", "疗养院东楼 313 房间钥匙" },
            { "5a0eed4386f77405112912aa", "疗养院东楼 314 房间钥匙" },
            { "5a0eee1486f77402aa773226", "疗养院东楼 328 房间钥匙" },
            { "5a0eff2986f7741fd654e684", "疗养院西楼 321 房间保险箱钥匙" },
            { "5a0f068686f7745b0d4ea242", "别墅保险箱钥匙" },
            { "5a0f08bc86f77478f33b84c2", "疗养院经理办公室保险箱钥匙" },
            { "5a0f0f5886f7741c4e32a472", "疗养院仓库保险箱钥匙" },
            { "5a13eebd86f7746fd639aa93", "疗养院西楼 218 房间钥匙" },
            { "5a13ef0686f7746e5a411744", "疗养院西楼 219 房间钥匙" },
            { "5a13ef7e86f7741290491063", "疗养院西楼 301 房间钥匙" },
            { "5a13f24186f77410e57c5626", "疗养院东楼 222 房间钥匙" },
            { "5a13f35286f77413ef1436b0", "疗养院东楼 226 房间钥匙" },
            { "5a13f46386f7741dd7384b04", "疗养院西楼 306 房间钥匙" },
            { "5a144bdb86f7741d374bbde0", "疗养院东楼 205 房间钥匙" },
            { "5a144dfd86f77445cb5a0982", "疗养院西楼 203 房间钥匙" },
            { "5a1452ee86f7746f33111763", "疗养院西楼 222 房间钥匙" },
            { "5a145d4786f7744cbb6f4a12", "疗养院东楼 306 房间钥匙" },
            { "5a145d7b86f7744cbb6f4a13", "疗养院东楼 308 房间钥匙" },
            { "5a145ebb86f77458f1796f05", "疗养院东楼 316 房间钥匙" },
            { "5ad5ccd186f774446d5706e9", "OLI管理员办公室钥匙" },
            { "5ad5cfbd86f7742c825d6104", "OLI物流部钥匙" },
            { "5ad5d20586f77449be26d877", "OLI超市杂物间钥匙" },
            { "5ad5d49886f77455f9731921", "变电站杂物间钥匙" },
            { "5ad5d64486f774079b080af8", "药房钥匙" },
            { "5ad5d7d286f77450166e0a89", "Kiba 商店外门钥匙" },
            { "5ad5db3786f7743568421cce", "EMERCOM医疗区钥匙" },
            { "5ad7247386f7747487619dc3", "Goshan收银机钥匙" },
            { "5addaffe86f77470b455f900", "Kiba 商店内侧格栅门钥匙" },
            { "5c1d0c5f86f7744bb2683cf0", "实验室钥匙卡·蓝" },
            { "5c1d0d6d86f7744bb2683e1f", "实验室钥匙卡·黄 " },
            { "5c1d0dc586f7744baf2e7b79", "实验室钥匙卡·绿 " },
            { "5c1d0efb86f7744baf2e7b7b", "实验室钥匙卡·红" },
            { "5c1d0f4986f7744bb01837fa", "实验室钥匙卡·黑" },
            { "5c1e2a1e86f77431ea0ea84c", "TerraGroup 实验室经理办公室钥匙" },
            { "5c1e2d1f86f77431e9280bee", "TerraGroup 实验室武器测试区域钥匙" },
            { "5c1e495a86f7743109743dfb", "实验室钥匙卡·紫" },
            { "5c1f79a086f7746ed066fb8f", "TerraGroup 实验室军械库钥匙" },
            { "5d08d21286f774736e7c94c3", "Shturman 的储物箱钥匙" },
            { "5d80c60f86f77440373c4ece", "RB-BK 符号钥匙" },
            { "5d80c62a86f7744036212b3f", "RB-VO 符号钥匙" },
            { "5d80c66d86f774405611c7d6", "RB-AO钥匙" },
            { "5d80c6c586f77440351beef1", "RB-OB钥匙" },
            { "5d80c6fc86f774403a401e3c", "RB-TB钥匙" },
            { "5d80c78786f774403a401e3e", "RB-AK钥匙" },
            { "5d80c88d86f77440556dbf07", "RB-AM钥匙" },
            { "5d80c8f586f77440373c4ed0", "RB-OP钥匙" },
            { "5d80c93086f7744036212b41", "RB-MP11钥匙" },
            { "5d80c95986f77440351beef3", "RB-MP12钥匙" },
            { "5d80ca9086f774403a401e40", "RB-MP21钥匙" },
            { "5d80cab086f77440535be201", "RB-MP22钥匙" },
            { "5d80cb3886f77440556dbf09", "RB-PSP1钥匙" },
            { "5d80cb5686f77440545d1286", "RB-PSV1钥匙" },
            { "5d80cbd886f77470855c26c2", "RB-MP13钥匙" },
            { "5d80ccac86f77470841ff452", "RB-ORB1钥匙" },
            { "5d80ccdd86f77474f7575e02", "RB-ORB2钥匙" },
            { "5d80cd1a86f77402aa362f42", "RB-ORB3钥匙" },
            { "5d8e0db586f7744450412a42", "RB-KORL钥匙" },
            { "5d8e0e0e86f774321140eb56", "RB-KPRL钥匙" },
            { "5d8e15b686f774445103b190", "水电站仓库钥匙" },
            { "5d8e3ecc86f774414c78d05e", "RB-GN钥匙" },
            { "5d947d3886f774447b415893", "RB-SMP钥匙" },
            { "5d947d4e86f774447b415895", "RB-KSM钥匙" },
            { "5d95d6be86f77424444eb3a7", "RB-PSV2钥匙" },
            { "5d95d6fa86f77424484aa5e9", "RB-PSP2钥匙" },
            { "5d9f1fa686f774726974a992", "RB-ST钥匙" },
            { "5da46e3886f774653b7a83fe", "RB-RS钥匙" },
            { "5da5cdcd86f774529238fb9b", "RB-RH钥匙" },
            { "5da743f586f7744014504f72", "USEC 仓库钥匙" },
            { "5e42c71586f7747f245e1343", "ULTRA 医疗仓库钥匙" },
            { "5e42c81886f7742a01529f57", "Object #11SR 钥匙卡" },
            { "5e42c83786f7742a021fdf3c", "Object #21WS 钥匙卡" },
            { "5ede7a8229445733cb4c18e2", "RB-PKPM 符号钥匙" },
            { "5ede7b0c6d23e5473e6e8c66", "RB-RLSA 钥匙" },
            { "5efde6b4f5448336730dbd61", "蓝色记号钥匙卡" },
            { "5eff09cd30a7dc22fd1ddfed", "粘有胶带的疗养院办公室钥匙" },
            { "61a64428a8c6aa1b795f0ba1", "灯塔便利店储藏室" },
            { "61a6444b8c141d68246e2d2f", "山边小屋钥匙" },
            { "61a64492ba05ef10d62adcc1", "叛乱USEC仓库钥匙" },
            { "61aa5aed32a4743c3453d319", "灯塔警用卡车驾驶室钥匙" },
            { "61aa5b518f5e7a39b41416e2", "Merin小轿车后备箱钥匙" },
            { "61aa5ba8018e9821b7368da9", "USEC灯塔二号保险箱钥匙" },
            { "61aa81fcb225ac1ead7957c3", "叛乱USEC工作间钥匙" },
            { "62987c658081af308d7558c6", "雷达站指挥室钥匙" },
            { "62987cb98081af308d7558c8", "会议室钥匙" },
            { "62987da96188c076bc0d8c51", "手术室钥匙" },
            { "62987dfc402c7f69bf010923", "套间符号钥匙" },
            { "62987e26a77ec735f90a2995", "污水处理厂储藏室钥匙" },
            { "62a9cb937377a65d7b070cef", "叛乱USEC营房钥匙" },
            { "6398fd8ad3de3849057f5128", "备用藏身处钥匙" },
            { "63a39667c9b3aa4b61683e98", "金融办公室钥匙" },
            { "63a397d3af870e651d58e65b", "车行封闭区域钥匙" },
            { "63a399193901f439517cafb6", "车行负责人办公室钥匙" },
            { "63a39c69af870e651d58e6aa", "商店经理钥匙" },
            { "63a39c7964283b5e9c56b280", "Concordia 安保处钥匙" },
            { "63a39cb1c9b3aa4b61683ee2", "建筑工地工棚钥匙" },
            { "63a39ce4cd6db0635c1975fa", "供应部门主管办公室钥匙" },
            { "63a39ddda3a2b32b5f6e007a", "公寓上锁房间保险箱钥匙" },
            { "63a39df18a56922e82001f25", "Zmeevsky 5公寓20号钥匙" },
            { "63a39dfe3901f439517cafba", "Zmeevsky 3公寓8号钥匙" },
            { "63a39e1d234195315d4020bd", "Primorsky 46-48号天桥钥匙" },
            { "63a39e49cd6db0635c1975fc", "档案室钥匙" },
            { "63a39e5b234195315d4020bf", "房屋管理处二楼保险箱钥匙" },
            { "63a39e6acd6db0635c1975fe", "房屋管理处一楼保险箱钥匙" },
            { "63a39f08cd6db0635c197600", "酒店215房间钥匙" },
            { "63a39f6e64283b5e9c56b289", "铁门钥匙" },
            { "63a39fc0af870e651d58e6ae", "Chekannaya 15号公寓钥匙" },
            { "63a39fd1c9b3aa4b61683efb", "楼梯间钥匙" },
            { "63a39fdf1e21260da44a0256", "货物网格门钥匙" },
            { "63a3a93f8a56922e82001f5d", "废弃工厂符号钥匙" },
            { "63a71e781031ac76fe773c7d", "Concordia 8号公寓钥匙" },
            { "63a71e86b7f4570d3a293169", "Concordia 64号公寓办公室钥匙" },
            { "63a71e922b25f7513905ca20", "Concordia 64号公寓钥匙" },
            { "63a71eb5b7f4570d3a29316b", "Primorsky 48 公寓钥匙" },
            { "63a71ed21031ac76fe773c7f", "金融办公室小房间钥匙" },
            { "64ccc1d4a0f13c24561edf27", "Concordia34号公寓房间钥匙" },
            { "64ccc1ec1779ad6ba200a137", "Concordia 8号公寓家庭影院钥匙" },
            { "64ccc1f4ff54fb38131acf27", "Concordia 63号公寓钥匙" },
            { "64ccc1fe088064307e14a6f7", "“白鲸”餐厅经理办公室钥匙" },
            { "64ccc206793ca11c8f450a38", "TerraGroup会议室钥匙" },
            { "64ccc2111779ad6ba200a139", "塔科夫银行现金区钥匙" },
            { "64ccc246ff54fb38131acf29", "X光检查室钥匙" },
            { "64ccc24de61ea448b507d34d", "TerraGroup军械库钥匙" },
            { "64ccc25f95763a1ae376e447", "神秘房间符号钥匙" },
            { "64ccc268c41e91416064ebc7", "体育教师办公室钥匙" },
            { "64ce572331dd890873175115", "\"Aspect\"公司办公区钥匙" },
            { "64d4b23dc1b37504b41ac2b6", "生锈的带血钥匙" },
            { "6581998038c79576a2569e11", "Unity Credit银行现金收纳机钥匙" },
            { "658199972dc4e60f6d556a2f", "地下停车场杂物间钥匙" },
            { "658199a0490414548c0fa83b", "Horse饭店厕所钥匙" },
            { "658199aa38c79576a2569e13", "TerraGroup科学家办公室钥匙" },
            { "6582dbe43a2e5248357dbe9a", "\"调解室\"钥匙" },
            { "6582dbf0b8d7830efc45016f", "休息室钥匙" },
            { "6582dc4b6ba9e979af6b79f4", "内务部学院门厅警卫室钥匙" },
            { "6582dc5740562727a654ebb1", "不动产管理处办公室钥匙" },
            { "66265d7be65f224b2e17c6aa", "USEC小屋房间钥匙" },
            { "664d3db6db5dea2bad286955", "Shatun的藏身处钥匙" },
            { "664d3dd590294949fe2d81b7", "Grumpy的藏身处钥匙" },
            { "664d3ddfdda2e85aca370d75", "Voron的藏身处钥匙" },
            { "664d3de85f2355673b09aed5", "Leon的藏身处钥匙" },
            { "66acd6702b17692df20144c0", "TerraGroup 仓库钥匙卡" },
            { "6711039f9e648049e50b3307", "TerraGroup 实验室生活区钥匙卡 " },
            { "6761a6ccd9bbb27ad703c48a", "旧屋钥匙" },
            { "6761a6f90575f25e020816a4", "公司主管办公室钥匙" },
            { "678fa929819ddc4c350c0317", "阀门手轮" },
            { "679baa2c61f588ae2b062a24", "一号房钥匙" },
            { "679baa4f59b8961f370dd683", "二号房钥匙" },
            { "679baa5a59b8961f370dd685", "三号房钥匙" },
            { "679baa9091966fe40408f149", "四号房钥匙" },
            { "679baace4e9ca6b3d80586b2", "观察室钥匙" },
            { "679baae891966fe40408f14c", "行刑室钥匙" },
            { "679bab714e9ca6b3d80586b4", "停尸房钥匙" },
            { "679bac1d61f588ae2b062a26", "迷宫钥匙" },
            { "67ab3d4b83869afd170fdd3f", "BBQ-S43 喷枪" },
        };

        // Auto-generated: key item template id -> localized name (en)
        private static readonly Dictionary<string, string> Names_en = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "5448ba0b4bdc2d02308b456c", "Factory emergency exit key" },
            { "5672c92d4bdc2d180f8b4567", "Dorm room 118 key" },
            { "5780cda02459777b272ede61", "Dorm room 306 key" },
            { "5780cf692459777de4559321", "Dorm room 315 key" },
            { "5780cf722459777a5108b9a1", "Dorm room 308 key" },
            { "5780cf7f2459777de4559322", "Dorm room 314 marked key" },
            { "5780cf942459777df90dcb72", "Dorm room 214 key" },
            { "5780cf9e2459777df90dcb73", "Dorm room 218 key" },
            { "5780cfa52459777dfb276eb1", "Dorm room 220 key" },
            { "5780d0532459777a5108b9a2", "Tarcone Director's office key" },
            { "5780d0652459777df90dcb74", "Gas station office key" },
            { "5780d07a2459777de4559324", "Portable cabin key" },
            { "5913611c86f77479e0084092", "Trailer park portable cabin key" },
            { "59136a4486f774447a1ed172", "Dorm guard desk key" },
            { "59136e1e86f774432f15d133", "Dorm room 110 key" },
            { "591382d986f774465a6413a7", "Dorm room 105 key" },
            { "591383f186f7744a4c5edcf3", "Dorm room 104 key" },
            { "5913877a86f774432f15d444", "Gas station storage room key" },
            { "5913915886f774123603c392", "Military checkpoint key" },
            { "5914578086f774123569ffa4", "Dorm room 108 key" },
            { "59148c8a86f774197930e983", "Dorm room 204 key" },
            { "591ae8f986f77406f854be45", "Yotota car key" },
            { "591afe0186f77431bd616a11", "ZB-014 key" },
            { "5937ee6486f77408994ba448", "Machinery key" },
            { "5938144586f77473c2087145", "Portable bunkhouse key" },
            { "5938504186f7740991483f30", "Dorm room 203 key" },
            { "5938603e86f77435642354f4", "Dorm room 206 key" },
            { "59387a4986f77401cc236e62", "Dorm room 114 key" },
            { "5938994586f774523a425196", "Dorm room 103 key" },
            { "593962ca86f774068014d9af", "Unknown key" },
            { "593aa4be86f77457f56379f8", "Dorm room 303 key" },
            { "5a0dc45586f7742f6b0b73e3", "Health Resort west wing office room 104 key" },
            { "5a0dc95c86f77452440fc675", "Health Resort west wing office room 112 key" },
            { "5a0ea64786f7741707720468", "Health Resort east wing office room 107 key" },
            { "5a0ea79b86f7741d4a35298e", "Health Resort universal utility room key" },
            { "5a0eb38b86f774153b320eb0", "SMW car key" },
            { "5a0eb6ac86f7743124037a28", "Cottage back door key" },
            { "5a0ec6d286f7742c0b518fb5", "Health Resort west wing room 205 key" },
            { "5a0ee30786f774023b6ee08f", "Health Resort west wing room 216 key" },
            { "5a0ee34586f774023b6ee092", "Health Resort west wing room 220 key" },
            { "5a0ee37f86f774023657a86f", "Health Resort west wing room 221 key" },
            { "5a0ee4b586f7743698200d22", "Health Resort east wing room 206 key" },
            { "5a0eec9686f77402ac5c39f2", "Health Resort east wing room 310 key" },
            { "5a0eecf686f7740350630097", "Health Resort east wing room 313 key" },
            { "5a0eed4386f77405112912aa", "Health Resort east wing room 314 key" },
            { "5a0eee1486f77402aa773226", "Health Resort east wing room 328 key" },
            { "5a0eff2986f7741fd654e684", "Health Resort west wing room 321 safe key" },
            { "5a0f068686f7745b0d4ea242", "Cottage safe key" },
            { "5a0f08bc86f77478f33b84c2", "Health Resort management office safe key" },
            { "5a0f0f5886f7741c4e32a472", "Health Resort management warehouse safe key" },
            { "5a13eebd86f7746fd639aa93", "Health Resort west wing room 218 key" },
            { "5a13ef0686f7746e5a411744", "Health Resort west wing room 219 key" },
            { "5a13ef7e86f7741290491063", "Health Resort west wing room 301 key" },
            { "5a13f24186f77410e57c5626", "Health Resort east wing room 222 key" },
            { "5a13f35286f77413ef1436b0", "Health Resort east wing room 226 key" },
            { "5a13f46386f7741dd7384b04", "Health Resort west wing room 306 key" },
            { "5a144bdb86f7741d374bbde0", "Health Resort east wing room 205 key" },
            { "5a144dfd86f77445cb5a0982", "Health Resort west wing room 203 key" },
            { "5a1452ee86f7746f33111763", "Health Resort west wing room 222 key" },
            { "5a145d4786f7744cbb6f4a12", "Health Resort east wing room 306 key" },
            { "5a145d7b86f7744cbb6f4a13", "Health Resort east wing room 308 key" },
            { "5a145ebb86f77458f1796f05", "Health Resort east wing room 316 key" },
            { "5ad5ccd186f774446d5706e9", "OLI administration office key" },
            { "5ad5cfbd86f7742c825d6104", "OLI logistics department office key" },
            { "5ad5d20586f77449be26d877", "OLI outlet utility room key" },
            { "5ad5d49886f77455f9731921", "Power substation utility cabin key" },
            { "5ad5d64486f774079b080af8", "NecrusPharm pharmacy key" },
            { "5ad5d7d286f77450166e0a89", "Kiba Arms outer door key" },
            { "5ad5db3786f7743568421cce", "EMERCOM medical unit key" },
            { "5ad7247386f7747487619dc3", "Goshan cash register key" },
            { "5addaffe86f77470b455f900", "Kiba Arms inner grate door key" },
            { "5c1d0c5f86f7744bb2683cf0", "TerraGroup Labs keycard (Blue)" },
            { "5c1d0d6d86f7744bb2683e1f", "TerraGroup Labs keycard (Yellow)" },
            { "5c1d0dc586f7744baf2e7b79", "TerraGroup Labs keycard (Green)" },
            { "5c1d0efb86f7744baf2e7b7b", "TerraGroup Labs keycard (Red)" },
            { "5c1d0f4986f7744bb01837fa", "TerraGroup Labs keycard (Black)" },
            { "5c1e2a1e86f77431ea0ea84c", "TerraGroup Labs manager's office room key" },
            { "5c1e2d1f86f77431e9280bee", "TerraGroup Labs weapon testing area key" },
            { "5c1e495a86f7743109743dfb", "TerraGroup Labs keycard (Violet)" },
            { "5c1f79a086f7746ed066fb8f", "TerraGroup Labs arsenal storage room key" },
            { "5d08d21286f774736e7c94c3", "Shturman's stash key" },
            { "5d80c60f86f77440373c4ece", "RB-BK marked key" },
            { "5d80c62a86f7744036212b3f", "RB-VO marked key" },
            { "5d80c66d86f774405611c7d6", "RB-AO key" },
            { "5d80c6c586f77440351beef1", "RB-OB key" },
            { "5d80c6fc86f774403a401e3c", "RB-TB key" },
            { "5d80c78786f774403a401e3e", "RB-AK key" },
            { "5d80c88d86f77440556dbf07", "RB-AM key" },
            { "5d80c8f586f77440373c4ed0", "RB-OP key" },
            { "5d80c93086f7744036212b41", "RB-MP11 key" },
            { "5d80c95986f77440351beef3", "RB-MP12 key" },
            { "5d80ca9086f774403a401e40", "RB-MP21 key" },
            { "5d80cab086f77440535be201", "RB-MP22 key" },
            { "5d80cb3886f77440556dbf09", "RB-PSP1 key" },
            { "5d80cb5686f77440545d1286", "RB-PSV1 key" },
            { "5d80cbd886f77470855c26c2", "RB-MP13 key" },
            { "5d80ccac86f77470841ff452", "RB-ORB1 key" },
            { "5d80ccdd86f77474f7575e02", "RB-ORB2 key" },
            { "5d80cd1a86f77402aa362f42", "RB-ORB3 key" },
            { "5d8e0db586f7744450412a42", "RB-KORL key" },
            { "5d8e0e0e86f774321140eb56", "RB-KPRL key" },
            { "5d8e15b686f774445103b190", "HEP station storage room key" },
            { "5d8e3ecc86f774414c78d05e", "RB-GN key" },
            { "5d947d3886f774447b415893", "RB-SMP key" },
            { "5d947d4e86f774447b415895", "RB-KSM key" },
            { "5d95d6be86f77424444eb3a7", "RB-PSV2 key" },
            { "5d95d6fa86f77424484aa5e9", "RB-PSP2 key" },
            { "5d9f1fa686f774726974a992", "RB-ST key" },
            { "5da46e3886f774653b7a83fe", "RB-RS key" },
            { "5da5cdcd86f774529238fb9b", "RB-RH key" },
            { "5da743f586f7744014504f72", "USEC stash key" },
            { "5e42c71586f7747f245e1343", "ULTRA medical storage key" },
            { "5e42c81886f7742a01529f57", "Object #11SR keycard" },
            { "5e42c83786f7742a021fdf3c", "Object #21WS keycard" },
            { "5ede7a8229445733cb4c18e2", "RB-PKPM marked key" },
            { "5ede7b0c6d23e5473e6e8c66", "RB-RLSA key" },
            { "5efde6b4f5448336730dbd61", "Keycard with a blue marking" },
            { "5eff09cd30a7dc22fd1ddfed", "Health Resort office key with a blue tape" },
            { "61a64428a8c6aa1b795f0ba1", "Convenience store storage room key" },
            { "61a6444b8c141d68246e2d2f", "Hillside house key" },
            { "61a64492ba05ef10d62adcc1", "Rogue USEC stash key" },
            { "61aa5aed32a4743c3453d319", "Police truck cabin key" },
            { "61aa5b518f5e7a39b41416e2", "Merin car trunk key" },
            { "61aa5ba8018e9821b7368da9", "USEC cottage second safe key" },
            { "61aa81fcb225ac1ead7957c3", "Rogue USEC workshop key" },
            { "62987c658081af308d7558c6", "Radar station commandant room key" },
            { "62987cb98081af308d7558c8", "Conference room key" },
            { "62987da96188c076bc0d8c51", "Operating room key" },
            { "62987dfc402c7f69bf010923", "Shared bedroom marked key" },
            { "62987e26a77ec735f90a2995", "Water treatment plant storage room key" },
            { "62a9cb937377a65d7b070cef", "Rogue USEC barrack key" },
            { "6398fd8ad3de3849057f5128", "Backup hideout key" },
            { "63a39667c9b3aa4b61683e98", "Financial institution office key" },
            { "63a397d3af870e651d58e65b", "Car dealership closed section key" },
            { "63a399193901f439517cafb6", "Car dealership director's office room key" },
            { "63a39c69af870e651d58e6aa", "Store manager's key" },
            { "63a39c7964283b5e9c56b280", "Concordia security room key" },
            { "63a39cb1c9b3aa4b61683ee2", "Construction site bunkhouse key" },
            { "63a39ce4cd6db0635c1975fa", "Supply department director's office key" },
            { "63a39ddda3a2b32b5f6e007a", "Apartment locked room safe key" },
            { "63a39df18a56922e82001f25", "Zmeisky 5 apartment 20 key" },
            { "63a39dfe3901f439517cafba", "Zmeisky 3 apartment 8 key" },
            { "63a39e1d234195315d4020bd", "Primorsky 46-48 skybridge key" },
            { "63a39e49cd6db0635c1975fc", "Archive room key" },
            { "63a39e5b234195315d4020bf", "Housing office second floor safe key" },
            { "63a39e6acd6db0635c1975fe", "Housing office first floor safe key" },
            { "63a39f08cd6db0635c197600", "Pinewood hotel room 215 key" },
            { "63a39f6e64283b5e9c56b289", "Iron gate key" },
            { "63a39fc0af870e651d58e6ae", "Chekannaya 15 apartment key" },
            { "63a39fd1c9b3aa4b61683efb", "Stair landing key" },
            { "63a39fdf1e21260da44a0256", "Cargo container mesh door key" },
            { "63a3a93f8a56922e82001f5d", "Abandoned factory marked key" },
            { "63a71e781031ac76fe773c7d", "Concordia apartment 8 room key" },
            { "63a71e86b7f4570d3a293169", "Concordia apartment 64 office room key" },
            { "63a71e922b25f7513905ca20", "Concordia apartment 64 key" },
            { "63a71eb5b7f4570d3a29316b", "Primorsky 48 apartment key" },
            { "63a71ed21031ac76fe773c7f", "Financial institution small office key" },
            { "64ccc1d4a0f13c24561edf27", "Concordia apartment 34 room key" },
            { "64ccc1ec1779ad6ba200a137", "Concordia apartment 8 home cinema key" },
            { "64ccc1f4ff54fb38131acf27", "Concordia apartment 63 room key" },
            { "64ccc1fe088064307e14a6f7", "Beluga restaurant director key" },
            { "64ccc206793ca11c8f450a38", "TerraGroup meeting room key" },
            { "64ccc2111779ad6ba200a139", "Tarbank cash register department key" },
            { "64ccc246ff54fb38131acf29", "X-ray room key" },
            { "64ccc24de61ea448b507d34d", "TerraGroup security armory key" },
            { "64ccc25f95763a1ae376e447", "Mysterious room marked key" },
            { "64ccc268c41e91416064ebc7", "PE teacher's office key" },
            { "64ce572331dd890873175115", "Aspect company office key" },
            { "64d4b23dc1b37504b41ac2b6", "Rusted bloody key" },
            { "6581998038c79576a2569e11", "Unity Credit Bank cash register key" },
            { "658199972dc4e60f6d556a2f", "Underground parking utility room key" },
            { "658199a0490414548c0fa83b", "Horse restaurant toilet key" },
            { "658199aa38c79576a2569e13", "TerraGroup science office key" },
            { "6582dbe43a2e5248357dbe9a", "\"Negotiation\" room key" },
            { "6582dbf0b8d7830efc45016f", "Relaxation room key" },
            { "6582dc4b6ba9e979af6b79f4", "MVD academy entrance hall guard room key" },
            { "6582dc5740562727a654ebb1", "Real estate agency office room key" },
            { "66265d7be65f224b2e17c6aa", "USEC cottage room key" },
            { "664d3db6db5dea2bad286955", "Shatun's hideout key" },
            { "664d3dd590294949fe2d81b7", "Grumpy's hideout key" },
            { "664d3ddfdda2e85aca370d75", "Voron's hideout key" },
            { "664d3de85f2355673b09aed5", "Leon's hideout key" },
            { "66acd6702b17692df20144c0", "TerraGroup storage room keycard" },
            { "6711039f9e648049e50b3307", "TerraGroup Labs residential unit keycard " },
            { "6761a6ccd9bbb27ad703c48a", "Old house room key" },
            { "6761a6f90575f25e020816a4", "Company director's room key" },
            { "678fa929819ddc4c350c0317", "Valve handwheel" },
            { "679baa2c61f588ae2b062a24", "Key 01" },
            { "679baa4f59b8961f370dd683", "Key 02" },
            { "679baa5a59b8961f370dd685", "Key 03" },
            { "679baa9091966fe40408f149", "Key 04" },
            { "679baace4e9ca6b3d80586b2", "Observation room key" },
            { "679baae891966fe40408f14c", "Torture room key" },
            { "679bab714e9ca6b3d80586b4", "Corpse room key" },
            { "679bac1d61f588ae2b062a26", "Labyrinth key" },
            { "67ab3d4b83869afd170fdd3f", "BBQ-S43 gas torch" },
        };

        // Auto-generated: key item template id -> in-game short name (ch, shown on the item tile)
        private static readonly Dictionary<string, string> NamesShort_ch = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "5448ba0b4bdc2d02308b456c", "工厂" },
            { "5672c92d4bdc2d180f8b4567", "118钥匙" },
            { "5780cda02459777b272ede61", "306钥匙" },
            { "5780cf692459777de4559321", "315钥匙" },
            { "5780cf722459777a5108b9a1", "308钥匙" },
            { "5780cf7f2459777de4559322", "314钥匙" },
            { "5780cf942459777df90dcb72", "214钥匙" },
            { "5780cf9e2459777df90dcb73", "218钥匙" },
            { "5780cfa52459777dfb276eb1", "220钥匙" },
            { "5780d0532459777a5108b9a2", "海关物流" },
            { "5780d0652459777df90dcb74", "加油站" },
            { "5780d07a2459777de4559324", "集装箱" },
            { "5913611c86f77479e0084092", "停车场" },
            { "59136a4486f774447a1ed172", "门卫" },
            { "59136e1e86f774432f15d133", "110钥匙" },
            { "591382d986f774465a6413a7", "105钥匙" },
            { "591383f186f7744a4c5edcf3", "104钥匙" },
            { "5913877a86f774432f15d444", "储藏室" },
            { "5913915886f774123603c392", "检查站" },
            { "5914578086f774123569ffa4", "108钥匙" },
            { "59148c8a86f774197930e983", "204钥匙" },
            { "591ae8f986f77406f854be45", "Yotota" },
            { "591afe0186f77431bd616a11", "ZB-014" },
            { "5937ee6486f77408994ba448", "钥匙" },
            { "5938144586f77473c2087145", "简易工棚" },
            { "5938504186f7740991483f30", "203钥匙" },
            { "5938603e86f77435642354f4", "206钥匙" },
            { "59387a4986f77401cc236e62", "114钥匙" },
            { "5938994586f774523a425196", "103钥匙" },
            { "593962ca86f774068014d9af", "未知钥匙" },
            { "593aa4be86f77457f56379f8", "303钥匙" },
            { "5a0dc45586f7742f6b0b73e3", "西104" },
            { "5a0dc95c86f77452440fc675", "西112" },
            { "5a0ea64786f7741707720468", "东107" },
            { "5a0ea79b86f7741d4a35298e", "杂物间" },
            { "5a0eb38b86f774153b320eb0", "SMW" },
            { "5a0eb6ac86f7743124037a28", "别墅" },
            { "5a0ec6d286f7742c0b518fb5", "西205" },
            { "5a0ee30786f774023b6ee08f", "西216" },
            { "5a0ee34586f774023b6ee092", "西220" },
            { "5a0ee37f86f774023657a86f", "西221" },
            { "5a0ee4b586f7743698200d22", "东206" },
            { "5a0eec9686f77402ac5c39f2", "东310" },
            { "5a0eecf686f7740350630097", "东313" },
            { "5a0eed4386f77405112912aa", "东314" },
            { "5a0eee1486f77402aa773226", "东328" },
            { "5a0eff2986f7741fd654e684", "321保险" },
            { "5a0f068686f7745b0d4ea242", "保险箱 " },
            { "5a0f08bc86f77478f33b84c2", "保险箱 " },
            { "5a0f0f5886f7741c4e32a472", "保险箱" },
            { "5a13eebd86f7746fd639aa93", "西218" },
            { "5a13ef0686f7746e5a411744", "西219" },
            { "5a13ef7e86f7741290491063", "西301" },
            { "5a13f24186f77410e57c5626", "东222" },
            { "5a13f35286f77413ef1436b0", "东226" },
            { "5a13f46386f7741dd7384b04", "西306" },
            { "5a144bdb86f7741d374bbde0", "东205" },
            { "5a144dfd86f77445cb5a0982", "西203" },
            { "5a1452ee86f7746f33111763", "西222" },
            { "5a145d4786f7744cbb6f4a12", "东306" },
            { "5a145d7b86f7744cbb6f4a13", "东308" },
            { "5a145ebb86f77458f1796f05", "东316" },
            { "5ad5ccd186f774446d5706e9", "OLI办公室" },
            { "5ad5cfbd86f7742c825d6104", "物流办公室" },
            { "5ad5d20586f77449be26d877", "OLI杂物间" },
            { "5ad5d49886f77455f9731921", "变电站" },
            { "5ad5d64486f774079b080af8", "药房" },
            { "5ad5d7d286f77450166e0a89", "KIBA外" },
            { "5ad5db3786f7743568421cce", "EMC" },
            { "5ad7247386f7747487619dc3", "Goshan" },
            { "5addaffe86f77470b455f900", "KIBA内" },
            { "5c1d0c5f86f7744bb2683cf0", "蓝卡" },
            { "5c1d0d6d86f7744bb2683e1f", "黄卡" },
            { "5c1d0dc586f7744baf2e7b79", "绿卡" },
            { "5c1d0efb86f7744baf2e7b7b", "红卡" },
            { "5c1d0f4986f7744bb01837fa", "黑卡" },
            { "5c1e2a1e86f77431ea0ea84c", "经理办" },
            { "5c1e2d1f86f77431e9280bee", "测试区" },
            { "5c1e495a86f7743109743dfb", "紫卡" },
            { "5c1f79a086f7746ed066fb8f", "军械库" },
            { "5d08d21286f774736e7c94c3", "SSK" },
            { "5d80c60f86f77440373c4ece", "RB-BK" },
            { "5d80c62a86f7744036212b3f", "RB-VO" },
            { "5d80c66d86f774405611c7d6", "RB-AO" },
            { "5d80c6c586f77440351beef1", "RB-OB" },
            { "5d80c6fc86f774403a401e3c", "RB-TB" },
            { "5d80c78786f774403a401e3e", "RB-AK" },
            { "5d80c88d86f77440556dbf07", "RB-AM" },
            { "5d80c8f586f77440373c4ed0", "RB-OP" },
            { "5d80c93086f7744036212b41", "RB-MP11" },
            { "5d80c95986f77440351beef3", "RB-MP12" },
            { "5d80ca9086f774403a401e40", "RB-MP21" },
            { "5d80cab086f77440535be201", "RB-MP22" },
            { "5d80cb3886f77440556dbf09", "RB-PSP1" },
            { "5d80cb5686f77440545d1286", "RB-PSV1" },
            { "5d80cbd886f77470855c26c2", "RB-MP13" },
            { "5d80ccac86f77470841ff452", "RB-ORB1" },
            { "5d80ccdd86f77474f7575e02", "RB-ORB2" },
            { "5d80cd1a86f77402aa362f42", "RB-ORB3" },
            { "5d8e0db586f7744450412a42", "RB-KORL" },
            { "5d8e0e0e86f774321140eb56", "RB-KPRL" },
            { "5d8e15b686f774445103b190", "水电站" },
            { "5d8e3ecc86f774414c78d05e", "RB-GN" },
            { "5d947d3886f774447b415893", "RB-SMP" },
            { "5d947d4e86f774447b415895", "RB-KSM" },
            { "5d95d6be86f77424444eb3a7", "RB-PSV2" },
            { "5d95d6fa86f77424484aa5e9", "RB-PSP2" },
            { "5d9f1fa686f774726974a992", "RB-ST" },
            { "5da46e3886f774653b7a83fe", "RB-RS" },
            { "5da5cdcd86f774529238fb9b", "RB-RH" },
            { "5da743f586f7744014504f72", "USEC" },
            { "5e42c71586f7747f245e1343", "ULTRA医疗" },
            { "5e42c81886f7742a01529f57", "#11SR" },
            { "5e42c83786f7742a021fdf3c", "#21WS" },
            { "5ede7a8229445733cb4c18e2", "RB-PKPM" },
            { "5ede7b0c6d23e5473e6e8c66", "RB-RLSA" },
            { "5efde6b4f5448336730dbd61", "钥匙卡" },
            { "5eff09cd30a7dc22fd1ddfed", "胶带钥匙" },
            { "61a64428a8c6aa1b795f0ba1", "便利店" },
            { "61a6444b8c141d68246e2d2f", "小屋" },
            { "61a64492ba05ef10d62adcc1", "仓库" },
            { "61aa5aed32a4743c3453d319", "警用" },
            { "61aa5b518f5e7a39b41416e2", "Merin" },
            { "61aa5ba8018e9821b7368da9", "USEC 2" },
            { "61aa81fcb225ac1ead7957c3", "工作间" },
            { "62987c658081af308d7558c6", "雷达站" },
            { "62987cb98081af308d7558c8", "会议室" },
            { "62987da96188c076bc0d8c51", "手术室" },
            { "62987dfc402c7f69bf010923", "套间" },
            { "62987e26a77ec735f90a2995", "储藏室" },
            { "62a9cb937377a65d7b070cef", "营房" },
            { "6398fd8ad3de3849057f5128", "藏身处" },
            { "63a39667c9b3aa4b61683e98", "金融" },
            { "63a397d3af870e651d58e65b", "LexOs封闭" },
            { "63a399193901f439517cafb6", "LexOs" },
            { "63a39c69af870e651d58e6aa", "商店" },
            { "63a39c7964283b5e9c56b280", "Conc安保" },
            { "63a39cb1c9b3aa4b61683ee2", "工地" },
            { "63a39ce4cd6db0635c1975fa", "供应部" },
            { "63a39ddda3a2b32b5f6e007a", "公寓保险箱" },
            { "63a39df18a56922e82001f25", "公寓20" },
            { "63a39dfe3901f439517cafba", "公寓8" },
            { "63a39e1d234195315d4020bd", "天桥46-48" },
            { "63a39e49cd6db0635c1975fc", "档案室" },
            { "63a39e5b234195315d4020bf", "房管处2" },
            { "63a39e6acd6db0635c1975fe", "房管处1" },
            { "63a39f08cd6db0635c197600", "酒店215" },
            { "63a39f6e64283b5e9c56b289", "铁门" },
            { "63a39fc0af870e651d58e6ae", "Chek. 15" },
            { "63a39fd1c9b3aa4b61683efb", "楼梯" },
            { "63a39fdf1e21260da44a0256", "军火" },
            { "63a3a93f8a56922e82001f5d", "废弃工厂" },
            { "63a71e781031ac76fe773c7d", "Conc 8" },
            { "63a71e86b7f4570d3a293169", "Conc办公室" },
            { "63a71e922b25f7513905ca20", "Conc64" },
            { "63a71eb5b7f4570d3a29316b", "Prim 48" },
            { "63a71ed21031ac76fe773c7f", "金融小房间" },
            { "64ccc1d4a0f13c24561edf27", "Concd 34" },
            { "64ccc1ec1779ad6ba200a137", "影院" },
            { "64ccc1f4ff54fb38131acf27", "Conc 63" },
            { "64ccc1fe088064307e14a6f7", "白鲸" },
            { "64ccc206793ca11c8f450a38", "TG会议室" },
            { "64ccc2111779ad6ba200a139", "塔科夫银行" },
            { "64ccc246ff54fb38131acf29", "X光" },
            { "64ccc24de61ea448b507d34d", "TG军械库" },
            { "64ccc25f95763a1ae376e447", "Chek. 13" },
            { "64ccc268c41e91416064ebc7", "体育" },
            { "64ce572331dd890873175115", "Aspect" },
            { "64d4b23dc1b37504b41ac2b6", "生锈钥匙" },
            { "6581998038c79576a2569e11", "UC收纳机" },
            { "658199972dc4e60f6d556a2f", "杂物间" },
            { "658199a0490414548c0fa83b", "厕所" },
            { "658199aa38c79576a2569e13", "科学家" },
            { "6582dbe43a2e5248357dbe9a", "调解室" },
            { "6582dbf0b8d7830efc45016f", "休息室" },
            { "6582dc4b6ba9e979af6b79f4", "内务部" },
            { "6582dc5740562727a654ebb1", "不动产" },
            { "66265d7be65f224b2e17c6aa", "USEC小屋" },
            { "664d3db6db5dea2bad286955", "Shatun" },
            { "664d3dd590294949fe2d81b7", "Grumpy" },
            { "664d3ddfdda2e85aca370d75", "Voron" },
            { "664d3de85f2355673b09aed5", "Leon" },
            { "66acd6702b17692df20144c0", "化工厂" },
            { "6711039f9e648049e50b3307", "生活区" },
            { "6761a6ccd9bbb27ad703c48a", "技术站" },
            { "6761a6f90575f25e020816a4", "主管办" },
            { "678fa929819ddc4c350c0317", "手轮" },
            { "679baa2c61f588ae2b062a24", "钥匙 01" },
            { "679baa4f59b8961f370dd683", "钥匙 02" },
            { "679baa5a59b8961f370dd685", "钥匙 03" },
            { "679baa9091966fe40408f149", "钥匙 04" },
            { "679baace4e9ca6b3d80586b2", "观察室" },
            { "679baae891966fe40408f14c", "行刑室" },
            { "679bab714e9ca6b3d80586b4", "停尸房" },
            { "679bac1d61f588ae2b062a26", "迷宫" },
            { "67ab3d4b83869afd170fdd3f", "BBQ-S43" },
        };

        // Auto-generated: key item template id -> in-game short name (en, shown on the item tile)
        private static readonly Dictionary<string, string> NamesShort_en = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "5448ba0b4bdc2d02308b456c", "Factory" },
            { "5672c92d4bdc2d180f8b4567", "Dorm 118" },
            { "5780cda02459777b272ede61", "Dorm 306" },
            { "5780cf692459777de4559321", "Dorm 315" },
            { "5780cf722459777a5108b9a1", "Dorm 308" },
            { "5780cf7f2459777de4559322", "Dorm mrk." },
            { "5780cf942459777df90dcb72", "Dorm 214" },
            { "5780cf9e2459777df90dcb73", "Dorm 218" },
            { "5780cfa52459777dfb276eb1", "Dorm 220" },
            { "5780d0532459777a5108b9a2", "Director's" },
            { "5780d0652459777df90dcb74", "Gas office" },
            { "5780d07a2459777de4559324", "Cabin" },
            { "5913611c86f77479e0084092", "Cabin" },
            { "59136a4486f774447a1ed172", "GDesk" },
            { "59136e1e86f774432f15d133", "Dorm 110" },
            { "591382d986f774465a6413a7", "Dorm 105" },
            { "591383f186f7744a4c5edcf3", "Dorm 104" },
            { "5913877a86f774432f15d444", "Gas store" },
            { "5913915886f774123603c392", "Checkpoint" },
            { "5914578086f774123569ffa4", "Dorm 108" },
            { "59148c8a86f774197930e983", "Dorm 204" },
            { "591ae8f986f77406f854be45", "Yotota" },
            { "591afe0186f77431bd616a11", "ZB-014" },
            { "5937ee6486f77408994ba448", "Machinery" },
            { "5938144586f77473c2087145", "Bunkhouse" },
            { "5938504186f7740991483f30", "Dorm 203" },
            { "5938603e86f77435642354f4", "Dorm 206" },
            { "59387a4986f77401cc236e62", "Dorm 114" },
            { "5938994586f774523a425196", "Dorm 103" },
            { "593962ca86f774068014d9af", "Unknown" },
            { "593aa4be86f77457f56379f8", "Dorm 303" },
            { "5a0dc45586f7742f6b0b73e3", "W104 San" },
            { "5a0dc95c86f77452440fc675", "W112 San" },
            { "5a0ea64786f7741707720468", "E107 San" },
            { "5a0ea79b86f7741d4a35298e", "San util." },
            { "5a0eb38b86f774153b320eb0", "SMW" },
            { "5a0eb6ac86f7743124037a28", "Cottage" },
            { "5a0ec6d286f7742c0b518fb5", "W205 San" },
            { "5a0ee30786f774023b6ee08f", "W216 San" },
            { "5a0ee34586f774023b6ee092", "W220 San" },
            { "5a0ee37f86f774023657a86f", "W221 San" },
            { "5a0ee4b586f7743698200d22", "E206 San" },
            { "5a0eec9686f77402ac5c39f2", "E310 San" },
            { "5a0eecf686f7740350630097", "E313 San" },
            { "5a0eed4386f77405112912aa", "E314 San" },
            { "5a0eee1486f77402aa773226", "E328 San" },
            { "5a0eff2986f7741fd654e684", "W321 safe" },
            { "5a0f068686f7745b0d4ea242", "Safe" },
            { "5a0f08bc86f77478f33b84c2", "Safe" },
            { "5a0f0f5886f7741c4e32a472", "Safe" },
            { "5a13eebd86f7746fd639aa93", "W218 San" },
            { "5a13ef0686f7746e5a411744", "W219 San" },
            { "5a13ef7e86f7741290491063", "W301 San" },
            { "5a13f24186f77410e57c5626", "E222 San" },
            { "5a13f35286f77413ef1436b0", "E226 San" },
            { "5a13f46386f7741dd7384b04", "W306 San" },
            { "5a144bdb86f7741d374bbde0", "E205 San" },
            { "5a144dfd86f77445cb5a0982", "W203 San" },
            { "5a1452ee86f7746f33111763", "W222 San" },
            { "5a145d4786f7744cbb6f4a12", "E306 San" },
            { "5a145d7b86f7744cbb6f4a13", "E308 San" },
            { "5a145ebb86f77458f1796f05", "E316 San" },
            { "5ad5ccd186f774446d5706e9", "OLI office" },
            { "5ad5cfbd86f7742c825d6104", "OLI Log." },
            { "5ad5d20586f77449be26d877", "OLI util." },
            { "5ad5d49886f77455f9731921", "Power" },
            { "5ad5d64486f774079b080af8", "Pharmacy" },
            { "5ad5d7d286f77450166e0a89", "KIBA outer" },
            { "5ad5db3786f7743568421cce", "EMC" },
            { "5ad7247386f7747487619dc3", "Goshan reg." },
            { "5addaffe86f77470b455f900", "KIBA inner" },
            { "5c1d0c5f86f7744bb2683cf0", "Blue" },
            { "5c1d0d6d86f7744bb2683e1f", "Yellow" },
            { "5c1d0dc586f7744baf2e7b79", "Green" },
            { "5c1d0efb86f7744baf2e7b7b", "Red" },
            { "5c1d0f4986f7744bb01837fa", "Black" },
            { "5c1e2a1e86f77431ea0ea84c", "TGL MO" },
            { "5c1e2d1f86f77431e9280bee", "TGL WT" },
            { "5c1e495a86f7743109743dfb", "Violet" },
            { "5c1f79a086f7746ed066fb8f", "TGL ASR" },
            { "5d08d21286f774736e7c94c3", "SSK" },
            { "5d80c60f86f77440373c4ece", "RB-BK mrk." },
            { "5d80c62a86f7744036212b3f", "RB-VO mrk." },
            { "5d80c66d86f774405611c7d6", "RB-AO" },
            { "5d80c6c586f77440351beef1", "RB-OB" },
            { "5d80c6fc86f774403a401e3c", "RB-TB" },
            { "5d80c78786f774403a401e3e", "RB-AK" },
            { "5d80c88d86f77440556dbf07", "RB-AM" },
            { "5d80c8f586f77440373c4ed0", "RB-OP" },
            { "5d80c93086f7744036212b41", "RB-MP11" },
            { "5d80c95986f77440351beef3", "RB-MP12" },
            { "5d80ca9086f774403a401e40", "RB-MP21" },
            { "5d80cab086f77440535be201", "RB-MP22" },
            { "5d80cb3886f77440556dbf09", "RB-PSP1" },
            { "5d80cb5686f77440545d1286", "RB-PSV1" },
            { "5d80cbd886f77470855c26c2", "RB-MP13" },
            { "5d80ccac86f77470841ff452", "RB-ORB1" },
            { "5d80ccdd86f77474f7575e02", "RB-ORB2" },
            { "5d80cd1a86f77402aa362f42", "RB-ORB3" },
            { "5d8e0db586f7744450412a42", "RB-KORL" },
            { "5d8e0e0e86f774321140eb56", "RB-KPRL" },
            { "5d8e15b686f774445103b190", "HEPS" },
            { "5d8e3ecc86f774414c78d05e", "RB-GN" },
            { "5d947d3886f774447b415893", "RB-SMP" },
            { "5d947d4e86f774447b415895", "RB-KSM" },
            { "5d95d6be86f77424444eb3a7", "RB-PSV2" },
            { "5d95d6fa86f77424484aa5e9", "RB-PSP2" },
            { "5d9f1fa686f774726974a992", "RB-ST" },
            { "5da46e3886f774653b7a83fe", "RB-RS" },
            { "5da5cdcd86f774529238fb9b", "RB-RH" },
            { "5da743f586f7744014504f72", "USEC" },
            { "5e42c71586f7747f245e1343", "ULTRA med." },
            { "5e42c81886f7742a01529f57", "#11SR" },
            { "5e42c83786f7742a021fdf3c", "#21WS" },
            { "5ede7a8229445733cb4c18e2", "RB-PKPM mrk." },
            { "5ede7b0c6d23e5473e6e8c66", "RB-RLSA" },
            { "5efde6b4f5448336730dbd61", "Keycard" },
            { "5eff09cd30a7dc22fd1ddfed", "San tape" },
            { "61a64428a8c6aa1b795f0ba1", "Store" },
            { "61a6444b8c141d68246e2d2f", "House" },
            { "61a64492ba05ef10d62adcc1", "Stash" },
            { "61aa5aed32a4743c3453d319", "Police" },
            { "61aa5b518f5e7a39b41416e2", "Merin" },
            { "61aa5ba8018e9821b7368da9", "USEC 2" },
            { "61aa81fcb225ac1ead7957c3", "Workshop" },
            { "62987c658081af308d7558c6", "Radar" },
            { "62987cb98081af308d7558c8", "Conf." },
            { "62987da96188c076bc0d8c51", "OR" },
            { "62987dfc402c7f69bf010923", "Bedroom" },
            { "62987e26a77ec735f90a2995", "WTP store" },
            { "62a9cb937377a65d7b070cef", "Barrack" },
            { "6398fd8ad3de3849057f5128", "Hideout" },
            { "63a39667c9b3aa4b61683e98", "Finance" },
            { "63a397d3af870e651d58e65b", "LexOs sect." },
            { "63a399193901f439517cafb6", "LexOs" },
            { "63a39c69af870e651d58e6aa", "Store" },
            { "63a39c7964283b5e9c56b280", "Conc sec." },
            { "63a39cb1c9b3aa4b61683ee2", "Construct." },
            { "63a39ce4cd6db0635c1975fa", "Supp." },
            { "63a39ddda3a2b32b5f6e007a", "Apt. safe" },
            { "63a39df18a56922e82001f25", "Zm apt. 20" },
            { "63a39dfe3901f439517cafba", "Zm apt. 8" },
            { "63a39e1d234195315d4020bd", "Skybridge 46-48" },
            { "63a39e49cd6db0635c1975fc", "Archives" },
            { "63a39e5b234195315d4020bf", "HO 2" },
            { "63a39e6acd6db0635c1975fe", "HO 1" },
            { "63a39f08cd6db0635c197600", "Pnwd 215" },
            { "63a39f6e64283b5e9c56b289", "Iron gate" },
            { "63a39fc0af870e651d58e6ae", "Chek 15" },
            { "63a39fd1c9b3aa4b61683efb", "Stairs" },
            { "63a39fdf1e21260da44a0256", "Container" },
            { "63a3a93f8a56922e82001f5d", "Aband." },
            { "63a71e781031ac76fe773c7d", "Conc 8" },
            { "63a71e86b7f4570d3a293169", "Conc off." },
            { "63a71e922b25f7513905ca20", "Conc 64" },
            { "63a71eb5b7f4570d3a29316b", "Prim 48" },
            { "63a71ed21031ac76fe773c7f", "Finance s" },
            { "64ccc1d4a0f13c24561edf27", "Conc 34" },
            { "64ccc1ec1779ad6ba200a137", "Cinema" },
            { "64ccc1f4ff54fb38131acf27", "Conc 63" },
            { "64ccc1fe088064307e14a6f7", "Beluga" },
            { "64ccc206793ca11c8f450a38", "TG meeting" },
            { "64ccc2111779ad6ba200a139", "Tarbank" },
            { "64ccc246ff54fb38131acf29", "X-ray" },
            { "64ccc24de61ea448b507d34d", "TG arm." },
            { "64ccc25f95763a1ae376e447", "Chek. 13" },
            { "64ccc268c41e91416064ebc7", "PE" },
            { "64ce572331dd890873175115", "Aspect" },
            { "64d4b23dc1b37504b41ac2b6", "Rusted" },
            { "6581998038c79576a2569e11", "UC reg." },
            { "658199972dc4e60f6d556a2f", "Utility" },
            { "658199a0490414548c0fa83b", "Toilet" },
            { "658199aa38c79576a2569e13", "Science" },
            { "6582dbe43a2e5248357dbe9a", "Negotiations" },
            { "6582dbf0b8d7830efc45016f", "Relax" },
            { "6582dc4b6ba9e979af6b79f4", "MVD" },
            { "6582dc5740562727a654ebb1", "REA" },
            { "66265d7be65f224b2e17c6aa", "USEC cott." },
            { "664d3db6db5dea2bad286955", "Shatun" },
            { "664d3dd590294949fe2d81b7", "Grumpy" },
            { "664d3ddfdda2e85aca370d75", "Voron" },
            { "664d3de85f2355673b09aed5", "Leon" },
            { "66acd6702b17692df20144c0", "Polikhim" },
            { "6711039f9e648049e50b3307", "Res. unit" },
            { "6761a6ccd9bbb27ad703c48a", "Depot" },
            { "6761a6f90575f25e020816a4", "Company" },
            { "678fa929819ddc4c350c0317", "Wheel" },
            { "679baa2c61f588ae2b062a24", "Key 01" },
            { "679baa4f59b8961f370dd683", "Key 02" },
            { "679baa5a59b8961f370dd685", "Key 03" },
            { "679baa9091966fe40408f149", "Key 04" },
            { "679baace4e9ca6b3d80586b2", "Observe" },
            { "679baae891966fe40408f14c", "Torture" },
            { "679bab714e9ca6b3d80586b4", "Corpses" },
            { "679bac1d61f588ae2b062a26", "Labyrinth" },
            { "67ab3d4b83869afd170fdd3f", "BBQ-S43" },
        };


        internal static int CountChinese { get { return Names_ch.Count; } }
        internal static int CountEnglish { get { return Names_en.Count; } }
        internal static int CountShortChinese { get { return NamesShort_ch.Count; } }
        internal static int CountShortEnglish { get { return NamesShort_en.Count; } }

        internal static string Resolve(string id, string language)
        {
            return Resolve(id, language, false);
        }

        /// <summary>
        /// id -> localized key name. useShort picks the in-game short name (the one the game shows
        /// on the item tile, e.g. "118钥匙" / "Dorm 118") and falls back to the full name when the
        /// short one is missing or empty.
        /// </summary>
        internal static string Resolve(string id, string language, bool useShort)
        {
            if (string.IsNullOrEmpty(id)) return null;
            string v;

            // Chinese is the preferred display language for this build.
            // English is only used when the Chinese locale has no entry for that key.
            bool english = language == KeyNamesPlugin.OptEnglish;

            if (useShort)
            {
                string shortName;
                if (english)
                {
                    if (NamesShort_en.TryGetValue(id, out shortName) && !string.IsNullOrEmpty(shortName)) return shortName;
                }
                else
                {
                    if (NamesShort_ch.TryGetValue(id, out shortName) && !string.IsNullOrEmpty(shortName)) return shortName;
                }
                // no usable short name for the preferred language - fall through to the full names
            }

            if (english)
            {
                if (Names_en.TryGetValue(id, out v)) return v;
                if (Names_ch.TryGetValue(id, out v)) return v;
            }
            else
            {
                if (Names_ch.TryGetValue(id, out v)) return v;
                if (Names_en.TryGetValue(id, out v)) return v;
            }

            // Not in the built-in table: either a key the table never covered (the game has more
            // than these 196) or an item added by another server mod. Both are resolved from disk:
            // names.json override -> mod CustomLocales -> SPT locale database.
            return KeyNamesPlugin.ResolveExternal(id, english, useShort);
        }
    }

    /// <summary>
    /// MapMarker.AssociatedItemId is assigned right after the marker text inside
    /// MapMarker.Create&lt;T&gt;, so its setter is the safe place to re-label the marker.
    /// </summary>
    [HarmonyPatch]
    internal static class MapMarkerPatches
    {
        [HarmonyPrepare]
        private static bool Prepare()
        {
            var t = AccessTools.TypeByName("DynamicMaps.UI.Components.MapMarker");
            if (t == null)
            {
                ModLog.Warn("[DMKeyNames] DynamicMaps MapMarker not found - is DynamicMaps installed?");
                return false;
            }
            var p = AccessTools.Property(t, "AssociatedItemId");
            if (p == null || p.GetSetMethod(true) == null)
            {
                ModLog.Warn("[DMKeyNames] MapMarker.AssociatedItemId setter not found.");
                return false;
            }
            return true;
        }

        [HarmonyTargetMethod]
        private static System.Reflection.MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("DynamicMaps.UI.Components.MapMarker");
            var p = AccessTools.Property(t, "AssociatedItemId");
            return p.GetSetMethod(true);
        }

        [HarmonyPostfix]
        private static void Postfix(object __instance, string value)
        {
            try
            {
                if (__instance == null || string.IsNullOrEmpty(value)) return;
                if (KeyNamesPlugin.ShowKeyNames == null || !KeyNamesPlugin.ShowKeyNames.Value) return;

                var t = __instance.GetType();

                var catProp = AccessTools.Property(t, "Category");
                string category = catProp != null ? catProp.GetValue(__instance, null) as string : null;

                // Some map packs (e.g. Labyrinth) write the category as "Locked Door" with a
                // space and Shoreline writes "Locked", so compare ignoring spaces/case
                // instead of doing an exact match against DynamicMaps' own "LockedDoor".
                if (category == null) return;
                string catKey = NormalizeCategory(category);
                if (catKey != "lockeddoor" && catKey != "locked")
                {
                    KeyNamesPlugin.ReportSkippedCategory(category, value);
                    return;
                }

                var textProp = AccessTools.Property(t, "Text");
                if (textProp == null) return;
                string text = textProp.GetValue(__instance, null) as string;

                KeyNamesPlugin.LabelPlan plan = KeyNamesPlugin.PlanLabel(text, value);
                if (!plan.IsKeyMarker) return;

                // 文字的最终形态（是否需要合并到两扇门中间）由分组逻辑决定并写入
                KeyNamesPlugin.RegisterDoor(t, __instance, text, value, plan);

                if (KeyNamesPlugin.AlwaysShowLabels != null && KeyNamesPlugin.AlwaysShowLabels.Value)
                {
                    KeyNamesPlugin.ApplyAlwaysVisibleLabel(t, __instance);
                }

                KeyNamesPlugin.RememberLabeledMarker(__instance);
                KeyNamesPlugin.ApplyFontSize(t, __instance);

                if (KeyNamesPlugin.IsDebug)
                {
                    // LogInfo on purpose: BepInEx discards Debug level by default and this is
                    // already gated behind the Debug Logging config entry.
                    string applied = textProp.GetValue(__instance, null) as string;
                    ModLog.Info("[DMKeyNames] tr=" + KeyNamesPlugin.Truncate(text) +
                                               " | key=" + value +
                                               " | cfgLang=" + KeyNamesPlugin.LanguageValue +
                                               " | lang=" + KeyNamesPlugin.ResolveLanguage() +
                                               " | changed=" + (applied != text) +
                                               " | now=" + KeyNamesPlugin.Truncate(applied));
                }
            }
            catch (Exception ex)
            {
                ModLog.Warn("[DMKeyNames] relabel failed: " + ex.Message);
            }
        }

        /// <summary>Normalizes a marker category for comparison: "Locked Door" -> "lockeddoor".</summary>
        internal static string NormalizeCategory(string category)
        {
            if (string.IsNullOrEmpty(category)) return null;
            return category.Replace(" ", "").Replace("_", "").Replace("-", "").ToLowerInvariant();
        }
    }

    /// <summary>
    /// Re-applies the configured font size whenever a labeled marker refreshes its layer status.
    /// MapView.AddMapMarker calls this right after the marker is created, and it runs again on
    /// hover and on map level changes - so changing "Key Name Font Size" in the F12 config takes
    /// effect on the already loaded map instead of requiring a reload.
    /// </summary>
    [HarmonyPatch]
    internal static class MapMarkerStatusPatches
    {
        [HarmonyPrepare]
        private static bool Prepare()
        {
            var t = AccessTools.TypeByName("DynamicMaps.UI.Components.MapMarker");
            if (t == null) return false;
            if (AccessTools.Method(t, "HandleNewLayerStatus") == null)
            {
                ModLog.Warn(
                    "[DMKeyNames] MapMarker.HandleNewLayerStatus not found - font size changes need a map reload.");
                return false;
            }
            return true;
        }

        [HarmonyTargetMethod]
        private static System.Reflection.MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("DynamicMaps.UI.Components.MapMarker");
            return AccessTools.Method(t, "HandleNewLayerStatus");
        }

        [HarmonyPostfix]
        private static void Postfix(object __instance)
        {
            if (__instance == null) return;
            if (!KeyNamesPlugin.IsLabeledMarker(__instance)) return;
            KeyNamesPlugin.ApplyFontSize(__instance.GetType(), __instance);
        }
    }

    /// <summary>
    /// 标记销毁时把它从"同钥匙双开门"分组里摘掉，这样剩下那扇门会自动接管钥匙名的显示，
    /// 同时避免跨战局/跨地图累积过期记录。
    /// </summary>
    [HarmonyPatch]
    internal static class MapMarkerDestroyPatches
    {
        [HarmonyPrepare]
        private static bool Prepare()
        {
            var t = AccessTools.TypeByName("DynamicMaps.UI.Components.MapMarker");
            if (t == null) return false;
            if (AccessTools.Method(t, "OnDestroy") == null)
            {
                ModLog.Warn(
                    "[DMKeyNames] MapMarker.OnDestroy not found - merged door bookkeeping may go stale.");
                return false;
            }
            return true;
        }

        [HarmonyTargetMethod]
        private static System.Reflection.MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("DynamicMaps.UI.Components.MapMarker");
            return AccessTools.Method(t, "OnDestroy");
        }

        [HarmonyPostfix]
        private static void Postfix(object __instance)
        {
            KeyNamesPlugin.UnregisterDoor(__instance);
        }
    }

    /// <summary>
    /// Releases the merged-door bookkeeping when DynamicMaps tears a map down.
    ///
    /// MapMarker.OnDestroy covers the normal case one marker at a time. This is the safety net for
    /// the case that patch warns about: if DynamicMaps ever drops OnDestroy, the per-marker hook
    /// disables itself and the static door dictionaries would keep pinning destroyed markers for the
    /// whole process lifetime, growing with every raid.
    ///
    /// MapView.UnloadMap is the right place: it is public, takes no arguments, releases DynamicMaps'
    /// own marker/label/layer lists, and is called both at raid end (ModdedMapScreen.OnRaidEnd) and
    /// before another map is loaded (MapView.LoadMap). OnDisable is patched as well, since that is
    /// what fires when the map screen itself goes away.
    /// </summary>
    [HarmonyPatch]
    internal static class MapViewLifecyclePatches
    {
        [HarmonyPrepare]
        private static bool Prepare()
        {
            var t = AccessTools.TypeByName("DynamicMaps.UI.Components.MapView");
            if (t == null)
            {
                ModLog.Warn("[DMKeyNames] DynamicMaps MapView not found - door bookkeeping is released " +
                            "per marker only.");
                return false;
            }
            if (AccessTools.Method(t, "UnloadMap") == null)
            {
                ModLog.Warn("[DMKeyNames] MapView.UnloadMap not found - door bookkeeping is released " +
                            "per marker only.");
                return false;
            }
            return true;
        }

        [HarmonyTargetMethod]
        private static System.Reflection.MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("DynamicMaps.UI.Components.MapView");
            return AccessTools.Method(t, "UnloadMap");
        }

        [HarmonyPostfix]
        private static void Postfix()
        {
            KeyNamesPlugin.ClearDoorBookkeeping();
        }
    }

    /// <summary>
    /// Same cleanup for the map screen being disabled, for the case where UnloadMap is never reached.
    /// Patched separately so a missing OnDisable cannot take the UnloadMap hook down with it.
    /// </summary>
    [HarmonyPatch]
    internal static class MapViewDisablePatches
    {
        [HarmonyPrepare]
        private static bool Prepare()
        {
            var t = AccessTools.TypeByName("DynamicMaps.UI.Components.MapView");
            if (t == null) return false;
            // OnDisable is private on MapView, so ask Harmony to search non-public members too.
            return AccessTools.Method(t, "OnDisable") != null;
        }

        [HarmonyTargetMethod]
        private static System.Reflection.MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("DynamicMaps.UI.Components.MapView");
            return AccessTools.Method(t, "OnDisable");
        }

        [HarmonyPostfix]
        private static void Postfix()
        {
            KeyNamesPlugin.ClearDoorBookkeeping();
        }
    }

    /// <summary>
    /// Key names for ids that are not in the generated tables, resolved from disk at runtime.
    ///
    /// Why this exists: the built-in tables are static, so any key the game adds - or that another
    /// server mod adds, e.g. WTT-ContentBackport - had no name at all. An unresolved id also made
    /// the whole marker get dropped, so such doors lost the always-visible and font-size treatment
    /// as well and silently fell back to DynamicMaps' hover-only behaviour.
    ///
    /// Lookup order (first hit wins):
    ///   1. dynamicmaps.keynames.names.json next to the DLL   (hand-maintained override table)
    ///   2. the same file in BepInEx\config
    ///   3. user\mods\*\db\CustomLocales\&lt;lang&gt;.json        (names shipped by content mods)
    ///   4. SPT_Data\database\locales\global\&lt;lang&gt;.json     (the game's own locale database)
    ///
    /// Layers 3 and 4 complement each other: the SPT database is the pre-mod one so it never
    /// contains mod items, while the mod locales never contain vanilla items.
    ///
    /// Everything is lazy and cached: each file is read at most once per session, and only once a
    /// marker actually needs a name the built-in tables could not provide. Any failure degrades
    /// silently to the built-in tables plus one warning - the plugin must never break the map.
    ///
    /// Threading: the tables below are plain dictionaries with no locking. That is a deliberate
    /// assumption, not an oversight - every entry point is reached from the Unity main thread
    /// (Harmony postfixes on MapMarker, the Awake hook, and config SettingChanged callbacks), so
    /// there are no concurrent readers or writers. Verify before moving any call onto a worker
    /// thread; the scan is idempotent but not safe to run twice at once.
    /// </summary>
    internal static class ExternalKeyNames
    {
        private const string SidecarName = "dynamicmaps.keynames.names.json";

        // One "<24 hex id> Name" / "<24 hex id> ShortName" entry inside a locale file.
        private static readonly System.Text.RegularExpressions.Regex LocaleEntry =
            new System.Text.RegularExpressions.Regex(
                "\"([0-9a-fA-F]{24})\\s+(Name|ShortName)\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        // One record per file that was read, kept for the diagnostics log.
        private sealed class Source
        {
            public string Path;
            public int Entries;
            public bool Failed;
        }

        private static readonly List<Source> Loaded = new List<Source>();
        private static bool _scanned;
        private static bool _warned;

        // Readable entries of every locale file, merged from all files of the same language in
        // priority order. Slot keys are LANG + ":" + (Name|ShortName), e.g. "ch:Name".
        // A later file never overrides an entry an earlier one already provided.
        private static readonly Dictionary<string, Dictionary<string, string>> Locale =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        // Hand-written overrides, same slot keys. These win over anything discovered on disk.
        private static readonly Dictionary<string, Dictionary<string, string>> Overrides =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        // The game root and the log source are resolved from BepInEx when running inside the game.
        // Both are settable so the name-resolution code can be exercised without Unity/BepInEx.
        private static string _injectedRoot;
        private static ManualLogSource _log;

        /// <summary>Logger used for the warnings raised here; falls back to the plugin's own log.</summary>
        internal static ManualLogSource Log
        {
            get { return _log != null ? _log : KeyNamesPlugin.Log; }
            set { _log = value; }
        }

        /// <summary>Injects a game root so the resolver can run outside the game (verification only).</summary>
        internal static void InjectRoot(string root)
        {
            _injectedRoot = root;
        }

        /// <summary>
        /// Folder holding EscapeFromTarkov.exe. An injected value wins; otherwise BepInEx's own
        /// answer is used when it looks usable. BepInEx can answer with an empty string when it has
        /// not finished initialising, and Path.Combine would then silently degrade into a relative
        /// path, so that is treated as "unknown" and the plugin folder is used to find the root.
        /// </summary>
        private static string Root()
        {
            if (!string.IsNullOrEmpty(_injectedRoot) && Directory.Exists(_injectedRoot)) return _injectedRoot;

            string fromBepInEx = KeyNamesPlugin.GameRoot();
            if (!string.IsNullOrEmpty(fromBepInEx) && Directory.Exists(fromBepInEx)) return fromBepInEx;

            return DeriveRootFromPluginFolder();
        }

        /// <summary>
        /// Walks up from the plugin DLL looking for the folder that holds the game (marked by
        /// EscapeFromTarkov.exe, or by a BepInEx\plugins layout). Used only when BepInEx cannot say.
        /// </summary>
        private static string DeriveRootFromPluginFolder()
        {
            try
            {
                var dir = new DirectoryInfo(Path.GetDirectoryName(typeof(KeyNamesPlugin).Assembly.Location));
                for (int i = 0; i < 8 && dir != null; i++)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "EscapeFromTarkov.exe"))) return dir.FullName;
                    if (Directory.Exists(Path.Combine(dir.FullName, "SPT_Runtime"))
                        || Directory.Exists(Path.Combine(dir.FullName, "SPT_Data")))
                        return dir.FullName;
                    dir = dir.Parent;
                }
            }
            catch { }
            return null;
        }

        private static void Warn(string message)
        {
            try
            {
                var log = Log;
                if (log != null) log.LogWarning("[DMKeyNames] " + message);
            }
            catch { }
        }

        /// <summary>Drops the cached scan so the next lookup rescans (the language may have changed).</summary>
        internal static void Invalidate()
        {
            Locale.Clear();
            Overrides.Clear();
            Loaded.Clear();
            _scanned = false;
            _warned = false;
        }

        private static int ScannedFileCount { get { return Loaded.Count; } }

        /// <summary>How many locale files the scan actually read (for diagnostics).</summary>
        internal static int SourceCount { get { EnsureScanned(); return Loaded.Count; } }

        internal static int CountOf(string lang, string field)
        {
            EnsureScanned();
            Dictionary<string, string> d;
            if (!Locale.TryGetValue(lang + ":" + field, out d)) return 0;
            return d.Count;
        }

        internal static string SidecarStatus()
        {
            EnsureScanned();
            foreach (var s in Loaded)
            {
                if (!string.Equals(Path.GetFileName(s.Path), SidecarName, StringComparison.OrdinalIgnoreCase)) continue;
                return s.Failed ? "read-error" : (s.Entries + " entries");
            }
            return "<not found>";
        }

        /// <summary>Resolves one id. Returns null when nothing on disk names it either.</summary>
        internal static string Find(string id, bool english, bool useShort)
        {
            if (string.IsNullOrEmpty(id)) return null;
            try
            {
                EnsureScanned();

                string preferred = english ? "en" : "ch";
                string other = english ? "ch" : "en";

                // Mirrors the built-in tables: a missing short name falls through to the full name.
                if (useShort)
                {
                    string s = Lookup(preferred, "ShortName", id);
                    if (!string.IsNullOrEmpty(s)) return s;
                }

                string v = Lookup(preferred, "Name", id);
                if (!string.IsNullOrEmpty(v)) return v;

                if (useShort)
                {
                    string s = Lookup(other, "ShortName", id);
                    if (!string.IsNullOrEmpty(s)) return s;
                }

                return Lookup(other, "Name", id);
            }
            catch (Exception ex)
            {
                WarnOnce("external key-name lookup failed, using the built-in table only: ", ex);
                return null;
            }
        }

        private static string Lookup(string lang, string field, string id)
        {
            string slot = lang + ":" + field;
            string v;
            Dictionary<string, string> d;

            if (Overrides.TryGetValue(slot, out d) && d.TryGetValue(id, out v) && !string.IsNullOrEmpty(v)) return v;
            if (Locale.TryGetValue(slot, out d) && d.TryGetValue(id, out v) && !string.IsNullOrEmpty(v)) return v;
            return null;
        }

        private static void WarnOnce(string message, Exception ex)
        {
            if (_warned) return;
            _warned = true;
            Warn(message + ex.Message);
        }

        private static void EnsureScanned()
        {
            if (_scanned) return;
            _scanned = true;
            try
            {
                LoadOverrides();

                // Order matters: the first file to provide an entry keeps it. A content mod's own
                // locale is the authority for the ids that mod adds, and the SPT database is the
                // authority for vanilla ids - neither normally mentions the other's items, but on
                // the rare overlap the mod (loaded first, most specific) should win.
                LoadModLocales();
                LoadSptLocales();
            }
            catch (Exception ex)
            {
                WarnOnce("could not read external key names: ", ex);
            }
        }

        /// <summary>Startup summary: how many names came from where. Called once from Awake.</summary>
        internal static void LogSummary()
        {
            try
            {
                EnsureScanned();
                var log = Log;
                if (log != null)
                {
                    log.LogInfo(
                        "[DMKeyNames] external key names: names.json=" + SidecarStatus() +
                        ", sources=" + ScannedFileCount +
                        ", ch=" + CountOf("ch", "Name") + "/" + CountOf("ch", "ShortName") +
                        ", en=" + CountOf("en", "Name") + "/" + CountOf("en", "ShortName"));
                }

                if (KeyNamesPlugin.IsDebug && Loaded.Count > 0 && log != null)
                {
                    var parts = new List<string>();
                    foreach (var s in Loaded)
                    {
                        string dir = Path.GetFileName(Path.GetDirectoryName(s.Path));
                        parts.Add(dir + "/" + Path.GetFileName(s.Path) + (s.Failed ? "=<error>" : "=" + s.Entries));
                    }
                    log.LogInfo("[DMKeyNames] external files: " + string.Join(", ", parts.ToArray()));
                }
            }
            catch { }
        }

        // ------------------------------------------------------------------
        //  1+2. the hand-maintained override table
        // ------------------------------------------------------------------

        private static void LoadOverrides()
        {
            foreach (var path in SidecarPaths())
            {
                if (!File.Exists(path)) continue;
                var src = new Source { Path = path };
                try
                {
                    var parsed = ParseOverrides(File.ReadAllText(path));
                    foreach (var slotKv in parsed)
                    {
                        Dictionary<string, string> slot;
                        if (!Overrides.TryGetValue(slotKv.Key, out slot))
                        {
                            slot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            Overrides[slotKv.Key] = slot;
                        }
                        foreach (var e in slotKv.Value)
                        {
                            if (!slot.ContainsKey(e.Key)) slot[e.Key] = e.Value;
                            src.Entries++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    src.Failed = true;
                    Warn("ignoring " + SidecarName + " (" + path + "): " + ex.Message);
                }
                Loaded.Add(src);
            }
        }

        private static IEnumerable<string> SidecarPaths()
        {
            // Next to the DLL first so the table travels with the plugin folder; BepInEx\config is
            // the fallback for people who keep configuration out of plugins\.
            string pluginDir = null, configDir = null;
            try { pluginDir = Path.GetDirectoryName(typeof(KeyNamesPlugin).Assembly.Location); } catch { }
            try { configDir = BepInEx.Paths.ConfigPath; } catch { }

            if (!string.IsNullOrEmpty(pluginDir)) yield return Path.Combine(pluginDir, SidecarName);
            if (!string.IsNullOrEmpty(configDir) &&
                !string.Equals(configDir, pluginDir, StringComparison.OrdinalIgnoreCase))
                yield return Path.Combine(configDir, SidecarName);
        }

        /// <summary>
        /// Parses the override table. Both shapes are keyed by item id:
        ///   { "5a0ea69f86f7741cd5406619": "疗养院东楼 108 办公室钥匙" }
        ///   { "5a0ea69f86f7741cd5406619": { "ch": "…", "shortCh": "…", "en": "…", "shortEn": "…" } }
        /// In the object shape "name"/"ch" and "shortName"/"shortCh" are accepted as synonyms.
        /// </summary>
        private static Dictionary<string, Dictionary<string, string>> ParseOverrides(string text)
        {
            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var root = TinyJson.ParseObject(text);
            if (root == null) return result;

            foreach (var kv in root)
            {
                string id = NormalizeId(kv.Key);
                if (id == null) continue;

                var flat = kv.Value as string;
                if (flat != null)
                {
                    if (flat.Length > 0) AddOverride(result, "ch:Name", id, flat);
                    continue;
                }

                var obj = kv.Value as Dictionary<string, object>;
                if (obj == null) continue;
                foreach (var f in obj)
                {
                    string slot = FieldSlot(f.Key);
                    var value = f.Value as string;
                    if (slot == null || string.IsNullOrEmpty(value)) continue;
                    AddOverride(result, slot, id, value);
                }
            }
            return result;
        }

        private static void AddOverride(Dictionary<string, Dictionary<string, string>> into,
                                        string slot, string id, string value)
        {
            Dictionary<string, string> d;
            if (!into.TryGetValue(slot, out d))
            {
                d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                into[slot] = d;
            }
            d[id] = value;
        }

        private static string FieldSlot(string name)
        {
            switch ((name ?? "").Trim().ToLowerInvariant())
            {
                case "ch":
                case "zh":
                case "name":
                case "fullname":
                    return "ch:Name";
                case "shortch":
                case "shortname":
                case "short":
                    return "ch:ShortName";
                case "en":
                case "english":
                    return "en:Name";
                case "shorten":
                case "shortenglish":
                    return "en:ShortName";
                default:
                    return null;
            }
        }

        /// <summary>
        /// Accepts an item id only if it is exactly 24 hexadecimal characters. A hand-edited override
        /// table is the only source that can get this wrong, and a malformed key would otherwise be
        /// stored and never match anything.
        /// </summary>
        private static string NormalizeId(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            id = id.Trim();
            if (id.Length != 24) return null;

            for (int i = 0; i < id.Length; i++)
            {
                char c = id[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return null;
            }
            return id;
        }

        // ------------------------------------------------------------------
        //  3. locales shipped by content mods
        // ------------------------------------------------------------------

        private static void LoadModLocales()
        {
            string root = Root();
            if (string.IsNullOrEmpty(root)) return;

            string mods = Path.Combine(root, "SPT_Runtime", "user", "mods");
            if (!Directory.Exists(mods)) mods = Path.Combine(root, "user", "mods");
            if (!Directory.Exists(mods)) return;

            // Only folders that can hold item names: a CustomLocales/Locales folder directly under
            // the mod folder or under its db\. Walking the whole tree would also pick up quest and
            // achievement locales, which name dialogue and tasks rather than keys.
            var files = new List<string>();
            try
            {
                foreach (var modDir in Directory.GetDirectories(mods))
                {
                    foreach (var sub in new[] { "CustomLocales", @"db\CustomLocales", "Locales", @"db\Locales" })
                    {
                        string dir = Path.Combine(modDir, sub);
                        if (!Directory.Exists(dir)) continue;
                        try { files.AddRange(Directory.GetFiles(dir, "*.json")); }
                        catch { }
                    }
                }
            }
            catch { }

            // Deterministic order; the SPT database is loaded afterwards and never overrides these.
            files.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files) LoadLocaleFile(f);
        }

        // ------------------------------------------------------------------
        //  4. the game's own locale database
        // ------------------------------------------------------------------

        private static void LoadSptLocales()
        {
            string db = FindSptDatabase();
            if (string.IsNullOrEmpty(db)) return;

            string globals = Path.Combine(db, "locales", "global");
            if (!Directory.Exists(globals)) return;

            var files = new List<string>(Directory.GetFiles(globals, "*.json"));
            files.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files) LoadLocaleFile(f);
        }

        /// <summary>
        /// SPT 4.x moved the database to SPT_Runtime\SPT_Data\database; older layouts keep it at
        /// the game root. Both are probed so this keeps working across SPT versions.
        /// </summary>
        private static string FindSptDatabase()
        {
            string root = Root();
            if (string.IsNullOrEmpty(root)) return null;

            string newLayout = Path.Combine(root, "SPT_Runtime", "SPT_Data", "database");
            if (Directory.Exists(newLayout)) return newLayout;

            string oldLayout = Path.Combine(root, "SPT_Data", "database");
            if (Directory.Exists(oldLayout)) return oldLayout;

            return null;
        }

        // ------------------------------------------------------------------
        //  shared locale parsing
        // ------------------------------------------------------------------

        private static void LoadLocaleFile(string path)
        {
            string lang = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(lang)) return;

            var src = new Source { Path = path };
            try
            {
                string text = File.ReadAllText(path);
                foreach (System.Text.RegularExpressions.Match m in LocaleEntry.Matches(text))
                {
                    string id = m.Groups[1].Value;
                    string field = m.Groups[2].Value;
                    string value = Unescape(m.Groups[3].Value);
                    if (string.IsNullOrEmpty(value)) continue;

                    string slot = lang + ":" + field;
                    Dictionary<string, string> d;
                    if (!Locale.TryGetValue(slot, out d))
                    {
                        d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        Locale[slot] = d;
                    }
                    // Files are applied in priority order and never override an earlier one.
                    if (!d.ContainsKey(id)) d[id] = value;
                    src.Entries++;
                }
            }
            catch (Exception ex)
            {
                src.Failed = true;
                Warn("could not read locale " + path + ": " + ex.Message);
            }
            Loaded.Add(src);
        }

        private static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }

                char n = s[++i];
                switch (n)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case '/': sb.Append('/'); break;
                    case '\\': sb.Append('\\'); break;
                    case '"': sb.Append('"'); break;
                    case 'u':
                        if (i + 4 < s.Length)
                        {
                            int code;
                            if (int.TryParse(s.Substring(i + 1, 4),
                                    System.Globalization.NumberStyles.HexNumber,
                                    System.Globalization.CultureInfo.InvariantCulture, out code))
                            {
                                sb.Append((char)code);
                                i += 4;
                                break;
                            }
                        }
                        sb.Append(n);
                        break;
                    default: sb.Append(n); break;
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Minimal lenient JSON reader for the hand-maintained override table. Supports the JSON
    /// subset that matters here (objects, arrays, strings with escapes, numbers, true/false/null)
    /// and never throws, because the file is meant to be edited by hand and a malformed table must
    /// not cost the user their map labels.
    /// </summary>
    internal static class TinyJson
    {
        internal static Dictionary<string, object> ParseObject(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int i = 0;
            SkipTrivia(text, ref i);
            if (i >= text.Length || text[i] != '{') return null;
            return ParseObj(text, ref i);
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipTrivia(s, ref i);
            if (i >= s.Length) return null;

            switch (s[i])
            {
                case '{': return ParseObj(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return ParseString(s, ref i);
                default: return ParseLiteral(s, ref i);
            }
        }

        private static Dictionary<string, object> ParseObj(string s, ref int i)
        {
            var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            i++; // {
            while (true)
            {
                SkipTrivia(s, ref i);
                if (i >= s.Length) return d;
                if (s[i] == '}') { i++; return d; }
                if (s[i] == ',') { i++; continue; }
                if (s[i] != '"') { i++; continue; }

                string key = ParseString(s, ref i);
                SkipTrivia(s, ref i);
                if (i < s.Length && s[i] == ':') i++;
                object value = ParseValue(s, ref i);
                if (key != null) d[key] = value;
            }
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            var list = new List<object>();
            i++; // [
            while (true)
            {
                SkipTrivia(s, ref i);
                if (i >= s.Length) return list;
                if (s[i] == ']') { i++; return list; }
                if (s[i] == ',') { i++; continue; }
                list.Add(ParseValue(s, ref i));
            }
        }

        private static string ParseString(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') return null;
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '"') { i++; break; }
                if (c == '\\' && i + 1 < s.Length)
                {
                    i++;
                    char n = s[i];
                    switch (n)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 4 < s.Length)
                            {
                                int code;
                                if (int.TryParse(s.Substring(i + 1, 4),
                                        System.Globalization.NumberStyles.HexNumber,
                                        System.Globalization.CultureInfo.InvariantCulture, out code))
                                {
                                    sb.Append((char)code);
                                    i += 4;
                                }
                            }
                            break;
                        default: sb.Append(n); break;
                    }
                    i++;
                    continue;
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        private static object ParseLiteral(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && s[i] != ',' && s[i] != '}' && s[i] != ']') i++;
            string raw = s.Substring(start, i - start).Trim();
            if (raw.Length == 0) return null;
            if (raw == "true") return true;
            if (raw == "false") return false;
            if (raw == "null") return null;

            double d;
            if (double.TryParse(raw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out d)) return d;
            return raw;
        }

        private static void SkipTrivia(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { i++; continue; }
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
                {
                    while (i < s.Length && s[i] != '\n') i++;
                    continue;
                }
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/')) i++;
                    i += 2;
                    continue;
                }
                break;
            }
        }
    }
}
