using System.Collections.Concurrent;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Domain.Common;
using ChatHub.Infrastructure.Identity;
using MediatR;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace ChatHub.Application.IntegrationTests.Infrastructure;

public sealed class TestCurrentUser : ICurrentUserService
{
    public Guid? UserId { get; set; }
}

public sealed class TestClock(DateTimeOffset start) : FakeTimeProvider(start)
{
    private readonly AsyncLocal<Hook?> _hook = new();

    public void OnCall(Action<int> action) => _hook.Value = new Hook(action);

    public override DateTimeOffset GetUtcNow()
    {
        if (_hook.Value is { } hook) hook.Action(Interlocked.Increment(ref hook.Calls));
        return base.GetUtcNow();
    }

    private sealed class Hook(Action<int> action)
    {
        public int Calls;
        public Action<int> Action { get; } = action;
    }
}

public sealed class RecordingPasswordHasher : IPasswordHasher
{
    private static readonly ConcurrentDictionary<string, int> ProducedHashes = new();
    private readonly PasswordHasher _inner = new();
    private readonly ConcurrentQueue<string> _verifiedHashes = new();
    private int _hashCalls;

    public int HashCalls => Volatile.Read(ref _hashCalls);

    public IReadOnlyList<string> VerifiedHashes => [.. _verifiedHashes];

    public static int TimesProduced(string hash) => ProducedHashes.GetValueOrDefault(hash);

    public string Hash(string password)
    {
        Interlocked.Increment(ref _hashCalls);
        var hash = _inner.Hash(password);
        ProducedHashes.AddOrUpdate(hash, 1, (_, count) => count + 1);
        return hash;
    }

    public bool Verify(string password, string passwordHash)
    {
        _verifiedHashes.Enqueue(passwordHash);
        return _inner.Verify(password, passwordHash);
    }
}

public sealed class DomainEventRecorder
{
    private readonly ConcurrentQueue<IDomainEvent> _events = new();

    public IReadOnlyList<T> OfType<T>() => _events.OfType<T>().ToList();

    public void Clear() => _events.Clear();

    public void Record(IDomainEvent domainEvent) => _events.Enqueue(domainEvent);
}

public sealed class RecordingDomainEventHandler<TEvent>(DomainEventRecorder recorder)
    : INotificationHandler<DomainEventNotification<TEvent>>
    where TEvent : IDomainEvent
{
    public Task Handle(DomainEventNotification<TEvent> notification, CancellationToken cancellationToken)
    {
        recorder.Record(notification.DomainEvent);
        return Task.CompletedTask;
    }
}

public sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Testing";
    public string ApplicationName { get; set; } = "ChatHub.Application.IntegrationTests";
    public string ContentRootPath { get; set; } = contentRootPath;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

public static class TestFiles
{
    public static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    public static readonly byte[] Ogg = [.. "OggS"u8, .. new byte[28]];

    public static readonly byte[] Pdf = "%PDF-1.4\n1 0 obj<<>>endobj\n%%EOF"u8.ToArray();
}
