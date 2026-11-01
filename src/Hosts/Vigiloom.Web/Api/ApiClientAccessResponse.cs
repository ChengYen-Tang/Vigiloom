namespace Vigiloom.Web.Api;

/// <summary>The authenticated third-party client's identifier and granted scopes.</summary>
public sealed record ApiClientAccessResponse(string ClientId, string[] Scopes);
