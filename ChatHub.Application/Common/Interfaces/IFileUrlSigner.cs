namespace ChatHub.Application.Common.Interfaces;

public interface IFileUrlSigner
{
    string Sign(Guid fileId, DateTime expiresAtUtc);

    bool Verify(Guid fileId, long? expiresAtUnixSeconds, string? signature);
}
