using System.Text.Json;
using OMTS.Application.Features.Employees;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Farming.Rafts.Dtos;

/// <summary>One lane of the layout: lanes are numbered 1…N; positions are metres from marker A.</summary>
public sealed record LaneInput(int LaneNumber, decimal? Length, string? MarkerA, string? MarkerB);
