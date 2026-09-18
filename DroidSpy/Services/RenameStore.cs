using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace DroidSpy.Services;

/// <summary>一条别名映射。</summary>
public sealed record RenameEntry(string Original, string Alias);

/// <summary>
/// 显示层别名表：把不可读的混淆名字（AAAFBKBHAKM）在**所有工具输出里**换成
/// 分析出来的可读名字（NetworkManager）。
///
/// 只改显示，不动元数据 —— 我们没有写回能力，也不需要写回：
/// token 还是原来那个，AI 拿换过名字的输出去调下一个工具时，
/// 参数会在 ReadTypeName 里被反解回原始名字，所以链路是通的。
///
/// 之所以值得做：AI 的记忆里留下的是「NetworkManager」这种有语义的名字，
/// 而不是一串随机字符，跨会话分析同一个人混淆过的 DLL 时收益尤其明显。
/// </summary>
public static class RenameStore
{
    private static readonly object Gate = new();

    /// <summary>替换时长的名字优先，避免短的先命中把长的切碎。</summary>
    private static List<KeyValuePair<string, string>>? _cache;

    private static string FilePath =>
        Path.Combine(AppContext.BaseDirectory, "droidspy-renames.json");

    public static IReadOnlyList<RenameEntry> List()
    {
        lock (Gate)
        {
            return Read()
                .Select(pair => new RenameEntry(pair.Key, pair.Value))
                .OrderBy(e => e.Original, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>登记一条别名；原名已有别名时覆盖。</summary>
    public static void Set(string original, string alias)
    {
        lock (Gate)
        {
            var map = Read();
            map[original] = alias;
            Write(map);
            _cache = null;
        }
    }

    public static bool Remove(string original)
    {
        lock (Gate)
        {
            var map = Read();
            if (!map.Remove(original)) return false;

            Write(map);
            _cache = null;
            return true;
        }
    }

    /// <summary>把输出里的原始名字换成别名。</summary>
    public static string Apply(string text)
    {
        var pairs = Cache();
        if (pairs.Count == 0) return text;

        foreach (var pair in pairs)
            text = text.Replace(pair.Key, pair.Value, StringComparison.Ordinal);

        return text;
    }

    /// <summary>
    /// 反解：调用方传进来的可能是别名，要换回程序集里的真名才能查到东西。
    /// 如果这个名字本身就是个真实存在的名字，优先当真名用 ——
    /// 否则别名叫「Button」而程序集里真有个 Button 时就会解析错。
    /// </summary>
    public static string Resolve(string name, Func<string, bool> exists)
    {
        if (exists(name)) return name;

        var pairs = Cache();
        foreach (var pair in pairs)
            if (string.Equals(pair.Value, name, StringComparison.Ordinal))
                return pair.Key;

        return name;
    }

    private static List<KeyValuePair<string, string>> Cache()
    {
        lock (Gate)
        {
            if (_cache != null) return _cache;

            // 长的原名排前面：先换掉 AAAFBKBHAKM2 再换 AAAFBKBHAKM，
            // 否则后者会先把前者的前缀吃掉，剩个孤零零的 2
            _cache = Read()
                .OrderByDescending(pair => pair.Key.Length)
                .ToList();

            return _cache;
        }
    }

    // ------------------------------------------------------------ 存取

    private static Dictionary<string, string> Read()
    {
        try
        {
            if (!File.Exists(FilePath)) return new Dictionary<string, string>(StringComparer.Ordinal);

            var json = File.ReadAllText(FilePath, Encoding.UTF8);
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return map == null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(map, StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static void Write(Dictionary<string, string> map)
    {
        var json = JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(FilePath, json, new UTF8Encoding(false));
    }
}
