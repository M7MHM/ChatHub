namespace ChatHub.Application.Common.Interfaces;

public interface IBotReplyWaker
{
    void WakeAt(DateTime dueAtUtc);
}
