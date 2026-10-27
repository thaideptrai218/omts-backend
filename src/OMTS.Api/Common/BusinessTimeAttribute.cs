namespace OMTS.Api.Common;

/// <summary>
/// Marks a text field that carries a date and time (a multipart field read by <see cref="InvariantFormValues"/>), so
/// Swagger documents the business time rule on it (<see cref="BusinessTimeSchemaFilter"/>).
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class BusinessTimeAttribute : Attribute;
