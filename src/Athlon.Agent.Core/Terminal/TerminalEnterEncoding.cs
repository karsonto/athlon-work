namespace Athlon.Agent.Core.Terminal;

/// <summary>Enter key bytes for a workspace terminal, chosen by the ConPTY input mode.</summary>
public static class TerminalEnterEncoding
{
    // VK_RETURN=13, scan=28, Unicode=13 (CR) — see Microsoft Terminal spec #4999.
    public const string Win32EnterKeyDown = "\x1b[13;28;13;1;0;1_";
    public const string Win32EnterKeyUp = "\x1b[13;28;13;0;0;1_";

    /// <summary>Carriage return for a remote PTY (cooked mode treats CR as Enter).</summary>
    public const string RemoteCarriageReturn = "\r";

    public static string EnterSequence(bool useWin32Enter) =>
        useWin32Enter ? Win32EnterKeyDown + Win32EnterKeyUp : RemoteCarriageReturn;
}
