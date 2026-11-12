using ChatHub.Domain.Common;
using MediatR;

namespace ChatHub.Application.Common.Models;

public sealed record DomainEventNotification<TEvent>(TEvent DomainEvent) : INotification
    where TEvent : IDomainEvent;
