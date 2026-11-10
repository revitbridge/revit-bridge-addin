using System.IO;
using System.Reflection;

namespace RevitMCPCommandSet.Commands.ExecuteDynamicCode
{
    /// <summary>
    /// <para>.NET Framework（Revit 2020-2024）按精确版本绑定强名称程序集，且只读取 Revit.exe.config 里的
    /// 绑定重定向。Roslyn 的依赖（System.Memory 等）比某些 Revit 年份自带的副本新：Revit 2021 带
    /// System.Runtime.CompilerServices.Unsafe 4.0.4.0，而 System.Memory 4.0.1.2 要 4.0.4.1，该引用绑定失败，
    /// 第一次编译就抛 TypeInitializationException（PerTypeValues`1）。这里代替缺失的绑定重定向：本文件夹里的
    /// 程序集发出的、或没有请求方的 Roslyn 依赖（固定名单）的强名称引用绑定失败时，返回本文件夹里同名、
    /// 同公钥、版本不低于所需版本的那个副本。.NET 8 / .NET 10（Revit 2025 及以后）不需要，什么也不做。</para>
    /// <para>.NET Framework (Revit 2020-2024) binds strong-named references by exact version and
    /// reads binding redirects only from Revit.exe.config. Roslyn's dependencies (System.Memory and
    /// friends) are newer than the copies some Revit years ship: Revit 2021 has
    /// System.Runtime.CompilerServices.Unsafe 4.0.4.0 where System.Memory 4.0.1.2 asks for 4.0.4.1,
    /// so that reference fails to bind and the first compile throws TypeInitializationException
    /// (PerTypeValues`1). This stands in for the missing binding redirect: when a strong-named
    /// reference made by an assembly in this folder, or one of Roslyn's dependencies (a fixed list)
    /// requested without a named requester, cannot be bound, the copy in this folder with the same
    /// name and public key and the same or a newer version is returned. Nothing happens on .NET 8 / .NET 10
    /// (Revit 2025 and later), where a newer version binds anyway.</para>
    /// <para>AssemblyResolve is raised only after normal binding has failed, so binds that work
    /// (another add-in's, or Revit 2023/2024, whose own copies match) are not affected.</para>
    /// </summary>
    internal static class DependencyResolver
    {
#if NETFRAMEWORK
        private static readonly object Gate = new object();
        private static bool _registered;
        private static string _folder;

        // The System.* part of Roslyn's dependency closure that this folder ships. Derived from
        // the net48 build output (Commands\RevitMCPCommandSet\<year>\, the same for 2020, 2021,
        // 2023 and 2024): the assembly references of Microsoft.CodeAnalysis.dll and
        // Microsoft.CodeAnalysis.CSharp.dll, followed transitively, kept when the DLL is in the
        // folder. A failed bind without a named requester is answered only for these names, so
        // another add-in's (or Revit's) failed load of some other assembly is never given ours.
        private static readonly HashSet<string> RoslynDependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System.Buffers",
            "System.Collections.Immutable",
            "System.Memory",
            "System.Numerics.Vectors",
            "System.Reflection.Metadata",
            "System.Runtime.CompilerServices.Unsafe",
            "System.Text.Encoding.CodePages",
            "System.Threading.Tasks.Extensions",
        };
#endif

        /// <summary>Registers the handler once; call before the first compile.</summary>
        public static void EnsureRegistered()
        {
#if NETFRAMEWORK
            lock (Gate)
            {
                if (_registered)
                    return;
                _folder = Path.GetDirectoryName(typeof(DependencyResolver).Assembly.Location);
                AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
                _registered = true;
            }
#endif
        }

#if NETFRAMEWORK
        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            try
            {
                var wanted = new AssemblyName(args.Name);

                // References made by this command set's own assemblies, or with no requester named
                // for one of Roslyn's dependencies: the failing bind seen on Revit 2021 comes from the
                // type initializer of a generic type (System.Memory's SpanHelpers.PerTypeValues<T>)
                // and has RequestingAssembly null.
                Assembly requesting = args.RequestingAssembly;
                if (requesting == null)
                {
                    if (!RoslynDependencies.Contains(wanted.Name ?? string.Empty))
                        return null;
                }
                else if (requesting.IsDynamic || string.IsNullOrEmpty(requesting.Location) ||
                         !string.Equals(Path.GetDirectoryName(requesting.Location), _folder, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                // Only strong-named requests: a bind without a public key token is not a version
                // mismatch this handler stands in for (.NET Framework ignores versions there).
                byte[] wantedToken = wanted.GetPublicKeyToken();
                if (wantedToken == null || wantedToken.Length == 0)
                    return null;

                string candidate = Path.Combine(_folder, wanted.Name + ".dll");
                if (!File.Exists(candidate))
                    return null;

                AssemblyName found = AssemblyName.GetAssemblyName(candidate);
                if (!SameBytes(wantedToken, found.GetPublicKeyToken()))
                    return null;
                if (wanted.Version != null && (found.Version == null || found.Version < wanted.Version))
                    return null;

                return Assembly.LoadFrom(candidate);
            }
            catch (Exception ex)
            {
                // 解析不了就按原样失败（与没有本处理器时相同），记下原因。
                // Unresolved: the bind fails as it would without this handler; say why.
                System.Diagnostics.Trace.WriteLine($"revit-bridge DependencyResolver could not resolve {args.Name}: {ex.Message}");
                return null;
            }
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                    return false;
            }
            return true;
        }
#endif
    }
}
