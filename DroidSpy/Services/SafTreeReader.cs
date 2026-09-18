using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Android.Content;
using Android.Provider;

namespace DroidSpy.Services;

/// <summary>SAF 树里找到的一个文件。</summary>
public sealed record TreeFile(string Name, Android.Net.Uri Uri, long Size);

/// <summary>
/// 遍历用户选中的 SAF 目录（content://…/tree/…），把里面的文件列出来。
///
/// 为什么要走 SAF 而不是直接读路径：Android 10 以后外部存储上的目录
/// 应用拿不到真实路径，只能通过 DocumentsProvider 一层层往下问。
/// </summary>
public static class SafTreeReader
{
    /// <summary>递归找文件时的目录深度上限，防止软链接之类的把遍历拖死。</summary>
    private const int MaxDepth = 12;

    /// <summary>一次遍历最多返回多少个文件。</summary>
    private const int MaxFiles = 20000;

    /// <summary>
    /// 递归列出树里所有文件。单个子目录读失败（权限、Provider 抽风）时跳过它继续走，
    /// 不让一个坏目录毁掉整次遍历。
    /// </summary>
    public static List<TreeFile> ListFiles(Context ctx, Android.Net.Uri treeUri,
        CancellationToken? ct = null)
    {
        var resolver = ctx.ContentResolver!;
        var rootId = DocumentsContract.GetTreeDocumentId(treeUri);
        var rootDoc = DocumentsContract.BuildDocumentUriUsingTree(treeUri, rootId)
                      ?? throw new IOException("无法访问所选目录");

        var results = new List<TreeFile>();
        Walk(resolver, rootDoc, 0, results, ct);
        return results;
    }

    private static void Walk(ContentResolver resolver, Android.Net.Uri dir, int depth,
        List<TreeFile> results, CancellationToken? ct)
    {
        if (depth > MaxDepth || results.Count >= MaxFiles) return;
        ct?.ThrowIfCancellationRequested();

        string[] columns =
        {
            DocumentsContract.Document.ColumnDocumentId,
            DocumentsContract.Document.ColumnDisplayName,
            DocumentsContract.Document.ColumnMimeType,
            DocumentsContract.Document.ColumnSize,
        };

        var children = DocumentsContract.BuildChildDocumentsUriUsingTree(
            dir, DocumentsContract.GetDocumentId(dir));
        if (children == null) return;

        var subdirs = new List<Android.Net.Uri>();

        using (var cursor = resolver.Query(children, columns, null, null, null))
        {
            if (cursor == null) return;

            while (cursor.MoveToNext())
            {
                if (results.Count >= MaxFiles) return;

                var name = cursor.GetString(1) ?? string.Empty;
                var mime = cursor.GetString(2);
                var size = cursor.IsNull(3) ? 0 : cursor.GetLong(3);
                var child = DocumentsContract.BuildDocumentUriUsingTree(dir, cursor.GetString(0));

                if (mime == DocumentsContract.Document.MimeTypeDir)
                {
                    subdirs.Add(child);
                    continue;
                }

                results.Add(new TreeFile(name, child, size));
            }
        }

        // 先收完当前层再往下钻：Provider 的游标和递归查询叠在一起容易出问题
        foreach (var sub in subdirs)
            Walk(resolver, sub, depth + 1, results, ct);
    }
}
