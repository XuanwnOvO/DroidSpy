using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DroidSpy.Services;

/// <summary>一条笔记。</summary>
public sealed record NoteEntry(string Key, string Text, string UpdatedAt);

/// <summary>
/// AI 笔记：让电脑端的模型把「这个混淆名字是什么意思」这类结论存下来，
/// 下次连上来还能用，不用每次重新推理一遍。
///
/// 存在应用私有目录下的一个 JSON 文件里，格式就是 key → { text, updatedAt }，
/// 用 key 覆盖式写入，同一个结论改几次也只留最新的一条。
/// </summary>
public static class NoteStore
{
    private static readonly object Gate = new();

    private static string FilePath =>
        Path.Combine(AppContext.BaseDirectory, "droidspy-notes.json");

    public static IReadOnlyList<NoteEntry> List()
    {
        lock (Gate)
        {
            var map = Read();

            return map
                .Select(pair => new NoteEntry(
                    Key: pair.Key,
                    Text: pair.Value.Text,
                    UpdatedAt: pair.Value.UpdatedAt))
                .OrderByDescending(n => n.UpdatedAt, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>写一条笔记；key 相同则覆盖。返回是否是覆盖写入。</summary>
    public static bool Save(string key, string text)
    {
        lock (Gate)
        {
            var map = Read();
            bool replaced = map.ContainsKey(key);

            map[key] = new StoredNote
            {
                Text = text,
                UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            };

            Write(map);
            return replaced;
        }
    }

    /// <summary>删一条笔记，返回是否真的删掉了。</summary>
    public static bool Delete(string key)
    {
        lock (Gate)
        {
            var map = Read();
            if (!map.Remove(key)) return false;

            Write(map);
            return true;
        }
    }

    // ------------------------------------------------------------ 存取

    private sealed class StoredNote
    {
        public string Text { get; set; } = string.Empty;
        public string UpdatedAt { get; set; } = string.Empty;
    }

    private static Dictionary<string, StoredNote> Read()
    {
        try
        {
            if (!File.Exists(FilePath)) return new Dictionary<string, StoredNote>(StringComparer.Ordinal);

            var json = File.ReadAllText(FilePath, Encoding.UTF8);
            var map = JsonSerializer.Deserialize<Dictionary<string, StoredNote>>(json);
            return map == null
                ? new Dictionary<string, StoredNote>(StringComparer.Ordinal)
                : new Dictionary<string, StoredNote>(map, StringComparer.Ordinal);
        }
        catch
        {
            // 文件坏了就当没有，总比让整个工具调用失败强
            return new Dictionary<string, StoredNote>(StringComparer.Ordinal);
        }
    }

    private static void Write(Dictionary<string, StoredNote> map)
    {
        var json = JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(FilePath, json, new UTF8Encoding(false));
    }
}
