using System.Globalization;
using System.Text;
using System.Text.Json;
using CommerceErp.Core;

namespace CommerceErp.Infrastructure;

// Delimited text only; no spreadsheet formulas are evaluated and identifiers remain strings.
internal static class BillCsv
{
    internal sealed record Parsed(int Line, string Transaction, string Order, BillKind Kind, long Cents, DateTimeOffset Time, string Case, string Raw);
    internal static List<Parsed> Parse(BillInput input)
    {
        if (input is null || input.Columns is null || input.Types is null) throw new BusinessRuleException("账单、列映射和类型映射不能为空。");
        if (string.IsNullOrWhiteSpace(input.Content) || Encoding.UTF8.GetByteCount(input.Content) > 120000) throw new BusinessRuleException("请导入不超过 120 KB 的 UTF-8 CSV，每批最多 500 行。");
        var rows = Read(input.Content.TrimStart('\uFEFF'));
        if (rows.Count < 2 || rows.Count > 501) throw new BusinessRuleException("CSV 须有表头和 1–500 行明细。");
        var header = rows[0].Select(s => s.Trim()).ToList();
        if (header.Any(string.IsNullOrWhiteSpace) || header.Distinct(StringComparer.Ordinal).Count() != header.Count) throw new BusinessRuleException("表头不能为空或重复。");
        var c = input.Columns;
        var names = new[] { c.Transaction, c.Order, c.Kind, c.Amount, c.Time, c.Currency, c.AfterSale };
        if (names.Any(string.IsNullOrWhiteSpace) || names.Distinct(StringComparer.Ordinal).Count() != names.Length) throw new BusinessRuleException("七个字段应映射到不同的列，售后编号列可以留空值。");
        var indexes = names.Select(n => header.IndexOf(n)).ToArray();
        if (indexes.Any(i => i < 0)) throw new BusinessRuleException("列映射中有表头不存在的名称。");
        if (input.Types.Count is < 1 or > 30 || input.Types.Any(t => string.IsNullOrWhiteSpace(t.Key) || !Enum.IsDefined(t.Value))) throw new BusinessRuleException("请提供有效的账单类型映射。");
        var result = new List<Parsed>();
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            try
            {
                if (row.Count != header.Count) throw new BusinessRuleException("列数与表头不符。");
                string Cell(int n) => row[indexes[n]].Trim();
                var transaction = Identifier(Cell(0)); var order = Identifier(Cell(1));
                if (!input.Types.TryGetValue(Cell(2), out var kind)) throw new BusinessRuleException("未知交易类型，请配置类型映射。");
                if (!decimal.TryParse(Cell(3), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) || amount <= 0 || amount > 1000000000 || decimal.Round(amount, 2) != amount)
                    throw new BusinessRuleException("金额须为大于零、最多两位小数的元金额；收支方向由类型确定，不接受负号或千分符。");
                var time = JsonSerializer.Deserialize<DateTimeOffset>(JsonSerializer.Serialize(Cell(4)), ErpJson.Options);
                if (time == default || time > DateTimeOffset.UtcNow.AddMinutes(5)) throw new BusinessRuleException("发生时间无效或在未来。");
                if (Cell(5) != "CNY") throw new BusinessRuleException("当前仅接受 CNY。");
                var caseNo = Cell(6); if (caseNo != "") Identifier(caseNo);
                if (kind == BillKind.Refund && caseNo == "") throw new BusinessRuleException("退款必须填写来源售后编号。");
                if (kind != BillKind.Refund && caseNo != "") throw new BusinessRuleException("非退款行不得填写售后编号。");
                result.Add(new(i + 1, transaction, order, kind, checked((long)(amount * 100)), time.ToUniversalTime(), caseNo, CommerceDatabase.Json(row)));
            }
            catch (Exception e) when (e is BusinessRuleException or JsonException) { throw new BusinessRuleException($"第 {i + 1} 行：{e.Message}"); }
        }
        return result;
    }
    internal static string Identifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 150 || value.Any(char.IsControl)) throw new BusinessRuleException("业务标识须为 1–150 个非控制字符。");
        return value;
    }
    internal static List<List<string>> Read(string content)
    {
        var rows = new List<List<string>>(); var row = new List<string>(); var cell = new StringBuilder(); bool quoted = false, closed = false;
        for (var i = 0; i < content.Length; i++)
        {
            var ch = content[i];
            if (quoted)
            {
                if (ch == '"') { if (i + 1 < content.Length && content[i + 1] == '"') { cell.Append('"'); i++; } else { quoted = false; closed = true; } }
                else cell.Append(ch);
                continue;
            }
            if (ch == ',' || ch == '\r' || ch == '\n')
            {
                row.Add(cell.ToString()); cell.Clear(); closed = false;
                if (ch != ',') { if (ch == '\r' && i + 1 < content.Length && content[i + 1] == '\n') i++; rows.Add(row); row = []; }
                continue;
            }
            if (closed) throw new BusinessRuleException("CSV 引号闭合后只能接分隔符或换行。");
            if (ch == '"') { if (cell.Length != 0) throw new BusinessRuleException("CSV 字段中引号须转义。"); quoted = true; }
            else cell.Append(ch);
        }
        if (quoted) throw new BusinessRuleException("CSV 引号未闭合。");
        if (cell.Length > 0 || row.Count > 0 || closed) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }
    public static string Escape(string? value)
    {
        value ??= "";
        if (value.TrimStart().StartsWith('=') || value.TrimStart().StartsWith('+') || value.TrimStart().StartsWith('-') || value.TrimStart().StartsWith('@') || value.StartsWith('\t') || value.StartsWith('\r') || value.StartsWith('\n')) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
