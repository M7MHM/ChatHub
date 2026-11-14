using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Common.Extensions;

public static class DbUpdateExceptionExtensions
{
    private const string UniqueViolationSqlState = "23505";
    public static bool IsUniqueViolation(this DbUpdateException exception) =>
        exception.InnerException is DbException { SqlState: UniqueViolationSqlState };
}
