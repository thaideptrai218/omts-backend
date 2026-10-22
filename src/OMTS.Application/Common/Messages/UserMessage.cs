namespace OMTS.Application.Common.Messages;

/// <summary>
/// A message from the SRS message list: <see cref="Code"/> (MSGxx) is what clients map to the localized
/// text; <see cref="Text"/> is the English SRS wording with its placeholders filled in.
/// </summary>
public sealed record UserMessage(string Code, string Text);
