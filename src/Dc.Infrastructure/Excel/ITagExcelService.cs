using Dc.Domain.Entities;

namespace Dc.Infrastructure.Excel;

public interface ITagExcelService
{
    IReadOnlyList<TagImportRow> Read(Stream excelStream);
    void Write(IEnumerable<Tag> tags, Stream output);
}
