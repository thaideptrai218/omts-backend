using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.AccessManagement.Dtos;

public sealed record Paged<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);
