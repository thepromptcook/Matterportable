using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Matterportable.Core
{
    /// <summary>
    /// A field in a GraphQL selection set. Optional fields may be pruned automatically when the server reports
    /// that they do not exist, which keeps the importer working when Matterport renames or removes fields.
    /// </summary>
    public sealed class GqlField
    {
        public string Name { get; }
        public string Arguments { get; }
        public bool Optional { get; }
        public List<GqlField> Children { get; } = new List<GqlField>();
        public GqlField Parent { get; private set; }
        internal int Line;

        public GqlField(string name, bool optional = true, string arguments = null)
        {
            Name = name;
            Optional = optional;
            Arguments = arguments;
        }

        public bool IsLeaf => Children.Count == 0;
        public string Path => Parent == null ? Name : Parent.Path + "/" + Name;

        public GqlField Add(params GqlField[] children)
        {
            foreach (var c in children)
            {
                c.Parent = this;
                Children.Add(c);
            }
            return this;
        }

        /// <summary>Adds optional scalar leaves.</summary>
        public GqlField Leaves(params string[] names)
        {
            foreach (var n in names) Add(new GqlField(n));
            return this;
        }

        /// <summary>Adds required scalar leaves.</summary>
        public GqlField Required(params string[] names)
        {
            foreach (var n in names) Add(new GqlField(n, optional: false));
            return this;
        }

        public GqlField Clone()
        {
            var c = new GqlField(Name, Optional, Arguments);
            foreach (var ch in Children) c.Add(ch.Clone());
            return c;
        }

        internal void Detach() => Parent = null;
    }

    /// <summary>A single-operation GraphQL query document rendered with one field per line.</summary>
    public sealed class GqlDocument
    {
        public string OperationName { get; }
        public GqlField Root { get; }

        public GqlDocument(string operationName, GqlField root)
        {
            OperationName = operationName;
            Root = root;
        }

        public GqlDocument Clone() => new GqlDocument(OperationName, Root.Clone());

        public string Render()
        {
            var sb = new StringBuilder();
            int line = 1;
            sb.Append("query ").Append(OperationName).Append(" {\n");
            line++;
            RenderField(sb, Root, 1, ref line);
            sb.Append("}\n");
            return sb.ToString();
        }

        static void RenderField(StringBuilder sb, GqlField f, int depth, ref int line)
        {
            f.Line = line;
            sb.Append(' ', depth * 2).Append(f.Name);
            if (!string.IsNullOrEmpty(f.Arguments)) sb.Append('(').Append(f.Arguments).Append(')');
            if (f.IsLeaf)
            {
                sb.Append('\n');
                line++;
                return;
            }
            sb.Append(" {\n");
            line++;
            foreach (var c in f.Children) RenderField(sb, c, depth + 1, ref line);
            sb.Append(' ', depth * 2).Append("}\n");
            line++;
        }

        public IEnumerable<GqlField> AllFields()
        {
            var stack = new Stack<GqlField>();
            stack.Push(Root);
            while (stack.Count > 0)
            {
                var f = stack.Pop();
                yield return f;
                for (int i = f.Children.Count - 1; i >= 0; i--) stack.Push(f.Children[i]);
            }
        }

        /// <summary>Finds the field rendered on the given 1-based line of the last <see cref="Render"/>.</summary>
        public GqlField FindByLine(int line)
        {
            foreach (var f in AllFields())
                if (f.Line == line) return f;
            return null;
        }

        public GqlField FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var parts = path.Trim('/').Split('/');
            GqlField cur = Root;
            int start = parts.Length > 0 && parts[0] == Root.Name ? 1 : 0;
            for (int i = start; i < parts.Length && cur != null; i++)
            {
                GqlField next = null;
                foreach (var c in cur.Children)
                    if (c.Name == parts[i]) { next = c; break; }
                cur = next;
            }
            return cur == Root ? null : cur;
        }

        /// <summary>
        /// Removes a field. Parents left without any selections are removed too (empty selection sets are invalid).
        /// Returns false (and removes nothing) if that would remove a required field.
        /// </summary>
        public bool TryRemove(GqlField field, out string removedPath)
        {
            removedPath = null;
            if (field == null || field == Root || field.Parent == null || !field.Optional) return false;

            // Removing the last selection of a parent would leave an empty (invalid) selection set,
            // so remove the parent instead - but only if it is optional itself.
            var target = field;
            while (target.Parent != Root && target.Parent.Children.Count == 1)
            {
                if (!target.Parent.Optional) return false;
                target = target.Parent;
            }

            removedPath = target.Path;
            target.Parent.Children.Remove(target);
            target.Detach();
            return true;
        }
    }

    public sealed class GraphQLError
    {
        public string Message;
        public string Classification;
        public List<int> Lines = new List<int>();
        public string Path;

        public bool IsValidationError
        {
            get
            {
                var m = Message ?? "";
                var c = Classification ?? "";
                return c.IndexOf("Validation", StringComparison.OrdinalIgnoreCase) >= 0
                       || m.IndexOf("Validation error", StringComparison.OrdinalIgnoreCase) >= 0
                       || m.IndexOf("Cannot query field", StringComparison.OrdinalIgnoreCase) >= 0
                       || m.IndexOf("is undefined", StringComparison.OrdinalIgnoreCase) >= 0
                       || m.IndexOf("FieldUndefined", StringComparison.OrdinalIgnoreCase) >= 0
                       || m.IndexOf("SubSelection", StringComparison.OrdinalIgnoreCase) >= 0
                       || m.IndexOf("Unknown field", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        public override string ToString() => Message + (Lines.Count > 0 ? " (line " + Lines[0] + ")" : "");
    }

    public sealed class GraphQLResponse
    {
        public JsonValue Data = JsonValue.Null;
        public List<GraphQLError> Errors = new List<GraphQLError>();
        public string Raw;

        public static GraphQLResponse Parse(string raw)
        {
            var r = new GraphQLResponse { Raw = raw };
            var json = JsonValue.Parse(raw);
            r.Data = json["data"];
            foreach (var e in json["errors"].Items)
            {
                var err = new GraphQLError
                {
                    Message = e["message"].AsString() ?? e.ToJson(false),
                    Classification = e["extensions"]["classification"].AsString() ?? e["extensions"]["code"].AsString(),
                };
                foreach (var loc in e["locations"].Items)
                {
                    int line = loc["line"].AsInt(-1);
                    if (line > 0) err.Lines.Add(line);
                }
                var path = e["path"];
                if (path.IsArray)
                {
                    var parts = new List<string>();
                    foreach (var p in path.Items) if (p.IsString) parts.Add(p.AsString());
                    err.Path = string.Join("/", parts);
                }
                r.Errors.Add(err);
            }
            return r;
        }
    }

    public sealed class QueryOutcome
    {
        public JsonValue Data = JsonValue.Null;
        public string FinalQuery;
        public string RawResponse;
        public List<string> PrunedFields = new List<string>();
        public List<GraphQLError> RemainingErrors = new List<GraphQLError>();
        public int Attempts;
    }

    /// <summary>
    /// Runs a query, pruning optional fields the server rejects (by error line/path/name) and retrying.
    /// </summary>
    public static class SelfHealingQuery
    {
        static readonly Regex[] FieldNamePatterns =
        {
            new Regex(@"Field '([A-Za-z0-9_]+)'"),
            new Regex(@"Cannot query field ""([A-Za-z0-9_]+)"""),
            new Regex(@"field '([A-Za-z0-9_]+)'"),
            new Regex(@"Unknown field ""?'?([A-Za-z0-9_]+)"),
        };
        static readonly Regex PathPattern = new Regex(@"@\s*\[([^\]]+)\]|@\s*'([^']+)'");

        /// <param name="post">Sends the request body (JSON) and returns the raw response text.</param>
        public static async Task<QueryOutcome> RunAsync(GqlDocument document, Func<string, CancellationToken, Task<string>> post,
            ILogSink log, CancellationToken ct, int maxAttempts = 16)
        {
            var doc = document.Clone();
            var outcome = new QueryOutcome();
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                outcome.Attempts = attempt;
                string query = doc.Render();
                var body = JsonValue.NewObject().Set("operationName", doc.OperationName).Set("query", query)
                    .Set("variables", JsonValue.NewObject()).ToJson(false);
                string raw = await post(body, ct).ConfigureAwait(false);
                outcome.FinalQuery = query;
                outcome.RawResponse = raw;

                GraphQLResponse resp;
                try { resp = GraphQLResponse.Parse(raw); }
                catch (FormatException e)
                {
                    throw new MatterportException("The Matterport API returned something that is not JSON (" + e.Message + "). " +
                                                  "First 200 chars: " + Truncate(raw, 200));
                }

                var validation = resp.Errors.FindAll(e => e.IsValidationError);
                if (validation.Count == 0)
                {
                    outcome.Data = resp.Data;
                    outcome.RemainingErrors = resp.Errors;
                    foreach (var e in resp.Errors) log.Warn("GraphQL: " + e);
                    return outcome;
                }

                int removed = 0;
                foreach (var err in validation)
                {
                    foreach (var f in CandidatesFor(doc, err))
                    {
                        if (doc.TryRemove(f, out var path))
                        {
                            outcome.PrunedFields.Add(path);
                            log.Info("GraphQL: field not available, dropping '" + path + "' (" + err.Message + ")");
                            removed++;
                            break;
                        }
                    }
                }

                if (removed == 0)
                {
                    outcome.RemainingErrors = validation;
                    throw new MatterportException("The Matterport API rejected the query: " +
                                                  string.Join("; ", validation.ConvertAll(e => e.ToString())));
                }
            }
            throw new MatterportException("The Matterport API kept rejecting the query after " + maxAttempts + " attempts.");
        }

        static IEnumerable<GqlField> CandidatesFor(GqlDocument doc, GraphQLError err)
        {
            foreach (var line in err.Lines)
            {
                var f = doc.FindByLine(line);
                if (f != null && f != doc.Root) yield return f;
            }
            var m = PathPattern.Match(err.Message ?? "");
            if (m.Success)
            {
                var path = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                var f = doc.FindByPath(path.Replace(" ", ""));
                if (f != null) yield return f;
            }
            foreach (var p in FieldNamePatterns)
            {
                var fm = p.Match(err.Message ?? "");
                if (!fm.Success) continue;
                foreach (var f in doc.AllFields())
                    if (f != doc.Root && f.Name == fm.Groups[1].Value) yield return f;
            }
        }

        internal static string Truncate(string s, int n) => s == null ? "" : s.Length <= n ? s : s.Substring(0, n) + "…";
    }

    public class MatterportException : Exception
    {
        public MatterportException(string message) : base(message) { }
        public MatterportException(string message, Exception inner) : base(message, inner) { }
    }
}
