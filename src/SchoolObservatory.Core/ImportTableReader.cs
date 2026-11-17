using System.Text;

namespace SchoolObservatory.Core;

/// <summary>Read-only input acquisition. File type is restricted by extension and verified content.</summary>
public static class ImportTableReader
{
    public static ImportTable Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new ArgumentException("اختر ملفا موجودا.");
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var size = new FileInfo(path).Length;
        if (size is <= 0 or > 8_000_000)
            throw new ArgumentException("حجم ملف الاستيراد غير صحيح أو يتجاوز 8 ميجابايت.");

        if (extension == ".csv")
        {
            var bytes = File.ReadAllBytes(path);
            var text = new UTF8Encoding(false, true).GetString(bytes);
            var table = SchoolCsvParser.ParseImportRows(text);
            return new ImportTable("csv", table[0], table.Skip(1).ToArray());
        }
        if (extension is not (".xlsx" or ".docx"))
            throw new ArgumentException("الصيغ المسموحة هي CSV (UTF-8)، Excel .xlsx أو Word .docx فقط.");
        // Validate ZIP signature before invoking the decompressor.
        using (var input = File.OpenRead(path))
        {
            Span<byte> magic = stackalloc byte[4];
            if (input.Read(magic) != 4 || magic[0] != 0x50 || magic[1] != 0x4b ||
                magic[2] != 0x03 || magic[3] != 0x04)
                throw new ArgumentException("امتداد الملف لا يطابق صيغة OOXML المضغوطة.");
        }
        var parsed = SchoolStaffImportParser.ReadStructuredTableWithPositions(path, _ => true);
        var rows = parsed.Rows;
        if (rows.Count < 2) throw new ArgumentException("الملف لا يحتوي عناوين وصفوف بيانات.");
        if (rows[0].Count is 0 or > 30)
            throw new ArgumentException("عدد الأعمدة غير صالح.");
        return new ImportTable(extension.TrimStart('.'), rows[0], rows.Skip(1).ToArray(),
            parsed.RowNumbers.Skip(1).ToArray());
    }
}
