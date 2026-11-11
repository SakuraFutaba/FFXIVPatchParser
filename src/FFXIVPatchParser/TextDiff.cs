using System.Globalization;
using System.Text;

namespace FFXIVPatchParser;

public sealed record TextSpan(string Text, bool Changed);
public sealed record TextDifference(IReadOnlyList<TextSpan> Before, IReadOnlyList<TextSpan> After);

public static class TextDiff
{
    // Compare Unicode text elements so surrogate pairs and combining marks stay intact.
    public static TextDifference Compare(string before, string after)
    {
        if (before == after) return new([new(before, false)], [new(after, false)]);
        var a = Elements(before); var b = Elements(after);
        var left = new bool[a.Length]; var right = new bool[b.Length];
        Array.Fill(left, true); Array.Fill(right, true);
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix])
        { left[prefix] = right[prefix] = false; prefix++; }
        var endA = a.Length; var endB = b.Length;
        while (endA > prefix && endB > prefix && a[endA - 1] == b[endB - 1])
        { left[--endA] = right[--endB] = false; }
        var n = endA - prefix; var m = endB - prefix;
        // Bound work for unusually large text; retain exact common prefix/suffix.
        if ((long)n * m <= 250_000 && n > 0 && m > 0)
        {
            var lengths = new int[n + 1, m + 1];
            for (var i = n - 1; i >= 0; i--)
                for (var j = m - 1; j >= 0; j--)
                    lengths[i, j] = a[prefix + i] == b[prefix + j] ? lengths[i + 1, j + 1] + 1 : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            var x = 0; var y = 0;
            while (x < n && y < m)
            {
                if (a[prefix + x] == b[prefix + y]) { left[prefix + x++] = false; right[prefix + y++] = false; }
                else if (lengths[x + 1, y] >= lengths[x, y + 1]) x++;
                else y++;
            }
        }
        return new(Group(a, left), Group(b, right));
    }
    private static string[] Elements(string text)
    {
        var result = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext()) result.Add(enumerator.GetTextElement());
        return result.ToArray();
    }
    private static IReadOnlyList<TextSpan> Group(string[] text, bool[] changed)
    {
        var result = new List<TextSpan>();
        var buffer = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            buffer.Append(text[i]);
            if (i + 1 == text.Length || changed[i] != changed[i + 1])
            { result.Add(new(buffer.ToString(), changed[i])); buffer.Clear(); }
        }
        return result;
    }
}
