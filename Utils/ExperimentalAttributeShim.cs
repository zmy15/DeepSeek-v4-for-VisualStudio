// net472 兼容垫片：为 VSEXT 源生成器生成的代码提供 ExperimentalAttribute。
// 该属性在 .NET 8+ BCL 中内置，net472 需手动声明。
namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Module | AttributeTargets.Class |
                    AttributeTargets.Struct | AttributeTargets.Enum | AttributeTargets.Constructor |
                    AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Field |
                    AttributeTargets.Event | AttributeTargets.Interface | AttributeTargets.Delegate,
                    AllowMultiple = false, Inherited = false)]
    internal sealed class ExperimentalAttribute : Attribute
    {
        public ExperimentalAttribute(string diagnosticId) { DiagnosticId = diagnosticId; }
        public string DiagnosticId { get; }
        public string? UrlFormat { get; set; }
    }
}
