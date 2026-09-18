<div align="center">

# DroidSpy

**在手机上读懂 .NET —— 安卓端的 dnSpy**

打开一个 `Assembly-CSharp.dll`，直接在手机上看反编译出来的 C# 源码。

[![Platform](https://img.shields.io/badge/Platform-Android-3DDC84?logo=android&logoColor=white)](#)
[![.NET](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet&logoColor=white)](#)
[![UI](https://img.shields.io/badge/UI-Material%203-6750A4?logo=materialdesign&logoColor=white)](#)
[![License](https://img.shields.io/badge/License-MIT-blue)](#许可证)

</div>

---

## 这是什么

DroidSpy 是一个**纯安卓端**的 .NET 反编译工具，定位是 **dnSpy 的手机替代品**。

它解决的是一个很具体的痛点：想看看某个 Unity 手游的逻辑，得先找到电脑，把 `Assembly-CSharp.dll` 拷过去，打开 dnSpy，翻半天；看完想改点东西，还得再拷回去。

DroidSpy 把这一整套搬到手机上 —— **从解包到看源码，全程不碰电脑**。

主要目标场景是 **Unity 的 Mono 后端**：游戏安装目录里的 `Assembly-CSharp.dll` 直接扔进来就能读。

<div align="center">

| 能做的 | 不做的 |
| --- | --- |
| 反编译 .NET 程序集，看 C# 源码 | ❌ 调试器 / 断点 / 单步 |
| 解包 UnityFS，从资源包里直接取出 DLL | ❌ IL2CPP（`libil2cpp.so` 那种） |
| 搜索类型、成员、字符串、源码 | ❌ 改完直接回写进 DLL |
| 把整包源码导出成 `.cs` 文件 | ❌ 运行目标程序 |
| 起一个 MCP 服务，让电脑上的 AI 直接读源码 | |

</div>

---

## 功能

### 反编译与阅读

- 打开 `.dll` 后自动扫描元数据，列出全部类型，可按**命名空间、类型种类（类 / 接口 / 结构 / 枚举 / 委托）、是否公开、是否编译器生成**过滤
- 点进类型看反编译后的完整 C# 源码，带**行号**和**语法高亮**
- 类型成员一览，逐个成员单独反编译，也可以直接看 **IL 指令**
- 长按任意位置**自动换行**，横屏竖屏都跟手，设置会记住
- 字号可调，深浅色跟随系统的 Material You 动态取色

### 搜索

- **搜类型** —— 按名字模糊匹配
- **搜成员** —— 方法 / 字段 / 属性，按名字找
- **搜字符串** —— 扫全程序集的字符串字面量，找"提示文本""URL""密钥"这类东西特别快
- **搜源码** —— 在**反编译后的源码文本**里搜，比搜元数据更接近你眼睛看到的东西

### 引用分析

- **找引用** —— 一个类型/成员被谁用了，直接给出源码位置和行号
- **查用法** —— 反向：这个方法里引用了哪些东西
- **类型继承树** —— 往上追父类、往下看子类
- **程序集引用** —— 这个 DLL 依赖了哪些别的程序集

后台会在打开文件后**建一份索引**，索引用线性表存，不用反查字典 —— 几百万条引用在手机上也不会把内存吃光。

### Unity 资源包

- 直接解包资源包，自动从里面**找出所有 .NET DLL**
- 认三种容器：**UnityFS**（Unity 5.3 以后）、**UnityWeb** / **UnityRaw**（更老的格式，第 6 版起内部结构与 UnityFS 一致）
- 认四种压缩：**不压缩 / LZ4 / LZ4HC / LZMA**

### 导出

- 把整个程序集的源码**流式导出**成一堆 `.cs` 文件，导出时带上命名空间目录结构
- 走 Android 的 SAF 选择保存位置，不申请存储权限

### 笔记与重命名

- 给任意类型/成员**写笔记**，下次打开同一个文件还在
- 给混淆过的名字**起别名**，所有输出（反编译结果、搜索结果、引用列表）都会自动换成你起的名字 —— 对付 `AAAFBKBHAKM` 这种名字很有用

### MCP：让 AI 直接读源码

这是 DroidSpy 比较特别的地方。应用内置一个 **MCP（Model Context Protocol）服务**，手机和电脑在同一个局域网里时，电脑上的 AI 客户端可以直接连上来读反编译结果。

- 手机上点一下启动，显示局域网地址（默认端口 `1233`）
- 传输走 **Streamable HTTP**，端点是 `/mcp`
- 提供 **20 个工具**：`assembly_info`、`list_types`、`search_members`、`decompile_type`、`type_members`、`decompile_member`、`get_il`、`search_source`、`find_references`、`find_usages`、`search_strings`、`type_hierarchy`、`assembly_references`、`current_type`、`save_note`、`list_notes`、`delete_note`、`set_rename`、`list_renames`、`clear_rename`

也就是说，你可以让 AI 直接分析手机上的这个 DLL，不用先把文件传过去。

---

## 支持范围

想省事的话，对照下面这张表看你的文件能不能直接用：

| 你的文件 | 支持情况 |
| --- | --- |
| `Assembly-CSharp.dll`（Unity Mono 后端） | ✅ 最推荐的用法，直接打开 |
| 自己写的 .NET 类库 / 应用 | ✅ 只要能读出元数据都能反编译 |
| `UnityFS` 资源包（`.bundle` / `.assets` / 无扩展名） | ✅ 自动解包并列出里面的 DLL |
| `UnityWeb` / `UnityRaw` 老格式资源包 | ✅ 老包也能解 |
| Unity 资源包用 LZMA 压缩（Unity 的默认选项） | ✅ 已支持 |
| Unity 资源包用 LZ4 / LZ4HC 压缩 | ✅ 已支持 |
| 加密过的资源包 | ❌ 解包会明确提示"可能已加密"，不会静默失败 |
| `libil2cpp.so` + `global-metadata.dat`（IL2CPP 后端） | ❌ 不打算支持，架构不同，做不了 |
| 原生 `.so` / 不含托管元数据的 `.exe` | ❌ 能识别出来并告诉你它是什么，但反编译不了 |

---

## 截图

> 待补充

---

## 开始使用

### 直接装

到 [Releases](https://github.com/XuanwnOvO/DroidSpy/releases) 下载 APK 安装即可。

- 最低支持 **Android 8.0（API 26）**
- 无需 root

### 怎么用

1. 打开 DroidSpy，点**选择文件**，挑一个 `.dll`
   - 也可以直接选 Unity 的 **`.assets` / `.bundle` 资源包**，DroidSpy 会自己解包并列出里面的 DLL
2. 等索引建完，进**浏览**页，用搜索或筛选找到想看的类型
3. 点进去看源码，长按可以开自动换行
4. 想给 AI 看？回主页点 **MCP 服务**启动，把地址填进电脑上的 AI 客户端

### 从源码构建

需要 **.NET 9 SDK**、**Android SDK**、**JDK 17**。

```bash
git clone https://github.com/XuanwnOvO/DroidSpy.git
cd DroidSpy
dotnet build DroidSpy/DroidSpy.csproj -c Release
```

本地 SDK 路径在 `DroidSpy/DroidSpy.csproj` 里，按需改：

```xml
<AndroidSdkDirectory>D:\Android\Sdk</AndroidSdkDirectory>
<JavaSdkDirectory>C:\JDK\jdk-17.0.12</JavaSdkDirectory>
```

---

## 技术栈

| 部分 | 用什么 |
| --- | --- |
| 语言 / 框架 | C# / .NET for Android（`net9.0-android35.0`） |
| 反编译引擎 | ICSharpCode.Decompiler（ILSpy / dnSpy 同款） |
| 界面 | Material 3 + Material You 动态取色 |
| 压缩 | K4os.Compression.LZ4、SharpCompress |
| 元数据读写 | `System.Reflection.Metadata` |

---

## 引用的开源库

DroidSpy 站在这些项目的肩膀上，感谢作者们的付出：

| 项目 | 版本 | 用途 |
| --- | --- | --- |
| [ICSharpCode.Decompiler](https://github.com/icsharpcode/ILSpy) | 9.1.0.7988 | 反编译引擎，dnSpy / ILSpy 同款 |
| [K4os.Compression.LZ4](https://github.com/MiloszKrajewski/K4os.Compression.LZ4) | 1.3.8 | 解包资源包用的 LZ4 解压 |
| [SharpCompress](https://github.com/adamhathcock/sharpcompress) | 0.39.0 | 解包资源包用的 LZMA 解压 |
| [Xamarin.Google.Android.Material](https://github.com/xamarin/GooglePlayServicesComponents) | 1.14.0.6 | Material 3 界面组件 |

---

## 交流与反馈

- **QQ 群**：`1040835997` —— [点这里加群](https://qm.qq.com/q/1040835997)
- **Issue**：[提交问题或建议](https://github.com/XuanwnOvO/DroidSpy/issues)

用着有问题、有想加的功能，都欢迎来说。

---

## 许可证

MIT

---

<div align="center">

**用爱发电，愿你反编译顺利**

</div>
