using System.Globalization;
using System.Text;

namespace RemoteFileSync.State;

/// <summary>
/// Structured payload for file_versions.detail. LogConflict / LogResurrection decide the
/// `action` column; this record only carries the data the review report renders, so nothing
/// downstream ever has to sniff the detail string to work out what kind of event it was.
/// Encode is single-line and tab-separated so a detail can never split a report row, and is
/// versioned so a future field can be added without misreading v1 rows already on disk.
/// </summary>
public sealed record ConflictDetail(
    long ClientSize, long ClientMtimeTicks,
    long ServerSize, long ServerMtimeTicks,
    string? RenamedTo)
{
    private const string FormatVersion = "v1";
    private const char Separator = '\t';
    private const int FieldCount = 6;

    /// <summary>Flags an absent rename. A bare empty field cannot be used: RenamedTo == ""
    /// and RenamedTo == null must survive the round trip as distinct values.</summary>
    private const char NoRename = '-';
    private const char HasRename = '+';

    public string Encode()
    {
        var sb = new StringBuilder();
        sb.Append(FormatVersion).Append(Separator);
        sb.Append(ClientSize.ToString(CultureInfo.InvariantCulture)).Append(Separator);
        sb.Append(ClientMtimeTicks.ToString(CultureInfo.InvariantCulture)).Append(Separator);
        sb.Append(ServerSize.ToString(CultureInfo.InvariantCulture)).Append(Separator);
        sb.Append(ServerMtimeTicks.ToString(CultureInfo.InvariantCulture)).Append(Separator);

        if (RenamedTo is null)
            sb.Append(NoRename);
        else
            sb.Append(HasRename).Append(Escape(RenamedTo));

        return sb.ToString();
    }

    public static ConflictDetail? Decode(string? detail)
    {
        if (string.IsNullOrEmpty(detail)) return null;

        var parts = detail.Split(Separator);
        if (parts.Length != FieldCount) return null;
        if (!string.Equals(parts[0], FormatVersion, StringComparison.Ordinal)) return null;

        if (!TryParseTicks(parts[1], out var clientSize))       return null;
        if (!TryParseTicks(parts[2], out var clientMtimeTicks)) return null;
        if (!TryParseTicks(parts[3], out var serverSize))       return null;
        if (!TryParseTicks(parts[4], out var serverMtimeTicks)) return null;

        var renameField = parts[5];
        string? renamedTo;
        if (renameField.Length == 1 && renameField[0] == NoRename)
        {
            renamedTo = null;
        }
        else if (renameField.Length >= 1 && renameField[0] == HasRename)
        {
            if (!TryUnescape(renameField.Substring(1), out renamedTo)) return null;
        }
        else
        {
            return null;
        }

        return new ConflictDetail(clientSize, clientMtimeTicks, serverSize, serverMtimeTicks, renamedTo);
    }

    // AllowLeadingSign only: whitespace, thousands separators and hex must all be rejected,
    // because anything Encode did not produce is by definition not a v1 detail.
    private static bool TryParseTicks(string field, out long value) =>
        long.TryParse(field, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

    private static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\t': sb.Append("\\t");  break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                default:   sb.Append(ch);     break;
            }
        }
        return sb.ToString();
    }

    private static bool TryUnescape(string value, out string? result)
    {
        var sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\') { sb.Append(value[i]); continue; }

            // A trailing lone backslash means the row was truncated; decoding it as a literal
            // would silently hand the caller a rename target that is not the one recorded.
            if (i + 1 >= value.Length) { result = null; return false; }

            switch (value[++i])
            {
                case '\\': sb.Append('\\'); break;
                case 't':  sb.Append('\t'); break;
                case 'n':  sb.Append('\n'); break;
                case 'r':  sb.Append('\r'); break;
                default:   result = null; return false;
            }
        }

        result = sb.ToString();
        return true;
    }
}
