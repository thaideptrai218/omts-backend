namespace OMTS.Api.Filters;

/// <summary>
/// The field an upload endpoint names in its MSG01 for an invalid file ("Logo", "Photo"...). A file refused by
/// the request size limit (<see cref="OversizedUploadFilter"/>) names the same field as one refused by the
/// endpoint's file rules, so the client shows both the same way.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class UploadFieldAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}
