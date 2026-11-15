using ChatHub.Application.Common.Models;

namespace ChatHub.Application.Features.Files;

public static class FileErrors
{
    public static readonly Error NotFound = Error.NotFound("File.NotFound", "The file was not found.");

    public static readonly Error NotOwned = Error.Forbidden("File.NotOwned", "You can only use files you uploaded.");

    public static readonly Error WrongPurpose =
        Error.Validation("File.WrongPurpose", "The file was uploaded for a different purpose.");

    public static readonly Error AlreadyAttached = Error.Validation("File.AlreadyAttached", "The file is already in use.");

    public static readonly Error TypeMismatch =
        Error.Validation("File.TypeMismatch", "The file type does not match the message type.");
}
