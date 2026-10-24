using System.Text;

namespace AirWave.Core.Plist;

/// <summary>Short readable text for a decoded plist, for diagnostics (binary data shown by size, long text cut).</summary>
public static class PlistText
{
    public static string Describe(object o, int max = 700)
    {
        var sb = new StringBuilder();
        Write(sb, o, 0);
        return sb.Length > max ? sb.ToString(0, max) + "…" : sb.ToString();
    }

    public static string DescribeBody(byte[] body, int max = 700)
    {
        if (body == null || body.Length == 0) return "";
        if (body.Length >= 8 && Encoding.ASCII.GetString(body, 0, 6) == "bplist")
        {
            try { return Describe(BinaryPlist.Read(body), max); } catch { return $"<plist {body.Length} bytes>"; }
        }
        bool text = body.All(b => b == '\r' || b == '\n' || b == '\t' || (b >= 32 && b < 127));
        if (text) { var s = Encoding.ASCII.GetString(body).Trim(); return s.Length > max ? s[..max] + "…" : s; }
        return $"<{body.Length} bytes>";
    }

    private static void Write(StringBuilder sb, object o, int depth)
    {
        if (depth > 6) { sb.Append("…"); return; }
        switch (o)
        {
            case null: sb.Append("null"); break;
            case string s: sb.Append('"').Append(s.Length > 80 ? s[..80] + "…" : s).Append('"'); break;
            case byte[] b: sb.Append($"<{b.Length} bytes>"); break;
            case Dictionary<string, object> d:
                sb.Append('{');
                bool first = true;
                foreach (var kv in d)
                {
                    if (!first) sb.Append(", ");
                    first = false;
                    sb.Append(kv.Key).Append('=');
                    Write(sb, kv.Value, depth + 1);
                }
                sb.Append('}');
                break;
            case List<object> l:
                sb.Append('[');
                for (int i = 0; i < l.Count; i++) { if (i > 0) sb.Append(", "); Write(sb, l[i], depth + 1); }
                sb.Append(']');
                break;
            case DateTime dt: sb.Append(dt.ToString("u")); break;
            default: sb.Append(Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture)); break;
        }
    }
}
