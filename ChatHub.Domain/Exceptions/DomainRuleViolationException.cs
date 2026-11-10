namespace ChatHub.Domain.Exceptions;

public sealed class DomainRuleViolationException(string message) : BaseDomainException(message);
