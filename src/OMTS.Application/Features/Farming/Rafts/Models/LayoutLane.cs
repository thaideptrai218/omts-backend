using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using System.Text.Json;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Farming.Rafts.Models;

/// <summary>A checked lane of a layout.</summary>
public sealed record LayoutLane(int LaneNumber, decimal Length, string MarkerA, string MarkerB);
