namespace ChatHub.Application.Common.Models;

public sealed record StoredFile(string Url, string FileName, string ContentType, long SizeBytes);
