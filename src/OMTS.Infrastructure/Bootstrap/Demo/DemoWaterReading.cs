using System.Security.Cryptography;
using System.Text;
using OMTS.Application.Features.WaterQuality.Dtos;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>A demo reading taken <see cref="DaysAgo"/> days before the first seed day, early in the morning.</summary>
internal sealed record DemoWaterReading(int DaysAgo, TimeOnly Time, decimal Ph, decimal Salinity);
