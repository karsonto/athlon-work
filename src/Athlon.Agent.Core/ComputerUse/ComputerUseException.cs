namespace Athlon.Agent.Core.ComputerUse;

public sealed class ComputerUseException : Exception
{
    public ComputerUseException(string code, string message, string hint = "call computer_observe")
        : base(message)
    {
        Code = code;
        Hint = hint;
    }

    public string Code { get; }

    public string Hint { get; }
}
