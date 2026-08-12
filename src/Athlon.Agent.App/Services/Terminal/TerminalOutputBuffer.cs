using Athlon.Agent.Core.Terminal;
using EasyWindowsTerminalControl;

namespace Athlon.Agent.App.Services.Terminal;

/// <summary>Ring buffer fed by TermPTY output interception for agent readback.</summary>
public sealed class TerminalOutputBuffer
{
    private const int MaxStoredChars = 256 * 1024;

    private readonly object _gate = new();
    private readonly System.Text.StringBuilder _buffer = new();
    private TermPTY? _attachedSession;
    private TermPTY.InterceptDelegate? _handler;

    public void Attach(TermPTY session)
    {
        if (session is null)
        {
            throw new ArgumentNullException(nameof(session));
        }

        lock (_gate)
        {
            if (ReferenceEquals(_attachedSession, session))
            {
                return;
            }

            DetachCore();
            _attachedSession = session;
            _handler = OnOutput;
            session.InterceptOutputToUITerminal = _handler;
        }
    }

    public void Detach()
    {
        lock (_gate)
        {
            DetachCore();
        }
    }

    public TerminalOutputSnapshot Snapshot(int maxChars)
    {
        if (maxChars <= 0)
        {
            maxChars = 8000;
        }

        lock (_gate)
        {
            var total = _buffer.Length;
            if (total == 0)
            {
                return new TerminalOutputSnapshot(string.Empty, Truncated: false, TotalChars: 0);
            }

            if (total <= maxChars)
            {
                return new TerminalOutputSnapshot(_buffer.ToString(), Truncated: false, TotalChars: total);
            }

            var start = total - maxChars;
            return new TerminalOutputSnapshot(
                _buffer.ToString(start, maxChars),
                Truncated: true,
                TotalChars: total);
        }
    }

    private void OnOutput(ref Span<char> str)
    {
        if (str.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            _buffer.Append(str);
            TrimToMax();
        }
    }

    private void TrimToMax()
    {
        if (_buffer.Length <= MaxStoredChars)
        {
            return;
        }

        var overflow = _buffer.Length - MaxStoredChars;
        _buffer.Remove(0, overflow);
    }

    private void DetachCore()
    {
        if (_attachedSession is not null)
        {
            try
            {
                _attachedSession.InterceptOutputToUITerminal = null;
            }
            catch
            {
                // Best-effort detach.
            }
        }

        _attachedSession = null;
        _handler = null;
    }
}
