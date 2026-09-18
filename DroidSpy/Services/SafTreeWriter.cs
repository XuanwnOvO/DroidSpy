using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Android.Content;
using Android.Provider;

namespace DroidSpy.Services;

/// <summary>
/// 往用户选中的 SAF 目录（content://…/tree/…）里按相对路径写文件。
///
/// 逐级找目录、找不到就建，建过的记在缓存里 —— 一整个程序集动辄几百个类型，
/// 每次都从根重新走一遍 DocumentsProvider 会慢得离谱。
/// 同一个实例只服务一次导出任务，缓存的生命周期就是这次任务。
/// </summary>
public sealed class SafTreeWriter
{
    /// <summary>
    /// 创建 .cs 文件时用的 MIME 类型。
    ///
    /// 不能用 "text/plain"：Android 内置的 Downloads / DocumentsUI 这类
    /// DocumentsProvider 会拿 MIME 去查它自己的扩展名表，text/plain 对应的
    /// 是 .txt，于是最终落盘的名字就变成 AAAFBKBHAKM.cs.txt —— 显示名我们
    /// 给的是完整的 "xxx.cs"，扩展名却被它按 MIME 又追加了一遍。
    ///
    /// 换成 .cs 这个确切类型，它查不到对应扩展名，就老老实实用我们给的名字。
    /// 万一某个 Provider 更挑剔、认不出这个类型，CreateDocument 会抛
    /// IllegalArgumentException，届时退回 text/plain 至少还能写成功
    /// （代价就是多一个 .txt 后缀，总比整个导出失败强）。
    /// </summary>
    private const string CSharpMime = "text/x-csharp";

    private readonly ContentResolver _resolver;
    private readonly Android.Net.Uri _rootDoc;

    /// <summary>相对目录 → 已经拿到的文档 URI。</summary>
    private readonly Dictionary<string, Android.Net.Uri> _dirs = new(StringComparer.Ordinal);

    public SafTreeWriter(Context ctx, Android.Net.Uri treeUri)
    {
        _resolver = ctx.ContentResolver!;

        var rootId = DocumentsContract.GetTreeDocumentId(treeUri);
        _rootDoc = DocumentsContract.BuildDocumentUriUsingTree(treeUri, rootId)
                   ?? throw new IOException("无法访问所选目录");
        _dirs[string.Empty] = _rootDoc;
    }

    /// <summary>把相对路径里的目录逐级建出来（父目录先建），返回该目录的文档 URI。</summary>
    private Android.Net.Uri EnsureDir(string relDir)
    {
        if (relDir.Length == 0) return _rootDoc;
        if (_dirs.TryGetValue(relDir, out var cached)) return cached;

        var parent = EnsureDir(Path.GetDirectoryName(relDir) ?? string.Empty);

        var name = Path.GetFileName(relDir);
        var child = FindChild(parent, name) ?? DocumentsContract.CreateDocument(
            _resolver, parent, DocumentsContract.Document.MimeTypeDir, name)
            ?? throw new IOException($"无法创建目录 {relDir}");

        _dirs[relDir] = child;
        return child;
    }

    /// <summary>在 SAF 目录里按显示名找一个已存在的子项，找不到返回 null。</summary>
    private Android.Net.Uri? FindChild(Android.Net.Uri parent, string displayName)
    {
        var children = DocumentsContract.BuildChildDocumentsUriUsingTree(
            parent, DocumentsContract.GetDocumentId(parent));
        if (children == null) return null;

        string[] columns =
        {
            DocumentsContract.Document.ColumnDocumentId,
            DocumentsContract.Document.ColumnDisplayName,
        };

        using var cursor = _resolver.Query(children, columns, null, null, null);
        if (cursor == null) return null;

        while (cursor.MoveToNext())
        {
            if (!string.Equals(cursor.GetString(1), displayName, StringComparison.Ordinal))
                continue;

            return DocumentsContract.BuildDocumentUriUsingTree(parent, cursor.GetString(0));
        }

        return null;
    }

    private Android.Net.Uri CreateCodeDocument(Android.Net.Uri parent, string fileName)
    {
        try
        {
            return DocumentsContract.CreateDocument(_resolver, parent, CSharpMime, fileName)!;
        }
        catch (Exception ex) when (ex is Java.Lang.IllegalArgumentException
                                      or Java.Lang.UnsupportedOperationException)
        {
            return DocumentsContract.CreateDocument(_resolver, parent, "text/plain", fileName)
                   ?? throw new IOException($"无法创建 {fileName}");
        }
    }

    /// <summary>把一个 .cs 写进 SAF 目录，返回写出的文件在树里的相对路径。</summary>
    public string WriteSource(string relPath, string code)
    {
        var relDir = Path.GetDirectoryName(relPath) ?? string.Empty;
        var fileName = Path.GetFileName(relPath);

        var parent = EnsureDir(relDir);

        // 同名的旧文件先删掉：直接 CreateDocument 会留下 xxx (1).cs 这种重复
        var stale = FindChild(parent, fileName);
        if (stale != null) DocumentsContract.DeleteDocument(_resolver, stale);

        var doc = CreateCodeDocument(parent, fileName);

        var bytes = Encoding.UTF8.GetBytes(code);
        using var output = _resolver.OpenOutputStream(doc)
                           ?? throw new IOException($"无法写入 {relPath}");
        output.Write(bytes, 0, bytes.Length);
        output.Flush();

        return relPath;
    }

    /// <summary>
    /// 在目标目录下找（或建）一个子目录，用来放某个 DLL 的全部源码。
    /// 目录名重名时按 Provider 的规则自动让路。
    /// </summary>
    public Android.Net.Uri EnsureChildDir(string name)
    {
        var parent = _rootDoc;
        var existing = FindChild(parent, name);
        if (existing != null) return existing;

        return DocumentsContract.CreateDocument(
            _resolver, parent, DocumentsContract.Document.MimeTypeDir, name)
            ?? throw new IOException($"无法创建目录 {name}");
    }
}
