using Clicky.Core.Voice;
using Clicky.Core.Walkthroughs;

namespace Clicky.App.Walkthroughs;

/// <summary>A walkthrough turn's speech, through the reply presenter's <see cref="SpokenReply"/>.</summary>
internal sealed class SpokenReplySpeech(SpokenReply reply) : IReplySpeech
{
    public void Append(string text) => reply.Append(text);

    public void Complete() => reply.Complete();

    public void Interrupt() => reply.Interrupt();
}
