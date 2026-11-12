using System.Text;
using Newtonsoft.Json.Linq;

namespace RevitMCPCommandSet.Commands.ExecuteDynamicCode
{
    /// <summary>
    /// <para>与片段一起编译的一个完整 C# 文件（不套外壳，自带 using、命名空间与类型）。</para>
    /// <para>One complete C# file compiled together with the snippet (not wrapped: it has its own
    /// usings, namespaces and types).</para>
    /// </summary>
    public sealed class CodeSource
    {
        public CodeSource(string name, string text)
        {
            Name = name;
            Text = text ?? string.Empty;
        }

        /// <summary>The file name as the caller gave it; diagnostics in this file carry it as "file".</summary>
        public string Name { get; }

        /// <summary>The complete file text.</summary>
        public string Text { get; }
    }

    /// <summary>
    /// <para>读取请求里可选的 "sources"：[{ "name": "Core.cs", "text": "..." }, ...]，并在编译之前检查上限。
    /// 不合规时抛 ArgumentException（与缺少 code 时相同，调用方收到 JSON-RPC 错误）。</para>
    /// <para>Reads the optional "sources" of a request, [{ "name": "Core.cs", "text": "..." }, ...],
    /// and checks the limits before anything is compiled. A request that breaks them gets an
    /// ArgumentException (as for a missing code, the caller receives a JSON-RPC error).</para>
    /// </summary>
    public static class CodeSources
    {
        /// <summary>At most this many files.</summary>
        public const int MaxFiles = 20;

        /// <summary>At most this many bytes of text (UTF-8) over all files together; the snippet is not counted.</summary>
        public const int MaxTotalBytes = 400 * 1024;

        /// <summary>At most this many characters in a file name.</summary>
        public const int MaxNameLength = 200;

        private static readonly IReadOnlyList<CodeSource> None = new CodeSource[0];

        /// <summary>
        /// <para>没有 "sources"、为 null 或为空数组时返回空列表；否则按给定顺序返回各文件。</para>
        /// <para>An empty list when "sources" is missing, null or an empty array; otherwise the files
        /// in the order given.</para>
        /// </summary>
        public static IReadOnlyList<CodeSource> FromRequest(JObject parameters)
        {
            JToken token = parameters?["sources"];
            if (token == null || token.Type == JTokenType.Null)
                return None;

            JArray array = token as JArray;
            if (array == null)
                throw new ArgumentException("'sources' must be an array of {\"name\": ..., \"text\": ...} objects.");
            if (array.Count == 0)
                return None;
            if (array.Count > MaxFiles)
                throw new ArgumentException($"'sources' has {array.Count} files; at most {MaxFiles} are accepted.");

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sources = new List<CodeSource>(array.Count);
            long totalBytes = 0;
            for (int i = 0; i < array.Count; i++)
            {
                JObject entry = array[i] as JObject;
                if (entry == null)
                    throw new ArgumentException($"sources[{i}] must be an object with \"name\" and \"text\".");

                JToken nameToken = entry["name"];
                JToken textToken = entry["text"];
                if (nameToken == null || nameToken.Type != JTokenType.String)
                    throw new ArgumentException($"sources[{i}].name must be a string.");
                if (textToken == null || textToken.Type != JTokenType.String)
                    throw new ArgumentException($"sources[{i}].text must be a string.");

                string name = nameToken.Value<string>() ?? string.Empty;
                string text = textToken.Value<string>() ?? string.Empty;
                if (name.Trim().Length == 0)
                    throw new ArgumentException($"sources[{i}].name is empty.");
                if (name.Length > MaxNameLength)
                    throw new ArgumentException($"sources[{i}].name is longer than {MaxNameLength} characters.");
                if (name.Any(char.IsControl))
                    throw new ArgumentException($"sources[{i}].name contains a control character.");
                if (!names.Add(name))
                    throw new ArgumentException($"sources[{i}].name '{name}' is given more than once (names are compared ignoring case).");

                totalBytes += Encoding.UTF8.GetByteCount(text);
                if (totalBytes > MaxTotalBytes)
                    throw new ArgumentException($"'sources' hold more than {MaxTotalBytes / 1024} KB of text (UTF-8) together, at sources[{i}] '{name}'; at most {MaxTotalBytes / 1024} KB are accepted.");

                sources.Add(new CodeSource(name, text));
            }
            return sources;
        }
    }
}
