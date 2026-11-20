using Dollhouse.Server.Echo;
using Dollhouse.Server.Debugging;

namespace Dollhouse.App;

internal sealed class TerminalRequestJournal : IRequestJournal
{
    public void Write(RequestMetadata metadata) => TerminalOutput.Request(metadata);
    public void Write(RequestMetadata metadata, RequestCaptureResult? capture) => TerminalOutput.Request(metadata, capture);
}
