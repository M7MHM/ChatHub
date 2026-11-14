namespace ChatHub.Infrastructure.Storage;

public class FileStorageSettings
{
    public const string SectionName = "FileStorage";

    public static readonly IReadOnlyDictionary<string, string> SupportedContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["image/jpeg"] = ".jpg",
            ["image/png"] = ".png",
            ["image/gif"] = ".gif",
            ["image/webp"] = ".webp",
            ["audio/mpeg"] = ".mp3",
            ["audio/mp4"] = ".m4a",
            ["audio/aac"] = ".aac",
            ["audio/ogg"] = ".oga",
            ["audio/wav"] = ".wav",
            ["audio/x-wav"] = ".wav",
            ["audio/webm"] = ".webm",
            ["application/pdf"] = ".pdf",
            ["application/msword"] = ".doc",
            ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = ".docx",
            ["application/vnd.ms-excel"] = ".xls",
            ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = ".xlsx",
            ["application/vnd.ms-powerpoint"] = ".ppt",
            ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = ".pptx",
            ["application/zip"] = ".zip",
            ["application/x-zip-compressed"] = ".zip"
        };

    public string RootPath { get; init; } = "wwwroot";

    public long MaxFileSizeBytes { get; init; } = 20 * 1024 * 1024;

    public List<string> AllowedContentTypes { get; init; } = [];
}
