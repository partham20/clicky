using Clicky.Core.Diagnostics;

namespace Clicky.Tests.Diagnostics;

public sealed class SecretRedactorTests
{
    [Theory]
    [InlineData("key sk-ant-api03-AbCdEf_123-xyz9 used", "key [redacted] used")]
    [InlineData("OPENAI_API_KEY=sk-proj-abcdefghijklmnop1234", "OPENAI_API_KEY=[redacted]")]
    [InlineData("token ghp_abcdefghijklmnopqrstuvwxyz0123", "token [redacted]")]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.payload.sig", "Authorization: Bearer [redacted]")]
    [InlineData("{ \"apiKey\": \"hunter2hunter2\" }", "{ \"apiKey\": \"[redacted]\" }")]
    [InlineData("password=correct-horse;user=x", "password=[redacted];user=x")]
    public void Secrets_are_removed(string input, string expected) =>
        Assert.Equal(expected, SecretRedactor.Redact(input));

    [Theory]
    [InlineData(@"Saved C:\Users\jane.doe\AppData\Roaming\Clicky\settings.json", @"Saved C:\Users\<user>\AppData\Roaming\Clicky\settings.json")]
    [InlineData(@"""C:\Users\PARTHA~1.M\AppData\Local\Temp\x""", @"""C:\Users\<user>\AppData\Local\Temp\x""")]
    [InlineData("c:/users/Jane Doe/Desktop/a.png", "c:/users/<user>/Desktop/a.png")]
    [InlineData(@"profile at C:\Users\jane", @"profile at C:\Users\<user>")]
    public void User_name_in_paths_is_removed(string input, string expected) =>
        Assert.Equal(expected, SecretRedactor.Redact(input));

    [Theory]
    [InlineData("2026-10-21 09:00:00.000 +05:30 [INF] Clicky.App: Talk took 412 ms")]
    [InlineData("Speech-to-text: Local, language en; tokens 512")]
    [InlineData("The sky-is-blue and a task-list")]
    [InlineData(@"Look in C:\Users for profiles")]
    public void Ordinary_log_lines_are_untouched(string line) =>
        Assert.Equal(line, SecretRedactor.Redact(line));
}
