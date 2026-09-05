using System;

// net472 兼容垫片：为 string.Contains(string, StringComparison) 提供扩展方法。
//
// String.Contains(String, StringComparison) 重载仅在 .NET Core 2.1+ / .NET Standard 2.1+ 提供，
// .NET Framework（含 4.8.1）没有该实例方法。Microsoft 官方文档对 .NET Framework 的建议正是：
// "On .NET Framework: Create a custom method"，即提供等价扩展方法（内部以 IndexOf 实现，语义一致）。
// 参考：https://learn.microsoft.com/en-us/dotnet/api/system.string.contains
//
// 与项目既有的 net472 垫片惯例一致（参见 Utils/IsExternalInitShim.cs）：
// 放在全局命名空间，保证程序集内所有调用点无需额外 using 即可解析。

/// <summary>
/// 为 net472 提供 <c>string.Contains(string, StringComparison)</c> 的等价扩展方法。
/// 语义与 .NET Core 版本一致；value 为 null 时抛 ArgumentNullException。
/// </summary>
public static class StringContainsComparisonShim
{
    /// <summary>
    /// 返回当前字符串是否包含指定的子字符串（使用指定比较规则）。
    /// </summary>
    public static bool Contains(this string str, string value, StringComparison comparisonType)
    {
        if (value == null)
            throw new ArgumentNullException(nameof(value));
        return str.IndexOf(value, comparisonType) >= 0;
    }
}
