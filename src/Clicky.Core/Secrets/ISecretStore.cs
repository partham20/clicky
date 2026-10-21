namespace Clicky.Core.Secrets;

/// <summary>
/// API keys and other secrets, kept out of settings.json. On Windows this is Credential Manager,
/// encrypted per user by DPAPI.
/// </summary>
public interface ISecretStore
{
    /// <summary>The secret, or null if none is stored.</summary>
    string? Read(string name);

    void Write(string name, string value);

    /// <summary>Removes the secret; does nothing if there is none.</summary>
    void Delete(string name);
}

/// <summary>Names of the secrets Clicky stores.</summary>
public static class SecretNames
{
    public const string OpenAiApiKey = "openai-api-key";
}
