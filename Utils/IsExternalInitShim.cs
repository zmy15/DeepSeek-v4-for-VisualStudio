// net472 兼容垫片：为 { get; init; } 提供编译所需的 IsExternalInit 预定义类型。
// .NET Framework BCL 未内置该类型，SDK 工程惯例是在每个程序集内声明一次。
namespace System.Runtime.CompilerServices
{
    internal class IsExternalInit
    {
    }
}
