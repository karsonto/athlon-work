using EasyWindowsTerminalControl;

namespace Athlon.Agent.App.Services.Terminal;

/// <summary>
/// Sends stdin to ConPTY. With Win32 input mode (always on for workspace terminals),
/// newline must be encoded as win32-input-mode Enter key events, not raw CR/LF text.
/// </summary>
internal static class TerminalInputWriter
{
    // VK_RETURN=13, scan=28, Unicode=13 (CR) — see Microsoft Terminal spec #4999.
    private const string EnterKeyDown = "\x1b[13;28;13;1;0;1_";
    private const string EnterKeyUp = "\x1b[13;28;13;0;0;1_";

    public static void Write(TermPTY session, string text, bool appendNewline)
    {
        if (!string.IsNullOrEmpty(text))
        {
            session.WriteToTerm(text);
        }

        if (!appendNewline)
        {
            return;
        }

        session.WriteToTerm(EnterKeyDown);
        session.WriteToTerm(EnterKeyUp);
    }
}
