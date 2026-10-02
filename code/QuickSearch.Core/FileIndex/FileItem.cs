namespace QuickSearch.Core.FileIndex;

public class FileItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime LastModified { get; set; }
    public bool IsDirectory { get; set; }
    public string ParentPath { get; set; } = string.Empty;
}
