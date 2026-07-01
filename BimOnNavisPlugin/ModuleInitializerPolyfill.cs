// net48 에서 C# 9 [ModuleInitializer] 를 사용하기 위한 폴리필
// ModuleInitializerAttribute 는 .NET 5+ 에만 기본 포함되므로 직접 선언합니다.
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    internal sealed class ModuleInitializerAttribute : Attribute { }
}
