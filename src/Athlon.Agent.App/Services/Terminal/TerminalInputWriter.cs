using Athlon.Agent.Core.Terminal;
using EasyWindowsTerminalControl;

namespace Athlon.Agent.App.Services.Terminal;

/// <summary>
/// Sends stdin to ConPTY. Local shells use Win32 input mode, so Enter is a key event.
/// Remote shells use VT input, so Enter is a carriage return.
/// </summary>
internal static class TerminalInputWriter
{
    public static void Write(TermPTY session, string text, bool appendNewline, bool useWin32Enter = true)
    {
        if (!string.IsNullOrEmpty(text))
        {
            session.WriteToTerm(text);
        }

        if (!appendNewline)
        {
            return;
        }

        if (useWin32Enter)
        {
            session.WriteToTerm(TerminalEnterEncoding.Win32EnterKeyDown);
            session.WriteToTerm(TerminalEnterEncoding.Win32EnterKeyUp);
            return;
        }

        session.WriteToTerm(TerminalEnterEncoding.RemoteCarriageReturn);
    }
}
