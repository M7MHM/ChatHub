using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Common.Models;

public class PaginatedList<T>(IReadOnlyCollection<T> items, int totalCount, int pageNumber, int pageSize)
{
    public IReadOnlyCollection<T> Items { get; } = items;
    public int PageNumber { get; } = pageNumber;
    public int PageSize { get; } = pageSize;
    public int TotalCount { get; } = totalCount;
    public int TotalPages { get; } = pageSize > 0 ? (int)Math.Ceiling(totalCount / (double)pageSize) : 0;

    public bool HasPreviousPage => PageNumber > 1;
    public bool HasNextPage => PageNumber < TotalPages;

    public static async Task<PaginatedList<T>> CreateAsync(
        IQueryable<T> source, int pageNumber, int pageSize, CancellationToken cancellationToken = default)
    {
        pageNumber = Math.Max(pageNumber, 1);
        pageSize = Math.Max(pageSize, 1);

        var count = await source.CountAsync(cancellationToken);
        var items = await source.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new PaginatedList<T>(items, count, pageNumber, pageSize);
    }
}
