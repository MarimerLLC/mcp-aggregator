using System.Text;

namespace McpAggregator.Core.Services;

/// <summary>
/// A unified line diff (longest-common-subsequence), enough for a reviewer to see what
/// <c>refresh_skill</c> changed in a skill document (issue #57). Skill documents are a few hundred
/// lines; inputs whose LCS table would exceed <see cref="MaxCells"/> get a summary instead.
/// </summary>
public static class LineDiff
{
    public const long MaxCells = 4_000_000;

    public static string Unified(string before, string after, int context = 3,
        string fromLabel = "current", string toLabel = "proposed")
    {
        var a = SplitLines(before);
        var b = SplitLines(after);

        if ((long)a.Length * b.Length > MaxCells)
            return $"--- {fromLabel}\n+++ {toLabel}\n(diff omitted: {a.Length} → {b.Length} lines is too large to compare)\n";

        var ops = Script(a, b);
        if (ops.All(o => o.Kind == ' '))
            return string.Empty;

        var sb = new StringBuilder();
        sb.Append("--- ").Append(fromLabel).Append('\n');
        sb.Append("+++ ").Append(toLabel).Append('\n');

        var i = 0;
        while (i < ops.Count)
        {
            if (ops[i].Kind == ' ') { i++; continue; }

            // A hunk: from `context` lines before this change to `context` lines after the last
            // change that is no more than 2*context unchanged lines away.
            var start = Math.Max(0, i - context);
            var end = i;
            var lastChange = i;
            while (end < ops.Count)
            {
                if (ops[end].Kind != ' ') lastChange = end;
                else if (end - lastChange > 2 * context) break;
                end++;
            }
            end = Math.Min(ops.Count, lastChange + context + 1);

            var oldStart = ops[start].OldLine;
            var newStart = ops[start].NewLine;
            var oldCount = ops.Skip(start).Take(end - start).Count(o => o.Kind != '+');
            var newCount = ops.Skip(start).Take(end - start).Count(o => o.Kind != '-');
            sb.Append($"@@ -{oldStart + 1},{oldCount} +{newStart + 1},{newCount} @@\n");
            for (var k = start; k < end; k++)
                sb.Append(ops[k].Kind).Append(ops[k].Text).Append('\n');

            i = end;
        }

        return sb.ToString();
    }

    private readonly record struct Op(char Kind, string Text, int OldLine, int NewLine);

    private static string[] SplitLines(string text)
    {
        var normalized = text.Replace("\r\n", "\n");
        if (normalized.EndsWith('\n'))
            normalized = normalized[..^1];
        return normalized.Length == 0 ? [] : normalized.Split('\n');
    }

    private static List<Op> Script(string[] a, string[] b)
    {
        // lcs[i, j] = LCS length of a[i..] and b[j..]
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var ops = new List<Op>(a.Length + b.Length);
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y]) { ops.Add(new Op(' ', a[x], x, y)); x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) { ops.Add(new Op('-', a[x], x, y)); x++; }
            else { ops.Add(new Op('+', b[y], x, y)); y++; }
        }
        for (; x < a.Length; x++) ops.Add(new Op('-', a[x], x, y));
        for (; y < b.Length; y++) ops.Add(new Op('+', b[y], x, y));
        return ops;
    }
}
