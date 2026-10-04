namespace Comments.Application.Services;

/// <summary>Storage configuration (docs/ARCHITECTURE-v2.md, docs/API-v2.md §10).</summary>
public sealed class AttachmentStorageOptions
{
    /// <summary>filesystem | s3 | azureblob.</summary>
    public string Provider { get; set; } = "filesystem";

    /// <summary>Root directory for uploads; relative paths are resolved against the content root.</summary>
    public string Root { get; set; } = "storage";
}
