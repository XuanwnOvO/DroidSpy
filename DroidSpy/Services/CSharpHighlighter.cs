using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DroidSpy.Services;

public enum TokenKind
{
    Default,
    Keyword,
    Type,
    String,
    Comment,
    Number,
    Punctuation,
    Preprocessor,
}

public readonly record struct CodeToken(int Start, int Length, TokenKind Kind);

/// <summary>
/// 轻量 C# 词法高亮器。逐字符扫描，无正则回溯，能处理大文件。
/// 只做着色用途，不追求完整语法分析。
/// </summary>
public static class CSharpHighlighter
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract","as","base","bool","break","byte","case","catch","char","checked",
        "class","const","continue","decimal","default","delegate","do","double","else",
        "enum","event","explicit","extern","false","finally","fixed","float","for",
        "foreach","goto","if","implicit","in","int","interface","internal","is","lock",
        "long","namespace","new","null","object","operator","out","override","params",
        "private","protected","public","readonly","ref","return","sbyte","sealed","short",
        "sizeof","stackalloc","static","string","struct","switch","this","throw","true",
        "try","typeof","uint","ulong","unchecked","unsafe","ushort","using","virtual",
        "void","volatile","while",
        // 上下文关键字
        "add","and","async","await","by","descending","dynamic","equals","file","from",
        "get","global","group","init","into","join","let","managed","nameof","nint",
        "not","notnull","nuint","on","or","orderby","partial","record","remove",
        "required","select","set","unmanaged","value","var","when","where","with","yield",
    };

    /// <summary>扫描源码，返回可按区间着色的 token 列表。</summary>
    public static List<CodeToken> Tokenize(string code)
    {
        var tokens = new List<CodeToken>(Math.Max(16, code.Length / 8));
        int i = 0;
        int n = code.Length;

        while (i < n)
        {
            char c = code[i];

            // 行注释
            if (c == '/' && i + 1 < n && code[i + 1] == '/')
            {
                int start = i;
                while (i < n && code[i] != '\n') i++;
                tokens.Add(new CodeToken(start, i - start, TokenKind.Comment));
                continue;
            }

            // 块注释
            if (c == '/' && i + 1 < n && code[i + 1] == '*')
            {
                int start = i;
                i += 2;
                while (i + 1 < n && !(code[i] == '*' && code[i + 1] == '/')) i++;
                i = Math.Min(n, i + 2);
                tokens.Add(new CodeToken(start, i - start, TokenKind.Comment));
                continue;
            }

            // 预处理指令（#if 等），整行着色
            if (c == '#' && IsLineStart(code, i))
            {
                int start = i;
                while (i < n && code[i] != '\n') i++;
                tokens.Add(new CodeToken(start, i - start, TokenKind.Preprocessor));
                continue;
            }

            // 字符串 / 字符字面量（含 @"、" 与 $""）
            if (c == '"' || c == '\'')
            {
                int start = i;
                bool verbatim = i > 0 && code[i - 1] == '@';
                i++;
                while (i < n)
                {
                    if (!verbatim && code[i] == '\\') { i += 2; continue; }
                    if (code[i] == c)
                    {
                        if (verbatim && i + 1 < n && code[i + 1] == c) { i += 2; continue; }
                        i++;
                        break;
                    }
                    if (code[i] == '\n') break; // 未闭合，遇行尾截断
                    i++;
                }
                tokens.Add(new CodeToken(start, Math.Min(i, n) - start, TokenKind.String));
                continue;
            }

            // 标识符 / 关键字
            if (char.IsLetter(c) || c == '_' || c == '@')
            {
                int start = i;
                if (c == '@') i++;
                while (i < n && (char.IsLetterOrDigit(code[i]) || code[i] == '_')) i++;
                var text = code.Substring(start, i - start);
                var bare = text.StartsWith('@') ? text[1..] : text;

                var kind = Keywords.Contains(bare)
                    ? TokenKind.Keyword
                    : LooksLikeTypeName(bare) ? TokenKind.Type : TokenKind.Default;

                tokens.Add(new CodeToken(start, i - start, kind));
                continue;
            }

            // 数字
            if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(code[i + 1])))
            {
                int start = i;
                while (i < n && (char.IsLetterOrDigit(code[i]) || code[i] == '.' || code[i] == '_')) i++;
                tokens.Add(new CodeToken(start, i - start, TokenKind.Number));
                continue;
            }

            // 标点
            if (IsPunctuation(c))
            {
                int start = i;
                i++;
                tokens.Add(new CodeToken(start, i - start, TokenKind.Punctuation));
                continue;
            }

            i++;
        }

        return tokens;
    }

    private static bool IsLineStart(string code, int index)
    {
        for (int k = index - 1; k >= 0; k--)
        {
            char ch = code[k];
            if (ch == '\n') return true;
            if (!char.IsWhiteSpace(ch)) return false;
        }
        return true;
    }

    /// <summary>启发式判断：首字母大写且非全大写常量，视为类型名。</summary>
    private static bool LooksLikeTypeName(string name)
    {
        if (name.Length == 0) return false;
        if (name == "I" && name.Length == 1) return false;
        char first = name[0];
        if (!char.IsUpper(first)) return false;

        bool hasLower = false;
        foreach (var ch in name)
        {
            if (char.IsLower(ch)) { hasLower = true; break; }
        }
        return hasLower || name.Length <= 3;
    }

    private static bool IsPunctuation(char c) => c switch
    {
        '{' or '}' or '(' or ')' or '[' or ']' or ';' or ',' or '.' or ':' => true,
        '+' or '-' or '*' or '/' or '%' or '=' or '<' or '>' or '!' or '&' => true,
        '|' or '^' or '~' or '?' or '@' or '$' => true,
        _ => false,
    };

    /// <summary>统计源码行数（用于显示）。</summary>
    public static int CountLines(string code)
    {
        if (string.IsNullOrEmpty(code)) return 0;
        int count = 1;
        foreach (var ch in code)
            if (ch == '\n') count++;
        return count;
    }
}
